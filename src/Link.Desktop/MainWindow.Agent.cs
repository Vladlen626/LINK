using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Agent: a CLI agent (Claude Code or Codex) works in the project's workspace. A confirmation card in the panel comes
// first; then Activity shows its steps, the files it changed and how it ended, with Stop, decisions and undo.
public partial class MainWindow
{
    // Verbs that ask for work rather than an answer; they only light the Agent hint, the route stays as it is.
    private static readonly Regex ActionVerbs = new(
        @"\b(исправь|почини|сделай|добавь|удали|переименуй|перенеси|экспортируй|запусти|создай|собери|обнови|напиши тест|" +
        @"fix|make|add|remove|delete|rename|move|export|run|create|build|update|refactor|implement)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan ActivityFlush = TimeSpan.FromMilliseconds(100);
    private readonly AgentCoordinator _agents;
    private readonly List<AgentEvent> _pendingAgentEvents = [];
    private AgentPanel? _agentPanel;
    private bool _panelShowsAgent;
    private InputSnapshot? _agentSnapshot;
    private AgentRun? _lastRun;
    private AgentTask? _runningTask;
    // The task as the card or the Map button started it: what "start over" starts again, whatever runs continued it.
    private AgentTask? _agentOrigin;
    // An earlier session opened from the project panel: the next reply takes it up again.
    private AgentTask? _earlierSession;
    // Words the user gave while the agent worked: the stopped session goes on with them.
    private string? _steerWith;
    private bool _restartWhenStopped;
    private bool _backupBeforeDo = true;
    private CancellationTokenSource? _agentCancellation;
    private DispatcherTimer? _activityTimer;
    private Stopwatch _agentClock = new();
    private Action? _decision;
    private bool _rememberAllowance;
    // Files the running agent reported changed, listed until git or the copy tells the whole list.
    private readonly HashSet<string> _runningFiles = new(StringComparer.OrdinalIgnoreCase);

    private bool AgentRunning => _agentCancellation is not null;

    private AgentProviderKind ProjectProvider =>
        _projects.ActiveProject.AgentProvider ?? _settings.Current.Agent.DefaultProvider;

    private static string ShortName(AgentProviderKind provider) => provider == AgentProviderKind.ClaudeCode ? "Claude" : "Codex";

    /// <summary>Enter on the Agent route, Ctrl+Enter or the hint: the input goes to the card, not anywhere yet.</summary>
    private async Task OpenAgentCardFromPromptAsync()
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        RemoveMissingContextFiles();
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            SetError("Write what the agent should do.");
            return;
        }

