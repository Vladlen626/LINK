using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Claude Code in print mode with stream-json events. A run is in <c>acceptEdits</c> (a command not allowed by the
/// project's rules is refused and listed in the result), or in <c>auto</c> on Auto, where Claude Code's own check stops
/// risky actions. A No-stop task runs in <c>bypassPermissions</c> with its deny list. No run is read only: <c>plan</c>
/// and <c>dontAsk</c> are never used.
/// </summary>
public sealed partial class ClaudeCodeProvider(Func<AgentSettings> settings, Action<string> log, AgentConsole? console = null)
    : CliAgentProvider(settings, log, console)
{
    public override AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
    public override string Name => "Claude Code";
    public override AgentCapabilities Capabilities => new(true, true, true, "Anthropic");

    // Claude Code's --effort levels; max is the highest. Ultracode is no effort but a setting that runs subagent
    // workflows, and Link runs agents without subagents.
    public override IReadOnlyList<AgentChoice> Efforts =>
        [new("low", "Low"), new("medium", "Medium"), new("high", "High"), new("xhigh", "Extra high"), new("max", "Max — the highest")];

    // Families first: Claude Code takes its newest model of the family, so a new release needs no new Link; the run
    // shows the model it got, and the task tells when that changes (see Families). Full names pin a version; Haiku 5.5
    // only so, while Claude Code 2.1.286 still takes Haiku 4.5 for haiku.
    public override IReadOnlyList<AgentChoice> Models =>
    [
        new("opus", "Opus · latest"),
        new("sonnet", "Sonnet · latest"),
        new("haiku", "Haiku · latest"),
        new("fable", "Fable · latest"),
        new("claude-opus-5-5", "Opus 5.5"),
        new("claude-fable-5-1", "Fable 5.1"),
        new("claude-sonnet-5-5", "Sonnet 5.5"),
        new("claude-haiku-5-5", "Haiku 5.5")
    ];

    /// <summary>The families Claude Code resolves to its newest model, by the alias it takes.</summary>
    public static readonly IReadOnlyList<string> Families = ["opus", "sonnet", "haiku", "fable"];

    protected override IReadOnlyList<string> SignInCheck => ["auth", "status"];

    public override IReadOnlyList<string> Arguments(AgentTask task)
    {
        // A plan first is asked for in words (CliAgentProvider.PlanRequest), not by plan mode: the agent keeps all its tools.
        var arguments = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose",
            "--permission-mode", task.NoStop ? "bypassPermissions" : task.AutoApprove ? "auto" : "acceptEdits"
        };
        if (task.MaxTurns is { } turns) arguments.AddRange(["--max-turns", turns.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (task.Model is { } model) arguments.AddRange(["--model", model]);
        if (task.Effort is { } effort) arguments.AddRange(["--effort", effort]);
        // No AskUserQuestion: a run has no dialog to answer it, so a question ends the answer as a link-ask block, which the
        // Task tab shows as a choice. Subagents are the agent's own choice.
        // No-stop's deny list holds in bypassPermissions too: deny rules win over every mode.
        arguments.AddRange(["--disallowedTools", "AskUserQuestion", .. task.Deny]);
        // MCP servers come only from the workspace's .mcp.json, as for Codex: otherwise the user's claude.ai
        // connectors ride along too, such as a docs app in a game task. Link's own answers get none.
        arguments.Add("--strict-mcp-config");
        if (!task.WithoutMcp && McpConfigIn(task.Workspace) is { } servers) arguments.AddRange(["--mcp-config", servers]);
        if (task.ResumeSessionId is { } session) arguments.AddRange(["--resume", session]);
        // Images live in Link's data and attached files anywhere, outside the workspace: their folders are opened for reading.
        foreach (var folder in task.Images.Concat(task.Files).Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--add-dir", folder]);
        }

        // So do the skills of the workspace's tool packs, which every project shares.
        foreach (var pack in ToolPacks.For(task.Workspace))
        {
            if (Path.Combine(pack.Folder, "skills") is var skills && Directory.Exists(skills)) arguments.AddRange(["--add-dir", skills]);
        }

        // Last: the option takes every argument after it.
        if (task.AllowedTools.Count > 0) arguments.AddRange(task.AllowedTools.Prepend("--allowedTools"));
        return arguments;
    }

    public override string Input(AgentTask task) =>
        task.Images.Count == 0
            ? task.Prompt
            : task.Prompt + "\n\nImages attached to this task (open them with Read):\n" + string.Join('\n', task.Images);

    /// <summary>
    /// A set budget is Claude Code's auto-compact window: it compacts near it, and never above the model's own window.
    /// On Auto Link sets nothing: Claude Code's own window is tuned for the model, and a wider one re-sends more.
    /// </summary>
    public override IReadOnlyDictionary<string, string> EnvironmentVariables(AgentTask task)
    {
        var variables = new Dictionary<string, string>(AccountVariables(Settings));
        if (task.ContextBudget is { } budget and > 0)
        {
            variables["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return variables;
    }

    protected override IReadOnlyDictionary<string, string> AccountVariables(AgentSettings setup) => ClaudeAccounts.Variables(setup.ClaudeAccount);

    public override AgentEventParser CreateParser(AgentTask task) => new ClaudeEventParser(plan: task.Plan);

    /// <summary>
    /// Claude Code's own <c>/usage</c>, which print mode runs without the model: its lines "Current session: 23% used ·
    /// resets Oct 4, 3pm (Europe/Moscow)" give the windows. Run in the temp folder, so that no project is touched.
    /// </summary>
    public override async Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken)
    {
        if (FindExecutable() is not { } executable) return [];
        var output = new System.Text.StringBuilder();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(UsageWait);
        await AgentProcess.RunAsync(
            new AgentCommand(executable, ["-p", "/usage"], Path.GetTempPath(), string.Empty) { EnvironmentVariables = AccountVariables(Settings) },
            line => output.AppendLine(line), null, wait.Token);
        return UsageLimits(output.ToString(), DateTimeOffset.Now);
    }

    /// <summary>The windows of <c>/usage</c>'s text: the session as "5 h", the week, and a week of one model.</summary>
    internal static IReadOnlyList<AgentLimit> UsageLimits(string text, DateTimeOffset now) => UsageLine().Matches(text).Select(match =>
    {
        var scope = match.Groups["scope"].Value;
        var window = match.Groups["session"].Success ? "5 h"
            : scope.Equals("all models", StringComparison.OrdinalIgnoreCase) ? "week"
            : $"week, {scope.Replace(" only", string.Empty, StringComparison.OrdinalIgnoreCase)}";
        var used = double.Parse(match.Groups["used"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new AgentLimit(AgentProviderKind.ClaudeCode, window, used, ResetTime(match.Groups["when"].Value, now), used >= 100, now);
    }).ToArray();

    /// <summary>
    /// A reset as /usage writes it, in this computer's time: "Oct 4, 3pm", "Oct 4, 3:30pm", "Jan 2, 2027, 3pm"; null
    /// when it does not read.
    /// </summary>
    internal static DateTimeOffset? ResetTime(string text, DateTimeOffset now)
    {
        // en-US writes am and pm in lower case here; the parser takes them in upper.
        var words = System.Text.RegularExpressions.Regex.Replace(text.Trim(), "(am|pm)$", match => match.Value.ToUpperInvariant());
        string[] formats = ["MMM d, htt", "MMM d, h:mmtt", "MMM d, yyyy, htt", "MMM d, yyyy, h:mmtt"];
        if (!DateTime.TryParseExact(words, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            return null;
        }

        // Without a year it is this one; a date that has passed by more than a day is next year's.
        if (!words.Contains(local.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            local < now.LocalDateTime.AddDays(-1))
        {
            local = local.AddYears(1);
        }

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^\s*Current (?:(?<session>session)|week \((?<scope>[^)]+)\)):\s*(?<used>\d+(?:\.\d+)?)% used(?:\s*·\s*resets\s+(?<when>[^(\r\n]+?))?(?:\s*\([^)\r\n]*\))?\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex UsageLine();

    protected override bool IsSignedIn(string answer) =>
        StatusField(answer, "loggedIn") is { ValueKind: JsonValueKind.True };

    protected override string? AccountOf(string answer) =>
        StatusField(answer, "email") is { ValueKind: JsonValueKind.String } email && email.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>A field of <c>claude auth status</c>'s JSON; null when the answer is no JSON or lacks it.</summary>
    private static JsonElement? StatusField(string answer, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.TryGetProperty(name, out var field) ? field.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Claude Code's stream-json: <c>system/init</c>, <c>assistant</c> text and tool calls, tool results, <c>result</c>.
/// <paramref name="plan"/> is the plan of the session that goes on, which TaskUpdate names by number.
/// </summary>
public sealed class ClaudeEventParser(IReadOnlyList<AgentPlanItem>? plan = null) : AgentEventParser
{
    private static readonly HashSet<string> EditTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];
    private static readonly HashSet<string> SearchTools = ["Glob", "Grep", "Read", "LS"];
    private static readonly HashSet<string> ShellTools = ["Bash", "PowerShell"];

    // Each call's tool until its result comes back; an edit counts as a change only when that result is not an error.
    private readonly Dictionary<string, string> _pendingTools = [];
    private readonly Dictionary<string, string> _pendingEdits = [];

    // The agent's tasks as TaskCreate and TaskUpdate keep them, by number. A plan without numbers (TodoWrite's) is numbered
    // in order, as Claude Code does.
    private readonly List<AgentPlanItem> _tasks = (plan ?? []).Select((item, index) => item with { Number = item.Number ?? index + 1 }).ToList();

    // A TaskCreate's task until its result tells the number.
    private readonly Dictionary<string, AgentPlanItem> _creating = [];

    protected override void Read(JsonElement root, List<AgentEvent> events)
    {
        switch (Text(root, "type"))
        {
            case "system" when Text(root, "subtype") == "init":
                SessionId = Text(root, "session_id");
                if (Text(root, "model") is { Length: > 0 } model) Model = model;
                var (started, servers) = Describe(root);
                // The model is in the run's header: a start shows as a step only with the MCP servers it found.
                events.Add(new AgentEvent(AgentEventKind.Status, started, servers ? null : AgentEvent.SessionStart));
                break;
            // Claude Code compacted the session by itself, near the budget or at the model's window.
            case "system" when Text(root, "subtype") == "compact_boundary":
                var before = root.TryGetProperty("compact_metadata", out var compacted) ? Number(compacted, "pre_tokens") : 0;
                events.Add(new AgentEvent(AgentEventKind.Status,
                    before > 0 ? $"Claude Code compacted the context (was {AgentContext.Tokens(before)})" : "Claude Code compacted the context"));
                break;
            case "assistant":
                ReadContext(root, events);
                foreach (var block in Content(root)) ReadAssistant(block, events);
                break;
            case "user":
                foreach (var block in Content(root)) ReadToolResult(block, events);
                break;
            case "result":
                ReadResult(root, events);
                break;
            case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info) && Limits(info) is { Count: > 0 } limits:
                events.Add(new AgentEvent(AgentEventKind.Limit, "Usage reported") { Limits = limits });
                break;
        }
    }

    /// <summary>
    /// The subscription's windows as Claude Code reports them when they change: the window the status is about, and
    /// every window it lists with its share used and reset time.
    /// </summary>
    internal static IReadOnlyList<AgentLimit> Limits(JsonElement info)
    {
        var now = DateTimeOffset.Now;
        var rejected = Text(info, "status") == "rejected";
        var type = Text(info, "rateLimitType");
        var limits = new List<AgentLimit>();
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (var window in windows.EnumerateObject())
            {
                if (AgentLimits.WindowName(window.Name) is not { } name) continue;
                limits.Add(new AgentLimit(AgentProviderKind.ClaudeCode, name, AgentLimits.Percent(window.Value, "utilization"),
                    AgentLimits.UnixTime(window.Value, "resetsAt"), rejected && window.Name == type, now));
            }
        }

        if (limits.Count == 0 && AgentLimits.WindowName(type) is { } single)
        {
            limits.Add(new AgentLimit(AgentProviderKind.ClaudeCode, single, AgentLimits.Percent(info, "utilization"),
                AgentLimits.UnixTime(info, "resetsAt"), rejected, now));
        }

        return limits;
    }

    /// <summary>
    /// The context after this message: all the input it read, cached or not. Claude Code repeats a message's usage for
    /// each of its content blocks, so a size is told once, when it changes.
    /// </summary>
    private void ReadContext(JsonElement root, List<AgentEvent> events)
    {
        var usage = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("usage", out var found)
            ? found
            : default;
        var tokens = Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "cache_creation_input_tokens");
        if (tokens > 0 && tokens != ContextTokens) Context(tokens, null, events);
    }

    /// <summary>
    /// The window of the main model: the result lists the models of a run, and the one that read the most is the
    /// session's (a helper model reads little). Null when the result does not say.
    /// </summary>
    private static long? WindowOf(JsonElement result)
    {
        if (!result.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object) return null;
        long? window = null;
        var most = -1L;
        foreach (var model in models.EnumerateObject())
        {
            var size = Number(model.Value, "contextWindow");
            var read = Number(model.Value, "inputTokens") + Number(model.Value, "cacheReadInputTokens") + Number(model.Value, "cacheCreationInputTokens");
            if (size > 0 && read > most) (window, most) = (size, read);
        }

        return window;
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
                // The agent's own plan is no step of the work: the Task tab shows it as a list.
                if (name == "TodoWrite" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) &&
                    todos.ValueKind == JsonValueKind.Array)
                {
                    var list = todos.EnumerateArray()
                        .Select(todo => new AgentPlanItem(Text(todo, "content"), Text(todo, "status") == "completed", Text(todo, "status") == "in_progress",
                            Text(todo, "activeForm") is { Length: > 0 } doing ? doing : null))
                        .Where(item => item.Text.Length > 0)
                        .ToArray();
                    events.Add(PlanEvent(list));
                    break;
                }

                // Its tasks come one call at a time; a new one is numbered by the result, an old one is named by that number.
                if (name == "TaskCreate" && Text(input, "subject") is { Length: > 0 } subject)
                {
                    _creating[Text(block, "id")] = new AgentPlanItem(subject, false, false, Text(input, "activeForm") is { Length: > 0 } active ? active : null);
                    break;
                }

                if (name == "TaskUpdate" && input.ValueKind == JsonValueKind.Object)
                {
                    UpdateTask(input, events);
                    break;
                }

                // Reading the list changes nothing.
                if (name is "TaskList" or "TaskGet") break;

                var command = Text(input, "command");
                var target = Text(input, "file_path") is { Length: > 0 } file ? file
                    : command.Length > 0 ? command
                    : Text(input, "pattern") is { Length: > 0 } pattern ? pattern
                    : Text(input, "url");
                var search = SearchTools.Contains(name) || (ShellTools.Contains(name) && IsSearchCommand(command));
                // A command's description is the model's own words and may differ for the very same call.
                var arguments = input.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", input.EnumerateObject().Where(field => field.Name != "description").Select(field => $"{field.Name}={field.Value.GetRawText()}"))
                    : string.Empty;
                // An MCP tool is named mcp__<server>__<tool>.
                var mcp = name.StartsWith("mcp__", StringComparison.Ordinal) ? name[5..].Split("__", 2) : [];
                var row = mcp.Length == 2 ? McpCall(mcp[0], mcp[1], input) : target.Length > 0 ? $"{name}: {target}" : name;
                var work = mcp.Length == 2 || (ShellTools.Contains(name) && RunsCode(command));
                Called(name, row, arguments, search, events, work, image: name == "Read" && RunImages.IsImage(target) ? target : null);
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
        if (_creating.Remove(id, out var created))
        {
            if (!failed) AddTask(created, ResultText(block), events);
            return;
        }

        if (_pendingTools.Remove(id, out var tool) && failed) ToolFailed(tool, ResultText(block), events);
        if (_pendingEdits.Remove(id, out var file) && !failed) Changed(file, events);
    }

    /// <summary>
    /// A created task joins the plan under the number the result tells ("Task #3 created successfully"); one that tells
    /// none gets the next number, as Claude Code counts.
    /// </summary>
    private void AddTask(AgentPlanItem created, string result, List<AgentEvent> events)
    {
        var told = System.Text.RegularExpressions.Regex.Match(result, @"#(\d+)");
        var number = told.Success && int.TryParse(told.Groups[1].Value, out var read) ? read : (_tasks.Max(item => item.Number) ?? 0) + 1;
        var at = _tasks.FindIndex(item => item.Number == number);
        if (at >= 0) _tasks[at] = created with { Number = number };
        else _tasks.Add(created with { Number = number });
        events.Add(PlanEvent([.. _tasks]));
    }

    /// <summary>
    /// TaskUpdate changes a task named by its number: its status, and its words when it gives them; "deleted" takes it
    /// off the plan. A number the plan does not know is not guessed at.
    /// </summary>
    private void UpdateTask(JsonElement input, List<AgentEvent> events)
    {
        var named = input.TryGetProperty("taskId", out var id) && int.TryParse(id.ToString(), out var number) ? number : 0;
        var at = _tasks.FindIndex(item => item.Number == named);
        if (at < 0) return;
        var status = Text(input, "status");
        if (status == "deleted")
        {
            _tasks.RemoveAt(at);
        }
        else
        {
            var task = _tasks[at];
            var known = status is "pending" or "in_progress" or "completed";
            _tasks[at] = task with
            {
                Text = Text(input, "subject") is { Length: > 0 } subject ? subject : task.Text,
                Doing = Text(input, "activeForm") is { Length: > 0 } doing ? doing : task.Doing,
                Done = known ? status == "completed" : task.Done,
                Active = known ? status == "in_progress" : task.Active
            };
        }

        events.Add(PlanEvent([.. _tasks]));
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

        // The window is told only here; the size it is told with is the last message's.
        if (ContextTokens is { } context && WindowOf(root) is { } window) Context(context, window, events);

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
    private static (string Text, bool Servers) Describe(JsonElement init)
    {
        var model = Text(init, "model");
        var servers = init.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(server => $"{Text(server, "name")} ({Text(server, "status")})").ToArray()
            : [];
        var text = model.Length > 0 ? $"Model {model}" : "Session started";
        return servers.Length > 0 ? ($"{text}; MCP: {string.Join(", ", servers)}", true) : (text, false);
    }
}
