using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace JeekLauncher.Services;

/// <summary>
/// One application instance the fixed per-user MCP adapter can route to.
/// Release uses the stable <c>release</c> key; every Debug worktree uses its path-derived id.
/// </summary>
public sealed record McpRegisteredInstance(
    string InstanceId,
    string AppPath,
    string DebugPipeName,
    bool IsDebugBuild,
    string WorkspaceRoot);

/// <summary>
/// Fixed adapter location and per-instance registry shared by the app and the stdio adapter
/// (which links this file). The adapter executable lives at a stable path forever, while
/// these entries point it at the current install or a specific Debug worktree.
/// </summary>
public static class McpAdapterRegistry
{
    private const string RegistryBasePath = @"Software\JeekLauncher\Mcp\Instances";
    private const string InstallMutexName = "JeekLauncher.McpAdapter.Install";

    public static string AdapterDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekLauncher",
        "Mcp");

    public static string AdapterPath { get; } =
        Path.Combine(AdapterDirectory, "JeekLauncherMcp.exe");

    /// <summary>
    /// Installs the adapter built next to the app into the stable per-user path that agents
    /// launch. Agents must use that fixed path (or the worktree debug launcher that invokes
    /// it), never the side-by-side copy under <c>bin\</c> — holding that one open would fail
    /// the next build.
    /// </summary>
    public static bool EnsureAdapterInstalled(string sourcePath, bool allowUpdate)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The JeekLauncher MCP adapter was not published.", sourcePath);

        Directory.CreateDirectory(AdapterDirectory);
        using var mutex = new Mutex(false, InstallMutexName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
                return File.Exists(AdapterPath);

            if (File.Exists(AdapterPath)
                && (!allowUpdate || HaveSameFileMetadata(sourcePath, AdapterPath)))
            {
                return true;
            }

            var temporary = AdapterPath + "." + Environment.ProcessId + ".new";
            try
            {
                File.Copy(sourcePath, temporary, overwrite: true);
                File.Move(temporary, AdapterPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && File.Exists(AdapterPath))
            {
                // An agent may currently be running the fixed executable. It is a protocol-
                // agnostic pipe forwarder, so keeping the installed copy is safe.
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup after a locked replacement.
                }
            }
        }
        finally
        {
            if (lockTaken)
                mutex.ReleaseMutex();
        }
    }

    public static void WriteInstance(McpRegisteredInstance instance)
    {
        ValidateInstanceId(instance.InstanceId);
        using var key = Registry.CurrentUser.CreateSubKey(
                            $@"{RegistryBasePath}\{instance.InstanceId}", writable: true)
                        ?? throw new InvalidOperationException("Could not open the MCP instance registry key.");

        key.SetValue("AppPath", Path.GetFullPath(instance.AppPath), RegistryValueKind.String);
        key.SetValue("DebugPipeName", instance.DebugPipeName, RegistryValueKind.String);
        key.SetValue("Build", instance.IsDebugBuild ? "Debug" : "Release", RegistryValueKind.String);
        key.SetValue("WorkspaceRoot", Path.GetFullPath(instance.WorkspaceRoot), RegistryValueKind.String);
        key.SetValue("UpdatedUtc", DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
    }

    public static bool TryReadInstance(string? instanceId, out McpRegisteredInstance instance)
    {
        instanceId = string.IsNullOrWhiteSpace(instanceId) ? "release" : instanceId.Trim();
        instance = null!;
        if (!IsValidInstanceId(instanceId))
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"{RegistryBasePath}\{instanceId}", writable: false);
            if (key is null)
                return false;

            var appPath = key.GetValue("AppPath") as string ?? "";
            var debugPipe = key.GetValue("DebugPipeName") as string ?? "";
            var build = key.GetValue("Build") as string ?? "";
            var workspace = key.GetValue("WorkspaceRoot") as string ?? "";
            if (!Path.IsPathFullyQualified(appPath) || !File.Exists(appPath))
                return false;

            // A Debug entry must still hash to the id it is filed under, or a moved/stale
            // worktree could hijack another instance's route.
            var isDebug = build.Equals("Debug", StringComparison.OrdinalIgnoreCase);
            if (isDebug
                && !string.Equals(
                    McpPipeNames.InstanceId(Path.GetDirectoryName(appPath)!),
                    instanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            instance = new McpRegisteredInstance(
                instanceId,
                Path.GetFullPath(appPath),
                debugPipe,
                isDebug,
                workspace.Length == 0 ? Path.GetDirectoryName(appPath)! : Path.GetFullPath(workspace));
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether an installed adapter is current using only file size and UTC
    /// modification time. File contents are deliberately not read.
    /// </summary>
    public static bool HaveSameFileMetadata(string first, string second)
    {
        var a = new FileInfo(first);
        var b = new FileInfo(second);
        return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }

    private static void ValidateInstanceId(string instanceId)
    {
        if (!IsValidInstanceId(instanceId))
            throw new ArgumentException($"Invalid MCP instance id '{instanceId}'.", nameof(instanceId));
    }

    private static bool IsValidInstanceId(string instanceId) =>
        instanceId.Equals("release", StringComparison.OrdinalIgnoreCase)
        || instanceId.Length == 12 && instanceId.All(char.IsAsciiHexDigit);
}
