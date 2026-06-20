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
    public static List<string> GetDirectories(AppSettings settings)
    {
        var directories = new List<string>();
        // The Start Menu roots (all-users + current-user); scanned recursively, so this
        // covers the Programs subfolder plus any shortcuts placed at the root.
        AddSpecialFolder(directories, Environment.SpecialFolder.CommonStartMenu);
        AddSpecialFolder(directories, Environment.SpecialFolder.StartMenu);
        foreach (string dir in settings.CustomDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                directories.Add(dir);
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<AppEntry> Scan(AppSettings settings)
    {
        var entries = new List<AppEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) .lnk / .url shortcuts from the Start Menu + custom directories.
        foreach (string dir in GetDirectories(settings))
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (string file in EnumerateShortcuts(dir))
            {
                try
                {
                    var entry = BuildEntry(file);
                    if (entry is null)
                        continue;

                    // De-duplicate by resolved target (the same app can have a
                    // shortcut in both the all-users and current-user Start Menu).
                    string key = (entry.Target ?? entry.Path).ToLowerInvariant();
                    if (!seenTargets.Add(key))
                        continue;

                    entries.Add(entry);
                    seenNames.Add(entry.Name);
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

                    entries.Add(BuildPackagedEntry(name, aumid));
                }
                catch
                {
                    // Skip individual problematic apps.
                }
            }
        }

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
            PinyinFull = HasCjk(name) ? PinyinHelper.GetFullPinyin(name) : searchName,
            Icon = IconLoader.LoadShellIcon(parsingName),
        };
    }

    private static AppEntry? BuildEntry(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(name) || IsNoiseName(name))
            return null;

        string extension = Path.GetExtension(file);
        string? target = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLinkResolver.Resolve(file)
            : null;

        if (target is not null && IsNoiseTarget(target))
            return null;

        string searchName = InitialsHelper.Normalize(name);
        string pinyinFull = HasCjk(name) ? PinyinHelper.GetFullPinyin(name) : searchName;

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

    private static bool HasCjk(string s)
    {
        foreach (char c in s)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string current = stack.Pop();

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(current); }
            catch { subdirectories = Array.Empty<string>(); }
            foreach (string sub in subdirectories)
                stack.Push(sub);

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
