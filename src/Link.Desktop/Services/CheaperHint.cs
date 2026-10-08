using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A hint that the custom model could do a task's work instead of a subscription agent. It only says so, with its reason;
/// the user hands the task over by the button, and nothing switches by itself.
/// </summary>
public static class CheaperHint
{
    /// <summary>The phase of a plan the user has approved: what is left is to carry it out.</summary>
    public const string RunPlan = "RunPlan";

    // So many runs of the custom model for a role, and so large a share of them done, before the project's stats speak.
    private const int FewestRuns = 3;

    /// <summary>
    /// The hint for a task, or null: there is none unless the custom model is set up and the task's agent is another.
    /// A plan to carry out is one reason; the other is the project's own stats, where the custom model did well enough
    /// for the task's role (<paramref name="roleName"/> as the stats name it): at least three runs and two thirds of the
    /// tasks done.
    /// </summary>
    public static string? For(
        AgentTaskRecord record, bool customConfigured, string customName, string roleName, IReadOnlyList<StatsRow> stats, string phase)
    {
        if (!customConfigured || record.LastRun is not { Provider: not AgentProviderKind.Custom }) return null;
        if (phase == RunPlan) return $"{customName} could carry out this plan: it is written; hand it over to save your subscription";

        var own = stats.Where(row =>
            string.Equals(row.Agent, customName, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Role, roleName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var (runs, tasks, done) = (own.Sum(row => row.Runs), own.Sum(row => row.Tasks), own.Sum(row => row.Done));
        return runs >= FewestRuns && done * 3 >= tasks * 2 ? $"{customName}: {done} of {tasks} done for {roleName} here" : null;
    }
}
