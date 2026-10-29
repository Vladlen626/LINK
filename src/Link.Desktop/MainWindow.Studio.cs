using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

// Studio: the agents' tasks, the Harness Inspector and the Spend view in a window of their own, apart from the overlay and its panel. It
// is made the first time it is wanted and is gone when closed; the AgentPanel it shows stays here, with the tasks' state,
// and moves into each new Studio. Nothing here runs while the Studio is closed.
public partial class MainWindow
{
    // A running session tells its context many times a second; the inspector reads files, so it follows once a second.
    private static readonly TimeSpan LiveInspectorEvery = TimeSpan.FromSeconds(1);
    private StudioWindow? _studio;
    private long _inspectorBuiltAt;

    private void StudioHeader_OnClick(object sender, RoutedEventArgs e) => ShowAgentPanel();

    /// <summary>The Studio, opened or brought forward, with the tasks as they stand: the card, the open task, or the list.</summary>
    public void ShowAgentPanel()
    {
        _agentPanel ??= CreateAgentPanel();
        _studio ??= CreateStudio(_agentPanel);
        if (!_studio.IsVisible) _studio.Show();
        if (_studio.WindowState == WindowState.Minimized) _studio.WindowState = WindowState.Normal;
        _studio.Activate();
        RefreshStudio();
    }

    private StudioWindow CreateStudio(AgentPanel tasks)
    {
        var studio = new StudioWindow(_settings.Current.Studio, tasks);
        studio.CompactRequested += () =>
        {
            if (_open is { } work) _ = CompactTask(work);
        };
        studio.InspectorOpened += () => RefreshStudio();
        studio.SpendFixRequested += fix =>
        {
            if (fix == SpendFix.Map) _ = WriteProjectMapAsync();
            else if (fix == SpendFix.Compact && StudioTask is { } work) _ = CompactTask(work);
        };
        studio.SpendHistoryChanged += () => studio.ShowSpendHistory(SpendDays(studio));
        studio.OpenFileRequested += ShowFile;
        studio.ConsoleRequested += ShowAgentConsole;
        studio.TeamPackExportRequested += ExportTeamPack;
        studio.TeamPackImportRequested += ImportTeamPack;
        // Back from editing a rules, role or skill file: the inspector reads it again.
        studio.Activated += (_, _) =>
        {
            KeepOnTop(false);
            RefreshStudio();
        };
        studio.Deactivated += (_, _) => KeepOnTop(true);
        studio.Closed += (_, _) =>
        {
            _studio = null;
            KeepOnTop(true);
            _settings.Save();
        };
        return studio;
    }

    /// <summary>
    /// The overlay and its side panel stay on top of other apps, not of the Studio the user works in. Both go together:
    /// an owned window cannot sit below a topmost owner.
    /// </summary>
    private void KeepOnTop(bool onTop)
    {
        Topmost = onTop && _settings.Current.AlwaysOnTop;
        if (_panel is not null) _panel.Topmost = Topmost;
    }

    /// <summary>
    /// The Studio's header (the project, the open task's context, Compact) and its right column as things stand. The
    /// column is built only while it is open, and again when it opens; <paramref name="live"/> (the session told its
    /// context) leaves it as it is if it was built a moment ago, and the Spend history as it is.
    /// </summary>
    private void RefreshStudio(bool live = false)
    {
        if (_studio is not { } studio) return;
        var context = StudioTask is { } work ? ContextLine(work.Record) ?? "Context not told yet" : null;
        var canCompact = StudioTask is { Busy: false, Waiting: null } open && ResumableTask(open) is not null;
        studio.ShowHeader(_projects.ActiveProject.Name, context, StudioTask is { } shownTask ? ContextFull(shownTask.Record) : null, canCompact);
        if (!studio.InspectorOpen || live && Environment.TickCount64 - _inspectorBuiltAt < LiveInspectorEvery.TotalMilliseconds) return;
        _inspectorBuiltAt = Environment.TickCount64;
        if (!studio.SpendOpen)
        {
            studio.ShowHarness(CurrentHarness());
            return;
        }

        studio.ShowSpend(StudioTask is { } shown ? TaskSpend(shown.Record, canCompact) : null, StudioTask?.Run is not null, SpendGauges());
        if (!live) studio.ShowSpendHistory(SpendDays(studio));
    }

    /// <summary>How full the task's session is against its limit, 0 to 1; null while neither is known.</summary>
    private double? ContextFull(AgentTaskRecord record)
    {
        var (used, window) = ContextOf(record);
        return used is { } tokens && AgentContext.Limit(_settings.Current.Agent.ContextBudget, window) is long limit and > 0
            ? Math.Clamp((double)tokens / limit, 0, 1)
            : null;
    }

