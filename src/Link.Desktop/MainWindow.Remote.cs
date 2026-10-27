using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Remote control: what the Telegram bot sees and does of the tasks (see TelegramBot). Every call runs on the
// window's thread and goes the way the Studio's own buttons go; each action is written into the task as from its device.
public partial class MainWindow : IRemoteHost
{
    private const int RemoteListSize = 40;
    private const int RemoteEntries = 40;
    private const int RemoteDiff = 500_000;
    private const int RemoteBeforeLines = 8;

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
        var plan = PlanOf(record);
        return new RemoteTaskView(
            RemoteRow(record, work?.Project ?? _projects.ActiveProject),
            [.. record.Entries.TakeLast(RemoteEntries).Select(entry => new RemoteEntry(entry.At, entry.Kind.ToString(), entry.Text))],
            question is null ? null : new RemoteQuestion(question.Question, question.Options, question.MultiSelect),
            work?.Decision is { } decision ? new RemoteDecision(decision.Text, decision.Button, decision.CanRemember, decision.Refuse is not null) : null,
            work?.Limit?.Text,
            work?.NoStopPending,
            record.ChangedFiles,
            UsageText(record).Text,
            work is null ? record.LastRun?.SessionId is not null : work.Waiting is null && (work.Busy || TakesWords(work)),
            work?.Run is { } live ? RemoteNow(live, plan) : null,
            plan,
            RemoteBefore(record, live: work?.Run is not null),
            [.. LatestPages(record).Select(page => new RemoteTaskPage(page.Label, page.Reply?.Label, page.Reply?.Text))]);
    });


    /// <summary>A page served again as its turn's button does, without opening a browser here: the bot's link shows it.</summary>
    public async Task<Uri?> OpenPageAsync(Guid id, int page)
    {
        var (found, workspace) = await OnWindow<(LocalPages.Page?, string?)>(() => (WorkOf(id)?.Record ?? ActiveRecord(id)) is { } record
            ? (LatestPages(record).ElementAtOrDefault(page), record.Workspace)
            : (null, null));
        if (found is null) return null;
        return await LocalPages.ReopenAsync(found, workspace) ?? (await LocalPages.IsUpAsync(found.Address) ? found.Address : null);
    }

    public async Task<string?> ApplyPageAsync(Guid id, int page, string device)
    {
        var (found, workspace) = await OnWindow<(LocalPages.Page?, string?)>(() => (WorkOf(id)?.Record ?? ActiveRecord(id)) is { } record
            ? (LatestPages(record).ElementAtOrDefault(page), record.Workspace)
            : (null, null));
        if (found?.Reply is not { } reply) return "The page is gone: open the task again";
        return await ReplyAsync(id, await LocalPages.ReplyTextAsync(reply, workspace), device);
    }

    public Task<RemoteAccounts> AccountsAsync() => OnWindow(() => new RemoteAccounts([null, .. ClaudeAccounts.Names()], _settings.Current.Agent.ClaudeAccount));

    /// <summary>Another Claude account for new runs (one at its limit, say); a run under way keeps its own.</summary>
    public Task<string?> SetAccountAsync(string? account, string device) => OnWindow(() =>
    {
        var name = ClaudeAccounts.Names().FirstOrDefault(each => string.Equals(each, account, StringComparison.OrdinalIgnoreCase));
        if (account is not null && name is null) return $"No Claude account «{account}»";
        var agent = _settings.Current.Agent;
        if (agent.ClaudeAccount == name) return null;
        agent.ClaudeAccount = name;
        _settings.Save();
        // Another account has limits of its own.
        RefreshLimits(AgentProviderKind.ClaudeCode);
        DiagnosticsReported?.Invoke($"[Remote] {device}: Claude account {name ?? "Main"}");
        return (string?)null;
    });

    /// <summary>The step under way: the plan's step and what the agent is at in it ("Fixing the jump — Edit Player.cs").</summary>
    private static string RemoteNow(LiveRun live, IReadOnlyList<AgentPlanItem> plan)
    {
        var step = plan.FirstOrDefault(item => item.Active) ?? plan.FirstOrDefault(item => !item.Done);
        var parts = new[] { step?.Doing ?? step?.Text, live.Turn.Current }.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return parts.Length == 0 ? "Working" : string.Join(" — ", parts);
    }

    /// <summary>
    /// The task's course without the noise of its thread: the user's words and each run with what it did, the last
    /// <see cref="RemoteBeforeLines"/>. The last run's answer goes in full while nothing runs: it is how the task stands.
    /// </summary>
    private List<string> RemoteBefore(AgentTaskRecord record, bool live)
    {
        var lines = new List<string>();
        var runs = 0;
        foreach (var entry in record.Entries)
        {
            if (entry.Kind == AgentEntryKind.You) lines.Add("You: " + Shorten(TaskExport.OneLine(entry.Text), 150));
            if (entry.Kind != AgentEntryKind.Run) continue;
            var run = runs < record.Runs.Count ? record.Runs[runs] : null;
            runs++;
            if (run is null)
            {
                lines.Add(entry.Text);
                continue;
            }

            var said = RunSaid(record, run).Said.Trim();
            var last = !live && ReferenceEquals(run, record.LastRun);
            var head = $"{AgentName(run.Provider)}: {run.Outcome.ToLowerInvariant()}";
            lines.Add(said.Length == 0 ? head : last ? $"{head}\n{Shorten(said, 1500)}" : $"{head} — {Shorten(TaskExport.OneLine(ToPreviewText(said)), 200)}");
        }

        return lines.TakeLast(RemoteBeforeLines).ToList();
    }

    /// <summary>
    /// A new task as the card would start it: the agent and level asked for, else Settings' choices (the agent Link starts
    /// with, its everyday level).
    /// </summary>
    public Task<string?> StartAsync(RemoteNewTask task, string device) => OnWindow(() =>
    {
        var project = task.ProjectId is { } id ? _projects.Projects.FirstOrDefault(each => each.Id == id) : _projects.ActiveProject;
        if (project is null) return "No such project";
        if (project.WorkspaceRoot is not { } workspace || !Directory.Exists(workspace)) return $"{project.Name} has no workspace folder";
        var settings = _settings.Current.Agent;
        if (task.Team && !settings.Team.Enabled) return "Teams are off: turn them on in Link's Settings";
        var provider = task.Provider ?? StartProvider;
        var (model, effort) = settings.Level(provider, task.Level ?? AgentLevel.Standard);
        // A team always plans first, on its lead.
        var plan = new AgentPlan(provider, workspace, task.PlanFirst || task.Team, task.Goal)
        {
            Team = task.Team,
            Model = model,
            Effort = effort,
            AutoEffort = settings.AutoEffort,
            AutoApprove = settings.DefaultAccess == AgentAccessChoice.Auto,
            RoleId = ProjectRoles.Suggest(task.Goal, ProjectRoles.In(workspace))?.Id,
            ProjectId = project.Id,
            NoStop = task.NoStop,
            Attached = task.Attached ?? []
        };
        DiagnosticsReported?.Invoke($"[Remote] {device}: new task in {project.Name}");
        _ = StartNewTaskAsync(plan, new InputSnapshot { ProjectId = project.Id, ProjectName = project.Name, Text = task.Goal }, project);
        return null;
    });

    /// <summary>Words for a task: a reply after its run, the answer to its question, or a correction while it works.</summary>
    public Task<string?> ReplyAsync(Guid id, string words, string device, IReadOnlyList<string>? images = null) =>
        OnRemoteWork(id, device, images is { Count: > 0 } ? "words and a picture" : "words", work =>
        {
            if (work.Waiting is not null) return "The task waits for its folder: stop it first";
            if (!work.Busy && !TakesWords(work)) return "This task has no session to go on with";
            UserSpoke(work);
            ContinueAgent(work, words.Trim(), images);
            return null;
        });

    public Task<string?> DecideAsync(Guid id, bool always, string device) => OnRemoteWork(id, device, "decision", work =>
    {
        if (work.Decision is not { } decision) return "Nothing to decide now";
        UserSpoke(work);
        TakeDecision(work, decision, always);
        return null;
    });

    public Task<string?> RefuseAsync(Guid id, string device) => OnRemoteWork(id, device, "deny", work =>
    {
        if (work.Decision is not { Refuse: not null } decision) return "Nothing to deny now";
        UserSpoke(work);
        RefuseDecision(work, decision);
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

    public Task<IReadOnlyList<string>> ImagesAsync(Guid id) => OnWindow<IReadOnlyList<string>>(() =>
        (WorkOf(id)?.Record ?? ActiveRecord(id))?.LastRun?.Images.Where(File.Exists).ToArray() ?? []);

    /// <summary>The last run's changes as the Changes view has them, while Link holds the task.</summary>
    public Task<string?> DiffAsync(Guid id) => Dispatcher.InvokeAsync(() => WorkOf(id)?.LastRun is { Changes.Count: > 0 } run
        ? _agents.Changes.DiffAsync(run.Before, run.Changes, stat: false, RemoteDiff, CancellationToken.None)
        : Task.FromResult<string?>(null)).Task.Unwrap();

    /// <summary>A task in the remote list: how it stands and what it waits for from the user.</summary>
    private RemoteTask RemoteRow(AgentTaskRecord record, LinkProject project)
    {
        var work = WorkOf(record.Id);
        var waits = work is null ? (record.Status == AgentTaskStatus.NeedsYou ? "your answer" : null)
            // A running task's decision is a tool its agent waits on.
            : work.Busy ? work.Decision?.Button
            : work.Question is not null ? "your answer"
            : work.Decision is { } decision ? decision.Button
            : work.Limit is not null ? "a usage limit"
            : work.Waiting is not null ? "its folder"
            : null;
        var provider = work?.Run?.Task.Provider ?? record.LastRun?.Provider;
        return new RemoteTask(record.Id, project.Name, record.Goal, work?.Run is not null ? "Working" : StatusWords(record.Status),
            provider is { } agent ? AgentName(agent) : null, work?.Busy == true, waits, record.NoStop, record.UpdatedAt, record.LastWords);
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
