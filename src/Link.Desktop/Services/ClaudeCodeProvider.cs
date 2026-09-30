using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Claude Code in print mode with stream-json events. Analyze runs in <c>dontAsk</c>: reads go, anything else not
/// allowed by the project's rules is refused and listed in the result. Do runs in <c>acceptEdits</c>; a plan runs in
/// <c>plan</c>. <c>bypassPermissions</c> is never used.
/// </summary>
public sealed class ClaudeCodeProvider(Func<AgentSettings> settings, Action<string> log) : CliAgentProvider(settings, log)
{
    public override AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
    public override string Name => "Claude Code";
    protected override IReadOnlyList<string> SignInCheck => ["auth", "status"];

    public override IReadOnlyList<string> Arguments(AgentTask task)
    {
        var mode = task.PlanOnly ? "plan" : task.Access == AgentAccess.Do ? "acceptEdits" : "dontAsk";
        var arguments = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose",
            "--permission-mode", mode,
            "--max-turns", task.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (task.ResumeSessionId is { } session) arguments.AddRange(["--resume", session]);
        // Images live in Link's data, outside the workspace: their folders are opened for reading.
        foreach (var folder in task.Images.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--add-dir", folder]);
        }

        // Last: the option takes every argument after it.
        if (task.AllowedTools.Count > 0) arguments.AddRange(task.AllowedTools.Prepend("--allowedTools"));
        return arguments;
    }

    public override string Input(AgentTask task) =>
        task.Images.Count == 0
            ? task.Prompt
            : task.Prompt + "\n\nImages attached to this task (open them with Read):\n" + string.Join('\n', task.Images);

    public override AgentEventParser CreateParser() => new ClaudeEventParser();

    protected override bool IsSignedIn(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Claude Code's stream-json: <c>system/init</c>, <c>assistant</c> text and tool calls, tool results, <c>result</c>.</summary>
public sealed class ClaudeEventParser : AgentEventParser
{
    private static readonly HashSet<string> EditTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];
    private static readonly HashSet<string> SearchTools = ["Glob", "Grep", "Read", "LS"];
    private static readonly HashSet<string> ShellTools = ["Bash", "PowerShell"];

    // Each call's tool until its result comes back; an edit counts as a change only when that result is not an error.
    private readonly Dictionary<string, string> _pendingTools = [];
    private readonly Dictionary<string, string> _pendingEdits = [];

    protected override void Read(JsonElement root, List<AgentEvent> events)
    {
        switch (Text(root, "type"))
        {
            case "system" when Text(root, "subtype") == "init":
                SessionId = Text(root, "session_id");
                events.Add(new AgentEvent(AgentEventKind.Status, Describe(root)));
                break;
            case "assistant":
                foreach (var block in Content(root)) ReadAssistant(block, events);
                break;
            case "user":
                foreach (var block in Content(root)) ReadToolResult(block, events);
                break;
            case "result":
                ReadResult(root, events);
                break;
        }
    }

    private void ReadAssistant(JsonElement block, List<AgentEvent> events)
    {
        switch (Text(block, "type"))
        {
            case "text" when Text(block, "text").Trim() is { Length: > 0 } text:
                events.Add(new AgentEvent(AgentEventKind.Output, text));
                break;
            case "tool_use":
                var name = Text(block, "name");
                var input = block.TryGetProperty("input", out var value) ? value : default;
                var command = Text(input, "command");
                var target = Text(input, "file_path") is { Length: > 0 } file ? file
                    : command.Length > 0 ? command
                    : Text(input, "pattern") is { Length: > 0 } pattern ? pattern
                    : Text(input, "url");
                var search = SearchTools.Contains(name) || (ShellTools.Contains(name) && IsSearchCommand(command));
                Called(name, target.Length > 0 ? $"{name}: {target}" : name, search, events);
                var id = Text(block, "id");
                _pendingTools[id] = name;
                if (EditTools.Contains(name) && Text(input, "file_path") is { Length: > 0 } edited) _pendingEdits[id] = edited;
                break;
        }
    }

    private void ReadToolResult(JsonElement block, List<AgentEvent> events)
    {
        if (Text(block, "type") != "tool_result") return;
        var id = Text(block, "tool_use_id");
        var failed = block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
        if (_pendingTools.Remove(id, out var tool) && failed) ToolFailed(tool, ResultText(block), events);
        if (_pendingEdits.Remove(id, out var file) && !failed) Changed(file, events);
    }

    /// <summary>A tool result's text: a string, or text blocks.</summary>
    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        return content.ValueKind == JsonValueKind.Array
            ? string.Join('\n', content.EnumerateArray().Select(part => Text(part, "text")).Where(text => text.Length > 0))
            : string.Empty;
    }

    private void ReadResult(JsonElement root, List<AgentEvent> events)
    {
        SessionId = Text(root, "session_id") is { Length: > 0 } session ? session : SessionId;
        Turns = (int)Number(root, "num_turns");
        if (root.TryGetProperty("usage", out var usage))
        {
            // The API counts fresh, cache-written and cache-read input apart; together they are all the model read.
            var cached = Number(usage, "cache_read_input_tokens");
            Spent(
                Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + cached,
                cached,
                Number(usage, "output_tokens"),
                root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var usd) ? usd : null);
        }

        if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
        {
            foreach (var denial in denials.EnumerateArray())
            {
                var tool = Text(denial, "tool_name");
                var input = denial.TryGetProperty("tool_input", out var value) ? value : default;
                // A command is allowed by its own rule, so that "allow" opens only what was asked for.
                var rule = tool == "Bash" && Text(input, "command") is { Length: > 0 } command ? $"Bash({command})" : tool;
                Denied(rule, events, $"Not allowed: {Short(rule)}");
            }
        }

        var failed = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
        var text = Text(root, "result").Trim();
        Summary = text;
        if (failed || Text(root, "subtype") is { Length: > 0 } subtype && subtype != "success")
        {
            Failed = true;
            var reason = text.Length > 0 ? text : Text(root, "subtype") == "error_max_turns" ? "Stopped at the turn limit." : "Claude Code failed.";
            events.Add(new AgentEvent(AgentEventKind.Error, reason));
            return;
        }

        events.Add(new AgentEvent(AgentEventKind.Completed, text, Turns > 0 ? $"{Turns} turns" : null));
    }

    private static IEnumerable<JsonElement> Content(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    /// <summary>The model and the MCP servers the session started with, as the provider reports them.</summary>
    private static string Describe(JsonElement init)
    {
        var model = Text(init, "model");
        var servers = init.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(server => $"{Text(server, "name")} ({Text(server, "status")})").ToArray()
            : [];
        var text = model.Length > 0 ? $"Model {model}" : "Session started";
        return servers.Length > 0 ? $"{text}; MCP: {string.Join(", ", servers)}" : text;
    }
}
