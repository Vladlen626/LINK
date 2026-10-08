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

    // The line may come as a list item or in bold: "- **Handed over:** Content/Dash.fbx".
    [GeneratedRegex(@"^[ \t>*_\-]*Handed over:[ \t*_]*(\S[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HandOverLine();
}
