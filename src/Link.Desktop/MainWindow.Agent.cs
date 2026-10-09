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

// Agent: a CLI agent (Claude Code, Codex or the custom model) works on one task of the user's in the project's workspace. The Studio
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
    // How much of a step's diff the team lead reads in its review (about 5k tokens); the rest it reads in the files.
    private const int ReviewDiff = 20_000;
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
    // The task the Studio shows: the open one, or another from the list, only to be read while an agent works.
    private AgentTaskRecord? _shownTask;
    // The project the open task belongs to: the user may switch projects while it runs.
    private LinkProject? _taskProject;
    private AgentRun? _lastRun;
    // What the shown task changed in its header ("+181 −12"), counted once when a run ends or the task opens.
    private (Guid Task, string Text, string Hint) _changes;
    private AgentTask? _runningTask;
    // The running agent's usage windows as they stood when the run began, and the first report of each during it: what
    // a run took of the subscription is measured from them (see AgentLimits.Spent).
    private (DateTimeOffset Start, IReadOnlyList<AgentLimit> Before, List<AgentLimit> First) _usageRun = (default, [], []);
    // The run under way in the thread: its steps arrive as the CLI writes them.
    private ThreadTurn? _liveTurn;
    // Link's own answer (the map), apart from the open task: the Studio shows it on its own, with Stop.
    private OwnRun? _own;
    // Whether the Studio shows Link's own answer rather than a task or the list.
    private bool _ownShown;
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
    private readonly List<string> _queuedImages = [];
    private NextSetup? _nextSetup;
    // Files the user attached: the next run of the task is told their paths and may read their folders.
    private readonly List<string> _attachedFiles = [];
    // A new task the user ran while an agent worked: it starts when that run is over.
    private (AgentPlan Plan, InputSnapshot Snapshot)? _nextTask;
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
    // The ⋯ menu's choices after the last run: undo comes and goes with it.
    private IReadOnlyList<PanelAction> _afterRun = [];
    // Files the running agent reported changed, listed until git, Perforce or the copy tells the whole list.
    private readonly HashSet<string> _runningFiles = new(StringComparer.OrdinalIgnoreCase);
    // The specialist's name in the header, which is drawn at every tick of a run: its file is read once, not at each.
    private (string? Id, string? Workspace, string? Name) _roleName;
    // The project's stats for the inspector, which reads them again only after a task is saved.
    private (Guid Project, IReadOnlyList<StatsRow> Rows)? _stats;

    private bool AgentRunning => _agentCancellation is not null;

    /// <summary>
    /// The agent a new card, the map and Link's own answers start on: Settings' default, or the other one only while the
    /// default's limit is reached now. Nothing is remembered from the last task. The notice says why, for the card.
    /// </summary>
    private (AgentProviderKind Provider, string? Notice) StartingAgent()
    {
        var preferred = _settings.Current.Agent.DefaultProvider;
        var other = Other(preferred);
        var (provider, until) = _limits.StartsOn(preferred, other);
        return (provider, until is { } reset
            ? $"{ShortName(preferred)}'s limit is reached until {AgentLimits.Time(reset)}: {ShortName(other)} takes it; change the agent if you want"
            : null);
    }

    private AgentProviderKind StartProvider => StartingAgent().Provider;

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
        // The Studio follows when it is open, without coming over what is being written; closed, the pill shows the agent at work.
        if (_studio is not null) ShowOpenTask();
        ContinueAgent(words, images);
    }

    /// <summary>New task in the Studio: a card with what is in Link's input and chips, maybe nothing yet; while an agent works, its Run waits for it.</summary>
    private async Task OpenNewTaskCardAsync()
    {
        RemoveMissingContextFiles();
        var snapshot = await TakeSnapshotAsync(_projects.ActiveProject, PromptBox.Text, RequestRoute.Agent, AutoFrameOn);
        _attempts.Keep(snapshot);
        OpenAgentCard(snapshot);
    }

    /// <summary>
    /// The workspace's Map button: the agent looks through the folder read only and Link writes its answer to
    /// docs/map.md, the one change, shown and undone like any other. Link's own answer, on Quick: a run of its own
    /// in the Studio, which leaves the open task as it is.
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

        var (provider, model, effort) = OwnAnswerSetup(StartProvider, inSession: false);
        var map = new AgentTask
        {
            Provider = provider,
            Workspace = workspace,
            Prompt = prompt,
            AllowedTools = AgentCoordinator.Tools(project, workspace, AgentAccess.Analyze),
            AnswerFile = ProjectMap.RelativePath,
            Model = model,
            Effort = effort,
            WithoutMcp = true
        };
        ShowAgentPanel();
        await StartAgentAsync(map, null);
    }

    /// <summary>The agent and level of Link's own answers (a map, a note), as <see cref="AgentSettings.OwnAnswers"/> settles them.</summary>
    private (AgentProviderKind Provider, string? Model, string? Effort) OwnAnswerSetup(AgentProviderKind provider, bool inSession) =>
        _settings.Current.Agent.OwnAnswers(provider, inSession, _agents.Provider(AgentProviderKind.Custom).Configured);

    /// <summary>The task card for a snapshot: <paramref name="brief"/> is what the agent is told, the question by default.</summary>
    private void OpenAgentCard(InputSnapshot snapshot, string? brief = null)
    {
        _agentSnapshot = snapshot;
        var (_, images) = AgentCoordinator.Words(snapshot, newTask: true);
        var others = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>()
            .Where(item => item.FilePath is null || !images.Contains(item.FilePath))
            .Select(item => item.Label)
            .ToArray();
        var (starting, notice) = StartingAgent();
        const string waits = "An agent is working now: Run puts this task next, and it starts by itself when that run ends.";
        if (AgentRunning) notice = notice is null ? waits : $"{waits} {notice}";
        var card = CardModel(brief ?? snapshot.Text, others.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", others),
            images, starting) with { Notice = notice };
        _cardRun = RunAgentAsync;
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus("Check the card and press Run: nothing runs before that");
    }

    /// <summary>
    /// The card's agents, each with its own models and efforts, Settings' choice first, and its usage. The card keeps the
    /// project it is made for (the active one unless <paramref name="forProject"/> says), whatever is active when it runs.
    /// </summary>
    private AgentCardModel CardModel(string goal, string attachments, IReadOnlyList<string> images, AgentProviderKind provider,
        LinkProject? forProject = null)
    {
        var project = forProject ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null;
        var roles = ProjectRoles.In(workspace);
        IReadOnlyList<ProjectSkill> skills = workspace is null ? [] : ProjectSkills.In(workspace);
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
            TeamOffered = settings.Team.Enabled,
            Roles = roles,
            RoleId = ProjectRoles.Suggest(goal, roles)?.Id,
            WorkspaceSkills = skills,
            HarnessLine = CardHarnessLine(workspace, skills.Count),
            ProjectId = project.Id,
            Renew = () => CardModel(goal, attachments, images, provider, project)
        };
    }

    private string CardHarnessLine(string? workspace, int skills) =>
        HarnessView.CardLine(_settings.Current.Agent.ContextBudget, skills, workspace is null ? [] : CliAgentProvider.McpServerNames(workspace));

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

    /// <summary>The bars wherever they are: the sidebar's foot and an open card.</summary>
    private void ShowLimitRows()
    {
        RefreshTaskList();
        if (_agentPanel is null) return;
        foreach (var agent in _agents.Providers) _agentPanel.ShowCardLimits(agent.Kind, LimitRows(agent.Kind));
    }

    private IReadOnlyList<LimitRow> AllLimitRows() => _agents.Providers.SelectMany(agent => LimitRows(agent.Kind)).ToArray();

    private AgentPanel CreateAgentPanel()
    {
        var panel = new AgentPanel { SaveImage = _history.SaveClipboardImage };
        panel.RunRequested += plan =>
        {
            if (_cardRun is { } run) _ = run(plan);
        };
        panel.CardCancelled += CancelCard;
        panel.StopRequested += () => _agentCancellation?.Cancel();
        panel.DecisionTaken += always =>
        {
            _rememberAllowance = always;
            // A decision is taken once: one that starts no run (a share, the team's last OK) leaves the panel too.
            var decide = _decision;
            _decision = null;
            panel.HideDecision();
            decide?.Invoke();
        };
        panel.RatingChosen += RateExam;
        panel.RestartRequested += RequestRestart;
        panel.EscalateRequested += Escalate;
        panel.ContinueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: false);
        panel.QueueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: true);
        panel.FinishRequested += FinishTask;
        panel.CheaperRequested += () => _ = ShowHandOffCardAsync(AgentProviderKind.Custom);
        panel.PlanModeOffRequested += LeavePlanMode;
        panel.OpenFileRequested += ShowFile;
        panel.NewTaskRequested += () => _ = OpenNewTaskCardAsync();
        panel.TaskSelected += OpenAgentTask;
        panel.OwnRunRequested += ShowOwnRun;
        panel.LimitRefreshRequested += provider => _ = RefreshLimitsAsync(provider);
        // The card is in the Studio, so the Studio is there to open its column.
        panel.HarnessRequested += () => _studio?.ShowInspector(true);
        panel.ShowList(TaskRows(), AllLimitRows(), null);
        return panel;
    }

    /// <summary>Cancel on the card: back to the task it was opened from, or to the middle with no task open.</summary>
    private void CancelCard()
    {
        _cardRun = null;
        _agentPanel?.HideCard();
        if (_ownShown) ShowOwnRun();
        else if (_shownTask is not null) ShowTask();
        else ShowTaskList();
    }

    /// <summary>No task is shown: the sidebar lists the project's, and an agent at work goes on, its row says so.</summary>
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
            string.Empty, $"{AgentName(own.Task.Provider)} · {status}", (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot), RoleBrushes.Link, null);
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

        // Each workspace's role files are read once per drawing, and only for a task that has a specialist.
        var roles = new Dictionary<string, Lazy<IReadOnlyList<AgentRole>>>(StringComparer.OrdinalIgnoreCase);
        return tasks.Select(record =>
        {
            // The list is drawn now and then: its running row says so, the time is in the pill and the task.
            var live = AgentRunning && record.Id == _task?.Id && _runningTask is { } running && IsUserWork(running);
            var (status, brush) = live ? ("working", (Brush)FindResource("BusyOrbBrush")) : Standing(record, live: false);
            var who = record.LastRun is { } last ? AgentName(last.Provider) + " · " : string.Empty;
            if (!roles.TryGetValue(record.Workspace, out var inWorkspace)) roles[record.Workspace] = inWorkspace = WorkspaceRoles(record.Workspace);
            return new TaskRow(record.Goal, ToPreviewText(record.LastWords), ShortTime(record.UpdatedAt), who + status, brush,
                RoleBrushes.For(record.RoleId, inWorkspace), record);
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

    /// <summary>
    /// Run on the card: a new task. An open one is finished first; it stays in the list. While an agent works, the new
    /// task waits for it and starts by itself when that run is over (<see cref="StartNextTask"/>).
    /// </summary>
    private async Task RunAgentAsync(AgentPlan plan)
    {
        if (_agentSnapshot is not { } snapshot) return;
        // The task belongs to the project the card was opened for, not to the one that is active by now.
        if (_projects.Projects.FirstOrDefault(each => each.Id == plan.ProjectId) is not { } project)
        {
            _agentPanel?.ShowCardWarning("The project this card was opened for is gone: cancel the card and start the task again.");
            return;
        }

        _cardRun = null;
        if (AgentRunning)
        {
            _nextTask = (plan, snapshot);
            _agentPanel?.HideCard();
            ShowTask();
            AgentStatus($"Next task waits: \"{Shorten(plan.Goal.Split('\n')[0].Trim(), 60)}\" starts when this run ends");
            return;
        }

        await StartNewTaskAsync(plan, snapshot, project);
    }

    /// <summary>
    /// The new task the user ran while an agent worked, once the run is wholly over and nothing of the open task goes on
    /// by itself: a steer, words for after it, a team's next step (which start before this is called) take precedence.
    /// </summary>
    private void StartNextTask() =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_nextTask is not { } next || AgentRunning || _steerWith is not null || _queued is not null) return;
            _nextTask = null;
            if (_projects.Projects.FirstOrDefault(each => each.Id == next.Plan.ProjectId) is not { } project) return;
            _agentSnapshot = next.Snapshot;
            _ = StartNewTaskAsync(next.Plan, next.Snapshot, project);
        });

    private async Task StartNewTaskAsync(AgentPlan plan, InputSnapshot snapshot, LinkProject project)
    {
        if (_task is { Status: not AgentTaskStatus.Finished }) FinishTask();
        _backupBeforeDo = plan.BackupBeforeDo;
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        var agent = _settings.Current.Agent;
        // A team is planned by its lead, the Strong worker on its Deep level, read only (OfferTeam routes the plan).
        var lead = plan.Team ? WorkerRouting.Workers(agent, WorkerStateOf)[WorkerTier.Strong] : null;
        if (lead is not null) plan = plan with { Provider = lead.Provider, PlanFirst = true, RoleId = null, Skills = [] };
        // A training's study only reads: its exams edit, with the card's Auto.
        else if (plan.Train) plan = plan with { Access = AgentAccess.Analyze, PlanFirst = false };
        // A plan is made on the strongest model and carried out with the card's, in the same session.
        _planSetup = (plan.Model, plan.Effort);
        var (model, effort) = lead is not null ? agent.Level(lead.Provider, lead.Level)
            : plan.PlanFirst && agent.PlanOnDeep ? agent.Level(plan.Provider, AgentLevel.Deep) : _planSetup;
        // What was pasted or dropped on the card: images go as images, other files by their paths.
        var (goal, attachedImages, attachedFiles) = WithAttached(plan.Goal, plan.Attached);
        _attachedFiles.Clear();
        _attachedFiles.AddRange(attachedFiles);
        _agentOrigin = AgentCoordinator.BuildTask(plan with { Model = model, Effort = effort, Goal = goal }, snapshot, project);
        _agentOrigin = _agentOrigin with { Images = [.. _agentOrigin.Images, .. attachedImages] };
        var brief = _agentOrigin.Prompt;
        if (lead is not null)
        {
            var request = File.Exists(TeamPlan.TaskFile) ? await File.ReadAllTextAsync(TeamPlan.TaskFile) : string.Empty;
            var evidence = _projects.ReadWorkerEvidence(project).Summary(ShortName);
            _agentOrigin = _agentOrigin with { Prompt = TeamPlan.Prompt(brief, request, evidence, ProjectRoles.In(plan.Workspace)) };
        }

        // A training studies first, read only: its lessons wait for an exam (see MainWindow.Training).
        TrainingCourse? course = null;
        if (plan.Train && lead is null) (_agentOrigin, course) = await TrainingStartAsync(_agentOrigin, plan, project, attachedFiles);
        _taskProject = project;
        _task = new AgentTaskRecord
        {
            Goal = Shorten(plan.Goal.Split('\n')[0].Trim(), 200),
            Brief = brief,
            Workspace = plan.Workspace,
            AutoEffort = plan.AutoEffort,
            RoleId = plan.RoleId,
            Skills = [.. plan.Skills],
            Mode = lead is not null ? TaskMode.Team : course is not null ? TaskMode.Training : TaskMode.Solo,
            Training = course
        };
        var how = lead is not null ? $"\n(team: {WorkerLabel(lead)} plans the steps)"
            : course is not null ? "\n(training: study first, then an exam)"
            : plan.PlanFirst ? "\n(plan first)" : string.Empty;
        _task.Add(AgentEntryKind.Brief, goal.Trim() + ImagesNote(_agentOrigin.Images) + how);
        _earlierSession = null;
        _lastRun = null;
        _shownTask = _task;
        // The lead's effort is its Deep level's: planning the whole team is no task to save on.
        await StartAgentAsync(_agentOrigin, null, phase: lead is null ? EffortPhase.New : null);
    }

    private static string ImagesNote(IReadOnlyCollection<string> images) =>
        images.Count == 0 ? string.Empty : $"\n+ {Plural(images.Count, "image")}";

    /// <summary>
    /// The user's words. While the agent works they correct it: it stops the step it is on and the same session goes
    /// on with them. After a run they are a reply in the same session, maybe the answer to its question; a task from
    /// the list is taken up again in its folder, with a copy before edits where there is no version control.
    /// </summary>
    private void ContinueAgent(string reply, IReadOnlyList<string>? images = null, bool afterLimit = false)
    {
        if (reply.Trim() is not { Length: > 0 } text) return;
        images ??= [];
        if (AgentRunning)
        {
            // Link's own step (a note for Start over, a map) takes no words.
            if (_runningTask is not { } running || !IsUserWork(running) || _restartWhenStopped) return;
            _steerWith = text;
            _steerImages = images;
            _task?.Add(AgentEntryKind.You, "(steer now) " + text + ImagesNote(images));
            _agentCancellation?.Cancel();
            ShowTask();
            AgentStatus($"Steering {ShortName(running.Provider)} now: it stops the step and goes on with your words…");
            return;
        }

        _question = null;
        // A training ready for its exam: the words set it, in a new session.
        if (_task is { Training: { Stage: TrainingStage.AwaitsExam } course } training)
        {
            _ = StartExamAsync(training, course, text, images);
            return;
        }

        var phase = _planLeft is not null && ReferenceEquals(_lastRun, _planLeft) ? EffortPhase.RunPlan : EffortPhase.Reply;
        // After the wait for a limit, or a long idle, the session's cache is cold: a big one is re-read at full price, and
        // re-sent at each step. Where Settings allow it, a fresh session from the task's state takes the words instead
        // (never a plan's own run).
        if (phase != EffortPhase.RunPlan && _lastRun is not { Task.PlanOnly: true } && WhyCompact(afterLimit) is { } why)
        {
            CompactAndContinue(text, images, why, phase);
            return;
        }

        _task?.Add(AgentEntryKind.You, text + ImagesNote(images));
        if (_earlierSession is { } earlier)
        {
            _agentOrigin = earlier with { ResumeSessionId = null };
            _backupBeforeDo = true;
            _ = StartAgentAsync(earlier with { Prompt = text, Images = images }, null, phase: phase);
            return;
        }

        if (_lastRun is { } run && WordsAfter(run, text) is { } task)
        {
            _ = StartAgentAsync(task with { Images = images }, run, phase: phase);
        }
    }

    /// <summary>
    /// The run that takes the user's words after <paramref name="ended"/>: its session goes on with them, and a new
    /// session stopped before it began starts the task again with them. Null when there is neither.
    /// </summary>
    private AgentTask? WordsAfter(AgentRun ended, string words) =>
        AgentCoordinator.ContinueTask(ended, words)
        ?? (_agentOrigin is { } origin && IsUserWork(ended.Task) ? origin with { Prompt = $"{origin.Prompt}\n\nThe user adds:\n{words}" } : null);

    /// <summary>Whether the open task takes words now: a session to go on with, or a first run stopped before its session began.</summary>
    private bool TakesWords => ResumableTask() is not null || (_lastRun is { } run && WordsAfter(run, "-") is not null);

    /// <summary>
    /// Why words that would resume the task's last session should start a fresh one instead (see <see cref="CompactRule"/>);
    /// null: resume as it is, or Settings do not let Link compact by itself.
    /// </summary>
    private string? WhyCompact(bool afterLimit) =>
        _task is { } record && ResumableTask() is not null
            ? CompactRule.Why(record.LastRun, _settings.Current.Agent.AutoCompact, _settings.Current.Agent.ContextBudget, DateTimeOffset.Now, afterLimit)
            : null;

    /// <summary>
    /// Compact by itself: a fresh session of the same agent takes the words from the task's state, as Compact does, but
    /// without the card to look at first. The history says why. The state has no git lines per file: asking git would
    /// leave a gap in which another reply could start a second session.
    /// </summary>
    private void CompactAndContinue(string words, IReadOnlyList<string> images, string why, EffortPhase phase)
    {
        if (_task is not { } record || (ResumableTask()?.Task ?? _agentOrigin) is not { } template) return;
        var state = TaskState.CompactContinue(record, null, record.Workspace, words, AgentName);
        var task = AgentCoordinator.CompactTask(template, record, state, images) with
        {
            Access = AgentAccess.Do,
            AllowedTools = AgentCoordinator.Tools(_taskProject ?? _projects.ActiveProject, template.Workspace, AgentAccess.Do)
        };
        record.Add(AgentEntryKind.You, words + ImagesNote(images));
        _ = StartCompactedAsync(task, record, why, phase);
    }

    /// <summary>A fresh session of the same agent starts from the task's state: the history says so, and Start over begins from it too.</summary>
    private Task StartCompactedAsync(AgentTask task, AgentTaskRecord record, string? why, EffortPhase phase)
    {
        record.Add(AgentEntryKind.HandOff, TaskState.Compacted(record.LastRun?.ContextTokens) + (why is null ? string.Empty : " · " + why));
        _agentOrigin = task;
        if (_lastRun is null) _backupBeforeDo = true;
        return StartAgentAsync(task, _lastRun, phase: phase);
    }

    /// <summary>
    /// The user's words with what they pasted or dropped: images go as images, other files are named by path at the end
    /// of the words, and the next run may read their folders.
    /// </summary>
    private (string Words, IReadOnlyList<string> Images, IReadOnlyList<string> Files) WithAttached(string words, IReadOnlyList<string> attached)
    {
        var images = attached.Where(AgentPanel.IsImage).ToArray();
        var files = attached.Except(images).ToArray();
        if (files.Length > 0) words = $"{words.Trim()}\n\nAttached files:\n{string.Join('\n', files.Select(file => "- " + file))}";
        return (words.Trim(), images, files);
    }

    /// <summary>A reply from the Studio with what came along with it.</summary>
    private void ContinueWithAttached(string words, IReadOnlyList<string> attached, bool queue)
    {
        var (text, images, files) = WithAttached(words, attached);
        _attachedFiles.AddRange(files);
        if (queue) QueueAfterRun(text, images);
        else ContinueAgent(text, images);
    }

    /// <summary>Tab while the agent works: the words wait for the end of the run, then the same session goes on with them.</summary>
    private void QueueAfterRun(string words, IReadOnlyList<string> images)
    {
        if (!AgentRunning || _runningTask is null) return;
        _queuedImages.AddRange(images);
        _queued = _queued is null ? words : $"{_queued}\n{words}";
        _task?.Add(AgentEntryKind.You, "(after this step) " + words);
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
        task.ContinueAt = null;
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
        RefreshStudio();
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
            _ = ShowChangesAsync(record);
            return;
        }

        if (!Directory.Exists(record.Workspace))
        {
            SetError($"The task's folder is gone: {record.Workspace}");
            return;
        }

        // The wait for a limit's reset belongs to the task it was asked on: another task is no place for its words.
        CancelContinue();
        TakeUpTask(record, _projects.ActiveProject);
        // A task that was left waiting for a reset (Link restarted meanwhile) goes on waiting.
        ArmContinue(record);
        _agentPanel?.ResetCards();
        ShowTaskInPanel();
        // A team left mid-way goes on from the step it is on; a question it ended on is answered first.
        if (record.Team.Count > 0 && _question is null) OfferTeamGoOn(record);
        // A study that drew its lessons still offers the exam.
        if (record.Training is { Stage: TrainingStage.Learning, Lessons.Count: > 0 } course && _question is null) OfferExam(record, course);
        _ = ShowChangesAsync(record);
        UpdateRouteButton();
        AgentStatus(_earlierSession is null
            ? "This task has no session to go on with"
            : $"Your words go on with the task: in its thread, or with Enter on the Agent route");
    }

    /// <summary>The task becomes the open one in <paramref name="project"/>: its last session is what the next words take up.</summary>
    private void TakeUpTask(AgentTaskRecord record, LinkProject project)
    {
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
    }

    /// <summary>The open task in the Studio.</summary>
    private void ShowTaskInPanel()
    {
        ShowAgentPanel();
        ShowOpenTask();
    }

    private void ShowOpenTask()
    {
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
        if (IsUserWork(task) && _attachedFiles.Count > 0)
        {
            task = task with { Files = [.. task.Files.Union(_attachedFiles, StringComparer.OrdinalIgnoreCase)] };
            _attachedFiles.Clear();
        }

        // The user's words take the setup chosen for them in the composer; Auto, when it stays on, then picks the effort.
        if (IsUserWork(task) && phase is EffortPhase.Reply or EffortPhase.Steer or EffortPhase.RunPlan) task = WithNextSetup(task);
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
            CancelContinue();
            _task!.Status = AgentTaskStatus.Running;
            // A session that goes on keeps its tasks; the parser follows the agent's TaskUpdate by their numbers.
            if (task.ResumeSessionId is not null && _task.LastRun?.Provider == task.Provider) task = task with { Plan = _task.Plan };
        }

        _runningTask = task;
        _usageRun = (DateTimeOffset.Now, _limits.For(task.Provider), []);
        _limitOffered = false;
        _runSteps.Clear();
        // A session taken up again holds what its last run left, and the model's window stays until a run says otherwise.
        _liveContext = !own && _task?.LastRun is { } last && last.Provider == task.Provider
            ? (last.SessionId == task.ResumeSessionId ? last.ContextTokens : null, last.ContextWindow)
            : default;
        _runningFiles.Clear();
        _liveTurn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason)}", "Starting…",
            (Brush)FindResource("AccentBrush"), RoleBrushes.For(task.RoleId, WorkspaceRoles(task.Workspace))) { StepsOpen = true };
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
            ? $"{provider.Name} writes {OwnWhat(task)} · Stop is in the Studio"
            : $"{provider.Name} is working · its thread is in the Studio");
        UpdateAgentPill();
        UpdateRouteButton();
        RefreshTaskList();
        var progress = new Progress<AgentEvent>(_pendingAgentEvents.Add);
        AgentRun? ended = null;
        Exception? failure = null;
        try
        {
            var run = await _agents.RunAsync(task, _backupBeforeDo, previous, progress, cancellation.Token);
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
            RefreshStudio();
            StartNextTask();
            return;
        }

        if (ended is not null) ShowAgentRun(ended);
        else if (failure is not null) ShowAgentFailure(provider, task, failure);
        _liveTurn = null;
        UpdateAgentPill();
        UpdateRouteButton();
        RefreshTaskList();
        RefreshStudio();

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
            if (ended is not null && WordsAfter(ended, AgentCoordinator.Interrupted(words)) is { } next)
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
            IReadOnlyList<string> images = [.. _queuedImages];
            _queuedImages.Clear();
            if (ended is not null && WordsAfter(ended, later) is { } next) await StartAgentAsync(next with { Images = images }, ended, phase: EffortPhase.Reply);
        }

        StartNextTask();
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
        var role = ProjectRoles.Find(record.RoleId, record.Workspace)?.Effort;
        // A team's step is a new session of its own worker: its effort follows the step's kind and its own failures.
        var (effort, reason) = TeamRunner.Current(record) is { Status: TeamStepStatus.Running } step
            ? AutoEffort.ForStep(step.Kind, efforts, step.Failures)
            : AutoEffort.Choose(phase, record.Training is { Stage: TrainingStage.Exam, Exams: [.., var exam] } ? exam.Brief : BriefOf(record),
                efforts, last?.Effort, AutoEffort.Failures(record.Runs),
                last is null ? null : AgentNext.Split(last.Words).Next, role);
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
        panel.ShowTaskView($"Link writes {OwnWhat(task)}", $"{_projects.ActiveProject.Name} · {task.Workspace}", null);
        panel.SetStatus(running ? "working" : own.Status, (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot));
        panel.SetHeader(running ? AgentHeader(task) : $"{AgentName(task.Provider)} · {SetupWords(task.Provider, task.Model, task.Effort)} · read only");
        panel.ShowPlan([]);
        panel.ShowChanges(string.Empty, string.Empty);
        panel.ShowThread([own.Turn]);
        panel.ShowUsage(string.Empty, string.Empty);
        panel.ResetCards();
        panel.ShowQuestion(null);
        panel.ShowMenus([], OwnActions(own));
        panel.ShowFinish(false);
        panel.ShowPlanMode(false);
        panel.SetRunning(running);
        // Link's own answer takes no words.
        panel.ShowReply(false, running ? null : "Link's own answer: the file is written. The tasks are on the left.");
        panel.ShowSetup(string.Empty, []);
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
        turn.Stats = StatsLine(result.Stats) + SpentLine(run.Task.Provider, SpentByRun(run.Task.Provider));
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
        AgentStatus($"{provider.Name} writes a note for a new session · Stop is in the Studio");
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
    /// Escalate: the same session goes on with the strongest model and effort of Settings, for a problem the task's model
    /// did not solve. One cache miss: the model reads the session anew. Auto goes on after it.
    /// </summary>
    private void Escalate()
    {
        if (AgentRunning || ResumableTask() is not { } resume) return;
        var (model, effort) = _settings.Current.Agent.Level(resume.Task.Provider, AgentLevel.Deep);
        _task?.Add(AgentEntryKind.Model, $"{AgentName(resume.Task.Provider)} · {SetupWords(resume.Task.Provider, model, effort)}, escalated");
        if (resume.Previous is null) _backupBeforeDo = true;
        _ = StartAgentAsync(resume.Task with
        {
            Model = model,
            Effort = effort,
            Prompt = "The work is not done yet. Think it through again from what you know, find the cause and finish the task.",
            Images = []
        }, resume.Previous);
    }

    /// <summary>The task's last session as a run to continue, and the run it continues; null when there is none.</summary>
    private (AgentTask Task, AgentRun? Previous)? ResumableTask() =>
        _lastRun is { } run && AgentCoordinator.SessionOf(run) is { } session ? (run.Task with { ResumeSessionId = session, PlanOnly = false }, run)
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
        var images = _agentOrigin?.Images ?? [];
        AgentCardModel Build() => CardModel(brief, string.Empty, images, to, _taskProject) with
        {
            Title = $"Hand off to {AgentName(to)} · new session",
            RunLabel = $"Hand off to {AgentName(to)}",
            FixedProvider = true,
            AutoEffort = record.AutoEffort && _settings.Current.Agent.AutoEffort,
            // The new session works as the task's specialist, unless it is changed here.
            RoleId = record.RoleId,
            Skills = record.Skills
        };
        var card = Build() with { Renew = Build };
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
        record.Handovers.Add($"── Handed over to {AgentName(plan.Provider)} ──\n{plan.Goal}");
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
        var images = _agentOrigin?.Images ?? [];
        AgentCardModel Build()
        {
            var plain = CardModel(state, string.Empty, images, provider, _taskProject);
            return plain with
            {
                // The card starts on the model and effort of the last run: Settings' Standard could be a silent downgrade.
                Providers = [.. plain.Providers.Select(agent => agent.Kind == provider ? agent with { Model = resume.Task.Model, Effort = resume.Task.Effort } : agent)],
                Access = resume.Task.AutoApprove ? AgentAccessChoice.Auto : AgentAccessChoice.Edit,
                Title = $"Compact · new session of {AgentName(provider)}",
                RunLabel = "Compact",
                FixedProvider = true,
                AutoEffort = record.AutoEffort && _settings.Current.Agent.AutoEffort,
                RoleId = record.RoleId,
                Skills = record.Skills
            };
        }

        var card = Build() with { Renew = Build };
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
        // Start over begins from the state, not from the session that has grown.
        await StartCompactedAsync(task, record, null, EffortPhase.Reply);
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
        record.Handovers.Add($"── Back to {AgentName(provider)} ──\n{prompt}");
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
            AnswerFile = null
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

    /// <summary>
    /// A step as the thread shows it; null for what has a place of its own (the answer, files, plan, usage). A tool
    /// starts with the mark of what it did, which groups the steps between the agent's words.
    /// </summary>
    private static string? StepText(AgentEvent agentEvent) => agentEvent.Kind switch
    {
        AgentEventKind.Completed or AgentEventKind.Artifact or AgentEventKind.ToolFailed or AgentEventKind.Plan or AgentEventKind.Limit
            or AgentEventKind.Context => null,
        AgentEventKind.Tool => StepGroups.Step(agentEvent),
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
        : StepGroups.KindOf(text) is not null || text.StartsWith('·') ? "MutedBrush"
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
            if (shown && userWork) ShowPlanOf(_task!);
        }

        if (userWork && events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Context) is { } context)
        {
            _liveContext = (context.ContextTokens, context.ContextWindow ?? _liveContext.Window);
            if (shown)
            {
                var (usage, hint) = UsageText(_task!);
                _agentPanel.ShowUsage(usage, hint);
            }

            RefreshStudio(live: true);
        }

        if (events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Limit))
        {
            foreach (var usage in events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Limit))
            {
                _limits.Update(usage.Limits);
                // A window's first report during the run is what its use is measured from where the start is not to be trusted.
                foreach (var limit in usage.Limits)
                {
                    if (!_usageRun.First.Any(each => each.Provider == limit.Provider && each.Window == limit.Window)) _usageRun.First.Add(limit);
                }
            }

            if (shown) _agentPanel.ShowLimits(AllLimitRows());
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
        AgentStatus($"{AgentName(provider)} is near its usage limit · the Studio offers a way on");
    }

    private string AgentHeader(AgentTask task)
    {
        var access = task.PlanOnly ? "plan"
            : task.AnswerFile is { } file ? $"read only, {Path.GetFileName(file)}"
            : task.Access == AgentAccess.Do ? task.AutoApprove ? "auto" : "edits"
            : "read only";
        var elapsed = _agentClock.Elapsed;
        return $"{RoleWords(task.RoleId, task.Workspace)}{_agents.Provider(task.Provider).Name} · " +
               $"{SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason)} · {access} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>The workspace's specialists, read when the first one is wanted: for the colours of their jellyfish.</summary>
    private static Lazy<IReadOnlyList<AgentRole>> WorkspaceRoles(string workspace) => new(() => ProjectRoles.In(workspace));

    /// <summary>The roles that colour a plan's jellyfish: read only when a step of the plan names its specialist.</summary>
    /// <summary>The task's plan card: a team's steps with their workers, a training's lessons, else the agent's own plan.</summary>
    private void ShowPlanOf(AgentTaskRecord record)
    {
        if (record.Training is { } course)
        {
            _agentPanel?.ShowPlan(Training.PlanItems(course), title: "Lessons");
            return;
        }

        var plan = record.Team.Count > 0 ? TeamPlanItems(record) : record.Plan;
        _agentPanel?.ShowPlan(plan, PlanRoles(plan, record.Workspace));
    }

    private static IReadOnlyList<AgentRole>? PlanRoles(IReadOnlyList<AgentPlanItem> plan, string workspace) =>
        plan.Any(item => !string.IsNullOrEmpty(item.Role)) ? ProjectRoles.In(workspace) : null;

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
    /// Where Link chose the effort, <paramref name="autoReason"/> says why: "Opus 5.5 · Medium · auto: a usual task". The
    /// model the CLI said it ran (<paramref name="ranModel"/>) follows a family: "Haiku · latest (claude-haiku-4-5)".
    /// </summary>
    private string SetupWords(AgentProviderKind provider, string? model, string? effort, string? autoReason = null, string? ranModel = null)
    {
        var agent = _agents.Provider(provider);
        var own = agent.OwnSetup;
        var modelName = (model ?? own.Model) is { } chosen ? ModelName(agent, chosen) : "its own model";
        if (ranModel is not null) modelName += $" ({ranModel})";
        var words = (effort ?? own.Effort) is { } level ? $"{modelName} · {EffortName(agent, level)}" : modelName;
        return autoReason is null ? words : $"{words} · auto: {autoReason}";
    }

    private string RunLine(AgentRunRecord run, string workspace) =>
        $"{RoleWords(run.RoleId, workspace)}{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, run.EffortReason, run.RanModel)} · " +
        $"{(run.Access != AgentAccess.Do ? "read only" : run.AutoApprove ? "auto" : "edits")}";

    /// <summary>Where the task works: the project, the folder and, in Perforce, its changelist.</summary>
    private string WhereLine(AgentTaskRecord record) =>
        $"{(_taskProject ?? _projects.ActiveProject).Name}{Stack(record.Workspace)} · {record.Workspace}" +
        (record.Changelist is { } changelist ? $" · P4 changelist {changelist}" : string.Empty);

    /// <summary>The tool packs a workspace uses, after the project's name: " · Unreal + Blender + Perforce"; empty for none.</summary>
    private static string Stack(string workspace) => ToolPacks.Names(workspace) is { Length: > 0 } names ? " · " + names : string.Empty;

    /// <summary>
    /// After a task that is done: a lesson the agent kept in the project's copy of a pack's skill may go to every
    /// project, on the user's word. One at a time; the next task's end offers the next.
    /// </summary>
    private void OfferShare(AgentTaskRecord record)
    {
        if (record.Runs.FirstOrDefault() is not { } first || ToolPacks.Lessons(record.Workspace, first.StartedAt).FirstOrDefault() is not ({ } copy, { } shared)) return;
        _agentPanel!.ShowDecision(
            $"The task changed this project's copy of the {shared.Pack} pack's skill {copy.Name}. Share it with all games? " +
            $"The pack's skill takes it, and the copy leaves {ProjectSkills.RelativeFolder}.",
            "Share with all games",
            canRemember: false);
        _decision = () =>
        {
            try
            {
                ToolPacks.Share(copy, shared);
                record.Add(AgentEntryKind.HandOff, $"Shared skill {copy.Name} with all games ({shared.Pack} pack)");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                record.Add(AgentEntryKind.HandOff, $"Could not share skill {copy.Name}: {exception.Message}");
            }

            SaveTask(record);
            ShowTask();
        };
    }

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
        var spent = SpentByRun(task.Provider);
        // The notifications name the task's project, which the user may have left for another meanwhile.
        var project = _taskProject ?? _projects.ActiveProject;
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
        var answer = task.AnswerFile is null ? result.Summary : AgentCoordinator.Unfenced(result.Summary);
        // The lead's plan is the plan card and its verdict a line of the thread: the answer is shown and kept without them.
        if (_task is { Mode: TaskMode.Team or TaskMode.Training }) answer = TeamPlan.WithoutBlocks(answer);
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
        _afterRun = new[]
        {
            new PanelAction("Discuss in chat", "The result, the changed files and their diff go with your next question to ChatGPT",
                () => _ = DiscussInChatAsync()),
            undo,
            ConsoleAction()
        }.OfType<PanelAction>().ToArray();

        if (_liveTurn is { } turn)
        {
            turn.Current = string.Empty;
            turn.Outcome = ending;
            turn.OutcomeBrush = OutcomeBrush(ending);
            turn.Said = text;
            turn.Files = run.Changes.Select(ToChangeRow).ToArray();
            turn.Stats = StatsLine(result.Stats) + SpentLine(task.Provider, spent);
            turn.StepsOpen = false;
        }

        var decision = OfferDecision(run);
        var needsYou = decision || _question is not null;
        RecordRun(run, ending, text, checkFailed ? text : answer, needsYou, stopped, checkFailed, spent);
        if (_task is not null && IsUserWork(task))
        {
            _ = ShowChangesAsync(_task);
            ShowTask();
            if (_task.Team.Count > 0) AfterTeamStep(_task, run, needsYou: _question is not null || decision || limitReached);
            if (_task.Training is not null) AfterTrainingRun(_task, run, needsYou: _question is not null || decision || limitReached);
            // A training shares the skills its exam's lessons went into once they are written, not on the way.
            if (!needsYou && _decision is null && _task.LastRun is { } done && WorkerEvidence.Verdict(done) == true && TeamRunner.Current(_task) is null &&
                _task.Training is null or { Stage: TrainingStage.AwaitsExam })
            {
                OfferShare(_task);
            }
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
        if (_task is not null && IsUserWork(task)) ShowResultInChat(run, diff: null);

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
            ? $"{provider.Name} ran out of its limit · the Studio offers a way on"
            : _question is not null ? $"{provider.Name} asks: the choice is in the Studio"
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
    private void RecordRun(AgentRun run, string ending, string text, string answer, bool needsYou, bool stopped, bool checkFailed,
        IReadOnlyList<UsageSpent> spent)
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
            RanModel = result.Model != task.Model ? result.Model : null,
            Effort = task.Effort,
            EffortReason = task.EffortReason,
            RoleId = task.RoleId,
            Access = task.Access,
            AutoApprove = task.AutoApprove,
            SessionId = AgentCoordinator.SessionOf(run),
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
            Spent = [.. spent],
            Steps = [.. _runSteps.TakeLast(StepsKeptPerRun)],
            Files = files,
            Words = Shorten(answer, 12_000)
        });
        record.Add(AgentEntryKind.Run,
            $"{AgentName(task.Provider)} · {SetupWords(task.Provider, task.Model, task.Effort, ranModel: record.Runs[^1].RanModel)}: " +
            $"{ending.ToLowerInvariant()}, {Plural(run.Changes.Count, "file")} changed");
        // A family the task asked for (haiku) got a newer model than last time: never a silent change of behaviour.
        if (task.Provider == AgentProviderKind.ClaudeCode && _settings.Current.Agent.NoteResolved(task.Model, result.Model) is { } resolved)
        {
            if (resolved.Changed is { } change) record.Add(AgentEntryKind.Model, $"Claude Code takes a newer model now: {change}");
            _settings.Save();
        }
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
            _stats = null;
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
                "The task goes on by itself a minute after the limit resets, also if Link is restarted before then; a big session starts fresh from the task's state",
                () => ContinueAfterReset(at)));
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

    /// <summary>
    /// The task goes on by itself a minute after the limit resets. The time is kept with the task, so that a restart of
    /// Link does not lose it; Finish, another task or a new run cancels it.
    /// </summary>
    private void ContinueAfterReset(DateTimeOffset reset)
    {
        if (_task is not { } record) return;
        var at = reset.AddMinutes(1);
        record.ContinueAt = at;
        record.Add(AgentEntryKind.You, $"Continue at {AgentLimits.Time(at)}");
        SaveTask(record);
        ArmContinue(record);
        ShowTask();
        AgentStatus($"The task goes on by itself at {AgentLimits.Time(at)}, also if Link is restarted before then");
    }

    /// <summary>
    /// Starts the timer that sends the task on at its <see cref="AgentTaskRecord.ContinueAt"/>; a time already past is a
    /// second away. It can only be of the open task: the words never reach another.
    /// </summary>
    private void ArmContinue(AgentTaskRecord record)
    {
        _continueAt?.Stop();
        if (record.ContinueAt is not { } at) return;
        var wait = at - DateTimeOffset.Now;
        var timer = new DispatcherTimer { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_task, record)) return;
            // Link's own answer (the map) is being written: a minute later.
            if (AgentRunning)
            {
                timer.Interval = TimeSpan.FromMinutes(1);
                timer.Start();
                return;
            }

            record.ContinueAt = null;
            record.Add(AgentEntryKind.Limit, "The limit reset: the session goes on");
            SaveTask(record);
            ContinueAgent("Your usage limit has reset. Go on with the task from where you stopped.", afterLimit: true);
        };
        _continueAt = timer;
        timer.Start();
    }

    /// <summary>The wait for a limit's reset is over without firing: the timer stops and the task no longer remembers it.</summary>
    private void CancelContinue()
    {
        _continueAt?.Stop();
        if (_task is not { ContinueAt: not null } record) return;
        record.ContinueAt = null;
        SaveTask(record);
    }

    /// <summary>
    /// Link started, or another project was loaded: the active project's task that waits for a limit's reset (its time was
    /// kept with it) is taken up again, without opening the Studio, and its timer runs. Nothing when a task is open.
    /// </summary>
    private void RearmContinue()
    {
        if (_task is not null || AgentRunning) return;
        var project = _projects.ActiveProject;
        var waiting = _projects.ReadAgentTasks(project)
            .FirstOrDefault(task => task.ContinueAt is not null && task.Status != AgentTaskStatus.Finished && Directory.Exists(task.Workspace));
        if (waiting is null) return;
        TakeUpTask(waiting, project);
        ArmContinue(waiting);
        UpdateAgentPill();
        UpdateRouteButton();
    }

    /// <summary>
    /// The size of the task's changes in its header: the files at once, then the lines git counts for them. Asked once
    /// when a run ends or a task opens, never on a timer; git runs off the UI thread.
    /// </summary>
    private async Task ShowChangesAsync(AgentTaskRecord record)
    {
        var files = record.ChangedFiles.ToArray();
        SetChanges(record, TaskChanges.Describe(null, files));
        SetChanges(record, TaskChanges.Describe(await _agents.Changes.NumstatAsync(record.Workspace, files, CancellationToken.None), files));
    }

    private void SetChanges(AgentTaskRecord record, (string Text, string Hint) changes)
    {
        _changes = (record.Id, changes.Text, changes.Hint);
        if (!_ownShown && _shownTask?.Id == record.Id) _agentPanel?.ShowChanges(changes.Text, changes.Hint);
    }

    /// <summary>The open task in the Studio, unless another task is shown there to be read.</summary>
    private void ShowTask()
    {
        if (_agentPanel is null || _task is not { } record || _ownShown) return;
        if (_shownTask is not null && _shownTask.Id != record.Id) return;
        _shownTask = record;
        ShowRecord(record, readOnly: false);
    }

    /// <summary>
    /// A task in the Studio: its goal and standing, the line of who works on what, its plan, thread and usage. The
    /// open task offers its menus and the box for the next words; another, read while an agent works, only shows.
    /// </summary>
    private void ShowRecord(AgentTaskRecord record, bool readOnly)
    {
        var panel = _agentPanel!;
        _ownShown = false;
        var live = !readOnly && AgentRunning && _runningTask is { } running && IsUserWork(running);
        panel.ShowTaskView(record.Goal, WhereLine(record), record.Id);
        var (status, dot) = Standing(record, live);
        panel.SetStatus(status, dot);
        panel.SetHeader(live ? AgentHeader(_runningTask!) : record.LastRun is { } last ? RunLine(last, record.Workspace) : "No run yet");
        ShowPlanOf(record);
        var (changed, changedFiles) = _changes.Task == record.Id ? (_changes.Text, _changes.Hint) : TaskChanges.Describe(null, record.ChangedFiles);
        panel.ShowChanges(changed, changedFiles);
        panel.ShowThread(BuildThread(record, live));
        var (usage, hint) = UsageText(record);
        panel.ShowUsage(usage, hint);
        var provider = (live ? _runningTask?.Provider : null) ?? record.LastRun?.Provider ?? StartProvider;
        panel.SetRunning(live);
        panel.SetReplyTarget(AgentName(provider));
        if (readOnly)
        {
            panel.ResetCards();
            panel.ShowMenus([], []);
            panel.ShowFinish(false);
            panel.ShowPlanMode(false);
            panel.ShowReply(false, $"{AgentName(_runningTask?.Provider ?? provider)} works on another task: open this one again once it ends to go on with it.");
            panel.ShowSetup(string.Empty, []);
            RefreshStudio();
            return;
        }

        panel.ShowQuestion(_question);
        ShowTrainingOf(record, live);
        panel.ShowMenus(HandOffActions(record), MoreActions());
        panel.ShowCheaper(live || AgentRunning ? null : CheaperHintFor(record), AgentName(AgentProviderKind.Custom));
        panel.ShowFinish(true);
        panel.ShowPlanMode(!live && !AgentRunning && _lastRun is { Task.PlanOnly: true });
        if (live) panel.ShowReply(true);
        // One agent at a time: Link's own answer, such as the map, goes first.
        else if (AgentRunning && _runningTask is { } busy) panel.ShowReply(false, $"{AgentName(busy.Provider)} writes {OwnWhat(busy)}: your words can go once it ends.");
        else if (TakesWords) panel.ShowReply(true);
        else panel.ShowReply(false, "This task has no session to go on with: hand it to an agent, or start it anew from its card.");
        ShowSetup(panel, record);
        UpdateAgentPill();
        RefreshStudio();
    }

    /// <summary>
    /// The hint that the custom model could do what is left of the open task, with its reason: the plan waiting to be
    /// carried out, or the project's own record of the model for the task's specialist. Null when there is none, or
    /// nothing left, or the task cannot be handed over.
    /// </summary>
    private string? CheaperHintFor(AgentTaskRecord record)
    {
        if (record.Status is AgentTaskStatus.Done or AgentTaskStatus.Finished || ResumableTask() is null) return null;
        var custom = _agents.Provider(AgentProviderKind.Custom);
        var planWaits = _decision is not null && _lastRun is { Task.PlanOnly: true };
        return CheaperHint.For(record, custom.Configured, custom.Name, RoleName(record.RoleId, record.Workspace) ?? "General",
            ProjectStats(_taskProject ?? _projects.ActiveProject, record.Workspace), planWaits ? CheaperHint.RunPlan : "Task");
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
        // The workspace's role files are read once for the whole thread, and only if a run worked as a specialist.
        var roles = WorkspaceRoles(record.Workspace);
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
                    turn = TurnOf(record, run, entry.Text, showNext: !live && run is not null && ReferenceEquals(run, record.LastRun), roles);
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
    private ThreadTurn TurnOf(AgentTaskRecord record, AgentRunRecord? run, string line, bool showNext, Lazy<IReadOnlyList<AgentRole>> roles)
    {
        // "Claude Code · Opus 5.5: done in 3:12, 2 files changed".
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        var who = run is not null ? $"{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, run.EffortReason, run.RanModel)}"
            : colon > 0 ? line[..colon] : line;
        var outcome = run?.Outcome ?? (colon > 0 ? line[(colon + 2)..] : string.Empty);
        var turn = new ThreadTurn(who, outcome, OutcomeBrush(outcome), RoleBrushes.For(run?.RoleId, roles));
        if (run is null) return turn;
        turn.AddSteps(run.Steps.Select(StepRow));
        // A team's answers were kept with the lead's plan before Link took it out, a training's with its lessons.
        var words = record.Mode is TaskMode.Team or TaskMode.Training ? TeamPlan.WithoutBlocks(run.Words) : run.Words;
        var (said, next) = AgentNext.Split(AgentQuestion.Split(words).Text);
        turn.Said = AgentChanges.SplitChangelistLine(said).Text;
        if (showNext && next is not null) turn.Next = next.Line;
        turn.Files = run.Files.Select(file => new AgentChangeRow(file, Path.Combine(record.Workspace, file))).ToArray();
        var tokens = run.InputTokens is { } input
            ? $" · {AgentContext.Tokens(input)} in ({AgentContext.Tokens(run.CachedTokens ?? 0)} cached), {AgentContext.Tokens(run.OutputTokens ?? 0)} out"
            : string.Empty;
        turn.Stats = $"{(int)run.Duration.TotalMinutes}:{run.Duration.Seconds:00}{tokens}{SpentLine(run.Provider, run.Spent)}";
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
    /// Hand off ▾: the other agent in a new session (with a brief to look at first), or the first agent back in its own
    /// session. Another model, effort or specialist for the same session is the composer's choice (<see cref="ShowSetup"/>).
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
        foreach (var other in others.Where(other => record.Runs.Any(run => run.Provider == other && run.SessionId is not null)))
        {
            actions.Add(new PanelAction($"Back to {AgentName(other)} · its own session",
                $"{AgentName(other)} goes on in its own session, told what {AgentName(current)} did meanwhile",
                () => _ = ReturnToAsync(other)));
        }

        return actions;
    }

    /// <summary>
    /// What the user's next words in the task's session run on, chosen in the composer: the model, the effort (null with
    /// Auto, which the task record keeps) and the specialist. Nothing runs when it is chosen; the next run takes it.
    /// </summary>
    private sealed record NextSetup(Guid TaskId, AgentProviderKind Provider, string? Model, string? Effort, string? RoleId);

    /// <summary>The setup the open task's next words run on: the one chosen, else the session's own; null when no session takes words.</summary>
    private NextSetup? CurrentSetup()
    {
        if (_task is not { } record) return null;
        if (_nextSetup is { } chosen && chosen.TaskId == record.Id) return chosen;
        var task = _runningTask is { } running && IsUserWork(running) ? running : ResumableTask()?.Task ?? _lastRun?.Task;
        return task is null ? null : new NextSetup(record.Id, task.Provider, task.Model, task.Effort, record.RoleId);
    }

    /// <summary>
    /// The composer's choice of model, effort and specialist for the next words, like the model picker of the agents' own
    /// apps: the line says what they run on, the menu changes it, and the runs after keep it.
    /// </summary>
    private void ShowSetup(AgentPanel panel, AgentTaskRecord record)
    {
        if (CurrentSetup() is not { } setup)
        {
            panel.ShowSetup(string.Empty, []);
            return;
        }

        var agent = _agents.Provider(setup.Provider);
        var auto = record.AutoEffort && _settings.Current.Agent.AutoEffort;
        var efforts = agent.EffortsOf(setup.Model);
        var effortChoices = efforts.Select(effort => new PanelAction(effort.Label, "This effort from the next words on; Auto stops choosing it",
            () => ChooseSetup(setup with { Effort = effort.Value }, auto: false), Checked: !auto && setup.Effort == effort.Value)).ToList();
        if (efforts.Count > 0 && _settings.Current.Agent.AutoEffort)
        {
            effortChoices.Insert(0, new PanelAction("Auto effort", "Link chooses the effort of each run, and says why in its line",
                () => ChooseSetup(setup, auto: true), Checked: auto));
        }

        var models = agent.Models.Select(model => new PanelAction(model.Label,
            "The same session goes on with this model: it keeps what it knows, the model reads it anew once",
            () => ChooseSetup(setup with
            {
                Model = model.Value,
                Effort = agent.EffortsOf(model.Value).Any(effort => effort.Value == setup.Effort) ? setup.Effort : null
            }, auto), Checked: setup.Model == model.Value)).ToArray();
        var roles = ProjectRoles.In(record.Workspace).Select(role => new PanelAction(role.Name,
                $"{role.Description} The same session goes on as this specialist, with its skills: it does not read the project again",
                () => ChooseSetup(setup with { RoleId = role.Id }, auto),
                Checked: string.Equals(role.Id, setup.RoleId, StringComparison.OrdinalIgnoreCase)))
            .Prepend(new PanelAction("General", "The same session goes on without a specialist role",
                () => ChooseSetup(setup with { RoleId = null }, auto), Checked: setup.RoleId is null)).ToArray();
        var effortWords = auto ? "Auto" : setup.Effort is { } effortValue ? EffortName(agent, effortValue) : null;
        var label = string.Join(" · ", new[]
        {
            RoleName(setup.RoleId, record.Workspace) ?? "General",
            setup.Model is { } modelValue ? ModelName(agent, modelValue) : null,
            effortWords
        }.OfType<string>());
        panel.ShowSetup(label, [effortChoices, models, roles]);
    }

    private void ChooseSetup(NextSetup setup, bool auto)
    {
        if (_task is not { } record) return;
        record.AutoEffort = auto;
        SaveTask(record);
        _nextSetup = setup;
        if (_agentPanel is { } panel) ShowSetup(panel, record);
    }

    /// <summary>
    /// The run with the setup chosen for it in the composer, when it is the open task's and its agent's: the model and
    /// effort, and another specialist, told so before the user's words.
    /// </summary>
    private AgentTask WithNextSetup(AgentTask task)
    {
        if (_nextSetup is not { } next || _task is not { } record || next.TaskId != record.Id) return task;
        _nextSetup = null;
        if (next.Provider != task.Provider) return task;
        task = task with { Model = next.Model, Effort = next.Effort };
        if (string.Equals(next.RoleId, record.RoleId, StringComparison.OrdinalIgnoreCase)) return task;
        var role = ProjectRoles.Find(next.RoleId, record.Workspace);
        var skills = ProjectRoles.SkillsFor(BriefOf(record), role, ProjectSkills.In(record.Workspace));
        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        record.Add(AgentEntryKind.HandOff, $"Specialist: {role?.Name ?? "General"}");
        if (_agentOrigin is { } origin) _agentOrigin = origin with { RoleId = role?.Id, Skills = skills };
        return task with { RoleId = role?.Id, Skills = skills, Prompt = $"{ProjectRoles.SwitchText(role, skills, record.Workspace)}\n\n{task.Prompt}" };
    }

    /// <summary>The ⋯ menu: what the last run allows (chat, undo), compacting, starting over, and the console.</summary>
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
            var used = AgentLimits.SpentWords(ShortName(runs.Key), AgentLimits.Total(runs.SelectMany(run => run.Spent)));
            return $"This task, {AgentName(runs.Key)}: {Plural(runs.Count(), "run")}{tokens}{(used.Length > 0 ? ", " + used : string.Empty)}";
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
        var (used, window) = ContextOf(record);
        return used is { } tokens ? AgentContext.Line(tokens, window, _settings.Current.Agent.ContextBudget) : null;
    }

    /// <summary>What the task's session holds and the model's window: as the run under way tells it, else as the last run left it.</summary>
    private (long? Used, long? Window) ContextOf(AgentTaskRecord record)
    {
        var live = AgentRunning && ReferenceEquals(record, _task) && _runningTask is { } running && IsUserWork(running);
        return live ? _liveContext : (record.LastRun?.ContextTokens, record.LastRun?.ContextWindow);
    }

    /// <summary>
    /// What the agent really gets, for the inspector: the open task's setup, context, rules, role, memory, state,
    /// skills, brief, tools, check and the project's stats. Before a task is open it is what the next one would get.
    /// Light enough to build at every refresh: no git, no model, the stats read once.
    /// </summary>
    internal HarnessView CurrentHarness()
    {
        var record = _task;
        var project = record is null ? _projects.ActiveProject : _taskProject ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = StudioWorkspace();

        // What the agent has now: the run under way, else the last run, else what the card would start from.
        AgentProviderKind provider;
        string? model, effort, reason;
        AgentAccess access;
        bool autoApprove;
        if (AgentRunning && _runningTask is { } running && IsUserWork(running))
        {
            (provider, model, effort, reason, access, autoApprove) = (running.Provider, running.Model, running.Effort, running.EffortReason, running.Access, running.AutoApprove);
        }
        else if (record?.LastRun is { } last)
        {
            (provider, model, effort, reason, access, autoApprove) = (last.Provider, last.Model, last.Effort, last.EffortReason, last.Access, last.AutoApprove);
        }
        else
        {
            provider = StartProvider;
            (model, effort) = settings.Level(provider, AgentLevel.Standard);
            (reason, access, autoApprove) = (null, AgentAccess.Do, settings.DefaultAccess == AgentAccessChoice.Auto);
        }

        var agent = _agents.Provider(provider);
        var (used, window) = record is null ? (null, null) : ContextOf(record);
        // The model behind a family: as the last run on it said, else as Claude Code resolved it last time.
        var ran = record?.LastRun is { } ranLast && ranLast.Provider == provider && ranLast.Model == model
            ? ranLast.RanModel
            : provider == AgentProviderKind.ClaudeCode && model is not null ? settings.LatestModels.GetValueOrDefault(model) : null;
        return HarnessView.Build(new HarnessInput(
            workspace,
            agent.Name,
            SetupWords(provider, model, effort, ranModel: ran),
            HarnessView.EffortWhy(agent.Capabilities.Effort, (record?.AutoEffort ?? true) && settings.AutoEffort, reason, effort),
            agent.Capabilities,
            settings.ContextBudget)
        {
            Task = record,
            Access = access,
            AutoApprove = autoApprove,
            ContextUsed = used,
            ContextWindow = window,
            CheckCommand = project.CheckCommand,
            ToolRules = workspace is null ? [] : AgentCoordinator.Tools(project, workspace, access),
            Stats = ProjectStats(project, workspace),
            AgentName = AgentName
        });
    }

    /// <summary>The project's stats by its saved tasks; read once, and again after a task is saved.</summary>
    private IReadOnlyList<StatsRow> ProjectStats(LinkProject project, string? workspace)
    {
        if (_stats is { } known && known.Project == project.Id) return known.Rows;
        var roles = ProjectRoles.In(workspace);
        var rows = AgentStatsReport.For(_projects.ReadAgentTasks(project), AgentName,
            id => id is null ? "General" : roles.FirstOrDefault(role => string.Equals(role.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id);
        _stats = (project.Id, rows);
        return rows;
    }

    /// <summary>The agent's pill in the card's header and the side panel's Studio button dot: who works and how the task stands.</summary>
    private void UpdateAgentPill()
    {
        if (_task is not { } task && !AgentRunning)
        {
            AgentPill.Visibility = Visibility.Collapsed;
            _panel?.ShowTaskDot(null);
            return;
        }

        var provider = _runningTask?.Provider ?? _task?.LastRun?.Provider ?? StartProvider;
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
                            "\nClick to see it in the Studio";
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
        // The team lead's review only reads: its verdict decides the step (AfterTeamStep), not a card of this.
        if (_task is { } reviewed && TeamRunner.Current(reviewed) is { Status: TeamStepStatus.Reviewing }) return false;
        // Changing files or running any command is never allowed in read only: it is a retry with edits, except for a
        // task whose file Link writes itself (a map).
        var claude = task.Provider.RunsOnClaudeCode();
        var readOnly = task.Access == AgentAccess.Analyze;
        var answersInFile = task.AnswerFile is not null;
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
        if (task.PlanOnly && result.Succeeded && _task is { Mode: TaskMode.Team } team && OfferTeam(team, run)) return true;
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
        // The task's project, which the user may have left for another meanwhile.
        var project = _taskProject ?? _projects.ActiveProject;
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
                _ = ShowChangesAsync(_task);
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

    /// <summary>What the run that just ended took of its agent's usage windows; nothing where its CLI told no numbers.</summary>
    private IReadOnlyList<UsageSpent> SpentByRun(AgentProviderKind provider) =>
        AgentLimits.Spent(_usageRun.Before, _limits.For(provider), _usageRun.Start, _usageRun.First);

    /// <summary>" · used 4% of Codex 5 h, 1% of week" for a run's stats line; empty when it took nothing worth saying.</summary>
    private string SpentLine(AgentProviderKind provider, IEnumerable<UsageSpent> spent) =>
        AgentLimits.SpentWords(ShortName(provider), AgentLimits.Total(spent)) is { Length: > 0 } words ? " · " + words : string.Empty;

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
