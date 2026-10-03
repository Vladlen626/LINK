using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Agent: a CLI agent (Claude Code or Codex) works on one task of the user's in the project's workspace. A card comes
// first; then the Task tab shows the task as one history: who works, its plan, steps, changes, result, what Link gave
// and what it spent, with Stop, corrections, decisions, hand-overs, levels and undo.
public partial class MainWindow
{
    // A run that ends on one of these ran out of its limit: the other agent can take the task over.
    private static readonly Regex LimitReached = new(
        @"\b(usage|rate|session|weekly|daily|5-hour|five-hour) limit\b|hit your (usage )?limit|limit reached|out of (credits|usage)|quota",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan ActivityFlush = TimeSpan.FromMilliseconds(100);
    // So many of the task's last steps are kept with it, for an agent that takes it over.
    private const int StepsHandedOn = 40;
    // How much diff goes with "Discuss in chat"; a bigger one goes as numbers of lines.
    private const int DiffForChat = 30_000;
    private readonly AgentCoordinator _agents;
    private readonly AgentLimits _limits;
    private readonly List<AgentEvent> _pendingAgentEvents = [];
    // The task's steps in Activity's words, oldest first: what an agent that takes the task over is told was done.
    private readonly List<string> _taskSteps = [];
    private AgentConsoleWindow? _agentConsoleWindow;
    private AgentPanel? _agentPanel;
    // What Run on the card does: start a task, or hand the open one over with the brief on the card.
    private Func<AgentPlan, Task>? _cardRun;
    private InputSnapshot? _agentSnapshot;
    // The open task: its record holds everything the Task tab shows; null when there is none.
    private AgentTaskRecord? _task;
    // The project the open task belongs to: the user may switch projects while it runs.
    private LinkProject? _taskProject;
    private AgentRun? _lastRun;
    private AgentTask? _runningTask;
    // The task as the card or the Map button started it: what Start over starts again.
    private AgentTask? _agentOrigin;
    // A task opened from the history: the next reply takes up its last session again.
    private AgentTask? _earlierSession;
    // The level a plan is carried out on, after Plan first ran on Deep.
    private AgentLevel _planLevel = AgentLevel.Standard;
    // Words the user gave while the agent worked: a correction now, or words for after the run.
    private string? _steerWith;
    private string? _queued;
    private bool _restartWhenStopped;
    private AgentProviderKind? _handOffWhenStopped;
    private bool _backupBeforeDo = true;
    private CancellationTokenSource? _agentCancellation;
    private DispatcherTimer? _activityTimer;
    private DispatcherTimer? _continueAt;
    private Stopwatch _agentClock = new();
    private Action? _decision;
    private bool _rememberAllowance;
    private bool _limitOffered;
    private string _lastStep = string.Empty;
    // The More menu's choices after the last run: undo and save as skill come and go with it.
    private IReadOnlyList<PanelAction> _afterRun = [];
    // Files the running agent reported changed, listed until git or the copy tells the whole list.
    private readonly HashSet<string> _runningFiles = new(StringComparer.OrdinalIgnoreCase);

    private bool AgentRunning => _agentCancellation is not null;

    private AgentProviderKind ProjectProvider =>
        _projects.ActiveProject.AgentProvider ?? _settings.Current.Agent.DefaultProvider;

    private static string ShortName(AgentProviderKind provider) => provider == AgentProviderKind.ClaudeCode ? "Claude" : "Codex";

    private static AgentProviderKind Other(AgentProviderKind provider) =>
        provider == AgentProviderKind.ClaudeCode ? AgentProviderKind.Codex : AgentProviderKind.ClaudeCode;

    private string AgentName(AgentProviderKind provider) => _agents.Provider(provider).Name;

    /// <summary>Enter on the Agent route or Ctrl+Enter: the input goes to the card, not anywhere yet.</summary>
    private async Task OpenAgentCardFromPromptAsync()
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        RemoveMissingContextFiles();
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            SetError("Write what the agent should do.");
            return;
        }

