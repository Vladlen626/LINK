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

// Agent: CLI agents (Claude Code, Codex or the custom model) work on the user's tasks in the project's workspace, several at
// once (MainWindow.Works). The Studio lists the project's tasks, a card comes before a new one, and a task shows as one
// thread, as in the agents' own apps: the user's words, each run with its steps folded, its answer and files, notes of
// hand-overs, models and limits, the agent's question as a choice. Stop, corrections, decisions, models, hand-overs and
// undo work on the open task. The agent stays out of the chat's way: no status line of its own steps, and of the voice
// only "started" and "finished".
public partial class MainWindow
{
    // A run that ends on one of these ran out of its limit: the other agent can take the task over.
    private static readonly Regex LimitReached = new(
        @"\b(usage|rate|session|weekly|daily|5-hour|five-hour) limit\b|hit your (usage )?limit|limit reached|out of (credits|usage)|quota",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // A run that ends on one of these failed in the service, not in the work ("Failed to authenticate. API Error: 403").
    private static readonly Regex ServiceError = new(
        @"\bAPI Error\b|Failed to authenticate|overloaded_error|Internal server error|ECONNRESET|ETIMEDOUT|stream disconnected",
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
    private AgentConsoleWindow? _agentConsoleWindow;
    private AgentPanel? _agentPanel;
    // What Run on the card does: start a task, or hand the open one over with the brief on the card.
    private Func<AgentPlan, Task>? _cardRun;
    private InputSnapshot? _agentSnapshot;
    // What the shown task changed in its header ("+181 −12"), counted once when a run ends or the task opens.
    private (Guid Task, string Text, string Hint) _changes;
    // Link's own answer (the map), apart from the tasks: the Studio shows it on its own, with Stop.
    private OwnRun? _own;
    // Whether the Studio shows Link's own answer rather than a task or the list.
    private bool _ownShown;
    // Agents whose usage is being asked for now.
    private readonly HashSet<AgentProviderKind> _askingLimits = [];
    // Draws what the runs under way told, ten times a second at most, while any runs.
    private DispatcherTimer? _activityTimer;
    // "Always for this project" on the decision being taken, read by what it does.
    private bool _rememberAllowance;
    // The specialist's name in the header, which is drawn at every tick of a run: its file is read once, not at each.
    private (string? Id, string? Workspace, string? Name) _roleName;
    // The project's stats for the inspector, which reads them again only after a task is saved.
    private (Guid Project, IReadOnlyList<StatsRow> Rows, IReadOnlyList<string> TeamVsSolo)? _stats;

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

    /// <summary>
    /// Whether the open task takes the next words: it is this project's and waits for no folder, and its agent works on
    /// it or its session can go on.
    /// </summary>
    private bool TaskTakesWords => _open is { Record.Status: not AgentTaskStatus.Finished, Waiting: null } work &&
                                   work.Project.Id == _projects.ActiveProject.Id &&
                                   (work.Busy ? work.Run is not null && !work.RestartWhenStopped : ResumableTask(work) is not null);

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
        if (_open is { } work) ContinueAgent(work, words, images);
    }

    /// <summary>New task in the Studio: a card with what is in Link's input and chips, maybe nothing yet; it runs beside the tasks at work.</summary>
    private async Task OpenNewTaskCardAsync()
    {
        RemoveMissingContextFiles();
        var snapshot = await TakeSnapshotAsync(_projects.ActiveProject, PromptBox.Text, RequestRoute.Agent, AutoFrameOn);
        _attempts.Keep(snapshot);
        OpenAgentCard(snapshot);
    }

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

        var (provider, model, effort) = OwnAnswerSetup(StartProvider, inSession: false);
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
        // The task runs beside the others at once; only its edits wait for another task's in the same folder.
        if (_projects.ActiveProject.WorkspaceRoot is { Length: > 0 } folder && FolderBusy(null, folder) is { } busy)
        {
            var waits = $"{busy}: this task's edits wait for that run and start by itself, reading and planning start at once.";
            notice = notice is null ? waits : $"{waits} {notice}";
        }

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
        // Stop is for what the Studio shows: Link's own answer, else the open task's run, Link's step for it or the run
        // that waits for its folder.
        panel.StopRequested += () =>
        {
            if (_ownShown) _own?.Live?.Stop.Cancel();
            else if (_open is { Waiting: not null } waiting) StopWaiting(waiting);
            else (_open?.Run?.Stop ?? _open?.Asking?.Stop)?.Cancel();
        };
        panel.DecisionTaken += always =>
        {
            _rememberAllowance = always;
            // A decision is taken once: one that starts no run (a share, the team's last OK) leaves the panel too.
            var decide = _open?.Decision;
            if (_open is { } work) work.Decision = null;
            panel.HideDecision();
            decide?.Take();
        };
        panel.RatingChosen += RateExam;
        panel.RestartRequested += RequestRestart;
        panel.EscalateRequested += Escalate;
        panel.ContinueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: false);
        panel.QueueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: true);
        panel.FinishRequested += FinishTask;
        panel.CheaperRequested += () =>
        {
            if (_open is { } work) _ = ShowHandOffCardAsync(work, AgentProviderKind.Custom);
        };
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
        else if (_shown is { } work) ShowTask(work);
        else ShowTaskList();
    }

    /// <summary>No task is shown: the sidebar lists the project's, and the agents at work go on, their rows say so.</summary>
    private void ShowTaskList()
    {
        _shown = null;
        _ownShown = false;
        RefreshTaskList();
        _agentPanel?.ShowListView();
    }

    private void RefreshTaskList() => _agentPanel?.ShowList(TaskRows(), AllLimitRows(), OwnRow());

    /// <summary>Link's own run at the top of the list while it works, and after it until the next one: one click shows it.</summary>
    private TaskRow? OwnRow()
    {
        if (_own is not { } own) return null;
        var running = own.Live is not null;
        var status = running ? "working" : own.Status;
        return new TaskRow($"Link writes {OwnWhat(own.Task)}", "Link's own answer, apart from the tasks",
            string.Empty, $"{AgentName(own.Task.Provider)} · {status}", (Brush)FindResource(running ? "BusyOrbBrush" : own.Dot), RoleBrushes.Link, null);
    }

    /// <summary>
    /// The project's tasks, newest first, those Link holds as they stand in memory, saved yet or not; the open one
    /// leads.
    /// </summary>
    private IReadOnlyList<TaskRow> TaskRows()
    {
        var project = _projects.ActiveProject;
        var tasks = _projects.ReadAgentTasks(project).Select(task => WorkOf(task.Id)?.Record ?? task).ToList();
        foreach (var work in _works.Where(work => work.Project.Id == project.Id && !tasks.Contains(work.Record))) tasks.Insert(0, work.Record);
        if (_open is { } open && open.Project.Id == project.Id)
        {
            tasks.Remove(open.Record);
            tasks.Insert(0, open.Record);
        }

        // Each workspace's role files are read once per drawing, and only for a task that has a specialist.
        var roles = new Dictionary<string, Lazy<IReadOnlyList<AgentRole>>>(StringComparer.OrdinalIgnoreCase);
        return tasks.Select(record =>
        {
            // The list is drawn now and then: a running row says so, the time is in the pill and the task.
            var work = WorkOf(record.Id);
            var (status, brush) = work?.Run is not null ? ("working", (Brush)FindResource("BusyOrbBrush")) : Standing(record);
            var provider = work?.Run?.Task.Provider ?? record.LastRun?.Provider;
            var who = provider is { } agent ? AgentName(agent) + " · " : string.Empty;
            if (!roles.TryGetValue(record.Workspace, out var inWorkspace)) roles[record.Workspace] = inWorkspace = WorkspaceRoles(record.Workspace);
            return new TaskRow(record.Goal, ToPreviewText(record.LastWords), ShortTime(record.UpdatedAt), who + status, brush,
                RoleBrushes.For(record.RoleId, inWorkspace), record);
        }).ToArray();
    }

    /// <summary>How a task stands, in words and the colour of its dot: at work and for how long, waiting for its folder, or as its record says.</summary>
    private (string Text, Brush Dot) Standing(AgentTaskRecord record)
    {
        var work = WorkOf(record.Id);
        var elapsed = work?.Run?.Clock.Elapsed ?? TimeSpan.Zero;
        var (text, brush) = work?.Run is not null
            ? ($"working · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", "BusyOrbBrush")
            : work?.Waiting is not null ? ("waits for its folder", "IdleOrbBrush")
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
    /// Run on the card: a new task, which becomes the open one. The task that was open goes on as it stood, at work or
    /// waiting for the user, in the list; its run is never stopped or finished for the new one.
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
        await StartNewTaskAsync(plan, snapshot, project);
    }

    private async Task StartNewTaskAsync(AgentPlan plan, InputSnapshot snapshot, LinkProject project)
    {
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        var agent = _settings.Current.Agent;
        // The agent works in English; the user sees what it was given in the task's brief.
        var written = plan.Goal;
        plan = plan with { Goal = await InEnglishAsync(plan.Goal, project) };
        // Measuring team against solo: by turns for each kind of work, the strong agent alone takes a team's task.
        var measured = plan.Team && agent.Team.Alternate;
        if (measured && !TeamVsSolo.TeamsTurn(_projects.ReadAgentTasks(project), StepKinds.Of(written.Split('\n')[0])))
        {
            plan = plan with { Team = false, Provider = WorkerRouting.Workers(agent, WorkerStateOf)[WorkerTier.Strong]?.Provider ?? plan.Provider };
        }

        // A team is planned by its lead, the Strong worker on its Deep level (OfferTeam routes the plan).
        var lead = plan.Team ? WorkerRouting.Workers(agent, WorkerStateOf)[WorkerTier.Strong] : null;
        if (lead is not null) plan = plan with { Provider = lead.Provider, PlanFirst = true, RoleId = null, Skills = [] };
        // A training studies straight away, with the card's access, as its exams do.
        else if (plan.Train) plan = plan with { PlanFirst = false };
        // A plan is made on the strongest model and carried out with the card's, in the same session.
        var planSetup = (plan.Model, plan.Effort);
        var (model, effort) = lead is not null ? agent.Level(lead.Provider, lead.Level)
            : plan.PlanFirst && agent.PlanOnDeep ? agent.Level(plan.Provider, AgentLevel.Deep) : planSetup;
        // What was pasted or dropped on the card: images go as images, other files by their paths.
        var (goal, attachedImages, attachedFiles) = WithAttached(plan.Goal, plan.Attached);
        var origin = AgentCoordinator.BuildTask(plan with { Model = model, Effort = effort, Goal = goal }, snapshot, project);
        origin = origin with { Images = [.. origin.Images, .. attachedImages] };
        var brief = origin.Prompt;
        if (lead is not null)
        {
            var request = File.Exists(TeamPlan.TaskFile) ? await File.ReadAllTextAsync(TeamPlan.TaskFile) : string.Empty;
            var evidence = _projects.ReadWorkerEvidence(project).Summary(ShortName);
            origin = origin with { Prompt = TeamPlan.Prompt(brief, request, evidence, ProjectRoles.In(plan.Workspace)) };
        }

        // A training studies first: its lessons wait for an exam (see MainWindow.Training).
        TrainingCourse? course = null;
        if (plan.Train && lead is null) (origin, course) = await TrainingStartAsync(origin, plan, project, attachedFiles);
        var record = new AgentTaskRecord
        {
            Goal = Shorten(written.Split('\n')[0].Trim(), 200),
            Brief = brief,
            Workspace = plan.Workspace,
            AutoEffort = plan.AutoEffort,
            RoleId = plan.RoleId,
            Skills = [.. plan.Skills],
            Measured = measured,
            Mode = lead is not null ? TaskMode.Team : course is not null ? TaskMode.Training : TaskMode.Solo,
            Training = course,
            Images = [.. origin.Images]
        };
        var how = lead is not null ? $"\n(team: {WorkerLabel(lead)} plans the steps)"
            : course is not null ? "\n(training: study first, then an exam)"
            : plan.PlanFirst ? "\n(plan first)" : string.Empty;
        if (measured && lead is null) how += "\n(team vs solo: alone this time, to compare)";
        if (!ReferenceEquals(written, plan.Goal)) how += "\n(translated into English by ChatGPT)";
        record.Add(AgentEntryKind.Brief, goal.Trim() + ImagesNote(origin.Images) + how);
        var work = new TaskWork(record, project) { BackupBeforeDo = plan.BackupBeforeDo, PlanSetup = planSetup, Origin = origin };
        work.AttachedFiles.AddRange(attachedFiles);
        _works.Add(work);
        OpenWork(work);
        // The lead's effort is its Deep level's: planning the whole team is no task to save on.
        await StartAgentAsync(work, origin, null, phase: lead is null ? EffortPhase.New : null);
    }

    private static string ImagesNote(IReadOnlyCollection<string> images) =>
        images.Count == 0 ? string.Empty : $"\n+ {Plural(images.Count, "image")}";

    /// <summary>
    /// The user's words for a task. While its agent works they correct it: it stops the step it is on and the same
    /// session goes on with them. After a run they are a reply in the same session, maybe the answer to its question; a
    /// task from the list is taken up again in its folder, with a copy before edits where there is no version control.
    /// </summary>
    private void ContinueAgent(TaskWork work, string reply, IReadOnlyList<string>? images = null, bool afterLimit = false)
    {
        if (reply.Trim() is not { Length: > 0 } text) return;
        images ??= [];
        // A run that waits for its folder takes no words: Stop calls it off first.
        if (work.Waiting is not null) return;
        if (work.Busy)
        {
            // Link's own step (a note for Start over) takes no words.
            if (work.Run is not { } live || work.RestartWhenStopped) return;
            work.SteerWith = text;
            work.SteerImages = images;
            work.Record.Add(AgentEntryKind.You, "(steer now) " + text + ImagesNote(images));
            live.Stop.Cancel();
            ShowTask(work);
            AgentStatus(work, $"Steering {ShortName(live.Task.Provider)} now: it stops the step and goes on with your words…");
            return;
        }

        // A training ready for its exam: the words set it, in a new session.
        if (work.Record.Training is { Stage: TrainingStage.AwaitsExam } course)
        {
            _ = StartExamAsync(work, course, text, images);
            return;
        }

        var phase = work.PlanLeft is not null && ReferenceEquals(work.LastRun, work.PlanLeft) ? EffortPhase.RunPlan : EffortPhase.Reply;
        // After the wait for a limit, or a long idle, the session's cache is cold: a big one is re-read at full price, and
        // re-sent at each step. Where Settings allow it, a fresh session from the task's state takes the words instead
        // (never a plan's own run).
        if (phase != EffortPhase.RunPlan && work.LastRun is not { Task.PlanOnly: true } && WhyCompact(work, afterLimit) is { } why)
        {
            CompactAndContinue(work, text, images, why, phase);
            return;
        }

        work.Record.Add(AgentEntryKind.You, text + ImagesNote(images));
        if (work.EarlierSession is { } earlier)
        {
            work.Origin = earlier with { ResumeSessionId = null };
            work.BackupBeforeDo = true;
            _ = StartAgentAsync(work, earlier with { Prompt = WithCriticNotes(work, text, earlier.ResumeSessionId), Images = images }, null, phase: phase);
            return;
        }

        if (work.LastRun is { } run && WordsAfter(work, run, WithCriticNotes(work, text, AgentCoordinator.SessionOf(run))) is { } task)
        {
            _ = StartAgentAsync(work, task with { Images = images }, run, phase: phase);
        }
    }

    /// <summary>
    /// The run that takes the user's words after <paramref name="ended"/>: its session goes on with them, and a new
    /// session stopped before it began starts the task again with them. Null when there is neither.
    /// </summary>
    private static AgentTask? WordsAfter(TaskWork work, AgentRun ended, string words) =>
        AgentCoordinator.ContinueTask(ended, words)
        ?? (work.Origin is { } origin ? origin with { Prompt = $"{origin.Prompt}\n\nThe user adds:\n{words}" } : null);

    /// <summary>Whether the task takes words now: a session to go on with, or a first run stopped before its session began.</summary>
    private static bool TakesWords(TaskWork work) =>
        ResumableTask(work) is not null || (work.LastRun is { } run && WordsAfter(work, run, "-") is not null);

    /// <summary>
    /// Why words that would resume the task's last session should start a fresh one instead (see <see cref="CompactRule"/>);
    /// null: resume as it is, or Settings do not let Link compact by itself.
    /// </summary>
    private string? WhyCompact(TaskWork work, bool afterLimit) =>
        ResumableTask(work) is not null
            ? CompactRule.Why(work.Record.LastRun, _settings.Current.Agent.AutoCompact, _settings.Current.Agent.ContextBudget, DateTimeOffset.Now, afterLimit)
            : null;

    /// <summary>
    /// Compact by itself: a fresh session of the same agent takes the words from the task's state, as Compact does, but
    /// without the card to look at first. The history says why. The state has no git lines per file: asking git would
    /// leave a gap in which another reply could start a second session.
    /// </summary>
    private void CompactAndContinue(TaskWork work, string words, IReadOnlyList<string> images, string why, EffortPhase phase)
    {
        if ((ResumableTask(work)?.Task ?? work.Origin) is not { } template) return;
        var record = work.Record;
        var state = TaskState.CompactContinue(record, null, record.Workspace, words, AgentName);
        var task = AgentCoordinator.CompactTask(template, record, state, images) with
        {
            AllowedTools = AgentCoordinator.Tools(work.Project, template.Workspace)
        };
        record.Add(AgentEntryKind.You, words + ImagesNote(images));
        _ = StartCompactedAsync(work, task, why, phase);
    }

    /// <summary>A fresh session of the same agent starts from the task's state: the history says so, and Start over begins from it too.</summary>
    private Task StartCompactedAsync(TaskWork work, AgentTask task, string? why, EffortPhase phase)
    {
        work.Record.Add(AgentEntryKind.HandOff, TaskState.Compacted(work.Record.LastRun?.ContextTokens) + (why is null ? string.Empty : " · " + why));
        work.Origin = task;
        if (work.LastRun is null) work.BackupBeforeDo = true;
        return StartAgentAsync(work, task, work.LastRun, phase: phase);
    }

    /// <summary>
    /// The user's words with what they pasted or dropped: images go as images, other files are named by path at the end
    /// of the words, and the next run may read their folders.
    /// </summary>
    private static (string Words, IReadOnlyList<string> Images, IReadOnlyList<string> Files) WithAttached(string words, IReadOnlyList<string> attached)
    {
        var images = attached.Where(AgentPanel.IsImage).ToArray();
        var files = attached.Except(images).ToArray();
        if (files.Length > 0) words = $"{words.Trim()}\n\nAttached files:\n{string.Join('\n', files.Select(file => "- " + file))}";
        return (words.Trim(), images, files);
    }

    /// <summary>A reply from the Studio for the open task, with what came along with it.</summary>
    private void ContinueWithAttached(string words, IReadOnlyList<string> attached, bool queue)
    {
        if (_open is not { } work) return;
        var (text, images, files) = WithAttached(words, attached);
        work.AttachedFiles.AddRange(files);
        if (queue) QueueAfterRun(work, text, images);
        else ContinueAgent(work, text, images);
    }

    /// <summary>Tab while the agent works: the words wait for the end of the run, then the same session goes on with them.</summary>
    private void QueueAfterRun(TaskWork work, string words, IReadOnlyList<string> images)
    {
        if (work.Run is null) return;
        work.QueuedImages.AddRange(images);
        work.Queued = work.Queued is null ? words : $"{work.Queued}\n{words}";
        work.Record.Add(AgentEntryKind.You, "(after this step) " + words);
        ShowTask(work);
        AgentStatus(work, "Your words wait for the end of this run");
    }

    /// <summary>
    /// Plan mode off: the session's next words go on as work, on the model a plan is carried out with; in a plan they are
    /// asked for a plan again.
    /// </summary>
    private void LeavePlanMode()
    {
        if (_open is not { Busy: false, Waiting: null, LastRun: { Task.PlanOnly: true } run } work) return;
        var (model, effort) = _settings.Current.Agent.PlanOnDeep ? work.PlanSetup : (run.Task.Model, run.Task.Effort);
        work.LastRun = work.PlanLeft = run with { Task = run.Task with { PlanOnly = false, Model = model, Effort = effort } };
        work.Record.Add(AgentEntryKind.You, "Plan mode off");
        SaveTask(work);
        ShowTask(work);
        AgentStatus("Plan mode is off: your next words go on with edits in the same session");
    }

    /// <summary>
    /// Finish: the open task is closed and stays in the list, where it can be opened and taken up again; a run that
    /// waited for its folder is called off with it.
    /// </summary>
    private void FinishTask()
    {
        if (_open is not { Busy: false } work) return;
        var task = work.Record;
        work.Waiting = null;
        CancelContinue(work);
        if (task.Status != AgentTaskStatus.Finished)
        {
            task.Status = AgentTaskStatus.Finished;
            task.Add(AgentEntryKind.Finished, "Finished");
            SaveTask(work);
        }

        _works.Remove(work);
        _open = null;
        _shown = null;
        UpdateAgentPill();
        UpdateRouteButton();
        ShowTaskList();
        RefreshStudio();
        AgentStatus("Task finished: it stays in the list of tasks");
    }

    /// <summary>
    /// A task from the list becomes the open one; the tasks at work go on beside it. One Link holds shows as it stands, at
    /// work or not; another is taken up, and the next words take up its last session.
    /// </summary>
    private void OpenAgentTask(AgentTaskRecord record)
    {
        if (WorkOf(record.Id) is { } held)
        {
            OpenWork(held);
            ShowTaskInPanel();
            _ = ShowChangesAsync(held.Record);
            UpdateRouteButton();
            return;
        }

        if (!Directory.Exists(record.Workspace))
        {
            SetError($"The task's folder is gone: {record.Workspace}");
            return;
        }

        var work = TakeUpTask(record, _projects.ActiveProject);
        OpenWork(work);
        ShowTaskInPanel();
        // A team left mid-way goes on from the step it is on; a question it ended on is answered first.
        if (record.Team.Count > 0 && work.Question is null) OfferTeamGoOn(work);
        // A study that drew its lessons, or a training between exams, still offers the exam.
        if (record.Training is { Stage: TrainingStage.Learning or TrainingStage.BetweenExams, Lessons.Count: > 0 } course && work.Question is null)
        {
            OfferExam(work, course);
        }
        _ = ShowChangesAsync(record);
        UpdateRouteButton();
        AgentStatus(work.EarlierSession is null
            ? "This task has no session to go on with"
            : $"Your words go on with the task: in its thread, or with Enter on the Agent route");
    }

    /// <summary>
    /// Link takes a task of <paramref name="project"/> up again from its record: its last session is what the next words
    /// take up, and a wait for a limit's reset kept with it goes on (Link was restarted meanwhile).
    /// </summary>
    private TaskWork TakeUpTask(AgentTaskRecord record, LinkProject project)
    {
        var work = new TaskWork(record, project) { AfterRun = [ConsoleAction()] };
        // In a team or a training the art critic only judged: words go on with the work it judged.
        var last = (record.Team.Count > 0 || record.Training is not null ? record.Runs.LastOrDefault(run => !ArtCritic.Judged(run)) : null) ?? record.LastRun;
        // A question the task ended on is still a question.
        work.Question = record.Status == AgentTaskStatus.NeedsYou && last is not null ? AgentQuestion.Split(last.Words).Question : null;
        work.EarlierSession = last?.SessionId is { } session
            ? new AgentTask
            {
                Provider = last.Provider,
                Workspace = record.Workspace,
                Prompt = record.Goal,
                AutoApprove = last.AutoApprove,
                Model = last.Model,
                Effort = last.Effort,
                AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
                CheckCommand = project.CheckCommand,
                RoleId = record.RoleId,
                Skills = record.Skills,
                ResumeSessionId = session
            }
            : null;
        work.Steps.AddRange(record.Steps);
        _works.Add(work);
        ArmContinue(work);
        return work;
    }

    /// <summary>The open task in the Studio.</summary>
    private void ShowTaskInPanel()
    {
        ShowAgentPanel();
        ShowOpenTask();
    }

    private void ShowOpenTask()
    {
        if (_open is not { } work) return;
        OpenWork(work);
        ShowTask(work);
    }

    /// <summary>
    /// Runs a task's agent, fresh or continuing <paramref name="previous"/>, and shows it in the task's thread;
    /// <paramref name="note"/> opens its steps, such as the note a new session starts over with. <paramref name="phase"/>
    /// is what the run is for: a task on Auto gets its effort from it; without one the task's own effort stands. A run
    /// that would edit where another run edits now waits for it (<see cref="WaitForFolder"/>).
    /// </summary>
    private async Task StartAgentAsync(TaskWork work, AgentTask task, AgentRun? previous, string? note = null, EffortPhase? phase = null)
    {
        // One run at a time for a task: words that come while it works steer it or wait for its end.
        if (work.Busy || !_works.Contains(work)) return;
        if (FolderBusy(work, task.Workspace) is { } why)
        {
            WaitForFolder(work, why, () => StartAgentAsync(work, task, previous, note, phase));
            return;
        }

        work.Waiting = null;
        var record = work.Record;
        // Every run starts here: the CLI compacts its session near the budget of Settings.
        task = task with { ContextBudget = _settings.Current.Agent.ContextBudget, EffortReason = null };
        if (work.AttachedFiles.Count > 0)
        {
            task = task with { Files = [.. task.Files.Union(work.AttachedFiles, StringComparer.OrdinalIgnoreCase)] };
            work.AttachedFiles.Clear();
        }

        // The user's words take the setup chosen for them in the composer; Auto, when it stays on, then picks the effort.
        if (phase is EffortPhase.Reply or EffortPhase.Steer or EffortPhase.RunPlan) task = WithNextSetup(work, task);
        if (phase is { } kind) task = WithAutoEffort(work, task, kind);
        var provider = _agents.Provider(task.Provider);
        // A new task starts clean; a run that goes on with the last one, or a task taken up again, keeps its past. The
        // console is every agent's: it is cleared only while no other one writes to it.
        if (previous is null && record.Runs.Count == 0)
        {
            if (!AnyRunning) _agents.Console.Clear();
            if (task.ResumeSessionId is null) work.Steps.Clear();
        }

        work.Decision = null;
        work.Question = null;
        work.EarlierSession = null;
        work.Limit = null;
        work.Restart = default;
        CancelContinue(work);
        record.Status = AgentTaskStatus.Running;
        // A session that goes on keeps its tasks; the parser follows the agent's TaskUpdate by their numbers.
        if (task.ResumeSessionId is not null && record.LastRun?.Provider == task.Provider) task = task with { Plan = record.Plan };
        var turn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason)}", "Starting…",
            (Brush)FindResource("AccentBrush"), RoleBrushes.For(task.RoleId, WorkspaceRoles(task.Workspace))) { StepsOpen = true };
        var live = new LiveRun(task, turn, _limits.For(task.Provider));
        // A session taken up again holds what its last run left, and the model's window stays until a run says otherwise.
        if (record.LastRun is { } last && last.Provider == task.Provider)
        {
            live.Context = (last.SessionId == task.ResumeSessionId ? last.ContextTokens : null, last.ContextWindow);
        }

        if (note is not null) live.Pending.Add(new AgentEvent(AgentEventKind.Output, note));
        // Another run edited the folder since the last one: what differs from that run's start is not all this task's.
        var since = work.OthersEdited ? null : previous;
        work.OthersEdited = false;
        // The run holds its folder from here on, before anything is awaited.
        work.Run = live;
        _agentPanel ??= CreateAgentPanel();
        // The open task's run shows at once; another's is drawn when it is opened.
        if (work == _open)
        {
            OpenWork(work);
            ShowTask(work);
        }

        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{ShortName(task.Provider)} начал работу.", $"{ShortName(task.Provider)} started.");
        AgentStatus(work, $"{provider.Name} is working · its thread is in the Studio");
        UpdateAgentPill();
        UpdateRouteButton();
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
            var run = await _agents.RunAsync(task, work.BackupBeforeDo, since, progress, live.Stop.Token);
            if (run.Before is { IsVersioned: false, Backup: null } && work.BackupBeforeDo && since is null)
            {
                live.Pending.Add(new AgentEvent(AgentEventKind.Error, "No copy was made: the workspace is over 2 GB."));
            }

            await KeepInChangelistAsync(work, run);
            ended = run;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            FlushAgentEvents();
            work.Run = null;
            live.Clock.Stop();
            live.Stop.Dispose();
            if (!AnyRunning) _activityTimer.Stop();
            // The other tasks in the folder find it as this run left it.
            EditedBy(work, task.Workspace);
            ReportState();
        }

        if (ended is not null) ShowAgentRun(work, live, ended);
        else if (failure is not null) ShowAgentFailure(work, provider, failure);
        UpdateAgentPill();
        UpdateRouteButton();
        RefreshTaskList();
        RefreshStudio();

        // What the user left for after the run goes first and holds the folder; the runs that waited for it start after.
        if (work.RestartWhenStopped)
        {
            work.RestartWhenStopped = false;
            _ = RestartWithNoteAsync(work);
        }
        else if (work.HandOffWhenStopped is { } to)
        {
            work.HandOffWhenStopped = null;
            _ = ShowHandOffCardAsync(work, to);
        }
        else if (work.SteerWith is { } words)
        {
            work.SteerWith = null;
            var images = work.SteerImages;
            work.SteerImages = [];
            // Words while the art critic judged stop it and go on with the work it judged (AfterCritique), which was not interrupted.
            var critic = ended is not null && ArtCritic.Judges(ended.Task);
            var after = critic ? work.LastRun : ended;
            if (after is not null && WordsAfter(work, after,
                    critic ? WithCriticNotes(work, words, AgentCoordinator.SessionOf(after)) : AgentCoordinator.Interrupted(words)) is { } next)
            {
                _ = StartAgentAsync(work, next with { Images = images }, after, phase: critic ? EffortPhase.Reply : EffortPhase.Steer);
            }
            else
            {
                SetError("The agent stopped before its session began: give your words as a new task.");
            }
        }
        else if (work.Queued is { } later)
        {
            work.Queued = null;
            IReadOnlyList<string> images = [.. work.QueuedImages];
            work.QueuedImages.Clear();
            // After the art critic's verdict the words go on with the work it judged (AfterCritique), after its question with it.
            var after = ended is not null && ArtCritic.Judges(ended.Task) ? work.LastRun : ended;
            if (after is not null && WordsAfter(work, after, WithCriticNotes(work, later, AgentCoordinator.SessionOf(after))) is { } next)
            {
                _ = StartAgentAsync(work, next with { Images = images }, after, phase: EffortPhase.Reply);
            }
        }

        StartWaitingRuns();
    }

    /// <summary>
    /// The run's task with the effort <see cref="AutoEffort"/> chooses for <paramref name="phase"/> and its reason, when
    /// the task is on Auto: from the task's brief and runs and the next step the last one ended with. A task whose effort
    /// the user chose and a plan made on Strong stay as they are. The model is never touched.
    /// </summary>
    private AgentTask WithAutoEffort(TaskWork work, AgentTask task, EffortPhase phase)
    {
        var agent = _settings.Current.Agent;
        var record = work.Record;
        if (!agent.AutoEffort || !record.AutoEffort || (task.PlanOnly && agent.PlanOnDeep)) return task;
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

    /// <summary>
    /// Link's own answer (the map): a run of its own beside the tasks at work, which it leaves as they are. Its agent only
    /// reads; the file Link writes is an edit of the folder, so the tasks' edits there wait for it.
    /// </summary>
    private async Task StartOwnAsync(AgentTask task)
    {
        task = task with { ContextBudget = _settings.Current.Agent.ContextBudget, EffortReason = null };
        var provider = _agents.Provider(task.Provider);
        var turn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort)}", "Starting…",
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
            ended = await _agents.RunAsync(task, backup: false, null, progress, live.Stop.Token);
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
        panel.ShowPlanMode(false);
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

    /// <summary>Start over with a note on the open task; its running agent is stopped first, and its note is asked once it has ended.</summary>
    private void RequestRestart()
    {
        if (_open is not { Waiting: null, Asking: null } work) return;
        if (work.Run is not { } live)
        {
            _ = RestartWithNoteAsync(work);
            return;
        }

        work.RestartWhenStopped = true;
        live.Stop.Cancel();
    }

    /// <summary>
    /// The last run's session writes a short note on what it tried and why it did not work (Stop ends the asking and
    /// the new start); then a new session takes the original task with that note, the same access and folder, and undo
    /// still covers both.
    /// </summary>
    private async Task RestartWithNoteAsync(TaskWork work)
    {
        if (work.LastRun is not { } run || work.Origin is not { } origin || work.Busy || work.Waiting is not null) return;
        var (asker, model, effort) = OwnAnswerSetup(run.Task.Provider, inSession: true);
        var provider = _agents.Provider(asker);
        var step = $"asking {provider.Name} for a note…";
        var cancellation = new CancellationTokenSource();
        work.Asking = (cancellation, step);
        // What the last run left to choose is over: a new session follows the note.
        work.Decision = null;
        work.Limit = null;
        work.Restart = default;
        if (Shows(work)) _agentPanel!.ShowStep(step, running: true);
        UpdateAgentPill();
        AgentStatus(work, $"{provider.Name} writes a note for a new session · Stop is in the Studio");
        (AgentTask Task, string Note)? next = null;
        string? error = null;
        try
        {
            next = await _agents.StartOverAsync(run, origin, (model, effort), cancellation.Token, asker);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
        }
        finally
        {
            work.Asking = null;
            cancellation.Dispose();
            ReportState();
        }

        if (next is not { } fresh)
        {
            ShowTask(work);
            UpdateAgentPill();
            if (error is not null) SetError(error);
            else AgentStatus(work, "Start over stopped: the last run stays as it was");
            return;
        }

        work.Record.Add(AgentEntryKind.You, "Start over with a note" + (fresh.Note.Length > 0 ? ": " + fresh.Note : string.Empty));
        await StartAgentAsync(work, fresh.Task, run,
            fresh.Note.Length > 0 ? "Note from the last attempt:\n" + fresh.Note : "No note came back: the new session starts with the task alone.",
            phase: EffortPhase.Restart);
    }

    /// <summary>
    /// Escalate: the same session goes on with the strongest model and effort of Settings, for a problem the task's model
    /// did not solve. One cache miss: the model reads the session anew. Auto goes on after it.
    /// </summary>
    private void Escalate()
    {
        if (_open is not { Busy: false, Waiting: null } work || ResumableTask(work) is not { } resume) return;
        var (model, effort) = _settings.Current.Agent.Level(resume.Task.Provider, AgentLevel.Deep);
        work.Record.Add(AgentEntryKind.Model, $"{AgentName(resume.Task.Provider)} · {SetupWords(resume.Task.Provider, model, effort)}, escalated");
        if (resume.Previous is null) work.BackupBeforeDo = true;
        _ = StartAgentAsync(work, resume.Task with
        {
            Model = model,
            Effort = effort,
            Prompt = "The work is not done yet. Think it through again from what you know, find the cause and finish the task.",
            Images = []
        }, resume.Previous);
    }

    /// <summary>The task's last session as a run to continue, and the run it continues; null when there is none.</summary>
    private static (AgentTask Task, AgentRun? Previous)? ResumableTask(TaskWork work) =>
        work.LastRun is { } run && AgentCoordinator.SessionOf(run) is { } session ? (run.Task with { ResumeSessionId = session, PlanOnly = false }, run)
        : work.EarlierSession is { } earlier ? (earlier, null)
        : null;

    /// <summary>
    /// Hand off: the other agent takes the task over in a new session of its own. The brief is made from the task's
    /// record and shown on the card first, where it can be changed, with the model and access of the new run; the task
    /// is the open one under it.
    /// </summary>
    private async Task ShowHandOffCardAsync(TaskWork work, AgentProviderKind to)
    {
        if (work.Busy || work.Waiting is not null || ResumableTask(work) is null && work.Origin is null) return;
        var record = work.Record;
        var fromProvider = record.LastRun?.Provider ?? Other(to);
        var brief = AgentCoordinator.HandOffBrief(record, AgentName(fromProvider), await NumstatAsync(work));
        // The images the task began with go along; a task from the list kept them on its record.
        var images = work.Origin?.Images ?? [.. record.Images.Where(File.Exists)];
        AgentCardModel Build() => CardModel(brief, string.Empty, images, to, work.Project) with
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
        _cardRun = plan => HandOffAsync(work, plan, fromProvider);
        OpenWork(work);
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus($"The brief for {AgentName(to)} is on the card: change it if you like, then Hand off");
    }

    private async Task HandOffAsync(TaskWork work, AgentPlan plan, AgentProviderKind fromProvider)
    {
        if (work.Busy || !_works.Contains(work)) return;
        _cardRun = null;
        var record = work.Record;
        var template = ResumableTask(work)?.Task ?? work.Origin;
        if (template is null) return;
        var images = work.Origin?.Images ?? [];
        var task = AgentCoordinator.HandOffTask(template, plan.Provider, plan.Goal, images) with
        {
            Model = plan.Model,
            Effort = plan.Effort,
            AutoApprove = plan.AutoApprove,
            AllowedTools = AgentCoordinator.Tools(work.Project, template.Workspace),
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
        var setup = WithAutoEffort(work, task, EffortPhase.HandOff);
        record.Add(AgentEntryKind.HandOff,
            $"{AgentName(fromProvider)} → {AgentName(plan.Provider)} · {SetupWords(setup.Provider, setup.Model, setup.Effort)}, a new session{specialist}");
        record.Handovers.Add($"── Handed over to {AgentName(plan.Provider)} ──\n{plan.Goal}");
        // Start over begins the new agent's brief again.
        work.Origin = task;
        if (work.LastRun is null) work.BackupBeforeDo = true;
        await StartAgentAsync(work, task, work.LastRun, phase: EffortPhase.HandOff);
    }

    /// <summary>
    /// The lines added and removed per file by the task's work, where git or Perforce tells them; null otherwise, and
    /// the task's state then lists the files of its record.
    /// </summary>
    private async Task<string?> NumstatAsync(TaskWork work) =>
        work.LastRun is { } run ? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, 4000, CancellationToken.None) : null;

    /// <summary>
    /// Compact: a fresh session of the same agent starts from the task's state rather than from the session that has
    /// grown. Link makes the state from the task's record, and it is shown on the card first, where it can be changed,
    /// with the model and access of the last run. The task's history stays whole.
    /// </summary>
    private async Task CompactTask(TaskWork work)
    {
        if (work.Busy || work.Waiting is not null || ResumableTask(work) is not { } resume) return;
        var record = work.Record;
        var provider = resume.Task.Provider;
        var state = TaskState.CompactBrief(record, await NumstatAsync(work), record.Workspace, AgentName);
        // The images the task began with go along; a task from the list kept them on its record.
        var images = work.Origin?.Images ?? [.. record.Images.Where(File.Exists)];
        AgentCardModel Build()
        {
            var plain = CardModel(state, string.Empty, images, provider, work.Project);
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
        _cardRun = plan => CompactAsync(work, plan);
        OpenWork(work);
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        AgentStatus($"A fresh session of {AgentName(provider)} starts from the state on the card: change it if you like, then Compact");
    }

    private async Task CompactAsync(TaskWork work, AgentPlan plan)
    {
        if (work.Busy || !_works.Contains(work)) return;
        _cardRun = null;
        var record = work.Record;
        var template = ResumableTask(work)?.Task ?? work.Origin;
        if (template is null) return;
        // The card may have changed the specialist, the skills and Auto: the task keeps them.
        record.AutoEffort = plan.AutoEffort;
        record.RoleId = plan.RoleId;
        record.Skills = [.. plan.Skills];
        var task = AgentCoordinator.CompactTask(template, record, plan.Goal, work.Origin?.Images ?? []) with
        {
            Model = plan.Model,
            Effort = plan.Effort,
            AutoApprove = plan.AutoApprove,
            AllowedTools = AgentCoordinator.Tools(work.Project, template.Workspace)
        };
        // Start over begins from the state, not from the session that has grown.
        await StartCompactedAsync(work, task, null, EffortPhase.Reply);
    }

    /// <summary>
    /// Return to the first agent, such as once its limit has reset: its own session goes on, told what the other did,
    /// rather than a new one learning the task anew.
    /// </summary>
    private async Task ReturnToAsync(TaskWork work, AgentProviderKind provider)
    {
        if (work.Busy || work.Waiting is not null) return;
        var record = work.Record;
        var back = record.Runs.LastOrDefault(run => run.Provider == provider && run.SessionId is not null);
        var template = ResumableTask(work)?.Task ?? work.Origin;
        if (back?.SessionId is not { } session || template is null) return;
        // Whoever worked last is who the first agent is told of.
        var numstat = await NumstatAsync(work);
        var settings = _settings.Current.Agent;
        // Its session is often hours old by then: grown big or gone cold, a fresh one starts from the task's state instead.
        var fresh = CompactRule.ForStep(back, settings.AutoCompact, settings.ContextBudget, DateTimeOffset.Now);
        var prompt = fresh is null
            ? AgentCoordinator.ReturnBrief(record, AgentName(record.LastRun?.Provider ?? Other(provider)), numstat)
            : TaskState.CompactBrief(record, numstat, record.Workspace, AgentName);
        record.Add(AgentEntryKind.HandOff, fresh is null ? $"Back to {AgentName(provider)}, its own session"
            : $"Back to {AgentName(provider)}, a fresh session from the task's state · {fresh}");
        record.Handovers.Add($"── Back to {AgentName(provider)} ──\n{prompt}");
        if (work.LastRun is null) work.BackupBeforeDo = true;
        await StartAgentAsync(work, template with
        {
            Provider = provider,
            ResumeSessionId = fresh is null ? session : null,
            Prompt = prompt,
            Model = back.Model,
            Effort = back.Effort,
            Images = [],
            PlanOnly = false,
            AnswerFile = null
        }, work.LastRun, phase: EffortPhase.HandOff);
    }

    /// <summary>The task's steps in Activity's words, for an agent that takes it over; the last ones are what counts.</summary>
    private static void RememberSteps(TaskWork work, IEnumerable<AgentEvent> events)
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
            if (step is not null) work.Steps.Add(Shorten(step, 300));
        }

        if (work.Steps.Count > StepsHandedOn * 2) work.Steps.RemoveRange(0, work.Steps.Count - StepsHandedOn);
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
    /// Events arrive as fast as the CLIs write them; each run's thread takes them in batches, ten times a second at most.
    /// Only the run the Studio shows is drawn there.
    /// </summary>
    private void FlushAgentEvents()
    {
        if (_agentPanel is null) return;
        foreach (var work in _works.Where(work => work.Run is not null).ToArray()) FlushRun(work.Run!, work);
        if (_own?.Live is { } own) FlushRun(own, null);
        UpdateAgentPill();
    }

    /// <summary>What a run's CLI told since the last batch: into its turn and its task's record, and the Studio where it shows the run.</summary>
    private void FlushRun(LiveRun live, TaskWork? work)
    {
        var panel = _agentPanel!;
        // A task's run, or Link's own answer (work null) beside them.
        var shown = work is null ? _ownShown : Shows(work);
        var elapsed = live.Clock.Elapsed;
        live.Turn.Outcome = $"working · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        if (shown)
        {
            panel.SetHeader(AgentHeader(live));
            if (work is not null)
            {
                var (status, dot) = Standing(work.Record);
                panel.SetStatus(status, dot);
            }
        }

        if (live.Pending.Count == 0) return;
        var events = live.Pending.ToArray();
        live.Pending.Clear();
        // Link's own answer is no step of a task: an agent that takes a task over is not told of it.
        if (work is not null) RememberSteps(work, events);
        var steps = events.Select(StepText).OfType<string>().Select(step => Shorten(step, 1200)).ToArray();
        live.Steps.AddRange(steps.Select(step => Shorten(step, 300)));
        live.Turn.AddSteps(steps.Select(StepRow));
        if (events.LastOrDefault(agentEvent => agentEvent.Kind is AgentEventKind.Tool or AgentEventKind.Status) is { } current) live.Turn.Current = current.Text;
        if (work is not null && events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Plan) is { } plan)
        {
            work.Record.Plan = [.. plan.Plan];
            if (shown) ShowPlanOf(work.Record);
        }

        if (work is not null && events.LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Context) is { } context)
        {
            live.Context = (context.ContextTokens, context.ContextWindow ?? live.Context.Window);
            if (shown)
            {
                var (usage, hint) = UsageText(work.Record);
                panel.ShowUsage(usage, hint);
            }

            if (work == _open) RefreshStudio(live: true);
        }

        if (events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Limit))
        {
            foreach (var usage in events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Limit))
            {
                _limits.Update(usage.Limits);
                // A window's first report during the run is what its use is measured from where the start is not to be trusted.
                foreach (var limit in usage.Limits)
                {
                    if (!live.UsageFirst.Any(each => each.Provider == limit.Provider && each.Window == limit.Window)) live.UsageFirst.Add(limit);
                }
            }

            if (shown) panel.ShowLimits(AllLimitRows());
        }

        if (work is not null && !live.LimitOffered && _limits.IsNear(live.Task.Provider)) OfferLimitAhead(work, live);

        // Changed files are listed as they come; the full list, from version control or the copy, replaces them at the end.
        if (events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Artifact))
        {
            live.Files.UnionWith(events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Artifact).Select(agentEvent => agentEvent.Text));
            live.Turn.Files = live.Files.Select(path => new AgentChangeRow(Relative(path, live.Task.Workspace), path)).ToArray();
        }

        if (shown) panel.FollowThread();
    }

    /// <summary>The agent is near its usage limit while it works: the ways on are offered, nothing is done by itself.</summary>
    private void OfferLimitAhead(TaskWork work, LiveRun live)
    {
        live.LimitOffered = true;
        var provider = live.Task.Provider;
        var other = Other(provider);
        SetLimitCard(work, $"{AgentName(provider)} is near its usage limit: {_limits.Describe(provider)}.",
        [
            new PanelAction($"Stop and hand off to {AgentName(other)}", "The agent stops; a card shows the brief for the other agent",
                () =>
                {
                    // A run that is over has nothing to stop: the card for the other agent comes at once.
                    if (work.Run != live)
                    {
                        _ = ShowHandOffCardAsync(work, other);
                        return;
                    }

                    work.HandOffWhenStopped = other;
                    live.Stop.Cancel();
                }),
            new PanelAction("Go on", "The agent works on; when the limit stops it, the same choices come again", () => { })
        ]);
        work.Record.Add(AgentEntryKind.Limit, $"{AgentName(provider)} near its limit: {_limits.Describe(provider)}");
        AgentStatus(work, $"{AgentName(provider)} is near its usage limit · the Studio offers a way on");
    }

    private string AgentHeader(LiveRun live)
    {
        var task = live.Task;
        var access = task.PlanOnly ? "plan"
            : task.AnswerFile is { } file ? $"answer to {Path.GetFileName(file)}"
            : task.Judge ? "judge"
            : task.AutoApprove ? "auto" : "edits";
        var elapsed = live.Clock.Elapsed;
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
        $"{(run.Judge ? "judge" : run.AutoApprove ? "auto" : "edits")}";

    /// <summary>Where the task works: its project, the folder and, in Perforce, its changelist.</summary>
    private static string WhereLine(TaskWork work) =>
        $"{work.Project.Name}{Stack(work.Record.Workspace)} · {work.Record.Workspace}" +
        (work.Record.Changelist is { } changelist ? $" · P4 changelist {changelist}" : string.Empty);

    /// <summary>The tool packs a workspace uses, after the project's name: " · Unreal + Blender + Perforce"; empty for none.</summary>
    private static string Stack(string workspace) => ToolPacks.Names(workspace) is { Length: > 0 } names ? " · " + names : string.Empty;

    /// <summary>
    /// After a task that is done: a lesson the agent kept in the project's copy of a pack's skill may go to every
    /// project, on the user's word. One at a time; the next task's end offers the next.
    /// </summary>
    private void OfferShare(TaskWork work)
    {
        var record = work.Record;
        if (record.Runs.FirstOrDefault() is not { } first || ToolPacks.Lessons(record.Workspace, first.StartedAt).FirstOrDefault() is not ({ } copy, { } shared)) return;
        OfferChoice(work,
            $"The task changed this project's copy of the {shared.Pack} pack's skill {copy.Name}. Share it with all games? " +
            $"The pack's skill takes it, and the copy leaves {ProjectSkills.RelativeFolder}.",
            "Share with all games",
            canRemember: false,
            () =>
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

                SaveTask(work);
                ShowTask(work);
            });
    }

    /// <summary>In Perforce the task's files wait in a pending changelist of its own, which P4V shows; its number stays with the task.</summary>
    private async Task KeepInChangelistAsync(TaskWork work, AgentRun run)
    {
        if (!run.Before.IsPerforce || run.Changes.Count == 0) return;
        // P4V's descriptions are in English whatever the task's language: the agent's own line, else a plain one.
        var english = AgentChanges.SplitChangelistLine(run.Result.Summary).Description;
        var changelist = await _agents.Changes.KeepInChangelistAsync(run.Before, run.Changes,
            english ?? $"Link: changes of an agent task ({Plural(run.Changes.Count, "file")})", english is not null,
            work.Record.Changelist, CancellationToken.None);
        if (changelist is null)
        {
            DiagnosticsReported?.Invoke("Perforce made no changelist for the task's files: they stay where the agent opened them");
            return;
        }

        work.Record.Changelist = changelist;
    }

    /// <summary>
    /// A task's run ended: its turn in the thread, the record, the choices it leaves, the team's or the training's next
    /// move, and a word to the user; a task other than the open one is drawn when it is opened.
    /// </summary>
    private void ShowAgentRun(TaskWork work, LiveRun live, AgentRun run)
    {
        work.LastRun = run;
        var record = work.Record;
        var (task, result) = (run.Task, run.Result);
        var provider = _agents.Provider(task.Provider);
        var spent = SpentByRun(live);
        var elapsed = $"{(int)live.Clock.Elapsed.TotalMinutes}:{live.Clock.Elapsed.Seconds:00}";
        var stopped = result.Stopped || run.Check is { Outcome: AgentCheckOutcome.Stopped };
        // A red check is not "done", whatever the agent's own summary says.
        var checkFailed = !stopped && run.Check is { Passed: false };
        var limitReached = !result.Succeeded && !stopped &&
                           (LimitReached.IsMatch(result.Summary) || _limits.ReachedUntil(task.Provider) is not null);
        // The service, not the work, failed (a sign-in, an API or network error): the same words go again once it is back.
        var serviceError = !result.Succeeded && !stopped && !limitReached && ServiceError.IsMatch(result.Summary);
        // A limit or a service error is no failure of the work: Auto effort does not raise the next run for it, and no
        // critic judges it.
        var ending = stopped ? $"Stopped after {elapsed}"
            : limitReached ? $"Stopped at the usage limit after {elapsed}"
            : serviceError ? $"Stopped by a service error after {elapsed}"
            : !result.Succeeded ? $"Failed after {elapsed}"
            : checkFailed ? $"Check failed after {elapsed}"
            : $"Done in {elapsed}";
        // The lead's plan is the plan card and its verdict a line of the thread: the answer is shown and kept without them.
        var answer = record.Mode is TaskMode.Team or TaskMode.Training ? TeamPlan.WithoutBlocks(result.Summary) : result.Summary;
        var (answerText, question) = AgentQuestion.Split(answer);
        // The changelist's line is for P4V and the next step is for Link's Auto effort; the thread shows the answer without them.
        var said = AgentChanges.SplitChangelistLine(AgentNext.Split(answerText).Text).Text;
        // A question is asked by a run that ended well; one cut short asks nothing.
        work.Question = !stopped && result.Succeeded && !checkFailed ? question : null;
        var text = checkFailed ? CheckReport(task, run.Check!) + (said.Length > 0 ? "\n\n" + said : string.Empty) : said;
        // After a failure, or a Stop in a loop; a Stop without a loop was the user's own choice, and a task done in the
        // end got past its loop. A limit has its own card.
        var failed = !stopped && !limitReached && !serviceError && (!result.Succeeded || checkFailed);
        work.AfterRun = new[]
        {
            new PanelAction("Discuss in chat", "The result, the changed files and their diff go with your next question to ChatGPT",
                () => _ = DiscussInChatAsync(work)),
            UndoAction(work, run),
            ConsoleAction()
        }.OfType<PanelAction>().ToArray();

        var turn = live.Turn;
        turn.Current = string.Empty;
        turn.Outcome = ending;
        turn.OutcomeBrush = OutcomeBrush(ending);
        turn.Said = text;
        turn.Files = run.Changes.Select(change => ToChangeRow(change, record.Workspace)).ToArray();
        turn.Stats = StatsLine(result.Stats) + SpentLine(task.Provider, spent);
        turn.StepsOpen = false;

        var decision = OfferDecision(work, run);
        var needsYou = decision || work.Question is not null;
        RecordRun(work, live, run, ending, text, checkFailed ? text : answer, needsYou, stopped || serviceError, checkFailed, spent);
        // The header counts the lines of one task, the open one: another's are counted when it is opened.
        if (work == _open) _ = ShowChangesAsync(record);
        ShowTask(work);
        if (record.Team.Count > 0) AfterTeamStep(work, run, needsYou: work.Question is not null || decision || limitReached);
        if (record.Training is not null) AfterTrainingRun(work, run, needsYou: work.Question is not null || decision || limitReached);
        // A training shares the skills its exam's lessons went into once they are written, not on the way.
        if (!needsYou && work.Decision is null && record.LastRun is { } done && WorkerEvidence.Verdict(done) == true && TeamRunner.Current(record) is null &&
            record.Training is null or { Stage: TrainingStage.AwaitsExam or TrainingStage.BetweenExams })
        {
            OfferShare(work);
        }

        // Start over only once a run has ended: a loop label often comes on good work, so while the agent works it stays
        // a line of Activity.
        SetRestart(work, work.Origin is not null && (failed || (stopped && result.Loops.Count > 0)), escalate: !IsStrongest(task));
        if (limitReached) OfferLimitReached(work, task.Provider);
        // The open task's result goes to the chat by itself; another's would take the place of the one the user reads.
        if (work == _open) ShowResultInChat(work, run, diff: null);

        var summary = text.Length > 0 ? text : ending;
        var name = ShortName(task.Provider);
        // The notifications name the task's project, which the user may have left for another meanwhile.
        if (work.Question is { } asked)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{name} спрашивает.", $"{name} asks you.");
            NotifyIfAway(work.Project, $"{provider.Name} asks", asked.Question);
        }
        else if (decision)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{name} ждёт решения.", $"{name} needs your decision.");
            NotifyIfAway(work.Project, "Agent needs a decision", ToPreviewText(summary));
        }
        else if (result.Succeeded && !stopped && !checkFailed)
        {
            if (!IsVisible) _unseen = LinkState.Done;
            _cues.Play(SoundCue.Done);
            SayAgent($"{name} закончил работу.", $"{name} finished.");
            NotifyIfAway(work.Project, "Agent finished", ToPreviewText(summary));
        }
        else if (!stopped)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.Error);
            SayAgent(checkFailed ? $"{name} закончил, но проверка не прошла." : $"{name}: не получилось.",
                checkFailed ? $"{name} finished, but the check fails." : $"{name} failed.");
            NotifyIfAway(work.Project, checkFailed ? "The check fails after the agent" : "Agent failed", ToPreviewText(summary));
        }

        AgentStatus(work, limitReached
            ? $"{provider.Name} ran out of its limit · the Studio offers a way on"
            : work.Question is not null ? $"{provider.Name} asks: the choice is in the Studio"
            : $"{provider.Name}: {ending.ToLowerInvariant()} · {Plural(run.Changes.Count, "file")} changed");
    }

    /// <summary>The agent could not start: why, in its thread; another agent can still take the task.</summary>
    private void ShowAgentFailure(TaskWork work, IAgentProvider provider, Exception exception)
    {
        work.Record.Status = AgentTaskStatus.Failed;
        work.Record.Add(AgentEntryKind.NoStart, $"{provider.Name} could not start: {exception.Message}");
        SaveTask(work);
        ShowTask(work);
        // No session began: words here would go to an older run.
        if (Shows(work)) _agentPanel!.ShowReply(false, $"{provider.Name} could not start: try again, or hand the task to the other agent.");
        _cues.Play(SoundCue.Error);
        if (!_sending) SetError(work == _open ? exception.Message : $"«{Shorten(work.Record.Goal, 40)}»: {exception.Message}");
    }

    private Brush OutcomeBrush(string outcome) => (Brush)FindResource(
        outcome.StartsWith("Done", StringComparison.OrdinalIgnoreCase) ? "SuccessBrush"
        : outcome.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("an earlier", StringComparison.Ordinal) ? "MutedBrush"
        : outcome.StartsWith("working", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("Starting", StringComparison.Ordinal) ? "AccentBrush"
        : "ErrorBrush");

    /// <summary>
    /// The run joins the task's record: its own tokens and cost (a Codex session reports its total tokens and a resumed
    /// Claude session its total cost, so the runs before in that session are taken off), its steps, files and answer for the thread, the check, and how the task stands.
    /// </summary>
    private void RecordRun(TaskWork work, LiveRun live, AgentRun run, string ending, string text, string answer, bool needsYou, bool stopped,
        bool checkFailed, IReadOnlyList<UsageSpent> spent)
    {
        var record = work.Record;
        var (task, result) = (run.Task, run.Result);
        var stats = result.Stats;
        var earlier = stats.SessionTokens
            ? record.Runs.Where(previous => previous.SessionId == result.SessionId).ToArray()
            : [];
        var files = run.Changes.Select(change => Relative(change.Path, record.Workspace)).ToList();
        // A worker's run on a team's step; the lead's review and the art critic's judgement run while the step waits on them.
        var step = TeamRunner.Current(record) is { Status: TeamStepStatus.Running } current ? current : null;
        record.Runs.Add(new AgentRunRecord
        {
            Provider = task.Provider,
            Model = task.Model,
            RanModel = result.Model != task.Model ? result.Model : null,
            Effort = task.Effort,
            EffortReason = task.EffortReason,
            RoleId = task.RoleId,
            Step = step is null ? null : TeamRunner.Number(record, step),
            Kind = step?.Kind,
            Tier = step?.Tier,
            Judge = task.Judge,
            AutoApprove = task.AutoApprove,
            SessionId = AgentCoordinator.SessionOf(run),
            StartedAt = DateTimeOffset.Now - stats.Duration,
            Duration = stats.Duration,
            Outcome = ending,
            InputTokens = Less(stats.InputTokens, earlier.Sum(previous => previous.InputTokens ?? 0)),
            CachedTokens = Less(stats.CachedTokens, earlier.Sum(previous => previous.CachedTokens ?? 0)),
            OutputTokens = Less(stats.OutputTokens, earlier.Sum(previous => previous.OutputTokens ?? 0)),
            CostUsd = stats.CostUsd is { } cost && task.ResumeSessionId is { } resumed
                ? Math.Max(0, cost - record.Runs.Where(previous => previous.SessionId == resumed).Sum(previous => previous.CostUsd ?? 0))
                : stats.CostUsd,
            ContextTokens = stats.ContextTokens,
            ContextWindow = stats.ContextWindow,
            Looped = result.Loops.Count > 0,
            Spent = [.. spent],
            Steps = [.. live.Steps.TakeLast(StepsKeptPerRun)],
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
            // A passed check's output tells the next session nothing, and the state carries it into every step.
            record.LastCheck = $"{task.CheckCommand}: {check}" + (!check.Passed && check.Tail.Length > 0 ?"\n" + string.Join('\n', check.Tail.Split('\n').TakeLast(20)) : string.Empty);
            record.Add(AgentEntryKind.Check, $"{task.CheckCommand}: {check}");
        }

        if (text.Trim().Length > 0) record.Add(AgentEntryKind.Said, ToPreviewText(text));
        record.LastWords = text;
        record.ChangedFiles = files;
        record.Steps = [.. work.Steps.TakeLast(StepsHandedOn)];
        record.Status = needsYou ? AgentTaskStatus.NeedsYou
            : stopped ? AgentTaskStatus.Stopped
            : !result.Succeeded ? AgentTaskStatus.Failed
            : checkFailed ? AgentTaskStatus.CheckFailed
            : AgentTaskStatus.Done;
        SaveTask(work);

        static long? Less(long? total, long before) => total is { } value ? Math.Max(0, value - before) : null;
    }

    /// <summary>The task's record saved in its project, which the user may have left for another meanwhile.</summary>
    private void SaveTask(TaskWork work)
    {
        try
        {
            _projects.SaveAgentTask(work.Project, work.Record);
            _stats = null;
            RefreshTaskList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsReported?.Invoke($"Agent task not saved: {exception.Message}");
        }
    }

    /// <summary>
    /// The run ended on its usage limit: hand the task to the other agent, or let the same session go on by itself a
    /// minute after the limit resets. Going on is a box on the card, ticked as the user last left it (see
    /// <see cref="AgentSettings.ContinueAfterLimit"/>). Without a known reset only the hand-off is offered.
    /// </summary>
    private void OfferLimitReached(TaskWork work, AgentProviderKind provider)
    {
        var record = work.Record;
        var until = _limits.ReachedUntil(provider);
        var goOn = until is not null && _settings.Current.Agent.ContinueAfterLimit;
        record.Add(AgentEntryKind.Limit, until is { } reset
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(reset)}" +
              (goOn ? $", and the task goes on by itself at {AgentLimits.Time(reset.AddMinutes(1))}" : string.Empty)
            : $"{AgentName(provider)} hit its usage limit");
        if (goOn)
        {
            record.ContinueAt = until!.Value.AddMinutes(1);
            ArmContinue(work);
        }

        ShowLimitReached(work, provider, until);
        SaveTask(work);
        ShowTask(work);
    }

    /// <summary>
    /// The card of a limit reached: the hand-off, and for a known reset the box that lets the task go on by itself after
    /// it.
    /// </summary>
    private void ShowLimitReached(TaskWork work, AgentProviderKind provider, DateTimeOffset? reset)
    {
        var other = Other(provider);
        PanelAction[] actions =
        [
            new($"Hand off to {AgentName(other)}", "A new session of the other agent with a brief of the task; you see it first",
                () => _ = ShowHandOffCardAsync(work, other))
        ];
        var toggle = reset is { } at
            ? new PanelToggle($"Continue at {AgentLimits.Time(at.AddMinutes(1))}",
                "The task goes on by itself a minute after the limit resets, also if Link is restarted before then; a big session starts fresh from the task's state. Untick it, and the task waits for your words",
                work.Record.ContinueAt is not null, on => GoOnAfterReset(work, at, on))
            : null;
        SetLimitCard(work, reset is { } time
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(time)}."
            : $"{AgentName(provider)} hit its usage limit.", actions, toggle);
    }

    /// <summary>
    /// The box on the limit's card: ticked, the task goes on by itself a minute after the limit resets, the time kept
    /// with the task so that a restart of Link does not lose it; unticked, it waits for the user's words. The next limit
    /// finds the box as it was left. Finish or a new run of the task cancels the wait.
    /// </summary>
    private void GoOnAfterReset(TaskWork work, DateTimeOffset reset, bool on)
    {
        if (_settings.Current.Agent.ContinueAfterLimit != on)
        {
            _settings.Current.Agent.ContinueAfterLimit = on;
            _settings.Save();
        }

        var record = work.Record;
        var at = reset.AddMinutes(1);
        if (on)
        {
            record.ContinueAt = at;
            ArmContinue(work);
        }
        else
        {
            CancelContinue(work);
        }

        // The card keeps the box as it was left.
        if (work.Limit is { Toggle: { } toggle } card) work.Limit = card with { Toggle = toggle with { On = on } };
        record.Add(AgentEntryKind.Limit, on ? $"The task goes on by itself at {AgentLimits.Time(at)}" : "The task waits for your words after the reset");
        SaveTask(work);
        ShowTask(work);
        AgentStatus(work, on
            ? $"The task goes on by itself at {AgentLimits.Time(at)}, also if Link is restarted before then"
            : "The task waits for your words: tick Continue again to let it go on by itself");
    }

    /// <summary>
    /// Starts the timer that sends the task on at its <see cref="AgentTaskRecord.ContinueAt"/>; a time already past is a
    /// second away. Each task waits for its own reset, and its words go only to it.
    /// </summary>
    private void ArmContinue(TaskWork work)
    {
        work.ContinueTimer?.Stop();
        if (work.Record.ContinueAt is not { } at) return;
        var wait = at - DateTimeOffset.Now;
        var timer = new DispatcherTimer { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_works.Contains(work)) return;
            // Its run waits for its folder: a minute later.
            if (work.Busy || work.Waiting is not null)
            {
                timer.Interval = TimeSpan.FromMinutes(1);
                timer.Start();
                return;
            }

            work.ContinueTimer = null;
            work.Record.ContinueAt = null;
            work.Record.Add(AgentEntryKind.Limit, "The limit reset: the session goes on");
            SaveTask(work);
            ContinueAgent(work, "Your usage limit has reset. Go on with the task from where you stopped.", afterLimit: true);
        };
        work.ContinueTimer = timer;
        timer.Start();
    }

    /// <summary>The wait for a limit's reset is over without firing: the timer stops and the task no longer remembers it.</summary>
    private void CancelContinue(TaskWork work)
    {
        work.ContinueTimer?.Stop();
        work.ContinueTimer = null;
        if (work.Record.ContinueAt is null) return;
        work.Record.ContinueAt = null;
        SaveTask(work);
    }

    /// <summary>
    /// Link started, or another project was loaded: the active project's tasks that wait for a limit's reset (their time
    /// was kept with them) are taken up again, without opening the Studio, and their timers run; the first is the open
    /// task while none is.
    /// </summary>
    private void RearmContinue()
    {
        var project = _projects.ActiveProject;
        foreach (var waiting in _projects.ReadAgentTasks(project).Where(task =>
                     task.ContinueAt is not null && task.Status != AgentTaskStatus.Finished && WorkOf(task.Id) is null && Directory.Exists(task.Workspace)))
        {
            var work = TakeUpTask(waiting, project);
            _open ??= work;
        }

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
        if (!_ownShown && _shown?.Record.Id == record.Id) _agentPanel?.ShowChanges(changes.Text, changes.Hint);
    }

    /// <summary>The task drawn again, while the Studio shows it: what happens to another is drawn when it is opened.</summary>
    private void ShowTask(TaskWork work)
    {
        if (Shows(work)) ShowRecord(work);
    }

    /// <summary>
    /// The open task in the Studio: its goal and standing, the line of who works on what, its plan, thread and usage, its
    /// menus and the box for the next words, and the cards kept with it: the agent's question, a choice, a limit, Start
    /// over. A run that waits for its folder shows as one not begun, with Stop.
    /// </summary>
    private void ShowRecord(TaskWork work)
    {
        var panel = _agentPanel!;
        var record = work.Record;
        var live = work.Run;
        panel.ShowTaskView(record.Goal, WhereLine(work), record.Id);
        var (status, dot) = Standing(record);
        panel.SetStatus(status, dot);
        panel.SetHeader(live is not null ? AgentHeader(live) : record.LastRun is { } last ? RunLine(last, record.Workspace) : "No run yet");
        ShowPlanOf(record);
        var (changed, changedFiles) = _changes.Task == record.Id ? (_changes.Text, _changes.Hint) : TaskChanges.Describe(null, record.ChangedFiles);
        panel.ShowChanges(changed, changedFiles);
        panel.ShowThread(BuildThread(record, live));
        var (usage, hint) = UsageText(record);
        panel.ShowUsage(usage, hint);
        var provider = live?.Task.Provider ?? record.LastRun?.Provider ?? StartProvider;
        var waiting = work.Waiting;
        panel.SetRunning(work.Busy || waiting is not null);
        panel.SetReplyTarget(AgentName(provider));
        panel.ResetCards();
        if (!work.Busy && waiting is null)
        {
            panel.ShowQuestion(work.Question);
            if (work.Decision is { } decision) panel.ShowDecision(decision.Text, decision.Button, decision.CanRemember);
            // A task that waits for its agent's limit to reset keeps the card, with the box that ends the wait.
            if (work.Limit is null && record.ContinueAt is { } continueAt && record.LastRun is { } stopped) ShowLimitReached(work, stopped.Provider, continueAt.AddMinutes(-1));
            panel.ShowRestart(work.Restart.Show, work.Restart.Escalate);
            panel.ShowCheaper(CheaperHintFor(work), AgentName(AgentProviderKind.Custom));
            ShowTrainingOf(work, live: false);
        }
        else
        {
            panel.ShowQuestion(null);
            ShowTrainingOf(work, live: true);
        }

        // The card of a limit stays while the agent works on: near the limit, it offers to stop and hand off.
        if (waiting is null) panel.ShowLimit(work.Limit?.Text, work.Limit?.Actions ?? [], work.Limit?.Toggle);
        panel.ShowMenus(work.Busy || waiting is not null ? [] : HandOffActions(work), MoreActions(work));
        panel.ShowFinish(true);
        panel.ShowPlanMode(!work.Busy && waiting is null && work.LastRun is { Task.PlanOnly: true });
        if (work.Asking is { } asking) panel.ShowStep(asking.Step, running: true);
        else if (live is not null) panel.ShowReply(true);
        else if (waiting is not null) panel.ShowReply(false, $"This task waits: {waiting.Why}. Its run starts by itself once that one ends; Stop calls it off.");
        else if (TakesWords(work)) panel.ShowReply(true);
        else panel.ShowReply(false, "This task has no session to go on with: hand it to an agent, or start it anew from its card.");
        ShowSetup(panel, work);
        UpdateAgentPill();
        RefreshStudio();
    }

    /// <summary>
    /// The hint that the custom model could do what is left of the task, with its reason: the plan waiting to be carried
    /// out, or the project's own record of the model for the task's specialist. Null when there is none, or nothing left,
    /// or the task cannot be handed over.
    /// </summary>
    private string? CheaperHintFor(TaskWork work)
    {
        var record = work.Record;
        if (record.Status is AgentTaskStatus.Done or AgentTaskStatus.Finished || ResumableTask(work) is null) return null;
        var custom = _agents.Provider(AgentProviderKind.Custom);
        var planWaits = work.Decision is not null && work.LastRun is { Task.PlanOnly: true };
        return CheaperHint.For(record, custom.Configured, custom.Name, RoleName(record.RoleId, record.Workspace) ?? "General",
            ProjectStats(work.Project, record.Workspace), planWaits ? CheaperHint.RunPlan : "Task");
    }

    /// <summary>
    /// The task as a thread, oldest first: the user's words, each run with its steps, answer, files and numbers, notes
    /// of hand-overs, models and limits between; the run under way last.
    /// </summary>
    private IReadOnlyList<object> BuildThread(AgentTaskRecord record, LiveRun? live)
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
                    turn = TurnOf(record, run, entry.Text, showNext: live is null && run is not null && ReferenceEquals(run, record.LastRun), roles);
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

        if (live is not null) items.Add(live.Turn);
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
    private IReadOnlyList<PanelAction> HandOffActions(TaskWork work)
    {
        if (ResumableTask(work) is not { } resume) return [];
        var current = resume.Task.Provider;
        // Every other agent that can work now: the custom model only once it is set up.
        var others = _agents.Providers.Where(agent => agent.Kind != current && agent.Configured).Select(agent => agent.Kind).ToArray();
        var actions = others.Select(other => new PanelAction($"{AgentName(other)} · new session",
            $"{AgentName(other)} takes the task over with a brief of it; you see and can change the brief first",
            () => _ = ShowHandOffCardAsync(work, other))).ToList();
        foreach (var other in others.Where(other => work.Record.Runs.Any(run => run.Provider == other && run.SessionId is not null)))
        {
            actions.Add(new PanelAction($"Back to {AgentName(other)} · its own session",
                $"{AgentName(other)} goes on in its own session, told what {AgentName(current)} did meanwhile",
                () => _ = ReturnToAsync(work, other)));
        }

        return actions;
    }

    /// <summary>
    /// What the user's next words in the task's session run on, chosen in the composer: the model, the effort (null with
    /// Auto, which the task record keeps) and the specialist. Nothing runs when it is chosen; the next run takes it.
    /// </summary>
    private sealed record NextSetup(AgentProviderKind Provider, string? Model, string? Effort, string? RoleId);

    /// <summary>The setup the task's next words run on: the one chosen, else the session's own; null when no session takes words.</summary>
    private static NextSetup? CurrentSetup(TaskWork work)
    {
        if (work.NextSetup is { } chosen) return chosen;
        var task = work.Run?.Task ?? ResumableTask(work)?.Task ?? work.LastRun?.Task;
        return task is null ? null : new NextSetup(task.Provider, task.Model, task.Effort, work.Record.RoleId);
    }

    /// <summary>
    /// The composer's choice of model, effort and specialist for the next words, like the model picker of the agents' own
    /// apps: the line says what they run on, the menu changes it, and the runs after keep it.
    /// </summary>
    private void ShowSetup(AgentPanel panel, TaskWork work)
    {
        if (CurrentSetup(work) is not { } setup)
        {
            panel.ShowSetup(string.Empty, []);
            return;
        }

        var record = work.Record;
        var agent = _agents.Provider(setup.Provider);
        var auto = record.AutoEffort && _settings.Current.Agent.AutoEffort;
        var efforts = agent.EffortsOf(setup.Model);
        var effortChoices = efforts.Select(effort => new PanelAction(effort.Label, "This effort from the next words on; Auto stops choosing it",
            () => ChooseSetup(work, setup with { Effort = effort.Value }, auto: false), Checked: !auto && setup.Effort == effort.Value)).ToList();
        if (efforts.Count > 0 && _settings.Current.Agent.AutoEffort)
        {
            effortChoices.Insert(0, new PanelAction("Auto effort", "Link chooses the effort of each run, and says why in its line",
                () => ChooseSetup(work, setup, auto: true), Checked: auto));
        }

        var models = agent.Models.Select(model => new PanelAction(model.Label,
            "The same session goes on with this model: it keeps what it knows, the model reads it anew once",
            () => ChooseSetup(work, setup with
            {
                Model = model.Value,
                Effort = agent.EffortsOf(model.Value).Any(effort => effort.Value == setup.Effort) ? setup.Effort : null
            }, auto), Checked: setup.Model == model.Value)).ToArray();
        var roles = ProjectRoles.In(record.Workspace).Select(role => new PanelAction(role.Name,
                $"{role.Description} The same session goes on as this specialist, with its skills: it does not read the project again",
                () => ChooseSetup(work, setup with { RoleId = role.Id }, auto),
                Checked: string.Equals(role.Id, setup.RoleId, StringComparison.OrdinalIgnoreCase)))
            .Prepend(new PanelAction("General", "The same session goes on without a specialist role",
                () => ChooseSetup(work, setup with { RoleId = null }, auto), Checked: setup.RoleId is null)).ToArray();
        var effortWords = auto ? "Auto" : setup.Effort is { } effortValue ? EffortName(agent, effortValue) : null;
        var label = string.Join(" · ", new[]
        {
            RoleName(setup.RoleId, record.Workspace) ?? "General",
            setup.Model is { } modelValue ? ModelName(agent, modelValue) : null,
            effortWords
        }.OfType<string>());
        panel.ShowSetup(label, [effortChoices, models, roles]);
    }

    private void ChooseSetup(TaskWork work, NextSetup setup, bool auto)
    {
        work.Record.AutoEffort = auto;
        SaveTask(work);
        work.NextSetup = setup;
        if (Shows(work)) ShowSetup(_agentPanel!, work);
    }

    /// <summary>
    /// The run with the setup chosen for it in the composer, when it is its agent's: the model and effort, and another
    /// specialist, told so before the user's words.
    /// </summary>
    private static AgentTask WithNextSetup(TaskWork work, AgentTask task)
    {
        if (work.NextSetup is not { } next) return task;
        work.NextSetup = null;
        if (next.Provider != task.Provider) return task;
        var record = work.Record;
        task = task with { Model = next.Model, Effort = next.Effort };
        if (string.Equals(next.RoleId, record.RoleId, StringComparison.OrdinalIgnoreCase)) return task;
        var role = ProjectRoles.Find(next.RoleId, record.Workspace);
        var skills = ProjectRoles.SkillsFor(BriefOf(record), role, ProjectSkills.In(record.Workspace));
        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        record.Add(AgentEntryKind.HandOff, $"Specialist: {role?.Name ?? "General"}");
        if (work.Origin is { } origin) work.Origin = origin with { RoleId = role?.Id, Skills = skills };
        return task with { RoleId = role?.Id, Skills = skills, Prompt = $"{ProjectRoles.SwitchText(role, skills, record.Workspace)}\n\n{task.Prompt}" };
    }

    /// <summary>
    /// The ⋯ menu: what the last run allows (chat, undo), compacting, starting over, the export, and the console. While its
    /// run waits for its folder, nothing that starts another.
    /// </summary>
    private IReadOnlyList<PanelAction> MoreActions(TaskWork work)
    {
        var actions = work.AfterRun.Where(action => action.Label != "Console").ToList();
        if (!work.Busy && work.Waiting is null && ResumableTask(work) is not null)
        {
            actions.Add(new PanelAction("Compact — fresh session from the task state",
                "A new session of the same agent starts from the task's state instead of the session that has grown; you see and can change the state first",
                () => _ = CompactTask(work)));
        }

        if (work.Waiting is null && work.LastRun is not null && work.Origin is not null)
        {
            actions.Add(new PanelAction("Start over with a note", "A new session of the same agent takes the task with a note of what was tried",
                RequestRestart));
        }

        actions.Add(new PanelAction("Export for analysis",
            "The task as Markdown, its thread with every run's numbers, steps and answer: copied to paste into ChatGPT in the browser, and saved as a file to attach",
            () => ExportTask(work)));
        actions.Add(ConsoleAction());
        return actions;
    }

    /// <summary>
    /// Export for analysis: the task as Markdown (<see cref="TaskExport"/>) on the clipboard, and in a file of the project's
    /// data for a chat that takes a long text better as an attachment.
    /// </summary>
    private void ExportTask(TaskWork work)
    {
        var record = work.Record;
        var text = TaskExport.Markdown(record, AgentName, id => RoleName(id, record.Workspace) ?? "General", _settings.Current.Agent.DefaultProvider, DateTimeOffset.Now);
        var file = Path.Combine(work.Project.DataFolder, "Exports", TaskExport.FileName(record));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"The task was not exported: {exception.Message}");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            AgentStatus($"The task is copied as Markdown ({text.Length / 1024 + 1} KB) to paste into ChatGPT, and saved to {file}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard: the file is there to attach.
            AgentStatus($"The task is saved to {file}: the clipboard was busy, attach the file instead");
        }
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
    private (long? Used, long? Window) ContextOf(AgentTaskRecord record) =>
        WorkOf(record.Id)?.Run is { } live ? live.Context : (record.LastRun?.ContextTokens, record.LastRun?.ContextWindow);

    /// <summary>
    /// What the agent really gets, for the inspector: the open task's setup, context, rules, role, memory, state,
    /// skills, brief, tools, check and the project's stats. Before a task is open it is what the next one would get.
    /// Light enough to build at every refresh: no git, no model, the stats read once.
    /// </summary>
    internal HarnessView CurrentHarness()
    {
        var record = _open?.Record;
        var project = _open?.Project ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = StudioWorkspace();

        // What the agent has now: the run under way, else the last run, else what the card would start from.
        AgentProviderKind provider;
        string? model, effort, reason;
        bool planOnly, autoApprove;
        if (_open?.Run?.Task is { } running)
        {
            (provider, model, effort, reason, planOnly, autoApprove) = (running.Provider, running.Model, running.Effort, running.EffortReason, running.PlanOnly, running.AutoApprove);
        }
        else if (record?.LastRun is { } last)
        {
            (provider, model, effort, reason, planOnly, autoApprove) = (last.Provider, last.Model, last.Effort, last.EffortReason, false, last.AutoApprove);
        }
        else
        {
            provider = StartProvider;
            (model, effort) = settings.Level(provider, AgentLevel.Standard);
            (reason, planOnly, autoApprove) = (null, false, settings.DefaultAccess == AgentAccessChoice.Auto);
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
            PlanOnly = planOnly,
            AutoApprove = autoApprove,
            ContextUsed = used,
            ContextWindow = window,
            CheckCommand = project.CheckCommand,
            ToolRules = workspace is null ? [] : AgentCoordinator.Tools(project, workspace),
            Stats = ProjectStats(project, workspace),
            TeamVsSolo = _stats?.TeamVsSolo ?? [],
            AgentName = AgentName
        });
    }

    /// <summary>The project's stats by its saved tasks; read once, and again after a task is saved.</summary>
    private IReadOnlyList<StatsRow> ProjectStats(LinkProject project, string? workspace)
    {
        if (_stats is { } known && known.Project == project.Id) return known.Rows;
        var roles = ProjectRoles.In(workspace);
        var tasks = _projects.ReadAgentTasks(project);
        var rows = AgentStatsReport.For(tasks, AgentName,
            id => id is null ? "General" : roles.FirstOrDefault(role => string.Equals(role.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id);
        _stats = (project.Id, rows, TeamVsSolo.Lines(tasks, _settings.Current.Agent.DefaultProvider));
        return rows;
    }

    /// <summary>
    /// The agent's pill in the card's header and the side panel's Studio button dot: who works and how the task stands,
    /// the open task's or else the one at work, Link's own answer while it is written, and how many more runs are at work.
    /// </summary>
    private void UpdateAgentPill()
    {
        var work = PillWork;
        var own = _own?.Live;
        if (work is null && own is null)
        {
            AgentPill.Visibility = Visibility.Collapsed;
            _panel?.ShowTaskDot(null);
            return;
        }

        // The task's run, else Link's own answer while the task rests.
        var live = work?.Run ?? own;
        var provider = live?.Task.Provider ?? work?.Record.LastRun?.Provider ?? StartProvider;
        var elapsed = live?.Clock.Elapsed ?? TimeSpan.Zero;
        var (words, dot) = live is not null
            ? ($"· {(live == own ? "map " : string.Empty)}{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", (Brush)FindResource("BusyOrbBrush"))
            : Standing(work!.Record);
        var more = _works.Count(other => other.Run is not null && other.Run != live) + (own is not null && own != live ? 1 : 0);
        var text = $"{ShortName(provider)} {words}" + (more > 0 ? $" · +{more}" : string.Empty);
        AgentPillText.Text = text;
        AgentPillDot.Fill = dot;
        var subject = live is not null && live == own ? $"Link writes {OwnWhat(live.Task)}" : work?.Record.Goal ?? "Agent";
        AgentPill.ToolTip = $"{subject}\n{text}" +
                            (more > 0 ? $"\n{Plural(more, "more run")} at work: the tasks are in the Studio" : string.Empty) +
                            (_limits.Describe(provider) is { Length: > 0 } limits ? $"\nUsage: {limits}" : string.Empty) +
                            "\nClick to see it in the Studio";
        AgentPill.Visibility = Visibility.Visible;
        _panel?.ShowTaskDot(dot);
    }

    private void AgentPill_OnClick(object sender, RoutedEventArgs e)
    {
        // What the pill names: the task, else Link's own answer.
        if (PillWork is { } work && (work.Run is not null || _own?.Live is null))
        {
            OpenWork(work);
            ShowTaskInPanel();
        }
        else if (_own is not null)
        {
            ShowAgentPanel();
            ShowOwnRun();
        }
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
    /// What only the user can decide after a run: carry out a plan, or allow what Claude was refused and go on. Returns
    /// whether a decision is offered.
    /// </summary>
    private bool OfferDecision(TaskWork work, AgentRun run)
    {
        var (task, result) = (run.Task, run.Result);
        var record = work.Record;
        ClearChoice(work);
        if (result.Stopped || result.SessionId is not { } session) return false;
        // The team lead's review and the art critic only read: their verdict decides (AfterTeamStep, AfterTrainingRun), not a card of this.
        if (TeamRunner.Current(record) is { Status: TeamStepStatus.Reviewing or TeamStepStatus.Critique } ||
            record.Training is not null && ArtCritic.Judges(task))
        {
            return false;
        }
        // After a plan the user decides whether to carry it out.
        if (task.PlanOnly && result.Succeeded && record.Mode == TaskMode.Team && OfferTeam(work, run)) return true;
        if (task.PlanOnly && result.Succeeded)
        {
            // The plan was made on the strongest model; it is carried out with the card's, in the same session.
            var (model, effort) = _settings.Current.Agent.PlanOnDeep ? work.PlanSetup : (task.Model, task.Effort);
            var auto = record.AutoEffort && _settings.Current.Agent.AutoEffort;
            // On Auto the plan's run picks its effort when it starts, as it does for each run.
            OfferChoice(work,
                "The plan is above. Carry it out with edits in the workspace?" +
                (auto ? " Link picks its effort."
                    : (model, effort) != (task.Model, task.Effort) ? $" It runs on {SetupWords(task.Provider, model, effort)}." : string.Empty),
                "Run the plan",
                canRemember: false,
                () =>
                {
                    record.Add(AgentEntryKind.You, "Run the plan");
                    _ = StartAgentAsync(work, task with
                    {
                        ResumeSessionId = session,
                        PlanOnly = false,
                        Prompt = "Carry out the plan.",
                        Images = [],
                        Model = model,
                        Effort = effort
                    }, null, phase: EffortPhase.RunPlan);
                });
            return true;
        }

        if (task.Provider.RunsOnClaudeCode() && result.Denials.Count > 0)
        {
            var rules = result.Denials;
            OfferChoice(work,
                $"{AgentName(task.Provider)} was not allowed: {string.Join(", ", rules)}. Allow {(rules.Count == 1 ? "it" : "them")} and continue?",
                "Allow and retry",
                canRemember: true,
                () =>
                {
                    if (_rememberAllowance) RememberAllowance(work.Project, rules);
                    var allowed = task.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal).ToArray();
                    record.Add(AgentEntryKind.You, $"Allowed {string.Join(", ", rules)}");
                    _ = StartAgentAsync(work, task with
                    {
                        ResumeSessionId = session,
                        AllowedTools = allowed,
                        Prompt = $"These tools are allowed now: {string.Join(", ", rules)}. Continue the task.",
                        Images = []
                    }, run, phase: EffortPhase.Reply);
                });
            return true;
        }

        return false;
    }

    /// <summary>
    /// "Always for this project": the rules join the project's list. The project is the task's, which the user may have left.
    /// </summary>
    private void RememberAllowance(LinkProject project, IEnumerable<string> rules)
    {
        foreach (var rule in rules.Where(rule => !project.AgentDoTools.Contains(rule))) project.AgentDoTools.Add(rule);

        _projects.Save();
    }

    /// <summary>
    /// Undo the agent's changes (Git, Perforce) or restore the copy (no version control), when there is something to undo:
    /// of a task's run, or of Link's own answer (<paramref name="work"/> null).
    /// </summary>
    private PanelAction? UndoAction(TaskWork? work, AgentRun run)
    {
        var undoable = run.Changes.Count(change => change.Undoable);
        if (run.Before.IsVersioned && undoable > 0)
        {
            return new PanelAction($"Undo agent changes ({undoable})",
                "Put back the files the agent changed that had no changes of yours before the task", () => _ = UndoAgentChangesAsync(work, run));
        }

        return run.Before.Backup is not null && run.Changes.Count > 0
            ? new PanelAction("Restore the copy", $"Put the workspace back as it was before the task, from {run.Before.Backup}",
                () => _ = UndoAgentChangesAsync(work, run))
            : null;
    }

    /// <summary>
    /// Undo of a run: the task's last one, or Link's own answer (the map), which leaves the tasks as they are. Never while
    /// another run edits the folder; after one did, the question says that its changes to these files go back too.
    /// </summary>
    private async Task UndoAgentChangesAsync(TaskWork? work, AgentRun run)
    {
        if (work is { Busy: true }) return;
        if (FolderBusy(work, run.Task.Workspace) is { } busy)
        {
            SetStatus($"{busy}: undo once that run ends");
            return;
        }

        var versioned = run.Before.IsVersioned;
        var files = versioned ? run.Changes.Where(change => change.Undoable).ToArray() : run.Changes.ToArray();
        var kept = run.Changes.Count - files.Length;
        var others = work is { OthersEdited: true } ? " Another run has edited this folder since: what it changed in these files goes back too." : string.Empty;
        var question = (versioned
            ? $"Undo the agent's changes in {files.Length} files? New files are deleted, changed ones come back from version control." +
              (kept > 0 ? $" {kept} files you had changed before the task stay as they are." : string.Empty)
            : $"Put {files.Length} files back from the copy made before the task? Files the agent added are deleted.") + others;
        if (MessageBox.Show(this, question, "Link · undo agent changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        // A run that waited for the folder may have begun while the question was open.
        if (work is { Busy: true } || FolderBusy(work, run.Task.Workspace) is not null)
        {
            SetStatus("Another run began to edit this folder meanwhile: undo once it ends");
            return;
        }

        try
        {
            if (versioned) await _agents.Changes.UndoAsync(run.Before, files, CancellationToken.None);
            else await _agents.Changes.RestoreAsync(run.Before, files, CancellationToken.None);
            // What still differs of the run's own files: what other runs changed since is not its.
            var left = (await _agents.Changes.CompareAsync(run.Before, CancellationToken.None))
                .Where(change => run.Changes.Any(each => each.Path.Equals(change.Path, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (work is null)
            {
                if (_own is { } own && own.Run == run)
                {
                    own.Run = run with { Changes = left };
                    own.Turn.Files = left.Select(change => ToChangeRow(change, run.Task.Workspace)).ToArray();
                    if (_ownShown) ShowOwnRun();
                }

                _projectPanel?.ShowProject(_projects.ActiveProject);
                AgentStatus(left.Length == 0 ? "Link's own answer was undone" : $"Undone; {left.Length} files still differ");
                return;
            }

            var record = work.Record;
            work.LastRun = run with { Changes = left };
            // The task's changelist goes with its last file; Perforce keeps one that still holds files.
            if (run.Before.IsPerforce && record.Changelist is { } changelist && left.All(change => !change.Undoable))
            {
                await AgentChanges.DropChangelistIfEmptyAsync(run.Before.Workspace, changelist, CancellationToken.None);
                record.Changelist = null;
            }

            work.AfterRun = work.AfterRun.Where(action => !action.Label.StartsWith("Undo", StringComparison.Ordinal) && action.Label != "Restore the copy")
                .Prepend(UndoAction(work, work.LastRun)).OfType<PanelAction>().ToArray();
            record.ChangedFiles = left.Select(change => Relative(change.Path, record.Workspace)).ToList();
            record.Add(AgentEntryKind.You, left.Length == 0 ? "Undid the agent's changes" : $"Undid the agent's changes; {left.Length} files still differ");
            SaveTask(work);
            _ = ShowChangesAsync(record);
            ShowTask(work);
            AgentStatus(work, left.Length == 0 ? "The agent's changes were undone" : $"Undone; {left.Length} files still differ");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"Could not undo: {exception.Message}");
        }
    }

    /// <summary>
    /// The task's result goes back into the discussion: a chip for the next Chat question, which × removes. After each
    /// run of the open task it comes by itself, without the diff; Discuss in chat adds the diff and turns the card to ChatGPT.
    /// </summary>
    private void ShowResultInChat(TaskWork work, AgentRun run, string? diff)
    {
        var record = work.Record;
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
    private async Task DiscussInChatAsync(TaskWork work)
    {
        if (work.LastRun is not { } run) return;
        var diff = await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: false, DiffForChat, CancellationToken.None)
                   ?? await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: true, DiffForChat, CancellationToken.None);
        ShowResultInChat(work, run, diff ?? "(no diff: the workspace has no supported version control, or the files are new)");
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

    /// <summary>What a run that just ended took of its agent's usage windows; nothing where its CLI told no numbers.</summary>
    private IReadOnlyList<UsageSpent> SpentByRun(LiveRun live) =>
        AgentLimits.Spent(live.UsageBefore, _limits.For(live.Task.Provider), live.StartedAt, live.UsageFirst);

    /// <summary>" · used 4% of Codex 5 h, 1% of week" for a run's stats line; empty when it took nothing worth saying.</summary>
    private string SpentLine(AgentProviderKind provider, IEnumerable<UsageSpent> spent) =>
        AgentLimits.SpentWords(ShortName(provider), AgentLimits.Total(spent)) is { Length: > 0 } words ? " · " + words : string.Empty;

    private static string Plural(int number, string one, string? many = null) => $"{number} {(number == 1 ? one : many ?? one + "s")}";

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static AgentChangeRow ToChangeRow(AgentChange change, string workspace)
    {
        var kind = change.Kind switch
        {
            "??" or "A" => "+",
            "D" => "−",
            _ => "~"
        };
        var note = change.Undoable ? string.Empty : "  (had your changes before)";
        return new AgentChangeRow($"{kind} {Relative(change.Path, workspace)}{note}", change.Path);
    }

    private static string Relative(string path, string workspace) =>
        path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(workspace, path) : path;

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
