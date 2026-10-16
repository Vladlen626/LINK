using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// How many steps of one kind one worker did well or badly in a project. A worker is an agent and its model; its level
/// is the model's own business.
/// </summary>
public sealed record WorkerRecord(StepKind Kind, AgentProviderKind Provider, string Model, int Done, int Failed, DateTimeOffset LastAt);

/// <summary>
/// What the project has learned of its workers: per kind of step, who did it well (the run succeeded and Link's check
/// passed, or there was none) and who failed. It lives in the project's data folder (workers.json), and the routing
/// reads it: a worker that keeps failing a kind no longer gets it here, one that keeps succeeding may get it by default.
/// </summary>
public sealed class WorkerEvidence
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string? _file;
    private readonly List<WorkerRecord> _records;

    /// <summary>Evidence kept in memory only.</summary>
    public WorkerEvidence()
    {
        _records = [];
    }

    private WorkerEvidence(string file, List<WorkerRecord> records)
    {
        _file = file;
        _records = records;
    }

    /// <summary>The records of a file; none when it is missing or does not read. <see cref="Record"/> writes back to it.</summary>
    public static WorkerEvidence Load(string file)
    {
        var records = (AtomicJsonFile.Read<List<WorkerRecord>>(file, Json) ?? [])
            .Where(record => record is not null && Enum.IsDefined(record.Kind) && Enum.IsDefined(record.Provider))
            .Select(record => record with { Model = record.Model ?? string.Empty, Done = Math.Max(0, record.Done), Failed = Math.Max(0, record.Failed) })
            .ToList();
        return new WorkerEvidence(file, records);
    }

    public IReadOnlyList<WorkerRecord> Records => _records;

    /// <summary>
    /// What a finished run says of its worker: true it did the step, false it failed it (the run failed, its check did,
    /// or it was stopped in a loop), null nothing: the user's Stop or Steer, a usage limit, a run that did not start.
    /// </summary>
    public static bool? Verdict(AgentRunRecord run)
    {
        if (run.Outcome.StartsWith("Stopped at the usage limit", StringComparison.Ordinal)) return null;
        if (run.Failed || (run.Looped && run.Outcome.StartsWith("Stopped", StringComparison.Ordinal))) return false;
        return run.Outcome.StartsWith("Done", StringComparison.Ordinal) ? true : null;
    }

    /// <summary>Counts a step the worker did well or badly, and saves.</summary>
    public void Record(StepKind kind, Worker worker, bool done, DateTimeOffset? at = null)
    {
        var index = _records.FindIndex(record => Is(record, kind, worker));
        var (doneBefore, failedBefore) = Counts(kind, worker);
        var next = new WorkerRecord(kind, worker.Provider, worker.Model ?? string.Empty, doneBefore + (done ? 1 : 0), failedBefore + (done ? 0 : 1), at ?? DateTimeOffset.Now);
        if (index >= 0) _records[index] = next;
        else _records.Add(next);
        Save();
    }

    /// <summary>How many steps of a kind the worker did well and how many it failed here.</summary>
    public (int Done, int Failed) Counts(StepKind kind, Worker worker) =>
        _records.FirstOrDefault(record => Is(record, kind, worker)) is { } found ? (found.Done, found.Failed) : (0, 0);

    /// <summary>Failed twice or more, and more often than done: the worker no longer gets this kind here.</summary>
    public bool Blocked(StepKind kind, Worker worker) => IsBlocked(Counts(kind, worker));

    /// <summary>Done three times or more and failed at most a quarter as often: it may get this kind by default, a tier lower.</summary>
    public bool Proven(StepKind kind, Worker worker) => IsProven(Counts(kind, worker));

    /// <summary>
    /// What each worker did here, one line per worker for the lead's prompt and the inspector:
    /// <c>Claude Code claude-haiku-5-5: Mechanical 6/6 ✓, Code 1/4 — not given Code here</c>. Done of tried; ✓ proven.
    /// </summary>
    public IReadOnlyList<string> Summary(Func<AgentProviderKind, string> agentName) =>
        [.. _records
            .GroupBy(record => (record.Provider, record.Model))
            .OrderByDescending(worker => worker.Sum(record => record.Done + record.Failed))
            .ThenBy(worker => worker.Key.Provider)
            .ThenBy(worker => worker.Key.Model, StringComparer.Ordinal)
            .Select(worker =>
            {
                var kinds = worker.OrderByDescending(record => record.Done + record.Failed).ThenBy(record => record.Kind).Select(Entry);
                var name = agentName(worker.Key.Provider);
                return $"{(worker.Key.Model.Length > 0 ? $"{name} {worker.Key.Model}" : name)}: {string.Join(", ", kinds)}";
            })];

    private static string Entry(WorkerRecord record)
    {
        var counts = (record.Done, record.Failed);
        return $"{record.Kind} {record.Done}/{record.Done + record.Failed}" +
               (IsProven(counts) ? " ✓" : string.Empty) +
               (IsBlocked(counts) ? $" — not given {record.Kind} here" : string.Empty);
    }

    private static bool IsBlocked((int Done, int Failed) counts) => counts.Failed >= 2 && counts.Done < counts.Failed;

    private static bool IsProven((int Done, int Failed) counts) => counts.Done >= 3 && counts.Failed * 4 <= counts.Done;

    private static bool Is(WorkerRecord record, StepKind kind, Worker worker) =>
        record.Kind == kind && record.Provider == worker.Provider && record.Model == (worker.Model ?? string.Empty);

    private void Save()
    {
        if (_file is null) return;
        try
        {
            AtomicJsonFile.Write(_file, _records, Json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next step's record writes it again.
        }
    }
}
