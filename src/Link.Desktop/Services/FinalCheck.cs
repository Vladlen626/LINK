using System.Text;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The check of a whole change once a task's plan is done: a fresh session on the strong level, told to change
/// nothing, reads the diff against the plan, as a verifier beside the one session that did the work. Its findings go
/// back to that session; its own session is never resumed by the user's words.
/// </summary>
public static partial class FinalCheck
{
    // A plan this long is a task big enough for a second pair of eyes; a short one the user reads in a glance.
    private const int PlanSteps = 3;

    public static string RequestFile => Path.Combine(AppContext.BaseDirectory, "harness", "final-check.md");

    /// <summary>
    /// Whether a run's end is the moment for the check: it carried out the task's own plan of three steps or more to the
    /// end, changed files and was not refused by the project's check. A team, a plan first and a judge are not.
    /// </summary>
    public static bool Due(AgentTaskRecord record, AgentRun run) =>
        run.Task is { Judge: false, PlanOnly: false, AnswerFile: null } && run.Result is { Succeeded: true, Stopped: false } &&
        run.Changes.Count > 0 && run.Check is not { Passed: false } && record.Team.Count == 0 &&
        record.Plan.Count >= PlanSteps && record.Plan.All(item => item.Done && item.Worker is null);

    /// <summary>A plan by its steps' words: the same plan is checked once.</summary>
    public static string Key(IReadOnlyList<AgentPlanItem> plan) => string.Join('\n', plan.Select(item => item.Text));

    /// <summary>What the checking session is asked: the request, the goal, the plan, the project's check and the diff.</summary>
    public static string Prompt(string request, string goal, IReadOnlyList<AgentPlanItem> plan, string? check, IReadOnlyList<string> files, string? diff)
    {
        var text = new StringBuilder(AgentRules.WithoutNotes(request)).Append("\n\n## The task\n\n").Append(goal).Append("\n\n## Its plan\n");
        foreach (var item in plan) text.Append("\n- ").Append(item.Text);
        if (check is not null) text.Append("\n\n## The project's check\n\n").Append(check);
        text.Append("\n\n## Changed files\n\n").AppendJoin('\n', files);
        text.Append("\n\n## Diff\n\n").Append(diff is { Length: > 0 } shown ? $"```diff\n{shown}\n```" : "Not available: read the changed files.");
        return text.ToString();
    }

    /// <summary>Whether the check found something to fix, by its last verdict line; null when it gave none.</summary>
    public static bool? NeedsFix(string answer) =>
        Verdict().Matches(answer).LastOrDefault() is { } verdict ? verdict.Groups[1].Value.Equals("fix", StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>The words the task's own session gets: the findings, without the verdict line.</summary>
    public static string Fix(string answer) =>
        "A fresh session checked the whole change against the plan and found:\n\n" + Verdict().Replace(answer, string.Empty).Trim() +
        "\n\nFix what is right. A finding you think is wrong: say why in a line and leave it.";

    [GeneratedRegex(@"^\s*Verdict:\s*(clean|fix)\b.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Verdict();
}
