namespace JeekLauncher.Services;

/// <summary>
/// Installs the fixed per-user MCP adapter and registers this build behind it.
/// Done at startup rather than during the build: an agent session holding the fixed
/// executable open must never be able to fail a rebuild of <c>bin\</c>.
/// </summary>
public static class McpAdapterRegistration
{
    public static McpRegisteredInstance RegisterCurrentInstance()
    {
        var sourceAdapter = Path.Combine(AppContext.BaseDirectory, "JeekLauncherMcp.exe");

        // Always refresh the fixed adapter when the copy published next to this app differs,
        // so routing fixes reach agents. A locked destination keeps the previous file, which
        // is safe for a protocol-agnostic forwarder.
        if (!McpAdapterRegistry.EnsureAdapterInstalled(sourceAdapter, allowUpdate: true))
            throw new IOException("Could not install the fixed JeekLauncher MCP adapter.");

        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath))
            throw new InvalidOperationException("The current JeekLauncher executable path is unavailable.");

        var registration = new McpRegisteredInstance(
            DebugInstanceContext.InstanceId,
            appPath,
            DebugInstanceContext.IsDebugBuild ? DebugInstanceContext.DebugMcpPipeName : "",
            DebugInstanceContext.IsDebugBuild,
            DebugInstanceContext.WorkspaceRoot);
        McpAdapterRegistry.WriteInstance(registration);
        return registration;
    }
}
