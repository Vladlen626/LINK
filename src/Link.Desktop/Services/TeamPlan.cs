using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A step of the lead's team plan: what to do, its kind, the tier the lead asked for (null: Link routes by the kind),
/// why, the files or folders it names, the specialist's role id (null: Link suggests one by the step's words), whether
/// the team pauses after it for the user's OK, and the agent the user named for it (null: the tier's own).
/// </summary>
public sealed record PlannedStep(
    string Text, StepKind Kind, WorkerTier? Worker, string Why, IReadOnlyList<string> Files, string? Role = null, bool Check = false,
    AgentProviderKind? Agent = null);

/// <summary>What the lead decides about a step a worker did, as its review asks.</summary>
public enum ReviewVerdict
{
    /// <summary>Done well enough for the steps after it.</summary>
    Accept,

    /// <summary>The same worker fixes it, told the notes.</summary>
    Redo,

    /// <summary>The worker is out of its depth: a stronger one takes the step, told the notes.</summary>
    Escalate,

    /// <summary>The plan is wrong or the user must decide: the team waits.</summary>
    Stop
}

/// <summary>
/// The lead's verdict on a step and its notes: what to change, or why it stops. The art critic's comes the same way,
/// with its score of the whole, 1-5 (null: none given).
/// </summary>
public sealed record LeadReview(ReviewVerdict Verdict, string Notes, int? Score = null);

/// <summary>
/// The team plan the lead ends its answer with, as its rules ask: a fenced <c>link-plan</c> block of JSON, a list of
/// steps with a kind, a worker tier, a role and why. Read tolerantly: the plan is the lead's draft, and Link routes it anyway.
/// </summary>
public static partial class TeamPlan
{
    /// <summary>More steps than this are not a plan a user can follow; the rest are dropped.</summary>
    public const int MaxSteps = 12;

