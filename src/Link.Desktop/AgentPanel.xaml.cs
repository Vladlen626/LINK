using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// The agents' tasks in the Studio: the sidebar with the project's tasks and the agents' usage, and beside it the card
/// before a run, or a task as one thread (the user's words, each run of an agent, notes between) with the box for the
/// next words, or nothing when no task is open. It only shows and asks; the window does the work behind each button.
/// </summary>
public partial class AgentPanel : UserControl
{
    private const double SidebarWidth = 264;
    private const double ThumbnailHeight = 54;

    // Pictures and files the user pasted or dropped, for the card and for the next reply.
    private readonly List<string> _cardAttached = [];
    private readonly List<string> _replyAttached = [];
    private static readonly AgentChoice AutoChoice = new("auto", "Auto — Link picks");
    private AgentCardModel? _card;
    private bool _fillingCard;
    // The specialist is the user's once they chose it (a hand-over starts so): the goal's words pick it no more.
    private bool _roleChosen;
    // Team is the user's once they ticked or cleared it: the goal's words set it no more.
    private bool _teamChosen;
    // The picked specialist (null: General) and its chips; a pick changes the chips in place, so that a click on one
    // that took the goal's focus is not lost to a chip drawn anew.
    private string? _roleId;
    private readonly List<RoleChip> _roleChips = [];
    // The sidebar's rows; the task the panel was given (null: Link's own run); and the row that is lit as drawn, by the
    // task's id (Guid.Empty: Link's own run, null: none), so that the rows are drawn again only when this changes.
    private IReadOnlyList<TaskRow> _rows = [];
    private Guid? _openTask;
    private Guid? _lit;
    // The goal's new words changed the pick as a press on Run took their focus: that Run shows the pick first.
    private bool _pickChangedByRun;
    // The skills on the card as chips; those the user took off or added stay so when Link picks again.
    private IReadOnlyList<string> _skills = [];
    private readonly HashSet<string> _skillsOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _skillsOn = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _planning;
    private bool _replanning;
    private bool _hasTask;
    private bool _replyShown;
    private bool _planOpen = true;
    private readonly HashSet<string> _openSteps = [];
    private readonly HashSet<string> _openWords = [];

    // The menu of a team's step, by its place in the plan; null: the plan's chips open nothing.
    private Func<int, IReadOnlyList<IReadOnlyList<PanelAction>>>? _stepMenu;
    private int _planDone;
    private int _planCount;
    private string _planTitle = "Plan";
    private string _replyTarget = "the agent";
    // What the next words do when it is not the usual reply, such as a training's exam; null: the usual.
    private string? _replyHint;
    private IReadOnlyList<PanelAction> _handOff = [];
    private IReadOnlyList<PanelAction> _more = [];
    private IReadOnlyList<IReadOnlyList<PanelAction>> _setup = [];
    private PanelToggle? _limitToggle;
    private PanelToggle? _noStopToggle;
    private readonly List<CheckBox> _choices = [];

    // The effort box's own tooltip, which a model without effort levels replaces.
    private readonly object? _effortTip;

    public AgentPanel()
    {
        InitializeComponent();
        _effortTip = EffortCombo.ToolTip;
        UpdateReplyHint();
    }

    public event Action<AgentPlan>? RunRequested;
    public event Action? CardCancelled;
    public event Action? StopRequested;

    /// <summary>The decision card's button; true: remember the allowance for the project.</summary>
    public event Action<bool>? DecisionTaken;

    /// <summary>The decision card's Deny.</summary>
    public event Action? DecisionRefused;

    /// <summary>A rating of a training's exam, 1-5.</summary>
    public event Action<int>? RatingChosen;

    /// <summary>Start over in a clean session with the agent's note on what did not work.</summary>
    public event Action? RestartRequested;

    /// <summary>The same session goes on with the strongest model.</summary>
    public event Action? EscalateRequested;

    /// <summary>The user's words: a reply after a run, words to steer it with while the agent works, or the answer to its question.</summary>
    public event Action<string, IReadOnlyList<string>>? ContinueRequested;

    /// <summary>Words for after the run that is going on (Tab): the same session goes on with them once it ends.</summary>
    public event Action<string, IReadOnlyList<string>>? QueueRequested;

    public event Action? FinishRequested;

    /// <summary>Plan mode off: the session's next words go on with edits.</summary>
    public event Action? PlanModeOffRequested;

    /// <summary>The user calls off re-planning: the next words go on as usual.</summary>
    public event Action? ReplanOffRequested;
    public event Action<string>? OpenFileRequested;

    public event Action? NewTaskRequested;
    public event Action<AgentTaskRecord>? TaskSelected;

    /// <summary>Link's own run (the map) of the list's first row is to be shown.</summary>
    public event Action? OwnRunRequested;

    /// <summary>An agent's usage was clicked: it is to be asked for its windows now.</summary>
    public event Action<AgentProviderKind>? LimitRefreshRequested;

    /// <summary>The spent line was clicked: ccusage counts again.</summary>
    public event Action? SpentRefreshRequested;

