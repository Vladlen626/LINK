using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Codex CLI through <c>codex exec --json</c>. Analyze and a plan run in the <c>read-only</c> sandbox, Do in
/// <c>workspace-write</c>; <c>danger-full-access</c> is never used. Codex has no plan mode, so a plan is asked for in
/// words and the sandbox keeps it from changing anything.
/// </summary>
public sealed class CodexProvider(Func<AgentSettings> settings, Action<string> log) : CliAgentProvider(settings, log)
{
    public override AgentProviderKind Kind => AgentProviderKind.Codex;
    public override string Name => "Codex";
    protected override IReadOnlyList<string> SignInCheck => ["login", "status"];

    // Codex reads .agents/skills from its folder up to the repository's root.
    protected override bool FindsSkills => true;

    public override IReadOnlyList<string> Arguments(AgentTask task)
    {
        var sandbox = task.Access == AgentAccess.Do && !task.PlanOnly ? "workspace-write" : "read-only";
        // A project folder need not be a git repository (a Blender project rarely is).
        var arguments = new List<string> { "exec" };
        if (task.ResumeSessionId is { } session)
        {
            // resume takes no --sandbox or --cd: the sandbox is set through config (an unquoted value is read as a
            // string), the folder is the working one.
            arguments.AddRange(["resume", session, "--json", "--skip-git-repo-check", "-c", $"sandbox_mode={sandbox}"]);
        }
        else
        {
            arguments.AddRange(["--json", "--skip-git-repo-check", "--sandbox", sandbox, "--cd", task.Workspace]);
        }

        foreach (var image in task.Images) arguments.AddRange(["--image", image]);
        // The task comes on stdin.
        arguments.Add("-");
        return arguments;
    }

    public override string Input(AgentTask task) =>
        task.PlanOnly
            ? "Make a plan only: read what you need, change nothing, and answer with the steps you would take.\n\n" + task.Prompt
            : task.Prompt;

    public override AgentEventParser CreateParser(AgentTask task) => new CodexEventParser(resumed: task.ResumeSessionId is not null);

    protected override bool IsSignedIn(string answer) =>
        answer.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Codex's JSONL: <c>thread.started</c>, <c>item.*</c> (messages, commands, file changes, MCP calls), <c>turn.*</c>.
/// The usage of <c>turn.completed</c> is the thread's total so far, so a resumed run reports its session's tokens.
/// </summary>
public sealed partial class CodexEventParser : AgentEventParser
{
    // Seen on a real resume: the note asked after a 33-minute run reported that whole run's tokens.
    public CodexEventParser(bool resumed = false) => TokensOfSession = resumed;

    protected override void Read(JsonElement root, List<AgentEvent> events)
    {
        switch (Text(root, "type"))
        {
            case "thread.started":
                SessionId = Text(root, "thread_id");
                events.Add(new AgentEvent(AgentEventKind.Status, "Session started"));
                break;
            case "item.started" when root.TryGetProperty("item", out var item) && Text(item, "type") == "command_execution":
                var command = Unwrap(Text(item, "command"));
                Called("command", command, command, IsSearchCommand(command), events);
                break;
            case "item.completed" when root.TryGetProperty("item", out var item):
                ReadItem(item, events);
                break;
            case "turn.completed":
                Turns++;
                // A turn that completes got past whatever Codex reported on the way, such as a reconnect.
                Failed = false;
                // Input counts the cached part too, as the API does.
                if (root.TryGetProperty("usage", out var usage))
                {
                    Spent(Number(usage, "input_tokens"), Number(usage, "cached_input_tokens"), Number(usage, "output_tokens"), null);
                }

                events.Add(new AgentEvent(AgentEventKind.Completed, Summary));
                break;
            case "turn.failed":
                Failed = true;
                events.Add(new AgentEvent(AgentEventKind.Error, Message(root.TryGetProperty("error", out var error) ? Text(error, "message") : string.Empty)));
                break;
            case "error":
                // Codex reports passing trouble as errors too ("Reconnecting... 2/5") and goes on: the end of the turn
                // decides, a failed turn tells why.
                Failed = true;
                events.Add(new AgentEvent(AgentEventKind.Status, Short(Message(Text(root, "message")))));
                break;
        }
    }

    private void ReadItem(JsonElement item, List<AgentEvent> events)
    {
        var status = Text(item, "status");
        var failed = status is "declined" or "failed";
        switch (Text(item, "type"))
        {
            case "agent_message" when Text(item, "text").Trim() is { Length: > 0 } text:
                Summary = text;
                events.Add(new AgentEvent(AgentEventKind.Output, text));
                break;
            case "command_execution":
                var output = Text(item, "aggregated_output");
                if (failed || Number(item, "exit_code") != 0) ToolFailed("command", output, events);
                if (failed && IsSandboxRefusal(output)) Denied("workspace-write", events, "The read-only sandbox refused a change");
                break;
            case "file_change" when item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array:
                var paths = changes.EnumerateArray().Select(change => Text(change, "path")).Where(path => path.Length > 0).ToArray();
                // Codex has no edit tool of its own: its patch is the call.
                Called("file_change", "Edit: " + string.Join(", ", paths), string.Join('\n', paths), false, events);
                if (failed)
                {
                    ToolFailed("file_change", "The patch was not applied.", events);
                    Denied("workspace-write", events, "The sandbox refused a file change");
                    break;
                }

                foreach (var path in paths) Changed(path, events);
                break;
            case "mcp_tool_call":
                var tool = $"{Text(item, "server")}: {Text(item, "tool")}";
                Called("mcp", tool, tool + (item.TryGetProperty("arguments", out var arguments) ? " " + arguments.GetRawText() : string.Empty), false, events);
                if (failed) ToolFailed("mcp", item.TryGetProperty("error", out var error) ? Text(error, "message") : string.Empty, events);
                break;
            case "web_search":
                Called("search", $"Search: {Text(item, "query")}", Text(item, "query"), false, events);
                break;
            case "error" when Text(item, "message") is { Length: > 0 } warning:
                // Codex reports warnings as error items and goes on; a real failure ends the turn.
                events.Add(new AgentEvent(AgentEventKind.Status, Short(warning)));
                break;
        }
    }

    /// <summary>Codex tells a write the read-only sandbox blocked only in its log: "patch rejected: writing is blocked…".</summary>
    public override IReadOnlyList<AgentEvent> ReadError(string line)
    {
        if (!line.Contains("blocked by read-only sandbox", StringComparison.OrdinalIgnoreCase) || Denials.Count > 0) return [];
        var events = new List<AgentEvent>();
        Denied("workspace-write", events, "The read-only sandbox refused a change");
        return events;
    }

    private static bool IsSandboxRefusal(string output) =>
        output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("read-only", StringComparison.OrdinalIgnoreCase);

    /// <summary>Codex runs each command through PowerShell; the row shows the command itself.</summary>
    private static string Unwrap(string command) =>
        ShellCommand().Match(command) is { Success: true } match ? match.Groups[1].Value.Replace("\\\"", "\"") : command;

    /// <summary>A failure often carries the API's JSON; its inner message is what the user can act on.</summary>
    private static string Message(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && Text(error, "message") is { Length: > 0 } inner) return inner;
            if (Text(root, "message") is { Length: > 0 } message) return message;
        }
        catch (JsonException)
        {
            // Plain text.
        }

        return text.Length > 0 ? text : "Codex failed.";
    }

    [GeneratedRegex("""powershell\.exe"?\s+-Command\s+"(.*)"\s*$""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShellCommand();
}
