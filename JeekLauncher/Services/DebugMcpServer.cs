using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekLauncher.Models;
using JeekLauncher.ViewModels;
using JeekLauncher.Views;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekLauncher.Services;

/// <summary>
/// App-specific configuration over the generic <see cref="McpHost"/> in JeekTools:
/// object-graph roots (App/Desktop/Launcher/LauncherVm), '#Name' visual-tree lookup, the
/// Avalonia tools, and the launcher probes. Compiled into every configuration so Debug and
/// Release behave identically, but the listener only starts in Debug builds.
///
/// <para>Agents reach it through the fixed per-user <c>JeekLauncherMcp.exe</c> — via the
/// repo-root <c>JeekLauncherDebugMcp.cmd</c>, which passes <c>--surface debug --app</c> this
/// worktree's exe — and that adapter forwards stdio to this instance's named pipe. The pipe
/// name carries the worktree's instance id, so parallel Debug builds never answer for each
/// other and there is no port to collide over. Agents must not launch
/// <c>bin\JeekLauncherMcp.exe</c> directly: that path is build output (installed to the fixed
/// path on startup) and holding it open would fail the next build.</para>
/// </summary>
internal static class DebugMcpServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DebugMcpServer));

    // A runtime gate rather than #if DEBUG around the whole file: the code compiles in
    // every configuration, only Debug builds actually listen.
    private static readonly bool ListeningEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static readonly ObjectGraph Graph = new(new ObjectGraphOptions
    {
        ResolveRoot = ResolveRoot,
        RootNamesHelp = "App, Desktop, Launcher, LauncherVm",
        FindNamedChild = (target, name) => target is Visual visual
            ? FindDescendantByName(visual, name)
            : throw new InvalidOperationException(
                $"'#{name}' requires a Visual; {target.GetType().Name} is not one."),
    });

    private static readonly McpHost Host = CreateHost();

    public static void Start()
    {
        Host.Start();
        OnEndpointChanged();
    }

    public static void Stop()
    {
        Host.Stop();
        OnEndpointChanged();
    }

    private static McpHost CreateHost()
    {
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-launcher-debug",
            ServerTitle = "JeekLauncher Debug Server",
            Graph = Graph,
            GetVersion = () => $"{AutoUpdateService.GetLocalCommitCount()}",
            Enabled = ListeningEnabled,
            // Named pipe only: no port to collide over between worktree instances.
            PipeName = DebugInstanceContext.DebugMcpPipeName,
            DefaultPort = 0,
            UiInvoker = func => Dispatcher.UIThread.InvokeAsync(func).GetTask()
                .WaitAsync(TimeSpan.FromSeconds(15)),
            Describe = BuildDescribeText,
            ToolListProvider = DebugMcpContract.BuildToolList,
        });

        host.AddTool("visual_tree", VisualTreeAsync);
        host.AddTool("screenshot", ScreenshotAsync);
        host.AddTool("show_launcher", ShowLauncherAsync);
        host.AddTool("hide_launcher", _ => HideLauncherAsync());
        host.AddTool("search_probe", SearchProbeAsync);
        host.AddTool("entries_snapshot", EntriesSnapshotAsync);
        host.AddTool("rescan", _ => RescanAsync());
        host.AddTool("config_state", _ => ConfigStateAsync());
        host.AddTool("hotkey_state", _ => HotkeyStateAsync());
        host.AddTool("quick_words", QuickWordsAsync);
        return host;
    }

    private static Task<T> OnUiAsync<T>(Func<T> func) => Host.OnUiAsync(func);

    private static JsonObject ToolText(string text, bool isError = false) =>
        McpHost.ToolText(text, isError);

    #region Roots

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static App CurrentApp =>
        Application.Current as App
        ?? throw new InvalidOperationException("Application.Current is not the JeekLauncher App.");

    private static object ResolveRoot(string name) => name switch
    {
        "App" => Application.Current
                 ?? throw new InvalidOperationException("Application.Current is null."),
        "Desktop" => Desktop
                     ?? throw new InvalidOperationException("No desktop lifetime."),
        "Launcher" => CurrentApp.DebugWindow,
        "LauncherVm" => CurrentApp.DebugViewModel,
        _ => throw new InvalidOperationException(
            $"Unknown root '{name}'. Available roots: App, Desktop, Launcher, LauncherVm."),
    };

    private static Visual? FindDescendantByName(Visual root, string name)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (visual is StyledElement styled && styled.Name == name)
                return visual;
            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }

        return null;
    }

    #endregion

    #region Discovery

    private static void OnEndpointChanged()
    {
        DebugInstanceContext.SetMcpPipeName(Host.PipeName);
        if (Host.PipeName.Length > 0)
        {
            WriteDiscovery();
            Log.ZLogInformation(
                $"Debug MCP listening on \\\\.\\pipe\\{Host.PipeName} for {DebugInstanceContext.InstanceLabel}");
        }
        else
        {
            DeleteOwnedDiscovery();
        }
    }

    private static void WriteDiscovery()
    {
        try
        {
            var info = DebugInstanceContext.Info;
            var discovery = new DebugMcpDiscovery
            {
                PipeName = Host.PipeName,
                ProcessId = Environment.ProcessId,
                ExecutablePath = Environment.ProcessPath ?? "",
                InstanceId = info.InstanceId,
                InstanceLabel = info.InstanceLabel,
                WorkspaceRoot = info.WorkspaceRoot,
                ConfigRoot = info.ConfigRoot,
            };
            SharedDataFile.WriteAllTextAtomic(
                DebugInstanceContext.DiscoveryPath,
                JsonSerializer.Serialize(discovery, PrettyOptions));
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Could not write Debug MCP discovery file");
        }
    }

    private static void DeleteOwnedDiscovery()
    {
        try
        {
            var path = DebugInstanceContext.DiscoveryPath;
            if (!File.Exists(path))
                return;
            var discovery = JsonSerializer.Deserialize<DebugMcpDiscovery>(File.ReadAllText(path));
            if (discovery?.ProcessId == Environment.ProcessId)
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; a stale file names a dead process id.
        }
    }

    #endregion

    #region Describe

    private static string BuildDescribeText()
    {
        var sb = new StringBuilder();
        var instance = DebugInstanceContext.Info;
        sb.AppendLine(
            $"JeekLauncher debug MCP server on \\\\.\\pipe\\{Host.PipeName} "
            + $"(build {AutoUpdateService.GetLocalCommitCount()}).");
        sb.AppendLine($"InstanceId: {instance.InstanceId}");
        sb.AppendLine($"InstanceLabel: {instance.InstanceLabel}");
        sb.AppendLine($"WorkspaceRoot: {instance.WorkspaceRoot}");
        sb.AppendLine($"ProcessId: {instance.ProcessId}");
        sb.AppendLine($"ConfigRoot: {instance.ConfigRoot}");
        sb.AppendLine($"Process uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}.");
        sb.AppendLine($"Log file: {LogManager.CurrentRollingLogFile}");
        sb.AppendLine();
        sb.AppendLine("The launcher lives in the tray and its window is hidden until summoned by the");
        sb.AppendLine("global hotkey. An agent cannot press that hotkey — use show_launcher instead.");
        sb.AppendLine("To check search behavior without any UI at all, use search_probe.");
        sb.AppendLine();
        sb.AppendLine("Roots for object paths:");
        sb.AppendLine("- App: the Avalonia Application (this App instance)");
        sb.AppendLine("- Desktop: the IClassicDesktopStyleApplicationLifetime (Windows list, Shutdown, ...)");
        sb.AppendLine("- Launcher: the LauncherWindow (search box, results list)");
        sb.AppendLine("- LauncherVm: the LauncherViewModel (Query, Results, SelectedIndex, StatusText)");
        sb.AppendLine();
        sb.AppendLine(DebugMcpContract.PathHelp);
        sb.AppendLine();

        if (Desktop is not { } desktop)
        {
            sb.AppendLine("No desktop lifetime yet.");
        }
        else
        {
            sb.AppendLine($"Windows ({desktop.Windows.Count}):");
            foreach (var window in desktop.Windows)
            {
                sb.AppendLine(
                    $"- {window.GetType().Name} \"{window.Title}\" Visible={window.IsVisible} "
                    + $"State={window.WindowState} ClientSize={window.ClientSize} "
                    + $"DataContext={window.DataContext?.GetType().Name ?? "null"}");
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Avalonia tools

    private const int MaxVisualNodes = 2000;

    private static async Task<JsonObject> VisualTreeAsync(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? "Launcher";
        var maxDepth = Math.Max(1, args["max_depth"]?.GetValue<int>() ?? 12);

        var text = await OnUiAsync(() =>
        {
            if (Graph.Resolve(path) is not Visual root)
                throw new InvalidOperationException($"'{path}' is not a Visual.");

            var sb = new StringBuilder();
            var count = 0;
            AppendVisual(sb, root, 0, maxDepth, null, ref count);
            if (count >= MaxVisualNodes)
                sb.AppendLine($"… truncated at {MaxVisualNodes} nodes.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static void AppendVisual(
        StringBuilder sb, Visual visual, int depth, int maxDepth, object? parentDataContext, ref int count)
    {
        if (count >= MaxVisualNodes)
            return;
        count++;

        sb.Append(' ', depth * 2).Append(visual.GetType().Name);

        var dataContext = parentDataContext;
        if (visual is StyledElement styled)
        {
            if (!string.IsNullOrEmpty(styled.Name))
                sb.Append(" #").Append(styled.Name);
            var classes = string.Join(' ', styled.Classes);
            if (classes.Length > 0)
                sb.Append(" (").Append(classes).Append(')');
            dataContext = styled.DataContext;
            if (dataContext != null && !ReferenceEquals(dataContext, parentDataContext))
                sb.Append(" DataContext=").Append(dataContext.GetType().Name);
        }

        var bounds = visual.Bounds;
        sb.Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
        if (!visual.IsVisible)
            sb.Append(" HIDDEN");

        switch (visual)
        {
            case TextBlock { Text.Length: > 0 } textBlock:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBlock.Text, 80)}\"");
                break;
            case TextBox { Text.Length: > 0 } textBox:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBox.Text, 80)}\"");
                break;
        }

        sb.AppendLine();

        if (depth >= maxDepth)
        {
            if (visual.GetVisualChildren().Any())
                sb.Append(' ', (depth + 1) * 2).AppendLine("…");
            return;
        }

        foreach (var child in visual.GetVisualChildren())
            AppendVisual(sb, child, depth + 1, maxDepth, dataContext, ref count);
    }

    private static async Task<JsonObject> ScreenshotAsync(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();

        var hadToShow = await OnUiAsync(() =>
        {
            var app = CurrentApp;

            // The launcher is parked off-screen while hidden, and rendering it then would
            // capture nothing useful — show it first. Showing resets the query, and the
            // window hides itself whenever it loses focus, so the query has to be applied
            // after showing rather than by a previous show_launcher call.
            var wasHidden = !app.DebugWindow.IsVisible;
            if (wasHidden)
                app.DebugShowLauncher();
            if (query is not null)
                app.DebugViewModel.Query = query;
            return wasHidden;
        });

        // Let Avalonia lay out and realize the result containers: rendering in the same
        // callback captures an empty list. A window that was just shown needs noticeably
        // longer than one that was already up.
        await Task.Delay(hadToShow ? 600 : 150);

        var (bytes, pixelSize, shownQuery) = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var window = app.DebugWindow;
            var scaling = window.RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * scaling)));

            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return (stream.ToArray(), size, app.DebugViewModel.Query);
        });

        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Launcher window screenshot, {pixelSize.Width}x{pixelSize.Height}px. "
                               + $"Query=\"{shownQuery}\"",
                },
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(bytes),
                    ["mimeType"] = "image/png",
                }),
        };
    }

    #endregion

    #region Launcher tools

    private static async Task<JsonObject> ShowLauncherAsync(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();

        var text = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            app.DebugShowLauncher();
            if (query is not null)
                app.DebugViewModel.Query = query;

            var vm = app.DebugViewModel;
            return $"Launcher shown. Query=\"{vm.Query}\" Results={vm.Results.Count} "
                   + $"Selected={vm.SelectedIndex} Status=\"{vm.StatusText}\"";
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> HideLauncherAsync()
    {
        var text = await OnUiAsync(() =>
        {
            CurrentApp.DebugHideLauncher();
            return $"Launcher hidden. Visible={CurrentApp.DebugWindow.IsVisible}";
        });

        return ToolText(text);
    }

    /// <summary>
    /// Runs a query through the production search engine without touching the UI, so
    /// initials / pinyin ranking can be verified directly.
    /// </summary>
    private static async Task<JsonObject> SearchProbeAsync(JsonObject args)
    {
        var query = McpHost.RequiredString(args, "query");
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 10, 1, 40);

        var text = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var entries = app.DebugEntries;
            var usage = app.DebugUsage;
            var quickWords = app.DebugQuickWords;

            var results = SearchEngine.Search(query, entries, usage, quickWords);
            var boundId = quickWords.GetTarget(query.Trim().ToLowerInvariant());

            var sb = new StringBuilder();
            sb.AppendLine($"query=\"{query}\"");
            sb.AppendLine($"catalog={entries.Count} entries");
            sb.AppendLine($"matches={results.Count}");
            sb.AppendLine($"quickWordBinding={boundId ?? "(none)"}");
            if (results.Count == 0)
            {
                sb.AppendLine("No matches.");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("rank  launches  quickWord  name / initials / pinyin");
            var rank = 0;
            foreach (var entry in results.Take(limit))
            {
                var isBound = boundId is not null
                              && string.Equals(boundId, entry.Id, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine(
                    $"{rank,4}  {usage.GetCount(entry.Id),8}  {(isBound ? "yes" : "no"),9}  "
                    + $"{entry.Name} | {entry.Initials} | {entry.PinyinFull}");
                rank++;
            }

            if (results.Count > limit)
                sb.AppendLine($"… {results.Count - limit} more.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> EntriesSnapshotAsync(JsonObject args)
    {
        var filter = args["filter"]?.GetValue<string>();
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 20, 1, 100);

        var text = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var entries = app.DebugEntries;
            var scanned = ShortcutScanner.GetDirectories(app.DebugLocalSettings.ScanDirectories);

            var matched = filter is { Length: > 0 }
                ? entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList()
                : entries.ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"entries={entries.Count}");
            sb.AppendLine($"packagedApps={entries.Count(e => e.Aumid is not null)}");
            sb.AppendLine($"shortcuts={entries.Count(e => e.Aumid is null)}");
            sb.AppendLine($"filter={filter ?? "(none)"} matched={matched.Count}");
            sb.AppendLine();
            sb.AppendLine($"Scan directories ({scanned.Count}):");
            foreach (var dir in scanned)
                sb.AppendLine($"- {dir}{(Directory.Exists(dir) ? "" : "  (missing)")}");
            sb.AppendLine();
            sb.AppendLine("name | initials | pinyin | target");
            foreach (var entry in matched.Take(limit))
                sb.AppendLine($"{entry.Name} | {entry.Initials} | {entry.PinyinFull} | {entry.Subtitle}");
            if (matched.Count > limit)
                sb.AppendLine($"… {matched.Count - limit} more.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> RescanAsync()
    {
        var before = await OnUiAsync(() => CurrentApp.DebugEntries.Count);

        // The scan runs on its own STA thread; wait for the catalog to be swapped in.
        await await OnUiAsync(() => CurrentApp.DebugRescanAsync());

        var after = await OnUiAsync(() => CurrentApp.DebugEntries.Count);
        return ToolText($"Rescan finished. entriesBefore={before} entriesAfter={after}");
    }

    private static async Task<JsonObject> ConfigStateAsync()
    {
        var text = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var settings = app.DebugSettings;
            var local = app.DebugLocalSettings;

            var sb = new StringBuilder();
            sb.AppendLine("--- storage ---");
            sb.AppendLine($"savedLocation={local.StorageLocation}");
            sb.AppendLine($"effectiveLocation={Storage.EffectiveLocation}");
            sb.AppendLine($"portableForced={Storage.PortableForced}");
            sb.AppendLine($"customStorageDir={(local.CustomStorageDir.Length > 0 ? local.CustomStorageDir : "(none)")}");
            sb.AppendLine();
            sb.AppendLine("--- paths ---");
            sb.AppendLine($"roamableConfigDir={Storage.RoamableConfigDir}");
            sb.AppendLine($"settings={Storage.SettingsPath} exists={File.Exists(Storage.SettingsPath)}");
            sb.AppendLine($"quickWords={Storage.QuickWordsPath} exists={File.Exists(Storage.QuickWordsPath)}");
            sb.AppendLine($"localConfigDir={Storage.LocalConfigDir}");
            sb.AppendLine($"localSettings={Storage.LocalSettingsPath} exists={File.Exists(Storage.LocalSettingsPath)}");
            sb.AppendLine($"usage={Storage.UsagePath} exists={File.Exists(Storage.UsagePath)}");
            sb.AppendLine($"updateRoot={AutoUpdateService.UpdateRoot}");
            sb.AppendLine($"stagedPackage={AutoUpdateService.PackageDir} "
                          + $"exists={Directory.Exists(AutoUpdateService.PackageDir)}");
            sb.AppendLine($"logFile={LogManager.CurrentRollingLogFile}");
            sb.AppendLine();
            sb.AppendLine("--- settings ---");
            sb.AppendLine($"hotkey={settings.Hotkey}");
            sb.AppendLine($"language={(settings.Language.Length > 0 ? settings.Language : "(follow system)")}");
            sb.AppendLine($"theme={(settings.Theme.Length > 0 ? settings.Theme : "(follow system)")}");
            sb.AppendLine($"checkUpdateOnStartup={settings.CheckUpdateOnStartup}");
            sb.AppendLine($"updateCheckIntervalHours={settings.UpdateCheckIntervalHours}");
            sb.AppendLine($"localVersion={AutoUpdateService.GetLocalCommitCount()} "
                          + $"isDevBuild={AutoUpdateService.IsDevBuild}");
            sb.AppendLine($"scanDirectories={local.ScanDirectories.Count}");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> HotkeyStateAsync()
    {
        var (registered, text) = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var configured = app.DebugSettings.Hotkey;
            var parsed = HotKeyDef.Parse(configured);
            var failure = app.DebugHotkeyFailure;
            var ok = failure is null;

            return (ok,
                $"{(ok ? "PASS" : "FAIL")}: global hotkey registration\n"
                + $"configured={configured}\n"
                + $"parsedText={parsed.Text}\n"
                + $"modifiers=0x{parsed.Modifiers:X} virtualKey=0x{parsed.VirtualKey:X}\n"
                + $"registered={ok}\n"
                + $"failedHotkey={failure ?? "(none)"}");
        });

        return ToolText(text, isError: !registered);
    }

    private static async Task<JsonObject> QuickWordsAsync(JsonObject args)
    {
        var filter = args["filter"]?.GetValue<string>();

        var text = await OnUiAsync(() =>
        {
            var app = CurrentApp;
            var words = app.DebugQuickWords.Words;
            var byId = app.DebugEntries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            var matched = words
                .Where(pair => filter is not { Length: > 0 }
                               || pair.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"quickWords={words.Count} matched={matched.Count}");
            sb.AppendLine($"file={Storage.QuickWordsPath}");
            sb.AppendLine();
            sb.AppendLine("word -> target (present?)");
            foreach (var (word, id) in matched)
            {
                var present = byId.TryGetValue(id, out AppEntry? entry);
                sb.AppendLine($"{word} -> {id}  {(present ? $"(ok: {entry!.Name})" : "(missing from catalog)")}");
            }

            return sb.ToString();
        });

        return ToolText(text);
    }

    #endregion
}
