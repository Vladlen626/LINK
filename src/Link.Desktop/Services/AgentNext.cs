using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The next step an agent ends an unfinished answer with, as its rules ask: a fenced <c>link-next</c> block of JSON with
/// the step, how hard it is (an effort the CLIs take) and why. The agent that has just worked knows best what the
/// next run needs; <see cref="AutoEffort"/> takes its word, and the thread shows the step.
/// </summary>
public sealed partial record AgentNext(string Step, string? Effort, string Why)
{
    /// <summary>"Next: wire the jump to Enhanced Input · medium": the quiet line under the answer.</summary>
    public string Line => Effort is null ? $"Next: {Step}" : $"Next: {Step} · {Effort}";

    /// <summary>
    /// The answer without its last link-next block, and the step that block holds; the answer as it is and null when there
    /// is none or it does not read. An effort the CLIs do not take is dropped from the step.
    /// </summary>
    public static (string Text, AgentNext? Next) Split(string answer)
    {
        var match = Block().Matches(answer).LastOrDefault();
        if (match is null) return (answer, null);
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || AgentQuestion.Text(root, "next")?.Trim() is not { Length: > 0 } step) return (answer, null);
            var effort = AgentQuestion.Text(root, "effort")?.Trim().ToLowerInvariant();
            var text = (answer[..match.Index] + answer[(match.Index + match.Length)..]).Trim();
            return (text, new AgentNext(step, effort is not null && AutoEffort.IsLevel(effort) ? effort : null,
                AgentQuestion.Text(root, "why")?.Trim() ?? string.Empty));
        }
        catch (JsonException)
        {
            return (answer, null);
        }
    }

    [GeneratedRegex(@"```link-next[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Block();
}
