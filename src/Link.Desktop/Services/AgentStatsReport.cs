using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// One line of a project's stats: how a specialist did on one agent and model across the project's tasks. The numbers
/// are the group's own: <see cref="Tasks"/> that had a run in it, <see cref="Runs"/> of it, the tasks that ended
/// <see cref="Done"/>, the runs that <see cref="Failed"/> (or failed the check), the <see cref="AverageTime"/> and
/// <see cref="AverageTokens"/> (in and out; null when no run told) of a run, and the user's <see cref="Steers"/>.
/// </summary>
public sealed record StatsRow(
    string Role,
    string Agent,
    string? Model,
    int Tasks,
    int Runs,
    int Done,
    int Failed,
    TimeSpan AverageTime,
    long? AverageTokens,
    int Steers);

/// <summary>
/// The project's stats, read from its tasks' records: who did how well, so that the choice of a specialist, an agent
/// and a model rests on more than a feeling. Nothing here is a verdict; a run is counted where it was made.
/// </summary>
public static class AgentStatsReport
{
    private readonly record struct Group(string? RoleId, AgentProviderKind Provider, string? Model);

    /// <summary>
    /// The runs grouped by specialist, agent and model, the busiest group first. A task is in each group it had a run
    /// in; it is Done there when it ended Done or Finished and its last run in the group did not fail. The user's words
    /// on the way (steers) count once per task, in the group of its last run. A run's specialist is the one it ran as
    /// (null: General); <paramref name="roleName"/> and <paramref name="agentName"/> give the names a row shows.
    /// </summary>
    public static IReadOnlyList<StatsRow> For(
        IEnumerable<AgentTaskRecord> tasks, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName) =>
        [.. tasks
            .SelectMany(task => task.Runs.Select(run => (Task: task, Run: run)))
            .GroupBy(each => GroupOf(each.Run))
            .Select(group => Row(group.Key, [.. group], agentName, roleName))
            .OrderByDescending(row => row.Tasks)
            .ThenByDescending(row => row.Runs)
            .ThenBy(row => row.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Agent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)];

    private static Group GroupOf(AgentRunRecord run) => new(run.RoleId, run.Provider, run.Model);

    private static StatsRow Row(
        Group group, (AgentTaskRecord Task, AgentRunRecord Run)[] entries, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName)
    {
        var runs = entries.Select(each => each.Run).ToArray();
        var byTask = entries.GroupBy(each => each.Task.Id).ToArray();
        var done = byTask.Count(task => task.First().Task.Status is (AgentTaskStatus.Done or AgentTaskStatus.Finished) && !task.Last().Run.Failed);
        var steers = byTask
            .Select(task => task.First().Task)
            .Where(task => GroupOf(task.Runs[^1]) == group)
            .Sum(task => task.Entries.Count(entry => entry.Kind == AgentEntryKind.You));
        // A run of an earlier Link has no time to average, and one that told no tokens has none to count.
        var timed = runs.Where(run => run.Duration > TimeSpan.Zero).ToArray();
        var counted = runs.Where(run => run.InputTokens is not null).ToArray();
        return new StatsRow(
            roleName(group.RoleId),
            agentName(group.Provider),
            group.Model,
            byTask.Length,
            runs.Length,
            done,
            runs.Count(run => run.Failed),
            timed.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)timed.Average(run => run.Duration.Ticks)),
            counted.Length == 0 ? null : (long)Math.Round(counted.Average(run => run.InputTokens!.Value + (run.OutputTokens ?? 0))),
            steers);
    }
}
