namespace Link.Desktop.Services;

/// <summary>
/// The project map: a short docs/map.md in the workspace (what is where, how to build and check, naming) that agents
/// read instead of searching the folders. On the user's request an agent looks through the workspace, changing nothing, and
/// answers with the map; Link writes the answer, so that the file is the only change.
/// </summary>
public static class ProjectMap
{
    /// <summary>Where the map lives in a workspace; the agent's rules point there.</summary>
    public const string RelativePath = "docs/map.md";

    /// <summary>The agent's task: a file beside Link.exe, to be changed without a new build.</summary>
    public static string TaskFile => Path.Combine(AppContext.BaseDirectory, "harness", "map-task.md");

    public static string PathIn(string workspace) => Path.Combine(workspace, "docs", "map.md");
}
