using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// What the agents share: find the CLI, run it through <see cref="AgentProcess"/>, turn its JSONL into events and
/// write one <c>[Agent]</c> line to link.log. A provider only says which arguments to pass and how to read its events.
/// </summary>
public abstract class CliAgentProvider : IAgentProvider
{
    private readonly Func<AgentSettings> _settings;
    private readonly Action<string> _log;
    private readonly AgentConsole? _console;

    protected CliAgentProvider(Func<AgentSettings> settings, Action<string> log, AgentConsole? console = null)
    {
        _settings = settings;
        _log = log;
        _console = console;
    }

    public abstract AgentProviderKind Kind { get; }
    public abstract string Name { get; }
    public abstract IReadOnlyList<AgentChoice> Efforts { get; }
    public abstract IReadOnlyList<AgentChoice> Models { get; }
    public virtual AgentCapabilities Capabilities => AgentCapabilities.Usual;

    public virtual IReadOnlyList<AgentChoice> EffortsOf(string? model) => Efforts;

    public virtual (string? Model, string? Effort) OwnSetup => (null, null);

    public abstract Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken);

    /// <summary>How long Link waits for a CLI to tell its usage: it asks the provider over the network.</summary>
    protected static readonly TimeSpan UsageWait = TimeSpan.FromSeconds(30);

    public string? FindExecutable() => AgentExecutables.Find(Kind, _settings().PathFor(Kind));


    public async Task<AgentProviderStatus> CheckAsync(AgentSettings setup, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(Kind, setup.PathFor(Kind));
        if (executable is null) return new AgentProviderStatus(null, null, null);
        var version = await QuietAsync(executable, ["--version"], new Dictionary<string, string>(), cancellationToken);
        var answer = await QuietAsync(executable, SignInCheck, AccountVariables(setup), cancellationToken);
        return new AgentProviderStatus(executable, version?.Trim(), answer is null ? null : IsSignedIn(answer), answer is null ? null : AccountOf(answer));
    }

    /// <summary>The settings as they are now, such as the account the CLI runs under.</summary>
    protected AgentSettings Settings => _settings();

    /// <summary>Variables that choose the account the CLI runs under in <paramref name="setup"/>.</summary>
    protected virtual IReadOnlyDictionary<string, string> AccountVariables(AgentSettings setup) => new Dictionary<string, string>();

    /// <summary>Whom the sign-in check's answer names, such as an email; null when it names no one.</summary>
    protected virtual string? AccountOf(string answer) => null;

    public async Task<AgentResult> RunAsync(AgentTask task, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new InvalidOperationException($"{Name} is not found. Install it or choose its file in Settings.");
        var parser = CreateParser(task);
        parser.Meter.Start(StandardInput(task).Length, task.ResumeSessionId is not null);
        var loops = new AgentLoopDetector(!task.PlanOnly);
        var clock = Stopwatch.StartNew();
        TimeSpan? firstEvent = null;
        var talk = Talks(task) ? new AgentInput() : null;
        // A question the CLI leaves unanswered ends with its run.
        using var talking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var command = new AgentCommand(executable, Arguments(task), task.Workspace, talk is null ? StandardInput(task) : Opening(StandardInput(task)))
        {
            EnvironmentVariables = EnvironmentVariables(task)
        };
        _console?.Sent($"{Name} · {(task.ResumeSessionId is { } session ? "resume " + session : "new session")}",
            command.Executable, command.Arguments, command.WorkingDirectory, command.EnvironmentVariables, command.Input);
        AgentProcessResult process;
        try
        {
            // stdout and stderr are read on two threads; the parser and the loop check take one line at a time.
            var turn = new Lock();
            string? named = null;
            void Report(IReadOnlyList<AgentEvent> events)
            {
                foreach (var agentEvent in events)
                {
                    firstEvent ??= clock.Elapsed;
                    progress.Report(agentEvent);
                    if (loops.Add(agentEvent) is { } loop) progress.Report(loop);
                }

                if (parser.SessionId is { } session && session != named) progress.Report(new AgentEvent(AgentEventKind.Session, named = session));
            }

            process = await AgentProcess.RunAsync(
                command,
                line =>
                {
                    _console?.Line(line);
                    lock (turn) Report(parser.Read(line));
                    if (talk is not null) Talk(line, task, talk, talking.Token);
                },
                line =>
                {
                    _console?.Line(line, error: true);
                    lock (turn) Report(parser.ReadError(line));
                },
                cancellationToken,
                talk);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log($"[Agent] {Name}: could not start ({exception.Message})");
            _console?.Line($"Link could not start it: {exception.Message}", error: true);
            throw;
        }
        finally
        {
            talking.Cancel();
        }

        var succeeded = !process.Stopped && process.ExitCode == 0 && !parser.Failed;
        var summary = parser.Summary;
        if (!succeeded && !process.Stopped && summary.Length == 0) summary = LastErrorLine(process.Log);
        var result = new AgentResult
        {
            SessionId = parser.SessionId,
            Model = parser.Model,
            Summary = summary,
            Succeeded = succeeded,
            Stopped = process.Stopped,
            ExitCode = process.ExitCode,
            Denials = parser.Denials.Distinct().ToArray(),
            ChangedFiles = parser.ChangedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Stats = parser.Stats with { Duration = clock.Elapsed },
            Loops = loops.Kinds.ToArray(),
            Log = process.Log
        };
        var after = await Task.Run(() => AfterRun(result), CancellationToken.None);
        // What the provider reads after the run (Codex's context) belongs to the run's numbers as well; a curve told
        // only now gives its own prompt's size: what the first request held beyond the text Link and the agent sent.
        if (after.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Context) is { } held)
        {
            if (!parser.Meter.Measured && held.Curve.Count > 0) parser.Meter.AddBase(held.Curve[0] * 1000L - parser.Meter.First);
            result = result with
            {
                Stats = result.Stats with
                {
                    ContextTokens = held.ContextTokens,
                    ContextWindow = held.ContextWindow,
                    ContextCurve = held.Curve.Count > 0 ? held.Curve : result.Stats.ContextCurve,
                    SpendInput = parser.Meter.Requests > 0 ? parser.Meter.Input : null
                }
            };
        }

        foreach (var agentEvent in after) progress.Report(agentEvent);
        // Never the task's text: it can hold anything the user wrote.
        var outcome = result.Stopped ? "stopped" : result.Succeeded ? "done" : $"failed (exit {result.ExitCode})";
        var first = firstEvent is { } elapsed ? Seconds(elapsed) : "none";
        var kind = task.PlanOnly ? ", plan"
            : task.AnswerFile is { } answer ? $", answer to {answer}"
            : task.AutoApprove ? ", auto"
            : string.Empty;
        // Runs compare only with the same model and effort.
        var setup = (task.Model is { } model ? $", {model}" : string.Empty) +
                    (result.Model is { } ran && ran != task.Model ? $" ({ran})" : string.Empty) + (task.Effort is { } effort ? $", effort {effort}" : string.Empty);
        _log($"[Agent] {Name}{(task.Judge ? ", judge" : string.Empty)}{kind}{setup}: {outcome}; " +
             $"session {result.SessionId ?? "none"}, first event {first}, {Seconds(clock.Elapsed)}, " +
             $"{result.ChangedFiles.Count} files, denials: {(result.Denials.Count == 0 ? "none" : string.Join(", ", result.Denials))}; " +
             LogStats(result.Stats) +
             (result.Loops.Count > 0 ? "; looks like a loop: " + string.Join(", ", result.Loops) : string.Empty));
        return result;
    }

    /// <summary>Seconds for link.log, with a point whatever the language of Windows.</summary>
    internal static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>The run's numbers for link.log, whole and in one order, so that runs can be compared line by line.</summary>
    internal static string LogStats(AgentStats stats)
    {
        var before = stats.SearchesBeforeChange is { } count ? $" ({count} before a change)" : string.Empty;
        var tokens = stats.InputTokens is { } input
            ? $"tokens in {input} (cached {stats.CachedTokens ?? 0}) out {stats.OutputTokens ?? 0}{(stats.SessionTokens ? " for the session" : string.Empty)}"
            : "tokens unknown";
        var cost = stats.CostUsd is { } usd ? ", cost $" + usd.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
        return $"turns {stats.Turns}, tools {stats.ToolCalls}, helpers {stats.HelperCalls}, searches {stats.Searches}{before}, tool errors {stats.ToolErrors}, {tokens}{cost}";
    }

    /// <summary>The CLI's arguments for a task; the task's text itself goes on stdin.</summary>
    public abstract IReadOnlyList<string> Arguments(AgentTask task);

    /// <summary>What the CLI reads on stdin: the task's text, and whatever the provider cannot take as an argument.</summary>
    public virtual string Input(AgentTask task) => task.Prompt;

    /// <summary>Variables the CLI's process gets on top of Link's own.</summary>
    public virtual IReadOnlyDictionary<string, string> EnvironmentVariables(AgentTask task) => new Dictionary<string, string>();

    /// <summary>
    /// Whether the CLI finds the workspace's own skills (<see cref="ProjectSkills"/>) by itself; those of its tool packs,
    /// outside the workspace, Link lists for every agent.
    /// </summary>
    protected virtual bool FindsSkills => false;

    /// <summary>Whether Link's guard checks the CLI's tool calls, so the rules it holds need no words.</summary>
    protected virtual bool Guarded => false;

    /// <summary>
    /// The workspace's <c>.mcp.json</c>, the one source of MCP servers for both agents in Link's runs; null when the
    /// workspace has none.
    /// </summary>
    internal static string? McpConfigIn(string workspace)
    {
        var file = Path.Combine(workspace, ".mcp.json");
        return File.Exists(file) ? file : null;
    }

    /// <summary>The names of the workspace's MCP servers, as its <c>.mcp.json</c> lists them; none for a file that cannot be read.</summary>
    internal static IReadOnlyList<string> McpServerNames(string workspace)
    {
        if (McpConfigIn(workspace) is not { } file) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object
                ? [.. servers.EnumerateObject().Select(server => server.Name)]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>What a new session is told of the helpers the run has (Claude Code's subagents, Codex as a tool); null: none.</summary>
    protected virtual string? HelpersText(AgentTask task) => null;

    /// <summary>What the provider learns after a run from outside its events, such as Codex's rate limits.</summary>
    protected virtual IReadOnlyList<AgentEvent> AfterRun(AgentResult result) => [];

    /// <summary>
    /// What the CLI gets on stdin: a new session starts with the agent's rules and the task's specialist and skills, a
    /// resumed one has them.
    /// </summary>
    public string StandardInput(AgentTask task)
    {
        var input = task.PlanOnly ? PlanRequest + Input(task) : Input(task);
        return task.ResumeSessionId is null
            ? AgentRules.Before(input, task.Workspace, !FindsSkills, ProjectRoles.Find(task.RoleId, task.Workspace), task.Skills, Guarded, HelpersText(task))
            : input;
    }

    /// <summary>
    /// What a plan first is asked, before the task: in words, not by a read-only mode, so the agent keeps all its tools
    /// (it may run a script to measure, say). The user runs the plan in the same session.
    /// </summary>
    public const string PlanRequest =
        "First a plan, not the work: look at what you need, then answer with the steps you would take, and stop there. " +
        "Do not make the change yet: the user reads the plan and runs it.\n\n";

    /// <summary>
    /// Whether the CLI is talked with while it works (<see cref="Talk"/>): its stdin stays open after the task, which goes
    /// in as <see cref="Opening"/> makes it.
    /// </summary>
    protected virtual bool Talks(AgentTask task) => false;

    /// <summary>The task as the first line of a talk.</summary>
    protected virtual string Opening(string input) => input;

    /// <summary>A line of the CLI's in a talk: a question to answer on <paramref name="input"/>, or the end of its work.</summary>
    protected virtual void Talk(string line, AgentTask task, AgentInput input, CancellationToken cancellationToken)
    {
    }

    public abstract AgentEventParser CreateParser(AgentTask task);

    protected abstract IReadOnlyList<string> SignInCheck { get; }

    protected abstract bool IsSignedIn(string answer);

    private static string LastErrorLine(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("! ", StringComparison.Ordinal))?[2..] ?? string.Empty;

    /// <summary>Runs a short CLI command, such as its version, and returns what it printed; null when it failed.</summary>
    private static async Task<string?> QuietAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = new System.Text.StringBuilder();
        try
        {
            var result = await AgentProcess.RunAsync(
                new AgentCommand(executable, arguments, Path.GetTempPath(), string.Empty) { EnvironmentVariables = environment },
                line => output.AppendLine(line),
                null,
                timeout.Token);
            return result.Stopped ? null : output.Length > 0 ? output.ToString() : result.Log.Replace("! ", string.Empty);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads one provider's JSONL into events and keeps what the result needs, its numbers included. Lines that are not
/// JSON and events it does not know are skipped: a newer CLI may add them.
/// </summary>
public abstract partial class AgentEventParser
{
    // Programs that only find, list or read files, by the name a shell command starts with.
    private static readonly HashSet<string> SearchPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rg", "grep", "egrep", "fgrep", "findstr", "find", "fd", "ls", "dir", "tree", "cat", "type", "head", "tail", "more",
        "Get-ChildItem", "gci", "Select-String", "sls", "Get-Content", "gc"
    };

    // Programs that run code, by the same first name: interpreters, builds, package runners and DCC apps run headless.
    private static readonly HashSet<string> CodeRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "python3", "py", "uv", "uvx", "dotnet", "npm", "npx", "pnpm", "yarn", "bun", "deno", "cargo", "go",
        "make", "cmake", "msbuild", "gradle", "mvn", "java", "pytest", "blender", "UnrealEditor", "UnrealEditor-Cmd"
    };

    private static readonly HashSet<string> FolderChanges = new(StringComparer.OrdinalIgnoreCase) { "cd", "Set-Location", "sl", "pushd", "Push-Location" };
    private readonly List<string> _denials = [];
    private readonly List<string> _changedFiles = [];
    private readonly Dictionary<string, (int Times, int Characters)> _reads = new(StringComparer.OrdinalIgnoreCase);
    private int _toolCalls;
    private int _helperCalls;
    private int _searches;
    private int _searchesBeforeChange;
    private int _toolErrors;
    private long? _inputTokens;
    private long? _cachedTokens;
    private long? _outputTokens;
    private decimal? _cost;
    private long? _cacheWrites;
    private long? _reasoning;
    private long? _contextWindow;

    public string? SessionId { get; protected set; }

    /// <summary>The model the CLI says the session runs on: a family the task asked for, resolved; null when it does not say.</summary>
    public string? Model { get; protected set; }

    public string Summary { get; protected set; } = string.Empty;
    public bool Failed { get; protected set; }
    public IReadOnlyList<string> Denials => _denials;
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

    /// <summary>What the run's context is made of, request by request: what its tokens went to.</summary>
    public SpendMeter Meter { get; } = new();

    /// <summary>The run's numbers so far; its time is the runner's to add.</summary>
    public AgentStats Stats => new()
    {
        Turns = Turns,
        ToolCalls = _toolCalls,
        HelperCalls = _helperCalls,
        Searches = _searches,
        SearchesBeforeChange = _changedFiles.Count > 0 ? _searchesBeforeChange : null,
        ToolErrors = _toolErrors,
        InputTokens = _inputTokens,
        CachedTokens = _cachedTokens,
        OutputTokens = _outputTokens,
        CostUsd = _cost,
        SessionTokens = TokensOfSession && _inputTokens is not null,
        ContextTokens = ContextTokens,
        ContextWindow = _contextWindow,
        CacheWriteTokens = _cacheWrites,
        ReasoningTokens = _reasoning,
        ApiTime = ApiTime,
        SpendInput = Meter.Requests > 0 ? Meter.Input : null,
        ColdTokens = Meter.Measured ? Meter.ColdTokens : null,
        ContextCurve = Meter.Measured ? Meter.Curve : [],
        CacheMinutes = CacheMinutes,
        Rereads = FileReads.Join(_reads.Where(read => read.Value.Times > 1)
            .Select(read => new FileReads(read.Key, read.Value.Times, (long)(read.Value.Characters / SpendMeter.CharsPerToken))))
    };

    protected int Turns { get; set; }

    /// <summary>The time the model's requests took, where the CLI tells it.</summary>
    protected TimeSpan? ApiTime { get; set; }

    /// <summary>How long the cache keeps what the run wrote to it, in minutes, where the CLI tells it.</summary>
    protected int? CacheMinutes { get; set; }

    /// <summary>What the session holds, as the provider last told; null before it tells.</summary>
    protected long? ContextTokens { get; private set; }

    /// <summary>The provider reports the tokens of the whole session so far, as Codex does for a resumed one.</summary>
    protected bool TokensOfSession { get; init; }

    public IReadOnlyList<AgentEvent> Read(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return [];
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var events = new List<AgentEvent>();
            Read(document.RootElement, events);
            return events;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    protected abstract void Read(JsonElement root, List<AgentEvent> events);

    /// <summary>A line of stderr: logs mostly, but some refusals are told only there.</summary>
    public virtual IReadOnlyList<AgentEvent> ReadError(string line) => [];

    protected void Denied(string rule, List<AgentEvent> events, string text)
    {
        _denials.Add(rule);
        events.Add(new AgentEvent(AgentEventKind.NeedsInput, text, rule));
    }

    /// <summary>The agent's plan as it stands, whole: what the Task tab shows as a list.</summary>
    protected static AgentEvent PlanEvent(IReadOnlyList<AgentPlanItem> plan) =>
        new(AgentEventKind.Plan, $"Plan: {plan.Count(item => item.Done)} of {plan.Count} done") { Plan = plan };

    protected void Changed(string path, List<AgentEvent> events)
    {
        _changedFiles.Add(path);
        events.Add(new AgentEvent(AgentEventKind.Artifact, path, path));
    }

    /// <summary>
    /// A tool call, a step of Activity; <paramref name="arguments"/> tell it from another call of the same tool (a long
    /// write is cut), <paramref name="search"/>: it only looks around; <paramref name="image"/>: the picture it looks at.
    /// </summary>
    protected void Called(string name, string text, string arguments, bool search, List<AgentEvent> events, bool work = false, string? image = null,
        bool helper = false)
    {
        _toolCalls++;
        if (helper) _helperCalls++;
        if (search)
        {
            _searches++;
            if (_changedFiles.Count == 0) _searchesBeforeChange++;
        }

        events.Add(new AgentEvent(AgentEventKind.Tool, Short(text), name)
        {
            IsSearch = search,
            IsWork = work,
            Arguments = arguments.Length <= 2000 ? arguments : arguments[..2000],
            Image = image
        });
    }

    /// <summary>A file the agent read, and the size of what the read brought back: files read again are a map's to spare.</summary>
    protected void FileRead(string path, int characters)
    {
        var (times, before) = _reads.GetValueOrDefault(path);
        _reads[path] = (times + 1, before + characters);
    }

    /// <summary>A tool call that came back with an error; the end of a long error is where its cause usually is.</summary>
    protected void ToolFailed(string name, string error, List<AgentEvent> events)
    {
        _toolErrors++;
        var text = error.Trim();
        events.Add(new AgentEvent(AgentEventKind.ToolFailed, text.Length <= 2000 ? text : text[^2000..], name));
    }

    /// <summary>
    /// Tokens and money as the provider's final events tell them, with the parts of them it tells apart (input written
    /// to the cache, output that was thinking); a run of several turns adds them up.
    /// </summary>
    protected void Spent(long input, long cached, long output, decimal? cost, long? cacheWrites = null, long? reasoning = null)
    {
        _inputTokens = (_inputTokens ?? 0) + input;
        _cachedTokens = (_cachedTokens ?? 0) + cached;
        _outputTokens = (_outputTokens ?? 0) + output;
        if (cost is { } usd) _cost = (_cost ?? 0) + usd;
        if (cacheWrites is { } written) _cacheWrites = (_cacheWrites ?? 0) + written;
        if (reasoning is { } thought) _reasoning = (_reasoning ?? 0) + thought;
    }

    /// <summary>
    /// The session's context now, a level that the run's last word keeps; the model's window when the provider names it
    /// (a window once told stays).
    /// </summary>
    protected void Context(long tokens, long? window, List<AgentEvent> events)
    {
        ContextTokens = tokens;
        _contextWindow = window ?? _contextWindow;
        events.Add(new AgentEvent(AgentEventKind.Context, "Context") { ContextTokens = tokens, ContextWindow = window });
    }

    /// <summary>
    /// Whether a shell command only looks around: the first program it runs, after any change of folder, finds, lists
    /// or reads files. Codex searches only this way; Claude Code mostly with its own tools.
    /// </summary>
    internal static bool IsSearchCommand(string command) => FirstCommand(command) is { } words &&
        (ProgramOf(words[0]).Equals("git", StringComparison.OrdinalIgnoreCase)
            ? words.Length > 1 && words[1] is "grep" or "ls-files"
            : SearchPrograms.Contains(ProgramOf(words[0])));

    /// <summary>Whether a shell command runs code (a script, a build, a test): work, as an edit is, not a look around.</summary>
    internal static bool RunsCode(string command) => FirstCommand(command) is { } words && CodeRunners.Contains(ProgramOf(words[0]));

    /// <summary>
    /// The words of the first command a shell line runs, past changes of folder, variables set for it and a time limit
    /// (<c>timeout 280 node check.mjs</c>).
    /// </summary>
    private static string[]? FirstCommand(string command)
    {
        foreach (var part in CommandSeparators().Split(command))
        {
            var words = part.Trim().TrimStart('(', '&', '@', '$').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(word => Assignment().IsMatch(word))
                .ToArray();
            if (words.Length > 2 && ProgramOf(words[0]).Equals("timeout", StringComparison.OrdinalIgnoreCase)) words = words[2..];
            if (words.Length == 0 || FolderChanges.Contains(ProgramOf(words[0]))) continue;
            return words;
        }

        return null;
    }

    private static string ProgramOf(string word) => Path.GetFileNameWithoutExtension(word.Trim('"', '\''));

    internal static long Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    protected static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>One line of a long text, for the step row: the first line, cut to a width a row can show.</summary>
    protected static string Short(string text, int length = 120)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }

    /// <summary>
    /// An MCP call for the step row: its server, its tool and the short words it was given. A server that works through
    /// one tool (ue-mcp's tools, Unreal MCP's call_tool) then shows what each call does: "ue-mcp: tools call level place_actor".
    /// </summary>
    protected static string McpCall(string server, string tool, JsonElement arguments)
    {
        var words = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject()
                .Select(field => field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null)
                .Where(word => word is { Length: > 0 and <= 40 } && !word.Contains('\n'))
            : [];
        return string.Join(' ', words.Prepend($"{server}: {tool}"));
    }

    // Commands one after another: ; && || and new lines. A pipe is not one: what it feeds belongs to the first program.
    [GeneratedRegex(@"&&|\|\||;|\n")]
    private static partial Regex CommandSeparators();

    // A variable set for the command that follows it: MSYS_NO_PATHCONV=1 node a.mjs.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();
}
