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

// Agent: a CLI agent (Claude Code, Codex or the custom model) works on one task of the user's in the project's workspace. The Tasks tab
// lists the project's tasks, a card comes before a new one, and a task shows as one thread, as in the agents' own apps:
// the user's words, each run with its steps folded, its answer and files, notes of hand-overs, models and limits, the
// agent's question as a choice. Stop, corrections, decisions, models, hand-overs and undo work on the open task. The
// agent stays out of the chat's way: no status line of its own steps, and of the voice only "started" and "finished".
public partial class MainWindow
{
    // A run that ends on one of these ran out of its limit: the other agent can take the task over.
    private static readonly Regex LimitReached = new(
        @"\b(usage|rate|session|weekly|daily|5-hour|five-hour) limit\b|hit your (usage )?limit|limit reached|out of (credits|usage)|quota",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan ActivityFlush = TimeSpan.FromMilliseconds(100);
    // So many of the task's last steps are kept with it, for an agent that takes it over.
    private const int StepsHandedOn = 40;
    // So many of a run's steps stay with the run in the task's record, for its thread.
    private const int StepsKeptPerRun = 60;
    // How much diff goes with "Discuss in chat"; a bigger one goes as numbers of lines.
    private const int DiffForChat = 30_000;
    private readonly AgentCoordinator _agents;
    private readonly AgentLimits _limits;
    private readonly List<AgentEvent> _pendingAgentEvents = [];
    // The task's steps in Activity's words, oldest first: what an agent that takes the task over is told was done.
    private readonly List<string> _taskSteps = [];
    // The running run's own steps, kept with it in the task's record.
    private readonly List<string> _runSteps = [];
    // What the running session holds and the model's window, as the CLI last told.
    private (long? Tokens, long? Window) _liveContext;
    private AgentConsoleWindow? _agentConsoleWindow;
    private AgentPanel? _agentPanel;
    // What Run on the card does: start a task, or hand the open one over with the brief on the card.
    private Func<AgentPlan, Task>? _cardRun;
    private InputSnapshot? _agentSnapshot;
    // The open task: its record holds everything its thread shows; null when there is none.
    private AgentTaskRecord? _task;
    // The task the Tasks tab shows: the open one, or another from the list, only to be read while an agent works.
    private AgentTaskRecord? _shownTask;
    // The project the open task belongs to: the user may switch projects while it runs.
    private LinkProject? _taskProject;
    private AgentRun? _lastRun;
    private AgentTask? _runningTask;
    // The run under way in the thread: its steps arrive as the CLI writes them.
    private ThreadTurn? _liveTurn;
    // Link's own answer (the map), apart from the open task: the Tasks tab shows it on its own, with Stop.
    private OwnRun? _own;
    // Whether the Tasks tab shows Link's own answer rather than a task or the list.
    private bool _ownShown;
    // The agent whose usage the shown task or run lists under its buttons.
    private AgentProviderKind? _limitsProvider;
    // Agents whose usage is being asked for now.
    private readonly HashSet<AgentProviderKind> _askingLimits = [];
    // The task as the card or the Map button started it: what Start over starts again.
    private AgentTask? _agentOrigin;
    // A task opened from the list: the next words take up its last session again.
    private AgentTask? _earlierSession;
    // The model and effort a plan is carried out on, after Plan first ran on the strongest.
    private (string? Model, string? Effort) _planSetup;
    // The run whose plan mode was turned off: while it is the last run, the next words carry its plan out.
    private AgentRun? _planLeft;
    // The agent's question at the end of its last run: a choice until it is answered.
    private AgentQuestion? _question;
    // Words the user gave while the agent worked: a correction now (with its images), or words for after the run.
    private string? _steerWith;
    private IReadOnlyList<string> _steerImages = [];
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
    // The ⋯ menu's choices after the last run: undo and save as skill come and go with it.
    private IReadOnlyList<PanelAction> _afterRun = [];
    // Files the running agent reported changed, listed until git, Perforce or the copy tells the whole list.
    private readonly HashSet<string> _runningFiles = new(StringComparer.OrdinalIgnoreCase);
    // The specialist's name in the header, which is drawn at every tick of a run: its file is read once, not at each.
    private (string? Id, string? Workspace, string? Name) _roleName;

    private bool AgentRunning => _agentCancellation is not null;

    // The agent the project's last task chose; the custom model only while it is still set up.
    private AgentProviderKind ProjectProvider =>
        _projects.ActiveProject.AgentProvider is { } chosen && _agents.Provider(chosen).Configured
            ? chosen
            : _settings.Current.Agent.DefaultProvider;

    private string ShortName(AgentProviderKind provider) => provider switch
    {
        AgentProviderKind.ClaudeCode => "Claude",
        AgentProviderKind.Codex => "Codex",
        _ => _settings.Current.Agent.Custom.DisplayName
    };

    /// <summary>
    /// The agent that takes the task over at a usage limit: Claude and Codex each have the other, the custom model has
    /// the default agent. Hand off ▾ offers every agent, not only this one.
    /// </summary>
    private AgentProviderKind Other(AgentProviderKind provider) => provider switch
    {
        AgentProviderKind.ClaudeCode => AgentProviderKind.Codex,
        AgentProviderKind.Codex => AgentProviderKind.ClaudeCode,
        _ => _settings.Current.Agent.DefaultProvider
    };

    private string AgentName(AgentProviderKind provider) => _agents.Provider(provider).Name;

    /// <summary>Whether the open task takes the next words: it is this project's, and its agent works on it or its session can go on.</summary>
    private bool TaskTakesWords => _task is { Status: not AgentTaskStatus.Finished } &&
                                   (_taskProject ?? _projects.ActiveProject).Id == _projects.ActiveProject.Id &&
                                   (AgentRunning
                                       ? _runningTask is { } running && IsUserWork(running) && !_restartWhenStopped
                                       : ResumableTask() is not null);

    /// <summary>
    /// The input to the agent. On the Agent route Enter gives it to the open task as the next words of its thread: a
    /// correction while the agent works, a reply after it; the chips go along, the screen and pictures as images.
    /// Ctrl+Enter, or no open task, opens the card of a new one: nothing runs before Run there.
    /// </summary>
    private async Task SendToAgentAsync(bool newTask)
    {
        RemoveMissingContextFiles();
        var takesWords = !newTask && TaskTakesWords;
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            SetError(takesWords ? "Write what the agent should hear." : "Write what the agent should do.");
            return;
        }

        if (!takesWords && AgentRunning)
        {
            SetStatus("An agent is working: a new task waits until it ends. Enter on the Agent route gives it your words");
            ShowAgentPanel();
            return;
        }

        var project = _projects.ActiveProject;
        var snapshot = await TakeSnapshotAsync(project, PromptBox.Text, RequestRoute.Agent, AutoFrameOn);
        // The images stay on disk while the agent may still read them.
        _attempts.Keep(snapshot);
        if (!takesWords)
        {
            OpenAgentCard(snapshot);
            return;
        }

        var (words, images) = AgentCoordinator.Words(snapshot, newTask: false);
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        // The panel follows when it is open; closed, the pill shows the agent at work.
        if (_settings.Current.SidePanel.IsOpen) ShowTaskInPanel();
        ContinueAgent(words, images);
    }

    /// <summary>New task in the Tasks tab: a card with what is in Link's input and chips, maybe nothing yet.</summary>
    private async Task OpenNewTaskCardAsync()
    {
        if (AgentRunning)
        {
            SetStatus("An agent is working: a new task waits until it ends");
            return;
        }

        RemoveMissingContextFiles();
        var snapshot = await TakeSnapshotAsync(_projects.ActiveProject, PromptBox.Text, RequestRoute.Agent, AutoFrameOn);
        _attempts.Keep(snapshot);
        OpenAgentCard(snapshot);
    }

    /// <summary>
    /// The workspace's Map button: the agent looks through the folder read only and Link writes its answer to
    /// docs/map.md, the one change, shown and undone like any other. Link's own answer, on Quick: a run of its own
    /// in the Tasks tab, which leaves the open task as it is.
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

