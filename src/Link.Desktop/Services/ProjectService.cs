using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class ProjectService
{
    private readonly StoragePaths _paths;
    private readonly SettingsService _settings;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ProjectService(StoragePaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
        Projects = Load();
        if (Projects.Count == 0)
        {
            Projects.Add(CreateCore("General"));
            Save();
        }

        foreach (var project in Projects)
        {
            Directory.CreateDirectory(project.DataFolder);
        }

        ActiveProject = Projects.FirstOrDefault(project => project.Id == settings.Current.ActiveProjectId) ?? Projects[0];
        settings.Current.ActiveProjectId = ActiveProject.Id;
    }

    public List<LinkProject> Projects { get; }
    public LinkProject ActiveProject { get; private set; }

    public LinkProject Create(string name)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
        var project = CreateCore(cleanName);
        Projects.Add(project);
        ActiveProject = project;
        _settings.Current.ActiveProjectId = project.Id;
        Save();
        return project;
    }

    public void SetActive(LinkProject project)
    {
        if (Projects.Any(candidate => candidate.Id == project.Id))
        {
            ActiveProject = project;
            _settings.Current.ActiveProjectId = project.Id;
            _settings.Save();
        }
    }

    public void Save() =>
        File.WriteAllText(_paths.ProjectsFile, JsonSerializer.Serialize(Projects, _json));

    private LinkProject CreateCore(string name)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_paths.Projects, id.ToString("N"));
        Directory.CreateDirectory(folder);
        return new LinkProject { Id = id, Name = name, DataFolder = folder };
    }

    private List<LinkProject> Load()
    {
        try
        {
            if (File.Exists(_paths.ProjectsFile))
            {
                return JsonSerializer.Deserialize<List<LinkProject>>(File.ReadAllText(_paths.ProjectsFile)) ?? [];
            }
        }
        catch (JsonException)
        {
            // Invalid project metadata is replaced with a General project.
        }

        return [];
    }
}
