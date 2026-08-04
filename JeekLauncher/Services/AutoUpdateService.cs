using JeekTools;

namespace JeekLauncher.Services;

/// <summary>
/// App-specific configuration over the generic <see cref="AutoUpdater"/> in
/// JeekTools. See that class for how checking, staging, and installing work: the
/// build number is the commit count CI bakes in as the assembly's major version,
/// downloads fall back across <see cref="GitHubMirrors"/>, and a postponed package
/// is kept under <see cref="UpdateRoot"/> for reuse on the next check.
/// </summary>
public static class AutoUpdateService
{
    public const string HomepageUrl = "https://github.com/tifish/JeekLauncher";

    private const string AppExeName = "JeekLauncher.exe";

    private static readonly AutoUpdater Updater = new(new AutoUpdaterOptions
    {
        AppExeName = AppExeName,
        ReleaseZipUrl = "https://github.com/tifish/JeekLauncher/releases/download/latest_release/JeekLauncher.zip",
        VersionTxtUrl = "https://github.com/tifish/JeekLauncher/releases/download/latest_release/version.txt",
        UserAgent = "JeekLauncher-Updater/1.0",
        // Debug instances never self-update. Release keeps downloads under LocalAppData
        // so a postponed install survives reboots and temp cleanups.
        Disabled = DebugInstanceContext.IsDebugBuild,
        UpdateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JeekLauncher",
            "Update"),
    });

    public static int LocalCommitCount => Updater.LocalVersion;
    public static int RemoteCommitCount => Updater.RemoteVersion;
    public static string FailureReason => Updater.FailureReason;
    public static string UpdateRoot => Updater.UpdateRoot;
    public static string PackageDir => Updater.PackageDir;

    /// <summary>A dev build has version 0.0.0.0 (major 0); CI bakes the commit count into
    /// the major version. Never offer updates to a dev build.</summary>
    public static bool IsDevBuild => GetLocalCommitCount() == 0;

    public static int GetLocalCommitCount() => Updater.GetLocalVersion();

    public static Task<UpdateCheckOutcome> HasUpdateAsync() => Updater.HasUpdateAsync();

    /// <summary>Returns an already-downloaded package for the current remote version, if any.</summary>
    public static string? TryGetReusableStagedPackageDir() => Updater.TryGetReusableStagedPackageDir();

    /// <summary>Downloads and unpacks the release zip; returns the verified package directory.</summary>
    public static Task<string?> DownloadAndStageAsync(
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Updater.DownloadAndStageAsync(null, progress, cancellationToken);

    /// <summary>Hands the staged package to <c>AutoUpdate.ps1</c>, which swaps the files
    /// in once this process exits and restarts the app.</summary>
    public static bool LaunchInstall(string stagedPackageDir) => Updater.LaunchInstall(stagedPackageDir);
}
