namespace Link.Desktop.Services;

public sealed class StoragePaths
{
    public StoragePaths()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Link");
        Screenshots = Path.Combine(Root, "Screenshots");
        Recordings = Path.Combine(Root, "Recordings");
        Projects = Path.Combine(Root, "Projects");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Screenshots);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(Projects);
    }

    public string Root { get; }
    public string Screenshots { get; }
    public string Recordings { get; }
    public string Projects { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProjectsFile => Path.Combine(Root, "projects.json");
    public string LastRequestFile => Path.Combine(Root, "last-request.json");
}
