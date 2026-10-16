using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Remote control: what a paired phone or browser sees and does of the tasks (see RemoteServer). Every call runs on the
// window's thread and goes the way the Studio's own buttons go; each action is written into the task as from its device.
public partial class MainWindow : IRemoteHost
{
    private const int RemoteListSize = 40;
    private const int RemoteEntries = 40;

    public event Action? RemoteChanged;
    public event Action<Guid, string, string>? RemoteAlert;

    public Task<IReadOnlyList<RemoteProject>> ProjectsAsync() => OnWindow<IReadOnlyList<RemoteProject>>(() =>
        _projects.Projects.Select(project => new RemoteProject(project.Id, project.Name, project.Id == _projects.ActiveProject.Id,
            project.WorkspaceRoot is { } root && Directory.Exists(root))).ToArray());

    /// <summary>The tasks Link holds, of every project, and the active project's others: newest first.</summary>
    public Task<IReadOnlyList<RemoteTask>> TasksAsync() => OnWindow<IReadOnlyList<RemoteTask>>(() =>
    {
        var active = _projects.ActiveProject;
        return _works.Select(work => (work.Record, work.Project))
            .Concat(_projects.ReadAgentTasks(active).Where(record => WorkOf(record.Id) is null).Select(record => (record, active)))
            .OrderByDescending(task => task.Item1.UpdatedAt)
            .Take(RemoteListSize)
            .Select(task => RemoteRow(task.Item1, task.Item2))
            .ToArray();
    });

    public Task<RemoteTaskView?> TaskAsync(Guid id) => OnWindow(() =>
    {
        var work = WorkOf(id);
        if ((work?.Record ?? ActiveRecord(id)) is not { } record) return null;
        var question = work?.Question;
        return new RemoteTaskView(
            RemoteRow(record, work?.Project ?? _projects.ActiveProject),
            [.. record.Entries.TakeLast(RemoteEntries).Select(entry => new RemoteEntry(entry.At, entry.Kind.ToString(), entry.Text))],
            question is null ? null : new RemoteQuestion(question.Question, question.Options, question.MultiSelect),
            work?.Decision is { } decision ? new RemoteDecision(decision.Text, decision.Button, decision.CanRemember) : null,
            work?.Limit?.Text,
            work?.NoStopPending,
            record.ChangedFiles,
            UsageText(record).Text,
            work is null ? record.LastRun?.SessionId is not null : work.Waiting is null && (work.Busy || TakesWords(work)));
    });

    /// <summary>A new task as the card would start it with Settings' choices: the agent Link starts with, its everyday level.</summary>
    public Task<string?> StartAsync(RemoteNewTask task, string device) => OnWindow(() =>
    {
        var project = task.ProjectId is { } id ? _projects.Projects.FirstOrDefault(each => each.Id == id) : _projects.ActiveProject;
        if (project is null) return "No such project";
        if (project.WorkspaceRoot is not { } workspace || !Directory.Exists(workspace)) return $"{project.Name} has no workspace folder";
        var settings = _settings.Current.Agent;
        var provider = StartProvider;
        var (model, effort) = settings.Level(provider, AgentLevel.Standard);
        var plan = new AgentPlan(provider, workspace, task.PlanFirst, task.Goal)
        {
            Model = model,
            Effort = effort,
            AutoEffort = settings.AutoEffort,
            AutoApprove = settings.DefaultAccess == AgentAccessChoice.Auto,
            RoleId = ProjectRoles.Suggest(task.Goal, ProjectRoles.In(workspace))?.Id,
            ProjectId = project.Id,
            NoStop = task.NoStop
        };
        DiagnosticsReported?.Invoke($"[Remote] {device}: new task in {project.Name}");
        _ = StartNewTaskAsync(plan, new InputSnapshot { ProjectId = project.Id, ProjectName = project.Name, Text = task.Goal }, project);
        return null;
    });

    /// <summary>Words for a task: a reply after its run, the answer to its question, or a correction while it works.</summary>
    public Task<string?> ReplyAsync(Guid id, string words, string device) => OnRemoteWork(id, device, "words", work =>
    {
        if (work.Waiting is not null) return "The task waits for its folder: stop it first";
        if (!work.Busy && !TakesWords(work)) return "This task has no session to go on with";
        UserSpoke(work);
        ContinueAgent(work, words.Trim());
        return null;
    });

    public Task<string?> DecideAsync(Guid id, bool always, string device) => OnRemoteWork(id, device, "decision", work =>
    {
        if (work.Decision is not { } decision) return "Nothing to decide now";
        UserSpoke(work);
        TakeDecision(work, decision, always);
        return null;
    });

    public Task<string?> StopAsync(Guid id, string device) => OnRemoteWork(id, device, "stop", work =>
    {
        if (work.Waiting is not null) StopWaiting(work);
        else if ((work.Run?.Stop ?? work.Asking?.Stop) is { } stop) stop.Cancel();
        else return "The task is not running";
        return null;
    });

    public Task<string?> SetNoStopAsync(Guid id, bool on, string device) => OnRemoteWork(id, device, on ? "No-stop on" : "No-stop off", work =>
    {
        SetNoStop(work, on);
        return null;
    });

    public Task<RemoteUsage?> UsageAsync() => OnWindow(() => _usage.Last is { } spent ? new RemoteUsage(spent.Line, spent.Hint) : null);

    /// <summary>A task in the remote list: how it stands and what it waits for from the user.</summary>
    private RemoteTask RemoteRow(AgentTaskRecord record, LinkProject project)
    {
        var work = WorkOf(record.Id);
        var waits = work is null ? (record.Status == AgentTaskStatus.NeedsYou ? "your answer" : null)
            : work.Busy ? null
            : work.Question is not null ? "your answer"
            : work.Decision is { } decision ? decision.Button
            : work.Limit is not null ? "a usage limit"
            : work.Waiting is not null ? "its folder"
            : null;
        var provider = work?.Run?.Task.Provider ?? record.LastRun?.Provider;
        return new RemoteTask(record.Id, project.Name, record.Goal, work?.Run is not null ? "Working" : StatusWords(record.Status),
            provider is { } agent ? AgentName(agent) : null, work?.Busy == true, waits, record.NoStop, record.UpdatedAt, ToPreviewText(record.LastWords));
    }

    /// <summary>A task of the active project that Link does not hold now.</summary>
    private AgentTaskRecord? ActiveRecord(Guid id) => _projects.ReadAgentTasks(_projects.ActiveProject).FirstOrDefault(record => record.Id == id);

    /// <summary>
    /// An action on a task, which Link takes up first if it does not hold it (as opening it from the list does, without the
    /// Studio); the task and link.log say which device it came from.
    /// </summary>
    private Task<string?> OnRemoteWork(Guid id, string device, string what, Func<TaskWork, string?> act) => OnWindow(() =>
    {
        var work = WorkOf(id);
        if (work is null)
        {
            if (ActiveRecord(id) is not { } record) return "No such task";
            if (!Directory.Exists(record.Workspace)) return $"The task's folder is gone: {record.Workspace}";
            work = TakeUpTask(record, _projects.ActiveProject);
        }

        DiagnosticsReported?.Invoke($"[Remote] {device}: {what} for «{Shorten(work.Record.Goal, 60)}»");
        var error = act(work);
        if (error is null) work.Record.Add(AgentEntryKind.HandOff, $"From {device}: {what}");
        SaveTask(work);
        ShowTask(work);
        return error;
    });

    private Task<T> OnWindow<T>(Func<T> read) => Dispatcher.InvokeAsync(read).Task;
}
