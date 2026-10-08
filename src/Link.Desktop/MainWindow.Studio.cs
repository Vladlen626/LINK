using System.Windows;

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
        studio.CompactRequested += () => _ = CompactTask();
        studio.InspectorOpened += () => RefreshStudio();
        studio.OpenFileRequested += ShowFile;
        studio.ConsoleRequested += ShowAgentConsole;
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

    /// <summary>Another project is shown: the Studio lists its tasks (the list, unless its own task is open) and its harness.</summary>
    private void RefreshStudioProject()
    {
        if (_agentPanel is not null && _shownTask is not null && _taskProject?.Id != _projects.ActiveProject.Id) ShowTaskList();
        else RefreshTaskList();
        RefreshStudio();
    }
}
