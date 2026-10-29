using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// What a run changed: the numbers in the header, the files' rows, the review, undo, and the task's Perforce changelist.
public partial class MainWindow
{
    // Your review shows the diff whole up to this (the team's reviewer gets far less); a larger one is cut, and says so.
    private const int YourReviewDiff = 2_000_000;

    // A new file git does not track is read for the review up to this size; a larger one is listed only.
    private const long ReviewNewFile = 512 * 1024;

    /// <summary>The last run left changes in a git or Perforce folder, which the review can show line by line.</summary>
    private static bool CanReview(TaskWork work) =>
        !work.Busy && work.Waiting is null && work.LastRun is { Before.IsVersioned: true, Changes.Count: > 0 };

    /// <summary>
    /// Review: the last run's diff in a window of its own, a comment on any line; the comments go to the agent as the
    /// next words of the task, in the same session, as a reply does. The diff and new files are read off the UI thread.
    /// </summary>
    private async Task ReviewChangesAsync(TaskWork work)
    {
        if (!CanReview(work) || work.LastRun is not { } run) return;
        var workspace = run.Before.Workspace;
        var diff = await _agents.Changes.DiffAsync(run.Before, run.Changes, stat: false, YourReviewDiff, CancellationToken.None) ?? string.Empty;
        var files = DiffReview.Parse(diff).Select(file => file with { Path = Relative(file.Path, workspace) }).ToList();
        foreach (var change in run.Changes.Where(change => change.Kind is "??" or "A"))
        {
            var path = Relative(change.Path, workspace);
            var at = files.FindIndex(file => string.Equals(file.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && files[at].Lines.Any(line => line.Kind != DiffLineKind.Note)) continue;
            var file = await Task.Run(() => NewFileOf(change.Path, path));
            if (at >= 0) files[at] = file;
            else files.Add(file);
        }

        if (files.Count == 0)
        {
            AgentStatus(work, "No changes Link can show as a diff");
            return;
        }

        var (changed, _) = TaskChanges.Describe(null, [.. files.Select(file => file.Path)]);
        var added = files.Sum(file => file.Added);
        var removed = files.Sum(file => file.Removed);
        var window = new ReviewWindow($"Review: {work.Record.Goal}  ·  +{added} −{removed} in {changed}", files) { Owner = _studio };
        window.ShowDialog();
        if (window.Review is not { } review || _open != work) return;
        UserSpoke(work);
        ContinueAgent(work, review);
    }

    /// <summary>A judge's run that handed the task back to the run it read (the final check): words go on with that one.</summary>
    private static bool JudgedOther(TaskWork work, AgentRun? ended) => ended is { Task.Judge: true } && work.LastRun is { } last && last != ended;

    /// <summary>The plan is done: a fresh session on the strong level may check the whole change before the user does.</summary>
    private void OfferFinalCheck(TaskWork work)
    {
        var provider = work.LastRun?.Task.Provider ?? StartProvider;
        var (model, effort) = _settings.Current.Agent.Level(provider, AgentLevel.Deep);
        OfferChoice(work,
            $"The plan is done. A fresh session ({SetupWords(provider, model, effort)}) can check the whole change against it first; it changes nothing.",
            "Check the work",
            canRemember: false,
            () => _ = StartFinalCheckAsync(work));
    }

    /// <summary>The final check: a new judge session reads the last run's diff with the task's plan; the record keeps whose change it read.</summary>
    private async Task StartFinalCheckAsync(TaskWork work)
    {
        if (work.Busy || work.LastRun is not { } done) return;
        var record = work.Record;
        var workspace = done.Before.Workspace;
        var diff = await _agents.Changes.DiffAsync(done.Before, done.Changes, stat: false, YourReviewDiff, CancellationToken.None);
        var request = File.Exists(FinalCheck.RequestFile) ? await File.ReadAllTextAsync(FinalCheck.RequestFile) : string.Empty;
        if (work.Busy || !_works.Contains(work)) return;
        var check = done.Check is { } result ? (result.Passed ? "passed" : result.ToString()) : null;
        var provider = done.Task.Provider;
        var (model, effort) = _settings.Current.Agent.Level(provider, AgentLevel.Deep);
        work.Checked = done;
        record.Add(AgentEntryKind.You, "Check the work");
        await StartAgentAsync(work, new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = FinalCheck.Prompt(request, record.Goal, record.Plan, check, [.. done.Changes.Select(change => Relative(change.Path, workspace))], diff),
            Judge = true,
            AllowedTools = AgentCoordinator.Tools(work.Project, record.Workspace),
            AutoApprove = done.Task.AutoApprove,
            Model = model,
            Effort = effort
        }, null);
    }

