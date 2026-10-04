using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>A choice the agent offers: what it does, and a line on why or what follows.</summary>
public sealed record AgentOption(string Label, string Description);

/// <summary>
/// A question the agent ends its answer with, as its rules ask: a fenced <c>link-ask</c> block of JSON with the question
/// and a few options. The Task tab shows it as a choice, as the agents' own apps do, and the pick goes back as the reply.
/// </summary>
public sealed partial record AgentQuestion(string Question, IReadOnlyList<AgentOption> Options, bool MultiSelect)
{
    private const int MaxOptions = 6;

    /// <summary>
    /// The answer without its last link-ask block, and the question that block holds; the answer as it is and null
    /// when there is none or it does not read. Claude's own form (<c>questions: [...]</c>) is read as well, its first question.
    /// </summary>
    public static (string Text, AgentQuestion? Question) Split(string answer)
    {
        var match = Block().Matches(answer).LastOrDefault();
        if (match is null) return (answer, null);
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var questions) &&
                questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0)
            {
                root = questions[0];
            }

            if (root.ValueKind != JsonValueKind.Object || Text(root, "question") is not { Length: > 0 } question) return (answer, null);
            var options = root.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(Option).OfType<AgentOption>().Take(MaxOptions).ToArray()
                : [];
            var multiSelect = root.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True;
            var text = (answer[..match.Index] + answer[(match.Index + match.Length)..]).Trim();
            return (text, new AgentQuestion(question.Trim(), options, multiSelect && options.Length > 1));
        }
        catch (JsonException)
        {
            return (answer, null);
        }
    }

    private static AgentOption? Option(JsonElement option) => option.ValueKind switch
    {
        JsonValueKind.String when option.GetString() is { Length: > 0 } label => new AgentOption(label.Trim(), string.Empty),
        JsonValueKind.Object when Text(option, "label") is { Length: > 0 } label =>
            new AgentOption(label.Trim(), Text(option, "description")?.Trim() ?? string.Empty),
        _ => null
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"```link-ask[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Block();
}
