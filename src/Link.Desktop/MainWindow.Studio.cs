using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

// The Link window's frame, with two tabs as in the apps the user knows. Chat: the project's chats on the left, the shown
// chat's thread and the input in the middle. Studio: the tasks on the left (the AgentPanel's sidebar), the open task in the
// middle, and in the header its context with Compact. On the right, a column for the project, and on the Studio tab for
// the Harness Inspector or the Spend view. The window draws its own caption; closing it hides Link to the tray.
public partial class MainWindow
{
    private const double ChatSidebarWidth = 264;
    private const double InspectorWidth = 340;
    private const double SpendWidth = 384;

    // The share of its limit where a session nears the CLI's own compaction: the meter turns to a warning.
    private const double ContextWarning = 0.8;

    // A window narrower than this has room for one side only: opening one folds the other.
    private const double NarrowWidth = 980;

    // A running session tells its context many times a second; the inspector reads files, so it follows once a second.
    private static readonly TimeSpan LiveInspectorEvery = TimeSpan.FromSeconds(1);
    private long _inspectorBuiltAt;

    // Read each time: Settings may replace the settings it holds.
    private StudioSettings Place => _settings.Current.Studio;

    /// <summary>What the right column shows now; null while it is folded or its view is not of this tab.</summary>
    private StudioSide? OpenSide => SideShown ? Place.Side : null;

    private bool Narrow => ActualWidth is > 0 and < NarrowWidth;

    // The Chat tab has the project's column only: the Harness and the Spend are of the Studio's tasks.
    private bool SideShown => Place.InspectorOpen && (Place.Tab == StudioTab.Studio || Place.Side == StudioSide.Project);

    /// <summary>The Link window on its Studio tab, with the tasks as they stand: the card, the open task, or the list.</summary>
    public void ShowAgentPanel() => ShowLinkWindow(StudioTab.Studio);

    /// <summary>The Link window, shown or brought forward on <paramref name="tab"/>, else on the tab it was left on.</summary>
    private void ShowLinkWindow(StudioTab? tab = null)
    {
        if (tab is { } chosen) ShowTab(chosen);
        ShowActivated = true;
        Show();
        // Restored as it was before, maximized too.
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Activate();
    }

    /// <summary>The window as it was left (its place, tab, sidebar and right column) and its panels; once, before the project loads.</summary>
    private void SetUpWindow()
    {
        Width = Place.Width;
        Height = Place.Height;
        if (Place.Left is { } left && Place.Top is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            // A monitor that is gone since: the window comes up centered.
            WindowStyleService.EnsureVisible(this);
        }

        if (Place.Maximized) WindowState = WindowState.Maximized;
        WirePanels();
        Inspector.LinkOpened += OpenHarnessLink;
        WireViewer();
        WireShare();
        Spend.Start(Place.SpendDays, Place.SpendAllProjects);
        Spend.FixRequested += fix =>
        {
            if (fix == SpendFix.Map) _ = WriteProjectMapAsync(_projects.ActiveProject);
            else if (fix == SpendFix.Compact && StudioTask is { } work) _ = CompactTask(work);
        };
        Spend.HistoryChanged += () =>
        {
            (Place.SpendDays, Place.SpendAllProjects) = (Spend.RangeDays, Spend.AllProjects);
            Spend.ShowHistory(SpendDays());
        };
        ShowTab(Place.Tab);
        ShowSidebar(Place.SidebarOpen);
        ApplyState();
        StateChanged += (_, _) => ApplyState();
        UpdateStudioTab();
    }

    /// <summary>One of the tabs, kept for the next time Link opens.</summary>
    private void ShowTab(StudioTab tab)
    {
        CloseViewer();
        Place.Tab = tab;
        var studio = tab == StudioTab.Studio;
        // The tasks are read the first time their tab is shown, with the task the Chat tab's input opened before.
        if (studio && _agentPanel is null)
        {
            _agentPanel = CreateAgentPanel();
            if (_shown is { } shown) ShowRecord(shown);
        }

        ChatPage.Visibility = studio ? Visibility.Collapsed : Visibility.Visible;
        TasksHost.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        StudioTools.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        ShowChosen(ChatTabButton, !studio);
        ShowChosen(StudioTabButton, studio);
        ShowSidebarTip();
        ApplySide();
        RefreshStudio();
    }

