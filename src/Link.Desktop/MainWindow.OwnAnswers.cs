using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Link's own answers, apart from the tasks: the project map, shown in the Studio on its own with Stop and undo.
public partial class MainWindow
{
    /// <summary>
    /// The workspace's Map button: the agent looks through the folder, told to change nothing, and Link writes its answer to
    /// docs/map.md, the one change, shown and undone like any other. Link's own answer, on Quick: a run of its own
    /// in the Studio, beside the tasks at work, which it leaves as they are.
    /// </summary>
    private async Task WriteProjectMapAsync()
    {
        if (_own?.Live is not null)
        {
            SetStatus("Link writes its own answer already: Stop it or wait for it first");
            ShowAgentPanel();
            ShowOwnRun();
            return;
        }

        var project = _projects.ActiveProject;
        if (project.WorkspaceRoot is not { } workspace || !Directory.Exists(workspace))
        {
            SetError("Choose the workspace folder first.");
            return;
        }

        // The file Link writes would be counted among the changes of the run that edits there now.
        if (FolderBusy(null, workspace) is { } busy)
        {
            SetStatus($"{busy}: map it once that run ends");
            return;
        }

        string prompt;
        try
        {
            prompt = await File.ReadAllTextAsync(ProjectMap.TaskFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"The map's task is missing: {ProjectMap.TaskFile}");
            return;
        }

        var provider = StartProvider;
        var (model, effort) = _settings.Current.Agent.OwnAnswers(provider);
        var map = new AgentTask
        {
            Provider = provider,
            Workspace = workspace,
            Prompt = prompt,
            AllowedTools = AgentCoordinator.Tools(project, workspace),
            AnswerFile = ProjectMap.RelativePath,
            Model = model,
            Effort = effort,
            WithoutMcp = true
        };
        ShowAgentPanel();
        await StartOwnAsync(map);
    }

    /// <summary>Link's own run at the top of the list while it works, and after it until the next one: one click shows it.</summary>
    private TaskRow? OwnRow()
    {
        if (_own is not { } own) return null;
        var running = own.Live is not null;
        var status = running ? "working" : own.Status;
        return new TaskRow($"Link writes {OwnWhat(own.Task)}", "Link's own answer, apart from the tasks",
            string.Empty, $"{AgentName(own.Task.Provider)} · {status}", (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot), RoleBrushes.Link, null)
        {
            Group = running ? TaskGroup.Running : TaskGroup.Recent
        };
    }

