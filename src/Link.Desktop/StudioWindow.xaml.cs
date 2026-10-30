using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// Link Studio: the tasks on the left (the sidebar of the <see cref="AgentPanel"/> it is given), the open task in the
/// middle, the Harness Inspector or the Spend view on the right, and in the header the open task's context with Compact. It wears Link's
/// glass and draws its own caption. A normal window, made when it is first opened and gone when it is closed: it keeps no
/// timer, and gives the tasks back so that the next Studio can host them.
/// </summary>
public partial class StudioWindow : Window
{
    private const double InspectorWidth = 340;
    private const double SpendWidth = 384;

    // The share of its limit where a session nears the CLI's own compaction: the meter turns to a warning.
    private const double ContextWarning = 0.8;

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
        Inspector.ShareLearnedRequested += () => ShareLearnedRequested?.Invoke();
        Inspector.TeamPackExportRequested += () => TeamPackExportRequested?.Invoke();
        Inspector.OpenPackRequested += () => OpenPackRequested?.Invoke();
        Inspector.PicksConfirmed += picks => PicksConfirmed?.Invoke(picks);
        Inspector.SetUpRequested += () => SetUpRequested?.Invoke();
        Spend.Start(settings.SpendDays, settings.SpendAllProjects);
        Spend.FixRequested += fix => SpendFixRequested?.Invoke(fix);
        Spend.HistoryChanged += () =>
        {
            (settings.SpendDays, settings.SpendAllProjects) = (Spend.RangeDays, Spend.AllProjects);
            SpendHistoryChanged?.Invoke();
        };
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
        ShowSide(settings.InspectorOpen, settings.SpendShown);
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

    /// <summary>The right column opened or turned to its other view: it is to be shown the harness or the spend as they stand.</summary>
    public event Action? InspectorOpened;

    /// <summary>A Spend tip's fix: the map is to be written or updated, or the open task compacted.</summary>
    public event Action<SpendFix>? SpendFixRequested;

    /// <summary>The Spend history's range or scope changed: its days are to be given again.</summary>
    public event Action? SpendHistoryChanged;

    /// <summary>A file of the harness is to be shown in Explorer.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>The Console is to be opened.</summary>
    public event Action? ConsoleRequested;

    /// <summary>The inspector's Share ▾ → What my agents learned.</summary>
    public event Action? ShareLearnedRequested;

    /// <summary>The inspector's Share ▾ → This workspace's roles and skills.</summary>
    public event Action? TeamPackExportRequested;

    /// <summary>The inspector's Open a pack…</summary>
    public event Action? OpenPackRequested;

    /// <summary>The ticked items of the inspector's list.</summary>
    public event Action<IReadOnlyList<SharePick>>? PicksConfirmed;

    /// <summary>The inspector's Set up for this project.</summary>
    public event Action? SetUpRequested;

    public bool InspectorOpen => _settings.InspectorOpen;

    /// <summary>The right column is open on the Spend view rather than the Harness.</summary>
    public bool SpendOpen => _settings.InspectorOpen && _settings.SpendShown;

    /// <summary>How many days the Spend history shows, and whether of every project.</summary>
    public (int Days, bool AllProjects) SpendRange => (_settings.SpendDays, _settings.SpendAllProjects);

    private bool Narrow => ActualWidth is > 0 and < NarrowWidth;

    /// <summary>
    /// The project, the open task's context line (null: no task is open) with how full it is (0 to 1; null: no limit is
    /// known), and whether Compact is offered.
    /// </summary>
    public void ShowHeader(string project, string? context, double? full, bool canCompact)
    {
        ProjectText.Text = project;
        ProjectText.ToolTip = project;
        ContextText.Text = context ?? string.Empty;
        ContextPanel.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        ContextMeter.Visibility = full is null ? Visibility.Collapsed : Visibility.Visible;
        ContextMeter.Value = (full ?? 0) * 100;
        ContextMeter.Foreground = (Brush)FindResource(full >= ContextWarning ? "BusyOrbBrush" : "SuccessBrush");
        CompactButton.IsEnabled = canCompact;
    }

    public void ShowHarness(HarnessView view) => Inspector.Show(view);

    public void ShowSpend(SpendTask? task, bool working, IReadOnlyList<SpendGauge> gauges) => Spend.Show(task, working, gauges);

    public void ShowSpendHistory(IReadOnlyList<SpendDay> days) => Spend.ShowHistory(days);

    public void ShowSharePicks(string heading, IReadOnlyList<SharePick> picks, string confirm) => Inspector.ShowPicks(heading, picks, confirm);

    public void ShowShareStatus(string text, bool setUp = false) => Inspector.ShowShareStatus(text, setUp);

    /// <summary>Opens the right column on the Harness, or folds it if the Harness is what it shows.</summary>
    public void ShowInspector(bool open) => ShowSide(open, spend: false);

    /// <summary>
    /// Opens the right column on the Harness or on the Spend view (<paramref name="spend"/>), or folds it, and keeps the
    /// choice; the view it was on last is the one it opens on.
    /// </summary>
    private void ShowSide(bool open, bool spend)
    {
        _settings.InspectorOpen = open;
        _settings.SpendShown = spend;
        InspectorColumn.Width = new GridLength(open ? spend ? SpendWidth : InspectorWidth : 0);
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InspectorScroll.Visibility = spend ? Visibility.Collapsed : Visibility.Visible;
        SpendScroll.Visibility = spend ? Visibility.Visible : Visibility.Collapsed;
        HarnessButton.Foreground = (Brush)FindResource(open && !spend ? "AccentBrush" : "TextBrush");
        SpendButton.Foreground = (Brush)FindResource(open && spend ? "AccentBrush" : "TextBrush");
        HarnessButton.ToolTip = open && !spend
            ? "Fold the Harness column (Ctrl+Shift+H)"
            : "Show the Harness: what the agent really gets, and why it behaves so (Ctrl+Shift+H)";
        SpendButton.ToolTip = open && spend
            ? "Fold the Spend column (Ctrl+Shift+U)"
            : "Show the Spend: where the task's tokens went, the usage windows and the days (Ctrl+Shift+U)";
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
        if (open && Narrow && _settings.InspectorOpen) ShowSide(false, _settings.SpendShown);
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
        // Ctrl+B for the tasks, Ctrl+Shift+H for the Harness, Ctrl+Shift+U for the Spend and Ctrl+N for a new task, wherever
        // the focus is in the Studio.
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) ShowSidebar(!_settings.SidebarOpen);
        else if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) ToggleSide(spend: false);
        else if (e.Key == Key.U && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) ToggleSide(spend: true);
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) _tasks.RequestNewTask();
        else return;
        e.Handled = true;
    }

    private void Compact_OnClick(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();

    /// <summary>A view's toggle: folds the column if it shows that view, else opens it on that view.</summary>
    private void ToggleSide(bool spend) => ShowSide(!(_settings.InspectorOpen && _settings.SpendShown == spend), spend);

    private void Harness_OnClick(object sender, RoutedEventArgs e) => ToggleSide(spend: false);

    private void Spend_OnClick(object sender, RoutedEventArgs e) => ToggleSide(spend: true);

    private void Sidebar_OnClick(object sender, RoutedEventArgs e) => ShowSidebar(!_settings.SidebarOpen);

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