    /// <summary>The task's spend for the Spend view: each run named by its agent and setup.</summary>
    private SpendTask TaskSpend(AgentTaskRecord record, bool canCompact) => SpendReport.Of(record,
        run => $"{AgentName(run.Provider)} · {SetupWords(run.Provider, run.Model, run.Effort, ranModel: run.RanModel)}",
        _settings.Current.Agent.ContextBudget, File.Exists(ProjectMap.PathIn(record.Workspace)), canCompact);

    /// <summary>Both agents' windows as last told, with the shown task's part of each.</summary>
    private IReadOnlyList<SpendGauge> SpendGauges() => SpendReport.Gauges(
        [.. _limits.For(AgentProviderKind.ClaudeCode), .. _limits.For(AgentProviderKind.Codex)], StudioTask?.Record, DateTimeOffset.Now);

    /// <summary>The days the Spend history shows: its range back from today, of the shown project or of all.</summary>
    private IReadOnlyList<SpendDay> SpendDays(StudioWindow studio)
    {
        var (days, all) = studio.SpendRange;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = today.AddDays(1 - days);
        return SpendReport.Days(_spending.Since(first, all ? null : _projects.ActiveProject.Id), first, today);
    }

    /// <summary>
    /// The task the Studio's header and inspector are of: the open one, but none while the new-task card is up, so that
    /// they say what a new task gets, not what the task left open behind the card holds.
    /// </summary>
    private TaskWork? StudioTask => Equals(_cardRun, (Func<AgentPlan, Task>)RunAgentAsync) ? null : _open;

    /// <summary>The folder the Studio's harness is of: the shown task's, else the project's workspace; null when there is none.</summary>
    private string? StudioWorkspace()
    {
        var project = StudioTask?.Project ?? _projects.ActiveProject;
        return StudioTask?.Record.Workspace ?? (project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null);
    }

    /// <summary>Export team pack…: the workspace's roles and skills to a .linkpack file the user names.</summary>
    private void ExportTeamPack()
    {
        if (_studio is not { } studio) return;
        if (StudioWorkspace() is not { } workspace)
        {
            studio.ShowTeamPackStatus("Choose the workspace folder in the Project tab first.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export team pack",
            Filter = "LINK team pack (*.linkpack)|*.linkpack",
            DefaultExt = TeamPack.Extension,
            FileName = Path.GetFileName(workspace) + TeamPack.Extension
        };
        if (dialog.ShowDialog(studio) != true) return;
        try
        {
            var (roles, skills) = TeamPack.Export(workspace, dialog.FileName);
            studio.ShowTeamPackStatus(roles + skills == 0
                ? "Nothing to export: the workspace has no roles or skills of its own in .agents."
                : $"Exported {Plural(roles, "role")} and {Plural(skills, "skill")} to {dialog.FileName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            studio.ShowTeamPackStatus($"The pack could not be written: {exception.Message}");
        }
    }

    /// <summary>Import team pack…: the roles and skills of a .linkpack file into the workspace; what it has already is replaced only if the user says so.</summary>
    private void ImportTeamPack()
    {
        if (_studio is not { } studio) return;
        if (StudioWorkspace() is not { } workspace)
        {
            studio.ShowTeamPackStatus("Choose the workspace folder in the Project tab first.");
            return;
        }

        var dialog = new OpenFileDialog { Title = "Import team pack", Filter = "LINK team pack (*.linkpack)|*.linkpack" };
        if (dialog.ShowDialog(studio) != true) return;
        // One question for everything the workspace has already, asked at the first.
        bool? replaceThem = null;
        try
        {
            var result = TeamPack.Import(dialog.FileName, workspace, name => replaceThem ??= MessageBox.Show(studio,
                $"The workspace has {name} already, and maybe more of the pack's. Replace the ones it has with the pack's?\n\nNo keeps yours and adds only what is new.",
                "Link · team pack", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            studio.ShowTeamPackStatus($"Imported: {result.Added.Count} new, {result.Replaced.Count} replaced, {result.Skipped.Count} kept as they were.");
            RefreshProjectPanel();
            RefreshStudio();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            studio.ShowTeamPackStatus($"The pack was not imported: {exception.Message}");
        }
    }

    /// <summary>Another project is shown: the Studio lists its tasks (the list, unless its own task is open) and its harness.</summary>
    private void RefreshStudioProject()
    {
        if (_agentPanel is not null && _shown is not null && _shown.Project.Id != _projects.ActiveProject.Id) ShowTaskList();
        else RefreshTaskList();
        RefreshStudio();
    }
}
