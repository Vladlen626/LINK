using System.Diagnostics;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Tasks at work side by side: each task Link holds keeps its own sessions, run and cards, and its agent works while the
// Studio shows another. Edits in one folder go one run at a time, as a run's changes are found by comparing the folder
// with how it was before: any run may change files.
public partial class MainWindow
{
    // The tasks Link holds while it runs: each one opened, started or taken up again, until it is finished.
    private readonly List<TaskWork> _works = [];
    // The open task: the Studio shows it, and the Agent route's words and the Studio's buttons are for it.
    private TaskWork? _open;
    // The task the Studio's middle shows: the open one, or null for the list and Link's own answer.
    private TaskWork? _shown;

    /// <summary>Whether any agent works now: a task's run or Link's own step for it, or Link's own answer.</summary>
    private bool AnyRunning => _works.Any(work => work.Busy) || _own?.Live is not null;

    /// <summary>The task the pill names: the open one, else the first at work or waiting for its folder.</summary>
    private TaskWork? PillWork => _open ?? _works.FirstOrDefault(work => work.Busy || work.Waiting is not null);

    /// <summary>The task Link holds for a record, by its id; null when it holds none.</summary>
    private TaskWork? WorkOf(Guid id) => _works.FirstOrDefault(work => work.Record.Id == id);

    /// <summary>Whether the Studio shows the task now: what happens to another is drawn when it is opened.</summary>
    private bool Shows(TaskWork work) => _agentPanel is not null && !_ownShown && _shown == work;

    /// <summary>The task becomes the open one, and the one the Studio shows.</summary>
    private void OpenWork(TaskWork work)
    {
        _open = work;
        _ownShown = false;
        _shown = work;
    }

    /// <summary>
    /// What edits <paramref name="folder"/> now, or a folder it is in or holds, apart from <paramref name="work"/>, as the
    /// reason another run waits: another task's run, or Link writing its own answer there; null when nothing does.
    /// </summary>
    private string? FolderBusy(TaskWork? work, string folder)
    {
        if (_works.FirstOrDefault(other => other != work && other.Run is { } live &&
                                           AgentCoordinator.SameFolder(live.Task.Workspace, folder)) is { } busy)
        {
            return $"«{Shorten(busy.Record.Goal, 60)}» edits in this folder now";
        }

        return _own?.Live is { } own && AgentCoordinator.SameFolder(own.Task.Workspace, folder) ? $"Link writes {OwnWhat(own.Task)} in this folder now" : null;
    }

    /// <summary>
    /// A run that would edit where another run edits now waits for it, like a run not begun: its Stop calls it off, and it
    /// starts by itself once that run ends (<see cref="StartWaitingRuns"/>).
    /// </summary>
    private void WaitForFolder(TaskWork work, string why, Func<Task> start)
    {
        if (work.Waiting is null) work.Record.Add(AgentEntryKind.HandOff, $"Waits: {why}; this run starts by itself once that one ends");
        work.Waiting = new WaitingRun(start, why, work.Waiting?.Since ?? DateTimeOffset.Now);
        SaveTask(work);
        ShowTask(work);
        UpdateAgentPill();
        UpdateRouteButton();
        AgentStatus(work, $"the task waits: {why}");
    }

    /// <summary>The run that waited for its folder is called off: the task rests as it was, its cards and words as they were.</summary>
    private void StopWaiting(TaskWork work)
    {
        if (work.Waiting is null) return;
        work.Waiting = null;
        work.Record.Add(AgentEntryKind.You, "Called off the run that waited");
        SaveTask(work);
        ShowTask(work);
        UpdateAgentPill();
        UpdateRouteButton();
    }

    /// <summary>A run has ended: the runs that waited for a folder it freed start, the one waiting longest first.</summary>
    private void StartWaitingRuns()
    {
        foreach (var work in _works.Where(work => work.Waiting is not null).OrderBy(work => work.Waiting!.Since).ToArray())
        {
            // A run that starts holds its folder at once, so the next one waiting for it waits on.
            if (work.Busy || work.Waiting is not { } waiting || FolderBusy(work, work.Record.Workspace) is not null) continue;
            _ = waiting.Start();
        }
    }

    /// <summary>
    /// A run edited <paramref name="folder"/>: the other tasks there no longer find it as their last run left it, so their
    /// next run's changes are compared from its own start, and undo warns that it takes this run's changes back too.
    /// </summary>
    private void EditedBy(TaskWork? work, string folder)
    {
        foreach (var other in _works.Where(other => other != work && AgentCoordinator.SameFolder(other.Record.Workspace, folder))) other.OthersEdited = true;
    }

