using System.Runtime.Versioning;
using Windows.Management.Deployment;

namespace JeekLauncher.Services;

/// <summary>
/// Enumerates the current user's packaged (MSIX / Store) apps via the official
/// WinRT <see cref="PackageManager"/> + <c>GetAppListEntries</c> API — the same
/// source PowerToys Run's UWP indexer uses. Unlike <c>shell:AppsFolder</c>, this
/// returns only real, launchable apps (no Control Panel / SDK clutter) with clean
/// display names and AUMIDs. This is where Codex, Claude, Store apps, etc. live —
/// they have no shortcut file.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class PackagedAppScanner
{
    public static List<(string Name, string Aumid)> Scan()
    {
        var result = new List<(string, string)>();

        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry> entries;
                try
                {
                    entries = package.GetAppListEntries();
                }
                catch
                {
                    // Frameworks / resource packages have no launchable entries.
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        string name = entry.DisplayInfo.DisplayName;
                        string aumid = entry.AppUserModelId;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(aumid))
                            result.Add((name, aumid));
                    }
                    catch
                    {
                        // Skip individual unreadable entries.
                    }
                }
            }
        }
        catch
        {
            // PackageManager unavailable: just return whatever we have.
        }

        return result;
    }
}