    /// <summary>Opens or folds the sidebar of both tabs (the chats, the tasks), and keeps the choice.</summary>
    private void ShowSidebar(bool open)
    {
        Place.SidebarOpen = open;
        _agentPanel?.ShowSidebar(open);
        ChatSidebarColumn.Width = new GridLength(open ? ChatSidebarWidth : 0);
        ChatsHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarButton.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        ShowSidebarTip();
        if (open && Narrow && SideShown) ShowSide(false, Place.Side);
    }

    private void ShowSidebarTip()
    {
        var what = Place.Tab == StudioTab.Studio ? "tasks" : "chats";
        SidebarButton.ToolTip = Place.SidebarOpen ? $"Fold the {what} (Ctrl+B)" : $"Show the {what} (Ctrl+B)";
    }

    /// <summary>Opens the right column on the Harness, or folds it.</summary>
    private void ShowInspector(bool open) => ShowSide(open, StudioSide.Harness);

    /// <summary>Opens the right column on the project, on either tab.</summary>
    private void ShowProjectColumn() => ShowSide(true, StudioSide.Project);

    /// <summary>Opens the right column on a view, or folds it, and keeps the choice; the view it was on last is the one it opens on.</summary>
    private void ShowSide(bool open, StudioSide side)
    {
        Place.InspectorOpen = open;
        Place.Side = side;
        ApplySide();
        if (open && Narrow && Place.SidebarOpen) ShowSidebar(false);
        if (open) RefreshStudio();
    }

