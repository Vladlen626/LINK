using System.Text;

namespace Link.Desktop.Services;

public enum DiffLineKind
{
    /// <summary>A hunk's "@@ -10,6 +10,9 @@" line: where the lines below are.</summary>
    Hunk,
    Context,
    Added,
    Removed,

    /// <summary>What a file shows instead of lines: binary, not tracked, cut.</summary>
    Note
}

/// <summary>A line of a file's diff, with its number in the old file and the new one where it has one.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? Old, int? New, string Text);

/// <summary>A changed file of a diff: its path as the diff names it, the lines added and removed, and its lines.</summary>
public sealed record DiffFile(string Path, int Added, int Removed, IReadOnlyList<DiffLine> Lines);

/// <summary>A comment of the user's review: on a line of a file, or on the whole change (no file).</summary>
public sealed record ReviewComment(string? Path, DiffLine? Line, string Text);

/// <summary>
/// The agent's changes as the review window shows them, read from a unified diff (git's, or Perforce's <c>diff -du</c>),
/// and the user's comments on them as the words the agent gets next.
/// </summary>
public static class DiffReview
{
    // A line of code in the message: enough to find it, not the whole of a minified one.
    private const int QuotedLine = 160;

    /// <summary>The files of a unified diff, in its order; lines Link wrote into it (an untracked file, a cut) are notes.</summary>
    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        var files = new List<DiffFile>();
        string? path = null;
        var lines = new List<DiffLine>();
        int added = 0, removed = 0, old = 0, now = 0;
        // The hunk's lines still to come, as its header counts them: a "--- " after them starts the next file.
        int oldLeft = 0, newLeft = 0;
        var inHunk = false;

        void Close()
        {
            if (path is not null) files.Add(new DiffFile(path, added, removed, [.. lines]));
            (path, added, removed, inHunk) = (null, 0, 0, false);
            lines.Clear();
        }

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Close();
                var b = raw.LastIndexOf(" b/", StringComparison.Ordinal);
                path = b >= 0 ? raw[(b + 3)..] : raw["diff --git ".Length..];
                continue;
            }

            if (!inHunk && raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                // Perforce starts a file with its ---, git has said "diff --git" already.
                if (path is null || lines.Count > 0) Close();
                path ??= PathOf(raw[4..], "a/");
                continue;
            }

            if (!inHunk && raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (PathOf(raw[4..], "b/") is { } named && named != "/dev/null") path = named;
                continue;
            }

            if (raw.StartsWith("@@ ", StringComparison.Ordinal) && Hunk(raw) is { } start)
            {
                path ??= "(file)";
                (old, now, oldLeft, newLeft) = (start.Old, start.New, start.OldCount, start.NewCount);
                inHunk = oldLeft + newLeft > 0;
                lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, raw));
                continue;
            }

            if (raw.StartsWith("?? ", StringComparison.Ordinal))
            {
                Close();
                path = raw[3..].Trim();
                lines.Add(new DiffLine(DiffLineKind.Note, null, null, "A new file, not in version control yet"));
                Close();
                continue;
            }

            if (raw.StartsWith("… cut at", StringComparison.Ordinal))
            {
                if (path is null) continue;
                lines.Add(new DiffLine(DiffLineKind.Note, null, null, raw));
                continue;
            }

            if (path is null) continue;
            if (inHunk && raw.Length > 0 && raw[0] is '+' or '-' or ' ')
            {
                var kind = raw[0] switch { '+' => DiffLineKind.Added, '-' => DiffLineKind.Removed, _ => DiffLineKind.Context };
                lines.Add(new DiffLine(kind, kind == DiffLineKind.Added ? null : old++, kind == DiffLineKind.Removed ? null : now++, raw[1..]));
                if (kind == DiffLineKind.Added) added++;
                if (kind == DiffLineKind.Removed) removed++;
                if (kind != DiffLineKind.Added) oldLeft--;
                if (kind != DiffLineKind.Removed) newLeft--;
                inHunk = oldLeft > 0 || newLeft > 0;
            }
            else if (raw.StartsWith("Binary files ", StringComparison.Ordinal) || raw.EndsWith("(binary or metadata change)", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Note, null, null, "A binary file: no lines to show"));
            }
            else if (raw.StartsWith("diff ", StringComparison.Ordinal) || raw.StartsWith("index ", StringComparison.Ordinal))
            {
                // Perforce's "diff -du" can name a file before its ---; git's index line says nothing to a reader.
                inHunk = false;
            }
        }

        Close();
        return files;
    }

    /// <summary>A new file that no diff shows (git does not track it yet): all its lines added.</summary>
    public static DiffFile NewFile(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return new DiffFile(path, lines.Length, 0, [.. lines.Select((line, index) => new DiffLine(DiffLineKind.Added, null, index + 1, line))]);
    }

    /// <summary>
    /// The review as the agent's next words: each comment under the place it is about, the line quoted, the file's in
    /// the diff's order; general comments first.
    /// </summary>
    public static string Message(IEnumerable<ReviewComment> comments)
    {
        var all = comments.Where(comment => comment.Text.Trim().Length > 0).ToArray();
        var text = new StringBuilder("Review of your changes. Address each comment, then say what you changed:\n");
        foreach (var comment in all.Where(comment => comment.Path is null))
        {
            text.Append("\n- ").Append(comment.Text.Trim()).Append('\n');
        }

        foreach (var comment in all.Where(comment => comment.Path is not null))
        {
            text.Append("\n- ").Append(Place(comment)).Append('\n');
            if (comment.Line is { Kind: not DiffLineKind.Note and not DiffLineKind.Hunk } line)
            {
                var code = line.Text.Trim();
                if (code.Length > QuotedLine) code = code[..QuotedLine] + "…";
                text.Append("  `").Append(line.Kind switch { DiffLineKind.Added => "+ ", DiffLineKind.Removed => "- ", _ => "  " })
                    .Append(code.Replace('`', '\'')).Append("`\n");
            }

            text.Append("  ").Append(comment.Text.Trim().Replace("\n", "\n  ")).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Where a comment is: "src/a.cs:42", the old line's number for a removed one.</summary>
    public static string Place(ReviewComment comment) => comment.Line switch
    {
        { Kind: DiffLineKind.Removed, Old: { } old } => $"{comment.Path}:{old} (removed line)",
        { New: { } line } => $"{comment.Path}:{line}",
        _ => comment.Path ?? "General"
    };

    private static (int Old, int OldCount, int New, int NewCount)? Hunk(string line)
    {
        // "@@ -12,7 +12,9 @@ name": where each side starts and how many lines it has; a count left out is one.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[1].StartsWith('-') || !parts[2].StartsWith('+')) return null;
        return Side(parts[1][1..]) is { } old && Side(parts[2][1..]) is { } now ? (old.Start, old.Count, now.Start, now.Count) : null;
    }

    private static (int Start, int Count)? Side(string text)
    {
        var numbers = text.Split(',');
        if (!int.TryParse(numbers[0], out var start)) return null;
        if (numbers.Length == 1) return (start, 1);
        return int.TryParse(numbers[1], out var count) ? (start, count) : null;
    }

    // "a/src/x.cs" or Perforce's "//depot/x.cs\t2024-…" or "C:\ws\x.cs\t…": the path without git's side or the time.
    private static string? PathOf(string text, string side)
    {
        var path = text.Split('\t')[0].Trim();
        if (path.Length == 0) return null;
        return path.StartsWith(side, StringComparison.Ordinal) ? path[side.Length..] : path;
    }
}
