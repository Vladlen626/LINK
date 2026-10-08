using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class ProjectService
{
    // The chats in the panel show their questions and answers from here: enough for the threads of a working week.
    private const int HistorySize = 150;
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

    /// <summary>Puts an answer at the top of the project's history, which keeps the last 150; returns the new history.</summary>
    public IReadOnlyList<ProjectExchange> AddToHistory(LinkProject project, ProjectExchange exchange)
    {
        var history = ReadHistory(project).Prepend(exchange).Take(HistorySize).ToList();
        AtomicJsonFile.Write(HistoryFile(project), history, _json);
        return history;
    }

    private static string HistoryFile(LinkProject project) => Path.Combine(project.DataFolder, "history.json");

    /// <summary>
    /// The project's agent tasks, newest first, in its data folder. The sessions an earlier Link kept come as tasks of
    /// one run until the first task is saved.
    /// </summary>
    public IReadOnlyList<AgentTaskRecord> ReadAgentTasks(LinkProject project)
    {
        var tasks = File.Exists(AgentTasksFile(project)) || !File.Exists(AgentSessionsFile(project))
            ? AtomicJsonFile.Read<List<AgentTaskRecord>>(AgentTasksFile(project), _json) ?? []
            : (AtomicJsonFile.Read<List<AgentSessionRecord>>(AgentSessionsFile(project), _json) ?? [])
                .Where(session => session.SessionId.Length > 0 && Enum.IsDefined(session.Provider) && Enum.IsDefined(session.Access))
                .Select(AgentTaskRecord.FromSession)
                .ToList();
        // A record a newer Link or a hand wrote, with something this Link does not know, is skipped.
        return tasks.Where(task => Enum.IsDefined(task.Status) && task.Runs.All(run => Enum.IsDefined(run.Provider))).ToList();
    }

    /// <summary>Puts a task at the top, in place of its older record, and keeps the last <see cref="TasksKept"/>; returns the list.</summary>
    public IReadOnlyList<AgentTaskRecord> SaveAgentTask(LinkProject project, AgentTaskRecord task)
    {
        var tasks = ReadAgentTasks(project).Where(old => old.Id != task.Id).Prepend(task).Take(TasksKept).ToList();
        AtomicJsonFile.Write(AgentTasksFile(project), tasks, _json);
        return tasks;
    }

    private const int TasksKept = 20;

    /// <summary>What the project has learned of its workers, in its data folder; recording into it saves.</summary>
    public WorkerEvidence ReadWorkerEvidence(LinkProject project) => WorkerEvidence.Load(Path.Combine(project.DataFolder, "workers.json"));

    private static string AgentTasksFile(LinkProject project) => Path.Combine(project.DataFolder, "agent-tasks.json");

    private static string AgentSessionsFile(LinkProject project) => Path.Combine(project.DataFolder, "agent-sessions.json");

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