        var project = _projects.ActiveProject;
        var snapshot = await TakeSnapshotAsync(project, PromptBox.Text, RequestRoute.Agent, AutoFrameOn);
        _attempts.Keep(snapshot);
        OpenAgentCard(snapshot);
    }

    /// <summary>
    /// The workspace's Map button: the agent looks through the folder read only and Link writes its answer to
    /// docs/map.md, the one change, shown and undone like any other. Link's own answer, on the Light level.
    /// </summary>
    private async Task WriteProjectMapAsync()
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        var project = _projects.ActiveProject;
        if (project.WorkspaceRoot is not { } workspace || !Directory.Exists(workspace))
        {
            SetError("Choose the workspace folder first.");
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

        var (model, effort) = OwnAnswerLevel(ProjectProvider);
        _agentOrigin = new AgentTask
        {
            Provider = ProjectProvider,
            Workspace = workspace,
            Prompt = prompt,
            AllowedTools = project.AgentReadTools,
            AnswerFile = ProjectMap.RelativePath,
            Model = model,
            Effort = effort
        };
        await StartAgentAsync(_agentOrigin, null);
    }

    /// <summary>
    /// Save as skill: the last run's session describes what it did as a skill, read only, and Link writes the SKILL.md
    /// to the workspace's skills, the one change, shown and undone like any other.
    /// </summary>
    private async Task SaveSkillAsync()
    {
        if (_lastRun is not { } run || AgentRunning) return;
        string request;
        try
        {
            request = await File.ReadAllTextAsync(ProjectSkills.TaskFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"The skill's task is missing: {ProjectSkills.TaskFile}");
            return;
        }

        if (AgentCoordinator.SkillTask(run, request) is not { } task) return;
        var (model, effort) = OwnAnswerLevel(task.Provider);
        _task?.Add(AgentEntryKind.You, "Save as skill");
        await StartAgentAsync(task with { Model = model, Effort = effort }, null);
    }

    /// <summary>Link's own answers (a map, a skill, a note) run on Light, unless Settings says otherwise.</summary>
    private (string? Model, string? Effort) OwnAnswerLevel(AgentProviderKind provider)
    {
        var agent = _settings.Current.Agent;
        return agent.Level(provider, agent.OwnAnswersOnLight ? AgentLevel.Light : AgentLevel.Standard);
    }

    /// <summary>A skill of the same name is written over only when the user says so.</summary>
    private bool ReplaceSkill(string name) =>
        MessageBox.Show(this, $"The workspace has the skill {name} already. Replace it with the new one?",
            "Link · save as skill", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>The task card for a snapshot: <paramref name="brief"/> is what the agent is told, the question by default.</summary>
    private void OpenAgentCard(InputSnapshot snapshot, string? brief = null)
    {
        _agentSnapshot = snapshot;
        var attachments = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>().Select(item => item.Label).ToArray();
        var card = CardModel(brief ?? snapshot.Text, attachments.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", attachments),
            ProjectProvider);
        _cardRun = RunAgentAsync;
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        SetStatus("Check the card and press Run: nothing runs before that");
    }

    private AgentCardModel CardModel(string goal, string attachments, AgentProviderKind provider)
    {
        var project = _projects.ActiveProject;
        return new AgentCardModel(
            _agents.Providers.Select(agent => (agent.Kind, agent.Name, LevelLabels(agent), LimitsLine(agent.Kind))).ToArray(),
            provider,
            project.Name,
            project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null,
            project.WorkspaceRoot is { } folder && HasGit(folder),
            attachments,
            goal,
            project.CheckCommand,
            _settings.Current.Agent.DefaultAccess);
    }

    /// <summary>"Light · Sonnet 5.5 · Medium" and so on for an agent's three levels.</summary>
    private IReadOnlyList<string> LevelLabels(IAgentProvider agent) =>
        Enum.GetValues<AgentLevel>().Select(level => $"{level} · {LevelSetup(agent.Kind, level)}").ToArray();

    /// <summary>A level's model and effort as the user reads them: "Opus 5.5 · Extra high".</summary>
    private string LevelSetup(AgentProviderKind provider, AgentLevel level)
    {
        var agent = _agents.Provider(provider);
        var (model, effort) = _settings.Current.Agent.Level(provider, level);
        var modelName = model is null ? "its own model" : agent.Models.FirstOrDefault(choice => choice.Value == model)?.Label ?? model;
        var effortName = effort is null ? "its own effort" : agent.Efforts.FirstOrDefault(choice => choice.Value == effort)?.Label.Split(" — ")[0] ?? effort;
        return $"{modelName} · {effortName}";
    }

    /// <summary>The level a run's model and effort are; null when they match none, e.g. after Settings changed.</summary>
    private AgentLevel? LevelOf(AgentTask task) =>
        Enum.GetValues<AgentLevel>().Cast<AgentLevel?>().FirstOrDefault(level =>
            _settings.Current.Agent.Level(task.Provider, level!.Value) == (task.Model, task.Effort));

    private string LimitsLine(AgentProviderKind provider) =>
        _limits.Describe(provider) is { Length: > 0 } limits ? $"Usage: {limits}" : string.Empty;

    private void ShowAgentPanel()
    {
        if (!_settings.Current.SidePanel.IsOpen) SetPanelOpen(true);
        ShowPanel();
        _agentPanel ??= CreateAgentPanel();
        ShowPanelTab(PanelTab.Task);
    }

    private AgentPanel CreateAgentPanel()
    {
        var panel = new AgentPanel();
        panel.RunRequested += plan =>
        {
            if (_cardRun is { } run) _ = run(plan);
        };
        panel.CardCancelled += CancelCard;
        panel.StopRequested += () => _agentCancellation?.Cancel();
        panel.DecisionTaken += always =>
        {
            _rememberAllowance = always;
            _decision?.Invoke();
        };
        panel.RestartRequested += RequestRestart;
        panel.EscalateRequested += () => SwitchLevel(AgentLevel.Deep, escalate: true);
        panel.ContinueRequested += ContinueAgent;
        panel.QueueRequested += QueueAfterRun;
        panel.FinishRequested += FinishTask;
        panel.ConsoleRequested += ShowAgentConsole;
        panel.OpenFileRequested += ShowFile;
        return panel;
    }

    /// <summary>Cancel on the card: back to the open task, or to the project when there is none.</summary>
    private void CancelCard()
    {
        _cardRun = null;
        if (_task is not null) ShowTask();
        else ShowPanelTab(PanelTab.Project);
    }

    /// <summary>Console: what Link gave the agent's CLI and every line it writes back, in a window of its own.</summary>
    private void ShowAgentConsole()
    {
        if (_agentConsoleWindow is null)
        {
            _agentConsoleWindow = new AgentConsoleWindow(_agents.Console);
            _agentConsoleWindow.Closed += (_, _) => _agentConsoleWindow = null;
        }

        _agentConsoleWindow.Show();
        if (_agentConsoleWindow.WindowState == WindowState.Minimized) _agentConsoleWindow.WindowState = WindowState.Normal;
        _agentConsoleWindow.Activate();
    }

    /// <summary>Run on the card: a new task. An open one is finished first; it stays in the history.</summary>
    private async Task RunAgentAsync(AgentPlan plan)
    {
        if (_agentSnapshot is not { } snapshot || AgentRunning) return;
        _cardRun = null;
        var project = _projects.ActiveProject;
        if (project.AgentProvider != plan.Provider)
        {
            project.AgentProvider = plan.Provider;
            _projects.Save();
            UpdateRouteButton();
        }

        if (_task is { Status: not AgentTaskStatus.Finished }) FinishTask();
        _backupBeforeDo = plan.BackupBeforeDo;
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        var agent = _settings.Current.Agent;
        // A plan is made on Deep and carried out on the level chosen, in the same session.
        _planLevel = plan.Level;
        var level = plan.PlanFirst && agent.PlanOnDeep ? AgentLevel.Deep : plan.Level;
        var (model, effort) = agent.Level(plan.Provider, level);
        _agentOrigin = AgentCoordinator.BuildTask(plan with { Model = model, Effort = effort }, snapshot, project);
        _taskProject = project;
        _task = new AgentTaskRecord
        {
            Goal = Shorten(plan.Goal.Split('\n')[0].Trim(), 200),
            Brief = _agentOrigin.Prompt,
            Workspace = plan.Workspace
        };
        _task.Add(AgentEntryKind.Brief, $"{plan.Goal.Split('\n')[0].Trim()} · {AgentName(plan.Provider)}, {level}{(plan.PlanFirst ? ", plan first" : string.Empty)}");
        _earlierSession = null;
        _lastRun = null;
        await StartAgentAsync(_agentOrigin, null);
    }

    /// <summary>
    /// The user's words. While the agent works they correct it: it stops the step it is on and the same session goes
    /// on with them. After a run they are a reply in the same session; a task from the history is taken up again in its
    /// folder, with a copy before edits where there is no git.
    /// </summary>
    private void ContinueAgent(string reply)
    {
        if (reply.Trim() is not { Length: > 0 } text) return;
        if (AgentRunning)
        {
            // Link's own step (a note for Start over) takes no words.
            if (_runningTask is null || _restartWhenStopped) return;
            _steerWith = text;
            _task?.Add(AgentEntryKind.You, "(correction) " + text);
            _agentCancellation?.Cancel();
            SetStatus("Stopping the agent to pass your words on…");
            return;
        }

        _task?.Add(AgentEntryKind.You, text);
        if (_earlierSession is { } earlier)
        {
            _agentOrigin = earlier with { ResumeSessionId = null };
            _backupBeforeDo = true;
            _ = StartAgentAsync(earlier with { Prompt = text }, null);
            return;
        }

        if (_lastRun is { } run && AgentCoordinator.ContinueTask(run, text) is { } task) _ = StartAgentAsync(task, run);
    }

    /// <summary>Tab while the agent works: the words wait for the end of the run, then the same session goes on with them.</summary>
    private void QueueAfterRun(string words)
    {
        if (!AgentRunning || _runningTask is null) return;
        _queued = _queued is null ? words : $"{_queued}\n{words}";
        _task?.Add(AgentEntryKind.You, "(after this run) " + words);
        ShowTask();
        SetStatus("Your words wait for the end of this run");
    }

    /// <summary>Finish: the task is closed and kept in the history, where it can be opened and taken up again.</summary>
    private void FinishTask()
    {
        if (AgentRunning || _task is not { } task) return;
        _continueAt?.Stop();
        if (task.Status != AgentTaskStatus.Finished)
        {
            task.Status = AgentTaskStatus.Finished;
            task.Add(AgentEntryKind.Finished, "Finished");
            SaveTask(task);
        }

        _task = null;
        _lastRun = null;
        _earlierSession = null;
        _agentOrigin = null;
        UpdateAgentPill();
        ShowPanelTab(PanelTab.Project);
        SetStatus("Task finished: it is in the project's history");
    }

    /// <summary>A task from the project's history: its record, and the reply box that takes up its last session.</summary>
    private void OpenAgentTask(AgentTaskRecord record)
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        if (!Directory.Exists(record.Workspace))
        {
            SetError($"The task's folder is gone: {record.Workspace}");
            return;
        }

        var project = _projects.ActiveProject;
        _task = record;
        _taskProject = project;
        _lastRun = null;
        _agentOrigin = null;
        var last = record.LastRun;
        var tools = last?.Access == AgentAccess.Do ? project.AgentReadTools.Concat(project.AgentDoTools) : project.AgentReadTools;
        _earlierSession = last?.SessionId is { } session
            ? new AgentTask
            {
                Provider = last.Provider,
                Workspace = record.Workspace,
                Prompt = record.Goal,
                Access = last.Access,
                AutoApprove = last.AutoApprove,
                Model = last.Model,
                Effort = last.Effort,
                AllowedTools = tools.Distinct(StringComparer.Ordinal).ToArray(),
                CheckCommand = project.CheckCommand,
                ResumeSessionId = session
            }
            : null;
        _taskSteps.Clear();
        _taskSteps.AddRange(record.Steps);
        ShowAgentPanel();
        _agentPanel!.ShowActivity(record.Goal, last is null ? "No run yet" : RunLine(last), $"{project.Name} · {record.Workspace}");
        _agentPanel.ShowResult(record.LastWords, $"{StatusWords(record.Status)} · {ShortTime(record.UpdatedAt)}");
        _agentPanel.ShowChanges(record.ChangedFiles.Select(file => new AgentChangeRow(file, Path.Combine(record.Workspace, file))).ToArray());
        _agentPanel.ShowStats(string.Empty);
        _agentPanel.ShowReply(_earlierSession is not null);
        _afterRun = [new PanelAction("Console", "What Link gave the agent's CLI and what it wrote back", ShowAgentConsole)];
        ShowTask();
        UpdateAgentPill();
        SetStatus(_earlierSession is null ? "This task has no session to go on with" : "Write a reply to take the task up again");
    }

    /// <summary>
    /// Runs a task, fresh or continuing <paramref name="previous"/>, and shows it in the Task tab; <paramref name="note"/>
    /// opens the steps, such as the note a new session starts over with.
    /// </summary>
    private async Task StartAgentAsync(AgentTask task, AgentRun? previous, string? note = null)
    {
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        // A new task starts clean; a run that goes on with the last one, or a task taken up again, keeps its past.
        if (previous is null && _task?.Runs.Count is null or 0) _agents.Console.Clear();
        if (previous is null && task.ResumeSessionId is null && _task?.Runs.Count is null or 0) _taskSteps.Clear();
        _runningTask = task;
        _decision = null;
        _earlierSession = null;
        _limitOffered = false;
        _continueAt?.Stop();
        if (_task is not null && IsUserWork(task)) _task.Status = AgentTaskStatus.Running;
        ShowAgentPanel();
        _agentPanel!.ShowActivity(_task?.Goal ?? Shorten(task.Prompt.Split('\n')[0], 200), AgentHeader(task), $"{project.Name} · {task.Workspace}");
        _agentPanel.SetReplyTarget(provider.Name);
        if (_task is not null) ShowTask();
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentClock = Stopwatch.StartNew();
        _pendingAgentEvents.Clear();
        if (note is not null) _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Output, note));
        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{provider.Name} started.");
        SetStatus($"{provider.Name} is working · Stop is in the Task tab");
        UpdateAgentPill();
        var progress = new Progress<AgentEvent>(_pendingAgentEvents.Add);
        AgentRun? ended = null;
        try
        {
            var run = await _agents.RunAsync(task, _backupBeforeDo, previous, progress, cancellation.Token, ReplaceSkill);
            if (run.Before is { IsGit: false, Backup: null } && _backupBeforeDo && task.Access == AgentAccess.Do && previous is null)
            {
                _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Error, "No copy was made: the workspace is over 2 GB."));
            }

            FlushAgentEvents();
            ShowAgentRun(run);
            ended = run;
        }
        catch (Exception exception)
        {
            FlushAgentEvents();
            _agentPanel.ShowResult(exception.Message, "Could not start");
            // No session began: a reply here would go to an older run. The other agent can still take the task.
            _agentPanel.ShowReply(false);
            if (_task is not null)
            {
                _task.Status = AgentTaskStatus.Failed;
                _task.Add(AgentEntryKind.Run, $"{provider.Name} could not start: {exception.Message}");
                SaveTask(_task);
            }

            ShowTask();
            _cues.Play(SoundCue.Error);
            SetError(exception.Message);
        }
        finally
        {
            _activityTimer.Stop();
            _agentCancellation = null;
            _runningTask = null;
            cancellation.Dispose();
            ReportState();
            UpdateAgentPill();
        }

        if (_restartWhenStopped)
        {
            _restartWhenStopped = false;
            await RestartWithNoteAsync();
        }
        else if (_handOffWhenStopped is { } to)
        {
            _handOffWhenStopped = null;
            await ShowHandOffCardAsync(to);
        }
        else if (_steerWith is { } words)
        {
            _steerWith = null;
            if (ended is not null && AgentCoordinator.ContinueTask(ended, AgentCoordinator.Interrupted(words)) is { } next)
            {
                await StartAgentAsync(next, ended);
            }
            else
            {
                SetError("The agent stopped before its session began: give your words as a new task.");
            }
        }
        else if (_queued is { } later)
        {
            _queued = null;
            if (ended is not null && AgentCoordinator.ContinueTask(ended, later) is { } next) await StartAgentAsync(next, ended);
        }
    }

    /// <summary>The user's work, as opposed to Link's own answer written to a file, such as the map.</summary>
    private static bool IsUserWork(AgentTask task) => task.AnswerFile is null;

    /// <summary>Start over with a note; a running agent is stopped first, and its note is asked once it has ended.</summary>
    private void RequestRestart()
    {
        if (!AgentRunning)
        {
            _ = RestartWithNoteAsync();
            return;
        }

        _restartWhenStopped = true;
        _agentCancellation?.Cancel();
    }

    /// <summary>
    /// The last run's session writes a short note on what it tried and why it did not work (Stop ends the asking and
    /// the new start); then a new session takes the original task with that note, the same access and folder, and undo
    /// still covers both.
    /// </summary>
    private async Task RestartWithNoteAsync()
    {
        if (_lastRun is not { } run || _agentOrigin is not { } origin || AgentRunning) return;
        var provider = _agents.Provider(run.Task.Provider);
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentPanel?.ShowStep($"Asking {provider.Name} what it tried, for a new session…", running: true);
        SetStatus($"{provider.Name} writes a note for a new session · Stop is in the Task tab");
        (AgentTask Task, string Note)? next;
        try
        {
            next = await _agents.StartOverAsync(run, origin, OwnAnswerLevel(run.Task.Provider), cancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _agentPanel?.ShowStep("Could not ask for a note", running: false);
            SetError(exception.Message);
            return;
        }
        finally
        {
            _agentCancellation = null;
            cancellation.Dispose();
            ReportState();
        }

        if (next is not { } fresh)
        {
            _agentPanel?.ShowStep("Start over stopped", running: false);
            SetStatus("Start over stopped: the last run stays as it was");
            return;
        }

        _task?.Add(AgentEntryKind.You, "Start over with a note" + (fresh.Note.Length > 0 ? ": " + fresh.Note : string.Empty));
        await StartAgentAsync(fresh.Task, run,
            fresh.Note.Length > 0 ? "Note from the last attempt:\n" + fresh.Note : "No note came back: the new session starts with the task alone.");
    }

    /// <summary>
    /// The same session goes on with another level's model and effort: cheaper for routine work, Deep for a problem the
    /// task's level did not solve (Escalate). One cache miss: the model reads the session anew.
    /// </summary>
    private void SwitchLevel(AgentLevel level, bool escalate = false)
    {
        if (AgentRunning || ResumableTask() is not { } resume) return;
        var (model, effort) = _settings.Current.Agent.Level(resume.Task.Provider, level);
        var prompt = escalate
            ? "The work is not done yet. Think it through again from what you know, find the cause and finish the task."
            : "Go on with the task.";
        _task?.Add(AgentEntryKind.Model,
            $"{AgentName(resume.Task.Provider)} · {level}: {LevelSetup(resume.Task.Provider, level)}{(escalate ? ", escalated" : string.Empty)}");
        if (resume.Previous is null) _backupBeforeDo = true;
        _ = StartAgentAsync(resume.Task with { Model = model, Effort = effort, Prompt = prompt, Images = [] }, resume.Previous);
    }

    /// <summary>The task's last session as a run to continue, and the run it continues; null when there is none.</summary>
    private (AgentTask Task, AgentRun? Previous)? ResumableTask() =>
        _lastRun is { Result.SessionId: { } session } run ? (run.Task with { ResumeSessionId = session, PlanOnly = false }, run)
        : _earlierSession is { } earlier ? (earlier, null)
        : null;

    /// <summary>
    /// Hand off: the other agent takes the task over in a new session of its own. The brief is made from the task's
    /// record and shown on the card first, where it can be changed, with the level and access of the new run.
    /// </summary>
    private async Task ShowHandOffCardAsync(AgentProviderKind to)
    {
        if (AgentRunning || _task is not { } record || ResumableTask() is not { } from && _agentOrigin is null) return;
        var fromProvider = record.LastRun?.Provider ?? Other(to);
        var numstat = _lastRun is { } run ? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, 4000, CancellationToken.None) : null;
        var brief = AgentCoordinator.HandOffBrief(record, AgentName(fromProvider), numstat);
        var card = CardModel(brief, string.Empty, to) with
        {
            Title = $"HAND OFF TO {AgentName(to).ToUpperInvariant()} · NEW SESSION",
            RunLabel = $"Hand off to {AgentName(to)}",
            FixedProvider = true
        };
        _cardRun = plan => HandOffAsync(plan, fromProvider);
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        SetStatus($"The brief for {AgentName(to)} is on the card: change it if you like, then Hand off");
    }

    private async Task HandOffAsync(AgentPlan plan, AgentProviderKind fromProvider)
    {
        if (AgentRunning || _task is not { } record) return;
        _cardRun = null;
        var template = ResumableTask()?.Task ?? _agentOrigin;
        if (template is null) return;
        var (model, effort) = _settings.Current.Agent.Level(plan.Provider, plan.Level);
        // The images the task began with go along; a task from the history has kept none.
        var images = _agentOrigin?.Images ?? [];
        var task = AgentCoordinator.HandOffTask(template, plan.Provider, plan.Goal, images) with
        {
            Model = model,
            Effort = effort,
            Access = AgentAccess.Do,
            AutoApprove = plan.AutoApprove
        };
        record.Add(AgentEntryKind.HandOff, $"{AgentName(fromProvider)} → {AgentName(plan.Provider)}, {plan.Level}, a new session");
        record.Brief += $"\n\n── Handed over to {AgentName(plan.Provider)} ──\n{plan.Goal}";
        // Start over begins the new agent's brief again.
        _agentOrigin = task;
        if (_lastRun is null) _backupBeforeDo = true;
        await StartAgentAsync(task, _lastRun);
    }

    /// <summary>
    /// Return to the first agent, such as once its limit has reset: its own session goes on, told what the other did,
    /// rather than a new one learning the task anew.
    /// </summary>
    private async Task ReturnToAsync(AgentProviderKind provider)
    {
        if (AgentRunning || _task is not { } record) return;
        var back = record.Runs.LastOrDefault(run => run.Provider == provider && run.SessionId is not null);
        var template = ResumableTask()?.Task ?? _agentOrigin;
        if (back?.SessionId is not { } session || template is null) return;
        var numstat = _lastRun is { } run ? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, 4000, CancellationToken.None) : null;
        var prompt = AgentCoordinator.ReturnBrief(record, AgentName(Other(provider)), numstat);
        var (model, effort) = (back.Model, back.Effort);
        record.Add(AgentEntryKind.HandOff, $"Back to {AgentName(provider)}, its own session");
        record.Brief += $"\n\n── Back to {AgentName(provider)} ──\n{prompt}";
        if (_lastRun is null) _backupBeforeDo = true;
        await StartAgentAsync(template with
        {
            Provider = provider,
            ResumeSessionId = session,
            Prompt = prompt,
            Model = model,
            Effort = effort,
            Images = [],
            PlanOnly = false,
            AnswerFile = null,
            AnswerIsSkill = false
        }, _lastRun);
    }

    /// <summary>The task's steps in Activity's words, for an agent that takes it over; the last ones are what counts.</summary>
    private void RememberSteps(IEnumerable<AgentEvent> events)
    {
        foreach (var agentEvent in events)
        {
            var step = agentEvent.Kind switch
            {
                AgentEventKind.Tool => agentEvent.Text,
                AgentEventKind.Output => "Said: " + ToPreviewText(agentEvent.Text),
                AgentEventKind.NeedsInput => "Refused: " + agentEvent.Text,
                AgentEventKind.Error => "Error: " + agentEvent.Text,
                _ => null
            };
            if (step is not null) _taskSteps.Add(Shorten(step, 300));
        }

        if (_taskSteps.Count > StepsHandedOn * 2) _taskSteps.RemoveRange(0, _taskSteps.Count - StepsHandedOn);
    }

    private DispatcherTimer CreateActivityTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ActivityFlush };
        timer.Tick += (_, _) => FlushAgentEvents();
        return timer;
    }

    /// <summary>Events arrive as fast as the CLI writes them; the tab takes them in batches, ten times a second at most.</summary>
    private void FlushAgentEvents()
    {
        if (_agentPanel is null) return;
        if (_runningTask is { } task)
        {
            _agentPanel.SetHeader(AgentHeader(task));
            UpdateAgentPill();
        }

        if (_pendingAgentEvents.Count == 0) return;
        var events = _pendingAgentEvents.ToArray();
        _pendingAgentEvents.Clear();
        _agentPanel.AddSteps(events);
        RememberSteps(events);
        if (events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Plan) is { } plan)
        {
            if (_task is not null) _task.Plan = [.. plan.Plan];
            _agentPanel.ShowPlan(plan.Plan);
        }

        foreach (var usage in events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Limit)) _limits.Update(usage.Limits);
        if (_runningTask is { } running && !_limitOffered && _limits.IsNear(running.Provider)) OfferLimitAhead(running.Provider);
        if (_agentOrigin is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Loop))
        {
            _agentPanel.ShowRestart(true, escalate: _runningTask is { } looping && LevelOf(looping) != AgentLevel.Deep);
        }

        // Changed files are listed as they come; the full list, from git or the copy, replaces them at the end.
        if (_runningTask is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Artifact))
        {
            _runningFiles.UnionWith(events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Artifact).Select(agentEvent => agentEvent.Text));
            _agentPanel.ShowChanges(_runningFiles.Select(path => new AgentChangeRow(Relative(path), path)).ToArray());
        }

        // The status line says who does what; the time is in the agent's pill.
        if (_runningTask is { } current && !_sending && _voice.Phase == VoicePhase.Idle &&
            events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Tool) is { } step && step.Text != _lastStep)
        {
            _lastStep = step.Text;
            SetStatus($"{AgentName(current.Provider)} is working · {step.Text}");
        }
    }

    /// <summary>The agent is near its usage limit while it works: the ways on are offered, nothing is done by itself.</summary>
    private void OfferLimitAhead(AgentProviderKind provider)
    {
        _limitOffered = true;
        var other = Other(provider);
        _agentPanel?.ShowLimit($"{AgentName(provider)} is near its usage limit: {_limits.Describe(provider)}.",
        [
            new PanelAction($"Stop and hand off to {AgentName(other)}", "The agent stops; a card shows the brief for the other agent",
                () =>
                {
                    _handOffWhenStopped = other;
                    _agentCancellation?.Cancel();
                }),
            new PanelAction("Go on", "The agent works on; when the limit stops it, the same choices come again", () => { })
        ]);
        _task?.Add(AgentEntryKind.Limit, $"{AgentName(provider)} near its limit: {_limits.Describe(provider)}");
        SetStatus($"{AgentName(provider)} is near its usage limit · the Task tab offers a way on");
    }

    private string AgentHeader(AgentTask task)
    {
        var access = task.PlanOnly ? "plan"
            : task.AnswerFile is { } file ? $"read only, {Path.GetFileName(file)}"
            : task.AnswerIsSkill ? "read only, SKILL.md"
            : task.Access == AgentAccess.Do ? task.AutoApprove ? "auto" : "edits"
            : "read only";
        var level = LevelOf(task) is { } known ? $"{known} · " : string.Empty;
        var elapsed = _agentClock.Elapsed;
        return $"{_agents.Provider(task.Provider).Name} · {level}{SetupWords(task.Provider, task.Model, task.Effort)} · {access} · " +
               $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>A run's model and effort as the user reads them.</summary>
    private string SetupWords(AgentProviderKind provider, string? model, string? effort)
    {
        var agent = _agents.Provider(provider);
        var modelName = model is null ? "own model" : agent.Models.FirstOrDefault(choice => choice.Value == model)?.Label ?? model;
        return effort is null ? modelName : $"{modelName} · {agent.Efforts.FirstOrDefault(choice => choice.Value == effort)?.Label.Split(" — ")[0] ?? effort}";
    }

    private string RunLine(AgentRunRecord run) =>
        $"{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort)} · " +
        $"{(run.Access != AgentAccess.Do ? "read only" : run.AutoApprove ? "auto" : "edits")}";

    private void ShowAgentRun(AgentRun run)
    {
        _lastRun = run;
        _runningFiles.Clear();
        var (task, result) = (run.Task, run.Result);
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        _agentPanel!.ShowChanges(run.Changes.Select(ToChangeRow).ToArray());
        var elapsed = $"{(int)_agentClock.Elapsed.TotalMinutes}:{_agentClock.Elapsed.Seconds:00}";
        var stopped = result.Stopped || run.Check is { Outcome: AgentCheckOutcome.Stopped };
        // A red check is not "done", whatever the agent's own summary says.
        var checkFailed = !stopped && run.Check is { Passed: false };
        var ending = stopped ? $"Stopped after {elapsed}"
            : !result.Succeeded ? $"Failed after {elapsed}"
            : checkFailed ? $"Check failed after {elapsed}"
            : $"Done in {elapsed}";
        // An answer Link wrote to a file is shown as written.
        var answer = task.AnswerFile is null && !task.AnswerIsSkill ? result.Summary : AgentCoordinator.Unfenced(result.Summary);
        var text = checkFailed ? CheckReport(task, run.Check!) + (answer.Length > 0 ? "\n\n" + answer : string.Empty) : answer;
        _agentPanel.ShowResult(text, ending);
        _agentPanel.ShowStats(StatsLine(result.Stats));
        _agentPanel.SetReplyTarget(provider.Name);
        var limitReached = !result.Succeeded && !stopped &&
                           (LimitReached.IsMatch(result.Summary) || _limits.ReachedUntil(task.Provider) is not null);
        // After a failure, or a Stop in a loop; a Stop without a loop was the user's own choice, and a task done in the
        // end got past its loop. A limit has its own card.
        var failed = !stopped && !limitReached && (!result.Succeeded || checkFailed);
        _agentPanel.ShowRestart(_agentOrigin is not null && (failed || (stopped && result.Loops.Count > 0)),
            escalate: LevelOf(task) != AgentLevel.Deep);
        _agentPanel.ShowReply(result.SessionId is not null);
        var undo = UndoAction(run);
        var saveSkill = result.Succeeded && !stopped && !checkFailed && !task.PlanOnly && task.AnswerFile is null &&
                        !task.AnswerIsSkill && result.SessionId is not null;
        _afterRun = new[]
        {
            new PanelAction("Discuss in chat", "The result, the changed files and their diff go with your next question to ChatGPT",
                () => _ = DiscussInChatAsync()),
            undo,
            saveSkill
                ? new PanelAction("Save as skill", "The agent describes what it just did as a skill; Link writes it to .agents/skills in the workspace",
                    () => _ = SaveSkillAsync())
                : null,
            new PanelAction("Console", "What Link gave the agent's CLI and what it wrote back", ShowAgentConsole)
        }.OfType<PanelAction>().ToArray();

        var decision = OfferDecision(run);
        RecordRun(run, ending, text, decision, stopped, checkFailed);
        ShowTask();
        if (limitReached) OfferLimitReached(task.Provider);
        if (_task is not null && IsUserWork(task) && !task.AnswerIsSkill) ShowResultInChat(run, diff: null);

        var summary = text.Length > 0 ? text : ending;
        if (decision)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{provider.Name} needs your decision.");
            NotifyIfAway(project, "Agent needs a decision", ToPreviewText(summary));
        }
        else if (result.Succeeded && !stopped && !checkFailed)
        {
            if (!IsVisible) _unseen = LinkState.Done;
            _cues.Play(SoundCue.Done);
            SayAgent(SpokenSummary(result.Summary, run.Changes.Count));
            NotifyIfAway(project, "Agent finished", ToPreviewText(summary));
        }
        else if (!stopped)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.Error);
            SayAgent(checkFailed ? $"{provider.Name} finished, but the check fails." : $"{provider.Name} failed.");
            NotifyIfAway(project, checkFailed ? "The check fails after the agent" : "Agent failed", ToPreviewText(summary));
        }

        SetStatus(limitReached
            ? $"{provider.Name} ran out of its limit · the Task tab offers a way on"
            : $"{provider.Name}: {ending.ToLowerInvariant()} · {run.Changes.Count} files changed");
    }

    /// <summary>
    /// The run joins the task's record: its own tokens (a Codex session reports its total, so the runs before in that
    /// session are taken off), its line in the history, the check, the agent's last words, the files and the steps.
    /// </summary>
    private void RecordRun(AgentRun run, string ending, string text, bool decision, bool stopped, bool checkFailed)
    {
        if (_task is not { } record || !IsUserWork(run.Task)) return;
        var (task, result) = (run.Task, run.Result);
        var stats = result.Stats;
        var earlier = stats.SessionTokens
            ? record.Runs.Where(previous => previous.SessionId == result.SessionId).ToArray()
            : [];
        record.Runs.Add(new AgentRunRecord
        {
            Provider = task.Provider,
            Model = task.Model,
            Effort = task.Effort,
            Access = task.Access,
            AutoApprove = task.AutoApprove,
            SessionId = result.SessionId,
            StartedAt = DateTimeOffset.Now - stats.Duration,
            Duration = stats.Duration,
            Outcome = ending,
            InputTokens = Less(stats.InputTokens, earlier.Sum(previous => previous.InputTokens ?? 0)),
            CachedTokens = Less(stats.CachedTokens, earlier.Sum(previous => previous.CachedTokens ?? 0)),
            OutputTokens = Less(stats.OutputTokens, earlier.Sum(previous => previous.OutputTokens ?? 0)),
            CostUsd = stats.CostUsd
        });
        record.Add(AgentEntryKind.Run,
            $"{AgentName(task.Provider)} · {SetupWords(task.Provider, task.Model, task.Effort)}: {ending.ToLowerInvariant()}, {Plural(run.Changes.Count, "file")} changed");
        if (run.Check is { } check)
        {
            record.LastCheck = $"{task.CheckCommand}: {check}" + (check.Tail.Length > 0 ? "\n" + string.Join('\n', check.Tail.Split('\n').TakeLast(20)) : string.Empty);
            record.Add(AgentEntryKind.Check, $"{task.CheckCommand}: {check}");
        }

        if (text.Trim().Length > 0) record.Add(AgentEntryKind.Said, ToPreviewText(text));
        record.LastWords = text;
        record.ChangedFiles = run.Changes.Select(change => Relative(change.Path)).ToList();
        record.Steps = [.. _taskSteps.TakeLast(StepsHandedOn)];
        record.Status = decision ? AgentTaskStatus.NeedsYou
            : stopped ? AgentTaskStatus.Stopped
            : !result.Succeeded ? AgentTaskStatus.Failed
            : checkFailed ? AgentTaskStatus.CheckFailed
            : AgentTaskStatus.Done;
        SaveTask(record);

        static long? Less(long? total, long before) => total is { } value ? Math.Max(0, value - before) : null;
    }

    private void SaveTask(AgentTaskRecord record)
    {
        try
        {
            _projects.SaveAgentTask(_taskProject ?? _projects.ActiveProject, record);
            ShowProjectHistory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsReported?.Invoke($"Agent task not saved: {exception.Message}");
        }
    }

    /// <summary>
    /// The run ended on its usage limit: hand the task to the other agent, or let the same session go on by itself
    /// once the limit resets. Nothing happens without a click.
    /// </summary>
    private void OfferLimitReached(AgentProviderKind provider)
    {
        var until = _limits.ReachedUntil(provider);
        var other = Other(provider);
        _task?.Add(AgentEntryKind.Limit, until is { } reset
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(reset)}"
            : $"{AgentName(provider)} hit its usage limit");
        var actions = new List<PanelAction>
        {
            new($"Hand off to {AgentName(other)}", "A new session of the other agent with a brief of the task; you see it first",
                () => _ = ShowHandOffCardAsync(other))
        };
        if (until is { } at)
        {
            actions.Add(new PanelAction($"Continue at {AgentLimits.Time(at.AddMinutes(1))}",
                "The same session goes on by itself a minute after the limit resets, while Link runs", () => ContinueAfterReset(at)));
        }

        _agentPanel?.ShowLimit(until is { } time
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(time)}."
            : $"{AgentName(provider)} hit its usage limit.", actions);
        if (_task is not null) SaveTask(_task);
    }

    /// <summary>The session goes on by itself a minute after the limit resets; Finish, Hand off or a new run cancels it.</summary>
    private void ContinueAfterReset(DateTimeOffset reset)
    {
        _continueAt?.Stop();
        var wait = reset.AddMinutes(1) - DateTimeOffset.Now;
        _continueAt = new DispatcherTimer { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1) };
        _continueAt.Tick += (_, _) =>
        {
            _continueAt?.Stop();
            if (AgentRunning || _task is null) return;
            _task.Add(AgentEntryKind.Limit, "The limit reset: the session goes on");
            ContinueAgent("Your usage limit has reset. Go on with the task from where you stopped.");
        };
        _continueAt.Start();
        _task?.Add(AgentEntryKind.You, $"Continue at {AgentLimits.Time(reset.AddMinutes(1))}");
        ShowTask();
        SetStatus($"The task goes on by itself at {AgentLimits.Time(reset.AddMinutes(1))} while Link runs");
    }

    /// <summary>Everything the Task tab shows of the open task: plan, history, what Link gave, usage, menus.</summary>
    private void ShowTask()
    {
        if (_agentPanel is null || _task is not { } record) return;
        _agentPanel.ShowPlan(record.Plan);
        _agentPanel.ShowHistory(record.Entries.Select(entry => $"{AgentLimits.Time(entry.At)}  {EntryPrefix(entry.Kind)}{entry.Text}").ToArray());
        _agentPanel.ShowBrief(record.Brief);
        _agentPanel.ShowUsage(UsageText(record));
        _agentPanel.ShowMenus(HandOffActions(record), _afterRun);
        _agentPanel.ShowTaskButtons(startOver: _lastRun is not null && _agentOrigin is not null, finish: true);
        UpdateAgentPill();
    }

    private static string EntryPrefix(AgentEntryKind kind) => kind switch
    {
        AgentEntryKind.Brief => "Task: ",
        AgentEntryKind.You => "You: ",
        AgentEntryKind.Check => "Check: ",
        AgentEntryKind.HandOff => "Hand-over: ",
        AgentEntryKind.Model => "Model: ",
        AgentEntryKind.Limit => "Limit: ",
        AgentEntryKind.Said => "Said: ",
        _ => string.Empty
    };

    /// <summary>
    /// Hand off ▾: the other agent in a new session (with a brief to look at first), another level of the same agent in
    /// the same session, or the first agent back in its own session.
    /// </summary>
    private IReadOnlyList<PanelAction> HandOffActions(AgentTaskRecord record)
    {
        if (ResumableTask() is not { } resume) return [];
        var current = resume.Task.Provider;
        var other = Other(current);
        var actions = new List<PanelAction>
        {
            new($"{AgentName(other)} · new session", $"{AgentName(other)} takes the task over with a brief of it; you see and can change the brief first",
                () => _ = ShowHandOffCardAsync(other))
        };
        var level = LevelOf(resume.Task);
        foreach (var each in Enum.GetValues<AgentLevel>().Where(each => each != level))
        {
            actions.Add(new PanelAction($"{AgentName(current)} · {each} · same session",
                $"The same session goes on with {LevelSetup(current, each)}: it keeps what it knows, the model reads it anew once",
                () => SwitchLevel(each)));
        }

        if (record.Runs.Any(run => run.Provider == other && run.SessionId is not null))
        {
            actions.Add(new PanelAction($"Back to {AgentName(other)} · its own session",
                $"{AgentName(other)} goes on in its own session, told what {AgentName(current)} did meanwhile",
                () => _ = ReturnToAsync(other)));
        }

        return actions;
    }

    /// <summary>The task's tokens by agent, and each agent's usage windows as last reported; the money is a hint only.</summary>
    private string UsageText(AgentTaskRecord record)
    {
        var lines = record.Runs.GroupBy(run => run.Provider).Select(runs =>
        {
            var input = runs.Sum(run => run.InputTokens ?? 0);
            var cached = runs.Sum(run => run.CachedTokens ?? 0);
            var output = runs.Sum(run => run.OutputTokens ?? 0);
            var tokens = input > 0 ? $", {Tokens(input)} tokens in ({Tokens(cached)} cached), {Tokens(output)} out" : string.Empty;
            return $"This task, {AgentName(runs.Key)}: {Plural(runs.Count(), "run")}{tokens}";
        }).ToList();
        lines.AddRange(_agents.Providers.Select(agent => (agent.Name, Limits: _limits.Describe(agent.Kind)))
            .Where(agent => agent.Limits.Length > 0)
            .Select(agent => $"{agent.Name}: {agent.Limits}"));
        var cost = record.Runs.Sum(run => run.CostUsd ?? 0);
        _agentPanel?.SetUsageHint(cost > 0
            ? $"≈ {cost.ToString("$0.00", CultureInfo.InvariantCulture)} at API prices (Claude Code reports it). On a subscription it is no bill: the windows above are what runs out."
            : "The windows are what runs out on a subscription: the 5-hour one and the week.");
        return string.Join('\n', lines);
    }

    /// <summary>The agent's pill in the card's header and the Task tab's dot: who works and how the task stands.</summary>
    private void UpdateAgentPill()
    {
        if (_task is not { } task && !AgentRunning)
        {
            AgentPill.Visibility = Visibility.Collapsed;
            _panel?.ShowTaskDot(null);
            return;
        }

        var provider = _runningTask?.Provider ?? _task?.LastRun?.Provider ?? ProjectProvider;
        var name = ShortName(provider);
        var elapsed = _agentClock.Elapsed;
        var (text, brush) = AgentRunning
            ? ($"{name} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", "BusyOrbBrush")
            : _task!.Status switch
            {
                AgentTaskStatus.NeedsYou => ($"{name} needs you", "ErrorOrbBrush"),
                AgentTaskStatus.Done => ($"{name} done", "SuccessOrbBrush"),
                AgentTaskStatus.CheckFailed => ($"{name}: check fails", "ErrorOrbBrush"),
                AgentTaskStatus.Failed => ($"{name} failed", "ErrorOrbBrush"),
                AgentTaskStatus.Stopped => ($"{name} stopped", "IdleOrbBrush"),
                _ => (name, "IdleOrbBrush")
            };
        var dot = (Brush)FindResource(brush);
        AgentPillText.Text = text;
        AgentPillDot.Fill = dot;
        AgentPill.ToolTip = $"{_task?.Goal ?? "Agent"}\n{text}" +
                            (_limits.Describe(provider) is { Length: > 0 } limits ? $"\nUsage: {limits}" : string.Empty) +
                            "\nClick to open the task";
        AgentPill.Visibility = Visibility.Visible;
        _panel?.ShowTaskDot(dot);
    }

    private void AgentPill_OnClick(object sender, RoutedEventArgs e)
    {
        ShowAgentPanel();
        ShowTask();
    }

    private static string StatusWords(AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.Running => "Running",
        AgentTaskStatus.NeedsYou => "Needs you",
        AgentTaskStatus.Done => "Done",
        AgentTaskStatus.CheckFailed => "Check failed",
        AgentTaskStatus.Failed => "Failed",
        AgentTaskStatus.Stopped => "Stopped",
        _ => "Finished"
    };

    /// <summary>
    /// What only the user can decide after a run: retry with edits what read only did not let the agent do, allow what
    /// Claude was refused and go on, or carry out a plan. Returns whether a decision is offered.
    /// </summary>
    private bool OfferDecision(AgentRun run)
    {
        var (task, result) = (run.Task, run.Result);
        _decision = null;
        _agentPanel!.HideDecision();
        if (result.Stopped || result.SessionId is not { } session) return false;
        // Changing files or running any command is never allowed in read only: it is a retry with edits, except for a
        // task whose file Link writes itself (a map, a skill).
        var claude = task.Provider == AgentProviderKind.ClaudeCode;
        var readOnly = task.Access == AgentAccess.Analyze;
        var answersInFile = task.AnswerFile is not null || task.AnswerIsSkill;
        if (readOnly && !task.PlanOnly && !answersInFile && result.Denials.Count > 0 &&
            (!claude || result.Denials.Any(AgentPermissions.ChangesThings)))
        {
            var rules = claude ? result.Denials : [];
            _agentPanel.ShowDecision(
                $"{_agents.Provider(task.Provider).Name} needs to change files or run commands, and read only does not let it. " +
                $"Retry with edits in {task.Workspace}?",
                "Retry with edits",
                canRemember: claude);
            _decision = () =>
            {
                if (_rememberAllowance) RememberAllowance(AgentAccess.Do, rules);
                // A copy of a workspace without git is made before the edits, as the card does by default.
                _backupBeforeDo = true;
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    Access = AgentAccess.Do,
                    AllowedTools = task.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal).ToArray(),
                    Prompt = "You may change files in the workspace now. Carry out the task." +
                             (task.CheckCommand is { } check ? " " + AgentCoordinator.CheckNote(check) : string.Empty),
                    Images = []
                }, null);
            };
            return true;
        }

        // In read only, only what looks can be allowed.
        var allowable = result.Denials.Where(rule => !readOnly || !AgentPermissions.ChangesThings(rule)).ToArray();
        if (claude && allowable.Length > 0)
        {
            IReadOnlyList<string> rules = allowable;
            _agentPanel.ShowDecision(
                $"Claude Code was not allowed: {string.Join(", ", rules)}. Allow {(rules.Count == 1 ? "it" : "them")} and continue?",
                "Allow and retry",
                canRemember: true);
            _decision = () =>
            {
                if (_rememberAllowance) RememberAllowance(task.Access, rules);
                var allowed = task.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal).ToArray();
                _task?.Add(AgentEntryKind.You, $"Allowed {string.Join(", ", rules)}");
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    AllowedTools = allowed,
                    Prompt = $"These tools are allowed now: {string.Join(", ", rules)}. Continue the task.",
                    Images = []
                }, run);
            };
            return true;
        }

        if (task.PlanOnly && result.Succeeded)
        {
            var edits = task.Access == AgentAccess.Do;
            // The plan was made on Deep; it is carried out on the level the card chose, in the same session.
            var (model, effort) = _settings.Current.Agent.PlanOnDeep ? _settings.Current.Agent.Level(task.Provider, _planLevel) : (task.Model, task.Effort);
            _agentPanel.ShowDecision(
                (edits ? "The plan is above. Carry it out with edits in the workspace?" : "The plan is above. Carry it out, read only?") +
                ((model, effort) != (task.Model, task.Effort) ? $" It runs on {_planLevel}: {LevelSetup(task.Provider, _planLevel)}." : string.Empty),
                "Run the plan",
                canRemember: false);
            // The plan read only; the copy before edits is made now.
            _decision = () =>
            {
                _task?.Add(AgentEntryKind.You, "Run the plan");
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    PlanOnly = false,
                    Prompt = "Carry out the plan.",
                    Images = [],
                    Model = model,
                    Effort = effort
                }, null);
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// "Always for this project": in Do the rules join the Do list, in read only the read list, except a rule that
    /// changes things, which belongs to Do whatever the access.
    /// </summary>
    private void RememberAllowance(AgentAccess access, IEnumerable<string> rules)
    {
        var project = _projects.ActiveProject;
        foreach (var rule in rules)
        {
            var list = access == AgentAccess.Do || AgentPermissions.ChangesThings(rule) ? project.AgentDoTools : project.AgentReadTools;
            if (!list.Contains(rule)) list.Add(rule);
        }

        _projects.Save();
    }

    /// <summary>Undo the agent's changes (git) or restore the copy (no git), when there is something to undo.</summary>
    private PanelAction? UndoAction(AgentRun run)
    {
        var undoable = run.Changes.Count(change => change.Undoable);
        if (run.Before.IsGit && undoable > 0)
        {
            return new PanelAction($"Undo agent changes ({undoable})",
                "Put back the files the agent changed that had no changes of yours before the task", () => _ = UndoAgentChangesAsync());
        }

        return run.Before.Backup is not null && run.Changes.Count > 0
            ? new PanelAction("Restore the copy", $"Put the workspace back as it was before the task, from {run.Before.Backup}",
                () => _ = UndoAgentChangesAsync())
            : null;
    }

    private async Task UndoAgentChangesAsync()
    {
        if (_lastRun is not { } run || AgentRunning) return;
        var git = run.Before.IsGit;
        var files = git ? run.Changes.Where(change => change.Undoable).ToArray() : run.Changes.ToArray();
        var kept = run.Changes.Count - files.Length;
        var question = git
            ? $"Undo the agent's changes in {files.Length} files? New files are deleted, changed ones come back from the last commit." +
              (kept > 0 ? $" {kept} files you had changed before the task stay as they are." : string.Empty)
            : $"Put {files.Length} files back from the copy made before the task? Files the agent added are deleted.";
        if (MessageBox.Show(this, question, "Link · undo agent changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (git) await _agents.Changes.UndoAsync(run.Before, files, CancellationToken.None);
            else await _agents.Changes.RestoreAsync(run.Before, files, CancellationToken.None);
            var left = await _agents.Changes.CompareAsync(run.Before, CancellationToken.None);
            _lastRun = run with { Changes = left };
            _agentPanel?.ShowChanges(left.Select(ToChangeRow).ToArray());
            _afterRun = _afterRun.Where(action => !action.Label.StartsWith("Undo", StringComparison.Ordinal) && action.Label != "Restore the copy")
                .Prepend(UndoAction(_lastRun)).OfType<PanelAction>().ToArray();
            if (_task is not null)
            {
                _task.ChangedFiles = left.Select(change => Relative(change.Path)).ToList();
                _task.Add(AgentEntryKind.You, left.Count == 0 ? "Undid the agent's changes" : $"Undid the agent's changes; {left.Count} files still differ");
                SaveTask(_task);
                ShowTask();
            }

            SetStatus(left.Count == 0 ? "The agent's changes were undone" : $"Undone; {left.Count} files still differ");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"Could not undo: {exception.Message}");
        }
    }

    /// <summary>
    /// The task's result goes back into the discussion: a chip for the next Chat question, which × removes. After each
    /// run it comes by itself, without the diff; Discuss in chat adds the diff and turns the card to ChatGPT.
    /// </summary>
    private void ShowResultInChat(AgentRun run, string? diff)
    {
        if (_task is not { } record) return;
        var files = record.ChangedFiles.Select(file => "- " + file).ToArray();
        var text = new StringBuilder($"Task: {record.Goal}\nOutcome: {StatusWords(record.Status)}")
            .Append(record.LastRun is { } last ? $" ({AgentName(last.Provider)}, {last.Outcome.ToLowerInvariant()})" : string.Empty)
            .Append(files.Length > 0 ? "\n\nChanged files:\n" + string.Join('\n', files) : "\n\nNo files changed.");
        if (record.LastCheck is { } check) text.Append("\n\nCheck:\n").Append(check);
        if (run.Result.Summary.Trim() is { Length: > 0 } words) text.Append("\n\nThe agent's last words:\n").Append(Shorten(words, 1500));
        if (diff is not null) text.Append("\n\nThe diff:\n```diff\n").Append(diff).Append("\n```");
        foreach (var old in _context.Where(item => item.Type == ContextItemType.AgentResult).ToArray()) _context.Remove(old);
        AddContext(new ContextItem
        {
            Type = ContextItemType.AgentResult,
            Label = diff is null ? $"Task result · {Shorten(record.Goal, 40)}" : $"Task result with diff · {Shorten(record.Goal, 30)}",
            Text = text.ToString()
        });
    }

    /// <summary>Discuss in chat: the result with its diff goes with the next question to ChatGPT, the card turned there.</summary>
    private async Task DiscussInChatAsync()
    {
        if (_lastRun is not { } run) return;
        var diff = await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: false, DiffForChat, CancellationToken.None)
                   ?? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, DiffForChat, CancellationToken.None);
        ShowResultInChat(run, diff ?? "(no diff: the workspace has no git, or the files are new)");
        if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
        ShowAndActivate();
        PromptBox.Focus();
        SetStatus("The task's result and its diff are in the context: ask ChatGPT about them");
    }

    /// <summary>
    /// → Agent task under an answer: a card whose brief is the part of the answer the user selected, else the answer's
    /// link-task block, else the whole answer, with the question it answered; the snapshot's context goes along.
    /// </summary>
    private void ToAgent_OnClick(object sender, RoutedEventArgs e)
    {
        if (_attemptOnScreen is not { } shown) return;
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        var selected = ResponseViewer.Selection?.Text.Trim() ?? string.Empty;
        var source = selected.Length >= 20 ? selected : TaskBlocks(_responseText).LastOrDefault() ?? _responseText.Trim();
        var brief = source.Length == 0 ? shown.Snapshot.Text
            : shown.Snapshot.Text.Length > 0 ? $"{source}\n\nFrom the discussion of: {shown.Snapshot.Text}"
            : source;
        OpenAgentCard(shown.Snapshot, brief);
    }

    /// <summary>"Give to agent" on a link-task block of an answer: a card with that block as its brief.</summary>
    private void GiveToAgent(string brief)
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        var project = _projects.ActiveProject;
        OpenAgentCard(_attemptOnScreen?.Snapshot ?? new InputSnapshot { ProjectId = project.Id, ProjectName = project.Name }, brief.Trim());
    }

    /// <summary>The bodies of an answer's fenced link-task blocks, the tasks ChatGPT wrote for the agent.</summary>
    private static IEnumerable<string> TaskBlocks(string answer) =>
        Regex.Matches(answer, @"```link-task[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(block => block.Length > 0);

    /// <summary>Why a task is not done: the check, how it ended and its last lines.</summary>
    private static string CheckReport(AgentTask task, AgentCheckResult check)
    {
        var last = check.Tail.Split('\n').TakeLast(12);
        return $"The check fails: {task.CheckCommand} {check}." + (check.Tail.Length > 0 ? "\n" + string.Join('\n', last) : string.Empty);
    }

    /// <summary>What the run cost, in one line: "12 turns · 34 tool calls, 20 searches · 1 tool error · 152k tokens in…".</summary>
    private static string StatsLine(AgentStats stats)
    {
        var parts = new List<string>();
        if (stats.Turns > 0) parts.Add(Plural(stats.Turns, "turn"));
        var before = stats.SearchesBeforeChange is { } count ? $" ({count} before the first change)" : string.Empty;
        parts.Add(stats.ToolCalls == 0
            ? "no tool calls"
            : $"{Plural(stats.ToolCalls, "tool call")}, {Plural(stats.Searches, "search", "searches")}{before}");
        if (stats.ToolErrors > 0) parts.Add(Plural(stats.ToolErrors, "tool error"));
        if (stats.InputTokens is { } input)
        {
            parts.Add($"{Tokens(input)} tokens in ({Tokens(stats.CachedTokens ?? 0)} cached), {Tokens(stats.OutputTokens ?? 0)} out" +
                      (stats.SessionTokens ? " for the session" : string.Empty));
        }

        return string.Join(" · ", parts);
    }

    private static string Plural(int number, string one, string? many = null) => $"{number} {(number == 1 ? one : many ?? one + "s")}";

    private static string Tokens(long number) => number switch
    {
        < 1_000 => number.ToString(CultureInfo.InvariantCulture),
        < 100_000 => (number / 1_000.0).ToString("0.#k", CultureInfo.InvariantCulture),
        < 1_000_000 => (number / 1_000.0).ToString("0k", CultureInfo.InvariantCulture),
        _ => (number / 1_000_000.0).ToString("0.#M", CultureInfo.InvariantCulture)
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private AgentChangeRow ToChangeRow(AgentChange change)
    {
        var kind = change.Kind switch
        {
            "??" or "A" => "+",
            "D" => "−",
            _ => "~"
        };
        var note = change.Undoable ? string.Empty : "  (had your changes before)";
        return new AgentChangeRow($"{kind} {Relative(change.Path)}{note}", change.Path);
    }

    private string Relative(string path) =>
        _projects.ActiveProject.WorkspaceRoot is { } root && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path)
            : path;

    /// <summary>Shows the file in Explorer: opening it could run it, and a changed script is not to be run by a click.</summary>
    private static void ShowFile(string path)
    {
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true })?.Dispose();
    }

    private static bool HasGit(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return true;
        }

        return false;
    }

    /// <summary>The agent speaks only its start, a needed decision and its result, briefly, when replies are read aloud.</summary>
    private void SayAgent(string text)
    {
        if (!_settings.Current.VoiceReplies || !_voice.CanReadAloud || _voice.Phase != VoicePhase.Idle) return;
        _ = SayAgentAsync(text);
    }

    private async Task SayAgentAsync(string text)
    {
        try
        {
            await _voice.ReadAloudAsync(text);
        }
        catch (Exception exception)
        {
            DiagnosticsReported?.Invoke($"Agent voice failed: {exception.Message}");
        }
    }

    /// <summary>One or two sentences of the result, without code, paths or lists: the rest is on screen.</summary>
    private static string SpokenSummary(string summary, int changedFiles)
    {
        var plain = Regex.Replace(ToPreviewText(summary), @"`[^`]*`|\S*[\\/]\S*", string.Empty);
        var sentences = Regex.Split(plain, @"(?<=[.!?])\s+").Where(sentence => sentence.Trim().Length > 0).Take(2);
        var spoken = string.Join(' ', sentences).Trim();
        if (spoken.Length > 220) spoken = spoken[..220];
        return spoken.Length > 0 ? spoken : changedFiles > 0 ? $"Done, {changedFiles} files changed." : "Done.";
    }
}