        var project = _projects.ActiveProject;
        var snapshot = await TakeSnapshotAsync(project, PromptBox.Text, RequestRoute.Agent);
        _attempts.Keep(snapshot);
        OpenAgentCard(snapshot);
    }

    /// <summary>
    /// The workspace's Map button: the agent looks through the folder read only and Link writes its answer to
    /// docs/map.md, the one change, shown and undone like any other. The same button updates the map.
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

        var agent = _settings.Current.Agent;
        _agentOrigin = new AgentTask
        {
            Provider = ProjectProvider,
            Workspace = workspace,
            Prompt = prompt,
            AllowedTools = project.AgentReadTools,
            AnswerFile = ProjectMap.RelativePath,
            Model = agent.ModelFor(ProjectProvider),
            Effort = agent.EffortFor(ProjectProvider)
        };
        await StartAgentAsync(_agentOrigin, null);
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
        // Start over belongs to the work, not to its skill.
        _agentOrigin = null;
        await StartAgentAsync(task, null);
    }

    /// <summary>A skill of the same name is written over only when the user says so.</summary>
    private bool ReplaceSkill(string name) =>
        MessageBox.Show(this, $"The workspace has the skill {name} already. Replace it with the new one?",
            "Link · save as skill", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OpenAgentCard(InputSnapshot snapshot)
    {
        _agentSnapshot = snapshot;
        var project = _projects.ActiveProject;
        var attachments = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>().Select(item => item.Label).ToArray();
        var settings = _settings.Current.Agent;
        var card = new AgentCardModel(
            _agents.Providers.Select(provider => (provider.Kind, provider.Name, provider.Efforts)).ToArray(),
            ProjectProvider,
            project.Name,
            project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null,
            project.WorkspaceRoot is { } folder && HasGit(folder),
            attachments.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", attachments),
            snapshot.Text,
            project.CheckCommand,
            _agents.Providers
                .Where(provider => settings.EffortFor(provider.Kind) is not null)
                .ToDictionary(provider => provider.Kind, provider => settings.EffortFor(provider.Kind)!),
            settings.DefaultAccess);
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        SetStatus("Check the card and press Run: nothing runs before that");
    }

    private void ShowAgentPanel()
    {
        if (!_settings.Current.SidePanel.IsOpen) SetPanelOpen(true);
        ShowPanel();
        if (_panel is null) return;
        _agentPanel ??= CreateAgentPanel();
        _panelShowsAgent = true;
        _panel.SetContent("Agent", _agentPanel);
    }

    private AgentPanel CreateAgentPanel()
    {
        var panel = new AgentPanel();
        panel.RunRequested += plan => _ = RunAgentAsync(plan);
        panel.CardCancelled += CloseAgentPanel;
        panel.Closed += CloseAgentPanel;
        panel.StopRequested += () => _agentCancellation?.Cancel();
        panel.DecisionTaken += always =>
        {
            _rememberAllowance = always;
            _decision?.Invoke();
        };
        panel.ToChatRequested += AgentResultToChat;
        panel.UndoRequested += () => _ = UndoAgentChangesAsync();
        panel.RestartRequested += RequestRestart;
        panel.SaveSkillRequested += () => _ = SaveSkillAsync();
        panel.ContinueRequested += ContinueAgent;
        panel.OpenFileRequested += ShowFile;
        return panel;
    }

    /// <summary>Back to the project panel; a running agent goes on and Expand or the hint brings Activity back.</summary>
    private void CloseAgentPanel()
    {
        _panelShowsAgent = false;
        RefreshProjectPanel();
    }

    private async Task RunAgentAsync(AgentPlan plan)
    {
        if (_agentSnapshot is not { } snapshot || AgentRunning) return;
        var project = _projects.ActiveProject;
        if (project.AgentProvider != plan.Provider)
        {
            project.AgentProvider = plan.Provider;
            _projects.Save();
            UpdateRouteButton();
        }

        // The effort chosen on the card stays for the agent; the model comes from Settings.
        var agent = _settings.Current.Agent;
        if (agent.EffortFor(plan.Provider) != plan.Effort)
        {
            agent.SetEffort(plan.Provider, plan.Effort);
            _settings.Save();
        }

        _backupBeforeDo = plan.BackupBeforeDo;
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        _agentOrigin = AgentCoordinator.BuildTask(plan with { Model = agent.ModelFor(plan.Provider) }, snapshot, project);
        await StartAgentAsync(_agentOrigin, null);
    }

    /// <summary>
    /// The user's reply: the same session goes on with it, and undo still covers the whole exchange; Start over keeps
    /// the task the exchange began with. An earlier session opened from the panel is taken up again in its folder,
    /// with a copy before edits where there is no git, as the card makes by default.
    /// </summary>
    private void ContinueAgent(string reply)
    {
        if (reply.Trim() is not { Length: > 0 } text) return;
        if (AgentRunning)
        {
            // Words while the agent works: it stops, and the session goes on with them once it has ended. Link's own
            // step (a note for Start over) takes none.
            if (_runningTask is null || _restartWhenStopped) return;
            _steerWith = text;
            _agentCancellation?.Cancel();
            SetStatus("Stopping the agent to pass your words on…");
            return;
        }

        if (_earlierSession is { } earlier)
        {
            _agentOrigin = earlier with { ResumeSessionId = null };
            _backupBeforeDo = true;
            _ = StartAgentAsync(earlier with { Prompt = text }, null);
            return;
        }

        if (_lastRun is { } run && AgentCoordinator.ContinueTask(run, text) is { } task) _ = StartAgentAsync(task, run);
    }

    /// <summary>
    /// Keeps the run's session in the project, so that it can be taken up again after Link restarts; a map or a skill,
    /// written by Link from one answer, is no session to go on with.
    /// </summary>
    private void RememberAgentSession(LinkProject project, AgentRun run)
    {
        var task = run.Task;
        if (run.Result.SessionId is not { } session || task.AnswerFile is not null || task.AnswerIsSkill) return;
        try
        {
            var earlier = _projects.ReadAgentSessions(project)
                .FirstOrDefault(record => record.SessionId == session || record.SessionId == task.ResumeSessionId);
            // The goal is the card's, the first paragraph of the task: a continued run's prompt is only the reply.
            var goal = earlier?.Goal ?? (_agentOrigin ?? task).Prompt.Split("\n\n")[0].Trim();
            var summary = run.Result.Summary.Trim() is { Length: > 0 } text ? text : run.Result.Stopped ? "Stopped." : "No answer.";
            var sessions = _projects.RememberAgentSession(project, new AgentSessionRecord
            {
                SessionId = session,
                Provider = task.Provider,
                Workspace = task.Workspace,
                Goal = goal.Length <= 300 ? goal : goal[..300] + "…",
                Access = task.Access,
                AutoApprove = task.AutoApprove,
                Model = task.Model,
                Effort = task.Effort,
                Summary = summary.Length <= 4000 ? summary : summary[..4000] + "…"
            }, task.ResumeSessionId);
            if (project == _projects.ActiveProject) _projectPanel?.ShowAgentSessions(sessions.Select(ToSessionRow));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsReported?.Invoke($"Agent session not saved: {exception.Message}");
        }
    }

    /// <summary>An earlier session from the project panel: its last result and the reply box; a reply takes it up again.</summary>
    private void OpenAgentSession(AgentSessionRecord session)
    {
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        if (!Directory.Exists(session.Workspace))
        {
            SetError($"The session's folder is gone: {session.Workspace}");
            return;
        }

        var project = _projects.ActiveProject;
        var tools = session.Access == AgentAccess.Do ? project.AgentReadTools.Concat(project.AgentDoTools) : project.AgentReadTools;
        _earlierSession = new AgentTask
        {
            Provider = session.Provider,
            Workspace = session.Workspace,
            Prompt = session.Goal,
            Access = session.Access,
            AutoApprove = session.AutoApprove,
            Model = session.Model,
            Effort = session.Effort,
            AllowedTools = tools.Distinct(StringComparer.Ordinal).ToArray(),
            CheckCommand = project.CheckCommand,
            ResumeSessionId = session.SessionId
        };
        ShowAgentPanel();
        _agentPanel!.ShowEarlierSession(
            $"{_agents.Provider(session.Provider).Name} · {SessionAccess(session)} · {session.Goal}",
            $"{project.Name} · {session.Workspace}",
            session.Summary,
            $"Earlier session · {SessionTime(session.UpdatedAt)}");
        SetStatus("Write a reply to take the session up again");
    }

    private static string SessionAccess(AgentSessionRecord session) =>
        session.Access != AgentAccess.Do ? "read only" : session.AutoApprove ? "auto" : "edits";

    private static string SessionTime(DateTimeOffset at)
    {
        var when = at.ToLocalTime();
        return when.Date == DateTime.Today
            ? when.ToString("HH:mm", CultureInfo.InvariantCulture)
            : when.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    private AgentSessionRow ToSessionRow(AgentSessionRecord session) =>
        new(session.Goal,
            $"{_agents.Provider(session.Provider).Name} · {SessionAccess(session)} · {ToPreviewText(session.Summary)}",
            SessionTime(session.UpdatedAt),
            session);

    /// <summary>
    /// Runs a task, fresh or continuing <paramref name="previous"/>, and shows it in Activity; <paramref name="note"/>
    /// opens Activity, such as the note a new session starts over with.
    /// </summary>
    private async Task StartAgentAsync(AgentTask task, AgentRun? previous, string? note = null)
    {
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        _runningTask = task;
        _decision = null;
        _earlierSession = null;
        ShowAgentPanel();
        _agentPanel!.ShowActivity(AgentHeader(task), $"{project.Name} · {task.Workspace}");
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentClock = Stopwatch.StartNew();
        _pendingAgentEvents.Clear();
        if (note is not null) _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Output, note));
        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{provider.Name} started.");
        SetStatus($"{provider.Name} is working · Stop is in the panel");
        var progress = new Progress<AgentEvent>(_pendingAgentEvents.Add);
        AgentRun? ended = null;
        try
        {
            var run = await _agents.RunAsync(task, _backupBeforeDo, previous, progress, cancellation.Token, ReplaceSkill);
            if (run.Before is { IsGit: false, Backup: null } && _backupBeforeDo && task.Access == AgentAccess.Do && previous is null)
            {
                _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Error, "No copy was made: the workspace is over 2 GB."));
            }

            FlushAgentEvents();
            ShowAgentRun(run);
            RememberAgentSession(project, run);
            ended = run;
        }
        catch (Exception exception)
        {
            FlushAgentEvents();
            _agentPanel.ShowResult(exception.Message, "Could not start");
            _cues.Play(SoundCue.Error);
            SetError(exception.Message);
        }
        finally
        {
            _activityTimer.Stop();
            _agentCancellation = null;
            _runningTask = null;
            cancellation.Dispose();
            ReportState();
        }

        if (_restartWhenStopped)
        {
            _restartWhenStopped = false;
            await RestartWithNoteAsync();
        }
        else if (_steerWith is { } words)
        {
            _steerWith = null;
            if (ended is not null && AgentCoordinator.ContinueTask(ended, AgentCoordinator.Interrupted(words)) is { } next)
            {
                await StartAgentAsync(next, ended);
            }
            else
            {
                SetError("The agent stopped before its session began: give your words as a new task.");
            }
        }
    }

    /// <summary>Start over with a note; a running agent is stopped first, and its note is asked once it has ended.</summary>
    private void RequestRestart()
    {
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
        var provider = _agents.Provider(run.Task.Provider);
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentPanel?.ShowStep($"Asking {provider.Name} what it tried, for a new session…", running: true);
        SetStatus($"{provider.Name} writes a note for a new session · Stop is in the panel");
        (AgentTask Task, string Note)? next;
        try
        {
            next = await _agents.StartOverAsync(run, origin, cancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _agentPanel?.ShowStep("Could not ask for a note", running: false);
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
            _agentPanel?.ShowStep("Start over stopped", running: false);
            SetStatus("Start over stopped: the last run stays as it was");
            return;
        }

        await StartAgentAsync(fresh.Task, run,
            fresh.Note.Length > 0 ? "Note from the last attempt:\n" + fresh.Note : "No note came back: the new session starts with the task alone.");
    }

    private DispatcherTimer CreateActivityTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ActivityFlush };
        timer.Tick += (_, _) => FlushAgentEvents();
        return timer;
    }

    /// <summary>Events arrive as fast as the CLI writes them; the panel takes them in batches, ten times a second at most.</summary>
    private void FlushAgentEvents()
    {
        if (_agentPanel is null) return;
        if (_runningTask is { } task) _agentPanel.SetHeader(AgentHeader(task));
        if (_pendingAgentEvents.Count == 0) return;
        var events = _pendingAgentEvents.ToArray();
        _pendingAgentEvents.Clear();
        _agentPanel.AddSteps(events);
        if (_agentOrigin is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Loop)) _agentPanel.ShowRestart(true);
        // Changed files are listed as they come; the full list, from git or the copy, replaces them at the end.
        if (_runningTask is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Artifact))
        {
            _runningFiles.UnionWith(events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Artifact).Select(agentEvent => agentEvent.Text));
            _agentPanel.ShowChanges(_runningFiles.Select(path => new AgentChangeRow(Relative(path), path)).ToArray());
        }
    }

    private string AgentHeader(AgentTask task)
    {
        var access = task.PlanOnly ? "plan"
            : task.AnswerFile is { } file ? $"read only, {Path.GetFileName(file)}"
            : task.AnswerIsSkill ? "read only, SKILL.md"
            : task.Access == AgentAccess.Do ? task.AutoApprove ? "auto" : "edits"
            : "read only";
        var elapsed = _agentClock.Elapsed;
        return $"{_agents.Provider(task.Provider).Name} · {access} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void ShowAgentRun(AgentRun run)
    {
        _lastRun = run;
        _runningFiles.Clear();
        var (task, result) = (run.Task, run.Result);
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        _agentPanel!.ShowChanges(run.Changes.Select(ToChangeRow).ToArray());
        var elapsed = $"{(int)_agentClock.Elapsed.TotalMinutes}:{_agentClock.Elapsed.Seconds:00}";
        var stopped = result.Stopped || run.Check is { Outcome: AgentCheckOutcome.Stopped };
        // A red check is not "done", whatever the agent's own summary says.
        var checkFailed = !stopped && run.Check is { Passed: false };
        var ending = stopped ? $"Stopped after {elapsed}"
            : !result.Succeeded ? $"Failed after {elapsed}"
            : checkFailed ? $"Check failed after {elapsed}"
            : $"Done in {elapsed}";
        // An answer Link wrote to a file is shown as written.
        var answer = task.AnswerFile is null && !task.AnswerIsSkill ? result.Summary : AgentCoordinator.Unfenced(result.Summary);
        var text = checkFailed ? CheckReport(task, run.Check!) + (answer.Length > 0 ? "\n\n" + answer : string.Empty) : answer;
        _agentPanel.ShowResult(text, ending);
        _agentPanel.ShowStats(StatsLine(result.Stats));
        _agentPanel.ShowLog(result.Log);
        ShowUndoOffer(run);
        // After a failure, or a Stop in a loop; a Stop without a loop was the user's own choice, and a task done in the
        // end got past its loop.
        var failed = !stopped && (!result.Succeeded || checkFailed);
        _agentPanel.ShowRestart(_agentOrigin is not null && (failed || (stopped && result.Loops.Count > 0)));
        // Work that went well can be kept as a skill; a map or a skill is no such work.
        _agentPanel.ShowSaveSkill(result.Succeeded && !stopped && !checkFailed && !task.PlanOnly && task.AnswerFile is null &&
                                  !task.AnswerIsSkill && result.SessionId is not null);
        _agentPanel.ShowReply(result.SessionId is not null);

        var decision = OfferDecision(run);
        var summary = text.Length > 0 ? text : ending;
        if (decision)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{provider.Name} needs your decision.");
            NotifyIfAway(project, "Agent needs a decision", ToPreviewText(summary));
        }
        else if (result.Succeeded && !stopped && !checkFailed)
        {
            if (!IsVisible) _unseen = LinkState.Done;
            _cues.Play(SoundCue.Done);
            SayAgent(SpokenSummary(result.Summary, run.Changes.Count));
            NotifyIfAway(project, "Agent finished", ToPreviewText(summary));
        }
        else if (!stopped)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.Error);
            SayAgent(checkFailed ? $"{provider.Name} finished, but the check fails." : $"{provider.Name} failed.");
            NotifyIfAway(project, checkFailed ? "The check fails after the agent" : "Agent failed", ToPreviewText(summary));
        }

        SetStatus($"{provider.Name}: {ending.ToLowerInvariant()} · {run.Changes.Count} files changed");
    }

    /// <summary>
    /// What only the user can decide after a run: retry with edits what read only did not let the agent do, allow what
    /// Claude was refused and go on, or carry out a plan. Returns whether a decision is offered.
    /// </summary>
    private bool OfferDecision(AgentRun run)
    {
        var (task, result) = (run.Task, run.Result);
        _decision = null;
        _agentPanel!.HideDecision();
        if (result.Stopped || result.SessionId is not { } session) return false;
        // Changing files or running any command is never allowed in read only: it is a retry with edits, except for a
        // task whose file Link writes itself (a map, a skill).
        var claude = task.Provider == AgentProviderKind.ClaudeCode;
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
                // A copy of a workspace without git is made before the edits, as the card does by default.
                _backupBeforeDo = true;
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    Access = AgentAccess.Do,
                    AllowedTools = task.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal).ToArray(),
                    Prompt = "You may change files in the workspace now. Carry out the task." +
                             (task.CheckCommand is { } check ? " " + AgentCoordinator.CheckNote(check) : string.Empty),
                    Images = []
                }, null);
            };
            return true;
        }

        // In read only, only what looks can be allowed.
        var allowable = result.Denials.Where(rule => !readOnly || !AgentPermissions.ChangesThings(rule)).ToArray();
        if (claude && allowable.Length > 0)
        {
            IReadOnlyList<string> rules = allowable;
            _agentPanel.ShowDecision(
                $"Claude Code was not allowed: {string.Join(", ", rules)}. Allow {(rules.Count == 1 ? "it" : "them")} and continue?",
                "Allow and retry",
                canRemember: true);
            _decision = () =>
            {
                if (_rememberAllowance) RememberAllowance(task.Access, rules);
                var allowed = task.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal).ToArray();
                _ = StartAgentAsync(task with
                {
                    ResumeSessionId = session,
                    AllowedTools = allowed,
                    Prompt = $"These tools are allowed now: {string.Join(", ", rules)}. Continue the task.",
                    Images = []
                }, run);
            };
            return true;
        }

        if (task.PlanOnly && result.Succeeded)
        {
            var edits = task.Access == AgentAccess.Do;
            _agentPanel.ShowDecision(
                edits ? "The plan is above. Carry it out with edits in the workspace?" : "The plan is above. Carry it out, read only?",
                "Run the plan",
                canRemember: false);
            // The plan read only; the copy before edits is made now.
            _decision = () => _ = StartAgentAsync(task with
            {
                ResumeSessionId = session,
                PlanOnly = false,
                Prompt = "Carry out the plan.",
                Images = []
            }, null);
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

    private void ShowUndoOffer(AgentRun run)
    {
        var undoable = run.Changes.Count(change => change.Undoable);
        if (run.Before.IsGit && undoable > 0)
        {
            _agentPanel!.ShowUndo($"Undo agent changes ({undoable})",
                "Put back the files the agent changed that had no changes of yours before the task");
        }
        else if (run.Before.Backup is not null && run.Changes.Count > 0)
        {
            _agentPanel!.ShowUndo("Restore the copy", $"Put the workspace back as it was before the task, from {run.Before.Backup}");
        }
        else
        {
            _agentPanel!.ShowUndo(null);
        }
    }

    private async Task UndoAgentChangesAsync()
    {
        if (_lastRun is not { } run || AgentRunning) return;
        var git = run.Before.IsGit;
        var files = git ? run.Changes.Where(change => change.Undoable).ToArray() : run.Changes.ToArray();
        var kept = run.Changes.Count - files.Length;
        var question = git
            ? $"Undo the agent's changes in {files.Length} files? New files are deleted, changed ones come back from the last commit." +
              (kept > 0 ? $" {kept} files you had changed before the task stay as they are." : string.Empty)
            : $"Put {files.Length} files back from the copy made before the task? Files the agent added are deleted.";
        if (MessageBox.Show(this, question, "Link · undo agent changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (git) await _agents.Changes.UndoAsync(run.Before, files, CancellationToken.None);
            else await _agents.Changes.RestoreAsync(run.Before, files, CancellationToken.None);
            var left = await _agents.Changes.CompareAsync(run.Before, CancellationToken.None);
            _lastRun = run with { Changes = left };
            _agentPanel?.ShowChanges(left.Select(ToChangeRow).ToArray());
            ShowUndoOffer(_lastRun);
            SetStatus(left.Count == 0 ? "The agent's changes were undone" : $"Undone; {left.Count} files still differ");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetError($"Could not undo: {exception.Message}");
        }
    }

    /// <summary>↻ To chat: the result and the list of changed files go into the context; the next Chat message asks about them.</summary>
    private void AgentResultToChat()
    {
        if (_lastRun is not { } run) return;
        var files = run.Changes.Select(change => $"- {change.Kind} {Relative(change.Path)}").ToArray();
        var text = (run.Result.Summary.Length > 0 ? run.Result.Summary : "The agent gave no summary.") +
                   (files.Length > 0 ? "\n\nChanged files:\n" + string.Join('\n', files) : "\n\nNo files changed.");
        foreach (var old in _context.Where(item => item.Type == ContextItemType.AgentResult).ToArray()) _context.Remove(old);
        AddContext(new ContextItem
        {
            Type = ContextItemType.AgentResult,
            Label = $"{_agents.Provider(run.Task.Provider).Name} result",
            Text = text
        });
        if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
        PromptBox.Focus();
        SetStatus("The agent's result is in the context: ask the chat about it");
    }

    /// <summary>↻ Agent on a Chat answer: the same snapshot goes to the card, nothing is captured again.</summary>
    private void ToAgent_OnClick(object sender, RoutedEventArgs e)
    {
        if (_attemptOnScreen is not { } shown) return;
        if (AgentRunning)
        {
            SetStatus("An agent is already working: Stop it or wait for it first");
            ShowAgentPanel();
            return;
        }

        OpenAgentCard(shown.Snapshot);
    }

    private async void AgentHint_OnClick(object sender, RoutedEventArgs e) => await OpenAgentCardFromPromptAsync();

    /// <summary>The Agent hint lights up on a verb of action; it never changes the route by itself.</summary>
    private void UpdateAgentHint()
    {
        var show = _settings.Current.Route != RequestRoute.Agent && ActionVerbs.IsMatch(PromptBox.Text);
        AgentHintButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Why a task is not done: the check, how it ended and its last lines.</summary>
    private static string CheckReport(AgentTask task, AgentCheckResult check)
    {
        var last = check.Tail.Split('\n').TakeLast(12);
        return $"The check fails: {task.CheckCommand} {check}." + (check.Tail.Length > 0 ? "\n" + string.Join('\n', last) : string.Empty);
    }

    /// <summary>What the task cost, in one line: "12 turns · 34 tool calls, 20 searches · 1 tool error · 152k tokens in…".</summary>
    private static string StatsLine(AgentStats stats)
    {
        var parts = new List<string>();
        if (stats.Turns > 0) parts.Add(Plural(stats.Turns, "turn", "turns"));
        var before = stats.SearchesBeforeChange is { } count ? $" ({count} before the first change)" : string.Empty;
        parts.Add(stats.ToolCalls == 0
            ? "no tool calls"
            : $"{Plural(stats.ToolCalls, "tool call", "tool calls")}, {Plural(stats.Searches, "search", "searches")}{before}");
        if (stats.ToolErrors > 0) parts.Add(Plural(stats.ToolErrors, "tool error", "tool errors"));
        if (stats.InputTokens is { } input)
        {
            parts.Add($"{Tokens(input)} tokens in ({Tokens(stats.CachedTokens ?? 0)} cached), {Tokens(stats.OutputTokens ?? 0)} out" +
                      (stats.SessionTokens ? " for the session" : string.Empty));
        }

        if (stats.CostUsd is { } usd) parts.Add(usd.ToString("$0.00", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);

        static string Plural(int number, string one, string many) => $"{number} {(number == 1 ? one : many)}";

        static string Tokens(long number) => number switch
        {
            < 1_000 => number.ToString(CultureInfo.InvariantCulture),
            < 100_000 => (number / 1_000.0).ToString("0.#k", CultureInfo.InvariantCulture),
            < 1_000_000 => (number / 1_000.0).ToString("0k", CultureInfo.InvariantCulture),
            _ => (number / 1_000_000.0).ToString("0.#M", CultureInfo.InvariantCulture)
        };
    }

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
        _projects.ActiveProject.WorkspaceRoot is { } root && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path)
            : path;

    /// <summary>Shows the file in Explorer: opening it could run it, and a changed script is not to be run by a click.</summary>
    private static void ShowFile(string path)
    {
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true })?.Dispose();
    }

    private static bool HasGit(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return true;
        }

        return false;
    }

    /// <summary>The agent speaks only its start, a needed decision and its result, briefly, when replies are read aloud.</summary>
    private void SayAgent(string text)
    {
        if (!_settings.Current.VoiceReplies || !_voice.CanReadAloud || _voice.Phase != VoicePhase.Idle) return;
        _ = SayAgentAsync(text);
    }

    private async Task SayAgentAsync(string text)
    {
        try
        {
            await _voice.ReadAloudAsync(text);
        }
        catch (Exception exception)
        {
            DiagnosticsReported?.Invoke($"Agent voice failed: {exception.Message}");
        }
    }

    /// <summary>One or two sentences of the result, without code, paths or lists: the rest is on screen.</summary>
    private static string SpokenSummary(string summary, int changedFiles)
    {
        var plain = Regex.Replace(ToPreviewText(summary), @"`[^`]*`|\S*[\\/]\S*", string.Empty);
        var sentences = Regex.Split(plain, @"(?<=[.!?])\s+").Where(sentence => sentence.Trim().Length > 0).Take(2);
        var spoken = string.Join(' ', sentences).Trim();
        if (spoken.Length > 220) spoken = spoken[..220];
        return spoken.Length > 0 ? spoken : changedFiles > 0 ? $"Done, {changedFiles} files changed." : "Done.";
    }
}