    /// <summary>The right column and its toggles as the tab and the choice say.</summary>
    private void ApplySide()
    {
        var side = Place.Side;
        var open = SideShown;
        InspectorColumn.Width = new GridLength(open ? side == StudioSide.Spend ? SpendWidth : InspectorWidth : 0);
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InspectorScroll.Visibility = side == StudioSide.Harness ? Visibility.Visible : Visibility.Collapsed;
        SpendScroll.Visibility = side == StudioSide.Spend ? Visibility.Visible : Visibility.Collapsed;
        ProjectHost.Visibility = side == StudioSide.Project ? Visibility.Visible : Visibility.Collapsed;
        HarnessButton.Foreground = (Brush)FindResource(open && side == StudioSide.Harness ? "AccentBrush" : "TextBrush");
        SpendButton.Foreground = (Brush)FindResource(open && side == StudioSide.Spend ? "AccentBrush" : "TextBrush");
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

    /// <summary>
    /// A view's toggle: folds the column if it shows that view, else opens it on that view. The Harness and the Spend open
    /// on the Studio tab, from the Chat tab too.
    /// </summary>
    private void ToggleSide(StudioSide side)
    {
        if (side != StudioSide.Project && Place.Tab != StudioTab.Studio)
        {
            ShowTab(StudioTab.Studio);
            ShowSide(true, side);
        }
        else ShowSide(!(SideShown && Place.Side == side), side);
    }

    /// <summary>Keeps where the window stands, its normal bounds when it is maximized, with the rest of the settings.</summary>
    public void SaveWindowState()
    {
        var bounds = RestoreBounds;
        if (!bounds.IsEmpty) Place.Remember(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        _settings.Save();
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

    /// <summary>
    /// The window's header (the open task's context, Compact) and its right column as things stand. The Harness and the
    /// Spend view are built only while shown, and again when they open or Link comes up; <paramref name="live"/> (the
    /// session told its context) leaves them as they are if built a moment ago, and the Spend history as it is. The
    /// project's column follows the project on its own.
    /// </summary>
    private void RefreshStudio(bool live = false)
    {
        var context = StudioTask is { } work ? ContextLine(work.Record) ?? "Context not told yet" : null;
        var canCompact = StudioTask is { Busy: false, Waiting: null } open && ResumableTask(open) is not null;
        ShowHeader(context, StudioTask is { } shownTask ? ContextFull(shownTask.Record) : null, canCompact);
        if (!IsVisible || OpenSide is not { } side || side == StudioSide.Project ||
            live && Environment.TickCount64 - _inspectorBuiltAt < LiveInspectorEvery.TotalMilliseconds) return;
        _inspectorBuiltAt = Environment.TickCount64;
        if (side == StudioSide.Harness)
        {
            Inspector.Show(CurrentHarness());
            return;
        }

        Spend.Show(StudioTask is { } shown ? TaskSpend(shown.Record, canCompact) : null, StudioTask?.Run is not null, SpendGauges());
        if (!live) Spend.ShowHistory(SpendDays());
    }

    /// <summary>
    /// The open task's context line (null: no task is open) with how full it is (0 to 1; null: no limit is known), and
    /// whether Compact is offered.
    /// </summary>
    private void ShowHeader(string? context, double? full, bool canCompact)
    {
        ContextText.Text = context ?? string.Empty;
        ContextPanel.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        ContextMeter.Visibility = full is null ? Visibility.Collapsed : Visibility.Visible;
        ContextMeter.Value = (full ?? 0) * 100;
        ContextMeter.Foreground = (Brush)FindResource(full >= ContextWarning ? "BusyOrbBrush" : "SuccessBrush");
        CompactButton.IsEnabled = canCompact;
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
        _settings.Current.Agent.ContextBudget, ProjectMap.Find(record.Workspace) is not null, canCompact, ownMap: ProjectMap.Shared(record.Workspace) is null);

    /// <summary>Both agents' windows as last told, with the shown task's part of each.</summary>
    private IReadOnlyList<SpendGauge> SpendGauges() => SpendReport.Gauges(
        [.. _limits.For(AgentProviderKind.ClaudeCode), .. _limits.For(AgentProviderKind.Codex)], StudioTask?.Record, DateTimeOffset.Now);

    /// <summary>The days the Spend history shows: its range back from today, of the project or of all.</summary>
    private IReadOnlyList<SpendDay> SpendDays()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = today.AddDays(1 - Place.SpendDays);
        return SpendReport.Days(_spending.Since(first, Place.SpendAllProjects ? null : _projects.ActiveProject.Id), first, today);
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

    /// <summary>Another project is shown: the Studio lists its tasks (the list, unless its own task is open), its harness and its column.</summary>
    private void RefreshStudioProject()
    {
        _projectPanel.ShowProject(_projects.ActiveProject);
        if (_agentPanel is not null && _shown is not null && _shown.Project.Id != _projects.ActiveProject.Id) ShowTaskList();
        else RefreshTaskList();
        RefreshStudio();
    }

    /// <summary>
    /// The project tabs as things stand: the window's project lit, a dot on each whose agent waits for the user, else works.
    /// Drawn again only when a tab changed: the Studio's tab follows a run many times a second.
    /// </summary>
    private void UpdateProjectTabs()
    {
        var tabs = _projects.Projects.Select(project =>
        {
            var groups = _works.Where(work => work.Project.Id == project.Id).Select(work => GroupOf(work.Record, work)).ToArray();
            var waits = groups.Count(group => group == TaskGroup.NeedsYou);
            var busy = groups.Count(group => group == TaskGroup.Running);
            var dot = waits > 0 ? (Brush)FindResource("ErrorOrbBrush") : busy > 0 ? (Brush)FindResource("BusyOrbBrush") : null;
            var active = project.Id == _projects.ActiveProject.Id;
            var agents = string.Join(" · ", new[] { waits > 0 ? $"Waiting for you: {waits}" : null, busy > 0 ? $"At work: {busy}" : null }.OfType<string>());
            var hint = string.Join('\n', new[]
            {
                project.WorkspaceRoot ?? "No folder: its chats only",
                agents,
                active ? "Click: its settings, name and ChatGPT conversation" : "Click to show its chats and tasks; every project's agents keep working"
            }.Where(line => line.Length > 0));
            return new ProjectTab(project, project.Name, active, dot, hint);
        }).ToArray();
        if (ProjectTabs.ItemsSource is ProjectTab[] drawn && drawn.SequenceEqual(tabs)) return;
        ProjectTabs.ItemsSource = tabs;
    }

    /// <summary>Another project's tab shows that project; the lit one's opens its menu.</summary>
    private void ProjectTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProjectTab tab } button) return;
        if (tab.Active) ShowProjectMenu(button);
        else ChooseProject(tab.Project);
    }

