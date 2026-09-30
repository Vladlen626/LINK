using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class ProjectService
{
    private const int HistorySize = 10;
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

    public void Rename(LinkProject project, string name)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? project.Name : name.Trim();
        if (!Projects.Any(candidate => candidate.Id == project.Id)) return;
        project.Name = cleanName;
        Save();
    }

    public LinkProject Remove(LinkProject project)
    {
        if (Projects.Count <= 1 || !Projects.Remove(project)) return ActiveProject;
        if (ActiveProject.Id == project.Id)
        {
            ActiveProject = Projects[0];
            _settings.Current.ActiveProjectId = ActiveProject.Id;
            _settings.Save();
        }

        Save();
        return ActiveProject;
    }

    public void Save() =>
        AtomicJsonFile.Write(_paths.ProjectsFile, Projects, _json);

    /// <summary>The project's last questions and answers, newest first. They live in its data folder, not in projects.json.</summary>
    public IReadOnlyList<ProjectExchange> ReadHistory(LinkProject project) =>
        AtomicJsonFile.Read<List<ProjectExchange>>(HistoryFile(project), _json) ?? [];

    /// <summary>Puts an answer at the top of the project's history, which keeps the last ten; returns the new history.</summary>
    public IReadOnlyList<ProjectExchange> AddToHistory(LinkProject project, ProjectExchange exchange)
    {
        var history = ReadHistory(project).Prepend(exchange).Take(HistorySize).ToList();
        AtomicJsonFile.Write(HistoryFile(project), history, _json);
        return history;
    }

    private static string HistoryFile(LinkProject project) => Path.Combine(project.DataFolder, "history.json");

    private LinkProject CreateCore(string name)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_paths.Projects, id.ToString("N"));
        Directory.CreateDirectory(folder);
        return new LinkProject { Id = id, Name = name, DataFolder = folder };
    }

    private List<LinkProject> Load()
    {
        var projects = AtomicJsonFile.Read<List<LinkProject>>(_paths.ProjectsFile, _json) ?? [];
        foreach (var project in projects) project.Normalize();
        return projects;
    }
}
