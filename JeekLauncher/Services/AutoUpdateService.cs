using System.Net.Http;
using System.Reflection;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekLauncher.Services;

public enum UpdateCheckOutcome
{
    Available,
    UpToDate,
    Failed,
}

/// <summary>
/// Checks GitHub Releases for a newer build and, if confirmed, launches the
/// PowerShell updater (<c>AutoUpdate.ps1</c>) that replaces the install on disk and
/// restarts the app. The build number is the commit count, baked in by CI as the
/// assembly's major version. All GitHub access goes through
/// <see cref="GitHubMirrors"/>, so checks and downloads still work where
/// github.com itself is unreachable.
/// </summary>
public static class AutoUpdateService
{
    public const string HomepageUrl = "https://github.com/tifish/JeekLauncher";

    private const string ReleaseZipUrl =
        "https://github.com/tifish/JeekLauncher/releases/download/latest_release/JeekLauncher.zip";

    private const string VersionTxtUrl =
        "https://github.com/tifish/JeekLauncher/releases/download/latest_release/version.txt";

    private const string UpdateScriptName = "AutoUpdate.ps1";

    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdateService));

    /// <summary>The (possibly mirror-rewritten) zip URL handed to the updater script.</summary>
    public static string DownloadUrl { get; private set; } = "";
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    /// <summary>A local build below the CI threshold (10) is a dev build; never offer updates to it.</summary>
    public static bool IsDevBuild => GetLocalCommitCount() < 10;

    public static async Task<UpdateCheckOutcome> HasUpdateAsync()
    {
        DownloadUrl = ReleaseZipUrl;
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = GetLocalCommitCount();

        try
        {
            // Probe the small version.txt to pick a reachable/fast GitHub mirror.
            GitHubMirrors.TestUrl = VersionTxtUrl;
            var versionUrl = await GitHubMirrors.GetFastestMirror(VersionTxtUrl).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionUrl))
                return Fail("no reachable GitHub mirror");

            var remote = await DownloadTextAsync(versionUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(remote))
                return Fail($"empty version.txt from {versionUrl}");

            if (!int.TryParse(remote.Trim(), out var remoteCount) || remoteCount <= 0)
                return Fail($"version.txt is not a positive integer: '{remote.Trim()}'");
            RemoteCommitCount = remoteCount;

            // Below the threshold is a local dev build — CI bakes in the real
            // commit count, which is always well above this.
            if (LocalCommitCount < 10)
                return Fail("local version unavailable (dev build?)");

            // Reuse the chosen mirror for the zip download URL.
            var zipUrl = await GitHubMirrors.GetFastestMirror(ReleaseZipUrl).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(zipUrl))
                DownloadUrl = zipUrl;

            if (RemoteCommitCount > LocalCommitCount)
            {
                Log.ZLogInformation($"Update available: local={LocalCommitCount}, remote={RemoteCommitCount}");
                return UpdateCheckOutcome.Available;
            }

            return UpdateCheckOutcome.UpToDate;
        }
        catch (Exception ex)
        {
            return Fail($"exception: {ex.Message}");
        }
    }

    public static bool LaunchUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(DownloadUrl))
                return false;

            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var workDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workDir))
                return false;

            var scriptPath = Path.Combine(workDir, UpdateScriptName);
            if (!File.Exists(scriptPath))
            {
                Log.ZLogWarning($"Updater script not found: {scriptPath}");
                return false;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{DownloadUrl}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true,
            });

            Log.ZLogInformation($"Launched updater from {DownloadUrl}");
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to launch updater: {ex.Message}");
            return false;
        }
    }

    public static int GetLocalCommitCount()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static UpdateCheckOutcome Fail(string reason)
    {
        FailureReason = reason;
        Log.ZLogWarning($"Update check failed: {reason}");
        return UpdateCheckOutcome.Failed;
    }

    private static async Task<string?> DownloadTextAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JeekLauncher-Updater/1.0");
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
