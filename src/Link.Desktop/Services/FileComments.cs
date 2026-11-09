namespace Link.Desktop.Services;

/// <summary>
/// A comment the user left in Link's viewer: the file, where in it (a line and the words picked, a point of a picture,
/// or nothing for the whole file) and what they said. <see cref="Spot"/> is the point as a share of the picture's width
/// and height, for its mark.
/// </summary>
public sealed record FileComment(string Path, string Place, string Text, (double X, double Y)? Spot = null);

/// <summary>Comments from the viewer as the task's next words, the way a review of the diff goes to the agent.</summary>
public static class FileComments
{
    // Words picked in a file are quoted up to this, enough for the agent to find them.
    private const int MaxQuote = 120;

    public static string Message(IEnumerable<FileComment> comments)
    {
        var text = new System.Text.StringBuilder("My comments on files, from Link's viewer. Address each one, then say what you changed:\n");
        foreach (var comment in comments.Where(comment => comment.Text.Trim().Length > 0))
        {
            text.Append("\n- ").Append(comment.Path);
            if (comment.Place.Length > 0) text.Append(", ").Append(comment.Place);
            text.Append("\n  ").Append(comment.Text.Trim().ReplaceLineEndings("\n  ")).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Where picked words are: "line 12 · “…”". Without a known <paramref name="line"/> it is looked for in the source,
    /// and left out when the words are not found there as they are.
    /// </summary>
    public static string Picked(string? source, string words, int? line = null)
    {
        var quote = words.Trim().ReplaceLineEndings(" ");
        if (quote.Length > MaxQuote) quote = quote[..MaxQuote].TrimEnd() + "…";
        var quoted = $"“{quote}”";
        return (line ?? LineOf(source, words)) is { } at ? $"line {at} · {quoted}" : quoted;
    }

    /// <summary>
    /// The source line the picked words start on. Rendered Markdown drops its marks, so the first line of the words is
    /// looked for, and only a line found once counts.
    /// </summary>
    public static int? LineOf(string? source, string words)
    {
        var first = words.Trim().Split('\n')[0].Trim();
        if (source is null || first.Length < 3) return null;
        var at = source.IndexOf(first, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(first, at + 1, StringComparison.Ordinal) >= 0) return null;
        return source.AsSpan(0, at).Count('\n') + 1;
    }

    /// <summary>A point of a picture in its own pixels.</summary>
    public static string Point(double x, double y, int width, int height) =>
        $"at {(int)Math.Round(x * width)}, {(int)Math.Round(y * height)} px of {width} × {height}";
}
