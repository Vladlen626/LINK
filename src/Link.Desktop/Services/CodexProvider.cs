using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Codex CLI through <c>codex exec --json</c>. Analyze and a plan run in the <c>read-only</c> sandbox, Do in
/// <c>workspace-write</c>; <c>danger-full-access</c> is never used. Codex has no plan mode, so a plan is asked for in
/// words and the sandbox keeps it from changing anything.
/// </summary>
public sealed class CodexProvider(Func<AgentSettings> settings, Action<string> log, AgentConsole? console = null)
    : CliAgentProvider(settings, log, console)
{
    public override AgentProviderKind Kind => AgentProviderKind.Codex;
    public override string Name => "Codex";

    // Codex lowers a level its model lacks to the model's highest. Ultra is left out: it works through parallel
    // subagents, and Link runs agents without them.
    public override IReadOnlyList<AgentChoice> Efforts =>
        [new("low", "Low"), new("medium", "Medium"), new("high", "High"), new("xhigh", "Extra high"), new("max", "Max — the highest")];

    /// <summary>The models of the account, as Codex keeps them in its own list (models_cache.json); none when it cannot be read.</summary>
    public override IReadOnlyList<AgentChoice> Models =>
        CachedModels().Select(model => new AgentChoice(model.Slug, model.Name)).ToArray();

    /// <summary>
    /// A model's own reasoning levels as Codex lists them, each with what it is for; ultra stays out, as in
    /// <see cref="Efforts"/>. The usual levels for a model the list does not have.
    /// </summary>
    public override IReadOnlyList<AgentChoice> EffortsOf(string? model)
    {
        var slug = model ?? OwnSetup.Model;
        var levels = CachedModels().FirstOrDefault(each => each.Slug == slug)?.Levels ?? [];
        var known = levels
            .Select(level => (level, Efforts.FirstOrDefault(effort => effort.Value == level.Effort)))
            .Where(pair => pair.Item2 is not null)
            .Select(pair => new AgentChoice(pair.level.Effort,
                pair.level.Description.Length > 0 ? $"{pair.Item2!.Label.Split(" — ")[0]} — {pair.level.Description}" : pair.Item2!.Label))
            .ToArray();
        return known.Length > 0 ? known : Efforts;
    }

    /// <summary>The model and effort at the top of the user's config.toml, which Codex runs on when Link names none.</summary>
    public override (string? Model, string? Effort) OwnSetup
    {
        get
        {
            // Read again only when the file changed: the Task tab asks for it with every redraw.
            var file = Path.Combine(CodexHome, "config.toml");
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_ownSetup.File == file && _ownSetup.Stamp == stamp) return _ownSetup.Setup;
            (string? Model, string? Effort) setup = (null, null);
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var trimmed = line.Trim();
                    // The top level ends where the first table begins.
                    if (trimmed.StartsWith('[')) break;
                    setup.Model ??= TomlString(trimmed, "model");
                    setup.Effort ??= TomlString(trimmed, "model_reasoning_effort");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // No config: Codex's own defaults, which Link cannot name.
            }

            _ownSetup = (file, stamp, setup);
            return setup;
        }
    }

    private static (string? File, DateTime Stamp, (string? Model, string? Effort) Setup) _ownSetup;
    private static (string? File, DateTime Stamp, IReadOnlyList<CachedModel> Models) _models = (null, default, []);

    private sealed record CachedModel(string Slug, string Name, IReadOnlyList<(string Effort, string Description)> Levels);

    /// <summary>Codex's list of models, read again only when Codex has written it anew.</summary>
    private static IReadOnlyList<CachedModel> CachedModels()
    {
        var file = Path.Combine(CodexHome, "models_cache.json");
        var stamp = File.GetLastWriteTimeUtc(file);
        if (_models.File == file && _models.Stamp == stamp) return _models.Models;
        var models = ReadModels(file);
        _models = (file, stamp, models);
        return models;
    }

    private static IReadOnlyList<CachedModel> ReadModels(string file)
    {
        try
        {
            using var cache = JsonDocument.Parse(File.ReadAllText(file));
            return cache.RootElement.GetProperty("models").EnumerateArray()
                .Where(model => Text(model, "visibility") == "list" && Text(model, "slug") is { Length: > 0 })
                .Select(model => new CachedModel(
                    Text(model, "slug")!,
                    Text(model, "display_name") ?? Text(model, "slug")!,
                    model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
                        ? levels.EnumerateArray()
                            .Where(level => Text(level, "effort") is { Length: > 0 })
                            .Select(level => (Text(level, "effort")!, Text(level, "description") ?? string.Empty))
                            .ToArray()
                        : []))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? TomlString(string line, string key)
    {
        var match = Regex.Match(line, $"^{key}\\s*=\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    protected override IReadOnlyList<string> SignInCheck => ["login", "status"];

    // Codex reads .agents/skills from its folder up to the repository's root.
    protected override bool FindsSkills => true;

    private static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Codex's exec events carry no rate limits; its own log of the session does, so they are read from there.</summary>
    protected override IReadOnlyList<AgentEvent> AfterRun(AgentResult result) =>
        result.SessionId is { } session && SessionLimits(Path.Combine(CodexHome, "sessions"), session) is { Count: > 0 } limits
            ? [new AgentEvent(AgentEventKind.Limit, "Usage reported") { Limits = limits }]
            : [];

    /// <summary>
    /// The 5-hour and weekly windows Codex last wrote into its log of a session (<c>rollout-…-&lt;session&gt;.jsonl</c>,
    /// <c>rate_limits</c> of a token count). Only those lines are looked at; nothing else of the session is read.
    /// </summary>
    internal static IReadOnlyList<AgentLimit> SessionLimits(string sessionsFolder, string session)
    {
        try
        {
            var file = Directory.Exists(sessionsFolder)
                ? Directory.EnumerateFiles(sessionsFolder, $"rollout-*{session}.jsonl", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            var last = file is null ? null : File.ReadLines(file).LastOrDefault(line => line.Contains("\"rate_limits\"") && line.Contains("\"codex\""));
            if (last is null) return [];
            using var document = JsonDocument.Parse(last);
            if (!document.RootElement.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("rate_limits", out var limits)) return [];
            var now = DateTimeOffset.Now;
            return new[] { "primary", "secondary" }
                .Select(name => limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object ? window : default)
                .Where(window => window.ValueKind == JsonValueKind.Object)
                .Select(window =>
                {
                    var minutes = window.TryGetProperty("window_minutes", out var length) && length.TryGetInt64(out var value) ? value : 0;
                    var used = AgentLimits.Percent(window, "used_percent");
                    var reset = AgentLimits.UnixTime(window, "resets_at") ??
                                (window.TryGetProperty("resets_in_seconds", out var seconds) && seconds.TryGetInt64(out var left) ? now.AddSeconds(left) : null);
                    var name = minutes switch { 300 => "5 h", 10080 => "week", > 0 => $"{minutes / 60} h", _ => "window" };
                    return new AgentLimit(AgentProviderKind.Codex, name, used, reset, used >= 100, now);
                })
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// What a task in a workspace does without. The Codex desktop app's plugins (with their skills and computer
    /// control), connected apps, plugin suggestions, computer and browser use ride along on every request, and in 12
    /// of 14 measured runs the agent read the computer-use plugin's guide (~10k tokens) to look at the game it was
    /// building, once for 33 minutes; mouse control goes only through an explicit choice. Subagents work out of
    /// Activity's sight and outside the task's numbers (none of the 14 runs used them). MCP servers of the user's
    /// config, such as Blender, stay.
    /// </summary>
    internal static readonly string[] FeaturesOff = ["plugins", "apps", "tool_suggest", "computer_use", "browser_use", "multi_agent"];

    public override IReadOnlyList<string> Arguments(AgentTask task)
    {
        var sandbox = task.Access == AgentAccess.Do && !task.PlanOnly ? "workspace-write" : "read-only";
        // A project folder need not be a git repository (a Blender project rarely is).
        var arguments = new List<string> { "exec" };
        if (task.ResumeSessionId is { } session) arguments.AddRange(["resume", session]);
        // exec's --image takes several files: the next option ends them, or the "-" for stdin at the end would be one.
        foreach (var image in task.Images) arguments.AddRange(["--image", image]);
        // resume takes no --sandbox or --cd: the sandbox is set through config (an unquoted value is read as a string),
        // the folder is the working one.
        arguments.AddRange(task.ResumeSessionId is null
            ? ["--json", "--skip-git-repo-check", "--sandbox", sandbox, "--cd", task.Workspace]
            : ["--json", "--skip-git-repo-check", "-c", $"sandbox_mode={sandbox}"]);
        // Perforce opens files for edit through its server, so p4 in a Perforce workspace needs the network.
        if (sandbox == "workspace-write" && AgentChanges.IsPerforce(task.Workspace))
        {
            arguments.AddRange(["-c", "sandbox_workspace_write.network_access=true"]);
        }

        // The same on resume: a session keeps its tools, model and cached start. A bare value is read as a string.
        foreach (var feature in FeaturesOff) arguments.AddRange(["-c", $"features.{feature}=false"]);
        foreach (var setting in McpServers(task.Workspace)) arguments.AddRange(["-c", setting]);
        if (task.Model is { } model) arguments.AddRange(["-c", $"model={model}"]);
        if (task.Effort is { } effort) arguments.AddRange(["-c", $"model_reasoning_effort={effort}"]);
        // Auto: what would leave the sandbox (the network, a file outside the workspace) goes to Codex's automatic
        // review instead of failing, as --approve-for-me does; resume takes it only through config.
        if (task.AutoApprove && sandbox == "workspace-write")
        {
            arguments.AddRange(["-c", "approval_policy=on-request", "-c", "approvals_reviewer=auto_review"]);
        }
        // The task comes on stdin.
        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    /// The workspace's MCP servers as Codex settings. Their one source is the <c>.mcp.json</c> Claude Code reads by
    /// itself: Codex reads a project's own config only in a trusted project, and its user config would start a game
    /// editor's server in every folder. Codex has no SSE; <c>timeout</c> is Claude Code's tool timeout in ms, and
    /// Codex's own 60 s would cut a long editor call short. A name or value TOML cannot take as it is leaves the server out.
    /// </summary>
    internal static IReadOnlyList<string> McpServers(string workspace)
    {
        var file = Path.Combine(workspace, ".mcp.json");
        if (!File.Exists(file)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object
                ? servers.EnumerateObject().SelectMany(server => McpServer(server.Name, server.Value)).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> McpServer(string name, JsonElement server)
    {
        if (server.ValueKind != JsonValueKind.Object || Property(server, "type") == "sse" || !IsBareKey(name)) return [];
        var settings = new List<string>();
        var values = new List<string>();
        if (Property(server, "url") is { } url)
        {
            settings.Add("url=" + Literal(url));
            values.Add(url);
        }
        else if (Property(server, "command") is { } command)
        {
            var args = server.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(arg => arg.ToString()).ToArray()
                : [];
            var env = server.TryGetProperty("env", out var variables) && variables.ValueKind == JsonValueKind.Object
                ? variables.EnumerateObject().Select(variable => (variable.Name, Value: variable.Value.ToString())).ToArray()
                : [];
            if (env.Any(variable => !IsBareKey(variable.Name))) return [];
            settings.Add("command=" + Literal(command));
            settings.Add($"args=[{string.Join(", ", args.Select(Literal))}]");
            settings.AddRange(env.Select(variable => $"env.{variable.Name}={Literal(variable.Value)}"));
            values.AddRange([command, .. args, .. env.Select(variable => variable.Value)]);
        }
        else
        {
            return [];
        }

        if (server.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt64(out var milliseconds) && milliseconds > 0)
        {
            settings.Add($"tool_timeout_sec={(milliseconds + 999) / 1000}");
        }

        // A TOML literal string holds anything but a quote mark and control characters.
        return values.Any(value => value.Any(c => c == '\'' || char.IsControl(c)))
            ? []
            : settings.Select(setting => $"mcp_servers.{name}.{setting}");

        static string? Property(JsonElement element, string key) =>
            element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static bool IsBareKey(string key) => key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

        static string Literal(string value) => $"'{value}'";
    }

    public override string Input(AgentTask task) =>
        task.PlanOnly
            ? "Make a plan only: read what you need, change nothing, and answer with the steps you would take.\n\n" + task.Prompt
            : task.Prompt;

    /// <summary>
    /// Codex's Windows sandbox runs commands as a user of its own, so git calls the user's repository "dubious
    /// ownership" and refuses it: 13 of 14 measured runs met it and asked again with <c>-c safe.directory</c>. The
    /// workspace's repository is safe for the agent's commands only.
    /// </summary>
    public override IReadOnlyDictionary<string, string> EnvironmentVariables(AgentTask task)
    {
        if (GitRoot(task.Workspace) is not { } root) return new Dictionary<string, string>();
        // Git reads each setting in single quotes; one inside the path is closed, escaped and opened again.
        var setting = "'safe.directory=" + root.Replace('\\', '/').Replace("'", @"'\''") + "'";
        var inherited = System.Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_PARAMETERS"] = string.IsNullOrWhiteSpace(inherited) ? setting : $"{inherited} {setting}"
        };
    }

    private static string? GitRoot(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return directory.FullName;
        }

        return null;
    }

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
            // The agent's own plan, whole each time it changes: the Task tab shows it as a list.
            case "item.started" or "item.updated" or "item.completed" when root.TryGetProperty("item", out var list) &&
                                                                           Text(list, "type") == "todo_list" &&
                                                                           list.TryGetProperty("items", out var items) &&
                                                                           items.ValueKind == JsonValueKind.Array:
                var plan = items.EnumerateArray()
                    .Select(item => new AgentPlanItem(Text(item, "text"),
                        item.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True, false))
                    .Where(item => item.Text.Length > 0)
                    .ToArray();
                // The first step not done is the one under way.
                var active = Array.FindIndex(plan, item => !item.Done);
                if (active >= 0) plan[active] = plan[active] with { Active = true };
                events.Add(new AgentEvent(AgentEventKind.Plan, $"Plan: {plan.Count(item => item.Done)} of {plan.Length} done") { Plan = plan });
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
                var arguments = item.TryGetProperty("arguments", out var given) ? given : default;
                Called("mcp", McpCall(Text(item, "server"), Text(item, "tool"), arguments),
                    arguments.ValueKind == JsonValueKind.Undefined ? tool : tool + " " + arguments.GetRawText(), false, events);
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

    /// <summary>
    /// Codex runs each command through PowerShell, as <c>powershell.exe -Command "…"</c> or, lately, as
    /// <c>"…\powershell.exe" '…'</c>; the row shows the command itself.
    /// </summary>
    internal static string Unwrap(string command) => ShellCommand().Match(command) is not { Success: true } match ? command
        : match.Groups["double"].Success ? match.Groups["double"].Value.Replace("\\\"", "\"")
        : match.Groups["single"].Value.Replace("''", "'");

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

    // The shell, its switches (-NoProfile, -Command…), then the command in double or single quotes.
    [GeneratedRegex("""(?:powershell|pwsh)(?:\.exe)?"?(?:\s+-\w+)*\s+(?:"(?<double>.*)"|'(?<single>.*)')\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShellCommand();
}
