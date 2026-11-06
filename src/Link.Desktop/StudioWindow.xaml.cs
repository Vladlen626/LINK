using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// The Link window, with two tabs as in the apps the user knows. Chat: the project's chats with ChatGPT (the
/// <see cref="ChatsPanel"/> it is given). Studio: the tasks on the left (the sidebar of the <see cref="AgentPanel"/> it is
/// given), the open task in the middle, and in the header the open task's context with Compact. On the right, a column
/// for the project (the <see cref="ProjectPanel"/> it is given), and on the Studio tab for the Harness Inspector or the
/// Spend view. It draws its own caption. A normal window, made when it is first opened and gone when it is closed: it
/// keeps no timer, and gives the panels back so that the next window can host them.
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
    // Link's projects as they stand (the list Link keeps), for the header's menu.
    private readonly IReadOnlyList<LinkProject> _projects;
    private Guid _project;

    public StudioWindow(StudioSettings settings, AgentPanel tasks, ChatsPanel chats, ProjectPanel project, IReadOnlyList<LinkProject> projects)
    {
        _settings = settings;
        _tasks = tasks;
        _projects = projects;
        InitializeComponent();
        TasksHost.Child = tasks;
        ChatHost.Child = chats;
        ProjectHost.Child = project;
        Inspector.OpenFileRequested += path => OpenFileRequested?.Invoke(path);
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
        ShowTab(settings.Tab);
        ShowSidebar(settings.SidebarOpen);
        ApplyState();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => ApplyState();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        Closing += (_, _) =>
        {
            Remember();
            // The panels outlive the window: the next one hosts them again, and a control has only one parent.
            TasksHost.Child = null;
            ChatHost.Child = null;
            ProjectHost.Child = null;
        };
    }

    /// <summary>The header's Compact: a fresh session of the open task's agent is to be started from its state.</summary>
    public event Action? CompactRequested;

    /// <summary>A project chosen in the header: the Studio is to show it, the card stays on its own.</summary>
    public event Action<LinkProject>? ProjectChosen;

    /// <summary>The right column opened or turned to another view: it is to be shown the harness or the spend as they stand.</summary>
    public event Action? InspectorOpened;

    /// <summary>A Spend tip's fix: the map is to be written or updated, or the open task compacted.</summary>
    public event Action<SpendFix>? SpendFixRequested;

    /// <summary>The Spend history's range or scope changed: its days are to be given again.</summary>
    public event Action? SpendHistoryChanged;

    /// <summary>A file of the harness is to be shown in Explorer.</summary>
    public event Action<string>? OpenFileRequested;

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

    /// <summary>What the right column shows now; null while it is folded or its view is not of this tab.</summary>
    public StudioSide? OpenSide => SideShown ? _settings.Side : null;

    /// <summary>How many days the Spend history shows, and whether of every project.</summary>
    public (int Days, bool AllProjects) SpendRange => (_settings.SpendDays, _settings.SpendAllProjects);

    private bool Narrow => ActualWidth is > 0 and < NarrowWidth;

    // The Chat tab has the project's column only: the Harness and the Spend are of the Studio's tasks.
    private bool SideShown => _settings.InspectorOpen && (_settings.Tab == StudioTab.Studio || _settings.Side == StudioSide.Project);

    /// <summary>
    /// The project, the open task's context line (null: no task is open) with how full it is (0 to 1; null: no limit is
    /// known), and whether Compact is offered.
    /// </summary>
    public void ShowHeader(LinkProject project, string? context, double? full, bool canCompact)
    {
        _project = project.Id;
        ProjectText.Text = project.Name;
        ProjectButton.ToolTip = $"{project.Name}: the Studio's project, apart from the card's. Click to switch, or for its note, workspace, map, skills and check";
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

    /// <summary>The Studio tab's dot: how the agent's task stands (working, needs you, done); null hides it.</summary>
    public void ShowTaskDot(Brush? dot)
    {
        TaskTabDot.Fill = dot;
        TaskTabDot.Visibility = dot is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One of the tabs, kept for the next time the window opens.</summary>
    public void ShowTab(StudioTab tab)
    {
        _settings.Tab = tab;
        var studio = tab == StudioTab.Studio;
        ChatHost.Visibility = studio ? Visibility.Collapsed : Visibility.Visible;
        TasksHost.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        SidebarButton.Visibility = studio ? Visibility.Visible : Visibility.Hidden;
        StudioTools.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        ShowChosen(ChatTabButton, !studio);
        ShowChosen(StudioTabButton, studio);
        ApplySide();
        if (SideShown) InspectorOpened?.Invoke();
    }

    /// <summary>Opens the right column on the Harness, or folds it.</summary>
    public void ShowInspector(bool open) => ShowSide(open, StudioSide.Harness);

    /// <summary>Opens the right column on the project, on either tab.</summary>
    public void ShowProject() => ShowSide(true, StudioSide.Project);

    /// <summary>Opens the right column on a view, or folds it, and keeps the choice; the view it was on last is the one it opens on.</summary>
    private void ShowSide(bool open, StudioSide side)
    {
        _settings.InspectorOpen = open;
        _settings.Side = side;
        ApplySide();
        if (open && Narrow && _settings.SidebarOpen && _settings.Tab == StudioTab.Studio) ShowSidebar(false);
        if (open) InspectorOpened?.Invoke();
    }

    /// <summary>The right column and its toggles as the tab and the choice say.</summary>
    private void ApplySide()
    {
        var side = _settings.Side;
        var open = SideShown;
        InspectorColumn.Width = new GridLength(open ? side == StudioSide.Spend ? SpendWidth : InspectorWidth : 0);
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InspectorScroll.Visibility = side == StudioSide.Harness ? Visibility.Visible : Visibility.Collapsed;
        SpendScroll.Visibility = side == StudioSide.Spend ? Visibility.Visible : Visibility.Collapsed;
        ProjectHost.Visibility = side == StudioSide.Project ? Visibility.Visible : Visibility.Collapsed;
        HarnessButton.Foreground = (Brush)FindResource(open && side == StudioSide.Harness ? "AccentBrush" : "TextBrush");
        SpendButton.Foreground = (Brush)FindResource(open && side == StudioSide.Spend ? "AccentBrush" : "TextBrush");
        ProjectText.Foreground = (Brush)FindResource(open && side == StudioSide.Project ? "AccentBrush" : "MutedBrush");
        HarnessButton.ToolTip = open && side == StudioSide.Harness
            ? "Fold the Harness column (Ctrl+Shift+H)"
            : "Show the Harness: what the agent really gets, and why it behaves so (Ctrl+Shift+H)";
        SpendButton.ToolTip = open && side == StudioSide.Spend
            ? "Fold the Spend column (Ctrl+Shift+U)"
            : "Show the Spend: where the task's tokens went, the usage windows and the days (Ctrl+Shift+U)";
    }

    /// <summary>A tab's button lit while its tab is shown.</summary>
    private void ShowChosen(Button button, bool chosen)
    {
        button.Background = chosen ? (Brush)FindResource("ActiveContextBrush") : Brushes.Transparent;
        button.Foreground = (Brush)FindResource(chosen ? "TextBrush" : "MutedBrush");
    }

    /// <summary>Opens or folds the tasks' sidebar, and keeps the choice.</summary>
    public void ShowSidebar(bool open)
    {
        _settings.SidebarOpen = open;
        _tasks.ShowSidebar(open);
        SidebarButton.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        SidebarButton.ToolTip = open ? "Fold the tasks (Ctrl+B)" : "Show the tasks (Ctrl+B)";
        if (open && Narrow && SideShown) ShowSide(false, _settings.Side);
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
        // Ctrl+1 and Ctrl+2 for the tabs, Ctrl+B for the tasks, Ctrl+Shift+H for the Harness, Ctrl+Shift+U for the Spend and
        // Ctrl+N for a new task, wherever the focus is in the window. The Studio's keys bring its tab.
        var keys = Keyboard.Modifiers;
        if (keys == ModifierKeys.Control && e.Key is Key.D1 or Key.NumPad1) ShowTab(StudioTab.Chat);
        else if (keys == ModifierKeys.Control && e.Key is Key.D2 or Key.NumPad2) ShowTab(StudioTab.Studio);
        else if (e.Key == Key.B && keys == ModifierKeys.Control && _settings.Tab == StudioTab.Studio) ShowSidebar(!_settings.SidebarOpen);
        else if (e.Key == Key.H && keys == (ModifierKeys.Control | ModifierKeys.Shift)) ToggleSide(StudioSide.Harness);
        else if (e.Key == Key.U && keys == (ModifierKeys.Control | ModifierKeys.Shift)) ToggleSide(StudioSide.Spend);
        else if (e.Key == Key.N && keys == ModifierKeys.Control)
        {
            ShowTab(StudioTab.Studio);
            _tasks.RequestNewTask();
        }
        else return;
        e.Handled = true;
    }

    private void Compact_OnClick(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();

    /// <summary>
    /// A view's toggle: folds the column if it shows that view, else opens it on that view. The Harness and the Spend open
    /// on the Studio tab, from the Chat tab too.
    /// </summary>
    private void ToggleSide(StudioSide side)
    {
        if (side != StudioSide.Project && _settings.Tab != StudioTab.Studio)
        {
            ShowTab(StudioTab.Studio);
            ShowSide(true, side);
        }
        else ShowSide(!(SideShown && _settings.Side == side), side);
    }

    /// <summary>The header's project: the projects to switch the Studio to, and this one's settings in the right column.</summary>
    private void Project_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = MenuPlacement.Under(ProjectButton);
        foreach (var project in _projects)
        {
            var item = new MenuItem { Header = project.Name, IsCheckable = true, IsChecked = project.Id == _project };
            item.Click += (_, _) => ProjectChosen?.Invoke(project);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Project settings", IsCheckable = true, IsChecked = OpenSide == StudioSide.Project };
        settings.Click += (_, _) => ToggleSide(StudioSide.Project);
        menu.Items.Add(settings);
        menu.IsOpen = true;
    }

    private void ChatTab_OnClick(object sender, RoutedEventArgs e) => ShowTab(StudioTab.Chat);

    private void StudioTab_OnClick(object sender, RoutedEventArgs e) => ShowTab(StudioTab.Studio);

    private void Harness_OnClick(object sender, RoutedEventArgs e) => ToggleSide(StudioSide.Harness);

    private void Spend_OnClick(object sender, RoutedEventArgs e) => ToggleSide(StudioSide.Spend);

    private void Sidebar_OnClick(object sender, RoutedEventArgs e) => ShowSidebar(!_settings.SidebarOpen);

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
