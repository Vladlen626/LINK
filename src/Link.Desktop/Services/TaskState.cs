using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The task's state as a short text that Link makes from the task's record, not a model: the goal, what the user
/// decided, where the plan stands, the files, what was checked, what went wrong and the next step. Compact starts a
/// fresh session of the same agent from it, and the inspector shows it. It does not depend on any model.
/// </summary>
public static class TaskState
{
    /// <summary>What a fresh session is told after the state.</summary>
    public const string GoOn =
        "Go on with the task from this state; look at the files touched before changing them; do not redo what is done.";

    /// <summary>
    /// How much each part of a state may take. A state over <see cref="AgentCoordinator.BriefBudget"/> is cut in this
    /// order, each step on top of the ones before: the older problems, the long decisions, the brief, the files. The
    /// goal, the decisions and the next action stay.
    /// </summary>
    private readonly record struct Cut(int Problems, int Decision, int Brief, int Files);

    private static readonly Cut[] Cuts =
    [
        new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
        new(3, int.MaxValue, int.MaxValue, int.MaxValue),
        new(3, 300, int.MaxValue, int.MaxValue),
        new(3, 300, 1500, int.MaxValue),
        new(3, 300, 1500, 30)
    ];

    /// <summary>
    /// The state of the task from its record: <paramref name="numstat"/> is git's lines added and removed per file
    /// where it told them (the record's list of files otherwise), <paramref name="workspace"/> is where the map and the
    /// skills it points to are looked for.
    /// </summary>
    public static string Build(AgentTaskRecord record, string? numstat, string workspace)
    {
        var pointers = Pointers(workspace);
        var next = NextAction(record);
        var state = string.Empty;
        foreach (var cut in Cuts)
        {
            state = Compose(record, numstat, pointers, next, cut);
            if (state.Length <= AgentCoordinator.BriefBudget) break;
        }

        return state;
    }

    /// <summary>The state with the words that a fresh session goes on from it with: the text Compact shows and sends.</summary>
    public static string CompactBrief(AgentTaskRecord record, string? numstat, string workspace) =>
        $"{Build(record, numstat, workspace)}\n\n{GoOn}";

    /// <summary>The line the task's history gets when it is compacted, with what the session held when that is known.</summary>
    public static string Compacted(long? contextTokens) =>
        "Compacted: a fresh session from the task state" + (contextTokens is { } tokens ? $" (was {AgentContext.Tokens(tokens)} tokens)" : string.Empty);

    private static string Compose(AgentTaskRecord record, string? numstat, IReadOnlyList<string> pointers, string next, Cut cut)
    {
        var lines = new List<string> { "# Task state", $"Goal: {record.Goal}" };
        var decisions = Said(record, cut.Decision);
        if (decisions.Length > 0) lines.AddRange(["## Decisions", .. decisions]);

        lines.Add("## Current state");
        lines.AddRange(record.Plan.Count > 0 ? PlanLines(record) : ["No plan written yet."]);

        var files = Files(record, numstat).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (files.Length > 0)
        {
            lines.Add("## Files touched");
            lines.AddRange(files.Take(cut.Files));
            if (files.Length > cut.Files) lines.Add($"… and {files.Length - cut.Files} more");
        }

        lines.Add("## Verified");
        lines.Add(record.LastCheck ?? "Nothing checked by Link yet.");

        var problems = Problems(record).TakeLast(cut.Problems).ToArray();
        if (problems.Length > 0) lines.AddRange(["## Known problems", .. problems]);

        lines.AddRange(["## Next action", next, "## Pointers", .. pointers, "## Brief", Shortened(record.Brief.Trim(), cut.Brief)]);
        return string.Join('\n', lines);
    }

    /// <summary>The user's words on the way as list lines, oldest first, each cut at <paramref name="limit"/> characters.</summary>
    internal static string[] Said(AgentTaskRecord record, int limit = int.MaxValue) =>
        [.. record.Entries.Where(entry => entry.Kind == AgentEntryKind.You).Select(entry => "- " + Shortened(entry.Text, limit))];

    /// <summary>The agent's plan as it last stood, done steps ticked.</summary>
    internal static string[] PlanLines(AgentTaskRecord record) =>
        [.. record.Plan.Select(item => $"- [{(item.Done ? "x" : " ")}] {item.Text}")];

    /// <summary>The files changed so far: git's lines added and removed per file where it told them, else the record's list.</summary>
    internal static string Files(AgentTaskRecord record, string? numstat) =>
        numstat ?? string.Join('\n', record.ChangedFiles.Select(file => "- " + file));

    private static string Shortened(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    /// <summary>
    /// The runs that failed, failed the check or were stopped, oldest first; one the CLI saw loop says so. A run that
    /// ended well after a loop got past it and is no problem.
    /// </summary>
    private static IEnumerable<string> Problems(AgentTaskRecord record) =>
        record.Runs
            // A Stop of the user's own, or at a usage limit, is no problem of the work; one in a loop is.
            .Where(run => run.Outcome.StartsWith("Failed", StringComparison.Ordinal) || run.Outcome.StartsWith("Check failed", StringComparison.Ordinal) ||
                          (run.Outcome.StartsWith("Stopped", StringComparison.Ordinal) && run.Looped))
            .Select(run => $"- {(run.Provider == AgentProviderKind.ClaudeCode ? "Claude Code" : "Codex")} run {AgentLimits.Time(run.StartedAt)}: " +
                           run.Outcome + (run.Looped ? " (stuck in a loop)" : string.Empty));

    /// <summary>The step the last run named for itself, else the plan's first open one.</summary>
    private static string NextAction(AgentTaskRecord record)
    {
        if (record.LastRun is { } last && AgentNext.Split(last.Words).Next is { } step) return step.Step;
        return record.Plan.FirstOrDefault(item => !item.Done)?.Text ?? "Decide from the state above.";
    }

    /// <summary>Where the agent finds more: the project map and the skills, when the workspace has them, and the brief below.</summary>
    private static string[] Pointers(string workspace)
    {
        var pointers = new List<string>();
        if (File.Exists(ProjectMap.PathIn(workspace))) pointers.Add($"- Project map: {ProjectMap.RelativePath}");
        if (ProjectSkills.In(workspace).Count > 0) pointers.Add($"- Skills: {ProjectSkills.RelativeFolder}/");
        pointers.Add("- The brief this task began with is below.");
        return [.. pointers];
    }
}
