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

    /// <summary>A worker's next action: its step, which follows the state.</summary>
    internal const string StepBelow = "Your step, below.";

    /// <summary>The next action of the team's lead in a fresh session: its review of a step, which follows the state.</summary>
    internal const string ReviewBelow = "You lead this team, in a fresh session: review the step below.";

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
    /// skills it points to are looked for, <paramref name="agentName"/> names an agent as Settings does.
    /// </summary>
    public static string Build(AgentTaskRecord record, string? numstat, string workspace, Func<AgentProviderKind, string>? agentName = null) =>
        Build(record, numstat, workspace, agentName, NextAction(record));

    private static string Build(AgentTaskRecord record, string? numstat, string workspace, Func<AgentProviderKind, string>? agentName, string next)
    {
        agentName ??= kind => kind.DefaultName();
        var pointers = Pointers(workspace);
        var state = string.Empty;
        foreach (var cut in Cuts)
        {
            state = Compose(record, numstat, pointers, next, cut, agentName);
            if (state.Length <= AgentCoordinator.BriefBudget) break;
        }

        return state;
    }

    /// <summary>The state with the words that a fresh session goes on from it with: the text Compact shows and sends.</summary>
    public static string CompactBrief(AgentTaskRecord record, string? numstat, string workspace, Func<AgentProviderKind, string>? agentName = null) =>
        $"{Build(record, numstat, workspace, agentName)}\n\n{GoOn}";

    /// <summary>
    /// What a fresh session is told when Link compacts by itself: the task's state with the words to go on from it, then
    /// the <paramref name="words"/> the old session was about to get (the user's reply, or that the limit has reset).
    /// </summary>
    public static string CompactContinue(AgentTaskRecord record, string? numstat, string workspace, string words, Func<AgentProviderKind, string>? agentName = null) =>
        $"{CompactBrief(record, numstat, workspace, agentName)}\n\n{words.Trim()}";

    /// <summary>
    /// What a worker's new session is told for its step of a team plan: the task's state, what the steps before handed
    /// over, then the step with the files the plan names. A step an earlier worker failed carries what that worker ended
    /// in, so that the next does not repeat it, and one the lead or the art critic sent back their notes. The step's text
    /// comes once, at the end.
    /// </summary>
    public static string StepBrief(AgentTaskRecord record, string? numstat, string workspace, TeamStep step, Func<AgentProviderKind, string>? agentName = null)
    {
        var lines = new List<string> { Build(record, numstat, workspace, agentName, StepBelow) };
        lines.AddRange(HandedOver(record, step));
        lines.AddRange(["", "## Your step", step.Text]);
        if (step.Files.Count > 0) lines.Add("files: " + string.Join(", ", step.Files));
        if (step.EscalatedFrom is not null && !string.IsNullOrWhiteSpace(step.Failure)) lines.Add($"The {step.EscalatedFrom} tried this step and failed: {step.Failure.Trim()}");
        if (!string.IsNullOrWhiteSpace(step.ReviewNotes))
        {
            lines.Add($"The team lead reviewed an earlier try of this step, whose changes are in the workspace, and asks for this: {step.ReviewNotes.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(step.CriticNotes))
        {
            lines.Add($"The art critic judged the frames of an earlier try of this step, whose changes are in the workspace, and asks for this: {step.CriticNotes.Trim()}");
        }
        lines.Add(TeamPlan.StepEnd);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// What a fresh session of the team's lead is told before it reviews <paramref name="step"/>, when the lead's own
    /// session has grown or gone cold: the task's state, where the plan's steps stand and what the steps before handed
    /// over. The review follows it.
    /// </summary>
    public static string ReviewBrief(AgentTaskRecord record, string workspace, TeamStep step, Func<AgentProviderKind, string>? agentName = null) =>
        string.Join('\n', [Build(record, null, workspace, agentName, ReviewBelow), .. HandedOver(record, step)]);

    /// <summary>What the steps before <paramref name="step"/> handed over, under a heading of its own; nothing when none said.</summary>
    private static IReadOnlyList<string> HandedOver(AgentTaskRecord record, TeamStep step)
    {
        var position = record.Team.IndexOf(step);
        var handed = record.Team
            .Select((each, index) => (Step: each, Number: index + 1))
            .Where(each => (position < 0 || each.Number <= position) && !string.IsNullOrWhiteSpace(each.Step.HandedOver))
            .Select(each => $"- Step {each.Number} ({each.Step.Worker}): {each.Step.HandedOver!.Trim()}")
            .ToArray();
        return handed.Length > 0 ? ["", "## Handed over by earlier steps", .. handed] : [];
    }

    /// <summary>The line the task's history gets when it is compacted, with what the session held when that is known.</summary>
    public static string Compacted(long? contextTokens) =>
        "Compacted: a fresh session from the task state" + (contextTokens is { } tokens ? $" (was {AgentContext.Tokens(tokens)} tokens)" : string.Empty);

    /// <summary>How many times the task was compacted, as its history says.</summary>
    public static int CompactCount(AgentTaskRecord record) =>
        record.Entries.Count(entry => entry.Kind == AgentEntryKind.HandOff && entry.Text.StartsWith("Compacted:", StringComparison.Ordinal));

    private static string Compose(
        AgentTaskRecord record, string? numstat, IReadOnlyList<string> pointers, string next, Cut cut, Func<AgentProviderKind, string> agentName)
    {
        var lines = new List<string> { "# Task state", $"Goal: {record.Goal}" };
        var decisions = Said(record, cut.Decision);
        if (decisions.Length > 0) lines.AddRange(["## Decisions", .. decisions]);

        lines.Add("## Current state");
        // A team's task stands where its plan's steps stand; a worker's own plan is only of its step.
        lines.AddRange(record.Team.Count > 0 ? TeamRunner.StateLines(record) : record.Plan.Count > 0 ? PlanLines(record) : ["No plan written yet."]);

        var files = Files(record, numstat).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (files.Length > 0)
        {
            lines.Add("## Files touched");
            lines.AddRange(files.Take(cut.Files));
            if (files.Length > cut.Files) lines.Add($"… and {files.Length - cut.Files} more");
        }

        lines.Add("## Verified");
        lines.Add(record.LastCheck ?? "Nothing checked by Link yet.");

        var problems = Problems(record, agentName).TakeLast(cut.Problems).ToArray();
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
    private static IEnumerable<string> Problems(AgentTaskRecord record, Func<AgentProviderKind, string> agentName) =>
        record.Runs
            // A Stop of the user's own, or at a usage limit, is no problem of the work; one in a loop is.
            .Where(run => run.Failed || (run.Outcome.StartsWith("Stopped", StringComparison.Ordinal) && run.Looped))
            .Select(run => $"- {agentName(run.Provider)} run {AgentLimits.Time(run.StartedAt)}: " +
                           run.Outcome + (run.Looped ? " (stuck in a loop)" : string.Empty));

    /// <summary>The step the last run named for itself, else the plan's first open one.</summary>
    private static string NextAction(AgentTaskRecord record)
    {
        if (TeamRunner.Current(record) is { } teamStep) return $"Step {TeamRunner.Number(record, teamStep)} of the team plan: {teamStep.Text}";
        if (record.LastRun is { } last && AgentNext.Split(last.Words).Next is { } step) return step.Step;
        return record.Plan.FirstOrDefault(item => !item.Done)?.Text ?? "Decide from the state above.";
    }

    /// <summary>Where the agent finds more: the project map and the skills, when the workspace has them, and the brief below.</summary>
    private static string[] Pointers(string workspace)
    {
        var pointers = new List<string>();
        if (File.Exists(ProjectMap.PathIn(workspace))) pointers.Add($"- Project map: {ProjectMap.RelativePath}");
        if (ProjectSkills.Own(workspace).Count > 0) pointers.Add($"- Skills: {ProjectSkills.RelativeFolder}/");
        pointers.Add("- The brief this task began with is below.");
        return [.. pointers];
    }
}
