using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// A task goes on in another session: compacted, started over with a note, escalated, handed to the other agent and back.
public partial class MainWindow
{
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
        return StartAgentAsync(work, task, work.LastRun, phase: phase);
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
        var asker = run.Task.Provider;
        var (model, effort) = _settings.Current.Agent.OwnAnswers(asker);
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
            next = await _agents.StartOverAsync(run, origin, (model, effort), cancellation.Token);
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

    /// <summary>
    /// Hand off ▾: the other agent in a new session (with a brief to look at first), or the first agent back in its own
    /// session. Another model, effort or specialist for the same session is the composer's choice (<see cref="ShowSetup"/>).
    /// </summary>
    private IReadOnlyList<PanelAction> HandOffActions(TaskWork work)
    {
        if (ResumableTask(work) is not { } resume) return [];
        var current = resume.Task.Provider;
        var others = _agents.Providers.Where(agent => agent.Kind != current).Select(agent => agent.Kind).ToArray();
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
}
