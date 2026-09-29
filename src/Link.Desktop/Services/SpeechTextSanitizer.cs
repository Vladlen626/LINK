using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>Words to say aloud; <see cref="Language"/> is "ru" or "en".</summary>
public sealed record SpokenAnswer(string Text, string Language);

/// <summary>A sentence to say, or a pointer to what stays on screen: <see cref="Note"/> names code, a table or a long list.</summary>
public readonly record struct SpeechUnit(string Text, SpeechNote? Note = null);

public enum SpeechNote
{
    Code,
    Table,
    MoreItems,
    Rest
}

/// <summary>
/// Turns a Markdown answer into what a person reading it out would say: plain sentences. Code, tables and formulas
/// are never read — code and tables become one short pointer to the screen; links keep their text; URLs, citation
/// markers, markup, stack frames and pictographs go; long paths shrink to their file name; a list is read up to
/// <see cref="MaxListItems"/> items. Only the speech changes: the written answer stays as it is.
/// </summary>
public static partial class SpeechTextSanitizer
{
    public const int MaxListItems = 8;
    // A sentence this long is split at a comma or a dash, so its first words sound sooner.
    private const int LongSentence = 220;
    private const int SplitTarget = 160;

    /// <summary>
    /// What to say of an answer, in order. While it streams (<paramref name="final"/> false) the sentence still
    /// being written is left out: "1." may yet become "1.5".
    /// </summary>
    public static List<SpeechUnit> Read(string markdown, bool final)
    {
        var units = new List<SpeechUnit>();
        var lines = new List<string>();
        var isList = false;
        var listItems = 0;
        var notedCode = false;
        var notedTable = false;
        string? fence = null;

        // True when the paragraph added sentences whose last one a streaming answer may still extend.
        bool EndParagraph()
        {
            var sentences = (isList ? lines.Select(EndSentence) : [string.Join(' ', lines)])
                .SelectMany(SplitSentences)
                .Select(sentence => new SpeechUnit(sentence))
                .ToList();
            units.AddRange(sentences);
            var overflow = isList && listItems > MaxListItems;
            if (overflow) units.Add(new SpeechUnit(string.Empty, SpeechNote.MoreItems));
            lines.Clear();
            isList = false;
            listItems = 0;
            return sentences.Count > 0 && !overflow;
        }

        void Note(SpeechNote note, ref bool noted)
        {
            if (noted) return;
            noted = true;
            units.Add(new SpeechUnit(string.Empty, note));
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

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                EndParagraph();
                Note(SpeechNote.Code, ref notedCode);
                fence = "```";
                continue;
            }

            if (line.StartsWith("$$", StringComparison.Ordinal))
            {
                EndParagraph();
                // A formula written on one line closes itself.
                if (!(line.Length >= 4 && line.EndsWith("$$", StringComparison.Ordinal))) fence = "$$";
                continue;
            }

            if (line.StartsWith('|'))
            {
                EndParagraph();
                Note(SpeechNote.Table, ref notedTable);
                continue;
            }

            if (line.Length == 0 || IsHeading(line) || IsRule(line))
            {
                EndParagraph();
                continue;
            }

            if (StackFrame().IsMatch(line)) continue;

            if (ListItem().Match(line) is { Success: true } item)
            {
                if (!isList) EndParagraph();
                isList = true;
                // Items past the limit are not read; one pointer says that more are on screen.
                if (++listItems <= MaxListItems) lines.Add(Clean(item.Groups[1].Value));
                continue;
            }

            if (isList) EndParagraph();
            lines.Add(Clean(line.TrimStart('>', ' ')));
        }

        if (EndParagraph() && !final) units.RemoveAt(units.Count - 1);
        return units;
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

