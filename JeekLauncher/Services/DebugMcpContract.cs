using System.Text.Json.Nodes;

namespace JeekLauncher.Services;

/// <summary>Human-readable discovery file written next to the executable. Connecting does
/// not need it — the pipe name is derived from the app directory — but it makes manual
/// troubleshooting possible.</summary>
public sealed class DebugMcpDiscovery
{
    /// <summary>Named pipe accepting Debug MCP sessions.</summary>
    public string PipeName { get; set; } = "";

    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string InstanceLabel { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string ConfigRoot { get; set; } = "";
}

/// <summary>
/// Schemas advertised over <c>tools/list</c>. Every tool has to appear both here and as an
/// <c>AddTool</c> handler in <see cref="DebugMcpServer"/> — a tool missing from this contract
/// is invisible to clients even though its handler works.
/// </summary>
public static class DebugMcpContract
{
    public const string PathHelp =
        "Paths start from a root: App (the Application), Desktop (the desktop lifetime), " +
        "Launcher (the launcher window), or LauncherVm (its LauncherViewModel). Segments: " +
        "'.Member' reads a property or field (non-public included), '[0]' indexes a list, " +
        "'[\"key\"]' indexes a dictionary, and '#Name' finds a named control in the visual " +
        "tree below the current object. " +
        "Examples: LauncherVm.Results[0].Name, Launcher.#SearchBox.Text";

    public static JsonArray BuildToolList() => new(
        Tool("describe",
            "Overview of the running launcher: instance, windows, config paths, roots, path syntax, and log file. Start here.",
            new()),
        Tool("get_value", "Read a value from the app's object graph. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path to read."),
                ["depth"] = Prop("integer", "Nested expansion depth, 0-5 (default 1)."),
            },
            ["path"]),
        Tool("set_value", "Write a property, field, or list element on the UI thread. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path to write."),
                ["value"] = new JsonObject { ["description"] = "New JSON value; {$path: ...} passes a live object." },
            },
            ["path", "value"]),
        Tool("invoke", "Execute an ICommand or call a method on the UI thread. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path ending with a command or method."),
                ["args"] = new JsonObject { ["type"] = "array", ["description"] = "JSON arguments." },
                ["depth"] = Prop("integer", "Return expansion depth, 0-5 (default 1)."),
            },
            ["path"]),
        Tool("list_members", "List properties, fields, and methods at a path. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path to inspect.") },
            ["path"]),
        Tool("read_logs", "Read the current app log tail.",
            new()
            {
                ["lines"] = Prop("integer", "Lines, 1-2000 (default 200)."),
                ["filter"] = Prop("string", "Case-insensitive filter."),
            }),
        Tool("visual_tree", "Dump the visual tree below a visual.",
            new()
            {
                ["path"] = Prop("string", "Starting Visual path (default Launcher)."),
                ["max_depth"] = Prop("integer", "Maximum depth (default 12)."),
            }),
        Tool("screenshot",
            "Render the launcher window to PNG, showing it first if it is hidden. Pass 'query' to capture a specific search state: the window hides itself whenever it loses focus, and showing it again clears the box, so a query set by an earlier show_launcher call is usually gone by now.",
            new() { ["query"] = Prop("string", "Text to put in the search box before capturing.") }),
        Tool("show_launcher",
            "Show and focus the launcher window, optionally typing a query into it. The global hotkey cannot be pressed from an agent, so this is how you open the launcher.",
            new() { ["query"] = Prop("string", "Text to put in the search box after showing.") }),
        Tool("hide_launcher", "Hide the launcher window, as pressing Escape would.", new()),
        Tool("search_probe",
            "Run a query through the real search engine against the live catalog and report the ranked results with their match tier, launch count, and quick-word binding. This is the core feature — use it to verify initials/pinyin matching without driving the UI.",
            new()
            {
                ["query"] = Prop("string", "Search text, e.g. 'vsc' or 'wangyi'."),
                ["limit"] = Prop("integer", "Maximum results to report, 1-40 (default 10)."),
            },
            ["query"]),
        Tool("entries_snapshot",
            "Summarize the scanned catalog: entry count, scan directories, and a sample of entries with their initials and pinyin, optionally filtered by name.",
            new()
            {
                ["filter"] = Prop("string", "Case-insensitive substring of the entry name."),
                ["limit"] = Prop("integer", "Maximum entries to list, 1-100 (default 20)."),
            }),
        Tool("rescan", "Rescan the shortcut directories and wait for the new catalog, as the tray 'Rescan' item does.", new()),
        Tool("config_state",
            "Report the resolved config layout: effective storage location, whether portable is forced, every config/update path, and the current settings values.",
            new()),
        Tool("hotkey_state",
            "Report the configured global hotkey, how it parsed, and whether registration succeeded (another app may hold it).",
            new()),
        Tool("quick_words",
            "List the remembered 'quick word -> app' bindings and whether each target still exists in the catalog.",
            new() { ["filter"] = Prop("string", "Case-insensitive substring of the quick word.") }));

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(JsonNode (r) => r).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };
}
