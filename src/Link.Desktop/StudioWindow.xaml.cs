using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// Link Studio: the tasks on the left (the sidebar of the <see cref="AgentPanel"/> it is given), the open task in the
/// middle, the Harness Inspector on the right, and in the header the open task's context with Compact. It wears Link's
/// glass and draws its own caption. A normal window, made when it is first opened and gone when it is closed: it keeps no
/// timer, and gives the tasks back so that the next Studio can host them.
/// </summary>
public partial class StudioWindow : Window
{
    private const double InspectorWidth = 340;

    // A window narrower than this has room for one side only: opening one folds the other.
    private const double NarrowWidth = 980;
    private readonly StudioSettings _settings;
    private readonly AgentPanel _tasks;

    public StudioWindow(StudioSettings settings, AgentPanel tasks)
    {
        _settings = settings;
        _tasks = tasks;
        InitializeComponent();
        TasksHost.Child = tasks;
        Inspector.OpenFileRequested += path => OpenFileRequested?.Invoke(path);
        Inspector.ConsoleRequested += () => ConsoleRequested?.Invoke();
        Inspector.TeamPackExportRequested += () => TeamPackExportRequested?.Invoke();
        Inspector.TeamPackImportRequested += () => TeamPackImportRequested?.Invoke();
        Width = settings.Width;
        Height = settings.Height;
        if (settings.Left is { } left && settings.Top is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            // A monitor that is gone since: the window comes up centered.
            WindowStyleService.EnsureVisible(this);
        }

        if (settings.Maximized) WindowState = WindowState.Maximized;
        ShowInspector(settings.InspectorOpen);
        ShowSidebar(settings.SidebarOpen);
        ApplyState();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => ApplyState();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        Closing += (_, _) =>
        {
            Remember();
            // The tasks outlive the window: the next one hosts them again, and a control has only one parent.
            TasksHost.Child = null;
        };
    }

    /// <summary>The header's Compact: a fresh session of the open task's agent is to be started from its state.</summary>
    public event Action? CompactRequested;

    /// <summary>The inspector column opened, empty or with an old view: it is to be shown the harness as it stands.</summary>
    public event Action? InspectorOpened;

    /// <summary>A file of the harness is to be shown in Explorer.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>The Console is to be opened.</summary>
    public event Action? ConsoleRequested;

    /// <summary>The inspector's Export team pack…</summary>
    public event Action? TeamPackExportRequested;

    /// <summary>The inspector's Import team pack…</summary>
    public event Action? TeamPackImportRequested;

    public bool InspectorOpen => _settings.InspectorOpen;

    private bool Narrow => ActualWidth is > 0 and < NarrowWidth;

    /// <summary>The project, the open task's context line (null: no task is open) and whether Compact is offered.</summary>
    public void ShowHeader(string project, string? context, bool canCompact)
    {
        ProjectText.Text = project;
        ProjectText.ToolTip = project;
        ContextText.Text = context ?? string.Empty;
        ContextPanel.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        CompactButton.IsEnabled = canCompact;
    }

    public void ShowHarness(HarnessView view) => Inspector.Show(view);

    public void ShowTeamPackStatus(string text) => Inspector.ShowTeamPackStatus(text);

    /// <summary>Opens or folds the inspector column, and keeps the choice.</summary>
    public void ShowInspector(bool open)
    {
        _settings.InspectorOpen = open;
        InspectorColumn.Width = new GridLength(open ? InspectorWidth : 0);
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HarnessButton.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        HarnessButton.ToolTip = open
            ? "Fold the Harness column (Ctrl+Shift+H)"
            : "Show the Harness column: what the agent really gets, and why it behaves so (Ctrl+Shift+H)";
        if (open && Narrow && _settings.SidebarOpen) ShowSidebar(false);
        if (open) InspectorOpened?.Invoke();
    }

    /// <summary>Opens or folds the tasks' sidebar, and keeps the choice.</summary>
    public void ShowSidebar(bool open)
    {
        _settings.SidebarOpen = open;
        _tasks.ShowSidebar(open);
        SidebarButton.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        SidebarButton.ToolTip = open ? "Fold the tasks (Ctrl+B)" : "Show the tasks (Ctrl+B)";
        if (open && Narrow && _settings.InspectorOpen) ShowInspector(false);
    }

    /// <summary>Writes where the window stands to its settings, its normal bounds when it is maximized; the owner saves them.</summary>
    public void Remember()
    {
        var bounds = RestoreBounds;
        if (bounds.IsEmpty) return;
        _settings.Remember(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    /// <summary>
    /// A maximized window with its own chrome reaches past the screen by the frame: the content is pulled back by it. The
    /// button shows what a press does.
    /// </summary>
    private void ApplyState()
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Padding = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeIcon.Data = (Geometry)FindResource(maximized ? "RestoreIconGeometry" : "MaximizeIconGeometry");
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+B for the tasks, Ctrl+Shift+H for the Harness and Ctrl+N for a new task, wherever the focus is in the Studio.
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) ShowSidebar(!_settings.SidebarOpen);
        else if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) ShowInspector(!_settings.InspectorOpen);
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) _tasks.RequestNewTask();
        else return;
        e.Handled = true;
    }

    private void Compact_OnClick(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();

    private void Harness_OnClick(object sender, RoutedEventArgs e) => ShowInspector(!_settings.InspectorOpen);

    private void Sidebar_OnClick(object sender, RoutedEventArgs e) => ShowSidebar(!_settings.SidebarOpen);

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
