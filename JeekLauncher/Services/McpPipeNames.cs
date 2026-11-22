using System.Security.Cryptography;
using System.Text;

namespace JeekLauncher.Services;

/// <summary>
/// Named-pipe naming convention shared by the app and the <c>JeekLauncherMcp</c> stdio adapter.
/// Compiled into both (the adapter links this file), so the two sides can never drift.
///
/// A pipe replaces a loopback HTTP endpoint for MCP: nothing to allocate, so the name is
/// stable forever and can be hard-coded in a client config, and access control is the pipe's
/// own ACL rather than a URL token. Debug builds append an instance id derived from the
/// executable directory, so parallel worktree instances never answer for each other.
/// </summary>
public static class McpPipeNames
{
    /// <summary>Debug surface: object graph, visual tree, probes. Debug builds only.</summary>
    public const string DebugBase = "JeekLauncher.Mcp.Debug";

    /// <summary>
    /// Stable 12-hex identity of an installation, hashed from its executable directory.
    /// The adapter is told which app directory to talk to, so it derives the same value
    /// the app did without any shared state.
    /// </summary>
    public static string InstanceId(string executableDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Normalize(executableDirectory))))[..12].ToLowerInvariant();

    public static string Debug(string? instanceId) => Compose(DebugBase, instanceId);

    /// <summary>Resolves a surface name plus an optional instance id to a pipe name.
    /// Only "debug" exists today; a product surface would get its own base name here.</summary>
    public static string Resolve(string surface, string? instanceId) =>
        surface.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? Debug(instanceId)
            : throw new ArgumentException($"Unknown MCP surface '{surface}'.", nameof(surface));

    private static string Compose(string baseName, string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) || instanceId.Equals("release", StringComparison.OrdinalIgnoreCase)
            ? baseName
            : $"{baseName}.{instanceId.Trim()}";

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
}
