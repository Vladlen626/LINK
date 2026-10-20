using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// The task as one thread: its record drawn as turns, steps and notes, and the runs under way drawn into it live.
public partial class MainWindow
{
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
            // A task that waits for its agent's limit to reset keeps the card, with the box that ends the wait.
            if (work.Limit is null && record.ContinueAt is { } continueAt && record.LastRun is { } stopped) ShowLimitReached(work, stopped.Provider, continueAt.AddMinutes(-1));
            panel.ShowRestart(work.Restart.Show, work.Restart.Escalate);
            ShowTrainingOf(work, live: false);
        }
        else
        {
            panel.ShowQuestion(null);
            ShowTrainingOf(work, live: true);
        }

        // A decision of a running task is a tool its agent asks for and waits on.
        if (waiting is null && work.Decision is { } decision) panel.ShowDecision(decision.Text, decision.Button, decision.CanRemember, decision.Refuse is not null);

        // The card of a limit stays while the agent works on: near the limit, it offers to stop and hand off.
        if (waiting is null) panel.ShowLimit(work.Limit?.Text, work.Limit?.Actions ?? [], work.Limit?.Toggle);
        panel.ShowMenus(work.Busy || waiting is not null ? [] : HandOffActions(work), MoreActions(work));
        panel.ShowFinish(true);
        panel.ShowNoStop(NoStopToggle(work));
        panel.ShowPlanMode(!work.Busy && waiting is null && work.LastRun is { Task.PlanOnly: true } && !work.Replanning);
        panel.ShowReplan(!work.Busy && waiting is null && work.Replanning);
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
        // A run an earlier Link kept has its rows untidied.
        turn.AddSteps(run.Steps.Select(step => StepRow(StepGroups.Tidy(step, record.Workspace))));
        var (said, pages, next) = RunSaid(record, run);
        turn.Said = said;
        turn.Pages = pages;
        if (showNext && next is not null) turn.Next = next.Line;
        turn.Files = run.Files.Select(file => new AgentChangeRow(file, Path.Combine(record.Workspace, file))).ToArray();
        turn.Images = [.. run.Images.Where(File.Exists).Select(path => new ThreadImage(path))];
        var tokens = run.InputTokens is { } input
            ? $" · {AgentContext.Tokens(input)} in ({AgentContext.Tokens(run.CachedTokens ?? 0)} cached), {AgentContext.Tokens(run.OutputTokens ?? 0)} out"
            : string.Empty;
        turn.Stats = $"{(int)run.Duration.TotalMinutes}:{run.Duration.Seconds:00}{tokens}{SpentLine(run.Provider, run.Spent)}";
        return turn;
    }

    /// <summary>
    /// A run's answer as the thread shows it: without the question, the next step, its local pages and the changelist line.
    /// </summary>
    private static (string Said, IReadOnlyList<LocalPages.Page> Pages, AgentNext? Next) RunSaid(AgentTaskRecord record, AgentRunRecord run)
    {
        // A team's answers were kept with the lead's plan before Link took it out, a training's with its lessons.
        var words = record.Mode is TaskMode.Team or TaskMode.Training ? TeamPlan.WithoutBlocks(run.Words) : run.Words;
        var (said, next) = AgentNext.Split(AgentQuestion.Split(words).Text);
        var (unpaged, pages) = LocalPages.Split(said);
        return (AgentChanges.SplitChangelistLine(unpaged).Text, pages, next);
    }

    // Link's own notes (a hand-over, a team's step, a plan) say what they are in their words: they go without a prefix.
    private static string EntryPrefix(AgentEntryKind kind) => kind switch
    {
        AgentEntryKind.Check => "Check: ",
        AgentEntryKind.Model => "Model: ",
        AgentEntryKind.Limit => "Limit: ",
        AgentEntryKind.Said => "Said: ",
        AgentEntryKind.Run => "Run: ",
        _ => string.Empty
    };

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
        AgentEventKind.Status when agentEvent.Detail == AgentEvent.SessionStart => null,
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
        if (live.Pending.Count > 0) live.Heard = true;
        if (live.Pending.LastOrDefault(agentEvent => agentEvent.Model is not null)?.Model is { } model) live.Model = model;
        live.Turn.Who = $"{_agents.Provider(live.Task.Provider).Name} · {LiveSetup(live)}";
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
        var workspace = live.Task.Workspace;
        var steps = events.Select(StepText).OfType<string>().Select(step => Shorten(StepGroups.Tidy(step, workspace), 1200)).ToArray();
        live.Steps.AddRange(steps.Select(step => Shorten(step, 300)));
        live.Turn.AddSteps(steps.Select(StepRow));
        if (events.LastOrDefault(agentEvent => agentEvent.Kind is AgentEventKind.Tool or AgentEventKind.Status) is { } current)
        {
            live.Turn.Current = current.Kind == AgentEventKind.Tool ? StepGroups.Tidy(StepGroups.Step(current), workspace)[2..] : current.Text;
        }
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

        // An artist's renders and screenshots show as they come; one rendered again to the same file is read again.
        var images = events.Select(agentEvent => agentEvent.Kind == AgentEventKind.Artifact && RunImages.IsImage(agentEvent.Text) ? agentEvent.Text : agentEvent.Image)
            .OfType<string>().Select(path => RunImages.Full(path, workspace)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (images.Count > 0)
        {
            live.Images.RemoveAll(images.Contains);
            live.Images.AddRange(images);
            var before = live.Turn.Images;
            live.Turn.Images = [.. RunImages.Of([], live.Images, workspace, live.StartedAt, RunImages.Written)
                .Select(path => !images.Contains(path) && before.FirstOrDefault(image => image.Path == path) is { } same ? same : new ThreadImage(path))];
        }

        if (shown) panel.FollowThread();
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
               $"{LiveSetup(live)} · {access} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>
    /// A running turn's model and effort. Where Link names no model, dots run till the CLI first speaks, then the model it
    /// said it runs on (Claude Code does at once), else its default.
    /// </summary>
    private string LiveSetup(LiveRun live)
    {
        var task = live.Task;
        var dots = new string('.', (int)live.Clock.Elapsed.TotalSeconds % 3 + 1);
        return SetupWords(task.Provider, task.Model, task.Effort, task.EffortReason,
            live.Model is { } ran && ran != task.Model ? ran : null, live.Heard ? "default model" : dots);
    }

    /// <summary>The workspace's specialists, read when the first one is wanted: for the colours of their jellyfish.</summary>
    private static Lazy<IReadOnlyList<AgentRole>> WorkspaceRoles(string workspace) => new(() => ProjectRoles.In(workspace));

    /// <summary>The roles that colour a plan's jellyfish: read only when a step of the plan names its specialist.</summary>
    /// <summary>The task's plan card: a team's steps with their workers, a training's lessons, else the agent's own plan.</summary>
    private void ShowPlanOf(AgentTaskRecord record)
    {
        var plan = PlanOf(record);
        if (record.Training is not null) _agentPanel?.ShowPlan(plan, title: "Lessons");
        else _agentPanel?.ShowPlan(plan, PlanRoles(plan, record.Workspace), stepMenu: record.Team.Count > 0 ? index => TeamStepMenu(record, index) : null);
    }

    private IReadOnlyList<AgentPlanItem> PlanOf(AgentTaskRecord record) =>
        record.Training is { } course ? Training.PlanItems(course) : record.Team.Count > 0 ? TeamPlanItems(record) : record.Plan;

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

    private string RunLine(AgentRunRecord run, string workspace) =>
        $"{RoleWords(run.RoleId, workspace)}{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, run.EffortReason, run.RanModel)} · " +
        $"{(run.Judge ? "judge" : run.AutoApprove ? "auto" : "edits")}";

    /// <summary>Where the task works: its project, the folder and, in Perforce, its changelist.</summary>
    private static string WhereLine(TaskWork work) =>
        $"{work.Project.Name}{Stack(work.Record.Workspace)} · {work.Record.Workspace}" +
        (work.Record.Changelist is { } changelist ? $" · P4 changelist {changelist}" : string.Empty);

    private Brush OutcomeBrush(string outcome) => (Brush)FindResource(
        outcome.StartsWith("Done", StringComparison.OrdinalIgnoreCase) ? "SuccessBrush"
        : outcome.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("an earlier", StringComparison.Ordinal) ? "MutedBrush"
        : outcome.StartsWith("working", StringComparison.OrdinalIgnoreCase) || outcome.StartsWith("Starting", StringComparison.Ordinal) ? "AccentBrush"
        : "ErrorBrush");
}