    /// <summary>The card's Harness button: the Studio's inspector column is to open.</summary>
    public event Action? HarnessRequested;

    /// <summary>The card's Choose folder…: the folder is to be the workspace of the card's project (its id).</summary>
    public event Action<Guid, string>? WorkspaceChosen;

    /// <summary>Opens or folds the sidebar; the window keeps the choice.</summary>
    public void ShowSidebar(bool open)
    {
        SidebarColumn.Width = new GridLength(open ? SidebarWidth : 0);
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The sidebar's list of the project's tasks with the agents' usage at its foot, Link's own run (<paramref name="own"/>)
    /// first while there is one. The row of the task the panel shows is lit.
    /// </summary>
    public void ShowList(IReadOnlyList<TaskRow> tasks, IReadOnlyList<LimitRow> limits, TaskRow? own)
    {
        _rows = own is null ? tasks : tasks.Prepend(own).ToArray();
        DrawRows();
        TasksEmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchField.Visibility = _rows.Count == 0 && SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowLimits(limits);
    }

    /// <summary>The agents' usage windows at the foot of the sidebar; none hides the block.</summary>
    public void ShowLimits(IReadOnlyList<LimitRow> limits)
    {
        LimitsList.ItemsSource = limits;
        LimitsBlock.Visibility = limits.Count == 0 && SpentButton.Visibility != Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The line of what the agents spent under the bars, its details on hover; null hides it.</summary>
    public void ShowSpent(string? line, string? hint)
    {
        SpentText.Text = line ?? string.Empty;
        SpentButton.ToolTip = hint;
        SpentButton.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        if (line is not null) LimitsBlock.Visibility = Visibility.Visible;
    }

    private void Spent_OnClick(object sender, RoutedEventArgs e) => SpentRefreshRequested?.Invoke();

    /// <summary>No task is open in the panel: the middle offers the way to one.</summary>
    public void ShowListView()
    {
        _hasTask = false;
        _openTask = null;
        ShowView(EmptyView);
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var each in new FrameworkElement[] { EmptyView, CardView, TaskView }) each.Visibility = each == view ? Visibility.Visible : Visibility.Collapsed;
        // The sidebar lights the task the middle shows, none while it shows a card or nothing.
        if (LitKey != _lit) DrawRows();
    }

    private Guid? LitKey => TaskView.Visibility == Visibility.Visible ? _openTask ?? Guid.Empty : null;

    /// <summary>The rows that have the search's words, under their groups' lines; the open task's row is lit.</summary>
    private void DrawRows()
    {
        _lit = LitKey;
        var words = SearchBox.Text.Trim();
        var rows = _rows.Where(row => words.Length == 0 || row.Title.Contains(words, StringComparison.CurrentCultureIgnoreCase)
            || row.Preview.Contains(words, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var items = new List<object>();
        foreach (var group in rows.GroupBy(row => row.Group).OrderBy(group => group.Key))
        {
            items.Add(new TaskGroupHeader(GroupTitle(group.Key)));
            items.AddRange(group.Select(row => row with { IsOpen = _lit == (row.Record?.Id ?? Guid.Empty) }));
        }

        TaskList.ItemsSource = items;
        NoMatchText.Visibility = rows.Length == 0 && _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GroupTitle(TaskGroup group) => group switch
    {
        TaskGroup.NeedsYou => "Needs you",
        TaskGroup.Running => "Running",
        _ => "Recent"
    };

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawRows();
    }

    private void SearchBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    /// <summary>
    /// Ctrl+N: a card for a new task, or, while a card is shown, its goal takes the focus, so that what is written there is
    /// not lost to a new card.
    /// </summary>
    public void RequestNewTask()
    {
        if (CardView.Visibility == Visibility.Visible) GoalBox.Focus();
        else NewTaskRequested?.Invoke();
    }

    /// <summary>A menu of the actions, a line between groups; the current choice of a group is checked.</summary>
    private static void ShowMenu(Button anchor, IReadOnlyList<IReadOnlyList<PanelAction>> groups, PlacementMode placement)
    {
        var menu = placement == PlacementMode.Top ? MenuPlacement.Over(anchor) : MenuPlacement.Under(anchor);
        foreach (var group in groups.Where(group => group.Count > 0))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            foreach (var action in group)
            {
                var item = new MenuItem { Header = action.Label, ToolTip = action.Hint, IsChecked = action.Checked };
                item.Click += (_, _) => action.Run();
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    private void TaskRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskRow row }) return;
        // The sidebar stays beside a card: the card gives way to the task picked, as its Cancel would.
        if (_card is not null) CancelCard_OnClick(sender, e);
        if (row.Record is { } record) TaskSelected?.Invoke(record);
        else OwnRunRequested?.Invoke();
    }

    private void NewTask_OnClick(object sender, RoutedEventArgs e) => NewTaskRequested?.Invoke();

    private void LimitRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LimitRow row }) LimitRefreshRequested?.Invoke(row.Provider);
    }
}
