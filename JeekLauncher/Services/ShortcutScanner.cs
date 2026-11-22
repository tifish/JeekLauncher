using System.Runtime.Versioning;
using JeekLauncher.Models;

namespace JeekLauncher.Services;

/// <summary>
/// Walks the Start Menu (all-users + current-user) plus any custom directories,
/// collecting <c>.lnk</c> / <c>.url</c> shortcuts into <see cref="AppEntry"/> items.
/// Designed to run on a background STA thread (COM is used for .lnk resolution).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutScanner
{
    private static readonly string[] NameNoise =
    {
        "uninstall", "卸载", "unins000", "help", "帮助", "readme", "read me",
        "license", "许可", "官网", "website", "用户手册", "manual",
        "changelog", "change log", "release notes", "更新日志",
    };

    private static readonly string[] TargetNoiseExtensions =
    {
        ".chm", ".txt", ".html", ".htm", ".pdf", ".rtf",
    };

    /// <summary>
    /// The directories that get scanned: the all-users and current-user Start Menu
    /// plus any configured custom directories (de-duplicated).
    /// </summary>
    public static List<string> GetDirectories(IEnumerable<string> customDirectories)
    {
        var directories = new List<string>();
        // The Start Menu roots (all-users + current-user); scanned recursively, so this
        // covers the Programs subfolder plus any shortcuts placed at the root.
        AddSpecialFolder(directories, Environment.SpecialFolder.CommonStartMenu);
        AddSpecialFolder(directories, Environment.SpecialFolder.StartMenu);
        foreach (string dir in customDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                directories.Add(dir);
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<AppEntry> Scan(IEnumerable<string> customDirectories)
    {
        var entries = new List<AppEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeIconKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) .lnk / .url shortcuts from the Start Menu + custom directories.
        foreach (string dir in GetDirectories(customDirectories))
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (string file in EnumerateShortcuts(dir))
            {
                try
                {
                    var entry = BuildEntry(file, seenTargets);
                    if (entry is null)
                        continue;

                    entries.Add(entry);
                    seenNames.Add(entry.Name);
                    activeIconKeys.Add(entry.Path);
                }
                catch
                {
                    // Skip individual problematic shortcuts.
                }
            }
        }

        // 2) Packaged / Store apps (e.g. Codex, Claude) that have no shortcut file,
        //    via the WinRT PackageManager. Skip ones already found above (by name).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            foreach (var (name, aumid) in PackagedAppScanner.Scan())
            {
                try
                {
                    if (IsNoiseName(name) || !seenNames.Add(name))
                        continue;

                    var entry = BuildPackagedEntry(name, aumid);
                    entries.Add(entry);
                    activeIconKeys.Add(entry.Path);
                }
                catch
                {
                    // Skip individual problematic apps.
                }
            }
        }

        IconLoader.PruneCache(activeIconKeys);
        return entries;
    }

    private static AppEntry BuildPackagedEntry(string name, string aumid)
    {
        string parsingName = $"shell:AppsFolder\\{aumid}";
        string searchName = InitialsHelper.Normalize(name);

        return new AppEntry
        {
            Name = name,
            Path = parsingName,
            Aumid = aumid,
            Target = null,
            Initials = InitialsHelper.Compute(name),
            SearchName = searchName,
            PinyinFull = InitialsHelper.HasCjk(name) ? PinyinHelper.GetFullPinyin(name) : searchName,
            Icon = IconLoader.LoadShellIcon(parsingName),
        };
    }

    private static AppEntry? BuildEntry(string file, HashSet<string> seenTargets)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(name) || IsNoiseName(name))
            return null;

        string extension = Path.GetExtension(file);
        ShellLinkInfo linkInfo = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLinkResolver.ResolveInfo(file)
            : default;
        string? target = linkInfo.Target;

        if (target is not null && IsNoiseTarget(target))
            return null;

        // De-duplicate by resolved (target, arguments) before extracting icons or computing pinyin:
        // identical shortcuts across all-users + current-user Start Menu merge, while shortcuts
        // targeting the same host exe with distinct arguments (e.g. PWAs, dev prompts) stay separate.
        string dedupKey = target is null
            ? file
            : linkInfo.Arguments is { Length: > 0 } args
                ? $"{target}\0{args}"
                : target;
        if (!seenTargets.Add(dedupKey))
            return null;

        string searchName = InitialsHelper.Normalize(name);
        string pinyinFull = InitialsHelper.HasCjk(name) ? PinyinHelper.GetFullPinyin(name) : searchName;

        return new AppEntry
        {
            Name = name,
            Path = file,
            Target = target,
            Initials = InitialsHelper.Compute(name),
            SearchName = searchName,
            PinyinFull = pinyinFull,
            Icon = IconLoader.Load(file),
        };
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        if (visited.Add(CanonicalDir(root)))
            stack.Push(root);

        while (stack.Count > 0)
        {
            string current = stack.Pop();

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(current); }
            catch { subdirectories = Array.Empty<string>(); }
            foreach (string sub in subdirectories)
            {
                if (visited.Add(CanonicalDir(sub)))
                    stack.Push(sub);
            }

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = Array.Empty<string>(); }

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static string CanonicalDir(string path)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.GetFullPath(resolved?.FullName ?? path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static void AddSpecialFolder(List<string> directories, Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                directories.Add(path);
        }
        catch
        {
            // Ignore folders that cannot be resolved.
        }
    }

    private static bool IsNoiseName(string name)
    {
        foreach (string noise in NameNoise)
        {
            if (name.Contains(noise, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsNoiseTarget(string target)
    {
        if (target.Contains("uninst", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("卸载", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string ext = Path.GetExtension(target);
        foreach (string noise in TargetNoiseExtensions)
        {
            if (ext.Equals(noise, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
