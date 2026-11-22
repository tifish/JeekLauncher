## Rules

- After finishing a feature or fixing a bug:
  - Add any interface it needs for testing to the debug MCP interface.
  - Automatically build and launch the program **as Debug** (e.g. `Run.cmd`, `Build.cmd`, or `dotnet build` without `-c Release`). The debug MCP only listens in Debug builds.
    - Do **not** use `Publish.cmd` or `-c Release` for this loop: `Publish.cmd` builds a Release package into `publish\` (strips PDBs, skips the Debug MCP adapter). Use it for packaging, not for feature testing.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave instances from other worktrees running.
  - Use the current worktree's debug MCP (`JeekLauncherDebugMcp.cmd` at the repo root) to test the feature or bug; if anything is wrong, fix it and test again until it passes.
- When reading code, logs, and the debug MCP are not enough to locate a problem, use a debugger:
  - Use `netcoredbg` on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin (`Get-Content cmds.txt | netcoredbg --interpreter=cli --attach <pid>`), and drive the program to the breakpoint through the debug MCP.
  - Use `dotnet-dump` to analyze hangs and crashes (`dotnet-dump collect -p <pid>`, then `dotnet-dump analyze <dump> -c "clrstack -all" -c threads -c exit`).
  - Only attach to the current worktree's process, run the session with a timeout so an unhit breakpoint cannot block forever, and always `detach` / `quit` when done.
  - Afterwards, if a log line or an MCP tool would have found it, add one — that is what keeps the next bug out of the debugger.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- `JeekTools.NET` is a submodule: commit and push it before the parent commit that moves its pointer.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP

Agents talk to a running instance over a Windows named pipe, never a TCP port — nothing to allocate, nothing to collide over, and access control is the pipe's own ACL rather than a URL token.

- **Stable adapter path.** Agents launch this worktree's root `JeekLauncherDebugMcp.cmd`, which runs the fixed per-user `%LocalAppData%\JeekLauncher\Mcp\JeekLauncherMcp.exe`. Never launch `bin\JeekLauncherMcp.exe` directly: that file is build output which the app copies to the fixed path on startup, and an agent holding it open makes the next build fail. The install deliberately happens at app startup, not during the build.
- **Debug routing.** `JeekLauncherDebugMcp.cmd` passes `--surface debug --app <this worktree\bin\JeekLauncher.exe>`. The adapter derives the worktree instance id from that app directory (or reads the HKCU registration), so parallel Debug worktrees never answer for each other. It reconnects on its own when the app restarts, so an app rebuild does not end the agent session.
- **Debug builds only.** The host is compiled into every configuration but only listens under `#if DEBUG`. If tools report the app as unreachable, check that `bin\JeekLauncher.exe` is a Debug build and is running.
- **Register a tool in two places.** The handler goes in `DebugMcpServer` (`AddTool`), the schema in `DebugMcpContract` (`BuildToolList`). A tool missing from the contract is invisible to clients even though its handler works.
- **The launcher window is hidden.** It lives in the tray and is summoned by a global hotkey an agent cannot press. Use `show_launcher` / `hide_launcher` to drive it, and `search_probe` to check ranking with no UI at all.
- Tool work that touches UI state runs on the UI thread through the host's invoker (`OnUiAsync`).
- A **product** MCP surface (the app's features exposed to an end user's agent) is not implemented. If one is added it must be a separate endpoint with its own pipe name and its own tool registry — the debug `invoke` tool can call anything in the process, so the two must never be merged.

## Layout

- `JeekLauncher/` — the Avalonia app. `Services/` holds the non-UI logic (scanning, search, config, updates, MCP).
- `Tools/JeekLauncherMcp/` — the stdio↔pipe MCP adapter, published beside the app. It links `McpPipeNames.cs` and `McpAdapterRegistry.cs` from the app so both ends derive the same pipe name.
- `JeekTools.NET/` — shared library submodule. Use its components rather than reimplementing: `SettingsStorage` / `JsonSettingsFile` / `SharedDataFile` (config), `AutoUpdater` + `GitHubMirrors` (updates), `LogManager` (logging), `McpHost` / `ObjectGraph` (MCP).
- `bin/` — the run/publish directory. Version-controlled runtime files (`Data/`, `*.ps1`, `*.cmd`) live here directly; build output does not.
