using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// The team in the Studio: the lead plans the task read only, the user sees each step with its worker and why and runs
/// the team, then Link gives the steps to their workers one after another, each in a new session that starts from the
/// task's state and what the steps before handed over. The decisions are <see cref="TeamRunner"/>'s; this only starts
/// runs and shows where the team stands.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The lead's plan has come: its steps are routed to workers and shown as the task's plan, and the user is asked to
    /// run the team. Words instead go on in the lead's session, which may answer with a new plan. False when the answer
    /// holds no plan: then it is an ordinary plan, carried out by the lead.
    /// </summary>
    private bool OfferTeam(AgentTaskRecord record, AgentRun run)
    {
        var roles = ProjectRoles.In(record.Workspace);
        var (_, planned) = TeamPlan.Split(run.Result.Summary, roles);
        if (planned.Count == 0) return false;
        var project = _taskProject ?? _projects.ActiveProject;
        var evidence = _projects.ReadWorkerEvidence(project);
        record.Team = TeamRunner.Steps(planned, Router(evidence), WorkerLabel);
        record.Add(AgentEntryKind.HandOff, $"Team plan: {Plural(record.Team.Count, "step")} · " +
                                           string.Join(", ", record.Team.GroupBy(step => step.Worker).Select(group => $"{group.Key} {group.Count()}")));
        OfferTeamGoOn(record);
        return true;
    }

    /// <summary>
    /// A step's run: a new session of its worker, as the step's specialist with the skills its words call for, told the
    /// task's state, what the steps before handed over and the step. On Auto, Link picks the effort from the step's words.
    /// </summary>
    private async Task StartTeamStepAsync(AgentTaskRecord record, TeamStep step, bool backup = false)
    {
        if (AgentRunning || step.Provider is not { } provider) return;
        var project = _taskProject ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var roles = ProjectRoles.In(record.Workspace);
        var role = ProjectRoles.Find(step.Role, record.Workspace) ?? ProjectRoles.Suggest(step.Text, roles);
        var skills = ProjectRoles.SkillsFor(step.Text, role, ProjectSkills.In(record.Workspace));
        var (model, effort) = settings.Level(provider, step.Level);
        var number = TeamRunner.Number(record, step);
        step.Status = TeamStepStatus.Running;
        record.Add(AgentEntryKind.HandOff, $"Step {number} → {step.Worker}{(role is null ? string.Empty : " as " + role.Name)}: {Shorten(step.Text, 160)} · {step.Reason}");
        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        SaveTask(record);
        // The card's access and Auto, as the lead's run kept them.
        var lead = record.Runs.FirstOrDefault();
        var access = lead?.Access ?? AgentAccess.Do;
        var task = new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = TaskState.StepBrief(record, null, record.Workspace, step, AgentName),
            Access = access,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace, access),
            CheckCommand = project.CheckCommand,
            AutoApprove = lead?.AutoApprove ?? settings.DefaultAccess == AgentAccessChoice.Auto,
            Model = step.Model ?? model,
            Effort = effort,
            RoleId = role?.Id,
            Skills = skills
        };
        // The copy of a folder without version control is made once, before the team's first edits.
        _backupBeforeDo = backup;
        _agentOrigin = task;
        await StartAgentAsync(task, null, phase: EffortPhase.New);
    }

    /// <summary>
    /// After a run of the step under way: the step is done, waits for the user's OK, is tried again, goes a tier up, or
    /// the team stops; the next step starts by itself once this call is over. A run that asked the user something waits
    /// for their answer in its session, and one that was stopped or hit a limit waits for "Go on".
    /// </summary>
    private void AfterTeamStep(AgentTaskRecord record, AgentRun run, bool needsYou)
    {
        if (run.Task.PlanOnly || TeamRunner.Current(record) is not { } step) return;
        // Words to a step that waited for the OK changed it: it waits for the OK again.
        if (step.Status == TeamStepStatus.AwaitsOk)
        {
            if (!needsYou) OfferOk(record, step);
            return;
        }

        if (step.Status != TeamStepStatus.Running) return;
        var number = TeamRunner.Number(record, step);
        var before = step.Worker;
        var verdict = needsYou ? null : WorkerEvidence.Verdict(record.LastRun!);
        var failure = run.Check is { Passed: false } check ? $"the check failed: {check}" : run.Result.Summary;
        var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
        var move = TeamRunner.After(record, step, verdict, run.Result.Summary, failure, Router(evidence), WorkerLabel, evidence);
        var note = move switch
        {
            TeamMove.Next => $"Step {number} done by {before}",
            TeamMove.AwaitOk => $"Step {number} done by {before}: it waits for your OK",
            TeamMove.Retry => $"Step {number} failed: {before} tries once more",
            TeamMove.Escalated => $"Step {number} failed twice: {step.Worker} takes it · {step.Reason}",
            TeamMove.Failed => $"Step {number} failed and no worker is left for it: the team stops · {step.Reason}",
            TeamMove.Finished => $"Step {number} done by {before}: the team is through all {Plural(record.Team.Count, "step")}",
            _ => null
        };
        if (note is not null) record.Add(AgentEntryKind.HandOff, note);
        SaveTask(record);
        ShowTask();
        switch (move)
        {
            case TeamMove.Next or TeamMove.Retry or TeamMove.Escalated:
                GoOnWithTeam(record);
                break;
            case TeamMove.AwaitOk:
                OfferOk(record, step);
                break;
            case TeamMove.Paused when !needsYou:
                OfferTeamGoOn(record);
                break;
        }
    }

    /// <summary>A step only the user can judge (a look, a feel): their OK lets the team go on.</summary>
    private void OfferOk(AgentTaskRecord record, TeamStep step)
    {
        var number = TeamRunner.Number(record, step);
        _agentPanel!.ShowDecision(
            $"Step {number} is done and only you can judge it: {Shorten(step.Text, 160)}. Look at it, then go on; " +
            "your words instead go to its session to change it.",
            "OK, next step",
            canRemember: false);
        _decision = () =>
        {
            record.Add(AgentEntryKind.You, $"OK for step {number}");
            if (TeamRunner.Approve(record, step) == TeamMove.Finished) record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
            SaveTask(record);
            ShowTask();
            GoOnWithTeam(record);
        };
    }

    /// <summary>The next step starts once the run that ended is wholly over; words the user queued for after it go first.</summary>
    private void GoOnWithTeam(AgentTaskRecord record) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (AgentRunning || _task != record || _queued is not null || _steerWith is not null) return;
            if (TeamRunner.Current(record) is { Status: TeamStepStatus.Waiting or TeamStepStatus.Running } step)
            {
                _ = StartTeamStepAsync(record, step);
            }
        });

    /// <summary>
    /// A team that stopped mid-way (a Stop, a limit, Link restarted): "Go on" runs the step it is on in a new session. A
    /// team that has not started yet is offered its run, with the copy before its first edits.
    /// </summary>
    private void OfferTeamGoOn(AgentTaskRecord record)
    {
        if (AgentRunning || _agentPanel is null || TeamRunner.Current(record) is not { Status: not TeamStepStatus.Failed } step) return;
        if (step.Status == TeamStepStatus.AwaitsOk)
        {
            OfferOk(record, step);
            return;
        }

        var number = TeamRunner.Number(record, step);
        var fresh = record.Team.All(each => each.Status == TeamStepStatus.Waiting);
        _agentPanel.ShowDecision(fresh
                ? $"The team plan is in Plan above: {Plural(record.Team.Count, "step")}. Run the team?"
                : $"The team stopped at step {number} of {record.Team.Count}: {Shorten(step.Text, 160)} ({step.Worker}). Go on with it in a new session?",
            fresh ? "Run team" : "Go on with the team", canRemember: false);
        _decision = () =>
        {
            record.Add(AgentEntryKind.You, fresh ? "Run team" : $"Go on with step {number}");
            _ = StartTeamStepAsync(record, step, backup: fresh);
        };
    }

    /// <summary>A team's plan for the task's plan card, its steps' specialists by name.</summary>
    private IReadOnlyList<AgentPlanItem> TeamPlanItems(AgentTaskRecord record) =>
        TeamRunner.PlanItems(record, id => ProjectRoles.Find(id, record.Workspace)?.Name);

    /// <summary>Routing as Settings, the project's evidence and the agents' state stand now.</summary>
    private TeamRunner.Router Router(WorkerEvidence evidence) =>
        (kind, asked) => WorkerRouting.Route(kind, asked, _settings.Current.Agent, WorkerStateOf, evidence, ShortName,
            provider => _agents.Provider(provider).Capabilities.Images);

    /// <summary>Whether an agent can take a step now: set up and found, and not at its usage limit.</summary>
    private WorkerState WorkerStateOf(AgentProviderKind provider)
    {
        var agent = _agents.Provider(provider);
        if (!agent.Configured || agent is CliAgentProvider { } cli && cli.FindExecutable() is null) return WorkerState.NotSet;
        return _limits.ReachedUntil(provider) is not null ? WorkerState.LimitReached : WorkerState.Ready;
    }

    /// <summary>A worker as the plan names it: Claude Code by its model ("Haiku 5.5"), Codex and the custom model by name.</summary>
    private string WorkerLabel(Worker worker) =>
        worker.Provider == AgentProviderKind.ClaudeCode && worker.Model is { } model ? ModelName(_agents.Provider(worker.Provider), model) : ShortName(worker.Provider);
}
