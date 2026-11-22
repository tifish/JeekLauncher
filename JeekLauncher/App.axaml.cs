using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Win32;
using Jeek.Avalonia.Localization;
using JeekLauncher.Models;
using JeekLauncher.Services;
using JeekLauncher.ViewModels;
using JeekLauncher.Views;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekLauncher;

public partial class App : Application
{
    /// <summary>Single-instance lock. Debug scopes it per worktree; see <see cref="DebugInstanceContext"/>.</summary>
    public static string MutexName => DebugInstanceContext.SingleInstanceMutexName;

    public static string ShowEventName => DebugInstanceContext.ShowEventName;

    private static readonly Uri IconUri = new("avares://JeekLauncher/Assets/jeek.png");

    private AppSettings _settings = null!;
    private LocalSettings _local = null!;
    private UsageStore _usage = null!;
    private QuickWordStore _quickWords = null!;
    private LauncherViewModel _viewModel = null!;
    private LauncherWindow _window = null!;
    private readonly HotKey _hotKey = new();
    private TrayIcon? _tray;
    private WindowIcon? _appIcon;
    private string? _hotkeyFailedText;
    private EventWaitHandle? _showEvent;
    private DirectoryWatcher? _watcher;
    private ConfigWatcher? _configWatcher;
    private IReadOnlyList<AppEntry> _entries = Array.Empty<AppEntry>();
    private SettingsWindow? _settingsWindow;
    private QuickWordsWindow? _quickWordsWindow;
    private bool _initialized;
    private DispatcherTimer? _updateTimer;
    private int _updateInProgress;
    private int _scanVersion;

    private static readonly ILogger Log = LogManager.CreateLogger(nameof(App));

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // ---- Debug MCP surface -------------------------------------------------
    // Live state and actions the Debug MCP tools read and drive. See DebugMcpServer;
    // these exist because an agent cannot press the global hotkey or watch the tray.

    internal LauncherWindow DebugWindow => _window;
    internal LauncherViewModel DebugViewModel => _viewModel;
    internal IReadOnlyList<AppEntry> DebugEntries => _entries;
    internal AppSettings DebugSettings => _settings;
    internal LocalSettings DebugLocalSettings => _local;
    internal UsageStore DebugUsage => _usage;
    internal QuickWordStore DebugQuickWords => _quickWords;

    /// <summary>Hotkey text that failed to register, or null when the hotkey is held.</summary>
    internal string? DebugHotkeyFailure => _hotkeyFailedText;

    internal void DebugShowLauncher() => ShowLauncher();

    internal void DebugHideLauncher() => HideLauncher();

    /// <summary>Rescans the shortcut directories and completes once the catalog is replaced.</summary>
    internal Task DebugRescanAsync()
    {
        var completion = new TaskCompletionSource();
        RescanFromDisk(() => completion.TrySetResult());
        return completion.Task;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The launcher lives in the tray; never quit just because the window hides.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Localizer.SetLocalizer(new TabLocalizer(
                Path.Combine(AppContext.BaseDirectory, "Data", "Languages.tab")));

            // Machine-local settings first: they decide where roamable config is read from.
            _local = LocalSettings.Load();
            Storage.SetLocation(_local.StorageLocation, _local.CustomStorageDir);

            _settings = AppSettings.Load();
            ApplyLanguage(_settings.Language);
            ApplyTheme(_settings.Theme);
            Log.ZLogInformation($"JeekLauncher starting (build {AutoUpdateService.GetLocalCommitCount()})");
            _usage = UsageStore.Load();
            _quickWords = QuickWordStore.Load();
            _viewModel = new LauncherViewModel(_usage, _quickWords);

            // Refresh code-built strings (tray labels, status line) on language change.
            Localizer.LanguageChanged += (_, _) =>
            {
                BuildTrayMenu();
                _viewModel.Refresh();
            };

            _window = new LauncherWindow(_viewModel);
            _window.LaunchRequested += OnLaunchRequested;
            _window.QuickWordSetRequested += OnQuickWordSetRequested;
            _window.HideRequested += HideLauncher;
            _window.Opened += OnWindowFirstOpened;

            _hotKey.Pressed = () => Dispatcher.UIThread.Post(ToggleLauncher);

            _appIcon = LoadAppIcon();

            SetUpTray();
            SetUpShowSignal();
            StartScan();
            SetUpWatcher();
            SetUpConfigWatcher();
            SetUpUpdates();
            SetUpDebugMcp(desktop);
            _ = Task.Run(AutoStart.MigrateLegacyRunValue);

            // Realize the native window off-screen so we have a handle for the
            // global hotkey, then hide it until summoned (done in OnWindowFirstOpened).
            _window.Position = new PixelPoint(-32000, -32000);
            _window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnWindowFirstOpened(object? sender, EventArgs e)
    {
        if (_initialized)
            return;
        _initialized = true;

        _window.Opened -= OnWindowFirstOpened;

        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            Win32Properties.AddWndProcHookCallback(_window, _hotKey.WndProc);

            if (!ReregisterHotkey())
            {
                BuildTrayMenu();
                string text = HotKeyDef.Parse(_settings.Hotkey).Text;
                Dispatcher.UIThread.Post(() => OpenSettings(text));
            }
        }

        _window.Hide();
    }

