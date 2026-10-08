using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Tokens apart by what they cost: input the model read fresh, input it read from its cache, and output. Both agents'
/// CLIs report an input that holds its cached part (Claude Code's fresh, cache-written and cache-read input together,
/// Codex's <c>input_tokens</c>), so the fresh part is what is left.
/// </summary>
public readonly record struct TokenCount(long Uncached, long Cached, long Output)
{
    /// <summary>
    /// A run's own tokens. A run that told none has none; a cached part beyond the input (a CLI's slip) is cut to it.
    /// </summary>
    public static TokenCount Of(AgentRunRecord run)
    {
        var input = Math.Max(0, run.InputTokens ?? 0);
        var cached = Math.Clamp(run.CachedTokens ?? 0, 0, input);
        return new TokenCount(input - cached, cached, Math.Max(0, run.OutputTokens ?? 0));
    }

    /// <summary>
    /// "≈ tokens": all of them as input tokens at the usual API price ratios, a cached one costing a tenth and an output
    /// one five times a fresh one. A measure to compare tasks by, not a bill.
    /// </summary>
    public double Equivalent => Uncached + 0.1 * Cached + 5.0 * Output;

    public static TokenCount operator +(TokenCount first, TokenCount second) =>
        new(first.Uncached + second.Uncached, first.Cached + second.Cached, first.Output + second.Output);
}

/// <summary>
/// What one agent spent on a task: its tokens, and per usage window how many percentage points its runs used of the
/// subscription (see <see cref="AgentLimits.Spent"/>; empty where the CLI reported none).
/// </summary>
public sealed record AgentTokens(AgentProviderKind Provider, TokenCount Tokens, IReadOnlyDictionary<string, double> Spent);

/// <summary>
/// One task measured: who did it, what it cost in tokens per agent, <see cref="Equivalent"/> over all agents and
/// <see cref="StrongEquivalent"/> on the strong one (whose subscription is the scarce one), time, runs, the user's
/// <see cref="Steers"/>, escalations to a stronger worker, whether it ended well, and whether Link's check passed
/// (null: none ran). <see cref="Kind"/> is the kind of its goal.
/// </summary>
public sealed record TaskMetric(
    Guid Id,
    TaskMode Mode,
    StepKind Kind,
    IReadOnlyList<AgentTokens> Agents,
    double Equivalent,
    double StrongEquivalent,
    TimeSpan Time,
    int Runs,
    int Steers,
    int Escalations,
    bool Done,
    bool? CheckPassed);

/// <summary>Measures a task from its record: the numbers that tell whether a team costs less than one agent.</summary>
public static class TaskMetrics
{
    /// <summary>
    /// The task's numbers. <paramref name="strong"/> is the agent whose tokens are counted apart (the default agent).
    /// A steer is a word of the user's given while a run was going, as the history stamps it; the words after a run are
    /// replies. The task is done when it ended Done or Finished and its last run did not fail (as the stats count it).
    /// </summary>
    public static TaskMetric Of(AgentTaskRecord task, AgentProviderKind strong)
    {
        var agents = task.Runs
            .GroupBy(run => run.Provider)
            .Select(runs => new AgentTokens(
                runs.Key,
                runs.Aggregate(default(TokenCount), (sum, run) => sum + TokenCount.Of(run)),
                AgentLimits.Total(runs.SelectMany(run => run.Spent))))
            .OrderBy(agent => agent.Provider)
            .ToArray();
        var last = task.LastRun;
        return new TaskMetric(
            task.Id,
            task.Mode,
            StepKinds.Of(task.Goal),
            agents,
            agents.Sum(agent => agent.Tokens.Equivalent),
            agents.Where(agent => agent.Provider == strong).Sum(agent => agent.Tokens.Equivalent),
            TimeSpan.FromTicks(task.Runs.Sum(run => run.Duration.Ticks)),
            task.Runs.Count,
            task.Entries.Count(entry => entry.Kind == AgentEntryKind.You && task.Runs.Any(run => entry.At >= run.StartedAt && entry.At <= run.StartedAt + run.Duration)),
            task.Team.Count(step => step.EscalatedFrom is not null),
            last is not null && task.Status is (AgentTaskStatus.Done or AgentTaskStatus.Finished) && !last.Failed,
            task.LastCheck is null ? null : task.Status != AgentTaskStatus.CheckFailed && last?.Outcome.StartsWith("Check failed", StringComparison.Ordinal) != true);
    }
}
