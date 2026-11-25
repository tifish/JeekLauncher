using JeekTools;

namespace JeekLauncher.Services;

public sealed record DebugInstanceInfo(
    bool IsDebugBuild,
    string InstanceId,
    string InstanceLabel,
    string WorkspaceRoot,
    int ProcessId,
    string McpPipeName,
    string ConfigRoot);

/// <summary>Stable identity and per-process runtime paths for this build, used to keep
/// parallel Debug worktrees from answering for each other on the MCP pipe.</summary>
public static class DebugInstanceContext
{
    public static bool IsDebugBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Repository root when running from <c>bin\</c> in Debug, otherwise the install folder.</summary>
    public static string WorkspaceRoot { get; } = IsDebugBuild
        ? Path.GetFullPath(Path.Combine(AppPaths.ProgramDir, ".."))
        : AppPaths.ProgramDir;

    public static string InstanceId { get; } = IsDebugBuild
        ? McpPipeNames.InstanceId(AppPaths.ProgramDir)
        : "release";

    public static string BranchLabel { get; }
    public static string CommitLabel { get; }
    public static string WorktreeLabel { get; }
    public static string InstanceLabel { get; }

    /// <summary>Named pipe for the Debug MCP surface. Release is single-instance, so it
    /// uses the bare name.</summary>
    public static string DebugMcpPipeName { get; } =
        McpPipeNames.Debug(IsDebugBuild ? InstanceId : null);

    // Release stays a single instance across the machine. Debug scopes the lock to the
    // worktree, so a second worktree can run its own build (and its own MCP pipe) instead
    // of silently handing focus to whichever instance started first.

    public static string SingleInstanceMutexName { get; } = IsDebugBuild
        ? $"JeekLauncher.SingleInstance.{InstanceId}"
        : "JeekLauncher.SingleInstance.{B3F9A2C1-7E54-4D8B-9A1F-6C2E0D5B8E11}";

    public static string ShowEventName { get; } = IsDebugBuild
        ? $"JeekLauncher.Show.{InstanceId}"
        : "JeekLauncher.Show.{B3F9A2C1-7E54-4D8B-9A1F-6C2E0D5B8E11}";

    /// <summary>Human-readable discovery file inside the runtime folder so the app root keeps only
    /// the executable. Not needed to connect (the pipe name is derived), but handy when troubleshooting.</summary>
    public static string DiscoveryPath => Path.Combine(AppContext.BaseDirectory, "debug-mcp.json");

    private static string _mcpPipeName = "";

    static DebugInstanceContext()
    {
        if (IsDebugBuild)
        {
            var git = ReadGitIdentity(WorkspaceRoot);
            BranchLabel = git.Branch;
            CommitLabel = git.Commit;
            WorktreeLabel = git.Worktree;
            InstanceLabel = $"{BranchLabel}@{CommitLabel} / {WorktreeLabel}";
        }
        else
        {
            BranchLabel = "";
            CommitLabel = "";
            WorktreeLabel = "";
            InstanceLabel = "Release";
        }
    }

    public static DebugInstanceInfo Info => new(
        IsDebugBuild,
        InstanceId,
        InstanceLabel,
        WorkspaceRoot,
        Environment.ProcessId,
        _mcpPipeName,
        Storage.RoamableConfigDir);

    internal static void SetMcpPipeName(string value) => _mcpPipeName = value ?? "";

    /// <summary>
    /// Reads branch / commit / worktree straight from <c>.git</c>, so a Debug instance can
    /// label itself without shelling out to git on every start. Worktrees store a
    /// <c>gitdir:</c> marker file instead of a directory, and their refs live in the common
    /// dir, so both layouts are handled.
    /// </summary>
    private static (string Branch, string Commit, string Worktree) ReadGitIdentity(string root)
    {
        try
        {
            var dotGit = Path.Combine(root, ".git");
            string gitDir;
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
            }
            else
            {
                var marker = File.ReadAllText(dotGit).Trim();
                if (!marker.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid .git worktree marker.");
                var value = marker[7..].Trim();
                gitDir = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
            }

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            var worktree = Directory.Exists(dotGit)
                ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(gitDir.TrimEnd(Path.DirectorySeparatorChar));

            if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                return ($"detached-{ShortCommit(head)}", ShortCommit(head), worktree);

            var reference = head[4..].Trim();
            var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? reference["refs/heads/".Length..]
                : reference;

            var refPath = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
            var commonDir = gitDir;
            var commonDirPath = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirPath))
            {
                var common = File.ReadAllText(commonDirPath).Trim();
                commonDir = Path.GetFullPath(Path.Combine(gitDir, common));
                if (!File.Exists(refPath))
                    refPath = Path.Combine(commonDir, reference.Replace('/', Path.DirectorySeparatorChar));
            }

            string commit;
            if (File.Exists(refPath))
            {
                commit = File.ReadAllText(refPath).Trim();
            }
            else
            {
                commit = ReadPackedRef(commonDir, reference) ?? "unknown";
            }
            return (branch, ShortCommit(commit), worktree);
        }
        catch
        {
            return ("unknown", "unknown", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
        }
    }

    private static string? ReadPackedRef(string commonDir, string reference)
    {
        var packedPath = Path.Combine(commonDir, "packed-refs");
        if (!File.Exists(packedPath))
            return null;

        foreach (var line in File.ReadLines(packedPath))
        {
            if (line.Length == 0 || line[0] is '#' or '^')
                continue;
            int space = line.IndexOf(' ');
            if (space > 0 && string.Equals(line[(space + 1)..].Trim(), reference, StringComparison.Ordinal))
                return line[..space].Trim();
        }
        return null;
    }

    private static string ShortCommit(string value) => value.Length >= 7 ? value[..7] : value;
}
