using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Codex CLI through <c>codex exec --json</c>. Every run is in the <c>workspace-write</c> sandbox, a plan first too (it is
/// asked for in words); <c>read-only</c> and <c>danger-full-access</c> are never used.
/// </summary>
public sealed partial class CodexProvider(Func<AgentSettings> settings, Action<string> log, AgentConsole? console = null)
    : CliAgentProvider(settings, log, console)
{
    public override AgentProviderKind Kind => AgentProviderKind.Codex;
    public override string Name => "Codex";
    public override AgentCapabilities Capabilities => new(true, true, true, "OpenAI");

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

    /// <summary>
    /// Codex's app-server, asked for the account's windows (<c>account/rateLimits/read</c>): a live read that spends no
    /// usage. The handshake comes first (initialize, then initialized), as its protocol asks.
    /// </summary>
    public override async Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken)
    {
        if (FindExecutable() is not { } executable) return [];
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(UsageWait);
        using var talk = AgentTalk.Start(new AgentCommand(executable, ["app-server"], Path.GetTempPath(), string.Empty));
        await talk.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"link","title":"Link","version":"1.0"}}}""");
        if (await talk.AnswerAsync(line => IsAnswer(line, 1), wait.Token) is null) return [];
        await talk.SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        await talk.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":null}""");
        return await talk.AnswerAsync(line => IsAnswer(line, 2), wait.Token) is { } answer ? AccountLimits(answer, DateTimeOffset.Now) : [];
    }

    private static bool IsAnswer(string line, int id)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The windows of the app-server's answer: primary is the 5 hours, secondary the week, each by its length.</summary>
    internal static IReadOnlyList<AgentLimit> AccountLimits(string answer, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            if (!document.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("rateLimits", out var limits) ||
                limits.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return new[] { "primary", "secondary" }
                .Select(name => limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object ? window : default)
                .Where(window => window.ValueKind == JsonValueKind.Object && window.TryGetProperty("usedPercent", out _))
                .Select(window =>
                {
                    var minutes = window.TryGetProperty("windowDurationMins", out var length) && length.TryGetInt64(out var value) ? value : 0;
                    var used = window.GetProperty("usedPercent").GetDouble();
                    return new AgentLimit(AgentProviderKind.Codex, WindowOf(minutes), used, AgentLimits.UnixTime(window, "resetsAt"), used >= 100, now);
                })
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>A window's name by its length: "5 h", "week".</summary>
    private static string WindowOf(long minutes) => minutes switch { 300 => "5 h", 10080 => "week", > 0 => $"{minutes / 60} h", _ => "window" };

    /// <summary>
    /// Codex's exec events carry no rate limits, no context size and no images it made; its own log of the session
    /// does, so they are read from there.
    /// </summary>
    protected override IReadOnlyList<AgentEvent> AfterRun(AgentResult result)
    {
        if (result.SessionId is not { } session) return [];
        // A session that goes on keeps its earlier runs' images in the same log: only this run's are its own.
        var startedAt = DateTimeOffset.Now - result.Stats.Duration - TimeSpan.FromSeconds(5);
        var (limits, context, images) = SessionReport(Path.Combine(CodexHome, "sessions"), session, startedAt);
        var events = new List<AgentEvent>();
        foreach (var image in images)
        {
            var line = image.Prompt.Split('\n', 2)[0].Trim();
            events.Add(new AgentEvent(AgentEventKind.Tool, "Image: " + (line.Length <= 110 ? line : line[..109] + "…"), "imagegen")
            {
                IsWork = true,
                Arguments = image.Prompt.Length <= 2000 ? image.Prompt : image.Prompt[..2000],
                Image = image.Path
            });
            if (image.Path is null) events.Add(new AgentEvent(AgentEventKind.ToolFailed, image.Failure ?? "Codex did not make the image.", "imagegen"));
        }

        if (limits.Count > 0) events.Add(new AgentEvent(AgentEventKind.Limit, "Usage reported") { Limits = limits });
        if (context is not null) events.Add(context);
        return events;
    }

    /// <summary>
    /// What Codex last wrote into its log of a session (<c>rollout-…-&lt;session&gt;.jsonl</c>) in a token count: the
    /// 5-hour and weekly windows (<c>rate_limits</c>) and the context now (<c>last_token_usage</c> with the model's
    /// window, and the context of each request since <paramref name="imagesSince"/> as its curve); and the images its
    /// <c>image_gen</c> tool finished since then. Only those lines are looked at; nothing else of the session is read.
    /// </summary>
    internal static (IReadOnlyList<AgentLimit> Limits, AgentEvent? Context, IReadOnlyList<CodexImage> Images) SessionReport(
        string sessionsFolder, string session, DateTimeOffset? imagesSince = null)
    {
        try
        {
            var file = Directory.Exists(sessionsFolder)
                ? Directory.EnumerateFiles(sessionsFolder, $"rollout-*{session}.jsonl", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (file is null) return ([], null, []);
            string? limits = null, count = null;
            var images = new List<CodexImage>();
            var curve = new List<long>();
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains("\"rate_limits\"") && line.Contains("\"codex\"")) limits = line;
                if (line.Contains("\"token_count\"") && line.Contains("\"last_token_usage\""))
                {
                    count = line;
                    if (RequestIn(line, imagesSince) is { } request) curve.Add(request);
                }

                if (imagesSince is { } since && line.Contains("\"image_gen.generation\"") && line.Contains("\"item_completed\"") &&
                    ImageIn(line, since) is { } image)
                {
                    images.Add(image);
                }
            }

            var context = count is null ? null : ContextIn(count);
            return (limits is null ? [] : LimitsIn(limits), context is null ? null : context with { Curve = SpendMeter.Thin(SpendMeter.Thousands(curve)) }, images);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ([], null, []);
        }
    }

    /// <summary>An image Codex's tool finished (its file, or why there is none) and the prompt it was made from.</summary>
    internal sealed record CodexImage(string? Path, string Prompt, string? Failure);

    /// <summary>An image of the log's completed items, if it was finished since <paramref name="since"/>.</summary>
    private static CodexImage? ImageIn(string line, DateTimeOffset since)
    {
        using var document = JsonDocument.Parse(line);
        var payload = Child(document.RootElement, "payload");
        var item = Child(payload, "item");
        if (Words(item, "kind") != "image_gen.generation" || AgentEventParser.Number(payload, "completed_at_ms") < since.ToUnixTimeMilliseconds()) return null;
        var path = Words(item, "savedPath");
        var failure = Child(item, "failure");
        return new CodexImage(
            Words(item, "status") == "completed" && path is { Length: > 0 } ? path : null,
            Words(item, "revisedPrompt") ?? string.Empty,
            failure.ValueKind switch
            {
                JsonValueKind.String => failure.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => failure.GetRawText(),
                _ => null
            });
    }

    private static string? Words(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static IReadOnlyList<AgentLimit> LimitsIn(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("rate_limits", out var limits)) return [];
        var now = DateTimeOffset.Now;
        return new[] { "primary", "secondary" }
            .Select(name => limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object ? window : default)
            .Where(window => window.ValueKind == JsonValueKind.Object)
            .Select(window =>
            {
                var minutes = window.TryGetProperty("window_minutes", out var length) && length.TryGetInt64(out var value) ? value : 0;
                // Codex writes percents (31.0), never shares: 1.0 is one percent, not all of it.
                double? used = window.TryGetProperty("used_percent", out var percent) && percent.TryGetDouble(out var number) ? number : null;
                var reset = AgentLimits.UnixTime(window, "resets_at") ??
                            (window.TryGetProperty("resets_in_seconds", out var seconds) && seconds.TryGetInt64(out var left) ? now.AddSeconds(left) : null);
                return new AgentLimit(AgentProviderKind.Codex, WindowOf(minutes), used, reset, used >= 100, now);
            })
            .ToArray();
    }

    /// <summary>
    /// The input of a token count's last request, if the count was written since <paramref name="since"/> (a line with
    /// no time counts); null for an earlier one.
    /// </summary>
    private static long? RequestIn(string line, DateTimeOffset? since)
    {
        using var document = JsonDocument.Parse(line);
        if (since is { } start && Words(document.RootElement, "timestamp") is { } stamp &&
            DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) &&
            at < start)
        {
            return null;
        }

        var tokens = AgentEventParser.Number(Child(Child(Child(document.RootElement, "payload"), "info"), "last_token_usage"), "input_tokens");
        return tokens > 0 ? tokens : null;
    }

    /// <summary>
    /// The context after the last request of a token count: what it read plus what it wrote is what the model holds
    /// now (the count's totals add every request of the session up). Null when the count lacks them.
    /// </summary>
    private static AgentEvent? ContextIn(string line)
    {
        using var document = JsonDocument.Parse(line);
        var info = Child(Child(document.RootElement, "payload"), "info");
        var last = Child(info, "last_token_usage");
        var tokens = AgentEventParser.Number(last, "input_tokens") + AgentEventParser.Number(last, "output_tokens");
        var window = AgentEventParser.Number(info, "model_context_window");
        return tokens > 0 ? new AgentEvent(AgentEventKind.Context, "Context") { ContextTokens = tokens, ContextWindow = window > 0 ? window : null } : null;
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>
    /// What a task in a workspace does without. The Codex desktop app's plugins (with their skills and computer
    /// control), connected apps, plugin suggestions, computer and browser use ride along on every request, and in 12
    /// of 14 measured runs the agent read the computer-use plugin's guide (~10k tokens) to look at the game it was
    /// building, once for 33 minutes; mouse control goes only through an explicit choice. Subagents are the agent's own
    /// choice, as for Claude Code. MCP servers of the user's config, such as Blender, stay.
    /// </summary>
    internal static readonly string[] FeaturesOff = ["plugins", "apps", "tool_suggest", "computer_use", "browser_use"];

    public override IReadOnlyList<string> Arguments(AgentTask task)
    {
        // A project folder need not be a git repository (a Blender project rarely is).
        var arguments = new List<string> { "exec" };
        if (task.ResumeSessionId is { } session) arguments.AddRange(["resume", session]);
        // exec's --image takes several files: the next option ends them, or the "-" for stdin at the end would be one.
        foreach (var image in task.Images) arguments.AddRange(["--image", image]);
        // resume takes no --sandbox or --cd: the sandbox is set through config (an unquoted value is read as a string),
        // the folder is the working one.
        arguments.AddRange(task.ResumeSessionId is null
            ? ["--json", "--skip-git-repo-check", "--sandbox", "workspace-write", "--cd", task.Workspace]
            : ["--json", "--skip-git-repo-check", "-c", "sandbox_mode=workspace-write"]);
        // Perforce opens files for edit through its server, so p4 in a Perforce workspace needs the network.
        // So does a No-stop run, which stops for nothing the sandbox would refuse, and a run that reads the web.
        if (AgentChanges.IsPerforce(task.Workspace) || task.NoStop || task.ReadsWeb)
        {
            arguments.AddRange(["-c", "sandbox_workspace_write.network_access=true"]);
        }

        // The same on resume: a session keeps its tools, model and cached start. A bare value is read as a string.
        foreach (var feature in FeaturesOff) arguments.AddRange(["-c", $"features.{feature}=false"]);
        // Link's own answers get no server of the workspace either: one that the user's config lacks is not defined at all.
        var servers = task.WithoutMcp ? [] : McpServers(task.Workspace);
        foreach (var setting in servers.SelectMany(server => server.Settings)) arguments.AddRange(["-c", setting]);
        // The workspace is the one source of servers, as --strict-mcp-config makes it for Claude Code: the user's own
        // (the desktop app's runtime, a DCC bridge for another project) stay off in Link's runs, their config untouched.
        foreach (var name in UserMcpServers().Except(servers.Select(server => server.Name), StringComparer.Ordinal))
        {
            arguments.AddRange(["-c", $"mcp_servers.{name}.enabled=false"]);
        }

        if (task.Model is { } model) arguments.AddRange(["-c", $"model={model}"]);
        if (task.Effort is { } effort) arguments.AddRange(["-c", $"model_reasoning_effort={effort}"]);
        // A tenth below a set budget, so that the session is compacted before it reaches it; the model's window caps it.
        // On Auto Codex keeps its own limit for the model.
        if (task.ContextBudget is { } budget and > 0) arguments.AddRange(["-c", $"model_auto_compact_token_limit={budget * 9 / 10}"]);
        // Auto: what would leave the sandbox (the network, a file outside the workspace) goes to Codex's automatic
        // review instead of failing, as --approve-for-me does; resume takes it only through config.
        // No-stop asks nothing: the sandbox stays the workspace's.
        if (task.NoStop)
        {
            arguments.AddRange(["-c", "approval_policy=never"]);
        }
        else if (task.AutoApprove)
        {
            arguments.AddRange(["-c", "approval_policy=on-request", "-c", "approvals_reviewer=auto_review"]);
        }
        // The task comes on stdin.
        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    /// The workspace's MCP servers as Codex settings, by server. Their one source is the <c>.mcp.json</c> Claude Code
    /// gets too: Codex reads a project's own config only in a trusted project, and its user config would start a game
    /// editor's server in every folder. Codex has no SSE; <c>timeout</c> is Claude Code's tool timeout in ms, and
    /// Codex's own 60 s would cut a long editor call short. A name or value TOML cannot take as it is leaves the server out.
    /// </summary>
    internal static IReadOnlyList<(string Name, IReadOnlyList<string> Settings)> McpServers(string workspace)
    {
        if (McpConfigIn(workspace) is not { } file) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object
                ? servers.EnumerateObject().Select(server => (server.Name, (IReadOnlyList<string>)McpServer(server.Name, server.Value).ToArray())).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The MCP servers of the user's own Codex config, by their <c>[mcp_servers.name]</c> tables.</summary>
    internal static IReadOnlyList<string> UserMcpServers()
    {
        var file = Path.Combine(CodexHome, "config.toml");
        try
        {
            return File.Exists(file)
                ? UserServer().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A table of a server or one of its parts ([mcp_servers.blender.tools.x]); a quoted name is left on.
    [GeneratedRegex(@"^\s*\[mcp_servers\.([A-Za-z0-9_-]+)[\].]", RegexOptions.Multiline)]
    private static partial Regex UserServer();

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

    // A tool's output came in since the model last answered: what it says next is a new request of its.
    private bool _answered;

    protected override void Read(JsonElement root, List<AgentEvent> events)
    {
        Measure(root);
        switch (Text(root, "type"))
        {
            case "thread.started":
                SessionId = Text(root, "thread_id");
                events.Add(new AgentEvent(AgentEventKind.Status, "Session started", AgentEvent.SessionStart));
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
                events.Add(PlanEvent(plan));
                break;
            case "item.started" when root.TryGetProperty("item", out var item) && Text(item, "type") == "command_execution":
                var command = Unwrap(Text(item, "command"));
                Called("command", command, command, IsSearchCommand(command), events, RunsCode(command));
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
                    Spent(Number(usage, "input_tokens"), Number(usage, "cached_input_tokens"), Number(usage, "output_tokens"), null,
                        reasoning: usage.TryGetProperty("reasoning_output_tokens", out _) ? Number(usage, "reasoning_output_tokens") : null);
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

    /// <summary>
    /// What an event adds to the context, for the spend meter. Codex tells no request's size on the way, so a request
    /// is counted where the model speaks after a tool's output (or first in a turn), and the text's size stands in.
    /// </summary>
    private void Measure(JsonElement root)
    {
        var type = Text(root, "type");
        if (type == "turn.started")
        {
            _answered = false;
            return;
        }

        if (type is not ("item.started" or "item.completed") || !root.TryGetProperty("item", out var item)) return;
        var kind = Text(item, "type");
        if (kind is "todo_list" or "error") return;
        var output = type == "item.completed" && kind is "command_execution" or "mcp_tool_call";
        if (!output || kind == "mcp_tool_call" && !_answered)
        {
            if (!_answered) Meter.Request();
            _answered = true;
        }

        if (type == "item.started") return;
        switch (kind)
        {
            case "agent_message" or "reasoning":
                Meter.Text(SpendPart.Writing, Text(item, "text").Length);
                break;
            case "command_execution":
                var command = Text(item, "command");
                Meter.Text(SpendPart.Writing, command.Length);
                Meter.Text(IsSearchCommand(Unwrap(command)) ? SpendPart.Search : SpendPart.Commands, Text(item, "aggregated_output").Length);
                _answered = false;
                break;
            case "mcp_tool_call":
                Meter.Text(SpendPart.Writing, item.TryGetProperty("arguments", out var arguments) ? arguments.GetRawText().Length : 0);
                MeasureMcp(item);
                _answered = false;
                break;
        }
    }

    /// <summary>An MCP result's text as a tool's work, its pictures as pictures.</summary>
    private void MeasureMcp(JsonElement item)
    {
        if (!item.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return;
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            Meter.Text(SpendPart.Commands, result.GetRawText().Length);
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (Text(part, "type") == "image")
            {
                var (width, height) = SpendMeter.ImageSize(Text(part, "data"));
                Meter.Tokens(SpendPart.Images, SpendMeter.ImageTokens(width, height));
            }
            else
            {
                Meter.Text(SpendPart.Commands, Text(part, "text").Length);
            }
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
                if (failed && IsSandboxRefusal(output)) Denied("workspace-write", events, "The sandbox refused a change");
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
                    arguments.ValueKind == JsonValueKind.Undefined ? tool : tool + " " + arguments.GetRawText(), false, events, work: true);
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

    /// <summary>Codex tells a write its sandbox blocked (outside the workspace) only in its log: "patch rejected: writing is blocked…".</summary>
    public override IReadOnlyList<AgentEvent> ReadError(string line)
    {
        if (!line.Contains("blocked by read-only sandbox", StringComparison.OrdinalIgnoreCase) || Denials.Count > 0) return [];
        var events = new List<AgentEvent>();
        Denied("workspace-write", events, "The sandbox refused a change");
        return events;
    }

    private static bool IsSandboxRefusal(string output) =>
        output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("read-only", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Codex runs each command through PowerShell, as <c>powershell.exe -Command "…"</c> (its quotes and backslashes
    /// escaped) or, lately, as <c>"…\powershell.exe" '…'</c>; an agent may call PowerShell itself inside that. The row
    /// shows the command itself.
    /// </summary>
    internal static string Unwrap(string command)
    {
        for (var layer = 0; layer < 3 && ShellCommand().Match(command) is { Success: true } match; layer++)
        {
            command = match.Groups["double"].Success ? Escaped().Replace(match.Groups["double"].Value, "$1") : match.Groups["single"].Value.Replace("''", "'");
        }

        return command;
    }

    // A backslash that escapes a quote or a backslash inside the double-quoted command.
    [GeneratedRegex(@"\\([""\\])")]
    private static partial Regex Escaped();

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
