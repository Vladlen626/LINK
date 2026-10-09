using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What a run is for, which settles how hard its agent should think.</summary>
public enum EffortPhase
{
    /// <summary>The first run of a task.</summary>
    New,

    /// <summary>The user's words after a run ended.</summary>
    Reply,

    /// <summary>The user's words that stopped the run they were given to.</summary>
    Steer,

    /// <summary>The plan the user approved is carried out.</summary>
    RunPlan,

    /// <summary>A new session takes the task again, after a run that got stuck.</summary>
    Restart,

    /// <summary>The other agent takes the task over.</summary>
    HandOff
}

/// <summary>
/// Which effort a run gets when the task is on Auto, and why. A failure raises it; otherwise the agent that has just
/// worked knows best what its next step needs, so its word comes first; then what Link knows of the phase, then the
/// brief. Only the effort is chosen, never the model or the agent.
/// </summary>
public static class AutoEffort
{
    // The efforts of the agents' CLIs by what they spend, lowest first.
    private static readonly string[] Order = ["low", "medium", "high", "xhigh", "max"];

    // Auto never goes above it: max costs too much to be a guess.
    private const string Highest = "xhigh";
    private const int WhyLength = 60;

    /// <summary>
    /// The effort for a run and the reason in words, the first rule that applies. A run since the last good one failed
    /// (<paramref name="failures"/>), or a restart: one step above <paramref name="lastEffort"/>. The effort the agent
    /// asked for in its <paramref name="next"/> step. Carrying out the plan: medium. Words after a run: the same effort.
    /// Otherwise the brief decides, then the role's usual effort (<paramref name="roleEffort"/>), then medium.
    /// <paramref name="efforts"/> are the CLI's own values, lowest first; the effort returned is one of them, the nearest
    /// to what was meant, and null when the list is empty (the CLI's own).
    /// </summary>
    public static (string? Effort, string Reason) Choose(
        EffortPhase phase,
        string brief,
        IReadOnlyList<string> efforts,
        string? lastEffort,
        int failures,
        AgentNext? next,
        string? roleEffort = null)
    {
        var (meant, reason) = Meant(phase, brief, efforts, lastEffort, failures, next, roleEffort);
        return (Nearest(meant, efforts), reason);
    }

    /// <summary>
    /// The effort for a step of a team plan and why: the step's kind as the lead named it, not the words of its text,
    /// which tell what the step touches (a long step mentions renames and comments in passing). Design, an unknown cause
    /// and editor or DCC work high, code, tests, reviews and instances of a generator medium, mechanical edits and text
    /// low; one above that once the step has failed (<paramref name="failures"/>).
    /// </summary>
    public static (string? Effort, string Reason) ForStep(StepKind kind, IReadOnlyList<string> efforts, int failures)
    {
        var meant = kind switch
        {
            StepKind.Design or StepKind.Investigate or StepKind.Asset => "high",
            StepKind.Mechanical or StepKind.Docs => "low",
            _ => "medium"
        };
        var what = kind switch
        {
            StepKind.Asset => "an asset step",
            StepKind.Instance => "an instance step",
            StepKind.Investigate => "an investigation step",
            StepKind.Docs => "a text step",
            _ => $"a {kind.ToString().ToLowerInvariant()} step"
        };
        return failures > 0
            ? (Nearest(OneAbove(meant, efforts), efforts), $"raised: {what} that failed")
            : (Nearest(meant, efforts), $"{what} of the plan");
    }

    /// <summary>Whether <paramref name="effort"/> is one of the efforts the CLIs take.</summary>
    public static bool IsLevel(string effort) => Rank(effort) >= 0;

    /// <summary>
    /// The runs since the task's last good one that failed or failed the check. A stopped run (the user's Stop or Steer,
    /// a usage limit) ends the count like a good one, a loop label on it too: the user stopped it and says what comes
    /// next, and a loop has its own Start over, which raises.
    /// </summary>
    public static int Failures(IReadOnlyList<AgentRunRecord> runs)
    {
        var failures = 0;
        for (var index = runs.Count - 1; index >= 0 && runs[index].Failed; index--) failures++;
        return failures;
    }

    private static (string Effort, string Reason) Meant(
        EffortPhase phase, string brief, IReadOnlyList<string> efforts, string? lastEffort, int failures, AgentNext? next, string? roleEffort)
    {
        if (failures > 0 || phase == EffortPhase.Restart)
        {
            return (OneAbove(lastEffort, efforts),
                phase == EffortPhase.Restart ? "raised: starting over after a stuck run" : "raised after a failed run");
        }

        if (next?.Effort is { } asked && Rank(asked) >= 0)
        {
            var why = next.Why.Trim();
            return (Capped(asked), why.Length == 0 ? "the agent asked" : $"the agent asked: {(why.Length <= WhyLength ? why : why[..(WhyLength - 1)] + "…")}");
        }

        if (phase == EffortPhase.RunPlan) return ("medium", "carrying out the approved plan");
        if (phase is EffortPhase.Reply or EffortPhase.Steer && lastEffort is { } last && Rank(last) >= 0) return (Order[Rank(last)], "same as the last run");
        return ByBrief(brief, roleEffort);
    }

    /// <summary>
    /// The brief's own words: design or an unknown cause is high, a mechanical change low, and high wins. Only a short
    /// brief is a mechanical change: a longer one does more than what one of its words names.
    /// </summary>
    private static (string Effort, string Reason) ByBrief(string brief, string? roleEffort)
    {
        foreach (var (reason, words) in StepKinds.Hard)
        {
            if (words.FirstOrDefault(word => Stems.Found(brief, word)) is { } hard) return ("high", $"the brief reads as {reason} (\"{hard}\")");
        }

        if (brief.Trim().Length <= MechanicalLength && StepKinds.Mechanical.FirstOrDefault(word => Stems.Found(brief, word)) is { } easy)
        {
            return ("low", $"the brief reads as a mechanical change (\"{easy.Trim()}\")");
        }

        return roleEffort?.Trim().ToLowerInvariant() is { } role && Rank(role) >= 0 ? (Capped(role), "the role's usual effort") : ("medium", "a usual task");
    }

    // About two sentences: "Rename the sprites to the SM_ scheme in Content/Props".
    private const int MechanicalLength = 200;

    /// <summary>The next effort of the CLI's list above the last one, never above <see cref="Highest"/>; high when the last is not known.</summary>
    private static string OneAbove(string? lastEffort, IReadOnlyList<string> efforts)
    {
        if (lastEffort is null || Rank(lastEffort) < 0) return "high";
        var last = Rank(lastEffort);
        var above = efforts.Where(effort => Rank(effort) > last && Rank(effort) <= Rank(Highest)).OrderBy(Rank).FirstOrDefault();
        return above ?? Order[Math.Min(last, Rank(Highest))];
    }

    private static string Capped(string effort) => Order[Math.Min(Rank(effort), Rank(Highest))];

    /// <summary>The CLI's own value for what was meant: that very one, else the nearest by what they spend, the cheaper of two.</summary>
    private static string? Nearest(string meant, IReadOnlyList<string> efforts)
    {
        if (efforts.FirstOrDefault(effort => string.Equals(effort, meant, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        return efforts.Where(effort => Rank(effort) >= 0).OrderBy(effort => Math.Abs(Rank(effort) - Rank(meant))).ThenBy(Rank).FirstOrDefault();
    }

    private static int Rank(string effort) => Array.FindIndex(Order, each => string.Equals(each, effort, StringComparison.OrdinalIgnoreCase));
}
