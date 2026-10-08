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

    // Stems of the brief's words, English and Russian; what sounds like design or an unknown cause asks for more.
    private static readonly (string Reason, string[] Words)[] Hard =
    [
        ("a design task", ["architecture", "design a", "new mechanic", "system", "refactor", "rewrite",
            "архитектур", "спроектир", "механик", "систем", "рефактор", "переписа"]),
        ("an unclear failure", ["investigate", "why", "crash", "race", "deadlock", "performance", "memory leak", "unclear",
            "разбер", "почему", "вылета", "падает", "утечк", "производител", "непонятн"])
    ];

    private static readonly string[] Mechanical =
    [
        "rename", "typo", "import ", "imports", "imported", "reimport", "move file", "format", "translate", "comment", "docs", "description", "bump version",
        "переимен", "опечат", "импорт", "перенеси", "форматир", "перевед", "коммент", "описани", "документац"
    ];

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

    /// <summary>Whether <paramref name="effort"/> is one of the efforts the CLIs take.</summary>
    public static bool IsLevel(string effort) => Rank(effort) >= 0;

    /// <summary>
    /// The runs since the task's last good one that failed, failed the check, or were stopped in a loop. A run stopped
    /// without one (the user's Stop or Steer, a usage limit) ends the count like a good one: it already had the raise,
    /// and the next run keeps its effort. A run done after a loop got past it.
    /// </summary>
    public static int Failures(IReadOnlyList<AgentRunRecord> runs)
    {
        var failures = 0;
        for (var index = runs.Count - 1; index >= 0; index--)
        {
            var run = runs[index];
            var stopped = run.Outcome.StartsWith("Stopped", StringComparison.Ordinal);
            if (run.Failed || (stopped && run.Looped))
            {
                failures++;
            }
            else
            {
                break;
            }
        }

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

    /// <summary>The brief's own words: design or an unknown cause is high, a mechanical change low, and high wins.</summary>
    private static (string Effort, string Reason) ByBrief(string brief, string? roleEffort)
    {
        foreach (var (reason, words) in Hard)
        {
            if (words.FirstOrDefault(word => Stems.Found(brief, word)) is { } hard) return ("high", $"\"{hard}\": {reason}");
        }

        if (Mechanical.FirstOrDefault(word => Stems.Found(brief, word)) is { } easy) return ("low", $"\"{easy.Trim()}\": a mechanical change");
        return roleEffort?.Trim().ToLowerInvariant() is { } role && Rank(role) >= 0 ? (Capped(role), "the role's usual effort") : ("medium", "a usual task");
    }

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
