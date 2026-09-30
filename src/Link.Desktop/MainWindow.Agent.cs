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

    private void OpenAgentCard(InputSnapshot snapshot)
    {
        _agentSnapshot = snapshot;
        var project = _projects.ActiveProject;
        var attachments = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>().Select(item => item.Label).ToArray();
        var card = new AgentCardModel(
            _agents.Providers.Select(provider => (provider.Kind, provider.Name)).ToArray(),
            ProjectProvider,
            project.Name,
            project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null,
            project.WorkspaceRoot is { } folder && HasGit(folder),
            attachments.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", attachments),
            snapshot.Text);
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

        _backupBeforeDo = plan.BackupBeforeDo;
        // The input went to the agent: like a sent message, it leaves the prompt.
        if (PromptBox.Text.Trim() == snapshot.Text) ClearSentInput(PromptBox.Text);
        await StartAgentAsync(AgentCoordinator.BuildTask(plan, snapshot, project), null);
    }

    /// <summary>Runs a task, fresh or continuing <paramref name="previous"/>, and shows it in Activity.</summary>
    private async Task StartAgentAsync(AgentTask task, AgentRun? previous)
    {
        var provider = _agents.Provider(task.Provider);
        var project = _projects.ActiveProject;
        _runningTask = task;
        _decision = null;
        ShowAgentPanel();
        _agentPanel!.ShowActivity(AgentHeader(task), $"{project.Name} · {task.Workspace}");
        var cancellation = new CancellationTokenSource();
        _agentCancellation = cancellation;
        _agentClock = Stopwatch.StartNew();
        _pendingAgentEvents.Clear();
        _activityTimer ??= CreateActivityTimer();
        _activityTimer.Start();
        _cues.Play(SoundCue.Sent);
        SayAgent($"{provider.Name} started.");
        SetStatus($"{provider.Name} is working · Stop is in the panel");
        var progress = new Progress<AgentEvent>(_pendingAgentEvents.Add);
        try
        {
            var run = await _agents.RunAsync(task, _backupBeforeDo, previous, progress, cancellation.Token);
            if (run.Before is { IsGit: false, Backup: null } && _backupBeforeDo && task.Access == AgentAccess.Do && previous is null)
            {
                _pendingAgentEvents.Add(new AgentEvent(AgentEventKind.Error, "No copy was made: the workspace is over 2 GB."));
            }

            FlushAgentEvents();
            ShowAgentRun(run);
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
        // Changed files are listed as they come; the full list, from git or the copy, replaces them at the end.
        if (_runningTask is not null && events.Any(agentEvent => agentEvent.Kind == AgentEventKind.Artifact))
        {
            _runningFiles.UnionWith(events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Artifact).Select(agentEvent => agentEvent.Text));
            _agentPanel.ShowChanges(_runningFiles.Select(path => new AgentChangeRow(Relative(path), path)).ToArray());
        }
    }

    private string AgentHeader(AgentTask task)
    {
        var access = task.PlanOnly ? "plan" : task.Access == AgentAccess.Do ? "edits" : "read only";
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
        var ending = result.Stopped ? $"Stopped after {elapsed}"
            : result.Succeeded ? $"Done in {elapsed}"
            : $"Failed after {elapsed}";
        _agentPanel.ShowResult(result.Summary, ending);
        _agentPanel.ShowStats(StatsLine(result.Stats));
        _agentPanel.ShowLog(result.Log);
        ShowUndoOffer(run);

        var decision = OfferDecision(run);
        var summary = result.Summary.Length > 0 ? result.Summary : ending;
        if (decision)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.NeedsDecision);
            SayAgent($"{provider.Name} needs your decision.");
            NotifyIfAway(project, "Agent needs a decision", ToPreviewText(summary));
        }
        else if (result.Succeeded)
        {
            if (!IsVisible) _unseen = LinkState.Done;
            _cues.Play(SoundCue.Done);
            SayAgent(SpokenSummary(result.Summary, run.Changes.Count));
            NotifyIfAway(project, "Agent finished", ToPreviewText(summary));
        }
        else if (!result.Stopped)
        {
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            _cues.Play(SoundCue.Error);
            SayAgent($"{provider.Name} failed.");
            NotifyIfAway(project, "Agent failed", ToPreviewText(summary));
        }

        SetStatus($"{provider.Name}: {ending.ToLowerInvariant()} · {run.Changes.Count} files changed");
    }

    /// <summary>
    /// What only the user can decide after a run: allow what Claude was refused and go on, let Codex retry with edits,
    /// or carry out a plan. Returns whether a decision is offered.
    /// </summary>
    private bool OfferDecision(AgentRun run)
    {
        var (task, result) = (run.Task, run.Result);
        _decision = null;
        _agentPanel!.HideDecision();
        if (result.Stopped || result.SessionId is not { } session) return false;
        if (task.Provider == AgentProviderKind.ClaudeCode && result.Denials.Count > 0)
        {
            var rules = result.Denials;
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

        if (task.Provider == AgentProviderKind.Codex && result.Denials.Count > 0 && task.Access == AgentAccess.Analyze)
        {
            _agentPanel.ShowDecision(
                $"Codex needs to change files, and read only does not let it. Retry with edits in {task.Workspace}?",
                "Retry with edits",
                canRemember: false);
            _decision = () => _ = StartAgentAsync(task with
            {
                ResumeSessionId = session,
                Access = AgentAccess.Do,
                PlanOnly = false,
                Prompt = "You may change files in the workspace now. Carry out the task.",
                Images = []
            }, task.Access == AgentAccess.Do ? run : null);
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

    /// <summary>"Always for this project": in Do the rules join the Do list, in read only the read list.</summary>
    private void RememberAllowance(AgentAccess access, IEnumerable<string> rules)
    {
        var project = _projects.ActiveProject;
        var list = access == AgentAccess.Do ? project.AgentDoTools : project.AgentReadTools;
        foreach (var rule in rules.Where(rule => !list.Contains(rule))) list.Add(rule);
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

    /// <summary>What the task cost, in one line: "12 turns · 34 tool calls, 20 searches · 1 tool error · 152k tokens in…".</summary>
    private static string StatsLine(AgentStats stats)
    {
        var parts = new List<string>();
        if (stats.Turns > 0) parts.Add(Plural(stats.Turns, "turn", "turns"));
        var before = stats.SearchesBeforeChange is { } count ? $" ({count} before the first change)" : string.Empty;
        parts.Add($"{Plural(stats.ToolCalls, "tool call", "tool calls")}, {Plural(stats.Searches, "search", "searches")}{before}");
        if (stats.ToolErrors > 0) parts.Add(Plural(stats.ToolErrors, "tool error", "tool errors"));
        if (stats.InputTokens is { } input)
        {
            parts.Add($"{Tokens(input)} tokens in ({Tokens(stats.CachedTokens ?? 0)} cached), {Tokens(stats.OutputTokens ?? 0)} out");
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
