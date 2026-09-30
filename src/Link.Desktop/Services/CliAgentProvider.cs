using System.Diagnostics;
using System.Text.Json;
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

    protected CliAgentProvider(Func<AgentSettings> settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public abstract AgentProviderKind Kind { get; }
    public abstract string Name { get; }

    public string? FindExecutable() => AgentExecutables.Find(Kind, _settings().PathFor(Kind));

    public async Task<AgentProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
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
        var parser = CreateParser();
        var clock = Stopwatch.StartNew();
        TimeSpan? firstEvent = null;
        var command = new AgentCommand(executable, Arguments(task), task.Workspace, Input(task));
        AgentProcessResult process;
        try
        {
            // stdout and stderr are read on two threads; the parser takes one line at a time.
            var turn = new Lock();
            void Report(IReadOnlyList<AgentEvent> events)
            {
                foreach (var agentEvent in events)
                {
                    firstEvent ??= clock.Elapsed;
                    progress.Report(agentEvent);
                }
            }

            process = await AgentProcess.RunAsync(
                command,
                line =>
                {
                    lock (turn) Report(parser.Read(line));
                },
                line =>
                {
                    lock (turn) Report(parser.ReadError(line));
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log($"[Agent] {Name}: could not start ({exception.Message})");
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
            ChangedFiles = parser.ChangedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        // Never the task's text: it can hold anything the user wrote.
        var outcome = result.Stopped ? "stopped" : result.Succeeded ? "done" : $"failed (exit {result.ExitCode})";
        var first = firstEvent is { } elapsed ? $"{elapsed.TotalSeconds:0.0} s" : "none";
        _log($"[Agent] {Name}, {task.Access}{(task.PlanOnly ? ", plan" : string.Empty)}: {outcome}; " +
             $"session {result.SessionId ?? "none"}, first event {first}, {clock.Elapsed.TotalSeconds:0.0} s, " +
             $"{result.ChangedFiles.Count} files, denials: {(result.Denials.Count == 0 ? "none" : string.Join(", ", result.Denials))}");
        return result;
    }

    /// <summary>The CLI's arguments for a task; the task's text itself goes on stdin.</summary>
    public abstract IReadOnlyList<string> Arguments(AgentTask task);

    /// <summary>What the CLI reads on stdin: the task's text, and whatever the provider cannot take as an argument.</summary>
    public virtual string Input(AgentTask task) => task.Prompt;

    public abstract AgentEventParser CreateParser();

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
/// Reads one provider's JSONL into events and keeps what the result needs. Lines that are not JSON and events it does
/// not know are skipped: a newer CLI may add them.
/// </summary>
public abstract class AgentEventParser
{
    private readonly List<string> _denials = [];
    private readonly List<string> _changedFiles = [];

    public string? SessionId { get; protected set; }
    public string Summary { get; protected set; } = string.Empty;
    public bool Failed { get; protected set; }
    public IReadOnlyList<string> Denials => _denials;
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

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
}