    /// <summary>
    /// The final check ended: the task goes on in the session of the run it read. Its findings are offered to that
    /// session; a clean verdict is a line of the history. A question of the check's waits for the user in its session.
    /// </summary>
    private void AfterFinalCheck(TaskWork work, AgentRun run, bool needsYou)
    {
        var checkedRun = work.Checked;
        work.Checked = null;
        if (work.Question is not null) return;
        if (checkedRun is not null) work.LastRun = checkedRun;
        if (needsYou || !run.Result.Succeeded) return;
        var record = work.Record;
        if (FinalCheck.NeedsFix(run.Result.Summary) is not true)
        {
            record.Add(AgentEntryKind.Check, "Final check: nothing to fix");
            SaveTask(work);
            return;
        }

        OfferChoice(work, "The check found things to fix (above). Give them to the task's session?", "Fix them",
            canRemember: false,
            () =>
            {
                UserSpoke(work);
                ContinueAgent(work, FinalCheck.Fix(run.Result.Summary));
            });
    }

    /// <summary>A new file as the review shows it: all its lines added, or a note when it is large or not text.</summary>
    private static DiffFile NewFileOf(string fullPath, string path)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return new DiffFile(path, 0, 0, [new DiffLine(DiffLineKind.Note, null, null, "Gone since the run")]);
            if (info.Length > ReviewNewFile) return new DiffFile(path, 0, 0, [new DiffLine(DiffLineKind.Note, null, null, $"A new file of {info.Length / 1024:N0} KB: open it to read")]);
            var text = File.ReadAllText(fullPath);
            return text.Contains('\0') ? new DiffFile(path, 0, 0, [new DiffLine(DiffLineKind.Note, null, null, "A new binary file: no lines to show")]) : DiffReview.NewFile(path, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiffFile(path, 0, 0, [new DiffLine(DiffLineKind.Note, null, null, "Could not be read: " + exception.Message)]);
        }
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

    /// <summary>
    /// Undo the agent's changes (Git, Perforce), when there is something to undo: of a task's run, or of Link's own answer
    /// (<paramref name="work"/> null).
    /// </summary>
    private PanelAction? UndoAction(TaskWork? work, AgentRun run)
    {
        var undoable = run.Changes.Count(change => change.Undoable);
        return run.Before.IsVersioned && undoable > 0
            ? new PanelAction($"Undo agent changes ({undoable})",
                "Put back the files the agent changed that had no changes of yours before the task", () => _ = UndoAgentChangesAsync(work, run))
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

        var files = run.Changes.Where(change => change.Undoable).ToArray();
        var kept = run.Changes.Count - files.Length;
        var others = work is { OthersEdited: true } ? " Another run has edited this folder since: what it changed in these files goes back too." : string.Empty;
        var question = $"Undo the agent's changes in {files.Length} files? New files are deleted, changed ones come back from version control." +
                       (kept > 0 ? $" {kept} files you had changed before the task stay as they are." : string.Empty) + others;
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
            await _agents.Changes.UndoAsync(run.Before, files, CancellationToken.None);
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

            work.AfterRun = work.AfterRun.Where(action => !action.Label.StartsWith("Undo", StringComparison.Ordinal))
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
}