    /// <summary>
    /// The answer without its last link-plan block, and the steps that block holds; the answer as it is and no steps
    /// when there is none, it does not read or holds no step. A step without a known kind is classified by its words
    /// (<see cref="StepKinds.Of"/>), an unknown worker or a role that is not among <paramref name="roles"/> is none (so is
    /// the art critic: Link's own), a step without words is dropped.
    /// </summary>
    public static (string Text, IReadOnlyList<PlannedStep> Steps) Split(string answer, IReadOnlyList<AgentRole>? roles = null)
    {
        var match = Block().Matches(answer).LastOrDefault();
        if (match is null) return (answer, []);
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("steps", out var steps)) root = steps;
            if (root.ValueKind != JsonValueKind.Array) return (answer, []);
            var planned = root.EnumerateArray().Select(step => Step(step, roles ?? [])).OfType<PlannedStep>().Take(MaxSteps).ToArray();
            return planned.Length == 0 ? (answer, []) : ((answer[..match.Index] + answer[(match.Index + match.Length)..]).Trim(), planned);
        }
        catch (JsonException)
        {
            return (answer, []);
        }
    }

    /// <summary>The request for a team plan, beside Link.exe: only a planning run is told how to plan, not every session.</summary>
    public static string TaskFile => Path.Combine(AppContext.BaseDirectory, "harness", "team-task.md");

    /// <summary>How every step's prompt ends, in a new session or a resumed one: the next specialist reads the handover.</summary>
    public const string StepEnd =
        "Do only this step; Link checks the result. End your answer with one line `Handed over: …`: paths, asset, " +
        "skeleton and animation names, what the next specialist needs.";

    /// <summary>
    /// What the lead is asked when the task is planned for a team: the task, the request for a plan (the text of
    /// <see cref="TaskFile"/>), the specialists it can give a step to (all but the art critic: Link's own), and what
    /// workers did in this project before (<see cref="WorkerEvidence.Summary"/>).
    /// </summary>
    public static string Prompt(string task, string request, IReadOnlyList<string> evidence, IReadOnlyList<AgentRole>? roles = null) =>
        task.Trim() + "\n\n" + AgentRules.WithoutNotes(request) + Specialists(roles) +
        "\n\nWhat workers did in this project before:" +
        (evidence.Count == 0 ? " nothing yet" : "\n" + string.Join('\n', evidence.Select(line => "- " + line)));

    /// <summary>
    /// What the lead is asked when the user re-plans the steps left: the user's words, where the plan stands and what
    /// its steps handed over, that the steps the team is through before the current one stay, and the request for a plan
    /// (<paramref name="request"/>; null for the lead's own session, which knows it, though the block's shape is told
    /// again). Its link-plan block holds the steps from the current one on.
    /// </summary>
    public static string ReplanPrompt(string words, AgentTaskRecord record, string? request, IReadOnlyList<AgentRole>? roles = null)
    {
        var kept = record.Team.TakeWhile(TeamRunner.Over).Count();
        var handed = record.Team.Select((step, index) => (step, index))
            .Where(each => !string.IsNullOrWhiteSpace(each.step.HandedOver))
            .Select(each => $"- Step {each.index + 1}: {each.step.HandedOver!.Trim()}").ToArray();
        var text = new System.Text.StringBuilder("The user asks you, the team's lead, to re-plan the steps left. Read what you need, change nothing.")
            .Append("\n\n## The user\n").Append(words.Trim())
            .Append("\n\n## The plan as it stands\n").Append(string.Join('\n', TeamRunner.StateLines(record)));
        if (handed.Length > 0) text.Append("\n\n## Handed over so far\n").Append(string.Join('\n', handed));
        text.Append("\n\n").Append(kept == 0
            ? "No step is done yet: the new plan replaces the whole plan."
            : $"{(kept == 1 ? "Step 1 is done and stays" : $"Steps 1–{kept} are done and stay")}. The new plan replaces the rest: " +
              $"plan only the steps from step {kept + 1} on.");
        text.Append(request is null
            ? "\nEnd your answer with one ```link-plan block of those steps, as before (step, kind, worker, agent, role, check, why, files), nothing after it."
            : "\n\n" + AgentRules.WithoutNotes(request).Trim() + Specialists(roles));
        return text.ToString();
    }

    // The specialists a step can be given to, all but the art critic (Link's own); nothing when the workspace has none.
    private static string Specialists(IReadOnlyList<AgentRole>? roles) =>
        roles?.Where(StepRole).ToArray() is { Length: > 0 } specialists
            ? "\n\nSpecialists for a step's \"role\":\n" + string.Join('\n', specialists.Select(role => $"- {role.Id}: {role.Name}. {role.Description}"))
            : string.Empty;

    /// <summary>The request for a review of a step, beside Link.exe like <see cref="TaskFile"/>.</summary>
    public static string ReviewFile => Path.Combine(AppContext.BaseDirectory, "harness", "team-review.md");

    /// <summary>
    /// What a lead's session that has given a verdict before is told instead of the whole request: it knows how. The
    /// block's shape is told again, should the CLI have compacted the session since.
    /// </summary>
    public const string ReviewAgain =
        "Review this step as you did the last one: change nothing, judge it against your plan, and end your answer with one " +
        "```link-review block holding {\"verdict\": \"accept|redo|escalate|stop\", \"notes\": \"...\"}, nothing after it.";

    /// <summary>
    /// What a judge's session (the lead's review, the art critic) is told once when its answer ended with no verdict that
    /// reads: the block alone, before the user is asked. A run told this that still gives none leaves the step to the user.
    /// </summary>
    public const string VerdictOnly =
        "Your answer ended without a ```link-review block Link can read. Answer with only that block, your verdict on what you " +
        "just judged, as your instructions show it ({\"verdict\": \"accept|redo|escalate|stop\", \"notes\": \"...\"}, with " +
        "\"score\" where they ask for one), nothing before or after it.";

    /// <summary>
    /// What the lead's session is asked when a worker has done a step: the request (the text of <see cref="ReviewFile"/>;
    /// null for a session that has given a verdict before), then the step, the worker's answer, the check and what
    /// changed: the diff when there is one, of the worker's run alone where <paramref name="ownDiff"/> (after the lead's
    /// notes, what changed since them), else the files to read.
    /// </summary>
    public static string ReviewPrompt(string? request, int number, TeamStep step, string answer, string? check, IReadOnlyList<string> files,
        string? diff, bool ownDiff = false)
    {
        var text = new System.Text.StringBuilder(request is null ? ReviewAgain : AgentRules.WithoutNotes(request).Trim())
            .Append($"\n\n## Step {number}, done by {step.Worker}\n").Append(step.Text.Trim())
            .Append("\n\n## Its answer\n").Append(answer.Trim().Length == 0 ? "(none)" : answer.Trim())
            .Append("\n\n## The project's check\n").Append(check ?? "none is set up")
            .Append("\n\n## Changed files\n").Append(files.Count == 0 ? "(none)" : string.Join('\n', files.Select(file => "- " + file)));
        text.Append(diff is null
            ? "\n\nNo diff: the workspace has no version control Link can ask. Read the changed files."
            : ownDiff
            ? $"\n\n## {(step.Redos > 0 ? "What the worker changed after your notes" : "What the worker changed")} (the diff of its run)\n```diff\n{diff.Trim()}\n```"
            : $"\n\n## The diff against the last commit (earlier steps' changes to these files show too)\n```diff\n{diff.Trim()}\n```");
        return text.ToString();
    }

    /// <summary>What a worker's own session is told when the lead sends its step back: the notes, and to fix in place.</summary>
    public static string RedoPrompt(string notes) =>
        $"The team lead reviewed your step and asks for this:\n{notes.Trim()}\nFix it in place: your changes are in the workspace. {StepEnd}";

    /// <summary>
    /// The answer without its last link-review block, and the verdict that block holds, with its score clamped to 1-5
    /// when it gives one; the answer as it is and null when there is none or it does not read.
    /// </summary>
    public static (string Text, LeadReview? Review) SplitReview(string answer)
    {
        var match = ReviewBlock().Matches(answer).LastOrDefault();
        if (match is null) return (answer, null);
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            if (json.RootElement.ValueKind != JsonValueKind.Object || ByName<ReviewVerdict>(AgentQuestion.Text(json.RootElement, "verdict")) is not { } verdict)
            {
                return (answer, null);
            }

            var text = (answer[..match.Index] + answer[(match.Index + match.Length)..]).Trim();
            return (text, new LeadReview(verdict, AgentQuestion.Text(json.RootElement, "notes")?.Trim() ?? string.Empty, Score(json.RootElement)));
        }
        catch (JsonException)
        {
            return (answer, null);
        }
    }

    /// <summary>
    /// The answer without Link's blocks (link-plan, link-review, and a training's link-lessons and link-exam), one cut
    /// short at the end included: the plan and the lessons are the plan card and a verdict a line of the thread, not
    /// words to read.
    /// </summary>
    public static string WithoutBlocks(string answer) => AnyBlock().Replace(answer, string.Empty).Trim();

    /// <summary>
    /// A step's first sentence, the line the plan card, the hand-overs and the other workers see, and the rest of its
    /// text. A first sentence longer than <see cref="HeadlineLength"/> is cut, and then the rest is the whole text.
    /// </summary>
    public static (string Title, string Body) Headline(string text)
    {
        text = text.Trim();
        var end = SentenceEnd().Match(text) is { Success: true } match ? match.Index : text.Length;
        var title = text[..end].Trim();
        if (title.Length <= HeadlineLength) return (title, text[end..].Trim());
        var space = title.LastIndexOf(' ', HeadlineLength - 1);
        return (title[..(space > 40 ? space : HeadlineLength - 1)] + "…", text);
    }

    private const int HeadlineLength = 160;

    /// <summary>
    /// What a step's answer hands over to the next specialist, as <see cref="StepEnd"/> asks: the text of its last
    /// <c>Handed over:</c> line. Null when it has none.
    /// </summary>
    public static string? HandedOver(string answer) =>
        HandOverLine().Matches(answer).LastOrDefault()?.Groups[1].Value.Trim().TrimEnd('*', '_').Trim() is { Length: > 0 } text ? text : null;

    private static PlannedStep? Step(JsonElement element, IReadOnlyList<AgentRole> roles)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString()?.Trim() is { Length: > 0 } text:
                return new PlannedStep(text, StepKinds.Of(text), null, string.Empty, []);
            case JsonValueKind.Object when AgentQuestion.Text(element, "step")?.Trim() is { Length: > 0 } text:
                return new PlannedStep(
                    text,
                    PlanKind(AgentQuestion.Text(element, "kind")) ?? StepKinds.Of(text),
                    ByName<WorkerTier>(AgentQuestion.Text(element, "worker")),
                    AgentQuestion.Text(element, "why")?.Trim() ?? string.Empty,
                    Files(element),
                    roles.FirstOrDefault(role => StepRole(role) && string.Equals(role.Id, AgentQuestion.Text(element, "role")?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id,
                    Check(element),
                    Agent(AgentQuestion.Text(element, "agent")));
            default:
                return null;
        }
    }

    // A critique is Link's own judgement of art, never a step a worker is given; nor is the critic a step's specialist.
    private static StepKind? PlanKind(string? name) => ByName<StepKind>(name) is { } kind && kind != StepKind.Critique ? kind : null;

    private static bool StepRole(AgentRole role) => !string.Equals(role.Id, ArtCritic.RoleId, StringComparison.OrdinalIgnoreCase);

    // The agents by the names a lead writes.
    private static AgentProviderKind? Agent(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "claude" or "claude code" or "claudecode" => AgentProviderKind.ClaudeCode,
        "codex" or "gpt" => AgentProviderKind.Codex,
        _ => null
    };

    private static int? Score(JsonElement review) =>
        review.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var value)
            ? Math.Clamp((int)Math.Round(value), 1, 5)
            : null;

    private static bool Check(JsonElement step) =>
        step.TryGetProperty("check", out var check) &&
        (check.ValueKind == JsonValueKind.True || (check.ValueKind == JsonValueKind.String && string.Equals(check.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase)));

    private static string[] Files(JsonElement step)
    {
        if (!step.TryGetProperty("files", out var files)) return [];
        IEnumerable<string?> names = files.ValueKind switch
        {
            JsonValueKind.String => [files.GetString()],
            JsonValueKind.Array => files.EnumerateArray().Where(file => file.ValueKind == JsonValueKind.String).Select(file => file.GetString()),
            _ => []
        };
        return [.. names.Select(name => name?.Trim()).OfType<string>().Where(name => name.Length > 0)];
    }

    /// <summary>The member of an enum by its name in any case; a number or an unknown word is none.</summary>
    private static T? ByName<T>(string? name) where T : struct, Enum =>
        name is null ? null : Enum.GetValues<T>().Cast<T?>().FirstOrDefault(value => string.Equals(value.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"```link-plan[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"```link-review[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ReviewBlock();

    // A kept answer is cut at its length, so the last block may have lost its closing fence.
    [GeneratedRegex(@"```link-(?:plan|review|lessons|exam)[^\n]*\n.*?(?:```|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnyBlock();

    // The end of a step's first sentence: a full stop before a space or a line, or a line break.
    [GeneratedRegex(@"(?<=\.)\s|\n")]
    private static partial Regex SentenceEnd();

    // The line may come as a list item or in bold: "- **Handed over:** Content/Dash.fbx".
    [GeneratedRegex(@"^[ \t>*_\-]*Handed over:[ \t*_]*(\S[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HandOverLine();
}