    /// <summary>
    /// Link's own answer (the map): a run of its own beside the tasks at work, which it leaves as they are. Its agent only
    /// reads; the file Link writes is an edit of the folder, so the tasks' edits there wait for it.
    /// </summary>
    private async Task StartOwnAsync(AgentTask task)
    {
        task = task with { ContextBudget = _settings.Current.Agent.ContextBudget, EffortReason = null };
        var provider = _agents.Provider(task.Provider);
        var turn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort, unknown: ".")}", "Starting…",
            (Brush)FindResource("AccentBrush"), RoleBrushes.For(task.RoleId, WorkspaceRoles(task.Workspace))) { StepsOpen = true };
        var live = new LiveRun(task, turn, _limits.For(task.Provider));
        var own = _own = new OwnRun(task, turn) { Live = live };
        _agentPanel ??= CreateAgentPanel();
        ShowOwnRun();
        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{ShortName(task.Provider)} начал работу.", $"{ShortName(task.Provider)} started.");
        AgentStatus($"{provider.Name} writes {OwnWhat(task)} · Stop is in the Studio");
        UpdateAgentPill();
        RefreshTaskList();
        var progress = new Progress<AgentEvent>(agentEvent =>
        {
            if (agentEvent.Kind == AgentEventKind.Session) live.SessionId = agentEvent.Text;
            else live.Pending.Add(agentEvent);
        });
        AgentRun? ended = null;
        Exception? failure = null;
        try
        {
            ended = await _agents.RunAsync(task, null, progress, live.Stop.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            FlushAgentEvents();
            own.Live = null;
            live.Clock.Stop();
            live.Stop.Dispose();
            if (!AnyRunning) _activityTimer.Stop();
            ReportState();
        }

        if (ended is not null)
        {
            // The tasks in the folder find the file Link wrote there.
            if (ended.Changes.Count > 0) EditedBy(null, task.Workspace);
            ShowOwnResult(own, live, ended);
        }
        else if (failure is not null)
        {
            ShowOwnFailure(own, failure);
        }

        UpdateAgentPill();
        RefreshTaskList();
        RefreshStudio();
        StartWaitingRuns();
    }

    /// <summary>Link's own answer, such as the map: a thread of its one run, outside the tasks.</summary>
    private void ShowOwnRun()
    {
        if (_agentPanel is not { } panel || _own is not { } own) return;
        _shown = null;
        _ownShown = true;
        var task = own.Task;
        var live = own.Live;
        panel.ShowTaskView($"Link writes {OwnWhat(task)}", $"{_projects.ActiveProject.Name} · {task.Workspace}", null);
        panel.SetStatus(live is not null ? "working" : own.Status, (Brush)FindResource(live is not null ? "BusyOrbBrush" : own.Dot));
        panel.SetHeader(live is not null ? AgentHeader(live) : $"{AgentName(task.Provider)} · {SetupWords(task.Provider, task.Model, task.Effort)} · answer");
        panel.ShowPlan([]);
        panel.ShowChanges(string.Empty, string.Empty);
        panel.ShowThread([own.Turn]);
        panel.ShowUsage(string.Empty, string.Empty);
        panel.ResetCards();
        panel.ShowQuestion(null);
        panel.ShowMenus([], OwnActions(own));
        panel.ShowFinish(false);
        panel.ShowNoStop(null);
        panel.ShowPlanMode(false);
        panel.ShowReplan(false);
        panel.SetRunning(live is not null);
        // Link's own answer takes no words.
        panel.ShowReply(false, live is not null ? null : "Link's own answer: the file is written. The tasks are on the left.");
        panel.ShowSetup(string.Empty, []);
    }

    /// <summary>What Link's own run writes, as the user reads it: "docs/map.md".</summary>
    private static string OwnWhat(AgentTask task) =>
        task.AnswerFile is { } file ? file.Replace('\\', '/') : Shorten(task.Prompt.Split('\n')[0], 80);

    /// <summary>The ⋯ menu of Link's own run: undo what it wrote, and the console.</summary>
    private IReadOnlyList<PanelAction> OwnActions(OwnRun own) =>
        own.Run is { } run && UndoAction(null, run) is { } undo ? [undo, ConsoleAction()] : [ConsoleAction()];

    /// <summary>Link's own run ended: its answer in its turn, its file listed; the tasks stay as they were.</summary>
    private void ShowOwnResult(OwnRun own, LiveRun live, AgentRun run)
    {
        own.Run = run;
        var result = run.Result;
        var elapsed = $"{(int)live.Clock.Elapsed.TotalMinutes}:{live.Clock.Elapsed.Seconds:00}";
        var ending = result.Stopped ? $"Stopped after {elapsed}" : !result.Succeeded ? $"Failed after {elapsed}" : $"Done in {elapsed}";
        own.Status = result.Stopped ? "stopped" : !result.Succeeded ? "failed" : "done";
        own.Dot = result.Stopped ? "IdleOrbBrush" : !result.Succeeded ? "ErrorOrbBrush" : "SuccessOrbBrush";
        var turn = own.Turn;
        turn.Current = string.Empty;
        turn.Outcome = ending;
        turn.OutcomeBrush = OutcomeBrush(ending);
        turn.Said = AgentCoordinator.Unfenced(result.Summary);
        turn.Files = run.Changes.Select(change => ToChangeRow(change, run.Task.Workspace)).ToArray();
        turn.Stats = StatsLine(result.Stats) + SpentLine(run.Task.Provider, SpentByRun(live));
        turn.StepsOpen = false;
        if (_ownShown) ShowOwnRun();
        _projectPanel?.ShowProject(_projects.ActiveProject);
        var name = ShortName(run.Task.Provider);
        if (result.Stopped) return;
        _cues.Play(result.Succeeded ? SoundCue.Done : SoundCue.Error);
        if (result.Succeeded) SayAgent($"{name} закончил работу.", $"{name} finished.");
        else SayAgent($"{name}: не получилось.", $"{name} failed.");
        AgentStatus($"{AgentName(run.Task.Provider)}: {ending.ToLowerInvariant()} · {OwnWhat(run.Task)}");
    }

    private void ShowOwnFailure(OwnRun own, Exception exception)
    {
        own.Status = "could not start";
        own.Dot = "ErrorOrbBrush";
        own.Turn.Current = string.Empty;
        own.Turn.Outcome = "could not start";
        own.Turn.OutcomeBrush = (Brush)FindResource("ErrorBrush");
        own.Turn.Said = exception.Message;
        if (_ownShown) ShowOwnRun();
        _cues.Play(SoundCue.Error);
        if (!_sending) SetError(exception.Message);
    }

    /// <summary>Link's own run (a map): its task, its turn in the thread, the run while under way, and once over, the run and how it ended.</summary>
    private sealed class OwnRun(AgentTask task, ThreadTurn turn)
    {
        public AgentTask Task { get; } = task;
        public ThreadTurn Turn { get; } = turn;
        public LiveRun? Live { get; set; }
        public AgentRun? Run { get; set; }
        public string Status { get; set; } = "working";
        public string Dot { get; set; } = "BusyOrbBrush";
    }
}