    // ---- Show / hide -------------------------------------------------------

    private void ToggleLauncher()
    {
        if (_window.IsVisible)
            HideLauncher();
        else
            ShowLauncher();
    }

    private void ShowLauncher()
    {
        PositionWindow();
        _viewModel.Reset();

        _window.Show();
        _window.Activate();

        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Win32Interop.ForceForeground(handle);

        // Focus after the show has been processed so the caret lands in the box.
        Dispatcher.UIThread.Post(() => _window.FocusSearch(), DispatcherPriority.Input);
    }

    private void HideLauncher() => _window.Hide();

    private void PositionWindow()
    {
        // Prefer the screen under the mouse cursor for multi-monitor setups.
        Screen? screen = null;
        if (Win32Interop.GetCursorPos(out var p))
            screen = _window.Screens.ScreenFromPoint(new PixelPoint(p.X, p.Y));
        screen ??= _window.Screens.Primary ?? _window.Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)(_window.Width * scale);

        int x = area.X + (area.Width - width) / 2;
        int y = area.Y + (int)(area.Height * 0.22);
        _window.Position = new PixelPoint(x, y);
    }

    // ---- Launch ------------------------------------------------------------

    private void OnLaunchRequested(AppEntry entry, string query)
    {
        if (!LaunchEntry(entry))
            return;

        // Remember the quick word so this query jumps to the top next time.
        _usage.Record(entry.Id);
        _usage.Save();
        if (_quickWords.Set(query, entry.Id))
        {
            _quickWords.Save();
            _quickWordsWindow?.Reload();
        }
    }

    // Bind an explicit quick word to an app, then launch it.
    private void OnQuickWordSetRequested(AppEntry entry, string word)
    {
        string w = word.Trim().ToLowerInvariant();
        if (w.Length == 0)
            return;

        if (!LaunchEntry(entry))
            return;

        // Bump the launch count and bind the explicit quick word to the app.
        _usage.Record(entry.Id);
        _usage.Save();
        _quickWords.Set(w, entry.Id);
        _quickWords.Save();
        _quickWordsWindow?.Reload();
    }

    /// <summary>Hides the launcher and starts the app; returns whether it launched.</summary>
    private bool LaunchEntry(AppEntry entry)
    {
        HideLauncher();

        try
        {
            if (entry.Aumid is not null)
            {
                // Packaged / AppsFolder app: activate by AUMID via the shell.
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.Aumid}")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            // Target may be missing/blocked; nothing actionable to show here.
            Log.ZLogWarning($"Failed to launch {entry.Path}: {ex.Message}");
            return false;
        }

        return true;
    }

    // ---- Scanning ----------------------------------------------------------

    /// <param name="onCompleted">Runs on the UI thread once the new catalog is in place.</param>
    private void StartScan(Action? onCompleted = null)
    {
        int version = Interlocked.Increment(ref _scanVersion);
        var dirs = _local.ScanDirectories.ToArray();
        var thread = new Thread(() =>
        {
            var entries = ShortcutScanner.Scan(dirs);
            Dispatcher.UIThread.Post(() =>
            {
                if (version == Volatile.Read(ref _scanVersion))
                {
                    _entries = entries;
                    _viewModel.SetEntries(entries);
                    _quickWordsWindow?.Reload();
                }
                onCompleted?.Invoke();
            });
        })
        {
            IsBackground = true,
            Name = "JeekLauncher.Scan",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ---- Directory monitoring ----------------------------------------------

    private void SetUpWatcher()
    {
        _watcher = new DirectoryWatcher(() => Dispatcher.UIThread.Post(() => StartScan()));
        _watcher.Watch(ShortcutScanner.GetDirectories(_local.ScanDirectories));
    }

    private void SetUpConfigWatcher()
    {
        _configWatcher = new ConfigWatcher(changes => Dispatcher.UIThread.Post(() => ReloadConfigFromDisk(changes)));
        _configWatcher.Watch(Storage.RoamableConfigDir);
    }

    /// <summary>
    /// Re-reads the roamable settings / quick words that changed externally
    /// (hand-edited or synced into the Config folder).
    /// </summary>
    private void ReloadConfigFromDisk(ConfigChangeKind changes)
    {
        if (changes.HasFlag(ConfigChangeKind.Settings))
        {
            _settings = AppSettings.Load();
            ApplyLanguage(_settings.Language);
            ApplyTheme(_settings.Theme);
            RestartUpdateTimer();
            if (_settingsWindow is null)
                ReregisterHotkey();
            BuildTrayMenu();
        }

        if (changes.HasFlag(ConfigChangeKind.QuickWords))
        {
            _quickWords.ReloadFromDisk();
            _quickWordsWindow?.Reload();
            _viewModel.Refresh(); // new quick words affect ranking
        }
    }

    /// <summary>
    /// Switches the roamable storage location. <see cref="Storage"/> moves (or does not move)
    /// the config files; here we re-point the watcher at the new folder and make sure the
    /// roamable files exist there even when nothing was carried over.
    /// </summary>
    private void SetStorageLocation(StorageLocation location, string customDir, bool moveFiles)
    {
        if (!Storage.WouldChangeRoamableConfigDir(location, customDir))
        {
            Storage.SetLocation(location, customDir);
            return;
        }

        Storage.SwitchLocation(location, customDir, moveFiles);

        if (!moveFiles)
        {
            // Nothing was carried over, so the destination has no file to merge into:
            // write the in-memory state out whole.
            _settings.Save(forceAllLocal: true);
            _quickWords.Save();
        }

        _configWatcher?.Watch(Storage.RoamableConfigDir);
    }

    // ---- Tray icon ---------------------------------------------------------

    private void SetUpTray()
    {
        if (_appIcon is not null)
            _window.Icon = _appIcon;

        _tray = new TrayIcon
        {
            Icon = _appIcon,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowLauncher);

        BuildTrayMenu();
    }

    /// <summary>(Re)builds the tray menu and tooltip with the current language.</summary>
    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;

        _tray.ToolTipText = _hotkeyFailedText is null
            ? Localizer.Get("AppName")
            : string.Format(Localizer.Get("HotkeyRegisterFailedTip"), _hotkeyFailedText);

        var menu = new NativeMenu();
        menu.Items.Add(TrayItem("TrayShow", () => Dispatcher.UIThread.Post(ShowLauncher)));
        menu.Items.Add(TrayItem("Rescan", () => Dispatcher.UIThread.Post(() => RescanFromDisk())));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("ManageQuickWords", () => Dispatcher.UIThread.Post(OpenQuickWords)));
        menu.Items.Add(TrayItem("Settings", () => Dispatcher.UIThread.Post(() => OpenSettings())));
        menu.Items.Add(TrayItem("OpenConfigFolder", OpenConfigFolder));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("CheckForUpdates", () => Dispatcher.UIThread.Post(() => _ = CheckForUpdatesAsync(silent: false))));
        menu.Items.Add(TrayItem("TrayExit", Shutdown));
        _tray.Menu = menu;
    }

    private static NativeMenuItem TrayItem(string key, Action onClick)
    {
        var item = new NativeMenuItem(Localizer.Get(key));
        item.Click += (_, _) => onClick();
        return item;
    }

    private void RescanFromDisk(Action? onCompleted = null)
    {
        // Reload local settings (scan dirs may have been hand-edited) then rescan + re-watch.
        _local = LocalSettings.Load();
        _watcher?.Watch(ShortcutScanner.GetDirectories(_local.ScanDirectories));
        StartScan(onCompleted);
    }

    // ---- Settings & quick-string windows -----------------------------------

    private void OpenSettings(string? hotkeyError = null)
    {
        if (_settingsWindow is null)
        {
            // Suspend the global hotkey while settings is open so pressing a combo into
            // the capture box records it instead of firing the launcher; restore on close.
            _hotKey.Unregister();

            _settingsWindow = new SettingsWindow(
                _settings, _local, Storage.EffectiveLocation, Storage.CustomDir,
                CanRegisterHotkey, ApplySettings, SetStorageLocation,
                () => _ = CheckForUpdatesAsync(silent: false))
            {
                Icon = _appIcon,
            };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ReregisterHotkey();
                BuildTrayMenu();
            };
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
        if (hotkeyError is not null)
            _settingsWindow.ShowHotkeyError(hotkeyError);
    }

    private void OpenQuickWords()
    {
        if (_quickWordsWindow is not null)
        {
            _quickWordsWindow.Activate();
            return;
        }

        _quickWordsWindow = new QuickWordsWindow(_quickWords, () => _entries, OnQuickWordsEdited)
        {
            Icon = _appIcon,
        };
        _quickWordsWindow.Closed += (_, _) => _quickWordsWindow = null;
        _quickWordsWindow.Show();
        _quickWordsWindow.Activate();
    }

    // The quick-words window persists changes itself; refresh the result ranking.
    private void OnQuickWordsEdited()
    {
        _viewModel.Refresh();
    }

    private bool CanRegisterHotkey(string hotkeyText)
    {
        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var hotkey = HotKeyDef.Parse(hotkeyText);
        return _hotKey.CanRegister(handle, hotkey.Modifiers, hotkey.VirtualKey);
    }

    /// <summary>
    /// Applies new settings (roamable + machine-local); returns whether the hotkey
    /// registered successfully. Any storage-mode switch has already been applied by the
    /// settings window before this runs, so saves land in the right folder.
    /// </summary>
    private bool ApplySettings(AppSettings newSettings, LocalSettings newLocal)
    {
        if (!TryRegisterHotkey(newSettings.Hotkey))
        {
            // Keep the hotkey suspended while SettingsWindow stays open so the user
            // can still press any shortcut in the capture box without firing the launcher.
            _hotKey.Unregister();
            return false;
        }

        // Mutate the loaded instances in place so their merge baselines stay intact —
        // otherwise changing a setting back to its default would be discarded by the
        // three-way merge in JsonSettingsFile.
        _settings.ApplyFrom(newSettings);
        _local.ApplyFrom(newLocal);
        _settings.Save();
        _local.Save();

        // Switch language (raises LanguageChanged -> rebuilds tray + status line).
        ApplyLanguage(_settings.Language);
        ApplyTheme(_settings.Theme);
        RestartUpdateTimer();

        BuildTrayMenu(); // refresh tooltip (and labels, if language was unchanged)

        // Re-watch the new directory set and rescan.
        _watcher?.Watch(ShortcutScanner.GetDirectories(_local.ScanDirectories));
        StartScan();

        return true;
    }

    /// <summary>(Re)registers the global hotkey for the current settings; returns success.</summary>
    private bool ReregisterHotkey() => TryRegisterHotkey(_settings.Hotkey);

    private bool TryRegisterHotkey(string hotkeyText)
    {
        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return true;

        var hotkey = HotKeyDef.Parse(hotkeyText);
        bool ok = _hotKey.Register(handle, hotkey.Modifiers, hotkey.VirtualKey);
        _hotkeyFailedText = ok ? null : hotkey.Text;
        if (!ok)
            Log.ZLogWarning($"Hotkey registration failed: {hotkey.Text}");
        return ok;
    }

    /// <summary>
    /// Sets the UI language. An empty value follows the system UI culture
    /// (Chinese → zh, otherwise en).
    /// </summary>
    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language))
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";

        if (Localizer.Languages.Contains(language))
            Localizer.Language = language;
    }

    private void ApplyTheme(string? theme) => RequestedThemeVariant = ThemeVariantFor(theme);

    /// <summary>Maps a stored theme string to an Avalonia theme variant
    /// ("" / null / unrecognized = follow system).</summary>
    public static ThemeVariant ThemeVariantFor(string? theme) => theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    // ---- Auto-update -------------------------------------------------------

    /// <summary>Runs the optional startup check and (re)arms the periodic timer.</summary>
    private void SetUpUpdates()
    {
        if (_settings.CheckUpdateOnStartup && !AutoUpdateService.IsDevBuild)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await CheckForUpdatesAsync(silent: true).ConfigureAwait(false);
            });
        }

        RestartUpdateTimer();
    }

    /// <summary>(Re)starts the periodic update-check timer for the current interval setting.</summary>
    private void RestartUpdateTimer()
    {
        _updateTimer?.Stop();
        _updateTimer = null;

        int hours = _settings.UpdateCheckIntervalHours;
        if (hours <= 0 || AutoUpdateService.IsDevBuild)
            return;

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(hours) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(silent: true);
        _updateTimer.Start();
    }

    /// <summary>
    /// Checks GitHub for a newer release and, when there is one, downloads it in the
    /// background before asking anything — the single prompt the user sees is "restart and
    /// install now?", once the package is on disk and verified. Declining keeps the package,
    /// so the next check reuses it instead of downloading again. A silent check stays quiet
    /// when the app is already current or something failed; a manual check reports both.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0)
            return;
        try
        {
            var outcome = await AutoUpdateService.HasUpdateAsync().ConfigureAwait(false);

            if (outcome != UpdateCheckOutcome.Available)
            {
                if (!silent)
                {
                    string message = outcome == UpdateCheckOutcome.UpToDate
                        ? string.Format(Localizer.Get("UpToDate"), AutoUpdateService.LocalCommitCount)
                        : Localizer.Get("UpdateCheckFailed");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        MessageDialog.ShowInfoAsync(Localizer.Get("AppName"), message, _appIcon));
                }

                return;
            }

            // Download first, ask second. Progress goes to the tray tooltip — the launcher
            // has no status bar, and the window is hidden most of the time.
            string? packageDir = AutoUpdateService.TryGetReusableStagedPackageDir();
            if (packageDir is null)
            {
                var progress = new Progress<UpdateDownloadProgress>(ShowDownloadProgress);
                packageDir = await AutoUpdateService.DownloadAndStageAsync(progress).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(BuildTrayMenu); // clear the progress tooltip
            }

            if (packageDir is null)
            {
                Log.ZLogWarning($"Update download failed: {AutoUpdateService.FailureReason}");
                if (!silent)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => MessageDialog.ShowInfoAsync(
                        Localizer.Get("AppName"),
                        string.Format(Localizer.Get("UpdateDownloadFailed"), AutoUpdateService.FailureReason),
                        _appIcon));
                }

                return;
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                bool install = await MessageDialog.ShowConfirmAsync(
                    Localizer.Get("AppName"),
                    string.Format(Localizer.Get("UpdateReady"),
                        AutoUpdateService.RemoteCommitCount, AutoUpdateService.LocalCommitCount),
                    Localizer.Get("RestartAndInstall"), Localizer.Get("Later"), _appIcon);
                if (!install)
                    return; // package stays staged for the next check

                // Flush everything before handing the install over: the script waits for
                // this process to exit and then replaces the program directory.
                _settings.Save();
                _local.Save();
                _usage.Save();
                _quickWords.Save();

                if (AutoUpdateService.LaunchInstall(packageDir))
                    Shutdown();
            });
        }
        finally
        {
            Volatile.Write(ref _updateInProgress, 0);
        }
    }

    /// <summary>Reports download progress through the tray tooltip.</summary>
    private void ShowDownloadProgress(UpdateDownloadProgress progress)
    {
        if (_tray is null)
            return;

        string text = progress.TotalBytes is > 0
            ? $"{progress.ReceivedBytes * 100 / progress.TotalBytes.Value}%"
            : $"{progress.ReceivedBytes / 1024 / 1024} MB";
        string tooltip = string.Format(Localizer.Get("UpdateDownloading"), text);
        Dispatcher.UIThread.Post(() =>
        {
            if (_tray is not null)
                _tray.ToolTipText = tooltip;
        });
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(IconUri);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void OpenConfigFolder()
    {
        try
        {
            Storage.EnsureRoamableConfigDir();
            Process.Start(new ProcessStartInfo(Storage.RoamableConfigDir) { UseShellExecute = true });
        }
        catch
        {
            // Best effort.
        }
    }

    // ---- Debug MCP ---------------------------------------------------------

    /// <summary>
    /// In Debug builds, installs the fixed per-user stdio adapter, registers this worktree
    /// behind it, and starts the named-pipe Debug MCP host. Release builds skip adapter
    /// installation until a product MCP surface exists. Failures here must never keep the
    /// launcher from starting.
    /// </summary>
    private void SetUpDebugMcp(IClassicDesktopStyleApplicationLifetime desktop)
    {
#if DEBUG
        try
        {
            McpAdapterRegistration.RegisterCurrentInstance();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Could not register the MCP adapter: {ex.Message}");
        }
#endif

        try
        {
            DebugMcpServer.Start();
            desktop.Exit += (_, _) => DebugMcpServer.Stop();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Could not start the Debug MCP server: {ex.Message}");
        }
    }

    // ---- Single-instance "show me" signal ----------------------------------

    private void SetUpShowSignal()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.UIThread.Post(ShowLauncher);
            })
            {
                IsBackground = true,
                Name = "JeekLauncher.ShowSignal",
            };
            thread.Start();
        }
        catch
        {
            // Without the signal a second launch simply does nothing.
        }
    }

    // ---- Shutdown ----------------------------------------------------------

    private void Shutdown()
    {
        _hotKey.Unregister();
        _updateTimer?.Stop();
        _watcher?.Dispose();
        _configWatcher?.Dispose();
        _settingsWindow?.Close();
        _quickWordsWindow?.Close();

        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }

        LogManager.Shutdown();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
