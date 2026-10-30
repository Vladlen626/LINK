using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The agent rules that a script can hold instead of the model's memory (agent-rules.md, "Get stronger"): after a run,
/// the files it changed are checked against them, and what breaks a rule goes with the user's next words, once. No run
/// of its own: it costs no tokens until the user goes on anyway.
/// </summary>
public static partial class HarnessLint
{
    public const int MapLines = 100;
    public const int SkillLines = 150;

    // Every new session reads every skill's description; past this it is the skill's body, not when to use it.
    public const int DescriptionLength = 300;
    // A quoted line of the user's is fine; a section written in another language is not.
    private const int ForeignLines = 3;

    /// <summary>What the changed files break, a short line each; none when they keep the rules.</summary>
    public static IReadOnlyList<string> Check(string workspace, IReadOnlyList<AgentChange> changes)
    {
        var notes = new List<string>();
        foreach (var change in changes.Where(change => change.Kind != "D"))
        {
            var path = change.Path.Replace('\\', '/');
            var file = Path.Combine(workspace, path);
            if (path.Equals("docs/map.md", StringComparison.OrdinalIgnoreCase))
            {
                if (Lines(file) is not { } lines) continue;
                if (lines.Length > MapLines) notes.Add($"`{path}` is {lines.Length} lines: keep it an index of ~{MapLines} short lines, how-tos go into skills.");
                if (Foreign(lines)) notes.Add($"`{path}` has text in another language: write it in English.");
            }
            else if (Skill().IsMatch(path))
            {
                if (Lines(file) is not { } lines) continue;
                if (lines.Length > SkillLines) notes.Add($"`{path}` is {lines.Length} lines: condense it under ~{SkillLines}.");
                var (name, description) = ProjectSkills.Header(string.Join('\n', lines));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description)) notes.Add($"`{path}` needs front matter with `name` and a one-line `description`.");
                else if (description.Length > DescriptionLength)
                {
                    notes.Add($"`{path}`'s description is {description.Length} characters, read by every session: say when to use it in under {DescriptionLength}, the rest goes into the body.");
                }
                if (Foreign(lines)) notes.Add($"`{path}` has text in another language: write it in English.");
            }
            else if (change.Kind == "??" && Leftover().IsMatch(path))
            {
                notes.Add($"`{path}` looks like a check's leftover: temporary files go to the system's temp folder, not the project.");
            }
        }

        return notes;
    }

    /// <summary>The user's words with the notes the agent fixes along the way.</summary>
    public static string Heard(IReadOnlyList<string> notes, string words) =>
        $"{words}\n\nLink checked the files your last run changed against the harness rules; fix these along the way:\n" +
        string.Join('\n', notes.Select(note => "- " + note));

    private static string[]? Lines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Foreign(string[] lines) => lines.Count(line => Cyrillic().IsMatch(line) && !line.TrimStart().StartsWith('>')) > ForeignLines;

    [GeneratedRegex(@"^\.agents/skills/[^/]+/SKILL\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex Skill();

    // Logs, dumps and temp files anywhere; pictures only at the top, where no asset lives.
    [GeneratedRegex(@"(\.(log|tmp|dmp)$)|(^[^/]+\.(png|jpe?g|bmp)$)", RegexOptions.IgnoreCase)]
    private static partial Regex Leftover();

    [GeneratedRegex(@"\p{IsCyrillic}{3,}")]
    private static partial Regex Cyrillic();
}
