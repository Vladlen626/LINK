namespace Link.Desktop.Services;

/// <summary>
/// What the agents keep about a workspace between tasks: the map all of them read (<see cref="ProjectMap"/>) and each
/// model's own notes. Claude Code keeps its notes per folder in its memory folder; Codex keeps none of its own yet.
/// </summary>
public static class ProjectMemory
{
    /// <summary>Claude's notes on the workspace, its index first; none when it keeps no memory of it.</summary>
    public static IReadOnlyList<string> Notes(string workspace)
    {
        if (ClaudeAccounts.MemoryFolders(workspace, null) is not [var folder, ..]) return [];
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.md")
                .OrderBy(file => !Path.GetFileName(file).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The memory as one list: the map, then Claude's notes.</summary>
    public static IReadOnlyList<string> Files(string workspace) =>
        [.. new[] { ProjectMap.Find(workspace) }.OfType<string>(), .. Notes(workspace)];
}
