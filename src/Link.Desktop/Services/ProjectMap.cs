namespace Link.Desktop.Services;

/// <summary>
/// The project map: a short docs/map.md in the workspace (what is where, how to build and check, naming) that agents
/// read instead of searching the folders. On the user's request an agent looks through the workspace, changing nothing, and
/// answers with the map; Link writes the answer, so that the file is the only change. One map per project: a repository in
/// the workspace that keeps its own (docs/map.md or the docs' README, in git for the whole team) is the map, and Link's
/// own is neither read nor written.
/// </summary>
public static class ProjectMap
{
    /// <summary>Where the map lives in a workspace; the agent's rules point there.</summary>
    public const string RelativePath = "docs/map.md";

    /// <summary>The agent's task: a file beside Link.exe, to be changed without a new build.</summary>
    public static string TaskFile => Path.Combine(AppContext.BaseDirectory, "harness", "map-task.md");

    public static string PathIn(string workspace) => Path.Combine(workspace, "docs", "map.md");

    /// <summary>The map agents read first: the repository's own, else Link's; null when there is none.</summary>
    public static string? Find(string workspace) => Shared(workspace) ?? IndexIn(workspace);

    /// <summary>The map a repository inside the workspace keeps for its team; null when it keeps none.</summary>
    public static string? Shared(string workspace) =>
        AgentChanges.RepositoriesInside(workspace).Select(repository => IndexIn(Path.Combine(workspace, repository))).OfType<string>().FirstOrDefault();

    /// <summary>The map as the agent reads it, relative to the workspace: "DriverTheSlop/Docs/README.md".</summary>
    public static string Relative(string workspace, string map) => Path.GetRelativePath(workspace, map).Replace('\\', '/');

    // A docs/README.md is the team's way in to its docs: the router of what is where.
    private static string? IndexIn(string folder) =>
        new[] { PathIn(folder), Path.Combine(folder, "docs", "README.md") }.FirstOrDefault(File.Exists);
}
