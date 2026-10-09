using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A step of the lead's team plan: what to do, its kind, the tier the lead asked for (null: Link routes by the kind),
/// why, the files or folders it names, the specialist's role id (null: Link suggests one by the step's words), and
/// whether the team pauses after it for the user's OK.
/// </summary>
public sealed record PlannedStep(
    string Text, StepKind Kind, WorkerTier? Worker, string Why, IReadOnlyList<string> Files, string? Role = null, bool Check = false);

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

/// <summary>The lead's verdict on a step and its notes: what to change, or why it stops.</summary>
public sealed record LeadReview(ReviewVerdict Verdict, string Notes);

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
    /// (<see cref="StepKinds.Of"/>), an unknown worker or a role that is not among <paramref name="roles"/> is none, a
    /// step without words is dropped.
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
    /// <see cref="TaskFile"/>), the specialists it can give a step to, and what workers did in this project before
    /// (<see cref="WorkerEvidence.Summary"/>).
    /// </summary>
    public static string Prompt(string task, string request, IReadOnlyList<string> evidence, IReadOnlyList<AgentRole>? roles = null) =>
        task.Trim() + "\n\n" + AgentRules.WithoutNotes(request) +
        (roles is { Count: > 0 } ? "\n\nSpecialists for a step's \"role\":\n" + string.Join('\n', roles.Select(role => $"- {role.Id}: {role.Name}. {role.Description}")) : string.Empty) +
        "\n\nWhat workers did in this project before:" +
        (evidence.Count == 0 ? " nothing yet" : "\n" + string.Join('\n', evidence.Select(line => "- " + line)));

    /// <summary>The request for a review of a step, beside Link.exe like <see cref="TaskFile"/>.</summary>
    public static string ReviewFile => Path.Combine(AppContext.BaseDirectory, "harness", "team-review.md");

    /// <summary>
    /// What the lead's session is asked when a worker has done a step: the request (the text of <see cref="ReviewFile"/>),
    /// then the step, the worker's answer, the check and what changed: the diff when there is one, else the files to read.
    /// </summary>
    public static string ReviewPrompt(string request, int number, TeamStep step, string answer, string? check, IReadOnlyList<string> files, string? diff)
    {
        var text = new System.Text.StringBuilder(AgentRules.WithoutNotes(request).Trim())
            .Append($"\n\n## Step {number}, done by {step.Worker}\n").Append(step.Text.Trim())
            .Append("\n\n## Its answer\n").Append(answer.Trim().Length == 0 ? "(none)" : answer.Trim())
            .Append("\n\n## The project's check\n").Append(check ?? "none is set up")
            .Append("\n\n## Changed files\n").Append(files.Count == 0 ? "(none)" : string.Join('\n', files.Select(file => "- " + file)));
        text.Append(diff is null
            ? "\n\nNo diff: the workspace has no version control Link can ask. Read the changed files."
            : $"\n\n## The diff against the last commit (earlier steps' changes to these files show too)\n```diff\n{diff.Trim()}\n```");
        return text.ToString();
    }

    /// <summary>What a worker's own session is told when the lead sends its step back: the notes, and to fix in place.</summary>
    public static string RedoPrompt(string notes) =>
        $"The team lead reviewed your step and asks for this:\n{notes.Trim()}\nFix it in place: your changes are in the workspace. {StepEnd}";

    /// <summary>
    /// The answer without its last link-review block, and the verdict that block holds; the answer as it is and null when
    /// there is none or it does not read.
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
            return (text, new LeadReview(verdict, AgentQuestion.Text(json.RootElement, "notes")?.Trim() ?? string.Empty));
        }
        catch (JsonException)
        {
            return (answer, null);
        }
    }

    /// <summary>
    /// The answer without its link-plan and link-review blocks, one cut short at the end included: the plan is the plan
    /// card and the verdict a line of the thread, not words to read.
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
                    ByName<StepKind>(AgentQuestion.Text(element, "kind")) ?? StepKinds.Of(text),
                    ByName<WorkerTier>(AgentQuestion.Text(element, "worker")),
                    AgentQuestion.Text(element, "why")?.Trim() ?? string.Empty,
                    Files(element),
                    roles.FirstOrDefault(role => string.Equals(role.Id, AgentQuestion.Text(element, "role")?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id,
                    Check(element));
            default:
                return null;
        }
    }

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
    [GeneratedRegex(@"```link-(?:plan|review)[^\n]*\n.*?(?:```|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnyBlock();

    // The end of a step's first sentence: a full stop before a space or a line, or a line break.
    [GeneratedRegex(@"(?<=\.)\s|\n")]
    private static partial Regex SentenceEnd();

    // The line may come as a list item or in bold: "- **Handed over:** Content/Dash.fbx".
    [GeneratedRegex(@"^[ \t>*_\-]*Handed over:[ \t*_]*(\S[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HandOverLine();
}
