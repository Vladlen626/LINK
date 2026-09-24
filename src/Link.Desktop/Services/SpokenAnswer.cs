using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// What to say aloud for a Markdown answer: its opening paragraph, up to three sentences, as plain words.
/// Code, tables, formulas, headings, URLs and markup are skipped and links keep their text; when more follows,
/// a short phrase sends the listener to the screen. <see cref="Language"/> is "ru" or "en".
/// </summary>
public sealed partial record SpokenAnswer(string Text, string Language)
{
    private const int MaxSentences = 3;
    // Sentences after the first are added only while the speech stays short.
    private const int MaxLength = 300;

    public static SpokenAnswer From(string markdown)
    {
        var paragraphs = ReadParagraphs(markdown, out var hasDetails);
        if (paragraphs.Count == 0)
        {
            return IsRussian(markdown)
                ? new SpokenAnswer("Ответ на экране.", "ru")
                : new SpokenAnswer("The answer is on screen.", "en");
        }

        var opening = paragraphs[0];
        var spoken = new List<string>();
        var length = 0;
        foreach (var sentence in opening)
        {
            if (spoken.Count == MaxSentences || (spoken.Count > 0 && length + sentence.Length > MaxLength)) break;
            spoken.Add(sentence);
            length += sentence.Length + 1;
        }

        var text = string.Join(' ', spoken);
        var russian = IsRussian(text);
        if (hasDetails || paragraphs.Count > 1 || spoken.Count < opening.Count)
        {
            text += russian ? " Подробности на экране." : " Details are on screen.";
        }

        return new SpokenAnswer(text, russian ? "ru" : "en");
    }

    /// <summary>Russian answers keep English terms, so a quarter of Cyrillic letters already means Russian.</summary>
    public static bool IsRussian(string text)
    {
        int cyrillic = 0, latin = 0;
        foreach (var ch in text)
        {
            // А–я, Ё, ё
            if (ch is >= 'А' and <= 'я' or 'Ё' or 'ё') cyrillic++;
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
        }

        return cyrillic > 0 && cyrillic * 3 >= latin;
    }

    /// <summary>Speakable paragraphs split into sentences; code, tables and formulas only set <paramref name="hasDetails"/>.</summary>
    private static List<List<string>> ReadParagraphs(string markdown, out bool hasDetails)
    {
        var paragraphs = new List<List<string>>();
        var units = new List<string>();
        var isList = false;
        string? fence = null;
        hasDetails = false;

        void EndParagraph()
        {
            // List items rarely end with a stop; adding one keeps the voice from running them together.
            var sentences = (isList ? units.Select(EndSentence) : [string.Join(' ', units)])
                .SelectMany(SplitSentences)
                .ToList();
            if (sentences.Count > 0) paragraphs.Add(sentences);
            units.Clear();
            isList = false;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (fence is not null)
            {
                var closed = fence == "```"
                    ? line.StartsWith(fence, StringComparison.Ordinal)
                    : line.EndsWith(fence, StringComparison.Ordinal);
                if (closed) fence = null;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("$$", StringComparison.Ordinal))
            {
                EndParagraph();
                hasDetails = true;
                // A formula written on one line closes itself.
                var oneLineFormula = line.StartsWith('$') && line.Length >= 4 && line.EndsWith("$$", StringComparison.Ordinal);
                if (!oneLineFormula) fence = line[..(line.StartsWith('`') ? 3 : 2)];
                continue;
            }

            if (line.StartsWith('|'))
            {
                EndParagraph();
                hasDetails = true;
                continue;
            }

            if (line.Length == 0 || IsHeading(line) || IsRule(line))
            {
                EndParagraph();
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                if (!isList) EndParagraph();
                isList = true;
                units.Add(Clean(item.Groups[1].Value));
                continue;
            }

            if (isList) EndParagraph();
            units.Add(Clean(line.TrimStart('>', ' ')));
        }

        EndParagraph();
        return paragraphs;
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        SentenceBreak().Split(text)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Any(char.IsLetterOrDigit));

    private static string EndSentence(string text) =>
        text.Length > 0 && (char.IsLetterOrDigit(text[^1]) || text[^1] is ')' or '"' or '»' or '”') ? text + "." : text;

    /// <summary>Plain words: voices spell out markup and URLs ("asterisk", "h t t p s") or stumble over pictographs.</summary>
    private static string Clean(string text)
    {
        text = MarkdownLink().Replace(text, "$1");
        text = Url().Replace(text, string.Empty);
        text = Arrow().Replace(text, ", ");
        var plain = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '*' or '`' or '~') continue;
            if (ch == '_')
            {
                plain.Append(' ');
                continue;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse || ch == '️')
            {
                continue;
            }

            plain.Append(ch);
        }

        text = EmptyBrackets().Replace(plain.ToString(), string.Empty);
        text = Spaces().Replace(text, " ");
        return SpaceBeforePunctuation().Replace(text, "$1").Trim();
    }

    private static bool IsHeading(string line)
    {
        var level = line.TakeWhile(ch => ch == '#').Count();
        return level is > 0 and <= 6 && line.Length > level && line[level] == ' ';
    }

    private static bool IsRule(string line) =>
        line.Length >= 3 && (line.All(ch => ch == '-') || line.All(ch => ch == '*') || line.All(ch => ch == '_'));

    // A stop, then a space and a capital, a digit or an opening quote: "т. е." and "1.5" stay whole.
    [GeneratedRegex(@"(?<=[.!?…][""»”)\]]*)\s+(?=[\p{Lu}\d«""“(\[])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)\s]*\)")]
    private static partial Regex MarkdownLink();

    // The last character is not punctuation, so a URL at the end of a sentence leaves its stop behind.
    [GeneratedRegex(@"(?:https?://|www\.)\S*[^\s.,!?;:…)\]""'»”]")]
    private static partial Regex Url();

    [GeneratedRegex(@"\s*(?:->|=>|[←-⇿])\s*")]
    private static partial Regex Arrow();

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([,.!?:;…])(?=\s|$)")]
    private static partial Regex SpaceBeforePunctuation();
}
