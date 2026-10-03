using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// What the two agents share: find the CLI, run it through <see cref="AgentProcess"/>, turn its JSONL into events and
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

    public string? FindExecutable() => AgentExecutables.Find(Kind, _settings().PathFor(Kind));

    public async Task<AgentProviderStatus> CheckAsync(string? chosenPath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(Kind, chosenPath);
        if (executable is null) return new AgentProviderStatus(null, null, null);
        var version = await QuietAsync(executable, ["--version"], cancellationToken);
        var signedIn = await QuietAsync(executable, SignInCheck, cancellationToken) is { } answer
            ? IsSignedIn(answer)
            : (bool?)null;
        return new AgentProviderStatus(executable, version?.Trim(), signedIn);
    }

    public async Task<AgentResult> RunAsync(AgentTask task, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        var executable = FindExecutable()
            ?? throw new InvalidOperationException($"{Name} is not found. Install it or choose its file in Settings.");
        var parser = CreateParser(task);
        var loops = new AgentLoopDetector(task.Access == AgentAccess.Do && !task.PlanOnly);
        var clock = Stopwatch.StartNew();
        TimeSpan? firstEvent = null;
        var command = new AgentCommand(executable, Arguments(task), task.Workspace, StandardInput(task))
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
            void Report(IReadOnlyList<AgentEvent> events)
            {
                foreach (var agentEvent in events)
                {
                    firstEvent ??= clock.Elapsed;
                    progress.Report(agentEvent);
                    if (loops.Add(agentEvent) is { } loop) progress.Report(loop);
                }
            }

            process = await AgentProcess.RunAsync(
                command,
                line =>
                {
                    _console?.Line(line);
                    lock (turn) Report(parser.Read(line));
                },
                line =>
                {
                    _console?.Line(line, error: true);
                    lock (turn) Report(parser.ReadError(line));
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log($"[Agent] {Name}: could not start ({exception.Message})");
            _console?.Line($"Link could not start it: {exception.Message}", error: true);
            throw;
        }

        var succeeded = !process.Stopped && process.ExitCode == 0 && !parser.Failed;
        var summary = parser.Summary;
        if (!succeeded && !process.Stopped && summary.Length == 0) summary = LastErrorLine(process.Log);
        var result = new AgentResult
        {
            SessionId = parser.SessionId,
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
        foreach (var agentEvent in await Task.Run(() => AfterRun(result), CancellationToken.None)) progress.Report(agentEvent);
        // Never the task's text: it can hold anything the user wrote.
        var outcome = result.Stopped ? "stopped" : result.Succeeded ? "done" : $"failed (exit {result.ExitCode})";
        var first = firstEvent is { } elapsed ? Seconds(elapsed) : "none";
        var kind = task.PlanOnly ? ", plan"
            : task.AnswerFile is { } answer ? $", answer to {answer}"
            : task.AnswerIsSkill ? ", skill"
            : task.AutoApprove ? ", auto"
            : string.Empty;
        // Runs compare only with the same model and effort.
        var setup = (task.Model is { } model ? $", {model}" : string.Empty) + (task.Effort is { } effort ? $", effort {effort}" : string.Empty);
        _log($"[Agent] {Name}, {task.Access}{kind}{setup}: {outcome}; " +
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
        return $"turns {stats.Turns}, tools {stats.ToolCalls}, searches {stats.Searches}{before}, tool errors {stats.ToolErrors}, {tokens}{cost}";
    }

    /// <summary>The CLI's arguments for a task; the task's text itself goes on stdin.</summary>
    public abstract IReadOnlyList<string> Arguments(AgentTask task);

    /// <summary>What the CLI reads on stdin: the task's text, and whatever the provider cannot take as an argument.</summary>
    public virtual string Input(AgentTask task) => task.Prompt;

    /// <summary>Variables the CLI's process gets on top of Link's own.</summary>
    public virtual IReadOnlyDictionary<string, string> EnvironmentVariables(AgentTask task) => new Dictionary<string, string>();

    /// <summary>Whether the CLI finds the workspace's skills (<see cref="ProjectSkills"/>) by itself.</summary>
    protected virtual bool FindsSkills => false;

    /// <summary>What the provider learns after a run from outside its events, such as Codex's rate limits.</summary>
    protected virtual IReadOnlyList<AgentEvent> AfterRun(AgentResult result) => [];

    /// <summary>What the CLI gets on stdin: a new session starts with the agent's rules, a resumed one has them.</summary>
    public string StandardInput(AgentTask task) =>
        task.ResumeSessionId is null ? AgentRules.Before(Input(task), task.Workspace, !FindsSkills) : Input(task);

    public abstract AgentEventParser CreateParser(AgentTask task);

    protected abstract IReadOnlyList<string> SignInCheck { get; }

    protected abstract bool IsSignedIn(string answer);

    private static string LastErrorLine(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("! ", StringComparison.Ordinal))?[2..] ?? string.Empty;

    /// <summary>Runs a short CLI command, such as its version, and returns what it printed; null when it failed.</summary>
    private static async Task<string?> QuietAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = new System.Text.StringBuilder();
        try
        {
            var result = await AgentProcess.RunAsync(
                new AgentCommand(executable, arguments, Path.GetTempPath(), string.Empty),
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

    private static readonly HashSet<string> FolderChanges = new(StringComparer.OrdinalIgnoreCase) { "cd", "Set-Location", "sl", "pushd", "Push-Location" };
    private readonly List<string> _denials = [];
    private readonly List<string> _changedFiles = [];
    private int _toolCalls;
    private int _searches;
    private int _searchesBeforeChange;
    private int _toolErrors;
    private long? _inputTokens;
    private long? _cachedTokens;
    private long? _outputTokens;
    private decimal? _cost;

    public string? SessionId { get; protected set; }
    public string Summary { get; protected set; } = string.Empty;
    public bool Failed { get; protected set; }
    public IReadOnlyList<string> Denials => _denials;
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

    /// <summary>The run's numbers so far; its time is the runner's to add.</summary>
    public AgentStats Stats => new()
    {
        Turns = Turns,
        ToolCalls = _toolCalls,
        Searches = _searches,
        SearchesBeforeChange = _changedFiles.Count > 0 ? _searchesBeforeChange : null,
        ToolErrors = _toolErrors,
        InputTokens = _inputTokens,
        CachedTokens = _cachedTokens,
        OutputTokens = _outputTokens,
        CostUsd = _cost,
        SessionTokens = TokensOfSession && _inputTokens is not null
    };

    protected int Turns { get; set; }

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

    protected void Changed(string path, List<AgentEvent> events)
    {
        _changedFiles.Add(path);
        events.Add(new AgentEvent(AgentEventKind.Artifact, path, path));
    }

    /// <summary>
    /// A tool call, a step of Activity; <paramref name="arguments"/> tell it from another call of the same tool (a long
    /// write is cut), <paramref name="search"/>: it only looks around.
    /// </summary>
    protected void Called(string name, string text, string arguments, bool search, List<AgentEvent> events)
    {
        _toolCalls++;
        if (search)
        {
            _searches++;
            if (_changedFiles.Count == 0) _searchesBeforeChange++;
        }

        events.Add(new AgentEvent(AgentEventKind.Tool, Short(text), name)
        {
            IsSearch = search,
            Arguments = arguments.Length <= 2000 ? arguments : arguments[..2000]
        });
    }

    /// <summary>A tool call that came back with an error; the end of a long error is where its cause usually is.</summary>
    protected void ToolFailed(string name, string error, List<AgentEvent> events)
    {
        _toolErrors++;
        var text = error.Trim();
        events.Add(new AgentEvent(AgentEventKind.ToolFailed, text.Length <= 2000 ? text : text[^2000..], name));
    }

    /// <summary>Tokens and money as the provider's final events tell them; a run of several turns adds them up.</summary>
    protected void Spent(long input, long cached, long output, decimal? cost)
    {
        _inputTokens = (_inputTokens ?? 0) + input;
        _cachedTokens = (_cachedTokens ?? 0) + cached;
        _outputTokens = (_outputTokens ?? 0) + output;
        if (cost is { } usd) _cost = (_cost ?? 0) + usd;
    }

    /// <summary>
    /// Whether a shell command only looks around: the first program it runs, after any change of folder, finds, lists
    /// or reads files. Codex searches only this way; Claude Code mostly with its own tools.
    /// </summary>
    internal static bool IsSearchCommand(string command)
    {
        foreach (var part in CommandSeparators().Split(command))
        {
            var words = part.Trim().TrimStart('(', '&', '@', '$').Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            var program = Path.GetFileNameWithoutExtension(words[0].Trim('"', '\''));
            if (FolderChanges.Contains(program)) continue;
            if (program.Equals("git", StringComparison.OrdinalIgnoreCase)) return words.Length > 1 && words[1] is "grep" or "ls-files";
            return SearchPrograms.Contains(program);
        }

        return false;
    }

    protected static long Number(JsonElement element, string property) =>
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
}
