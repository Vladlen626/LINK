using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Link.Desktop;

/// <summary>
/// Markdown as document blocks, for ChatGPT's answer and the agents' answers: headings, rules, tables, quotes, lists,
/// code, bold, inline code and web links. Each block comes with its source, so that a streaming answer keeps the blocks
/// that did not change. The brushes are those <paramref name="resources"/> finds, so the theme of its window holds.
/// </summary>
public sealed class MarkdownRenderer(FrameworkElement resources, Action<Uri> openLink)
{
    /// <summary>
    /// Gives each block of <paramref name="text"/> to <paramref name="add"/>, in order, with its source and a way to build
    /// it. A complete fenced block that <paramref name="fence"/> knows by its language (and its code) is built by it.
    /// </summary>
    public void Render(string text, Action<string, Func<Block>> add, Func<string, string, Func<Block>?>? fence = null)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            var start = index;
            var source = () => string.Join('\n', lines, start, index - start);
            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var language = lines[index].TrimStart()[3..].Trim();
                index++;
                var codeLines = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[index++]);
                }

                var closed = index < lines.Length;
                if (closed) index++;
                var code = string.Join('\n', codeLines);
                if (closed && fence?.Invoke(language, code) is { } special)
                {
                    add(source(), special);
                    continue;
                }

                add(source(), () => new Paragraph(new Run(code))
                {
                    Margin = new Thickness(0, 3, 0, 10),
                    Padding = new Thickness(9, 7, 9, 7),
                    Background = Brush("ResponseCodeBrush"),
                    FontFamily = Mono,
                    FontSize = 12.5,
                    LineHeight = 18
                });
                continue;
            }

            if (TryGetHeading(lines[index], out var headingLevel, out var headingText))
            {
                index++;
                add(source(), () =>
                {
                    var heading = new Paragraph
                    {
                        Margin = new Thickness(0, start == 0 ? 0 : 5, 0, 5),
                        FontSize = headingLevel switch { 1 => 17, 2 => 15.5, _ => 14.5 },
                        FontWeight = FontWeights.SemiBold,
                        LineHeight = 22
                    };
                    AddInlineContent(heading.Inlines, headingText);
                    return heading;
                });
                continue;
            }

            if (IsRuleLine(lines[index]))
            {
                index++;
                add(source(), () => new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 11),
                    BorderBrush = Brush("HairlineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1,
                    LineHeight = 1
                });
                continue;
            }

            if (IsTableLine(lines[index]))
            {
                while (index < lines.Length && IsTableLine(lines[index])) index++;
                var rows = lines[start..index];
                add(source(), () => CreateTable(rows));
                continue;
            }

            if (IsQuoteLine(lines[index]))
            {
                while (index < lines.Length && IsQuoteLine(lines[index])) index++;
                var quoteLines = lines[start..index].Select(line => line.TrimStart()[1..].Trim()).Where(line => line.Length > 0).ToArray();
                add(source(), () =>
                {
                    var quote = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 9),
                        Padding = new Thickness(9, 1, 0, 1),
                        BorderBrush = Brush("AquaBrush"),
                        BorderThickness = new Thickness(2.5, 0, 0, 0),
                        Foreground = Brush("MutedBrush")
                    };
                    AddInlineContent(quote.Inlines, string.Join(' ', quoteLines));
                    return quote;
                });
                continue;
            }

            if (TryGetListItem(lines[index], numbered: false, out _) || TryGetListItem(lines[index], numbered: true, out _))
            {
                var numbered = !TryGetListItem(lines[index], numbered: false, out _);
                while (index < lines.Length && TryGetListItem(lines[index], numbered, out _)) index++;
                var items = lines[start..index];
                add(source(), () => CreateList(items, numbered));
                continue;
            }

            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index])) index++;
            var words = string.Join(' ', lines[start..index].Select(line => line.Trim()));
            add(source(), () =>
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
                AddInlineContent(paragraph.Inlines, words);
                return paragraph;
            });
        }
    }

    /// <summary>A line of dashes, stars or underscores: a rule between parts.</summary>
    public static bool IsRuleLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && (trimmed.All(ch => ch == '-') || trimmed.All(ch => ch == '*') || trimmed.All(ch => ch == '_'));
    }

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private Brush Brush(string key) => (Brush)resources.FindResource(key);

    private System.Windows.Documents.List CreateList(IEnumerable<string> lines, bool numbered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 1, 0, 9),
            Padding = new Thickness(0)
        };

        foreach (var line in lines)
        {
            TryGetListItem(line, numbered, out var itemText);
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlineContent(paragraph.Inlines, itemText);
            list.ListItems.Add(new ListItem(paragraph));
        }

        return list;
    }

    private static bool StartsBlock(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        TryGetHeading(line, out _, out _) ||
        IsRuleLine(line) ||
        IsTableLine(line) ||
        IsQuoteLine(line) ||
        TryGetListItem(line, numbered: false, out _) ||
        TryGetListItem(line, numbered: true, out _);

    private static bool IsTableLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 1 && trimmed[0] == '|' && trimmed.IndexOf('|', 1) > 0;
    }

    private static bool IsQuoteLine(string line) => line.TrimStart().StartsWith('>');

    private Paragraph CreateTable(IReadOnlyList<string> lines)
    {
        // Monospace columns keep tables readable without a grid control in the compact overlay.
        var rows = lines
            .Select(SplitTableRow)
            .Where(cells => !cells.All(cell => cell.Length > 0 && cell.All(ch => ch is '-' or ':')))
            .ToList();
        var columns = rows.Max(row => row.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(column => rows.Max(row => column < row.Count ? row[column].Length : 0))
            .ToArray();
        var formatted = rows
            .Select(row => string.Join("  ", Enumerable.Range(0, columns)
                .Select(column => (column < row.Count ? row[column] : string.Empty).PadRight(widths[column]))).TrimEnd())
            .ToList();

        var table = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            Background = Brush("ResponseCodeBrush"),
            FontFamily = Mono,
            FontSize = 12.5,
            LineHeight = 18
        };
        table.Inlines.Add(new Bold(new Run(formatted[0])));
        if (formatted.Count > 1) table.Inlines.Add(new Run("\n" + string.Join('\n', formatted.Skip(1))));
        return table;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return Regex.Split(trimmed, @"(?<!\\)\|")
            .Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal))
            .ToList();
    }

    private static bool TryGetHeading(string line, out int level, out string text)
    {
        var trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            text = string.Empty;
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryGetListItem(string line, bool numbered, out string text)
    {
        var trimmed = line.TrimStart();
        if (!numbered)
        {
            if (trimmed.Length >= 2 && trimmed[1] == ' ' && trimmed[0] is '-' or '*' or '+')
            {
                text = trimmed[2..].Trim();
                return true;
            }
        }
        else
        {
            var separator = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (separator > 0 && trimmed[..separator].All(char.IsDigit))
            {
                text = trimmed[(separator + 2)..].Trim();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    private void AddInlineContent(InlineCollection inlines, string text)
    {
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!TryReadInline(text, index, out var inline, out var next))
            {
                index++;
                continue;
            }

            if (index > plainStart) inlines.Add(new Run(text[plainStart..index]));
            inlines.Add(inline);
            index = plainStart = next;
        }

        if (plainStart < text.Length) inlines.Add(new Run(text[plainStart..]));
    }

    private bool TryReadInline(string text, int index, [NotNullWhen(true)] out Inline? inline, out int next)
    {
        inline = null;
        next = index;
        if (text[index] == '`')
        {
            var end = text.IndexOf('`', index + 1);
            if (end < 0) return false;
            inline = new Run(text[(index + 1)..end])
            {
                Background = Brush("ResponseCodeBrush"),
                FontFamily = Mono,
                FontSize = 12.5
            };
            next = end + 1;
            return true;
        }

        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*')
        {
            var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
            if (end < 0) return false;
            var bold = new Bold();
            AddInlineContent(bold.Inlines, text[(index + 2)..end]);
            inline = bold;
            next = end + 2;
            return true;
        }

        if (text[index] == '[')
        {
            var labelEnd = text.IndexOf(']', index + 1);
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(') return false;
            var targetEnd = text.IndexOf(')', labelEnd + 2);
            if (targetEnd < 0 ||
                !Uri.TryCreate(text[(labelEnd + 2)..targetEnd], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            var link = new Hyperlink
            {
                NavigateUri = uri,
                Foreground = Brush("LinkTextBrush"),
                ToolTip = uri.AbsoluteUri
            };
            AddInlineContent(link.Inlines, text[(index + 1)..labelEnd]);
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                openLink(e.Uri);
            };
            inline = link;
            next = targetEnd + 1;
            return true;
        }

        return false;
    }
}

/// <summary>
/// <c>MarkdownText.Source</c> on a read-only RichTextBox: the text shown as formatted Markdown, rebuilt when it changes.
/// The box is as tall as its text, so the mouse wheel goes on to the list it sits in.
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(MarkdownText), new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not RichTextBox box) return;
        if (e.OldValue is null)
        {
            box.PreviewMouseWheel -= PassWheel;
            box.PreviewMouseWheel += PassWheel;
        }

        var document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 20 };
        // Bound, not copied: a box in a template can get its text before its style, and a copy kept the default black.
        foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.ForegroundProperty })
        {
            document.SetBinding(property, new Binding(property.Name) { Source = box });
        }

        new MarkdownRenderer(box, Open).Render((string?)e.NewValue ?? string.Empty, (_, create) => document.Blocks.Add(create()));
        // The last block's space below would only pad the turn.
        if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        box.Document = document;
    }

    private static void PassWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject box || VisualTreeHelper.GetParent(box) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static void Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser took the link: the text still shows where it goes.
        }
    }
}
