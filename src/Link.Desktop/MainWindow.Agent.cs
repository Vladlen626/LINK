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

// Agent: CLI agents (Claude Code or Codex) work on the user's tasks in the project's workspace, several at
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
    // Claude Code that Link runs signs in apart from the Claude app: without it a run ends at once on these words.
    private static readonly Regex SignedOut = new(
        @"\bNot logged in\b|Please run /login|OAuth token has expired",
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
    // What the agents spent at API prices, as ccusage reads their logs: the line under the usage bars.
    private readonly UsageReport _usage;
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
    private (Guid Project, IReadOnlyList<StatsRow> Rows, IReadOnlyList<string> TeamVsSolo, IReadOnlyList<string> Reflection)? _stats;

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

    private static string ShortName(AgentProviderKind provider) => provider == AgentProviderKind.ClaudeCode ? "Claude" : "Codex";

    /// <summary>The agent that takes the task over at a usage limit: Claude and Codex each have the other.</summary>
    private static AgentProviderKind Other(AgentProviderKind provider) =>
        provider == AgentProviderKind.ClaudeCode ? AgentProviderKind.Codex : AgentProviderKind.ClaudeCode;

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
            // A decision is taken once: one that starts no run (a share, the team's last OK) leaves the panel too.
            panel.HideDecision();
            if (_open is not { Decision: { } decision } work) return;
            UserSpoke(work);
            TakeDecision(work, decision, always);
        };
        panel.DecisionRefused += () =>
        {
            panel.HideDecision();
            if (_open is not { Decision: { } decision } work) return;
            UserSpoke(work);
            RefuseDecision(work, decision);
        };
        panel.RestartRequested += RequestRestart;
        panel.EscalateRequested += Escalate;
        panel.ContinueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: false);
        panel.QueueRequested += (words, attached) => ContinueWithAttached(words, attached, queue: true);
        panel.FinishRequested += FinishTask;
        panel.PlanModeOffRequested += LeavePlanMode;
        panel.ReplanOffRequested += () =>
        {
            if (_open is not { } work) return;
            work.Replanning = false;
            ShowTask(work);
        };
        panel.OpenFileRequested += ShowFile;
        panel.NewTaskRequested += () => _ = OpenNewTaskCardAsync();
        panel.TaskSelected += OpenAgentTask;
        panel.OwnRunRequested += ShowOwnRun;
        panel.LimitRefreshRequested += provider => _ = RefreshLimitsAsync(provider);
        panel.SpentRefreshRequested += () => _ = RefreshSpentAsync(now: true);
        panel.ShowSpent(_usage.Last?.Line, _usage.Last?.Hint);
        _ = RefreshSpentAsync(now: false);
        // The card is in the Studio, so the Studio is there to open its column.
        panel.HarnessRequested += () => _studio?.ShowInspector(true);
        // The folder chosen on the card is the project's, as if chosen in the Project tab; the card takes its harness.
        panel.WorkspaceChosen += (projectId, folder) =>
        {
            if (_projects.Projects.FirstOrDefault(each => each.Id == projectId) is not { } project) return;
            project.WorkspaceRoot = folder;
            _projects.Save();
            if (project == _projects.ActiveProject) _projectPanel?.ShowProject(project);
            panel.RefreshSetup();
            RefreshStudio();
        };
        panel.ShowList(TaskRows(), AllLimitRows(), null);
        return panel;
    }

    /// <summary>No task is shown: the sidebar lists the project's, and the agents at work go on, their rows say so.</summary>
    private void ShowTaskList()
    {
        _shown = null;
        _ownShown = false;
        RefreshTaskList();
        _agentPanel?.ShowListView();
    }

    private void RefreshTaskList()
    {
        _agentPanel?.ShowList(TaskRows(), AllLimitRows(), OwnRow());
        RemoteChanged?.Invoke();
    }

    /// <summary>
    /// What the agents spent, as ccusage counts it, under the usage bars; <paramref name="now"/> counts again, else a count
    /// fresher than <see cref="UsageReport.Fresh"/> stands. The open task's line of its own cost follows.
    /// </summary>
    private async Task RefreshSpentAsync(bool now)
    {
        if (await _usage.ReadAsync(now) is not { } spent) return;
        _agentPanel?.ShowSpent(spent.Line, spent.Hint);
        if (_open is { } work && Shows(work))
        {
            var (usage, hint) = UsageText(work.Record);
            _agentPanel!.ShowUsage(usage, hint);
        }

        RemoteChanged?.Invoke();
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
                RoleBrushes.For(record.RoleId, inWorkspace), record) { Group = GroupOf(record, work) };
        }).ToArray();
    }

    /// <summary>The sidebar's group of a task: at work (or waiting for its folder), waiting for the user, or the rest.</summary>
    private static TaskGroup GroupOf(AgentTaskRecord record, TaskWork? work) =>
        // A running task that asks for a tool waits on the user.
        work?.Run is not null && work.Decision is not null ? TaskGroup.NeedsYou
        : work?.Run is not null || work?.Waiting is not null ? TaskGroup.Running
        : record.Status is AgentTaskStatus.NeedsYou or AgentTaskStatus.CheckFailed or AgentTaskStatus.Failed ? TaskGroup.NeedsYou
        : TaskGroup.Recent;

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

        var record = new AgentTaskRecord
        {
            Goal = Shorten(written.Split('\n')[0].Trim(), 200),
            Brief = brief,
            Workspace = plan.Workspace,
            AutoEffort = plan.AutoEffort,
            NoStop = plan.NoStop,
            RoleId = plan.RoleId,
            Skills = [.. plan.Skills],
            Measured = measured,
            Mode = lead is not null ? TaskMode.Team : TaskMode.Solo,
            Images = [.. origin.Images]
        };
        var how = lead is not null ? $"\n(team: {WorkerLabel(lead)} plans the steps)"
            : plan.PlanFirst ? "\n(plan first)" : string.Empty;
        if (measured && lead is null) how += "\n(team vs solo: alone this time, to compare)";
        if (!ReferenceEquals(written, plan.Goal)) how += "\n(translated into English by ChatGPT)";
        record.Add(AgentEntryKind.Brief, goal.Trim() + ImagesNote(origin.Images) + how);
        var work = new TaskWork(record, project) { PlanSetup = planSetup, Origin = origin };
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

        // Re-plan, chosen in a team step's menu: the words go to the lead for a new plan of the steps left.
        if (work.Replanning)
        {
            work.Replanning = false;
            _ = StartReplanAsync(work, text, images);
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
        if (work.HarnessNotes is { } notes)
        {
            work.HarnessNotes = null;
            text = HarnessLint.Heard(notes, text);
        }

        if (work.EarlierSession is { } earlier)
        {
            work.Origin = earlier with { ResumeSessionId = null };
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
    /// The user's words with what they pasted or dropped: images go as images, other files are named by path at the end
    /// of the words, and the next run may read their folders.
    /// </summary>
    private static (string Words, IReadOnlyList<string> Images, IReadOnlyList<string> Files) WithAttached(string words, IReadOnlyList<string> attached)
    {
        var images = attached.Where(RunImages.IsImage).ToArray();
        var files = attached.Except(images).ToArray();
        if (files.Length > 0) words = $"{words.Trim()}\n\nAttached files:\n{string.Join('\n', files.Select(file => "- " + file))}";
        return (words.Trim(), images, files);
    }

    /// <summary>A reply from the Studio for the open task, with what came along with it.</summary>
    private void ContinueWithAttached(string words, IReadOnlyList<string> attached, bool queue)
    {
        if (_open is not { } work) return;
        UserSpoke(work);
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
        work.Record.Add(AgentEntryKind.Note, "Plan mode off");
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
        // The art critic only judged (a team's step, or an older Link's exam): words go on with
        // the work it judged.
        var last = record.Runs.LastOrDefault(run => !ArtCritic.Judged(run)) ?? record.LastRun;
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
        // Every run starts here: the CLI compacts its session near the budget of Settings, and No-stop is the task's now.
        task = task with
        {
            ContextBudget = _settings.Current.Agent.ContextBudget,
            EffortReason = null,
            NoStop = record.NoStop,
            Deny = record.NoStop ? _settings.Current.Agent.NoStopDeny : [],
            // A tool its rules do not allow is asked while it works; No-stop asks nothing.
            AskPermission = record.NoStop ? null : (ask, token) => AskPermissionAsync(work, ask, token)
        };
        CancelNoStop(work);
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
        var turn = new ThreadTurn($"{provider.Name} · {SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason, unknown: ".")}", "Starting…",
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
            var run = await _agents.RunAsync(task, since, progress, live.Stop.Token);
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
        // A model with no effort levels has nothing to pick, and no reason to show.
        if (efforts.Length == 0) return task;
        var role = ProjectRoles.Find(record.RoleId, record.Workspace)?.Effort;
        // A team's step is a new session of its own worker: its effort follows the step's kind and its own failures.
        var (effort, reason) = TeamRunner.Current(record) is { Status: TeamStepStatus.Running } step
            ? AutoEffort.ForStep(step.Kind, efforts, step.Failures)
            : AutoEffort.Choose(phase, BriefOf(record),
                efforts, last?.Effort, AutoEffort.Failures(record.Runs),
                last is null ? null : AgentNext.Split(last.Words).Next, role);
        return task with { Effort = effort, EffortReason = reason };
    }

    /// <summary>The words the task began with, as the user gave them.</summary>
    private static string BriefOf(AgentTaskRecord record) =>
        record.Entries.FirstOrDefault(entry => entry.Kind == AgentEntryKind.Brief)?.Text ?? record.Goal;

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
            },
            noStop: false);
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
        var signedOut = !result.Succeeded && !stopped && !limitReached && task.Provider == AgentProviderKind.ClaudeCode &&
                        SignedOut.IsMatch(result.Summary);
        // The service, not the work, failed (a sign-in, an API or network error): the same words go again once it is back.
        var serviceError = !result.Succeeded && !stopped && !limitReached && (signedOut || ServiceError.IsMatch(result.Summary));
        // A limit or a service error is no failure of the work: Auto effort does not raise the next run for it, and no
        // critic judges it.
        var ending = stopped ? $"Stopped after {elapsed}"
            : limitReached ? $"Stopped at the usage limit after {elapsed}"
            : signedOut ? "Not signed in"
            : serviceError ? $"Stopped by a service error after {elapsed}"
            : !result.Succeeded ? $"Failed after {elapsed}"
            : checkFailed ? $"Check failed after {elapsed}"
            : $"Done in {elapsed}";
        // The lead's plan is the plan card and its verdict a line of the thread: the answer is shown and kept without them.
        var answer = record.Mode == TaskMode.Team ? TeamPlan.WithoutBlocks(result.Summary) : result.Summary;
        var (answerText, question) = AgentQuestion.Split(answer);
        // The changelist's line is for P4V, the next step for Link's Auto effort and the pages for their buttons; the thread
        // shows the answer without them.
        var (unpaged, pages) = LocalPages.Split(AgentNext.Split(answerText).Text);
        var said = AgentChanges.SplitChangelistLine(unpaged).Text;
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
        turn.Pages = pages.Count > 0 ? pages : LatestPages(record);
        turn.Files = run.Changes.Select(change => ToChangeRow(change, record.Workspace)).ToArray();
        turn.Stats = StatsLine(result.Stats) + SpentLine(task.Provider, spent);
        turn.StepsOpen = false;

        var decision = OfferDecision(work, run);
        if (signedOut)
        {
            OfferSignIn(work, run);
            decision = true;
        }

        var needsYou = decision || work.Question is not null;
        RecordRun(work, live, run, ending, text, checkFailed ? text : answer, needsYou, stopped || serviceError, checkFailed, spent);
        work.HarnessNotes = HarnessLint.Check(record.Workspace, run.Changes) is { Count: > 0 } broken ? broken : null;
        if (work.HarnessNotes is { } notes)
        {
            record.Add(AgentEntryKind.Check, "Harness: " + string.Join(" ", notes));
            SaveTask(work);
        }
        // The header counts the lines of one task, the open one: another's are counted when it is opened.
        if (work == _open) _ = ShowChangesAsync(record);
        ShowTask(work);
        if (record.Team.Count > 0) AfterTeamStep(work, run, needsYou: work.Question is not null || decision || limitReached);
        if (!needsYou && work.Decision is null && record.LastRun is { } done && WorkerEvidence.Verdict(done) == true && TeamRunner.Current(record) is null)
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
        // The paired devices hear how the run ended, unless No-stop goes on by itself (its wait has an alert of its own).
        var alert = stopped || record.NoStop && NoStopChoice(work) is not null ? null
            : work.Question is not null || decision ? $"{provider.Name} needs you"
            : result.Succeeded && !checkFailed ? $"{provider.Name} finished"
            : checkFailed ? "The check fails"
            : $"{provider.Name} stopped: {ending.ToLowerInvariant()}";
        if (alert is not null) RemoteAlert?.Invoke(record.Id, $"{alert} · {Shorten(record.Goal, 60)}", Shorten(summary, 12_000));

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
        NoStopSoon(work);
        _ = RefreshSpentAsync(now: false);
    }

    /// <summary>
    /// Claude Code is not signed in on this computer: the card signs it in and runs the same task again. No-stop leaves
    /// it to the user, whose browser it opens.
    /// </summary>
    private void OfferSignIn(TaskWork work, AgentRun failed) => OfferChoice(work,
        "Claude Code is not signed in on this computer: Link runs it apart from the Claude app, so it signs in once on its own.",
        "Sign in to Claude",
        canRemember: false,
        () => _ = SignInAndRunAgainAsync(work, failed),
        noStop: false);

    private async Task SignInAndRunAgainAsync(TaskWork work, AgentRun failed)
    {
        // The window waits for the user: whoever took the card (a phone, say) is answered first.
        await Task.Yield();
        var setup = _settings.Current.Agent;
        if (AgentExecutables.Find(AgentProviderKind.ClaudeCode, setup.ClaudePath) is not { } executable)
        {
            SetError("Claude Code is not found. Install it, or choose its file in Settings.");
            OfferSignIn(work, failed);
            return;
        }

        try
        {
            ClaudeSignInWindow.Run(_studio is { IsVisible: true } studio ? studio : this, executable, setup.ClaudeAccount);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetError($"The sign-in could not start: {exception.Message}");
        }

        var status = await _agents.Provider(AgentProviderKind.ClaudeCode).CheckAsync(setup, CancellationToken.None);
        if (status.SignedIn != true || !_works.Contains(work))
        {
            if (_works.Contains(work)) OfferSignIn(work, failed);
            return;
        }

        work.Record.Add(AgentEntryKind.Note, "Signed in to Claude: the run goes again");
        _ = StartAgentAsync(work, failed.Task, failed.Task.ResumeSessionId is null ? null : failed);
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
            Images = [.. RunImages.Of(ArtCritic.Named(answer), [.. live.Images, .. run.Changes.Select(change => change.Path)], record.Workspace,
                live.StartedAt, RunImages.Written)],
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
        // ccusage prices every agent's session, Codex's too, from the logs.
        if (_usage.Last?.CostOf(record.Runs.Select(run => run.SessionId).OfType<string>()) is { } counted)
        {
            lines.Add($"At API prices, its sessions: {UsageSummary.Money(counted)} (ccusage)");
        }

        var cost = record.Runs.Sum(run => run.CostUsd ?? 0);
        var hint = cost > 0
            ? $"≈ {cost.ToString("$0.00", CultureInfo.InvariantCulture)} at API prices (Claude Code reports it). On a subscription it is no bill: the windows below are what runs out."
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
        var shown = StudioTask;
        var record = shown?.Record;
        var project = shown?.Project ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = StudioWorkspace();

        // What the agent has now: the run under way, else the last run, else what the card would start from.
        AgentProviderKind provider;
        string? model, effort, reason;
        bool planOnly, autoApprove;
        if (shown?.Run?.Task is { } running)
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
            Reflection = _stats?.Reflection ?? [],
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
        _stats = (project.Id, rows, TeamVsSolo.Lines(tasks, _settings.Current.Agent.DefaultProvider),
            Reflection.Lines(tasks, [.. ProjectSkills.In(workspace).Select(skill => skill.Name)], DateTimeOffset.Now));
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
        // The team lead's review and the art critic only read: their verdict decides (AfterTeamStep), not a card of this. A
        // plan asked for meanwhile (a re-plan) is offered.
        if (!task.PlanOnly && TeamRunner.Current(record) is { Status: TeamStepStatus.Reviewing or TeamStepStatus.Critique })
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

        // A run that asked while it worked had the user's answer to each tool already: a Deny is not offered again.
        if (task.Provider == AgentProviderKind.ClaudeCode && task.AskPermission is null && result.Denials.Count > 0)
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
    /// A tool the running agent asks for, as Claude's own app asks: Allow, with "always for this project", or Deny, while the
    /// run waits. The task needs the user as a finished one would; the end of the run takes the card away unanswered.
    /// </summary>
    private Task<AgentPermissionAnswer> AskPermissionAsync(TaskWork work, AgentPermissionAsk ask, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<AgentPermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            if (cancellationToken.IsCancellationRequested || work.Run is not { } live)
            {
                answer.TrySetCanceled(cancellationToken);
                return;
            }

            var record = work.Record;
            var provider = AgentName(live.Task.Provider);
            var rules = string.Join(", ", ask.Rules);
            var asked = OfferChoice(work, $"{provider} asks to use {ask.Tool}" + (ask.What.Length > 0 ? $": {Shorten(ask.What, 400)}" : "."), "Allow",
                canRemember: true,
                () =>
                {
                    if (_rememberAllowance) RememberAllowance(work.Project, ask.Rules);
                    record.Add(AgentEntryKind.You, $"Allowed {rules}");
                    answer.TrySetResult(new AgentPermissionAnswer(true, _rememberAllowance));
                    RefreshTaskList();
                },
                noStop: false,
                refuse: () =>
                {
                    record.Add(AgentEntryKind.You, $"Denied {rules}");
                    answer.TrySetResult(new AgentPermissionAnswer(false));
                    RefreshTaskList();
                });
            var ended = cancellationToken.Register(() => Dispatcher.BeginInvoke(() =>
            {
                if (work.Decision == asked) ClearChoice(work);
                answer.TrySetCanceled(cancellationToken);
            }));
            _ = answer.Task.ContinueWith(_ => ended.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            RefreshTaskList();
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{ShortName(live.Task.Provider)} ждёт разрешения.", $"{ShortName(live.Task.Provider)} asks for a permission.");
            NotifyIfAway(work.Project, $"{provider} asks to use {ask.Tool}", Shorten(ask.What, 200));
            RemoteAlert?.Invoke(record.Id, $"{provider} needs you · {Shorten(record.Goal, 60)}", $"Allow {Shorten(rules, 200)}?");
            AgentStatus(work, $"{provider} asks to use {ask.Tool} · allow or deny it in the Studio");
        });
        return answer.Task;
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
        if (LocalPages.Split(AgentNext.Split(AgentQuestion.Split(run.Result.Summary).Text).Text).Text.Trim() is { Length: > 0 } words) text.Append("\n\nThe agent's last words:\n").Append(Shorten(words, 1500));
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