    /// <summary>The window's project: its settings in the right column, its name, and the ChatGPT conversation it talks in.</summary>
    private void ShowProjectMenu(Button tab)
    {
        var menu = MenuPlacement.Under(tab);
        var settings = new MenuItem { Header = "Project settings", IsCheckable = true, IsChecked = OpenSide == StudioSide.Project };
        settings.Click += (_, _) => ToggleSide(StudioSide.Project);
        menu.Items.Add(settings);
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => RenameCurrentProject();
        menu.Items.Add(rename);
        var remove = new MenuItem { Header = "Remove…", IsEnabled = _projects.Projects.Count > 1 };
        remove.Click += (_, _) => RemoveCurrentProject();
        menu.Items.Add(remove);
        var create = new MenuItem { Header = "New project without a folder…" };
        create.Click += (_, _) => CreateProject();
        menu.Items.Add(create);
        menu.Items.Add(new Separator());
        var link = new MenuItem { Header = "Link ChatGPT conversation…" };
        link.Click += (_, _) => LinkConversation();
        menu.Items.Add(link);
        var unlink = new MenuItem { Header = "Unlink conversation", IsEnabled = _projects.ActiveProject.LinkedConversation is not null };
        unlink.Click += (_, _) => UnlinkConversation();
        menu.Items.Add(unlink);
        menu.IsOpen = true;
    }

    /// <summary>"+": a folder becomes a project, or shows the project it is already; a folder inside one is a project of its own.</summary>
    private void AddProject_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open a folder as a project" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        var known = _projects.Projects.FirstOrDefault(project => project.WorkspaceRoot is { Length: > 0 } root &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), folder, StringComparison.OrdinalIgnoreCase));
        if (known is not null) ChooseProject(known);
        else AddProjectForFolder(dialog.FolderName);
    }

    /// <summary>The wheel moves along the tabs when they do not fit.</summary>
    private void ProjectStrip_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ProjectStrip.ScrollableWidth <= 0) return;
        ProjectStrip.ScrollToHorizontalOffset(ProjectStrip.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>A project's tab chosen. A card is made for its project: it goes with the project Link leaves.</summary>
    private void ChooseProject(LinkProject project)
    {
        if (_cardRun is not null && project.Id != _projects.ActiveProject.Id) CancelCard();
        SwitchProject(project);
    }

    private void Compact_OnClick(object sender, RoutedEventArgs e)
    {
        if (_open is { } work) _ = CompactTask(work);
    }

    private void ChatTab_OnClick(object sender, RoutedEventArgs e) => ShowTab(StudioTab.Chat);

    private void StudioTab_OnClick(object sender, RoutedEventArgs e) => ShowTab(StudioTab.Studio);

    private void Harness_OnClick(object sender, RoutedEventArgs e) => ToggleSide(StudioSide.Harness);

    private void Spend_OnClick(object sender, RoutedEventArgs e) => ToggleSide(StudioSide.Spend);

    private void Sidebar_OnClick(object sender, RoutedEventArgs e) => ShowSidebar(!Place.SidebarOpen);

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => HideLinkWindow();
}
