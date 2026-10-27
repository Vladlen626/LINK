using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// An agent's Markdown as the HTML Telegram's messages take (parse_mode HTML), so an answer keeps its lines, headings,
/// lists and code there as in Link instead of coming as one block of text. Telegram knows only a few tags and refuses a
/// message whose tags do not close, so every line closes its own and <see cref="Fit"/> cuts without breaking one.
/// </summary>
public static partial class TelegramHtml
{
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static string FromMarkdown(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                while (++index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index].TrimEnd());
                output.Add($"<pre>{Escape(string.Join('\n', code))}</pre>");
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add(string.Empty);
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading) output.Add($"<b>{Inline(heading.Groups[1].Value)}</b>");
            else if (Rule().IsMatch(trimmed) || TableRule().IsMatch(trimmed)) continue;
            else if (Bullet().Match(line) is { Success: true } bullet) output.Add(bullet.Groups[1].Value + "• " + Inline(bullet.Groups[2].Value));
            else if (trimmed.StartsWith('>')) output.Add("▎ " + Inline(trimmed.TrimStart('>', ' ')));
            else output.Add(Inline(line));
        }

        return string.Join('\n', output).Trim('\n');
    }

    /// <summary>
    /// The message cut to <paramref name="max"/> characters, with no tag or entity cut in two and every open tag closed:
    /// Telegram refuses the whole message otherwise.
    /// </summary>
    public static string Fit(string html, int max)
    {
        if (html.Length <= max) return html;
        var cut = html[..(max - 1)];
        if (cut.LastIndexOf('<') > cut.LastIndexOf('>')) cut = cut[..cut.LastIndexOf('<')];
        if (cut.LastIndexOf('&') > cut.LastIndexOf(';')) cut = cut[..cut.LastIndexOf('&')];
        var open = new Stack<string>();
        foreach (Match tag in Tag().Matches(cut))
        {
            if (tag.Groups[1].Length == 0) open.Push(tag.Groups[2].Value);
            else if (open.Count > 0) open.Pop();
        }

        var closed = new StringBuilder(cut).Append('…');
        while (open.Count > 0) closed.Append("</").Append(open.Pop()).Append('>');
        return closed.ToString();
    }

    /// <summary>The message's text without its tags, for when Telegram refuses the HTML.</summary>
    public static string Plain(string html) => System.Net.WebUtility.HtmlDecode(Tag().Replace(html, string.Empty));

    /// <summary>One line's inline Markdown: `code`, **bold** and web links; the rest escaped.</summary>
    private static string Inline(string text)
    {
        var html = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '`' && text.IndexOf('`', index + 1) is var codeEnd and > 0)
            {
                html.Append("<code>").Append(Escape(text[(index + 1)..codeEnd])).Append("</code>");
                index = codeEnd + 1;
            }
            else if (text.AsSpan(index).StartsWith("**") && text.IndexOf("**", index + 2, StringComparison.Ordinal) is var boldEnd and > 0)
            {
                html.Append("<b>").Append(Inline(text[(index + 2)..boldEnd])).Append("</b>");
                index = boldEnd + 2;
            }
            else if (text[index] == '[' && Link().Match(text, index) is { Success: true } link && link.Index == index)
            {
                html.Append("<a href=\"").Append(Escape(link.Groups[2].Value).Replace("\"", "&quot;", StringComparison.Ordinal)).Append("\">")
                    .Append(Escape(link.Groups[1].Value)).Append("</a>");
                index += link.Length;
            }
            else
            {
                html.Append(Escape(text[index].ToString()));
                index++;
            }
        }

        return html.ToString();
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<(/?)(b|code|pre|a)\b[^>]*>")]
    private static partial Regex Tag();
}