        var (provider, model, effort) = OwnAnswerSetup(ProjectProvider, inSession: false);
        var map = new AgentTask
        {
            Provider = provider,
            Workspace = workspace,
            Prompt = prompt,
            AllowedTools = AgentCoordinator.Tools(project, workspace, AgentAccess.Analyze),
            AnswerFile = ProjectMap.RelativePath,
            Model = model,
            Effort = effort
        };
        ShowAgentPanel();
        await StartAgentAsync(map, null);
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
        var (provider, model, effort) = OwnAnswerSetup(task.Provider, inSession: true);
        _task?.Add(AgentEntryKind.You, "Save as skill");
        await StartAgentAsync(task with { Provider = provider, Model = model, Effort = effort }, null);
    }

    /// <summary>The agent and level of Link's own answers (a map, a skill, a note), as <see cref="AgentSettings.OwnAnswers"/> settles them.</summary>
    private (AgentProviderKind Provider, string? Model, string? Effort) OwnAnswerSetup(AgentProviderKind provider, bool inSession) =>
        _settings.Current.Agent.OwnAnswers(provider, inSession, _agents.Provider(AgentProviderKind.Custom).Configured);

    /// <summary>A skill of the same name is written over only when the user says so.</summary>
    private bool ReplaceSkill(string name) =>
        MessageBox.Show(this, $"The workspace has the skill {name} already. Replace it with the new one?",
            "Link · save as skill", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>The task card for a snapshot: <paramref name="brief"/> is what the agent is told, the question by default.</summary>
    private void OpenAgentCard(InputSnapshot snapshot, string? brief = null)
    {
        _agentSnapshot = snapshot;
        var (_, images) = AgentCoordinator.Words(snapshot, newTask: true);
        var others = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>()
            .Where(item => item.FilePath is null || !images.Contains(item.FilePath))
            .Select(item => item.Label)
            .ToArray();
        var card = CardModel(brief ?? snapshot.Text, others.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", others),
            images, ProjectProvider);
        _cardRun = RunAgentAsync;
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus("Check the card and press Run: nothing runs before that");
    }

    /// <summary>The card's agents, each with its own models and efforts, Settings' choice first, and its usage.</summary>
    private AgentCardModel CardModel(string goal, string attachments, IReadOnlyList<string> images, AgentProviderKind provider)
    {
        var project = _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null;
        var roles = ProjectRoles.In(workspace);
        return new AgentCardModel(
            // The custom model is offered once it is set up; the agent the card is for stays, so that it never turns into another.
            _agents.Providers.Where(agent => agent.Configured || agent.Kind == provider).Select(agent =>
            {
                var (model, effort) = settings.Level(agent.Kind, AgentLevel.Standard);
                var own = agent.OwnSetup;
                return new AgentCardProvider(
                    agent.Kind,
                    agent.Name,
                    agent.Models,
                    agent.EffortsOf,
                    own.Model is { } ownModel ? $"Its own: {ModelName(agent, ownModel)}" : $"{agent.Name}'s own model",
                    own.Effort is { } ownEffort ? $"Its own: {EffortName(agent, ownEffort)}" : "Its own effort",
                    model,
                    effort,
                    LimitRows(agent.Kind));
            }).ToArray(),
            provider,
            project.Name,
            workspace,
            workspace is not null && (AgentChanges.IsGit(workspace) || AgentChanges.IsPerforce(workspace)),
            attachments,
            images,
            goal,
            project.CheckCommand,
            settings.DefaultAccess)
        {
            AutoEffort = settings.AutoEffort,
            Roles = roles,
            RoleId = ProjectRoles.Suggest(goal, roles)?.Id,
            WorkspaceSkills = workspace is null ? [] : ProjectSkills.In(workspace)
        };
    }

    private static string ModelName(IAgentProvider agent, string model) =>
        agent.Models.FirstOrDefault(choice => choice.Value == model)?.Label ?? model;

    private static string EffortName(IAgentProvider agent, string effort) =>
        agent.Efforts.FirstOrDefault(choice => choice.Value == effort)?.Label.Split(" — ")[0] ?? effort;

    /// <summary>Whether a run is on the strongest model and effort Settings has for its agent; the custom model has only one.</summary>
    private bool IsStrongest(AgentTask task) =>
        task.Provider == AgentProviderKind.Custom || (task.Model, task.Effort) == _settings.Current.Agent.Level(task.Provider, AgentLevel.Deep);

    /// <summary>
    /// An agent's usage windows as bars: how much is used and when each resets. Each asks the agent anew when clicked;
    /// an agent that has told nothing yet gets one row that offers it; the custom model has none.
    /// </summary>
    private IReadOnlyList<LimitRow> LimitRows(AgentProviderKind provider)
    {
        // No subscription, no windows: nothing to show or to ask.
        if (!_agents.Provider(provider).Capabilities.Subscription) return [];
        var asking = _askingLimits.Contains(provider);
        var again = $"\nClick to ask {AgentName(provider)} now: it reads the account's usage and spends no tokens";
        var known = _limits.For(provider);
        if (known.Count == 0)
        {
            return [new LimitRow($"{ShortName(provider)} · usage", 0, asking ? "asking…" : "click to ask", (Brush)FindResource("MutedBrush"),
                $"{AgentName(provider)} has told Link no usage yet.{again}", provider)];
        }

        return known.Select(limit =>
        {
            var percent = Math.Clamp(limit.UsedPercent ?? (limit.Reached ? 100 : 0), 0, 100);
            var brush = (Brush)FindResource(limit.Reached || percent >= AgentLimits.NearPercent ? "ErrorBrush" : percent >= 70 ? "AccentBrush" : "SuccessBrush");
            var resets = limit.ResetsAt is { } at ? $" · {AgentLimits.Time(at)}" : string.Empty;
            var used = limit.UsedPercent is not null ? $"{percent:0}%" : limit.Reached ? "reached" : "—";
            var hint = $"{AgentName(provider)}, the {limit.Window} window: " +
                       (limit.UsedPercent is not null ? $"{percent:0}% used" : limit.Reached ? "the limit is reached" : "no number yet") +
                       (limit.ResetsAt is { } reset ? $", resets {AgentLimits.Time(reset)}" : string.Empty) +
                       $" (as of {AgentLimits.Time(limit.SeenAt)}).{again}";
            return new LimitRow($"{ShortName(provider)} · {limit.Window}", percent, asking ? "asking…" : used + resets, brush, hint, provider);
        }).ToArray();
    }

    /// <summary>
    /// A click on an agent's usage: its CLI is asked for the account's windows now, without a model run (Claude Code's
    /// /usage, Codex's app-server), and every bar shows the answer.
    /// </summary>
    private async Task RefreshLimitsAsync(AgentProviderKind provider)
    {
        if (!_askingLimits.Add(provider)) return;
        ShowLimitRows();
        var agent = _agents.Provider(provider);
        try
        {
            var limits = await Task.Run(() => agent.ReadLimitsAsync(CancellationToken.None));
            if (limits.Count > 0) _limits.Update(limits);
            AgentStatus(limits.Count > 0
                ? $"{agent.Name}: {_limits.Describe(provider)}"
                : $"{agent.Name} told no usage: it may be signed out, or work without a subscription");
        }
        catch (OperationCanceledException)
        {
            AgentStatus($"{agent.Name} did not tell its usage in time; try again later");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AgentStatus($"Could not ask {agent.Name} for its usage: {exception.Message}");
        }
        finally
        {
            _askingLimits.Remove(provider);
            ShowLimitRows();
            UpdateAgentPill();
        }
    }

    /// <summary>The bars wherever they are: the list, the task shown and an open card.</summary>
    private void ShowLimitRows()
    {
        RefreshTaskList();
        if (_agentPanel is null) return;
        if (_limitsProvider is { } shown) _agentPanel.ShowTaskLimits(LimitRows(shown));
        foreach (var agent in _agents.Providers) _agentPanel.ShowCardLimits(agent.Kind, LimitRows(agent.Kind));
    }

    private IReadOnlyList<LimitRow> AllLimitRows() => _agents.Providers.SelectMany(agent => LimitRows(agent.Kind)).ToArray();

    /// <summary>The Tasks tab as it stands: the card, the open task, or the list.</summary>
    private void ShowAgentPanel()
    {
        if (!_settings.Current.SidePanel.IsOpen) SetPanelOpen(true);
        ShowPanel();
        _agentPanel ??= CreateAgentPanel();
        ShowPanelTab(PanelTab.Tasks);
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
        panel.EscalateRequested += Escalate;
        panel.ContinueRequested += words => ContinueAgent(words);
        panel.QueueRequested += QueueAfterRun;
        panel.FinishRequested += FinishTask;
        panel.PlanModeOffRequested += LeavePlanMode;
        panel.OpenFileRequested += ShowFile;
        panel.BackRequested += ShowTaskList;
        panel.NewTaskRequested += () => _ = OpenNewTaskCardAsync();
        panel.TaskSelected += OpenAgentTask;
        panel.OwnRunRequested += ShowOwnRun;
        panel.LimitRefreshRequested += provider => _ = RefreshLimitsAsync(provider);
        panel.ShowList(TaskRows(), AllLimitRows(), null);
        return panel;
    }

    /// <summary>Cancel on the card: back to the task it was opened from, or to the list.</summary>
    private void CancelCard()
    {
        _cardRun = null;
        _agentPanel?.HideCard();
        if (_ownShown) ShowOwnRun();
        else if (_shownTask is not null) ShowTask();
        else ShowTaskList();
    }

    /// <summary>Back to the project's tasks; an agent at work goes on, its row says so.</summary>
    private void ShowTaskList()
    {
        _shownTask = null;
        _ownShown = false;
        RefreshTaskList();
        _agentPanel?.ShowListView();
    }

    private void RefreshTaskList() => _agentPanel?.ShowList(TaskRows(), AllLimitRows(), OwnRow());

    /// <summary>Link's own run at the top of the list while it works, and after it until the next one: one click shows it.</summary>
    private TaskRow? OwnRow()
    {
        if (_own is not { } own) return null;
        var running = AgentRunning && _runningTask == own.Task;
        var status = running ? "working" : own.Status;
        return new TaskRow($"Link writes {OwnWhat(own.Task)}", "Link's own answer, apart from the tasks",
            string.Empty, $"{AgentName(own.Task.Provider)} · {status}", (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot), null);
    }

    /// <summary>The project's tasks, newest first; the open one leads while it is open, saved yet or not.</summary>
    private IReadOnlyList<TaskRow> TaskRows()
    {
        var project = _projects.ActiveProject;
        var tasks = _projects.ReadAgentTasks(project).ToList();
        if (_task is { } open && (_taskProject ?? project).Id == project.Id)
        {
            tasks.RemoveAll(task => task.Id == open.Id);
            tasks.Insert(0, open);
        }

        return tasks.Select(record =>
        {
            // The list is drawn now and then: its running row says so, the time is in the pill and the task.
            var live = AgentRunning && record.Id == _task?.Id && _runningTask is { } running && IsUserWork(running);
            var (status, brush) = live ? ("working", (Brush)FindResource("BusyOrbBrush")) : Standing(record, live: false);
            var who = record.LastRun is { } last ? AgentName(last.Provider) + " · " : string.Empty;
            return new TaskRow(record.Goal, ToPreviewText(record.LastWords), ShortTime(record.UpdatedAt), who + status, brush, record);
        }).ToArray();
    }

    /// <summary>How a task stands, in words and the colour of its dot.</summary>
    private (string Text, Brush Dot) Standing(AgentTaskRecord record, bool live)
    {
        var elapsed = _agentClock.Elapsed;
        var (text, brush) = live
            ? ($"working · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", "BusyOrbBrush")
            : record.Status switch
            {
                AgentTaskStatus.NeedsYou => ("needs you", "ErrorOrbBrush"),
                AgentTaskStatus.Done => ("done", "SuccessOrbBrush"),
                AgentTaskStatus.CheckFailed => ("check fails", "ErrorOrbBrush"),
                AgentTaskStatus.Failed => ("failed", "ErrorOrbBrush"),
                AgentTaskStatus.Stopped => ("stopped", "IdleOrbBrush"),
                AgentTaskStatus.Finished => ("finished", "IdleOrbBrush"),
                // Saved while it ran, and Link closed meanwhile.
                _ => ("interrupted", "IdleOrbBrush")
            };
        return (text, (Brush)FindResource(brush));
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

    private PanelAction ConsoleAction() => new("Console", "What Link gave the agent's CLI and what it wrote back", ShowAgentConsole);

    /// <summary>Run on the card: a new task. An open one is finished first; it stays in the list.</summary>
    private async Task RunAgentAsync(AgentPlan plan)
    {
        if (_agentSnapshot is not { } snapshot || AgentRunning) return;
        _cardRun = null;
        var project = _projects.ActiveProject;
        if (project.AgentProvider != plan.Provider)
        {
            project.AgentProvider = plan.Provider;
            _projects.Save();
        }

        if (_task is { Status: not AgentTaskStatus.Finished }) FinishTask();
        _backupBeforeDo = plan.BackupBeforeDo;
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        var agent = _settings.Current.Agent;
        // A plan is made on the strongest model and carried out with the card's, in the same session.
        _planSetup = (plan.Model, plan.Effort);
        var (model, effort) = plan.PlanFirst && agent.PlanOnDeep ? agent.Level(plan.Provider, AgentLevel.Deep) : _planSetup;
        _agentOrigin = AgentCoordinator.BuildTask(plan with { Model = model, Effort = effort }, snapshot, project);
        _taskProject = project;
        _task = new AgentTaskRecord
        {
            Goal = Shorten(plan.Goal.Split('\n')[0].Trim(), 200),
            Brief = _agentOrigin.Prompt,
            Workspace = plan.Workspace,
            AutoEffort = plan.AutoEffort,
            RoleId = plan.RoleId,
            Skills = [.. plan.Skills]
        };
        _task.Add(AgentEntryKind.Brief, plan.Goal.Trim() + ImagesNote(_agentOrigin.Images) + (plan.PlanFirst ? "\n(plan first)" : string.Empty));
        _earlierSession = null;
        _lastRun = null;
        _shownTask = _task;
        await StartAgentAsync(_agentOrigin, null, phase: EffortPhase.New);
    }

    private static string ImagesNote(IReadOnlyCollection<string> images) =>
        images.Count == 0 ? string.Empty : $"\n+ {Plural(images.Count, "image")}";

    /// <summary>
    /// The user's words. While the agent works they correct it: it stops the step it is on and the same session goes
    /// on with them. After a run they are a reply in the same session, maybe the answer to its question; a task from
    /// the list is taken up again in its folder, with a copy before edits where there is no version control.
    /// </summary>
    private void ContinueAgent(string reply, IReadOnlyList<string>? images = null)
    {
        if (reply.Trim() is not { Length: > 0 } text) return;
        images ??= [];
        if (AgentRunning)
        {
            // Link's own step (a note for Start over, a map) takes no words.
            if (_runningTask is not { } running || !IsUserWork(running) || _restartWhenStopped) return;
            _steerWith = text;
            _steerImages = images;
            _task?.Add(AgentEntryKind.You, "(correction) " + text + ImagesNote(images));
            _agentCancellation?.Cancel();
            ShowTask();
            AgentStatus("Stopping the agent to pass your words on…");
            return;
        }

        _question = null;
        _task?.Add(AgentEntryKind.You, text + ImagesNote(images));
        var phase = _planLeft is not null && ReferenceEquals(_lastRun, _planLeft) ? EffortPhase.RunPlan : EffortPhase.Reply;
        if (_earlierSession is { } earlier)
        {
            _agentOrigin = earlier with { ResumeSessionId = null };
            _backupBeforeDo = true;
            _ = StartAgentAsync(earlier with { Prompt = text, Images = images }, null, phase: phase);
            return;
        }

        if (_lastRun is { } run && AgentCoordinator.ContinueTask(run, text) is { } task)
        {
            _ = StartAgentAsync(task with { Images = images }, run, phase: phase);
        }
    }

    /// <summary>Tab while the agent works: the words wait for the end of the run, then the same session goes on with them.</summary>
    private void QueueAfterRun(string words)
    {
        if (!AgentRunning || _runningTask is null) return;
        _queued = _queued is null ? words : $"{_queued}\n{words}";
        _task?.Add(AgentEntryKind.You, "(after this run) " + words);
        ShowTask();
        AgentStatus("Your words wait for the end of this run");
    }

    /// <summary>
    /// Plan mode off: the session's next words go on with the task's access, on the model a plan is carried out with. In
    /// plan mode Claude Code refuses whatever changes things, so allowing a tool there does not help.
    /// </summary>
    private void LeavePlanMode()
    {
        if (AgentRunning || _lastRun is not { Task.PlanOnly: true } run) return;
        var (model, effort) = _settings.Current.Agent.PlanOnDeep ? _planSetup : (run.Task.Model, run.Task.Effort);
        _lastRun = _planLeft = run with { Task = run.Task with { PlanOnly = false, Model = model, Effort = effort } };
        if (_task is { } record)
        {
            record.Add(AgentEntryKind.You, "Plan mode off");
            SaveTask(record);
        }

        ShowTask();
        AgentStatus("Plan mode is off: your next words go on with edits in the same session");
    }

    /// <summary>Finish: the task is closed and stays in the list, where it can be opened and taken up again.</summary>
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
        _question = null;
        _decision = null;
        UpdateAgentPill();
        UpdateRouteButton();
        ShowTaskList();
        AgentStatus("Task finished: it stays in the list of tasks");
    }

    /// <summary>
    /// A task from the list. While an agent works on another one, it is shown to be read; otherwise it becomes the
    /// open task, and the next words take up its last session.
    /// </summary>
    private void OpenAgentTask(AgentTaskRecord record)
    {
        if (_task?.Id == record.Id)
        {
            ShowTaskInPanel();
            return;
        }

        if (AgentRunning)
        {
            _shownTask = record;
            ShowRecord(record, readOnly: true);
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
        _decision = null;
        var last = record.LastRun;
        // A question the task ended on is still a question.
        _question = record.Status == AgentTaskStatus.NeedsYou && last is not null ? AgentQuestion.Split(last.Words).Question : null;
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
                AllowedTools = AgentCoordinator.Tools(project, record.Workspace, last.Access),
                CheckCommand = project.CheckCommand,
                RoleId = record.RoleId,
                Skills = record.Skills,
                ResumeSessionId = session
            }
            : null;
        _taskSteps.Clear();
        _taskSteps.AddRange(record.Steps);
        _afterRun = [ConsoleAction()];
        _agentPanel?.ResetCards();
        ShowTaskInPanel();
        UpdateRouteButton();
        AgentStatus(_earlierSession is null
            ? "This task has no session to go on with"
            : $"Your words go on with the task: in its thread, or with Enter on the Agent route");
    }

    /// <summary>The open task in the Tasks tab.</summary>
    private void ShowTaskInPanel()
    {
        ShowAgentPanel();
        _ownShown = false;
        _shownTask = _task;
        ShowTask();
    }

    /// <summary>
    /// Runs a task, fresh or continuing <paramref name="previous"/>, and shows it in its thread; <paramref name="note"/>
    /// opens its steps, such as the note a new session starts over with. <paramref name="phase"/> is what the run is for:
    /// a task on Auto gets its effort from it; without one the task's own effort stands.
    /// </summary>
    private async Task StartAgentAsync(AgentTask task, AgentRun? previous, string? note = null, EffortPhase? phase = null)
    {
        // Every run starts here, the user's and Link's own: the CLI compacts its session near the budget of Settings.
        task = task with { ContextBudget = _settings.Current.Agent.ContextBudget, EffortReason = null };
        if (phase is { } kind) task = WithAutoEffort(task, kind);
        var provider = _agents.Provider(task.Provider);
        // Link's own answer (the map) runs apart: the open task keeps its session, its last run and its question.
        var own = !IsUserWork(task) || _task is null;
        if (!own)
        {
            // A new task starts clean; a run that goes on with the last one, or a task taken up again, keeps its past.
            if (previous is null && _task?.Runs.Count is null or 0) _agents.Console.Clear();
            if (previous is null && task.ResumeSessionId is null && _task?.Runs.Count is null or 0) _taskSteps.Clear();
            _decision = null;
            _question = null;
            _earlierSession = null;
            _continueAt?.Stop();
            _task!.Status = AgentTaskStatus.Running;
        }

        _runningTask = task;
        _limitOffered = false;
        _runSteps.Clear();
        // A session taken up again holds what its last run left, and the model's window stays until a run says otherwise.
        _liveContext = !own && _task?.LastRun is { } last && last.Provider == task.Provider
            ? (last.SessionId == task.ResumeSessionId ? last.ContextTokens : null, last.ContextWindow)
            : default;
        _runningFiles.Clear();
        _liveTurn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason)}", "Starting…",
            (Brush)FindResource("AccentBrush")) { StepsOpen = true };
        if (own) _own = new OwnRun(task, _liveTurn);
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentClock = Stopwatch.StartNew();
        _pendingAgentEvents.Clear();
        if (note is not null) _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Output, note));
        _agentPanel ??= CreateAgentPanel();
        if (own)
        {
            ShowOwnRun();
        }
        else
        {
            _agentPanel.ResetCards();
            _ownShown = false;
            _shownTask = _task;
            ShowTask();
        }

        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{ShortName(task.Provider)} начал работу.", $"{ShortName(task.Provider)} started.");
        AgentStatus(own
            ? $"{provider.Name} writes {OwnWhat(task)} · Stop is in the Tasks tab"
            : $"{provider.Name} is working · its thread is in the Tasks tab");
        UpdateAgentPill();
        UpdateRouteButton();
        RefreshTaskList();
        var progress = new Progress<AgentEvent>(_pendingAgentEvents.Add);
        AgentRun? ended = null;
        Exception? failure = null;
        try
        {
            var run = await _agents.RunAsync(task, _backupBeforeDo, previous, progress, cancellation.Token, ReplaceSkill);
            if (run.Before is { IsVersioned: false, Backup: null } && _backupBeforeDo && task.Access == AgentAccess.Do && previous is null)
            {
                _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Error, "No copy was made: the workspace is over 2 GB."));
            }

            await KeepInChangelistAsync(run);
            ended = run;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            FlushAgentEvents();
            _activityTimer.Stop();
            _agentCancellation = null;
            _runningTask = null;
            cancellation.Dispose();
            ReportState();
        }

        if (own && _own is { } done)
        {
            if (ended is not null) ShowOwnResult(done, ended);
            else if (failure is not null) ShowOwnFailure(done, failure);
            _liveTurn = null;
            UpdateAgentPill();
            RefreshTaskList();
            return;
        }

        if (ended is not null) ShowAgentRun(ended);
        else if (failure is not null) ShowAgentFailure(provider, task, failure);
        _liveTurn = null;
        UpdateAgentPill();
        UpdateRouteButton();
        RefreshTaskList();

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
            var images = _steerImages;
            _steerImages = [];
            if (ended is not null && AgentCoordinator.ContinueTask(ended, AgentCoordinator.Interrupted(words)) is { } next)
            {
                await StartAgentAsync(next with { Images = images }, ended, phase: EffortPhase.Steer);
            }
            else
            {
                SetError("The agent stopped before its session began: give your words as a new task.");
            }
        }
        else if (_queued is { } later)
        {
            _queued = null;
            if (ended is not null && AgentCoordinator.ContinueTask(ended, later) is { } next) await StartAgentAsync(next, ended, phase: EffortPhase.Reply);
        }
    }

    /// <summary>The user's work, as opposed to Link's own answer written to a file, such as the map.</summary>
    private static bool IsUserWork(AgentTask task) => task.AnswerFile is null;

    /// <summary>
    /// The run's task with the effort <see cref="AutoEffort"/> chooses for <paramref name="phase"/> and its reason, when
    /// the open task is on Auto: from the task's brief and runs and the next step the last one ended with. A task whose
    /// effort the user chose, Link's own answer and a plan made on Strong stay as they are. The model is never touched.
    /// </summary>
    private AgentTask WithAutoEffort(AgentTask task, EffortPhase phase)
    {
        var agent = _settings.Current.Agent;
        if (!agent.AutoEffort || _task is not { AutoEffort: true } record || !IsUserWork(task) || (task.PlanOnly && agent.PlanOnDeep)) return task;
        var last = record.LastRun;
        var efforts = _agents.Provider(task.Provider).EffortsOf(task.Model).Select(choice => choice.Value).ToArray();
        // An agent with no effort levels (the custom model) has nothing to pick, and no reason to show.
        if (efforts.Length == 0) return task;
        var (effort, reason) = AutoEffort.Choose(phase, BriefOf(record), efforts, last?.Effort, AutoEffort.Failures(record.Runs),
            last is null ? null : AgentNext.Split(last.Words).Next, ProjectRoles.Find(record.RoleId, record.Workspace)?.Effort);
        return task with { Effort = effort, EffortReason = reason };
    }

    /// <summary>The words the task began with, as the user gave them.</summary>
    private static string BriefOf(AgentTaskRecord record) =>
        record.Entries.FirstOrDefault(entry => entry.Kind == AgentEntryKind.Brief)?.Text ?? record.Goal;

    /// <summary>Link's own answer, such as the map: a thread of its one run, outside the tasks.</summary>
    private void ShowOwnRun()
    {
        if (_agentPanel is not { } panel || _own is not { } own) return;
        _shownTask = null;
        _ownShown = true;
        var task = own.Task;
        var running = AgentRunning && _runningTask == task;
        panel.ShowTaskView($"Link writes {OwnWhat(task)}", $"{_projects.ActiveProject.Name} · {task.Workspace}");
        panel.SetStatus(running ? "working" : own.Status, (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot));
        panel.SetHeader(running ? AgentHeader(task) : $"{AgentName(task.Provider)} · {SetupWords(task.Provider, task.Model, task.Effort)} · read only");
        panel.ShowPlan([]);
        panel.ShowThread([own.Turn]);
        panel.ShowBrief(string.Empty);
        panel.ShowUsage(string.Empty, string.Empty);
        _limitsProvider = task.Provider;
        panel.ShowTaskLimits(LimitRows(task.Provider));
        panel.ResetCards();
        panel.ShowQuestion(null);
        panel.ShowMenus([], OwnActions(own));
        panel.ShowFinish(false);
        panel.ShowPlanMode(false);
        panel.SetRunning(running);
        // Link's own answer takes no words.
        panel.ShowReply(false, running ? null : "Link's own answer: the file is written. ‹ goes back to the tasks.");
    }

    /// <summary>What Link's own run writes, as the user reads it: "docs/map.md".</summary>
    private static string OwnWhat(AgentTask task) =>
        task.AnswerFile is { } file ? file.Replace('\\', '/') : Shorten(task.Prompt.Split('\n')[0], 80);

    /// <summary>The ⋯ menu of Link's own run: undo what it wrote, and the console.</summary>
    private IReadOnlyList<PanelAction> OwnActions(OwnRun own) =>
        own.Run is { } run && UndoAction(run) is { } undo ? [undo, ConsoleAction()] : [ConsoleAction()];

    /// <summary>Link's own run ended: its answer in its turn, its file listed; the open task stays as it was.</summary>
    private void ShowOwnResult(OwnRun own, AgentRun run)
    {
        own.Run = run;
        var result = run.Result;
        var elapsed = $"{(int)_agentClock.Elapsed.TotalMinutes}:{_agentClock.Elapsed.Seconds:00}";
        var ending = result.Stopped ? $"Stopped after {elapsed}" : !result.Succeeded ? $"Failed after {elapsed}" : $"Done in {elapsed}";
        own.Status = result.Stopped ? "stopped" : !result.Succeeded ? "failed" : "done";
        own.Dot = result.Stopped ? "IdleOrbBrush" : !result.Succeeded ? "ErrorOrbBrush" : "SuccessOrbBrush";
        var turn = own.Turn;
        turn.Current = string.Empty;
        turn.Outcome = ending;
        turn.OutcomeBrush = OutcomeBrush(ending);
        turn.Said = AgentCoordinator.Unfenced(result.Summary);
        turn.Files = run.Changes.Select(ToChangeRow).ToArray();
        turn.Stats = StatsLine(result.Stats);
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

    /// <summary>Link's own run (a map): its task, its turn in the thread, and once over, the run and how it ended.</summary>
    private sealed class OwnRun(AgentTask task, ThreadTurn turn)
    {
        public AgentTask Task { get; } = task;
        public ThreadTurn Turn { get; } = turn;
        public AgentRun? Run { get; set; }
        public string Status { get; set; } = "working";
        public string Dot { get; set; } = "BusyOrbBrush";
    }

    /// <summary>Start over with a note; a running agent is stopped first, and its note is asked once it has ended.</summary>
    private void RequestRestart()
    {
        if (_runningTask is { } running && !IsUserWork(running)) return;
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
        var (asker, model, effort) = OwnAnswerSetup(run.Task.Provider, inSession: true);
        var provider = _agents.Provider(asker);
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentPanel?.ShowStep($"asking {provider.Name} for a note…", running: true);
        AgentStatus($"{provider.Name} writes a note for a new session · Stop is in the Tasks tab");
        (AgentTask Task, string Note)? next;
        try
        {
            next = await _agents.StartOverAsync(run, origin, (model, effort), cancellation.Token, asker);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _agentPanel?.ShowStep("no note", running: false);
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
            _agentPanel?.ShowStep("start over stopped", running: false);
            ShowTask();
            AgentStatus("Start over stopped: the last run stays as it was");
            return;
        }

        _task?.Add(AgentEntryKind.You, "Start over with a note" + (fresh.Note.Length > 0 ? ": " + fresh.Note : string.Empty));
        await StartAgentAsync(fresh.Task, run,
            fresh.Note.Length > 0 ? "Note from the last attempt:\n" + fresh.Note : "No note came back: the new session starts with the task alone.",
            phase: EffortPhase.Restart);
    }

    /// <summary>
    /// The same session goes on with another model and effort: cheaper for routine work, the strongest for a problem
    /// the task's model did not solve (Escalate). One cache miss: the model reads the session anew.
    /// </summary>
    private void SwitchSetup((string? Model, string? Effort) setup, bool escalate)
    {
        if (AgentRunning || ResumableTask() is not { } resume) return;
        var prompt = escalate
            ? "The work is not done yet. Think it through again from what you know, find the cause and finish the task."
            : "Go on with the task.";
        _task?.Add(AgentEntryKind.Model,
            $"{AgentName(resume.Task.Provider)} · {SetupWords(resume.Task.Provider, setup.Model, setup.Effort)}{(escalate ? ", escalated" : string.Empty)}");
        if (resume.Previous is null) _backupBeforeDo = true;
        // A level chosen by hand stays; Escalate is the rule for a failure done by hand, and Auto goes on after it.
        if (!escalate && _task is { } record) record.AutoEffort = false;
        _ = StartAgentAsync(resume.Task with { Model = setup.Model, Effort = setup.Effort, Prompt = prompt, Images = [] }, resume.Previous);
    }

    private void Escalate()
    {
        if (ResumableTask() is not { } resume) return;
        SwitchSetup(_settings.Current.Agent.Level(resume.Task.Provider, AgentLevel.Deep), escalate: true);
    }

    /// <summary>The task's last session as a run to continue, and the run it continues; null when there is none.</summary>
    private (AgentTask Task, AgentRun? Previous)? ResumableTask() =>
        _lastRun is { Result.SessionId: { } session } run ? (run.Task with { ResumeSessionId = session, PlanOnly = false }, run)
        : _earlierSession is { } earlier ? (earlier, null)
        : null;

    /// <summary>
    /// Hand off: the other agent takes the task over in a new session of its own. The brief is made from the task's
    /// record and shown on the card first, where it can be changed, with the model and access of the new run.
    /// </summary>
    private async Task ShowHandOffCardAsync(AgentProviderKind to)
    {
        if (AgentRunning || _task is not { } record || ResumableTask() is null && _agentOrigin is null) return;
        var fromProvider = record.LastRun?.Provider ?? Other(to);
        var brief = AgentCoordinator.HandOffBrief(record, AgentName(fromProvider), await NumstatAsync());
        // The images the task began with go along; a task from the list has kept none.
        var card = CardModel(brief, string.Empty, _agentOrigin?.Images ?? [], to) with
        {
            Title = $"HAND OFF TO {AgentName(to).ToUpperInvariant()} · NEW SESSION",
            RunLabel = $"Hand off to {AgentName(to)}",
            FixedProvider = true,
            AutoEffort = record.AutoEffort && _settings.Current.Agent.AutoEffort,
            // The new session works as the task's specialist, unless it is changed here.
            RoleId = record.RoleId,
            Skills = record.Skills
        };
        _cardRun = plan => HandOffAsync(plan, fromProvider);
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus($"The brief for {AgentName(to)} is on the card: change it if you like, then Hand off");
    }

    private async Task HandOffAsync(AgentPlan plan, AgentProviderKind fromProvider)
    {
        if (AgentRunning || _task is not { } record) return;
        _cardRun = null;
        var template = ResumableTask()?.Task ?? _agentOrigin;
        if (template is null) return;
        var images = _agentOrigin?.Images ?? [];
        var task = AgentCoordinator.HandOffTask(template, plan.Provider, plan.Goal, images) with
        {
            Model = plan.Model,
            Effort = plan.Effort,
            Access = AgentAccess.Do,
            AutoApprove = plan.AutoApprove,
            AllowedTools = AgentCoordinator.Tools(_taskProject ?? _projects.ActiveProject, template.Workspace, AgentAccess.Do),
            RoleId = plan.RoleId,
            Skills = plan.Skills
        };
        // Auto stays on the card's word: an effort chosen there pins it.
        record.AutoEffort = plan.AutoEffort;
        var specialist = string.Equals(record.RoleId, plan.RoleId, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $", as {RoleName(plan.RoleId, record.Workspace) ?? "General"}";
        record.RoleId = plan.RoleId;
        record.Skills = [.. plan.Skills];
        var setup = WithAutoEffort(task, EffortPhase.HandOff);
        record.Add(AgentEntryKind.HandOff,
            $"{AgentName(fromProvider)} → {AgentName(plan.Provider)} · {SetupWords(setup.Provider, setup.Model, setup.Effort)}, a new session{specialist}");
        record.Brief += $"\n\n── Handed over to {AgentName(plan.Provider)} ──\n{plan.Goal}";
        // Start over begins the new agent's brief again.
        _agentOrigin = task;
        if (_lastRun is null) _backupBeforeDo = true;
        await StartAgentAsync(task, _lastRun, phase: EffortPhase.HandOff);
    }

    /// <summary>
    /// The lines added and removed per file by the task's work, where git or Perforce tells them; null otherwise, and
    /// the task's state then lists the files of its record.
    /// </summary>
    private async Task<string?> NumstatAsync() =>
        _lastRun is { } run ? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, 4000, CancellationToken.None) : null;

    /// <summary>The open task's state as Compact shows it, for the inspector; null without an open task.</summary>
    internal async Task<string?> TaskStateText() =>
        _task is { } record ? TaskState.Build(record, await NumstatAsync(), record.Workspace, AgentName) : null;

    /// <summary>
    /// Compact: a fresh session of the same agent starts from the task's state rather than from the session that has
    /// grown. Link makes the state from the task's record, and it is shown on the card first, where it can be changed,
    /// with the model and access of the last run. The task's history stays whole.
    /// </summary>
    private async Task CompactTask()
    {
        if (AgentRunning || _task is not { } record || ResumableTask() is not { } resume) return;
        var provider = resume.Task.Provider;
        var state = TaskState.CompactBrief(record, await NumstatAsync(), record.Workspace, AgentName);
        // The images the task began with go along; a task from the list has kept none.
        var card = CardModel(state, string.Empty, _agentOrigin?.Images ?? [], provider);
        card = card with
        {
            // The card starts on the model and effort of the last run: Settings' Standard could be a silent downgrade.
            Providers = [.. card.Providers.Select(agent => agent.Kind == provider ? agent with { Model = resume.Task.Model, Effort = resume.Task.Effort } : agent)],
            Access = resume.Task.AutoApprove ? AgentAccessChoice.Auto : AgentAccessChoice.Edit,
            Title = $"COMPACT · NEW SESSION OF {AgentName(provider).ToUpperInvariant()}",
            RunLabel = "Compact",
            FixedProvider = true,
            AutoEffort = record.AutoEffort && _settings.Current.Agent.AutoEffort,
            RoleId = record.RoleId,
            Skills = record.Skills
        };
        _cardRun = CompactAsync;
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus($"A fresh session of {AgentName(provider)} starts from the state on the card: change it if you like, then Compact");
    }

    private async Task CompactAsync(AgentPlan plan)
    {
        if (AgentRunning || _task is not { } record) return;
        _cardRun = null;
        var template = ResumableTask()?.Task ?? _agentOrigin;
        if (template is null) return;
        // The card may have changed the specialist, the skills and Auto: the task keeps them.
        record.AutoEffort = plan.AutoEffort;
        record.RoleId = plan.RoleId;
        record.Skills = [.. plan.Skills];
        var task = AgentCoordinator.CompactTask(template, record, plan.Goal, _agentOrigin?.Images ?? []) with
        {
            Model = plan.Model,
            Effort = plan.Effort,
            Access = AgentAccess.Do,
            AutoApprove = plan.AutoApprove,
            AllowedTools = AgentCoordinator.Tools(_taskProject ?? _projects.ActiveProject, template.Workspace, AgentAccess.Do)
        };
        record.Add(AgentEntryKind.HandOff, TaskState.Compacted(record.LastRun?.ContextTokens));
        // Start over begins from the state, not from the session that has grown.
        _agentOrigin = task;
        if (_lastRun is null) _backupBeforeDo = true;
        await StartAgentAsync(task, _lastRun, phase: EffortPhase.Reply);
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
        // Whoever worked last is who the first agent is told of.
        var prompt = AgentCoordinator.ReturnBrief(record, AgentName(record.LastRun?.Provider ?? Other(provider)), await NumstatAsync());
        record.Add(AgentEntryKind.HandOff, $"Back to {AgentName(provider)}, its own session");
        record.Brief += $"\n\n── Back to {AgentName(provider)} ──\n{prompt}";
        if (_lastRun is null) _backupBeforeDo = true;
        await StartAgentAsync(template with
        {
            Provider = provider,
            ResumeSessionId = session,
            Prompt = prompt,
            Model = back.Model,
            Effort = back.Effort,
            Images = [],
            PlanOnly = false,
            AnswerFile = null,
            AnswerIsSkill = false
        }, _lastRun, phase: EffortPhase.HandOff);
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

    /// <summary>A step as the thread shows it; null for what has a place of its own (the answer, files, plan, usage).</summary>
    private static string? StepText(AgentEvent agentEvent) => agentEvent.Kind switch
    {
        AgentEventKind.Completed or AgentEventKind.Artifact or AgentEventKind.ToolFailed or AgentEventKind.Plan or AgentEventKind.Limit
            or AgentEventKind.Context => null,
        AgentEventKind.Tool => "▸ " + agentEvent.Text,
        AgentEventKind.Output => agentEvent.Text,
        AgentEventKind.NeedsInput => "⚠ " + agentEvent.Text,
        AgentEventKind.Loop => "⟳ " + agentEvent.Text,
        AgentEventKind.Error => "✗ " + agentEvent.Text,
        _ => "· " + agentEvent.Text
    };

    /// <summary>A step's row in the colour of its kind, by the mark it starts with.</summary>
    private AgentStepRow StepRow(string text) => new(text, (Brush)FindResource(
        text.StartsWith('✗') ? "ErrorBrush"
        : text.StartsWith('⚠') || text.StartsWith('⟳') ? "AccentBrush"
        : text.StartsWith('▸') || text.StartsWith('·') ? "MutedBrush"
        : "TextBrush"));

    private DispatcherTimer CreateActivityTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ActivityFlush };
        timer.Tick += (_, _) => FlushAgentEvents();
        return timer;
    }

    /// <summary>
    /// Events arrive as fast as the CLI writes them; the thread takes them in batches, ten times a second at most. A
    /// task shown only to be read is not touched.
    /// </summary>
    private void FlushAgentEvents()
    {
        if (_agentPanel is null) return;
        // The task's own run, or Link's own answer beside it; only what the tab shows is drawn there.
        var userWork = _runningTask is { } working && IsUserWork(working) && _task is not null;
        var shown = userWork ? !_ownShown && _shownTask is not null && _shownTask.Id == _task!.Id : _ownShown;
        if (_runningTask is { } task)
        {
            var elapsed = _agentClock.Elapsed;
            if (_liveTurn is not null) _liveTurn.Outcome = $"working · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            if (shown) _agentPanel.SetHeader(AgentHeader(task));
            if (shown && userWork)
            {
                var (status, dot) = Standing(_task!, live: true);
                _agentPanel.SetStatus(status, dot);
            }

            UpdateAgentPill();
        }

        if (_pendingAgentEvents.Count == 0) return;
        var events = _pendingAgentEvents.ToArray();
        _pendingAgentEvents.Clear();
        // Link's own answer is no step of the task: an agent that takes the task over is not told of it.
        if (userWork) RememberSteps(events);
        var steps = events.Select(StepText).OfType<string>().Select(step => Shorten(step, 1200)).ToArray();
        _runSteps.AddRange(steps.Select(step => Shorten(step, 300)));
        if (_liveTurn is { } turn)
        {
            turn.AddSteps(steps.Select(StepRow));
            if (events.LastOrDefault(agentEvent => agentEvent.Kind is AgentEventKind.Tool or AgentEventKind.Status) is { } current)
            {
                turn.Current = current.Text;
            }
        }

        if (events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Plan) is { } plan)
        {
            if (userWork) _task!.Plan = [.. plan.Plan];
            if (shown && userWork) _agentPanel.ShowPlan(plan.Plan);
        }

        if (userWork && events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Context) is { } context)
        {
            _liveContext = (context.ContextTokens, context.ContextWindow ?? _liveContext.Window);
            if (shown)
            {
                var (usage, hint) = UsageText(_task!);
                _agentPanel.ShowUsage(usage, hint);
            }
        }

        if (events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Limit))
        {
            foreach (var usage in events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Limit)) _limits.Update(usage.Limits);
            if (shown && _runningTask is { } reporting) _agentPanel.ShowTaskLimits(LimitRows(reporting.Provider));
        }

        if (userWork && _runningTask is { } running && !_limitOffered && _limits.IsNear(running.Provider)) OfferLimitAhead(running.Provider);

        // Changed files are listed as they come; the full list, from version control or the copy, replaces them at the end.
        if (_liveTurn is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Artifact))
        {
            _runningFiles.UnionWith(events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Artifact).Select(agentEvent => agentEvent.Text));
            _liveTurn.Files = _runningFiles.Select(path => new AgentChangeRow(Relative(path), path)).ToArray();
        }

        if (shown) _agentPanel.FollowThread();
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
        AgentStatus($"{AgentName(provider)} is near its usage limit · the Tasks tab offers a way on");
    }

    private string AgentHeader(AgentTask task)
    {
        var access = task.PlanOnly ? "plan"
            : task.AnswerFile is { } file ? $"read only, {Path.GetFileName(file)}"
            : task.AnswerIsSkill ? "read only, SKILL.md"
            : task.Access == AgentAccess.Do ? task.AutoApprove ? "auto" : "edits"
            : "read only";
        var elapsed = _agentClock.Elapsed;
        return $"{RoleWords(task.RoleId, task.Workspace)}{_agents.Provider(task.Provider).Name} · " +
               $"{SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason)} · {access} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>The specialist's name for a header; null for General. A role whose file is gone shows its file name.</summary>
    private string? RoleName(string? id, string workspace)
    {
        if (id is null) return null;
        if (_roleName.Id != id || _roleName.Workspace != workspace) _roleName = (id, workspace, ProjectRoles.Find(id, workspace)?.Name ?? id);
        return _roleName.Name;
    }

    /// <summary>"Rigger / Animator · " before a line of who works, when the task has a specialist.</summary>
    private string RoleWords(string? id, string workspace) => RoleName(id, workspace) is { } name ? name + " · " : string.Empty;

    /// <summary>
    /// A run's model and effort as the user reads them, "Opus 5.5 · Extra high"; the CLI's own as its config names it.
    /// Where Link chose the effort, <paramref name="autoReason"/> says why: "Opus 5.5 · Medium · auto: a usual task".
    /// </summary>
    private string SetupWords(AgentProviderKind provider, string? model, string? effort, string? autoReason = null)
    {
        var agent = _agents.Provider(provider);
        var own = agent.OwnSetup;
        var modelName = (model ?? own.Model) is { } chosen ? ModelName(agent, chosen) : "its own model";
        var words = (effort ?? own.Effort) is { } level ? $"{modelName} · {EffortName(agent, level)}" : modelName;
        return autoReason is null ? words : $"{words} · auto: {autoReason}";
    }

    private string RunLine(AgentRunRecord run, string workspace) =>
        $"{RoleWords(run.RoleId, workspace)}{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, run.EffortReason)} · " +
        $"{(run.Access != AgentAccess.Do ? "read only" : run.AutoApprove ? "auto" : "edits")}";

    /// <summary>Where the task works: the project, the folder and, in Perforce, its changelist.</summary>
    private string WhereLine(AgentTaskRecord record) =>
        $"{(_taskProject ?? _projects.ActiveProject).Name} · {record.Workspace}" +
        (record.Changelist is { } changelist ? $" · P4 changelist {changelist}" : string.Empty);

    /// <summary>In Perforce the task's files wait in a pending changelist of its own, which P4V shows; its number stays with the task.</summary>
    private async Task KeepInChangelistAsync(AgentRun run)
    {
        if (_task is not { } record || !IsUserWork(run.Task) || !run.Before.IsPerforce || run.Changes.Count == 0) return;
        // P4V's descriptions are in English whatever the task's language: the agent's own line, else a plain one.
        var english = AgentChanges.SplitChangelistLine(run.Result.Summary).Description;
        var changelist = await _agents.Changes.KeepInChangelistAsync(run.Before, run.Changes,
            english ?? $"Link: changes of an agent task ({Plural(run.Changes.Count, "file")})", english is not null,
            record.Changelist, CancellationToken.None);
        if (changelist is null)
        {
            DiagnosticsReported?.Invoke("Perforce made no changelist for the task's files: they stay where the agent opened them");
            return;
        }

        record.Changelist = changelist;
    }

    private void ShowAgentRun(AgentRun run)
    {
        _lastRun = run;
        var (task, result) = (run.Task, run.Result);
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        var elapsed = $"{(int)_agentClock.Elapsed.TotalMinutes}:{_agentClock.Elapsed.Seconds:00}";
        var stopped = result.Stopped || run.Check is { Outcome: AgentCheckOutcome.Stopped };
        // A red check is not "done", whatever the agent's own summary says.
        var checkFailed = !stopped && run.Check is { Passed: false };
        var limitReached = !result.Succeeded && !stopped &&
                           (LimitReached.IsMatch(result.Summary) || _limits.ReachedUntil(task.Provider) is not null);
        // A limit is no failure of the work: Auto effort does not raise the next run for it.
        var ending = stopped ? $"Stopped after {elapsed}"
            : limitReached ? $"Stopped at the usage limit after {elapsed}"
            : !result.Succeeded ? $"Failed after {elapsed}"
            : checkFailed ? $"Check failed after {elapsed}"
            : $"Done in {elapsed}";
        // An answer Link wrote to a file is shown as written.
        var answer = task.AnswerFile is null && !task.AnswerIsSkill ? result.Summary : AgentCoordinator.Unfenced(result.Summary);
        var (answerText, question) = AgentQuestion.Split(answer);
        // The changelist's line is for P4V and the next step is for Link's Auto effort; the thread shows the answer without them.
        var said = AgentChanges.SplitChangelistLine(AgentNext.Split(answerText).Text).Text;
        // A question is asked by a run that ended well; one cut short asks nothing.
        _question = !stopped && result.Succeeded && !checkFailed ? question : null;
        var text = checkFailed ? CheckReport(task, run.Check!) + (said.Length > 0 ? "\n\n" + said : string.Empty) : said;
        // After a failure, or a Stop in a loop; a Stop without a loop was the user's own choice, and a task done in the
        // end got past its loop. A limit has its own card.
        var failed = !stopped && !limitReached && (!result.Succeeded || checkFailed);
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
            ConsoleAction()
        }.OfType<PanelAction>().ToArray();

        if (_liveTurn is { } turn)
        {
            turn.Current = string.Empty;
            turn.Outcome = ending;
            turn.OutcomeBrush = OutcomeBrush(ending);
            turn.Said = text;
            turn.Files = run.Changes.Select(ToChangeRow).ToArray();
            turn.Stats = StatsLine(result.Stats);
            turn.StepsOpen = false;
        }

        var decision = OfferDecision(run);
        var needsYou = decision || _question is not null;
        RecordRun(run, ending, text, checkFailed ? text : answer, needsYou, stopped, checkFailed);
        if (_task is not null && IsUserWork(task))
        {
            ShowTask();
        }
        else if (_agentPanel is not null)
        {
            _agentPanel.SetRunning(false);
            _agentPanel.ShowReply(false);
            _agentPanel.SetStatus(ending.ToLowerInvariant(), OutcomeBrush(ending));
        }

        // Start over only once a run has ended: a loop label often comes on good work, so while the agent works it stays
        // a line of Activity.
        _agentPanel?.ShowRestart(_agentOrigin is not null && (failed || (stopped && result.Loops.Count > 0)), escalate: !IsStrongest(task));
        if (limitReached) OfferLimitReached(task.Provider);
        if (_task is not null && IsUserWork(task) && !task.AnswerIsSkill) ShowResultInChat(run, diff: null);

        var summary = text.Length > 0 ? text : ending;
        var name = ShortName(task.Provider);
        if (_question is { } asked)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{name} спрашивает.", $"{name} asks you.");
            NotifyIfAway(project, $"{provider.Name} asks", asked.Question);
        }
        else if (decision)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{name} ждёт решения.", $"{name} needs your decision.");
            NotifyIfAway(project, "Agent needs a decision", ToPreviewText(summary));
        }
        else if (result.Succeeded && !stopped && !checkFailed)
        {
            if (!IsVisible) _unseen = LinkState.Done;
            _cues.Play(SoundCue.Done);
            SayAgent($"{name} закончил работу.", $"{name} finished.");
            NotifyIfAway(project, "Agent finished", ToPreviewText(summary));
        }
        else if (!stopped)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.Error);
            SayAgent(checkFailed ? $"{name} закончил, но проверка не прошла." : $"{name}: не получилось.",
                checkFailed ? $"{name} finished, but the check fails." : $"{name} failed.");
            NotifyIfAway(project, checkFailed ? "The check fails after the agent" : "Agent failed", ToPreviewText(summary));
        }

        AgentStatus(limitReached
            ? $"{provider.Name} ran out of its limit · the Tasks tab offers a way on"
            : _question is not null ? $"{provider.Name} asks: the choice is in the Tasks tab"
            : $"{provider.Name}: {ending.ToLowerInvariant()} · {Plural(run.Changes.Count, "file")} changed");
    }

    /// <summary>The agent could not start: why, in its thread; another agent can still take the task.</summary>
    private void ShowAgentFailure(IAgentProvider provider, AgentTask task, Exception exception)
    {
        if (_task is not null && IsUserWork(task))
        {
            _task.Status = AgentTaskStatus.Failed;
            _task.Add(AgentEntryKind.NoStart, $"{provider.Name} could not start: {exception.Message}");
            SaveTask(_task);
            ShowTask();
        }
        else if (_liveTurn is { } turn && _agentPanel is not null)
        {
            turn.Current = string.Empty;
            turn.Outcome = "could not start";
            turn.OutcomeBrush = (Brush)FindResource("ErrorBrush");
            turn.Said = exception.Message;
            _agentPanel.SetRunning(false);
        }

        // No session began: words here would go to an older run.
        _agentPanel?.ShowReply(false, $"{provider.Name} could not start: try again, or hand the task to the other agent.");
        _cues.Play(SoundCue.Error);
        if (!_sending) SetError(exception.Message);
    }

    private Brush OutcomeBrush(string outcome) => (Brush)FindResource(
        outcome.StartsWith("Done", StringComparison.OrdinalIgnoreCase) ? "SuccessBrush"
        : outcome.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("an earlier", StringComparison.Ordinal) ? "MutedBrush"
        : outcome.StartsWith("working", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("Starting", StringComparison.Ordinal) ? "AccentBrush"
        : "ErrorBrush");

    /// <summary>
    /// The run joins the task's record: its own tokens (a Codex session reports its total, so the runs before in that
    /// session are taken off), its steps, files and answer for the thread, the check, and how the task stands.
    /// </summary>
    private void RecordRun(AgentRun run, string ending, string text, string answer, bool needsYou, bool stopped, bool checkFailed)
    {
        if (_task is not { } record || !IsUserWork(run.Task)) return;
        var (task, result) = (run.Task, run.Result);
        var stats = result.Stats;
        var earlier = stats.SessionTokens
            ? record.Runs.Where(previous => previous.SessionId == result.SessionId).ToArray()
            : [];
        var files = run.Changes.Select(change => Relative(change.Path)).ToList();
        record.Runs.Add(new AgentRunRecord
        {
            Provider = task.Provider,
            Model = task.Model,
            Effort = task.Effort,
            EffortReason = task.EffortReason,
            RoleId = task.RoleId,
            Access = task.Access,
            AutoApprove = task.AutoApprove,
            SessionId = result.SessionId,
            StartedAt = DateTimeOffset.Now - stats.Duration,
            Duration = stats.Duration,
            Outcome = ending,
            InputTokens = Less(stats.InputTokens, earlier.Sum(previous => previous.InputTokens ?? 0)),
            CachedTokens = Less(stats.CachedTokens, earlier.Sum(previous => previous.CachedTokens ?? 0)),
            OutputTokens = Less(stats.OutputTokens, earlier.Sum(previous => previous.OutputTokens ?? 0)),
            CostUsd = stats.CostUsd,
            ContextTokens = stats.ContextTokens,
            ContextWindow = stats.ContextWindow,
            Looped = result.Loops.Count > 0,
            Steps = [.. _runSteps.TakeLast(StepsKeptPerRun)],
            Files = files,
            Words = Shorten(answer, 12_000)
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
        record.ChangedFiles = files;
        record.Steps = [.. _taskSteps.TakeLast(StepsHandedOn)];
        record.Status = needsYou ? AgentTaskStatus.NeedsYou
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
            RefreshTaskList();
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
        if (_task is not null)
        {
            SaveTask(_task);
            ShowTask();
        }
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
        AgentStatus($"The task goes on by itself at {AgentLimits.Time(reset.AddMinutes(1))} while Link runs");
    }

    /// <summary>The open task in the Tasks tab, unless another task is shown there to be read.</summary>
    private void ShowTask()
    {
        if (_agentPanel is null || _task is not { } record || _ownShown) return;
        if (_shownTask is not null && _shownTask.Id != record.Id) return;
        _shownTask = record;
        ShowRecord(record, readOnly: false);
    }

    /// <summary>
    /// A task in the Tasks tab: its goal and standing, the line of who works on what, its plan, thread and usage. The
    /// open task offers its menus and the box for the next words; another, read while an agent works, only shows.
    /// </summary>
    private void ShowRecord(AgentTaskRecord record, bool readOnly)
    {
        var panel = _agentPanel!;
        _ownShown = false;
        var live = !readOnly && AgentRunning && _runningTask is { } running && IsUserWork(running);
        panel.ShowTaskView(record.Goal, WhereLine(record));
        var (status, dot) = Standing(record, live);
        panel.SetStatus(status, dot);
        panel.SetHeader(live ? AgentHeader(_runningTask!) : record.LastRun is { } last ? RunLine(last, record.Workspace) : "No run yet");
        panel.ShowPlan(record.Plan);
        panel.ShowThread(BuildThread(record, live));
        panel.ShowBrief(record.Brief);
        var (usage, hint) = UsageText(record);
        panel.ShowUsage(usage, hint);
        var provider = (live ? _runningTask?.Provider : null) ?? record.LastRun?.Provider ?? ProjectProvider;
        _limitsProvider = provider;
        panel.ShowTaskLimits(LimitRows(provider));
        panel.SetRunning(live);
        panel.SetReplyTarget(AgentName(provider));
        if (readOnly)
        {
            panel.ResetCards();
            panel.ShowMenus([], []);
            panel.ShowFinish(false);
            panel.ShowPlanMode(false);
            panel.ShowReply(false, $"{AgentName(_runningTask?.Provider ?? provider)} works on another task: open this one again once it ends to go on with it.");
            return;
        }

        panel.ShowQuestion(_question);
        panel.ShowMenus(HandOffActions(record), MoreActions());
        panel.ShowFinish(true);
        panel.ShowPlanMode(!live && !AgentRunning && _lastRun is { Task.PlanOnly: true });
        if (live) panel.ShowReply(true);
        // One agent at a time: Link's own answer, such as the map, goes first.
        else if (AgentRunning && _runningTask is { } busy) panel.ShowReply(false, $"{AgentName(busy.Provider)} writes {OwnWhat(busy)}: your words can go once it ends.");
        else if (ResumableTask() is not null) panel.ShowReply(true);
        else panel.ShowReply(false, "This task has no session to go on with: hand it to an agent, or start it anew from its card.");
        UpdateAgentPill();
    }

    /// <summary>
    /// The task as a thread, oldest first: the user's words, each run with its steps, answer, files and numbers, notes
    /// of hand-overs, models and limits between; the run under way last.
    /// </summary>
    private IReadOnlyList<object> BuildThread(AgentTaskRecord record, bool live)
    {
        var items = new List<object>();
        var runs = 0;
        ThreadTurn? turn = null;
        foreach (var entry in record.Entries)
        {
            var time = AgentLimits.Time(entry.At);
            switch (entry.Kind)
            {
                case AgentEntryKind.Brief or AgentEntryKind.You:
                    items.Add(new ThreadUser(entry.Text, time));
                    turn = null;
                    break;
                case AgentEntryKind.Run:
                    var run = runs < record.Runs.Count ? record.Runs[runs] : null;
                    // The next step the agent named is ahead only for the last run, and not while another runs.
                    turn = TurnOf(record, run, entry.Text, showNext: !live && run is not null && ReferenceEquals(run, record.LastRun));
                    runs++;
                    items.Add(turn);
                    break;
                case AgentEntryKind.Said when turn is not null:
                    // An earlier Link kept only these lines of the answer.
                    if (turn.Said.Length == 0) turn.Said = entry.Text;
                    break;
                case AgentEntryKind.Check when turn is not null:
                    turn.Stats = turn.Stats.Length == 0 ? entry.Text : $"{entry.Text}\n{turn.Stats}";
                    break;
                default:
                    items.Add(new ThreadNote(EntryPrefix(entry.Kind) + entry.Text, time));
                    break;
            }
        }

        if (live && _liveTurn is not null) items.Add(_liveTurn);
        return items;
    }

    /// <summary>A recorded run as a turn of the thread; an earlier Link's run has only its line.</summary>
    private ThreadTurn TurnOf(AgentTaskRecord record, AgentRunRecord? run, string line, bool showNext)
    {
        // "Claude Code · Opus 5.5: done in 3:12, 2 files changed".
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        var who = run is not null ? $"{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, run.EffortReason)}"
            : colon > 0 ? line[..colon] : line;
        var outcome = run?.Outcome ?? (colon > 0 ? line[(colon + 2)..] : string.Empty);
        var turn = new ThreadTurn(who, outcome, OutcomeBrush(outcome));
        if (run is null) return turn;
        turn.AddSteps(run.Steps.Select(StepRow));
        var (said, next) = AgentNext.Split(AgentQuestion.Split(run.Words).Text);
        turn.Said = AgentChanges.SplitChangelistLine(said).Text;
        if (showNext && next is not null) turn.Next = next.Line;
        turn.Files = run.Files.Select(file => new AgentChangeRow(file, Path.Combine(record.Workspace, file))).ToArray();
        var tokens = run.InputTokens is { } input
            ? $" · {AgentContext.Tokens(input)} in ({AgentContext.Tokens(run.CachedTokens ?? 0)} cached), {AgentContext.Tokens(run.OutputTokens ?? 0)} out"
            : string.Empty;
        turn.Stats = $"{(int)run.Duration.TotalMinutes}:{run.Duration.Seconds:00}{tokens}";
        return turn;
    }

    private static string EntryPrefix(AgentEntryKind kind) => kind switch
    {
        AgentEntryKind.Check => "Check: ",
        AgentEntryKind.HandOff => "Hand-over: ",
        AgentEntryKind.Model => "Model: ",
        AgentEntryKind.Limit => "Limit: ",
        AgentEntryKind.Said => "Said: ",
        AgentEntryKind.Run => "Run: ",
        _ => string.Empty
    };

    /// <summary>
    /// Hand off ▾: the other agent in a new session (with a brief to look at first), another model of Settings for the
    /// same agent in the same session, the first agent back in its own session, or another specialist in the same session.
    /// </summary>
    private IReadOnlyList<PanelAction> HandOffActions(AgentTaskRecord record)
    {
        if (ResumableTask() is not { } resume) return [];
        var current = resume.Task.Provider;
        // Every other agent that can work now: the custom model only once it is set up.
        var others = _agents.Providers.Where(agent => agent.Kind != current && agent.Configured).Select(agent => agent.Kind).ToArray();
        var actions = others.Select(other => new PanelAction($"{AgentName(other)} · new session",
            $"{AgentName(other)} takes the task over with a brief of it; you see and can change the brief first",
            () => _ = ShowHandOffCardAsync(other))).ToList();
        // Settings' three choices, each once, other than the one the session is on.
        var seen = new HashSet<(string?, string?)> { (resume.Task.Model, resume.Task.Effort) };
        foreach (var level in Enum.GetValues<AgentLevel>())
        {
            var setup = _settings.Current.Agent.Level(current, level);
            // The custom model has one setup; its other models are chosen on a card.
            if (current == AgentProviderKind.Custom || !seen.Add(setup)) continue;
            actions.Add(new PanelAction($"{AgentName(current)} · {SetupWords(current, setup.Model, setup.Effort)} · same session",
                "The same session goes on with this model: it keeps what it knows, the model reads it anew once",
                () => SwitchSetup(setup, escalate: false)));
        }

        foreach (var other in others.Where(other => record.Runs.Any(run => run.Provider == other && run.SessionId is not null)))
        {
            actions.Add(new PanelAction($"Back to {AgentName(other)} · its own session",
                $"{AgentName(other)} goes on in its own session, told what {AgentName(current)} did meanwhile",
                () => _ = ReturnToAsync(other)));
        }

        // The specialists: the same session goes on as another one, or as none.
        foreach (var role in ProjectRoles.In(record.Workspace).Where(role => !string.Equals(role.Id, record.RoleId, StringComparison.OrdinalIgnoreCase)))
        {
            actions.Add(new PanelAction($"As {role.Name} · same session",
                $"{role.Description} The same session goes on as this specialist, with its skills: it does not read the project again",
                () => SwitchRole(role)));
        }

        if (record.RoleId is not null)
        {
            actions.Add(new PanelAction("As General · same session", "The same session goes on without a specialist role", () => SwitchRole(null)));
        }

        return actions;
    }

    /// <summary>
    /// Another specialist takes the task over in the same session (null: General): it is told so with the role's text and
    /// the skills picked for it, and does not read the project again. What follows, a hand-over too, keeps the new one.
    /// </summary>
    private void SwitchRole(AgentRole? role)
    {
        if (AgentRunning || _task is not { } record || ResumableTask() is not { } resume) return;
        var skills = ProjectRoles.SkillsFor(BriefOf(record), role, ProjectSkills.In(record.Workspace));
        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        record.Add(AgentEntryKind.HandOff, $"Specialist: {role?.Name ?? "General"}");
        if (resume.Previous is null) _backupBeforeDo = true;
        if (_agentOrigin is { } origin) _agentOrigin = origin with { RoleId = role?.Id, Skills = skills };
        _ = StartAgentAsync(resume.Task with
        {
            RoleId = role?.Id,
            Skills = skills,
            Prompt = ProjectRoles.SwitchText(role, skills, record.Workspace),
            Images = []
        }, resume.Previous, phase: EffortPhase.Reply);
    }

    /// <summary>The ⋯ menu: what the last run allows (chat, undo, a skill), starting over, and the console.</summary>
    private IReadOnlyList<PanelAction> MoreActions()
    {
        var actions = _afterRun.Where(action => action.Label != "Console").ToList();
        if (!AgentRunning && ResumableTask() is not null)
        {
            actions.Add(new PanelAction("Compact — fresh session from the task state",
                "A new session of the same agent starts from the task's state instead of the session that has grown; you see and can change the state first",
                () => _ = CompactTask()));
        }

        if (_lastRun is not null && _agentOrigin is not null)
        {
            actions.Add(new PanelAction("Start over with a note", "A new session of the same agent takes the task with a note of what was tried",
                RequestRestart));
        }

        actions.Add(ConsoleAction());
        return actions;
    }

    /// <summary>
    /// The task's tokens by agent and what its session holds; the money is a hint only, as the usage windows are what
    /// runs out.
    /// </summary>
    private (string Text, string Hint) UsageText(AgentTaskRecord record)
    {
        var lines = record.Runs.GroupBy(run => run.Provider).Select(runs =>
        {
            var input = runs.Sum(run => run.InputTokens ?? 0);
            var cached = runs.Sum(run => run.CachedTokens ?? 0);
            var output = runs.Sum(run => run.OutputTokens ?? 0);
            var tokens = input > 0 ? $", {AgentContext.Tokens(input)} tokens in ({AgentContext.Tokens(cached)} cached), {AgentContext.Tokens(output)} out" : string.Empty;
            return $"This task, {AgentName(runs.Key)}: {Plural(runs.Count(), "run")}{tokens}";
        }).ToList();
        var cost = record.Runs.Sum(run => run.CostUsd ?? 0);
        var hint = cost > 0
            ? $"≈ {cost.ToString("$0.00", CultureInfo.InvariantCulture)} at API prices (Claude Code reports it). On a subscription it is no bill: the windows below are what runs out."
            : record.Runs.Count > 0 && record.Runs.All(run => run.Provider == AgentProviderKind.Custom)
                ? "The custom model runs on no subscription: its server counts the tokens, and bills them if it bills at all."
                : "On a subscription the usage windows below are what runs out: the 5-hour one and the week.";
        if (ContextLine(record) is { } context)
        {
            lines.Add(context);
            hint += "\nContext: what the agent's session holds. Its CLI compacts it near the budget of Settings, or at the model's window if that is smaller.";
        }

        return (string.Join('\n', lines), hint);
    }

    /// <summary>
    /// "Context 182k / 400k (46%)": the session against the limit it is compacted at. The run under way tells it as it
    /// goes; otherwise it is what the last run left. Null when the agent did not tell.
    /// </summary>
    private string? ContextLine(AgentTaskRecord record)
    {
        var live = AgentRunning && ReferenceEquals(record, _task) && _runningTask is { } running && IsUserWork(running);
        var (used, window) = live ? _liveContext : (record.LastRun?.ContextTokens, record.LastRun?.ContextWindow);
        return used is { } tokens ? AgentContext.Line(tokens, window, _settings.Current.Agent.ContextBudget) : null;
    }

    /// <summary>The agent's pill in the card's header and the Tasks tab's dot: who works and how the task stands.</summary>
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
        var what = _runningTask is { } running && !IsUserWork(running) ? "map " : string.Empty;
        var (words, dot) = AgentRunning
            ? ($"· {what}{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", (Brush)FindResource("BusyOrbBrush"))
            : Standing(_task!, live: false);
        var text = $"{name} {words}";
        AgentPillText.Text = text;
        AgentPillDot.Fill = dot;
        var subject = _runningTask is { } busy && !IsUserWork(busy) ? $"Link writes {OwnWhat(busy)}" : _task?.Goal ?? "Agent";
        AgentPill.ToolTip = $"{subject}\n{text}" +
                            (_limits.Describe(provider) is { Length: > 0 } limits ? $"\nUsage: {limits}" : string.Empty) +
                            "\nClick to see it in the Tasks tab";
        AgentPill.Visibility = Visibility.Visible;
        _panel?.ShowTaskDot(dot);
    }

    private void AgentPill_OnClick(object sender, RoutedEventArgs e)
    {
        // What works now: Link's own answer, else the open task.
        if (_runningTask is { } running && !IsUserWork(running) || _task is null && _own is not null)
        {
            ShowAgentPanel();
            ShowOwnRun();
        }
        else if (_task is not null) ShowTaskInPanel();
        else ShowAgentPanel();
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
    /// What only the user can decide after a run: retry with edits what read only did not let the agent do, carry out a
    /// plan, or allow what Claude was refused and go on. Returns whether a decision is offered.
    /// </summary>
    private bool OfferDecision(AgentRun run)
    {
        var (task, result) = (run.Task, run.Result);
        _decision = null;
        _agentPanel!.HideDecision();
        if (result.Stopped || result.SessionId is not { } session) return false;
        // Changing files or running any command is never allowed in read only: it is a retry with edits, except for a
        // task whose file Link writes itself (a map, a skill).
        var claude = task.Provider.RunsOnClaudeCode();
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
                // A copy of a workspace without version control is made before the edits, as the card does by default.
                _backupBeforeDo = true;
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    Access = AgentAccess.Do,
                    AllowedTools = task.AllowedTools.Concat(rules).Concat(AgentCoordinator.PerforceTools(task.Workspace, AgentAccess.Do))
                        .Distinct(StringComparer.Ordinal).ToArray(),
                    Prompt = "You may change files in the workspace now. Carry out the task." +
                             (task.CheckCommand is { } check ? " " + AgentCoordinator.CheckNote(check) : string.Empty),
                    Images = []
                }, null, phase: EffortPhase.Reply);
            };
            return true;
        }

        // A plan's run is refused whatever changes things: that is plan mode, not a missing permission, and allowing a
        // tool would only repeat the refusal. What the user decides is whether to carry the plan out.
        if (task.PlanOnly && result.Succeeded)
        {
            var edits = task.Access == AgentAccess.Do;
            // The plan was made on the strongest model; it is carried out with the card's, in the same session.
            var (model, effort) = _settings.Current.Agent.PlanOnDeep ? _planSetup : (task.Model, task.Effort);
            var auto = _task is { AutoEffort: true } && _settings.Current.Agent.AutoEffort;
            // On Auto the plan's run picks its effort when it starts, as it does for each run.
            _agentPanel.ShowDecision(
                (edits ? "The plan is above. Carry it out with edits in the workspace?" : "The plan is above. Carry it out, read only?") +
                (auto ? " Link picks its effort."
                    : (model, effort) != (task.Model, task.Effort) ? $" It runs on {SetupWords(task.Provider, model, effort)}." : string.Empty),
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
                }, null, phase: EffortPhase.RunPlan);
            };
            return true;
        }

        // In read only, only what looks can be allowed.
        var allowable = result.Denials.Where(rule => !readOnly || !AgentPermissions.ChangesThings(rule)).ToArray();
        if (claude && allowable.Length > 0)
        {
            IReadOnlyList<string> rules = allowable;
            _agentPanel.ShowDecision(
                $"{AgentName(task.Provider)} was not allowed: {string.Join(", ", rules)}. Allow {(rules.Count == 1 ? "it" : "them")} and continue?",
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
                }, run, phase: EffortPhase.Reply);
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

    /// <summary>Undo the agent's changes (Git, Perforce) or restore the copy (no version control), when there is something to undo.</summary>
    private PanelAction? UndoAction(AgentRun run)
    {
        var undoable = run.Changes.Count(change => change.Undoable);
        if (run.Before.IsVersioned && undoable > 0)
        {
            return new PanelAction($"Undo agent changes ({undoable})",
                "Put back the files the agent changed that had no changes of yours before the task", () => _ = UndoAgentChangesAsync(run));
        }

        return run.Before.Backup is not null && run.Changes.Count > 0
            ? new PanelAction("Restore the copy", $"Put the workspace back as it was before the task, from {run.Before.Backup}",
                () => _ = UndoAgentChangesAsync(run))
            : null;
    }

    /// <summary>Undo of a run: the task's last one, or Link's own answer (the map), which leaves the task as it is.</summary>
    private async Task UndoAgentChangesAsync(AgentRun run)
    {
        if (AgentRunning) return;
        var versioned = run.Before.IsVersioned;
        var files = versioned ? run.Changes.Where(change => change.Undoable).ToArray() : run.Changes.ToArray();
        var kept = run.Changes.Count - files.Length;
        var question = versioned
            ? $"Undo the agent's changes in {files.Length} files? New files are deleted, changed ones come back from version control." +
              (kept > 0 ? $" {kept} files you had changed before the task stay as they are." : string.Empty)
            : $"Put {files.Length} files back from the copy made before the task? Files the agent added are deleted.";
        if (MessageBox.Show(this, question, "Link · undo agent changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (versioned) await _agents.Changes.UndoAsync(run.Before, files, CancellationToken.None);
            else await _agents.Changes.RestoreAsync(run.Before, files, CancellationToken.None);
            var left = await _agents.Changes.CompareAsync(run.Before, CancellationToken.None);
            if (_own is { } own && own.Run == run)
            {
                own.Run = run with { Changes = left };
                own.Turn.Files = left.Select(ToChangeRow).ToArray();
                if (_ownShown) ShowOwnRun();
                _projectPanel?.ShowProject(_projects.ActiveProject);
                AgentStatus(left.Count == 0 ? "Link's own answer was undone" : $"Undone; {left.Count} files still differ");
                return;
            }

            _lastRun = run with { Changes = left };
            // The task's changelist goes with its last file; Perforce keeps one that still holds files.
            if (run.Before.IsPerforce && _task?.Changelist is { } changelist && left.All(change => !change.Undoable))
            {
                await AgentChanges.DropChangelistIfEmptyAsync(run.Before.Workspace, changelist, CancellationToken.None);
                _task.Changelist = null;
            }

            _afterRun = _afterRun.Where(action => !action.Label.StartsWith("Undo", StringComparison.Ordinal) && action.Label != "Restore the copy")
                .Prepend(UndoAction(_lastRun)).OfType<PanelAction>().ToArray();
            if (_task is not null)
            {
                _task.ChangedFiles = left.Select(change => Relative(change.Path)).ToList();
                _task.Add(AgentEntryKind.You, left.Count == 0 ? "Undid the agent's changes" : $"Undid the agent's changes; {left.Count} files still differ");
                SaveTask(_task);
                ShowTask();
            }

            AgentStatus(left.Count == 0 ? "The agent's changes were undone" : $"Undone; {left.Count} files still differ");
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
        if (AgentNext.Split(AgentQuestion.Split(run.Result.Summary).Text).Text.Trim() is { Length: > 0 } words) text.Append("\n\nThe agent's last words:\n").Append(Shorten(words, 1500));
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
        ShowResultInChat(run, diff ?? "(no diff: the workspace has no supported version control, or the files are new)");
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
            parts.Add($"{AgentContext.Tokens(input)} tokens in ({AgentContext.Tokens(stats.CachedTokens ?? 0)} cached), {AgentContext.Tokens(stats.OutputTokens ?? 0)} out" +
                      (stats.SessionTokens ? " for the session" : string.Empty));
        }

        return string.Join(" · ", parts);
    }

    private static string Plural(int number, string one, string? many = null) => $"{number} {(number == 1 ? one : many ?? one + "s")}";

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
        (_task?.Workspace ?? _projects.ActiveProject.WorkspaceRoot) is { } root && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path)
            : path;

    /// <summary>Shows the file in Explorer: opening it could run it, and a changed script is not to be run by a click.</summary>
    private static void ShowFile(string path)
    {
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>
    /// What the agent tells the status line, only while it is the chat's to spare: never over a request, its answer or
    /// its voice. The agent's own state is in its pill and its tab.
    /// </summary>
    private void AgentStatus(string text)
    {
        if (!_sending && _voice.Phase == VoicePhase.Idle) SetStatus(text);
    }

    /// <summary>
    /// The agent's few words aloud, when replies are read aloud: that it started, finished, failed or asks. Said only
    /// while the voice is idle and never holding it: sending, push-to-talk and the chat's answers go on as if silent.
    /// </summary>
    private void SayAgent(string russian, string english) => _voice.Announce(russian, english);
}