    /// <summary>
    /// Whether text is to be spoken in Russian. A Russian voice reads the English terms of a technical answer, but an
    /// English voice cannot read Cyrillic at all, so a tenth of Cyrillic letters is enough.
    /// </summary>
    public static bool SpeaksRussian(string text)
    {
        int cyrillic = 0, latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'А' and <= 'я' or 'Ё' or 'ё') cyrillic++;
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
        }

        return cyrillic >= 3 && cyrillic * 10 >= latin;
    }

    /// <summary>What a pointer to the screen says.</summary>
    public static string Say(SpeechNote note, bool russian) => (note, russian) switch
    {
        (SpeechNote.Code, true) => "Код — на экране.",
        (SpeechNote.Code, false) => "The code is on screen.",
        (SpeechNote.Table, true) => "Таблица — на экране.",
        (SpeechNote.Table, false) => "The table is on screen.",
        (SpeechNote.MoreItems, true) => "Остальные пункты — на экране.",
        (SpeechNote.MoreItems, false) => "The other items are on screen.",
        (_, true) => "Остальное — на экране.",
        _ => "The rest is on screen."
    };

    /// <summary>Sentences of plain text; one longer than a breath is split at a comma or a dash.</summary>
    public static IEnumerable<string> SplitSentences(string text) =>
        SentenceBreak().Split(text)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Any(char.IsLetterOrDigit))
            .SelectMany(SplitLong);

    private static IEnumerable<string> SplitLong(string sentence)
    {
        while (sentence.Length > LongSentence)
        {
            var cut = PhraseBreak().Matches(sentence[..SplitTarget]).LastOrDefault();
            // Too early a break would leave a scrap; then the sentence is said whole.
            if (cut is null || cut.Index < SplitTarget / 3) break;
            yield return sentence[..(cut.Index + 1)].Trim();
            sentence = sentence[(cut.Index + cut.Length)..].Trim();
        }

        if (sentence.Length > 0) yield return sentence;
    }

    private static string EndSentence(string text) =>
        text.Length > 0 && (char.IsLetterOrDigit(text[^1]) || text[^1] is ')' or '"' or '»' or '”') ? text + "." : text;

    /// <summary>
    /// Plain words: voices spell out markup and URLs ("asterisk", "h t t p s"), stumble over pictographs, and
    /// Gemini takes angle brackets and pipes for its own tags.
    /// </summary>
    private static string Clean(string text)
    {
        text = InlineCode().Replace(text, match => IdentifierJoint().Replace(match.Groups[1].Value, " "));
        text = MarkdownLink().Replace(text, "$1");
        text = Url().Replace(text, string.Empty);
        text = Citation().Replace(text, string.Empty);
        text = FilePath().Replace(text, match => match.Groups["name"].Value);
        text = HtmlTag().Replace(text, " ");
        text = Arrow().Replace(text, ", ");
        var plain = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '*' or '`' or '~' or '<' or '>' or '|' or '\\') continue;
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

        // ChatGPT wraps its source markers in private-use characters; what is left of them is a code word.
        text = ChatGptCitation().Replace(plain.ToString(), string.Empty);
        text = EmptyBrackets().Replace(text, string.Empty);
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

    // Where a long sentence may pause: after a comma, a semicolon or a colon, or at a dash.
    [GeneratedRegex(@"[,;:](?=\s)|\s[—–-](?=\s)")]
    private static partial Regex PhraseBreak();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    // "at Link.Desktop.App.OnStartup(...)" in .NET or Java, "File "x.py", line 3" in Python.
    [GeneratedRegex(@"^(?:at\s+[\w.$<>`\[\]]+\(|File\s+"".*"",\s+line\s+\d+)")]
    private static partial Regex StackFrame();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    // gemini-3.8-flash-lite-tts or voice_reply_speed inside code reads as words.
    [GeneratedRegex(@"(?<=[\p{L}\d])[-_](?=[\p{L}\d])")]
    private static partial Regex IdentifierJoint();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)\s]*\)")]
    private static partial Regex MarkdownLink();

    // The last character is not punctuation, so a URL at the end of a sentence leaves its stop behind.
    [GeneratedRegex(@"(?:https?://|www\.)\S*[^\s.,!?;:…)\]""'»”]")]
    private static partial Regex Url();

    // 【4†source】, [1], [1, 2] and [^3].
    [GeneratedRegex(@"【[^】]*】|\[\^?\d+(?:,\s*\d+)*\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"(?:cite|filecite|navlist)?(?:turn\d+[a-z]+\d+)+", RegexOptions.IgnoreCase)]
    private static partial Regex ChatGptCitation();

    // C:\Users\me\file.txt, ~/x/y.sh or src/Link.Desktop/App.xaml.cs: only the name is said.
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\|~/|/)?(?:[\w.-]+[\\/]){2,}(?<name>[\w.-]+)")]
    private static partial Regex FilePath();

    [GeneratedRegex(@"</?[A-Za-z][^<>\n]{0,40}>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s*(?:->|=>|[←-⇿])\s*")]
    private static partial Regex Arrow();

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([,.!?:;…])(?=\s|$)")]
    private static partial Regex SpaceBeforePunctuation();
}
