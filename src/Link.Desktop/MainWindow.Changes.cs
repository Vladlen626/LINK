using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// What a run changed: the numbers in the header, the files' rows, undo, and the task's Perforce changelist.
public partial class MainWindow
{
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
