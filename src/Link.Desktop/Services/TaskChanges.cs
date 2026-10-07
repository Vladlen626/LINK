namespace Link.Desktop.Services;

/// <summary>The size of a task's changes for the header of its view.</summary>
public static class TaskChanges
{
    // So many files are named in the hint; a task can change hundreds.
    private const int FilesInHint = 20;

    /// <summary>
    /// "+181 −12" where git counted the lines (<paramref name="numstat"/> is its <c>--numstat</c>: added, removed and file
    /// per line, a dash for a binary file), otherwise "5 files". The hint names the files. Empty text: nothing changed.
    /// </summary>
    public static (string Text, string Hint) Describe(string? numstat, IReadOnlyList<string> files)
    {
        var named = files.Take(FilesInHint).ToList();
        if (files.Count > FilesInHint) named.Add($"… and {files.Count - FilesInHint} more");
        var hint = string.Join('\n', named);
        long added = 0, removed = 0;
        var counted = false;
        foreach (var line in (numstat ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length < 3 || !IsCount(columns[0]) || !IsCount(columns[1])) continue;
            counted = true;
            added += long.TryParse(columns[0], out var plus) ? plus : 0;
            removed += long.TryParse(columns[1], out var minus) ? minus : 0;
        }

        if (counted) return ($"+{added} −{removed}", hint);
        return (files.Count == 0 ? string.Empty : $"{files.Count} {(files.Count == 1 ? "file" : "files")}", hint);
    }

    private static bool IsCount(string column) => column == "-" || (column.Length > 0 && column.All(char.IsAsciiDigit));
}