    /// <summary>
    /// A choice only the user makes for the task: kept with it, on its card whenever the Studio shows the task. No-stop
    /// takes it, unless <paramref name="noStop"/> is off (a choice beyond the task, such as sharing a skill with every project).
    /// With <paramref name="refuse"/> the card has Deny too.
    /// </summary>
    private TaskDecision OfferChoice(TaskWork work, string text, string button, bool canRemember, Action take, bool noStop = true, Action? refuse = null)
    {
        var decision = work.Decision = new TaskDecision(text, button, canRemember, take, noStop, refuse);
        if (Shows(work)) _agentPanel!.ShowDecision(text, button, canRemember, refuse is not null);
        if (noStop) NoStopSoon(work);
        return decision;
    }

    /// <summary>The task's decision is taken, by the user or No-stop: it leaves the card, then what it does is done.</summary>
    private void TakeDecision(TaskWork work, TaskDecision decision, bool always)
    {
        _rememberAllowance = always;
        work.Decision = null;
        if (Shows(work)) _agentPanel!.HideDecision();
        decision.Take();
    }

    /// <summary>The user refused the task's decision: it leaves the card, then its refusal is done.</summary>
    private void RefuseDecision(TaskWork work, TaskDecision decision)
    {
        work.Decision = null;
        if (Shows(work)) _agentPanel!.HideDecision();
        decision.Refuse?.Invoke();
    }

    private void ClearChoice(TaskWork work)
    {
        work.Decision = null;
        if (Shows(work)) _agentPanel!.HideDecision();
    }

    /// <summary>The task's card of a usage limit, kept with it; a way on taken leaves it. Null text takes it away.</summary>
    private void SetLimitCard(TaskWork work, string? text, IReadOnlyList<PanelAction> actions, PanelToggle? toggle = null)
    {
        work.Limit = text is null
            ? null
            : new LimitCard(text, [.. actions.Select(action => action with
            {
                Run = () =>
                {
                    work.Limit = null;
                    action.Run();
                }
            })], toggle);
        if (Shows(work)) _agentPanel!.ShowLimit(work.Limit?.Text, work.Limit?.Actions ?? [], work.Limit?.Toggle);
    }

    /// <summary>Start over after a run that failed, with Escalate when it was not on the strongest model; kept with the task.</summary>
    private void SetRestart(TaskWork work, bool show, bool escalate)
    {
        work.Restart = (show, escalate);
        if (Shows(work)) _agentPanel!.ShowRestart(show, escalate);
    }

    /// <summary>What a task's agent tells the status line; a task other than the open one is named first.</summary>
    private void AgentStatus(TaskWork work, string text) =>
        AgentStatus(work == _open ? text : $"«{Shorten(work.Record.Goal, 40)}»: {text}");

    /// <summary>Link closes: every agent stops, and so does Link's own step.</summary>
    /// <summary>
    /// Link closes while agents work: each run goes into its task's record now, with the session it got, since Link
    /// will not see it end. Without this a task taken up later went on in an older session.
    /// </summary>
    private void KeepRunsCutShort()
    {
        foreach (var work in _works)
        {
            if (work.Run is not { } live) continue;
            var task = live.Task;
            work.Record.Runs.Add(new AgentRunRecord
            {
                Provider = task.Provider,
                Model = task.Model,
                Effort = task.Effort,
                EffortReason = task.EffortReason,
                RoleId = task.RoleId,
                Judge = task.Judge,
                AutoApprove = task.AutoApprove,
                SessionId = live.SessionId ?? task.ResumeSessionId,
                StartedAt = live.StartedAt,
                Duration = live.Clock.Elapsed,
                Outcome = "Stopped: Link closed mid-run",
                Steps = [.. live.Steps.TakeLast(StepsKeptPerRun)],
                Files = [.. live.Files.Select(path => Relative(path, work.Record.Workspace))]
            });
            work.Record.Status = AgentTaskStatus.Stopped;
            work.Record.Add(AgentEntryKind.HandOff, "Link closed while the agent worked: your next words go on in its session");
            SaveTask(work);
        }
    }

    private void StopAllRuns()
    {
        foreach (var work in _works)
        {
            work.Run?.Stop.Cancel();
            work.Asking?.Stop.Cancel();
        }

        _own?.Live?.Stop.Cancel();
    }

    /// <summary>
    /// A task Link holds: its record and project; the session its next words take up (the last run's, else the one it was
    /// taken up with); the run under way, Link's own step for it or the run that waits for its folder; what the user left
    /// for after the run; and the cards the Studio shows for it.
    /// </summary>
    private sealed class TaskWork(AgentTaskRecord record, LinkProject project)
    {
        public AgentTaskRecord Record { get; } = record;

        // The project the task belongs to: the user may switch projects while it runs.
        public LinkProject Project { get; } = project;

        public LiveRun? Run { get; set; }

        // Link's own step around a run, such as asking for a note before Start over: its Stop, and what it does.
        public (CancellationTokenSource Stop, string Step)? Asking { get; set; }

        public WaitingRun? Waiting { get; set; }
        public AgentRun? LastRun { get; set; }

        // Another run edited the task's folder after its last run (see EditedBy).
        public bool OthersEdited { get; set; }

