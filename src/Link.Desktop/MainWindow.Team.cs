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
        // The lead's session goes on: it reviews what the workers do.
        record.LeadSession = AgentCoordinator.SessionOf(run);
        record.Add(AgentEntryKind.HandOff, $"Team plan: {Plural(record.Team.Count, "step")} · " +
                                           string.Join(", ", record.Team.GroupBy(step => step.Worker).Select(group => $"{group.Key} {group.Count()}")));
        OfferTeamGoOn(record);
        return true;
    }

    /// <summary>
    /// A step's run: a new session of its worker, as the step's specialist with the skills its words call for, told the
    /// task's state, what the steps before handed over and the step; an art step sees the images the task began with. On
    /// Auto, Link picks the effort from the step's words. A step the lead or the art critic sent back goes on in its
    /// worker's own session, told their notes.
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
        var again = step.ReviewNotes is { } notes ? TeamPlan.RedoPrompt(notes) : step.CriticNotes is { } defects ? ArtCritic.RedoPrompt(defects) : null;
        var redo = again is not null && step.SessionId is { } session ? (Prompt: again, Session: session) : default;
        var prompt = redo.Session is null ? TaskState.StepBrief(record, null, record.Workspace, step, AgentName) : redo.Prompt;
        step.ReviewNotes = null;
        step.CriticNotes = null;
        step.Status = TeamStepStatus.Running;
        if (redo.Session is null)
        {
            record.Add(AgentEntryKind.HandOff,
                $"Step {number} → {step.Worker}{(role is null ? string.Empty : " as " + role.Name)}: {TeamPlan.Headline(step.Text).Title} · {step.Reason}");
        }

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
            Prompt = prompt,
            ResumeSessionId = redo.Session,
            // The user's references; a session sent back has them already.
            Images = StepKinds.IsArt(step.Kind) && redo.Session is null ? [.. record.Images.Where(File.Exists)] : [],
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
        if (redo.Session is null) _agentOrigin = task;
        await StartAgentAsync(task, null, phase: EffortPhase.New);
    }

    /// <summary>
    /// After a run of the step under way: the step is done, goes to the art critic or the lead's review, waits for the
    /// user's OK, is tried again, goes a tier up, or the team stops; the next step starts by itself once this call is
    /// over. A run that asked the user something waits for their answer in its session, and one that was stopped or hit a
    /// limit waits for "Go on". A run while the step is under review is the lead's, and one while it is judged the
    /// critic's: their verdict decides the step.
    /// </summary>
    private void AfterTeamStep(AgentTaskRecord record, AgentRun run, bool needsYou)
    {
        if (run.Task.PlanOnly || TeamRunner.Current(record) is not { } step) return;
        // Words to a step that waited for the OK changed it: it waits for the OK again. Files changed now show its work,
        // and the critic's notes are of the work before.
        if (step.Status == TeamStepStatus.AwaitsOk)
        {
            if (run.Changes.Count > 0 && (step.Unverified || step.CriticNotes is not null))
            {
                step.Unverified = false;
                step.CriticNotes = null;
                SaveTask(record);
            }

            if (!needsYou) OfferOk(record, step);
            return;
        }

        if (step.Status == TeamStepStatus.Reviewing)
        {
            var lead = run.Task.ResumeSessionId == record.LeadSession || AgentCoordinator.SessionOf(run) == record.LeadSession;
            if (lead) AfterReview(record, step, run, needsYou);
            // Words the user left the worker for after its run went first: the lead reviews what it did then.
            else if (!needsYou) Dispatcher.BeginInvoke(() => _ = StartLeadReviewAsync(record, step, run));
            return;
        }

        if (step.Status == TeamStepStatus.Critique)
        {
            if (ArtCritic.Judges(run.Task)) AfterCritique(record, step, run, needsYou);
            // Words the user left the artist for after its run went first: the critic judges what it made then.
            else if (!needsYou) CritiqueAgain(record, step, run);
            return;
        }

        if (step.Status != TeamStepStatus.Running) return;
        var number = TeamRunner.Number(record, step);
        var before = step.Worker;
        step.SessionId = AgentCoordinator.SessionOf(run) ?? step.SessionId;
        var verdict = needsYou ? null : WorkerEvidence.Verdict(record.LastRun!);
        var failure = run.Check is { Passed: false } check ? $"the check failed: {check}" : run.Result.Summary;
        var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
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
        SaveTask(record);
        ShowTask();
        switch (move)
        {
            case TeamMove.Critique:
                var (critic, frames) = (art.Critic!, art.Frames);
                Dispatcher.BeginInvoke(() => _ = StartCritiqueAsync(record, step, critic, run, frames));
                break;
            case TeamMove.Review:
                Dispatcher.BeginInvoke(() => _ = StartLeadReviewAsync(record, step, run));
                break;
            case TeamMove.Next or TeamMove.Retry or TeamMove.Escalated:
                GoOnWithTeam(record);
                break;
            case TeamMove.AwaitOk or TeamMove.Unverified:
                OfferOk(record, step);
                break;
            case TeamMove.Paused when !needsYou:
                OfferTeamGoOn(record);
                break;
        }
    }

    /// <summary>
    /// The lead reviews the step a worker has done, in its own session and read only: the worker's answer, the check and
    /// the diff (<paramref name="workerRun"/>), else what the task's record kept of the worker's last run, with the files
    /// to read. Medium effort: a review reads a diff, it does not plan.
    /// </summary>
    private async Task StartLeadReviewAsync(AgentTaskRecord record, TeamStep step, AgentRun? workerRun)
    {
        if (AgentRunning || _task != record || record.Runs.FirstOrDefault(run => run.SessionId == record.LeadSession) is not { } lead) return;
        var project = _taskProject ?? _projects.ActiveProject;
        var kept = record.Runs.LastOrDefault(run => run.SessionId == step.SessionId);
        var answer = workerRun?.Result.Summary ?? kept?.Words ?? string.Empty;
        var files = workerRun?.Changes.Select(change => Relative(change.Path)).ToArray() ?? [.. kept?.Files ?? []];
        var diff = workerRun is { } done
            ? await _agents.Changes.DiffAsync(done.Before, done.Changes, stat: false, ReviewDiff, CancellationToken.None)
            : null;
        var check = workerRun?.Check is { } result ? (result.Passed ? "passed" : result.ToString()) : null;
        var request = File.Exists(TeamPlan.ReviewFile) ? await File.ReadAllTextAsync(TeamPlan.ReviewFile) : string.Empty;
        if (AgentRunning || _task != record) return;
        var efforts = _agents.Provider(lead.Provider).EffortsOf(lead.Model);
        await StartAgentAsync(new AgentTask
        {
            Provider = lead.Provider,
            Workspace = record.Workspace,
            Prompt = TeamPlan.ReviewPrompt(request, TeamRunner.Number(record, step), step, answer, check, files, diff),
            ResumeSessionId = record.LeadSession,
            Access = AgentAccess.Analyze,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace, AgentAccess.Analyze),
            Model = lead.Model,
            Effort = efforts.Any(effort => effort.Value == "medium") ? "medium" : lead.Effort
        }, null);
    }

    /// <summary>
    /// The lead's run on a step under review ended: its verdict moves the team on, sends the step back or up, or stops
    /// the team. Without a verdict (a Stop, a limit, a question, words of the user's) the user decides.
    /// </summary>
    private void AfterReview(AgentTaskRecord record, TeamStep step, AgentRun run, bool needsYou)
    {
        record.LeadSession = AgentCoordinator.SessionOf(run) ?? record.LeadSession;
        var number = TeamRunner.Number(record, step);
        var worker = step.Worker;
        var lead = LeadName(record);
        if (needsYou || TeamPlan.SplitReview(run.Result.Summary).Review is not { } review)
        {
            SaveTask(record);
            if (!needsYou) OfferReviewDecision(record, step, $"{lead}'s review of step {number} ended without a verdict.");
            return;
        }

        var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
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
        SaveTask(record);
        ShowTask();
        if (move is TeamMove.Next or TeamMove.Redo or TeamMove.Escalated) GoOnWithTeam(record);
        else if (move == TeamMove.Stopped) OfferReviewDecision(record, step, $"{lead} stopped the team at step {number}{notes}.");
    }

    /// <summary>
    /// A step under review that waits for the user: accept it and go on, or tell the lead what to do: the words go to its
    /// session, and its next verdict decides the step.
    /// </summary>
    private void OfferReviewDecision(AgentTaskRecord record, TeamStep step, string why)
    {
        var number = TeamRunner.Number(record, step);
        _agentPanel!.ShowDecision($"{why} Accept step {number} and go on, or tell the lead what to do.", "Accept and go on", canRemember: false);
        _decision = () =>
        {
            record.Add(AgentEntryKind.You, $"Accept step {number}");
            var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
            if (TeamRunner.Reviewed(record, step, new LeadReview(ReviewVerdict.Accept, string.Empty), Router(evidence), WorkerLabel, evidence) == TeamMove.Finished)
            {
                record.Add(AgentEntryKind.HandOff, "The team is through all its steps");
            }

            SaveTask(record);
            ShowTask();
            GoOnWithTeam(record);
        };
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
    private void CritiqueAgain(AgentTaskRecord record, TeamStep step, AgentRun run)
    {
        var number = TeamRunner.Number(record, step);
        var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
        var (critic, why, frames) = CritiqueOf(record, step, run, evidence);
        if (critic is null)
        {
            TeamRunner.Critiqued(record, step, new LeadReview(ReviewVerdict.Stop, string.Empty), evidence);
            record.Add(AgentEntryKind.HandOff, $"Step {number}: {why ?? "no art critic judged it"} · it waits for your OK");
            SaveTask(record);
            ShowTask();
            OfferOk(record, step);
            return;
        }

        record.Add(AgentEntryKind.HandOff, $"Step {number}: {WorkerLabel(critic)} judges its frames · {why}");
        SaveTask(record);
        ShowTask();
        Dispatcher.BeginInvoke(() => _ = StartCritiqueAsync(record, step, critic, run, frames));
    }

    /// <summary>The art critic judges what an artist's run made for a step: its text, hand-over and saved files, the task's references.</summary>
    private Task StartCritiqueAsync(AgentTaskRecord record, TeamStep step, Worker critic, AgentRun judged, IReadOnlyList<string> frames) =>
        StartCriticAsync(record, critic, judged, step.Text, step.HandedOver, [.. judged.Changes.Select(change => Relative(change.Path))], record.Images,
            frames);

    /// <summary>
    /// A new session of the art critic: read only, as the Art Critic with its skills and at its role's effort, told the
    /// task, what was handed over and saved, the user's references and the frames, the first of them attached; never the
    /// artist's account. After its verdict the user's words go on in the session of <paramref name="judged"/>.
    /// </summary>
    private async Task StartCriticAsync(AgentTaskRecord record, Worker critic, AgentRun judged, string task, string? handedOver,
        IReadOnlyList<string> saved, IReadOnlyList<string> references, IReadOnlyList<string> frames)
    {
        var request = File.Exists(ArtCritic.RequestFile) ? await File.ReadAllTextAsync(ArtCritic.RequestFile) : string.Empty;
        if (AgentRunning || _task != record) return;
        var project = _taskProject ?? _projects.ActiveProject;
        var role = ProjectRoles.Find(ArtCritic.RoleId, record.Workspace);
        var (deepModel, deepEffort) = _settings.Current.Agent.Level(critic.Provider, AgentLevel.Deep);
        var model = critic.Model ?? deepModel;
        var efforts = _agents.Provider(critic.Provider).EffortsOf(model);
        _critiqued = judged;
        await StartAgentAsync(new AgentTask
        {
            Provider = critic.Provider,
            Workspace = record.Workspace,
            Prompt = ArtCritic.Prompt(request, task, handedOver, saved, [.. references.Where(File.Exists)], frames),
            Images = [.. frames.Take(ArtCritic.MostFrames)],
            Access = AgentAccess.Analyze,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace, AgentAccess.Analyze),
            Model = model,
            // Its role's usual effort where the agent has it: judging frames needs no Deep level's longest thinking.
            Effort = role?.Effort is { } usual && efforts.Any(effort => effort.Value == usual) ? usual : deepEffort,
            RoleId = ArtCritic.RoleId,
            Skills = ProjectRoles.SkillsFor(task, role, ProjectSkills.In(record.Workspace))
        }, null);
    }

    /// <summary>
    /// The art critic's run on a step ended: its verdict sends the step back to its artist with the defects, lets the team
    /// go on, or leaves the step to the user's OK with what the critic saw; so does a run without a verdict (a Stop). A
    /// question of the critic's waits for the user's answer in its session; after the verdict the user's words go on with
    /// the artist's work.
    /// </summary>
    private void AfterCritique(AgentTaskRecord record, TeamStep step, AgentRun run, bool needsYou)
    {
        if (needsYou) return;
        if (_critiqued is { } judged) _lastRun = judged;
        _critiqued = null;
        var number = TeamRunner.Number(record, step);
        var artist = step.Worker;
        var critic = WorkerLabel(new Worker(run.Task.Provider, AgentLevel.Deep, run.Task.Model));
        var review = TeamPlan.SplitReview(run.Result.Summary).Review;
        var evidence = _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject);
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
        SaveTask(record);
        ShowTask();
        if (move is TeamMove.Next or TeamMove.Redo) GoOnWithTeam(record);
        else if (move is TeamMove.AwaitOk or TeamMove.Stopped) OfferOk(record, step);
    }

    /// <summary>
    /// The user's words for a session, with the art critic's notes on the team's step when that session is the step's
    /// artist's: the critic said them in its own session. Told once, they are the artist's to act on.
    /// </summary>
    private string WithCriticNotes(string words, string? session)
    {
        if (session is null || _task is not { } record || TeamRunner.Current(record) is not { CriticNotes: { } notes } step || step.SessionId != session)
        {
            return words;
        }

        step.CriticNotes = null;
        SaveTask(record);
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
    private void OfferOk(AgentTaskRecord record, TeamStep step)
    {
        var number = TeamRunner.Number(record, step);
        var critic = step.CriticNotes is { } notes ? $" The art critic: {Shorten(notes, 400)}" : string.Empty;
        _agentPanel!.ShowDecision(
            step.Status == TeamStepStatus.Critique
                ? $"The art critic did not judge step {number}: {Shorten(step.Text, 160)}. Look at it and go on; your words instead go " +
                  "to its artist, and the critic judges what it makes then."
                : step.Unverified
                ? $"Step {number} is called done, but no file changed: {Shorten(step.Text, 160)}. Look in the editor; OK goes on without " +
                  "counting it for its worker; your words instead go to its session, such as to save the work."
                : $"Step {number} is done and only you can judge it: {Shorten(step.Text, 160)}.{critic} Look at it, then go on; " +
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
        if (step.Status is TeamStepStatus.AwaitsOk or TeamStepStatus.Critique)
        {
            OfferOk(record, step);
            return;
        }

        if (step.Status == TeamStepStatus.Reviewing)
        {
            OfferReviewDecision(record, step, $"The team stopped while the lead reviewed step {TeamRunner.Number(record, step)}.");
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
