using System.Windows;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

// Studio: the agents' tasks and the Harness Inspector in a window of their own, apart from the overlay and its panel. It
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
        studio.ApplyGlass(_settings.Current.Opacity);
        studio.CompactRequested += () => _ = CompactTask();
        studio.InspectorOpened += () => RefreshStudio();
        studio.OpenFileRequested += ShowFile;
        studio.ConsoleRequested += ShowAgentConsole;
        studio.TeamPackExportRequested += ExportTeamPack;
        studio.TeamPackImportRequested += ImportTeamPack;
        // Back from editing a rules, role or skill file: the inspector reads it again.
        studio.Activated += (_, _) => RefreshStudio();
        studio.Closed += (_, _) =>
        {
            _studio = null;
            _settings.Save();
        };
        return studio;
    }

    /// <summary>
    /// The Studio's header (the project, the open task's context, Compact) and its inspector as things stand. The
    /// inspector is built only while its column is open, and again when it opens; <paramref name="live"/> (the session
    /// told its context) leaves it as it is if it was built a moment ago.
    /// </summary>
    private void RefreshStudio(bool live = false)
    {
        if (_studio is not { } studio) return;
        var context = _task is { } task ? ContextLine(task) ?? "Context not told yet" : null;
        studio.ShowHeader(_projects.ActiveProject.Name, context, _task is not null && !AgentRunning && ResumableTask() is not null);
        if (!studio.InspectorOpen || live && Environment.TickCount64 - _inspectorBuiltAt < LiveInspectorEvery.TotalMilliseconds) return;
        _inspectorBuiltAt = Environment.TickCount64;
        studio.ShowHarness(CurrentHarness());
    }

    /// <summary>The folder the Studio's harness is of: the open task's, else the project's workspace; null when there is none.</summary>
    private string? StudioWorkspace()
    {
        var project = _task is null ? _projects.ActiveProject : _taskProject ?? _projects.ActiveProject;
        return _task?.Workspace ?? (project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null);
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
        if (_agentPanel is not null && _shownTask is not null && _taskProject?.Id != _projects.ActiveProject.Id) ShowTaskList();
        else RefreshTaskList();
        RefreshStudio();
    }
}