        // The artist's run the art critic judges: after the verdict the user's words go on in its session, not the critic's.
        public AgentRun? Critiqued { get; set; }

        // The run whose whole change the final check reads: after it the user's words go on in its session (FinalCheck).
        public AgentRun? Checked { get; set; }

        // The plan the final check was offered for: once per plan, not again after the fixes it asked for.
        public string? PlanChecked { get; set; }

        // The task as the card or a hand-over started it: what Start over starts again.
        public AgentTask? Origin { get; set; }

        // A task taken up from the list: the next words take up its last session again.
        public AgentTask? EarlierSession { get; set; }

        // What the last run's files break of the harness rules (HarnessLint): told once, with the user's next words.
        public IReadOnlyList<string>? HarnessNotes { get; set; }

        // The model and effort a plan is carried out on, after Plan first ran on the strongest.
        public (string? Model, string? Effort) PlanSetup { get; set; }

        // The run whose plan mode was turned off: while it is the last run, the next words carry its plan out.
        public AgentRun? PlanLeft { get; set; }

        // The user chose Re-plan in a team step's menu: the next words go to the lead for a new plan of the steps left.
        public bool Replanning { get; set; }

        // The agent's question at the end of its last run: a choice until it is answered.
        public AgentQuestion? Question { get; set; }

        // Words the user gave while the agent worked: a correction now (with its images), or words for after the run.
        public string? SteerWith { get; set; }
        public IReadOnlyList<string> SteerImages { get; set; } = [];
        public string? Queued { get; set; }
        public List<string> QueuedImages { get; } = [];

        // What the next words run on, chosen in the composer.
        public NextSetup? NextSetup { get; set; }

        // Files the user attached: the next run of the task is told their paths and may read their folders.
        public List<string> AttachedFiles { get; } = [];

        public bool RestartWhenStopped { get; set; }
        public AgentProviderKind? HandOffWhenStopped { get; set; }
        public TaskDecision? Decision { get; set; }
        public LimitCard? Limit { get; set; }
        public (bool Show, bool Escalate) Restart { get; set; }

        // The ⋯ menu's choices after the last run: undo comes and goes with it.
        public IReadOnlyList<PanelAction> AfterRun { get; set; } = [];

        // The task's steps in Activity's words, oldest first: what an agent that takes the task over is told was done.
        public List<string> Steps { get; } = [];

        // The wait for a usage limit's reset, after which the task goes on by itself.
        public DispatcherTimer? ContinueTimer { get; set; }

        // No-stop's countdown to the choice it takes, that choice's words, and the key of the one it took last.
        public DispatcherTimer? NoStopTimer { get; set; }
        public string? NoStopPending { get; set; }
        public string? NoStopLast { get; set; }
        public bool NoStopArming { get; set; }

        public bool Busy => Run is not null || Asking is not null;
    }

    /// <summary>
    /// A run under way: its task, Stop and clock, the turn of the thread it draws into, what its CLI told that is not drawn
    /// yet, its steps and files so far, what its session holds and the model's window, and its agent's usage windows as
    /// they stood when it began, with the first report of each during it: what it took is measured from them
    /// (see <see cref="AgentLimits.Spent"/>).
    /// </summary>
    private sealed class LiveRun(AgentTask task, ThreadTurn turn, IReadOnlyList<AgentLimit> usageBefore)
    {
        public AgentTask Task { get; } = task;
        public ThreadTurn Turn { get; } = turn;
        public CancellationTokenSource Stop { get; } = new();
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public IReadOnlyList<AgentLimit> UsageBefore { get; } = usageBefore;
        public List<AgentLimit> UsageFirst { get; } = [];
        public List<AgentEvent> Pending { get; } = [];
        public List<string> Steps { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        // The pictures the run looked at or wrote, oldest first; one looked at again moves to the end.
        public List<string> Images { get; } = [];
        public (long? Tokens, long? Window) Context { get; set; }
        public bool LimitOffered { get; set; }

        // The session as the CLI named it on the way: kept with the task should Link close before the run ends.
        public string? SessionId { get; set; }

        // The model the CLI said the session runs on, and whether it has said anything yet: the header shows dots till then.
        public string? Model { get; set; }

        public bool Heard { get; set; }
    }

    /// <summary>A run that waits for another run's edits in its folder: what starts it, why it waits, and since when.</summary>
    private sealed record WaitingRun(Func<Task> Start, string Why, DateTimeOffset Since);

    /// <summary>
    /// A choice on the task's card: its words, its button, whether "always for this project" is offered, what it does,
    /// whether No-stop may take it, and what Deny does when the choice can be refused.
    /// </summary>
    private sealed record TaskDecision(string Text, string Button, bool CanRemember, Action Take, bool NoStop = true, Action? Refuse = null);

    /// <summary>The task's card of a usage limit: its words, the ways on, and the box of what goes on by itself.</summary>
    private sealed record LimitCard(string Text, IReadOnlyList<PanelAction> Actions, PanelToggle? Toggle);
}
