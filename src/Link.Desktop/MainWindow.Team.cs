using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// The team in the Studio: the lead plans the task, the user sees each step with its worker and why and runs
/// the team, then Link gives the steps to their workers one after another, each in a new session that starts from the
/// task's state and what the steps before handed over. The decisions are <see cref="TeamRunner"/>'s; this only starts
/// runs and shows where the team stands.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The lead's plan has come: its steps are routed to workers and shown as the task's plan, and the user is asked to
    /// run the team. A plan of the steps left (a re-plan) keeps the steps the team is through. Words instead go on in the
    /// lead's session, which may answer with a new plan. False when the answer holds no plan and the task has no team
    /// yet: then it is an ordinary plan, carried out by the lead; a team's plan stays as it was.
    /// </summary>
    private bool OfferTeam(TaskWork work, AgentRun run)
    {
        var record = work.Record;
        var roles = ProjectRoles.In(record.Workspace);
        var (_, planned) = TeamPlan.Split(run.Result.Summary, roles);
        if (planned.Count == 0)
        {
            if (record.Team.Count == 0) return false;
            record.Add(AgentEntryKind.HandOff, "The lead gave no new plan: the team's plan stays as it was");
            OfferTeamGoOn(work);
            return true;
        }

        var evidence = _projects.ReadWorkerEvidence(work.Project);
        var kept = record.Team.TakeWhile(TeamRunner.Over).Count();
        record.Team = TeamRunner.Replanned(record, planned, Router(evidence), WorkerLabel);
        // The lead's session goes on: it reviews what the workers do.
        record.LeadSession = AgentCoordinator.SessionOf(run);
        record.Add(AgentEntryKind.HandOff, (kept == 0 ? "Team plan: " : $"New plan from step {kept + 1}: ") + $"{Plural(record.Team.Count - kept, "step")} · " +
                                           string.Join(", ", record.Team.Skip(kept).GroupBy(step => step.Worker).Select(group => $"{group.Key} {group.Count()}")));
        OfferTeamGoOn(work);
        return true;
    }

    /// <summary>
    /// A step's run: a new session of its worker, as the step's specialist with the skills its words call for, told the
    /// task's state, what the steps before handed over and the step; an art step sees the images the task began with. On
    /// Auto, Link picks the effort from the step's words. A step the lead or the art critic sent back goes on in its
    /// worker's own session, told their notes.
    /// </summary>
    private async Task StartTeamStepAsync(TaskWork work, TeamStep step)
    {
        if (work.Busy) return;
        var record = work.Record;
        // Steps routed while a worker could not work go to one that can now, a limit having reset or an agent set up since.
        var rerouted = TeamRunner.Reroute(record, Router(_projects.ReadWorkerEvidence(work.Project)), WorkerLabel,
            provider => WorkerStateOf(provider) == WorkerState.Ready);
        foreach (var (each, before) in rerouted) record.Add(AgentEntryKind.HandOff, $"Step {TeamRunner.Number(record, each)}: {before} → {each.Worker} · {each.Reason}");
        if (step.Provider is not { } provider)
        {
            record.Add(AgentEntryKind.HandOff, $"Step {TeamRunner.Number(record, step)}: no worker can take it now · {step.Reason} · choose one with its worker chip in Plan");
            SaveTask(work);
            ShowTask(work);
            return;
        }
        var project = work.Project;
        var settings = _settings.Current.Agent;
        var roles = ProjectRoles.In(record.Workspace);
        var role = ProjectRoles.Find(step.Role, record.Workspace) ?? ProjectRoles.Suggest(step.Text, roles);
        // A specialist reads its role's skills first, whatever the step's words; a step without one, those its words call for.
        var workspaceSkills = ProjectSkills.In(record.Workspace);
        var skills = role is null ? ProjectRoles.SkillsFor(step.Text, null, workspaceSkills) : ProjectRoles.OwnSkills(role, workspaceSkills);
        var (model, effort) = settings.Level(provider, step.Level);
        var number = TeamRunner.Number(record, step);
        var again = step.ReviewNotes is { } notes ? TeamPlan.RedoPrompt(notes) : step.CriticNotes is { } defects ? ArtCritic.RedoPrompt(defects) : null;
        // A step sent up a tier has a session of another agent, which this worker cannot resume.
        var redo = again is not null && OwnSession(record, step.SessionId, provider) is { } session ? (Prompt: again, Session: session) : default;
        // The worker that did the step before as the same specialist goes on in its session: no second onboarding, no rereading.
        var goOn = redo.Session is null ? GoesOnFrom(record, step, provider, role, roles) : null;
        if (goOn is not null) redo = (TaskState.NextStep(step), goOn);
        var prompt = redo.Session is null ? TaskState.StepBrief(record, null, record.Workspace, step, AgentName) : redo.Prompt;
        step.ReviewNotes = null;
        step.CriticNotes = null;
        step.Status = TeamStepStatus.Running;
        if (redo.Session is null || goOn is not null)
        {
            record.Add(AgentEntryKind.HandOff,
                $"Step {number} → {step.Worker}{(role is null ? string.Empty : " as " + role.Name)}{(goOn is null ? string.Empty : ", in its session of the step before")}: " +
                $"{TeamPlan.Headline(step.Text).Title} · {step.Reason}");
        }

        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        SaveTask(work);
        // The card's Auto, as the lead's run kept it.
        var lead = record.Runs.FirstOrDefault();
        var task = new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = prompt,
            ResumeSessionId = redo.Session,
            // The user's references; a session sent back has them already.
            Images = StepKinds.IsArt(step.Kind) && redo.Session is null ? [.. record.Images.Where(File.Exists)] : [],
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
            CheckCommand = project.CheckCommand,
            AutoApprove = lead?.AutoApprove ?? settings.DefaultAccess == AgentAccessChoice.Auto,
            Model = step.Model ?? model,
            Effort = effort,
            RoleId = role?.Id,
            Skills = skills
        };
        if (redo.Session is null) work.Origin = task;
        // Start over begins a new session, which needs the whole brief.
        else if (goOn is not null) work.Origin = task with { Prompt = TaskState.StepBrief(record, null, record.Workspace, step, AgentName), ResumeSessionId = null };
        await StartAgentAsync(work, task, null, phase: EffortPhase.New);
    }

    /// <summary><paramref name="session"/> when it is a session of <paramref name="provider"/> in this task; null otherwise.</summary>
    private static string? OwnSession(AgentTaskRecord record, string? session, AgentProviderKind provider) =>
        session is not null && record.Runs.LastOrDefault(run => run.SessionId == session) is { } ran && ran.Provider == provider ? session : null;

    /// <summary>
    /// The session of the step just before <paramref name="step"/> when the same worker (agent, model, level) did it as the
    /// same specialist, on art or not alike, and it has not grown big or gone cold (<see cref="CompactRule"/>); null: a new session.
    /// </summary>
    private string? GoesOnFrom(AgentTaskRecord record, TeamStep step, AgentProviderKind provider, AgentRole? role, IReadOnlyList<AgentRole> roles)
    {
        var at = record.Team.IndexOf(step);
        if (at < 1 || step.EscalatedFrom is not null || record.Team[at - 1] is not { Status: TeamStepStatus.Done } before) return null;
        var beforeRole = ProjectRoles.Find(before.Role, record.Workspace) ?? ProjectRoles.Suggest(before.Text, roles);
        if (before.Provider != provider || before.Model != step.Model || before.Level != step.Level || beforeRole?.Id != role?.Id
            || StepKinds.IsArt(before.Kind) != StepKinds.IsArt(step.Kind)
            || OwnSession(record, before.SessionId, provider) is not { } session) return null;
        var settings = _settings.Current.Agent;
        var last = record.Runs.LastOrDefault(run => run.SessionId == session);
        return CompactRule.ForStep(last, settings.AutoCompact, settings.ContextBudget, DateTimeOffset.Now) is null ? session : null;
    }

    /// <summary>
    /// After a run of the step under way: the step is done, goes to the art critic or the lead's review, waits for the
    /// user's OK, is tried again, goes a tier up, or the team stops; the next step starts by itself once this call is
    /// over. A run that asked the user something waits for their answer in its session, and one that was stopped or hit a
    /// limit waits for "Go on". A run while the step is under review is the lead's, and one while it is judged the
    /// critic's: their verdict decides the step.
    /// </summary>
    private void AfterTeamStep(TaskWork work, AgentRun run, bool needsYou)
    {
        var record = work.Record;
        if (run.Task.PlanOnly || TeamRunner.Current(record) is not { } step) return;
        // Words to a step that waited for the OK changed it: it waits for the OK again. Files changed now show its work,
        // and the critic's notes are of the work before.
        if (step.Status == TeamStepStatus.AwaitsOk)
        {
            if (run.Changes.Count > 0 && (step.Unverified || step.CriticNotes is not null))
            {
                step.Unverified = false;
                step.CriticNotes = null;
                SaveTask(work);
            }

            if (!needsYou) OfferOk(work, step);
            return;
        }

        if (step.Status == TeamStepStatus.Reviewing)
        {
            // The lead's session, or the fresh one it reviews in (StartLeadReviewAsync): a worker's words go on in its own.
            var lead = run.Task.ResumeSessionId is null
                ? run.Task.Judge
                : run.Task.ResumeSessionId == record.LeadSession || AgentCoordinator.SessionOf(run) == record.LeadSession;
            if (lead) AfterReview(work, step, run, needsYou);
            // Words the user left the worker for after its run went first: the lead reviews what it did then.
            else if (!needsYou) Dispatcher.BeginInvoke(() => _ = StartLeadReviewAsync(work, step, run));
            return;
        }

        if (step.Status == TeamStepStatus.Critique)
        {
            if (ArtCritic.Judges(run.Task)) AfterCritique(work, step, run, needsYou);
            // Words the user left the artist for after its run went first: the critic judges what it made then.
            else if (!needsYou) CritiqueAgain(work, step, run);
            return;
        }

        if (step.Status != TeamStepStatus.Running) return;
        var number = TeamRunner.Number(record, step);
        var before = step.Worker;
        step.SessionId = AgentCoordinator.SessionOf(run) ?? step.SessionId;
        var verdict = needsYou ? null : WorkerEvidence.Verdict(record.LastRun!);
        var failure = run.Check is { Passed: false } check ? $"the check failed: {check}" : run.Result.Summary;
        var evidence = _projects.ReadWorkerEvidence(work.Project);
        var changed = run.Changes.Count > 0;
        var art = verdict == true ? CritiqueOf(record, step, run, evidence) : (null, null, []);
        var review = TeamRunner.Reviews(record, step, changed, _settings.Current.Agent.Team.LeadReview);
        var move = TeamRunner.After(record, step, verdict, run.Result.Summary, failure, Router(evidence), WorkerLabel, evidence, review, changed,
            critique: art.Critic is not null);
        var note = move switch
        {
            TeamMove.Critique => $"Step {number} done by {before}: {WorkerLabel(art.Critic!)} judges its frames · {art.Why}",
            TeamMove.Review => $"Step {number} done by {before}: {LeadName(record)} reviews it",
            TeamMove.Next => $"Step {number} done by {before}",
            TeamMove.AwaitOk => $"Step {number} done by {before}: it waits for your OK",
            TeamMove.Unverified => $"Step {number}: {before} says it is done, but no file changed: unverified, not counted for it · it waits for your OK",
            TeamMove.Retry => $"Step {number} failed: {before} tries once more",
            TeamMove.Escalated => $"Step {number} failed twice: {step.Worker} takes it · {step.Reason}",
            TeamMove.Failed => $"Step {number} failed and no worker is left for it: the team stops · {step.Reason}",
            TeamMove.Finished => $"Step {number} done by {before}: the team is through all {Plural(record.Team.Count, "step")}",
            _ => null
        };
        // What the next workers are told of this step, besides the plan's line for it.
        if (note is not null && move is TeamMove.Critique or TeamMove.Review or TeamMove.Next or TeamMove.AwaitOk or TeamMove.Unverified &&
            step.HandedOver is { } handed)
        {
            note += $" · hands over: {Shorten(handed, 240)}";
        }

        // Art the critic should have judged: why it did not.
        if (note is not null && art.Critic is null && art.Why is not null) note += " · " + art.Why;

        if (note is not null) record.Add(AgentEntryKind.HandOff, note);
        SaveTask(work);
        ShowTask(work);
        switch (move)
        {
            case TeamMove.Critique:
                var (critic, frames) = (art.Critic!, art.Frames);
                Dispatcher.BeginInvoke(() => _ = StartCritiqueAsync(work, step, critic, run, frames));
                break;
            case TeamMove.Review:
                Dispatcher.BeginInvoke(() => _ = StartLeadReviewAsync(work, step, run));
                break;
            case TeamMove.Next or TeamMove.Retry or TeamMove.Escalated:
                GoOnWithTeam(work);
                break;
            case TeamMove.AwaitOk or TeamMove.Unverified:
                OfferOk(work, step);
                break;
            case TeamMove.Paused when !needsYou:
            case TeamMove.Failed:
                OfferTeamGoOn(work);
                break;
        }
    }

    /// <summary>
    /// The lead reviews the step a worker has done, told to change nothing: the worker's answer, the check and the diff of its run
    /// (<paramref name="workerRun"/>), else what the task's record kept of the worker's last run, with the files to read.
    /// In its own session, told how to review once; a session near its budget or gone cold gives way to a fresh one from
    /// the task's state. Medium effort: a review reads a diff, it does not plan.
    /// </summary>
    private async Task StartLeadReviewAsync(TaskWork work, TeamStep step, AgentRun? workerRun)
    {
        var record = work.Record;
        if (work.Busy || !_works.Contains(work) || record.Runs.FirstOrDefault(run => run.SessionId == record.LeadSession) is not { } lead) return;
        var project = work.Project;
        var kept = record.Runs.LastOrDefault(run => run.SessionId == step.SessionId);
        var answer = workerRun?.Result.Summary ?? kept?.Words ?? string.Empty;
        var files = workerRun?.Changes.Select(change => Relative(change.Path, record.Workspace)).ToArray() ?? [.. kept?.Files ?? []];
        var settings = _settings.Current.Agent;
        // A lead's session grown big or gone cold is not read again for a review: a fresh one starts from the task's state.
        var fresh = CompactRule.ForStep(record.Runs.LastOrDefault(run => run.SessionId == record.LeadSession), settings.AutoCompact,
            settings.ContextBudget, DateTimeOffset.Now);
        // The diff of the worker's run alone; a fresh session did not see the try before the lead's notes: it gets all the files' changes.
        var before = workerRun?.Before is { } start && fresh is not null && step.Redos > 0 ? start with { Snapshot = null } : workerRun?.Before;
        var diff = workerRun is { } done && before is not null
            ? await _agents.Changes.DiffAsync(before, done.Changes, stat: false, ReviewDiff, CancellationToken.None)
            : null;
        var check = workerRun?.Check is { } result ? (result.Passed ? "passed" : result.ToString()) : null;
        var request = File.Exists(TeamPlan.ReviewFile) ? await File.ReadAllTextAsync(TeamPlan.ReviewFile) : string.Empty;
        if (work.Busy || !_works.Contains(work)) return;
        // A session that has given a verdict knows how to review: it is told the step alone.
        var review = TeamPlan.ReviewPrompt(fresh is null && record.ReviewSession == record.LeadSession ? null : request, TeamRunner.Number(record, step), step,
            answer, check, files, diff, before?.DiffsOwnWork == true);
        if (fresh is not null) record.Add(AgentEntryKind.HandOff, $"{LeadName(record)} reviews step {TeamRunner.Number(record, step)} in a fresh session from the task's state · {fresh}");
        var efforts = _agents.Provider(lead.Provider).EffortsOf(lead.Model);
        await StartAgentAsync(work, new AgentTask
        {
            Provider = lead.Provider,
            Workspace = record.Workspace,
            Prompt = fresh is null ? review : $"{TaskState.ReviewBrief(record, record.Workspace, step, AgentName)}\n\n{review}",
            ResumeSessionId = fresh is null ? record.LeadSession : null,
            Judge = true,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
            AutoApprove = record.Runs.FirstOrDefault()?.AutoApprove ?? _settings.Current.Agent.DefaultAccess == AgentAccessChoice.Auto,
            Model = lead.Model,
            Effort = efforts.Any(effort => effort.Value == "medium") ? "medium" : lead.Effort
        }, null);
    }

    /// <summary>
    /// The lead's run on a step under review ended: its verdict moves the team on, sends the step back or up, or stops
    /// the team. Without a verdict (a Stop, a limit, a question, words of the user's) the user decides.
    /// </summary>
    private void AfterReview(TaskWork work, TeamStep step, AgentRun run, bool needsYou)
    {
        var record = work.Record;
        record.LeadSession = AgentCoordinator.SessionOf(run) ?? record.LeadSession;
        var number = TeamRunner.Number(record, step);
        var worker = step.Worker;
        var lead = LeadName(record);
        if (needsYou || TeamPlan.SplitReview(run.Result.Summary).Review is not { } review)
        {
            SaveTask(work);
            if (!needsYou) OfferReviewDecision(work, step, $"{lead}'s review of step {number} ended without a verdict.");
            return;
        }

        record.ReviewSession = record.LeadSession;

        var evidence = _projects.ReadWorkerEvidence(work.Project);
        var move = TeamRunner.Reviewed(record, step, review, Router(evidence), WorkerLabel, evidence);
        var notes = review.Notes.Length > 0 ? " · " + Shorten(review.Notes, 200) : string.Empty;
        record.Add(AgentEntryKind.HandOff, move switch
        {
            TeamMove.Next or TeamMove.Finished => $"Step {number}: {lead} accepted the work of {worker}{notes}",
            TeamMove.Redo => $"Step {number}: {lead} sends it back to {worker} ({step.Redos} of {TeamRunner.MaxRedos}){notes}",
            TeamMove.Escalated => $"Step {number}: {lead} gives it to {step.Worker}{notes}",
            TeamMove.Failed => $"Step {number}: {lead} sends it on, but no stronger worker is left: the team stops{notes}",
            _ => $"Step {number}: {lead} stopped the team{notes}"
        });
        if (move == TeamMove.Finished) record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
        SaveTask(work);
        ShowTask(work);
        if (move is TeamMove.Next or TeamMove.Redo or TeamMove.Escalated) GoOnWithTeam(work);
        else if (move == TeamMove.Stopped) OfferReviewDecision(work, step, $"{lead} stopped the team at step {number}{notes}.");
        else if (move == TeamMove.Failed) OfferTeamGoOn(work);
    }

    /// <summary>
    /// A step under review that waits for the user: accept it and go on, or tell the lead what to do: the words go to its
    /// session, and its next verdict decides the step.
    /// </summary>
    private void OfferReviewDecision(TaskWork work, TeamStep step, string why)
    {
        var record = work.Record;
        var number = TeamRunner.Number(record, step);
        OfferChoice(work, $"{why} Accept step {number} and go on, or tell the lead what to do.", "Accept and go on", canRemember: false, () =>
        {
            record.Add(AgentEntryKind.You, $"Accept step {number}");
            var evidence = _projects.ReadWorkerEvidence(work.Project);
            if (TeamRunner.Reviewed(record, step, new LeadReview(ReviewVerdict.Accept, string.Empty), Router(evidence), WorkerLabel, evidence) == TeamMove.Finished)
            {
                record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
            }

            SaveTask(work);
            ShowTask(work);
            GoOnWithTeam(work);
        });
    }

    /// <summary>
    /// Whether the art critic judges the art a step's run made, who and from which frames, and why that critic; for art
    /// it cannot judge, why not. Nothing when art reviews are off, the step is not art the critic judges (see
    /// <see cref="ArtCritic.Judgeable"/>) or the run changed no file: then it is unverified.
    /// </summary>
    private (Worker? Critic, string? Why, IReadOnlyList<string> Frames) CritiqueOf(AgentTaskRecord record, TeamStep step, AgentRun run,
        WorkerEvidence evidence)
    {
        if (!_settings.Current.Agent.Team.ArtReview || !StepKinds.IsArt(step.Kind) || !ArtCritic.Judgeable(run.Task) || run.Changes.Count == 0 ||
            step.Provider is not { } provider)
        {
            return (null, null, []);
        }

        var frames = ArtCritic.Frames(ArtCritic.Named(run.Result.Summary), record.Workspace, File.Exists);
        if (frames.Count == 0) return (null, "it named no frames, so no art critic judged it", []);
        var (critic, why) = CriticFor(new Worker(provider, step.Level, step.Model), evidence);
        return (critic, why, frames);
    }

    /// <summary>Who judges an artist's work now, as Settings, the project's evidence and the agents' state stand (see <see cref="ArtCritic.Choose"/>).</summary>
    private (Worker? Critic, string Reason) CriticFor(Worker artist, WorkerEvidence evidence) =>
        ArtCritic.Choose(artist, _settings.Current.Agent, WorkerStateOf, evidence, provider => _agents.Provider(provider).Capabilities.Images, ShortName);

    /// <summary>
    /// The user's words changed a step's art before the critic judged it: the critic judges what the artist made then.
    /// With no frames named or no critic to judge them, the step waits for the user's OK.
    /// </summary>
    private void CritiqueAgain(TaskWork work, TeamStep step, AgentRun run)
    {
        var record = work.Record;
        var number = TeamRunner.Number(record, step);
        var evidence = _projects.ReadWorkerEvidence(work.Project);
        var (critic, why, frames) = CritiqueOf(record, step, run, evidence);
        if (critic is null)
        {
            TeamRunner.Critiqued(record, step, new LeadReview(ReviewVerdict.Stop, string.Empty), evidence);
            record.Add(AgentEntryKind.HandOff, $"Step {number}: {why ?? "no art critic judged it"} · it waits for your OK");
            SaveTask(work);
            ShowTask(work);
            OfferOk(work, step);
            return;
        }

        record.Add(AgentEntryKind.HandOff, $"Step {number}: {WorkerLabel(critic)} judges its frames · {why}");
        SaveTask(work);
        ShowTask(work);
        Dispatcher.BeginInvoke(() => _ = StartCritiqueAsync(work, step, critic, run, frames));
    }

    /// <summary>
    /// The art critic judges what an artist's run made for a step: its text, hand-over and saved files, the task's
    /// references. After a redo the critic that sent the step back judges the new frames in its own session.
    /// </summary>
    private Task StartCritiqueAsync(TaskWork work, TeamStep step, Worker critic, AgentRun judged, IReadOnlyList<string> frames)
    {
        var record = work.Record;
        var again = step.CriticSession is { } session && record.Runs.LastOrDefault(run => run.SessionId == session)?.Provider == critic.Provider
            ? session
            : null;
        return StartCriticAsync(work, critic, judged, step.Text, step.HandedOver, [.. judged.Changes.Select(change => Relative(change.Path, record.Workspace))],
            record.Images, frames, again);
    }

    /// <summary>
    /// The art critic, told to change nothing and blind to the artist's account: a new session as the Art Critic with its role's
    /// skills and at its role's effort, told the task, what was handed over and saved, the user's references and the
    /// frames, the sheets among them attached (else the first frames). Its own session after a redo
    /// (<paramref name="again"/>) knows all but the new frames and saved files, and judges them at medium effort. After
    /// its verdict the user's words go on in the session of <paramref name="judged"/>.
    /// </summary>
    private async Task StartCriticAsync(TaskWork work, Worker critic, AgentRun judged, string task, string? handedOver,
        IReadOnlyList<string> saved, IReadOnlyList<string> references, IReadOnlyList<string> frames, string? again = null)
    {
        var request = File.Exists(ArtCritic.RequestFile) ? await File.ReadAllTextAsync(ArtCritic.RequestFile) : string.Empty;
        if (work.Busy || !_works.Contains(work)) return;
        var record = work.Record;
        var project = work.Project;
        var role = ProjectRoles.Find(ArtCritic.RoleId, record.Workspace);
        var (deepModel, deepEffort) = _settings.Current.Agent.Level(critic.Provider, AgentLevel.Deep);
        var model = critic.Model ?? deepModel;
        var efforts = _agents.Provider(critic.Provider).EffortsOf(model);
        string? Has(string? effort) => effort is not null && efforts.Any(each => each.Value == effort) ? effort : null;
        var attached = ArtCritic.Attached(frames, ArtCritic.IsSheet);
        work.Critiqued = judged;
        await StartAgentAsync(work, new AgentTask
        {
            Provider = critic.Provider,
            Workspace = record.Workspace,
            Prompt = again is null
                ? ArtCritic.Prompt(request, task, handedOver, saved, [.. references.Where(File.Exists)], frames, attached)
                : ArtCritic.AgainPrompt(saved, frames, attached),
            ResumeSessionId = again,
            Images = attached,
            Judge = true,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
            // As the artist's run: on Auto it may run the project's checks and renders to see for itself.
            AutoApprove = judged.Task.AutoApprove,
            Model = model,
            // Its role's usual effort where the agent has it: judging frames needs no Deep level's longest thinking, a second look less.
            Effort = (again is null ? null : Has("medium")) ?? Has(role?.Effort) ?? deepEffort,
            RoleId = ArtCritic.RoleId,
            Skills = ProjectRoles.OwnSkills(role, ProjectSkills.In(record.Workspace))
        }, null);
    }

    /// <summary>
    /// The art critic's run on a step ended: its verdict sends the step back to its artist with the defects, lets the team
    /// go on, or leaves the step to the user's OK with what the critic saw; so does a run without a verdict (a Stop). A
    /// question of the critic's waits for the user's answer in its session; after the verdict the user's words go on with
    /// the artist's work.
    /// </summary>
    private void AfterCritique(TaskWork work, TeamStep step, AgentRun run, bool needsYou)
    {
        if (work.Question is not null) return;
        // After a limit too the user's words go on with the artist's work, never in the critic's session.
        if (work.Critiqued is { } judged) work.LastRun = judged;
        work.Critiqued = null;
        if (needsYou) return;
        var record = work.Record;
        step.CriticSession = AgentCoordinator.SessionOf(run) ?? step.CriticSession;
        var number = TeamRunner.Number(record, step);
        var artist = step.Worker;
        var critic = WorkerLabel(new Worker(run.Task.Provider, AgentLevel.Deep, run.Task.Model));
        var review = TeamPlan.SplitReview(run.Result.Summary).Review;
        var evidence = _projects.ReadWorkerEvidence(work.Project);
        var move = TeamRunner.Critiqued(record, step, review ?? new LeadReview(ReviewVerdict.Stop, string.Empty), evidence);
        var score = review?.Score is { } given ? $", {given} of 5" : string.Empty;
        var notes = review?.Notes is { Length: > 0 } said ? " · " + Shorten(said, 200) : string.Empty;
        record.Add(AgentEntryKind.HandOff, review is null ? $"Step {number}: {critic} gave no verdict · it waits for your OK" : move switch
        {
            TeamMove.Redo => $"Step {number}: {critic} sends it back to {artist}{score} ({step.Redos} of {TeamRunner.MaxRedos}){notes}",
            TeamMove.Stopped when review.Verdict == ReviewVerdict.Stop => $"Step {number}: {critic} leaves it to you{score} · it waits for your OK{notes}",
            TeamMove.Stopped => $"Step {number}: {critic} still asks for changes{score} after {Plural(step.Redos, "redo")} · it waits for your OK{notes}",
            _ => $"Step {number}: {critic} accepted the work of {artist}{score}{notes}"
        });
        if (move == TeamMove.Finished) record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
        SaveTask(work);
        ShowTask(work);
        if (move is TeamMove.Next or TeamMove.Redo) GoOnWithTeam(work);
        else if (move is TeamMove.AwaitOk or TeamMove.Stopped) OfferOk(work, step);
    }

    /// <summary>
    /// The user's words for a session, with the art critic's notes on the team's step when that session is the step's
    /// artist's: the critic said them in its own session. Told once, they are the artist's to act on.
    /// </summary>
    private string WithCriticNotes(TaskWork work, string words, string? session)
    {
        if (session is null || TeamRunner.Current(work.Record) is not { CriticNotes: { } notes } step || step.SessionId != session) return words;
        step.CriticNotes = null;
        SaveTask(work);
        return ArtCritic.Heard(notes, words);
    }

    /// <summary>The team's lead as the thread names it: the model of its planning run.</summary>
    private string LeadName(AgentTaskRecord record) =>
        record.Runs.FirstOrDefault(run => run.SessionId == record.LeadSession) is { } lead
            ? WorkerLabel(new Worker(lead.Provider, AgentLevel.Deep, lead.Model))
            : "The lead";

    /// <summary>
    /// A step only the user can judge (a look, a feel), with what the art critic saw in it; or one the critic did not get
    /// to judge: their OK lets the team go on.
    /// </summary>
    private void OfferOk(TaskWork work, TeamStep step)
    {
        var record = work.Record;
        var number = TeamRunner.Number(record, step);
        var critic = step.CriticNotes is { } notes ? $" The art critic: {Shorten(notes, 400)}" : string.Empty;
        OfferChoice(work,
            step.Status == TeamStepStatus.Critique
                ? $"The art critic did not judge step {number}: {Shorten(step.Text, 160)}. Look at it and go on; your words instead go " +
                  "to its artist, and the critic judges what it makes then."
                : step.Unverified
                ? $"Step {number} is called done, but no file changed: {Shorten(step.Text, 160)}. Look in the editor; OK goes on without " +
                  "counting it for its worker; your words instead go to its session, such as to save the work."
                : $"Step {number} is done and only you can judge it: {Shorten(step.Text, 160)}.{critic} Look at it, then go on; " +
                  "your words instead go to its session to change it.",
            "OK, next step",
            canRemember: false,
            () =>
            {
                record.Add(AgentEntryKind.You, $"OK for step {number}");
                if (TeamRunner.Approve(record, step) == TeamMove.Finished) record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
                SaveTask(work);
                ShowTask(work);
                GoOnWithTeam(work);
            });
    }

    /// <summary>
    /// The next step starts once the run that ended is wholly over; words the user queued for after it go first, and a
    /// finished task's team goes no further.
    /// </summary>
    private void GoOnWithTeam(TaskWork work) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (work.Busy || work.Waiting is not null || !_works.Contains(work) || work.Queued is not null || work.SteerWith is not null) return;
            if (TeamRunner.Current(work.Record) is { Status: TeamStepStatus.Waiting or TeamStepStatus.Running } step)
            {
                _ = StartTeamStepAsync(work, step);
            }
        });

    /// <summary>
    /// A team that stopped mid-way (a Stop, a limit, Link restarted): "Go on" runs the step it is on in a new session. A
    /// team that has not started yet is offered its run, with the copy before its first edits. A failed step is offered
    /// again, and its worker chip in Plan changes the worker, skips it or re-plans.
    /// </summary>
    private void OfferTeamGoOn(TaskWork work)
    {
        var record = work.Record;
        if (work.Busy || TeamRunner.Current(record) is not { } step) return;
        if (step.Status == TeamStepStatus.Failed)
        {
            var failed = TeamRunner.Number(record, step);
            OfferChoice(work, $"Step {failed} failed: {Shorten(step.Text, 160)} ({step.Worker}). Try it again, or use its worker chip in Plan " +
                              "to give it another worker, skip it or re-plan the steps left.", "Try it again", canRemember: false, () =>
            {
                record.Add(AgentEntryKind.You, $"Try step {failed} again");
                TeamRunner.RunAgain(step);
                SaveTask(work);
                _ = StartTeamStepAsync(work, step);
            });
            return;
        }

        if (step.Status is TeamStepStatus.AwaitsOk or TeamStepStatus.Critique)
        {
            OfferOk(work, step);
            return;
        }

        if (step.Status == TeamStepStatus.Reviewing)
        {
            OfferReviewDecision(work, step, $"The team stopped while the lead reviewed step {TeamRunner.Number(record, step)}.");
            return;
        }

        var number = TeamRunner.Number(record, step);
        var fresh = record.Team.All(each => each.Status == TeamStepStatus.Waiting);
        OfferChoice(work, fresh
                ? $"The team plan is in Plan above: {Plural(record.Team.Count, "step")}. Run the team?"
                : $"The team stopped at step {number} of {record.Team.Count}: {Shorten(step.Text, 160)} ({step.Worker}). Go on with it in a new session?",
            fresh ? "Run team" : "Go on with the team", canRemember: false, () =>
            {
                record.Add(AgentEntryKind.You, fresh ? "Run team" : $"Go on with step {number}");
                _ = StartTeamStepAsync(work, step);
            });
    }

    /// <summary>A team's plan for the task's plan card, its steps' specialists by name.</summary>
    private IReadOnlyList<AgentPlanItem> TeamPlanItems(AgentTaskRecord record) =>
        TeamRunner.PlanItems(record, id => ProjectRoles.Find(id, record.Workspace)?.Name);

    /// <summary>
    /// The menu of a team's step on the plan card: who does it (an agent set up, at a level, or Link's pick), then what
    /// can be done with it where it stands: run it again, skip it, go back to it, re-plan the steps left. While a run is
    /// under way only the steps after the one it works on change. After a change the team goes on by the user's word.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<PanelAction>> TeamStepMenu(AgentTaskRecord record, int index)
    {
        if (_works.FirstOrDefault(each => each.Record == record) is not { } work || index >= record.Team.Count) return [];
        var step = record.Team[index];
        var number = index + 1;
        var current = TeamRunner.Current(record);
        var busy = work.Busy || work.Waiting is not null;
        if (busy && (current is null || index <= record.Team.IndexOf(current)))
        {
            return [[new PanelAction("Stop the run first to change this step", "While a run works on a step, only the steps after it change", () => { })]];
        }

        var groups = new List<IReadOnlyList<PanelAction>>();
        if (!TeamRunner.Over(step)) groups.Add(WorkerChoices(work, step));
        var actions = new List<PanelAction>();
        if (step.Status == TeamStepStatus.Failed || step == current && step.Status != TeamStepStatus.Waiting)
        {
            actions.Add(new PanelAction("Run it again from the start", "A new session of its worker, told an earlier try's changes may be in the workspace",
                () => ChangeTeam(work, $"Step {number} again from the start", () => TeamRunner.RunAgain(step))));
        }

        if (!TeamRunner.Over(step))
        {
            var needing = TeamRunner.Needing(record, step);
            actions.Add(new PanelAction(
                needing.Count == 0 ? "Skip this step" : $"Skip this step (step {string.Join(", ", needing)} uses its files)",
                "The team is through it with nothing done; the next worker is told it was skipped",
                () => ChangeTeam(work, $"Skip step {number}", () =>
                {
                    if (TeamRunner.Skip(record, step) == TeamMove.Finished) record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
                })));
        }
        else if (!busy)
        {
            actions.Add(new PanelAction("Back to this step", "It and the steps after it run again; the files stay as they are, and its worker checks what is there",
                () => ChangeTeam(work, $"Back to step {number}", () => TeamRunner.BackTo(record, step))));
        }

        groups.Add(actions);
        if (!busy)
        {
            groups.Add([new PanelAction("Re-plan the steps left…", "Your next words go to the team's lead: the steps done stay, it plans the rest anew", () =>
            {
                work.Replanning = true;
                ShowTask(work);
            })]);
        }

        return groups;
    }

    /// <summary>
    /// Who can do the step: Link's pick by the plan, or each agent found at each of its levels, with the model and effort
    /// Settings give that level. The step's own choice is checked.
    /// </summary>
    private IReadOnlyList<PanelAction> WorkerChoices(TaskWork work, TeamStep step)
    {
        var record = work.Record;
        var settings = _settings.Current.Agent;
        var number = TeamRunner.Number(record, step);
        var route = Router(_projects.ReadWorkerEvidence(work.Project));
        var choices = new List<PanelAction>
        {
            new("Link picks the worker", "By the plan's tier and the kind of step, as Settings and the project's record stand",
                () => ChangeTeam(work, $"Step {number}: Link picks the worker", () => TeamRunner.Choose(step, null, route, WorkerLabel)), !step.Pinned)
        };
        foreach (var provider in new[] { AgentProviderKind.ClaudeCode, AgentProviderKind.Codex }.Where(each => WorkerStateOf(each) != WorkerState.NotSet))
        {
            foreach (var level in Enum.GetValues<AgentLevel>())
            {
                var (model, effort) = settings.Level(provider, level);
                var worker = new Worker(provider, level, model);
                var label = $"{ShortName(provider)} {level}: {SetupWords(provider, model, effort)}";
                choices.Add(new PanelAction(label, WorkerStateOf(provider) == WorkerState.LimitReached ? "At its usage limit now" : "This agent at this level, kept on a tier up",
                    () => ChangeTeam(work, $"Step {number} → {label}", () => TeamRunner.Choose(step, worker, route, WorkerLabel)),
                    step.Pinned && step.Provider == provider && step.Level == level));
            }
        }

        return choices;
    }

    /// <summary>A change the user made to the team's plan: told in the thread and kept; a team at rest then waits for the user's word to go on.</summary>
    private void ChangeTeam(TaskWork work, string said, Action change)
    {
        change();
        work.Record.Add(AgentEntryKind.Note, said);
        SaveTask(work);
        ShowTask(work);
        if (!work.Busy && work.Waiting is null) OfferTeamGoOn(work);
    }

    /// <summary>
    /// The user's words to re-plan the steps left go to the lead's own session, as a plan only run: its plan replaces the
    /// steps the team is not through (<see cref="OfferTeam"/>). Without that session, or with one grown big or gone
    /// cold, a fresh session of the team's strong worker starts from the task's state and is told how to plan.
    /// </summary>
    private async Task StartReplanAsync(TaskWork work, string words, IReadOnlyList<string> images)
    {
        var record = work.Record;
        var settings = _settings.Current.Agent;
        var lead = record.Runs.LastOrDefault(run => run.SessionId == record.LeadSession);
        var fresh = lead is null || CompactRule.ForStep(lead, settings.AutoCompact, settings.ContextBudget, DateTimeOffset.Now) is not null;
        var request = fresh && File.Exists(TeamPlan.TaskFile) ? await File.ReadAllTextAsync(TeamPlan.TaskFile) : null;
        if (work.Busy || !_works.Contains(work)) return;
        var provider = lead?.Provider ?? WorkerRouting.Workers(settings, WorkerStateOf)[WorkerTier.Strong].Provider;
        var (model, effort) = lead is not null ? (lead.Model, lead.Effort) : settings.Level(provider, AgentLevel.Deep);
        var prompt = TeamPlan.ReplanPrompt(words, record, request, ProjectRoles.In(record.Workspace));
        record.Add(AgentEntryKind.You, "(re-plan) " + words + ImagesNote(images));
        if (fresh) record.Add(AgentEntryKind.HandOff, "The lead re-plans in a fresh session from the task's state");
        await StartAgentAsync(work, new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = fresh ? $"{TaskState.FreshBrief(record, null, record.Workspace, "Re-plan the team's steps left, as asked below.", AgentName)}\n\n{prompt}" : prompt,
            ResumeSessionId = fresh ? null : record.LeadSession,
            PlanOnly = true,
            Images = images,
            AllowedTools = AgentCoordinator.Tools(work.Project, record.Workspace),
            AutoApprove = record.Runs.FirstOrDefault()?.AutoApprove ?? settings.DefaultAccess == AgentAccessChoice.Auto,
            Model = model,
            Effort = effort
        }, null);
    }

    /// <summary>Routing as Settings, the project's evidence and the agents' state stand now.</summary>
    private TeamRunner.Router Router(WorkerEvidence evidence) =>
        (kind, asked, agent) => WorkerRouting.Route(kind, asked, _settings.Current.Agent, WorkerStateOf, evidence, ShortName,
            provider => _agents.Provider(provider).Capabilities.Images, agent);

    /// <summary>Whether an agent can take a step now: found, and not at its usage limit.</summary>
    private WorkerState WorkerStateOf(AgentProviderKind provider)
    {
        var agent = _agents.Provider(provider);
        if (agent is CliAgentProvider { } cli && cli.FindExecutable() is null) return WorkerState.NotSet;
        return _limits.ReachedUntil(provider) is not null ? WorkerState.LimitReached : WorkerState.Ready;
    }

    /// <summary>A worker as the plan names it: Claude Code by its model ("Haiku 5.5"), Codex by name.</summary>
    private string WorkerLabel(Worker worker) =>
        worker.Provider == AgentProviderKind.ClaudeCode && worker.Model is { } model ? ModelName(_agents.Provider(worker.Provider), model) : ShortName(worker.Provider);
}
