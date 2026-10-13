using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// An agent as the card offers it: its own models and, for each, the efforts it takes; the default the card starts
/// from (Settings' choice, null: the CLI's own, named by <see cref="OwnModel"/> and <see cref="OwnEffort"/>), and its
/// usage windows.
/// </summary>
public sealed record AgentCardProvider(
    AgentProviderKind Kind,
    string Name,
    IReadOnlyList<AgentChoice> Models,
    Func<string?, IReadOnlyList<AgentChoice>> EffortsOf,
    string OwnModel,
    string OwnEffort,
    string? Model,
    string? Effort,
    IReadOnlyList<LimitRow> Limits);

/// <summary>
/// What the task card starts from. A hand-over shows its brief on the same card with <see cref="FixedProvider"/> and
/// its own <see cref="Title"/> and <see cref="RunLabel"/>. <see cref="Images"/> are the pictures that go along.
/// </summary>
public sealed record AgentCardModel(
    IReadOnlyList<AgentCardProvider> Providers,
    AgentProviderKind Provider,
    string ProjectName,
    string? Workspace,
    bool WorkspaceHasVcs,
    string Attachments,
    IReadOnlyList<string> Images,
    string Goal,
    string? CheckCommand,
    AgentAccessChoice Access)
{
    public string Title { get; init; } = "New task";
    public string? RunLabel { get; init; }
    public bool FixedProvider { get; init; }

    /// <summary>The effort list starts with Auto, Link's pick for each run, and the card starts on it.</summary>
    public bool AutoEffort { get; init; }

    /// <summary>The specialists the card offers (none: no row) and the one it starts on, Link's pick; null: General.</summary>
    public IReadOnlyList<AgentRole> Roles { get; init; } = [];

    public string? RoleId { get; init; }

    /// <summary>The workspace's skills, which the card picks from and offers to add (none: no row).</summary>
    public IReadOnlyList<ProjectSkill> WorkspaceSkills { get; init; } = [];

    /// <summary>The skills the card starts with, such as a task's own on a hand-over; null: picked from the goal.</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>"Context budget 400k · 5 skills · MCP: unreal, blender": what else goes with the run, shown muted under the setup.</summary>
    public string HarnessLine { get; init; } = string.Empty;

    /// <summary>The project the card was opened for: Run starts the task in it, whichever project the overlay shows by then.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Why the card is not on Settings' default agent (its limit is reached now); null when it is.</summary>
    public string? Notice { get; init; }

    /// <summary>Makes the card anew from Settings as they stand, for when they are saved while it is open; see <see cref="AgentPanel.RefreshSetup"/>.</summary>
    public Func<AgentCardModel>? Renew { get; init; }

    /// <summary>The card offers a team (Settings allow it); the box is ticked while the goal reads as team work (<see cref="TeamRunner.Suggests"/>).</summary>
    public bool TeamOffered { get; init; }
}

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
    private bool _hasTask;
    private bool _replyShown;
    private bool _planOpen = true;
    private readonly HashSet<string> _openSteps = [];
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

    /// <summary>The button of the cheaper hint: the card of a hand-off to the custom model is wanted.</summary>
    public event Action? CheaperRequested;

    /// <summary>Plan mode off: the session's next words go on with edits.</summary>
    public event Action? PlanModeOffRequested;
    public event Action<string>? OpenFileRequested;

    public event Action? NewTaskRequested;
    public event Action<AgentTaskRecord>? TaskSelected;

    /// <summary>Link's own run (the map) of the list's first row is to be shown.</summary>
    public event Action? OwnRunRequested;

    /// <summary>An agent's usage was clicked: it is to be asked for its windows now.</summary>
    public event Action<AgentProviderKind>? LimitRefreshRequested;

    /// <summary>The card's Harness button: the Studio's inspector column is to open.</summary>
    public event Action? HarnessRequested;

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
        ShowLimits(limits);
    }

    /// <summary>The agents' usage windows at the foot of the sidebar; none hides the block.</summary>
    public void ShowLimits(IReadOnlyList<LimitRow> limits)
    {
        LimitsList.ItemsSource = limits;
        LimitsBlock.Visibility = limits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>No task is open in the panel: the middle offers the way to one.</summary>
    public void ShowListView()
    {
        _hasTask = false;
        _openTask = null;
        ShowView(EmptyView);
    }

    public void ShowCard(AgentCardModel card)
    {
        _card = card;
        _fillingCard = true;
        CardTitleText.Text = card.Title;
        ProviderCombo.ItemsSource = card.Providers.Select(provider => provider.Name).ToArray();
        ProviderCombo.SelectedIndex = Math.Max(0, card.Providers.ToList().FindIndex(provider => provider.Kind == card.Provider));
        ProviderCombo.IsEnabled = !card.FixedProvider;
        AccessCombo.SelectedIndex = card.Access == AgentAccessChoice.Auto ? 1 : 0;
        PlanFirstBox.IsChecked = false;
        PlanFirstBox.Visibility = card.FixedProvider ? Visibility.Collapsed : Visibility.Visible;
        _teamChosen = false;
        TeamBox.Visibility = card.TeamOffered && !card.FixedProvider ? Visibility.Visible : Visibility.Collapsed;
        TeamBox.IsChecked = TeamBox.Visibility == Visibility.Visible && TeamRunner.Suggests(card.Goal, card.Roles);
        TrainBox.Visibility = card.Roles.Count > 0 && !card.FixedProvider ? Visibility.Visible : Visibility.Collapsed;
        TrainBox.IsChecked = false;
        BackupBox.IsChecked = true;
        GoalBox.Text = card.Goal;
        ProjectText.Text = card.ProjectName;
        WorkspaceText.Text = card.Workspace ?? "Not chosen: choose it in the Project tab";
        WorkspaceText.Foreground = (Brush)FindResource(card.Workspace is null ? "ErrorBrush" : "MutedBrush");
        HarnessLineText.Text = card.HarnessLine;
        CardNoticeText.Text = card.Notice ?? string.Empty;
        CardNoticeText.Visibility = card.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentsText.Text = card.Attachments;
        AttachmentsText.Visibility = card.Attachments.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowImages(card.Images);
        CardWarningText.Visibility = Visibility.Collapsed;
        ShowAgentChoices();
        ShowRoles(card);
        _fillingCard = false;
        UpdateCard();
        ShowView(CardView);
        Window.GetWindow(this)?.Activate();
        GoalBox.Focus();
        GoalBox.CaretIndex = GoalBox.Text.Length;
    }

    /// <summary>
    /// Settings were saved while the card is open: its agents (their models and efforts, the defaults, Auto, whether the
    /// custom model is offered), their usage and the line of what goes with the run are made anew. The goal, the
    /// specialist and the skills stay, and so do the agent, model and effort the user picked here while they still exist;
    /// a model or effort the user left alone follows the new default.
    /// </summary>
    public void RefreshSetup()
    {
        if (_card is not { Renew: { } renew } old || SelectedAgent is not { } was) return;
        var (model, effort, auto) = (SelectedModel, SelectedEffort, SelectedAutoEffort);
        var fresh = renew();
        var card = _card = old with { Providers = fresh.Providers, AutoEffort = fresh.AutoEffort, HarnessLine = fresh.HarnessLine };
        _fillingCard = true;
        ProviderCombo.ItemsSource = card.Providers.Select(provider => provider.Name).ToArray();
        var kinds = card.Providers.Select(provider => provider.Kind).ToList();
        ProviderCombo.SelectedIndex = Math.Max(0, kinds.IndexOf(kinds.Contains(was.Kind) ? was.Kind : card.Provider));
        HarnessLineText.Text = card.HarnessLine;
        ShowAgentChoices();
        if (SelectedAgent is { Kind: var kind } agent && kind == was.Kind)
        {
            var models = agent.Models.ToList();
            if (model != was.Model && (model is null || models.Any(choice => choice.Value == model)))
            {
                ModelCombo.SelectedIndex = model is null ? 0 : models.FindIndex(choice => choice.Value == model) + 1;
            }

            // Auto was the default when the card offered it; otherwise the agent's own default effort.
            var effortChanged = old.AutoEffort ? !auto : effort != was.Effort;
            if (effortChanged && (effort is null || agent.EffortsOf(SelectedModel).Any(choice => choice.Value == effort)))
            {
                ShowEfforts(agent, effort, auto: false);
            }
            else
            {
                ShowEfforts(agent, agent.Effort, auto: true);
            }
        }

        _fillingCard = false;
        UpdateCard();
    }

    /// <summary>The card is gone: back to the open task, or to the empty middle.</summary>
    public void HideCard()
    {
        _card = null;
        ShowView(_hasTask ? TaskView : EmptyView);
    }

    public void ShowCardWarning(string message)
    {
        CardWarningText.Text = message;
        CardWarningText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A task opens in the panel: its goal and where it works, and its id, by which its row in the sidebar is lit (null:
    /// Link's own run); its thread comes with <see cref="ShowThread"/>.
    /// </summary>
    public void ShowTaskView(string goal, string where, Guid? taskId)
    {
        _hasTask = true;
        _openTask = taskId;
        GoalText.Text = goal;
        GoalText.ToolTip = goal;
        WhereText.Text = where;
        WhereText.ToolTip = where;
        if (_card is null) ShowView(TaskView);
    }

    /// <summary>A new run or another task: the cards of the last one go.</summary>
    public void ResetCards()
    {
        QuestionCard.Visibility = Visibility.Collapsed;
        DecisionCard.Visibility = Visibility.Collapsed;
        CheaperRow.Visibility = Visibility.Collapsed;
        RestartCard.Visibility = Visibility.Collapsed;
        LimitCard.Visibility = Visibility.Collapsed;
        RatingCard.Visibility = Visibility.Collapsed;
        _replyHint = null;
        UpdateReplyHint();
    }

    /// <summary>The line under the goal: who works, on what model, with what access, and for how long.</summary>
    public void SetHeader(string header) => HeaderText.Text = header;

    /// <summary>How the task stands, in the pill beside its goal.</summary>
    public void SetStatus(string text, Brush dot)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot;
    }

    public void SetRunning(bool running)
    {
        _running = running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TaskButtons.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        UpdateReplyHint();
        if (running) ShowReply(true);
        else UpdateComposer();
    }

    /// <summary>The task's thread, oldest first, scrolled to its end: the latest is what counts.</summary>
    public void ShowThread(IReadOnlyList<object> items)
    {
        ThreadList.ItemsSource = items;
        Dispatcher.BeginInvoke(ThreadScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The running turn grew: the thread follows it while the user is at its end.</summary>
    public void FollowThread()
    {
        if (ThreadScroll.VerticalOffset >= ThreadScroll.ScrollableHeight - 24) ThreadScroll.ScrollToEnd();
    }

    /// <summary>The reply box, offered while a session can go on; <paramref name="note"/> says why it is not, if so.</summary>
    public void ShowReply(bool show, string? note = null)
    {
        _replyShown = show;
        ReplyNoteText.Text = note ?? string.Empty;
        ReplyNoteText.Visibility = !show && note is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposer();
    }

    /// <summary>The choices of the Hand off ▾ and ⋯ menus; an empty list hides the button.</summary>
    public void ShowMenus(IReadOnlyList<PanelAction> handOff, IReadOnlyList<PanelAction> more)
    {
        _handOff = handOff;
        _more = more;
        HandOffButton.Visibility = handOff.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = more.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateComposer();
    }

    /// <summary>
    /// What the next words run on, in the composer beside the send button: model, effort and specialist, with groups of
    /// choices to change them. No choices hide it.
    /// </summary>
    public void ShowSetup(string label, IReadOnlyList<IReadOnlyList<PanelAction>> choices)
    {
        _setup = choices;
        SetupButton.Content = label + " ▾";
        UpdateComposer();
    }

    /// <summary>Whether Finish is offered: not for Link's own answer, such as a map, nor for a task shown only to be read.</summary>
    public void ShowFinish(bool finish)
    {
        FinishButton.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposer();
    }

    /// <summary>Whether the task's session goes on in plan mode, where only reading and planning work: its chip turns it off.</summary>
    public void ShowPlanMode(bool planning)
    {
        _planning = planning;
        PlanModeButton.Visibility = planning ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplyHint();
        UpdateComposer();
    }

    /// <summary>
    /// The agent's plan as it last wrote it, as its own app shows it: ✓ done, ● under way (in the agent's words for it,
    /// when it gave any), ○ to do; a step by its first sentence, the rest a click away. A team's step also names who does
    /// it and as which specialist, in the colour of that specialist's jellyfish, found by name among
    /// <paramref name="roles"/>, and why that worker under its words.
    /// </summary>
    public void ShowPlan(IReadOnlyList<AgentPlanItem> plan, IReadOnlyList<AgentRole>? roles = null, string title = "Plan")
    {
        _planTitle = title;
        var done = (Brush)FindResource("SuccessBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var text = (Brush)FindResource("TextBrush");
        PlanList.ItemsSource = plan.Select(item =>
        {
            var (mark, markBrush, words, brush) = item.Done ? ("✓", done, item.Text, muted)
                : item.Active ? ("●", accent, item.Doing ?? item.Text, text)
                : ("○", muted, item.Text, text);
            var (title, rest) = TeamPlan.Headline(words);
            var detail = string.Join("\n\n", new[] { rest, item.Why is { Length: > 0 } why ? "Why this worker: " + why : string.Empty }.Where(part => part.Length > 0));
            // The agent's own plan names neither.
            var team = !string.IsNullOrEmpty(item.Worker) || !string.IsNullOrEmpty(item.Role);
            var color = !team ? null
                : roles?.FirstOrDefault(each => string.Equals(each.Name, item.Role, StringComparison.OrdinalIgnoreCase)) is { } role
                    ? RoleBrushes.Of(ProjectRoles.ColorOf(role, roles))
                    : RoleBrushes.Link;
            // A step opened stays open while the plan is drawn again as the team moves on.
            return new PlanRow(mark, markBrush, title, brush)
            {
                Detail = detail, Worker = item.Worker ?? string.Empty, RoleName = item.Role ?? string.Empty, RoleBrush = color,
                Open = _openSteps.Contains(title)
            };
        }).ToArray();
        _planDone = plan.Count(item => item.Done);
        _planCount = plan.Count;
        PlanCard.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanList.Visibility = _planOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowPlanLabel();
    }

    private void ShowPlanLabel() => PlanLabel.Text = $"{_planTitle} · {_planDone} of {_planCount} {(_planOpen ? "▾" : "▸")}";

    /// <summary>A training's exam waits for the user's rating, 1-5, with what it means; null hides the card.</summary>
    public void ShowRating(string? text)
    {
        RatingText.Text = text ?? string.Empty;
        RatingCard.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the next words do when it is not the usual reply, in the box's placeholder; null: the usual.</summary>
    public void ShowReplyHint(string? hint)
    {
        _replyHint = hint;
        UpdateReplyHint();
    }

    /// <summary>What the task changed, beside its standing: "+181 −12" or "5 files", the files as its hint; empty hides it.</summary>
    public void ShowChanges(string text, string files)
    {
        ChangesText.Text = text;
        ChangesText.ToolTip = files.Length == 0 ? null : files;
        ChangesText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The task's tokens by agent, with what the money means on a subscription; empty hides it.</summary>
    public void ShowUsage(string usage, string hint)
    {
        UsageText.Text = usage;
        UsageText.ToolTip = hint;
        UsageText.Visibility = usage.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An agent's windows anew on an open card, as they are once asked again.</summary>
    public void ShowCardLimits(AgentProviderKind provider, IReadOnlyList<LimitRow> limits)
    {
        if (_card is not { } card) return;
        _card = card with { Providers = card.Providers.Select(agent => agent.Kind == provider ? agent with { Limits = limits } : agent).ToArray() };
        if (SelectedAgent?.Kind == provider) CardLimitsList.ItemsSource = limits;
    }

    /// <summary>The agent's question with its options; null hides it.</summary>
    public void ShowQuestion(AgentQuestion? question)
    {
        QuestionCard.Visibility = question is null ? Visibility.Collapsed : Visibility.Visible;
        OptionsPanel.Children.Clear();
        _choices.Clear();
        SendChoicesButton.Visibility = Visibility.Collapsed;
        if (question is null)
        {
            UpdateReplyHint();
            return;
        }

        QuestionText.Text = question.Question;
        foreach (var option in question.Options)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = option.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (option.Description.Length > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = option.Description,
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (question.MultiSelect)
            {
                var box = new CheckBox { Content = content, Margin = new Thickness(0, 4, 0, 0), Tag = option.Label };
                _choices.Add(box);
                OptionsPanel.Children.Add(box);
                continue;
            }

            var button = new Button
            {
                Style = (Style)FindResource("GhostButtonStyle"),
                Content = content,
                Margin = new Thickness(-8, 0, 0, 2),
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Answer with this"
            };
            button.Click += (_, _) => Answer(option.Label);
            OptionsPanel.Children.Add(button);
        }

        SendChoicesButton.Visibility = question.MultiSelect ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplyHint();
    }

    /// <summary>A usage limit near or reached, with the ways on and the box of what goes on by itself; null hides it.</summary>
    public void ShowLimit(string? text, IReadOnlyList<PanelAction> actions, PanelToggle? toggle = null)
    {
        LimitCard.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        LimitText.Text = text ?? string.Empty;
        _limitToggle = toggle;
        LimitBox.Visibility = toggle is null ? Visibility.Collapsed : Visibility.Visible;
        LimitBox.Content = toggle?.Label;
        LimitBox.ToolTip = toggle?.Hint;
        LimitBox.IsChecked = toggle?.On == true;
        LimitActions.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button { Style = (Style)FindResource("SmallChipStyle"), Content = action.Label, ToolTip = action.Hint };
            button.Click += (_, _) =>
            {
                LimitCard.Visibility = Visibility.Collapsed;
                action.Run();
            };
            LimitActions.Children.Add(button);
        }
    }

    /// <summary>A choice for the user; <paramref name="canRemember"/> offers "always for this project".</summary>
    public void ShowDecision(string text, string button, bool canRemember)
    {
        DecisionText.Text = text;
        DecisionButton.Content = button;
        AlwaysBox.IsChecked = false;
        AlwaysBox.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        DecisionCard.Visibility = Visibility.Visible;
    }

    public void HideDecision() => DecisionCard.Visibility = Visibility.Collapsed;

    /// <summary>A hint that the custom model could do what is left, with the button that hands the task over; null hides it.</summary>
    public void ShowCheaper(string? hint, string customName)
    {
        CheaperRow.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        CheaperText.Text = hint ?? string.Empty;
        CheaperButtonText.Text = $"Hand off to {customName}";
    }

    /// <summary>Start over, and Escalate when the task is not already on the strongest model.</summary>
    public void ShowRestart(bool show, bool escalate = false)
    {
        RestartCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EscalateButton.Visibility = escalate ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Link's own step around a run, such as asking for a note; while it runs, Stop shows and the choices hide.</summary>
    public void ShowStep(string step, bool running)
    {
        StatusText.Text = step;
        SetRunning(running);
        if (!running) return;
        ResetCards();
        // Link's own step, such as asking for a note, takes no words for the agent.
        ShowReply(false);
    }

    /// <summary>Who the words in the box go to, and what happens to them.</summary>
    public void SetReplyTarget(string agentName)
    {
        _replyTarget = agentName;
        UpdateReplyHint();
    }

    private void UpdateReplyHint()
    {
        var answering = !_running && QuestionCard.Visibility == Visibility.Visible;
        ReplyPlaceholder.Text = _running
            ? $"Steer {_replyTarget} now: Enter stops the step and goes on with your words · Tab: after this step"
            : answering
                ? $"Answer {_replyTarget} in your own words"
                : _planning
                    ? $"Reply to {_replyTarget}: the session stays in plan mode, reading and planning only"
                    : _replyHint ?? $"Reply to {_replyTarget}: the same session goes on with your words";
        // The send button is a round icon: its tooltip carries the label, and so does its name for UI Automation.
        var label = _running ? "Steer now" : answering ? "Answer" : "Send";
        AutomationProperties.SetName(ContinueButton, label);
        ContinueButton.ToolTip = _running
            ? "Steer now: stops the current step and goes on in the same session with your words; nothing is read again (Enter) · Tab: After this step, your words wait for it to end"
            : answering
                ? "Answer: the same session goes on with your words as the answer (Enter)"
                : "Send: the same session goes on with your words, with the same access and folder (Enter)";
    }

    /// <summary>
    /// The box at the foot of a task: the words and the send button while a session can go on, the task's chips (Plan
    /// mode, Hand off, Finish) in it while the agent rests. A task that takes no words and offers no chip has no box.
    /// </summary>
    private void UpdateComposer()
    {
        var chips = !_running && (PlanModeButton.Visibility == Visibility.Visible || HandOffButton.Visibility == Visibility.Visible
            || FinishButton.Visibility == Visibility.Visible);
        ReplyEditor.Visibility = _replyShown ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.Visibility = _replyShown ? Visibility.Visible : Visibility.Collapsed;
        SetupButton.Visibility = _replyShown && _setup.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ComposerFooter.Margin = new Thickness(0, _replyShown ? 6 : 0, 0, 0);
        ReplyPanel.Visibility = _replyShown || chips ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var each in new FrameworkElement[] { EmptyView, CardView, TaskView }) each.Visibility = each == view ? Visibility.Visible : Visibility.Collapsed;
        // The sidebar lights the task the middle shows, none while it shows a card or nothing.
        if (LitKey != _lit) DrawRows();
    }

    private Guid? LitKey => TaskView.Visibility == Visibility.Visible ? _openTask ?? Guid.Empty : null;

    private void DrawRows()
    {
        _lit = LitKey;
        TaskList.ItemsSource = _rows.Select(row => row with { IsOpen = _lit == (row.Record?.Id ?? Guid.Empty) }).ToArray();
    }

    /// <summary>Whether a file goes to the agent as an image rather than by its path.</summary>
    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>Saves a pasted picture where Link keeps its images and returns the file; set by the window.</summary>
    public Func<BitmapSource, string>? SaveImage { get; set; }

    // Ctrl+V with a picture or files on the clipboard attaches them; text pastes as always.
    private void Panel_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !CanAttach) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                Attach(Clipboard.GetFileDropList().Cast<string>());
                e.Handled = true;
            }
            else if (Clipboard.ContainsImage() && SaveImage is { } save && Clipboard.GetImage() is { } image)
            {
                Attach([save(image)]);
                e.Handled = true;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or IOException)
        {
            // A busy clipboard pastes nothing; the user tries again.
        }
    }

    private void Panel_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = CanAttach ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !CanAttach) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Attach(files);
        e.Handled = true;
    }

    /// <summary>Pictures and files go with the card while it is open, else with the reply.</summary>
    private bool CanAttach => _card is not null || ReplyPanel.Visibility == Visibility.Visible;

    private List<string> Attached => _card is not null ? _cardAttached : _replyAttached;

    private void Attach(IEnumerable<string> paths)
    {
        var attached = Attached;
        // A folder is no file to read; a file already there comes once.
        foreach (var path in paths.Where(File.Exists))
        {
            if (!attached.Contains(path, StringComparer.OrdinalIgnoreCase)) attached.Add(path);
        }

        ShowAttached();
    }

    /// <summary>What was pasted or dropped, as small pictures and file names; a click takes one off.</summary>
    private void ShowAttached()
    {
        Fill(CardAttachedPanel, _cardAttached);
        Fill(ReplyAttachedPanel, _replyAttached);
        ContinueButton.IsEnabled = ReplyBox.Text.Trim().Length > 0 || _replyAttached.Count > 0;
    }

    private void Fill(WrapPanel panel, List<string> attached)
    {
        panel.Children.Clear();
        foreach (var path in attached.ToArray())
        {
            UIElement content = new TextBlock { Text = "📄 " + Path.GetFileName(path), Margin = new Thickness(8, 3, 8, 3), FontSize = 12 };
            if (IsImage(path) && Thumbnail(path) is { } picture)
            {
                content = new Image { Source = picture, Height = ThumbnailHeight, Stretch = Stretch.Uniform };
            }

            var chip = new Border
            {
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("GlassBorderSoftBrush"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                ToolTip = $"{path}\nClick to take it off",
                Child = content
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                attached.Remove(path);
                ShowAttached();
            };
            panel.Children.Add(chip);
        }

        panel.Visibility = attached.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static BitmapImage? Thumbnail(string path)
    {
        try
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.UriSource = new Uri(path);
            picture.DecodePixelHeight = (int)(ThumbnailHeight * 2);
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Small pictures of the images that go along; a click shows the file.</summary>
    private void ShowImages(IReadOnlyList<string> images)
    {
        ImagesPanel.Children.Clear();
        foreach (var path in images.Where(File.Exists))
        {
            // A picture that does not read still goes; only its preview is missing.
            if (Thumbnail(path) is { } picture)
            {
                var thumbnail = new Border
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = (Brush)FindResource("GlassBorderSoftBrush"),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Cursor = Cursors.Hand,
                    ToolTip = $"Goes to the agent as an image: {path}",
                    Child = new Image { Source = picture, Height = ThumbnailHeight, Stretch = Stretch.Uniform }
                };
                thumbnail.MouseLeftButtonUp += (_, _) => OpenFileRequested?.Invoke(path);
                ImagesPanel.Children.Add(thumbnail);
            }
        }

        ImagesPanel.Visibility = ImagesPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private AgentCardProvider? SelectedAgent =>
        _card is { } card && ProviderCombo.SelectedIndex >= 0 ? card.Providers[ProviderCombo.SelectedIndex] : null;

    /// <summary>The selected agent's models and efforts, its default first chosen, and its usage windows.</summary>
    private void ShowAgentChoices()
    {
        if (SelectedAgent is not { } agent) return;
        var filling = _fillingCard;
        _fillingCard = true;
        ModelCombo.ItemsSource = agent.Models.Prepend(new AgentChoice(string.Empty, agent.OwnModel)).ToArray();
        ModelCombo.SelectedIndex = Math.Max(0, agent.Models.ToList().FindIndex(model => model.Value == agent.Model) + 1);
        ShowEfforts(agent, agent.Effort, auto: true);
        CardLimitsList.ItemsSource = agent.Limits;
        _fillingCard = filling;
    }

    /// <summary>
    /// The efforts the chosen model takes, keeping <paramref name="effort"/> when it is one of them. A card on Auto
    /// offers Auto first, and <paramref name="auto"/> chooses it.
    /// </summary>
    private void ShowEfforts(AgentCardProvider agent, string? effort, bool auto)
    {
        var efforts = agent.EffortsOf(SelectedModel);
        // A model with no effort levels (the custom model) has nothing to choose, and Auto nothing to pick.
        EffortCombo.IsEnabled = efforts.Count > 0;
        EffortCombo.ToolTip = efforts.Count > 0 ? _effortTip : "This model takes no effort setting";
        if (efforts.Count == 0)
        {
            EffortCombo.ItemsSource = new[] { new AgentChoice(string.Empty, "None") };
            EffortCombo.SelectedIndex = 0;
            return;
        }

        var offered = _card?.AutoEffort == true;
        var choices = efforts.Prepend(new AgentChoice(string.Empty, agent.OwnEffort));
        EffortCombo.ItemsSource = (offered ? choices.Prepend(AutoChoice) : choices).ToArray();
        EffortCombo.SelectedIndex = offered && auto ? 0 : (offered ? 1 : 0) + Math.Max(0, efforts.ToList().FindIndex(choice => choice.Value == effort) + 1);
    }

    // General, no role, is null.
    private string? SelectedRoleId => _roleId;
    private AgentRole? SelectedRole => _card?.Roles.FirstOrDefault(role => role.Id == _roleId);

    // The first item of each list is the CLI's own choice, sent as none; Auto, when offered, is before it.
    private string? SelectedModel => ModelCombo.SelectedItem is AgentChoice { Value.Length: > 0 } model ? model.Value : null;
    private bool SelectedAutoEffort => ReferenceEquals(EffortCombo.SelectedItem, AutoChoice);
    private string? SelectedEffort => EffortCombo.SelectedItem is AgentChoice { Value.Length: > 0 } effort && !SelectedAutoEffort ? effort.Value : null;

    // The items follow AgentAccessChoice: edit, auto.
    private bool SelectedAuto => AccessCombo.SelectedIndex == 1;

    private void Provider_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCard) return;
        ShowAgentChoices();
        UpdateCard();
    }

    private void Model_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCard || SelectedAgent is not { } agent) return;
        ShowEfforts(agent, SelectedEffort, SelectedAutoEffort);
    }

    /// <summary>The specialist and the skills the card starts with: Link's pick from the goal, or a hand-over's own.</summary>
    private void ShowRoles(AgentCardModel card)
    {
        _roleChosen = card.FixedProvider;
        _pickChangedByRun = false;
        _skillsOff.Clear();
        _skillsOn.Clear();
        RoleRow.Visibility = card.Roles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowRoleChips(card);
        SelectRole(card.RoleId);
        SkillsRow.Visibility = card.WorkspaceSkills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSkills(card.Skills ?? PickedSkills());
    }

    /// <summary>One chip for General and one for each specialist, each with its jellyfish in the specialist's colour.</summary>
    private void ShowRoleChips(AgentCardModel card)
    {
        RolesPanel.Children.Clear();
        _roleChips.Clear();
        if (card.Roles.Count == 0) return;
        AddRoleChip(null, "General", RoleBrushes.Link, "No specialist: the agent works as its rules say");
        foreach (var role in card.Roles) AddRoleChip(role.Id, role.Name, RoleBrushes.Of(ProjectRoles.ColorOf(role, card.Roles)), role.Description);
    }

    private void AddRoleChip(string? id, string name, Brush color, string tip)
    {
        var look = new Border
        {
            Height = 28,
            Padding = new Thickness(9, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Control { Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), Focusable = false, Foreground = color, Template = (ControlTemplate)FindResource("LinkGlyphTemplate") },
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Text = name }
                }
            }
        };
        var button = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 6, 6, 0),
            ToolTip = tip,
            Content = look
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => PickRole(id);
        RolesPanel.Children.Add(button);
        _roleChips.Add(new RoleChip(id, look, color));
    }

    /// <summary>The user's pick of a specialist: it is theirs from now on, and the skills follow it.</summary>
    private void PickRole(string? id)
    {
        _roleChosen = true;
        SelectRole(id);
        ShowSkills(PickedSkills());
    }

    private void SelectRole(string? id)
    {
        _roleId = _card?.Roles.Any(role => role.Id == id) == true ? id : null;
        // The picked chip is filled and rimmed in its specialist's colour; the others are quiet. They change in place.
        foreach (var chip in _roleChips)
        {
            var picked = chip.Id == _roleId;
            chip.Look.BorderBrush = picked ? chip.Color : (Brush)FindResource("GlassBorderSoftBrush");
            chip.Look.Background = picked ? (Brush)FindResource("ActiveContextBrush") : Brushes.Transparent;
        }

        RolePickText.Visibility = _roleId is not null && !_roleChosen ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record RoleChip(string? Id, Border Look, Brush Color);

    /// <summary>The skills for the goal and the specialist as they stand: Link's picks without those taken off, and those added.</summary>
    private IReadOnlyList<string> PickedSkills() =>
        _card is null
            ? []
            : [.. ProjectRoles.SkillsFor(GoalBox.Text, SelectedRole, _card.WorkspaceSkills).Where(name => !_skillsOff.Contains(name)).Union(_skillsOn, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The skills as chips: a click takes one off, and the last chip adds one of the workspace's that is not there.</summary>
    private void ShowSkills(IReadOnlyList<string> skills)
    {
        _skills = skills;
        SkillsPanel.Children.Clear();
        foreach (var name in skills)
        {
            var chip = new Button { Style = (Style)FindResource("SmallChipStyle"), Content = name + " ✕", ToolTip = "Take it off: the agent is not told to read it first" };
            chip.Click += (_, _) =>
            {
                _skillsOn.Remove(name);
                _skillsOff.Add(name);
                ShowSkills([.. _skills.Where(each => each != name)]);
            };
            SkillsPanel.Children.Add(chip);
        }

        var others = _card?.WorkspaceSkills.Where(skill => !skills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase)).ToArray() ?? [];
        if (others.Length == 0) return;
        var add = new Button { Style = (Style)FindResource("SmallChipStyle"), Content = "+ skill", ToolTip = "Add a skill of the workspace: the agent is told to read it first" };
        add.Click += (_, _) => ShowMenu(add, [[.. others.Select(skill => new PanelAction(skill.Name, skill.Description, () =>
        {
            _skillsOff.Remove(skill.Name);
            _skillsOn.Add(skill.Name);
            ShowSkills([.. _skills, skill.Name]);
        }))]], PlacementMode.Bottom);
        SkillsPanel.Children.Add(add);
    }

    /// <summary>
    /// The goal as the user left it: the specialist is picked from the new words while it is still Link's pick, and the
    /// skills are, whoever chose the specialist. A hand-over keeps the task's.
    /// </summary>
    private void Goal_OnLostFocus(object sender, RoutedEventArgs e)
    {
        // A press on Run takes the focus before its click: that click is not to run a pick the card has not shown yet.
        _pickChangedByRun = PickFromGoal() && RunButton.IsMouseOver;
    }

    /// <summary>Picks the specialist and the skills from the goal as it stands; whether the pick changed.</summary>
    private bool PickFromGoal()
    {
        if (_card is not { FixedProvider: false } card) return false;
        var role = SelectedRoleId;
        if (!_roleChosen && card.Roles.Count > 0) SelectRole(ProjectRoles.Suggest(GoalBox.Text, card.Roles)?.Id);
        // Team follows the goal until the user ticks or clears it.
        if (!_teamChosen && !Train && TeamBox.Visibility == Visibility.Visible) TeamBox.IsChecked = TeamRunner.Suggests(GoalBox.Text, card.Roles);
        // The chips stay as they are unless the pick changed: a click on one lost the focus of the goal, and a chip
        // drawn anew under the mouse would take that click.
        var skills = PickedSkills();
        var changed = role != SelectedRoleId || !skills.SequenceEqual(_skills);
        if (changed) ShowSkills(skills);
        return changed;
    }

    /// <summary>Ctrl+Enter in the goal runs the task as the Run button does, with the same guard against a pick not yet seen.</summary>
    private void Goal_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        _pickChangedByRun = PickFromGoal();
        Run_OnClick(RunButton, e);
    }

    private void Card_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_fillingCard) UpdateCard();
    }

    private void UpdateCard()
    {
        if (_card is not { } card) return;
        BackupBox.Visibility = !card.WorkspaceHasVcs ? Visibility.Visible : Visibility.Collapsed;
        CheckText.Text = $"After the edits Link runs the check itself, and the agent is told so: {card.CheckCommand}. " +
                         $"While it fails, the agent gets its output, {AgentCoordinator.CheckRetries} times at most.";
        CheckText.Visibility = card.CheckCommand is not null ? Visibility.Visible : Visibility.Collapsed;
        PlanFirstBox.Content = "Plan first: a plan, then you run it";
        PlanFirstBox.ToolTip = "The agent only reads and proposes a plan, on Settings' Strong model; you run it, on the model above, in the same session";
        // A team always plans first, on the lead; a training studies first.
        PlanFirstBox.IsEnabled = !Team && !Train;
        RunButton.IsEnabled = card.Workspace is not null;
        RunButton.Content = card.RunLabel ?? (Team ? "Plan the team" : Train ? "Start training" : PlanFirstBox.IsChecked == true ? "Make a plan" : SelectedAuto ? "Run on auto" : "Run with edits");
    }

    private bool Team => TeamBox.Visibility == Visibility.Visible && TeamBox.IsChecked == true;

    private bool Train => TrainBox.Visibility == Visibility.Visible && TrainBox.IsChecked == true;

    private void TeamBox_OnClick(object sender, RoutedEventArgs e)
    {
        _teamChosen = true;
        if (TeamBox.IsChecked == true) TrainBox.IsChecked = false;
    }

    private void TrainBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (TrainBox.IsChecked != true) return;
        _teamChosen = true;
        TeamBox.IsChecked = false;
    }

    private void Run_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card is not { Workspace: { } workspace } card || SelectedAgent is not { } agent) return;
        if (string.IsNullOrWhiteSpace(GoalBox.Text))
        {
            ShowCardWarning("Write what the agent should do.");
            return;
        }

        if (Train && SelectedRoleId is null)
        {
            ShowCardWarning("Pick the specialist to train: General has no role to learn for.");
            return;
        }

        if (_pickChangedByRun)
        {
            _pickChangedByRun = false;
            ShowCardWarning("The specialist or skills changed with the goal: check them, then press Run again.");
            return;
        }

        var plan = new AgentPlan(
            agent.Kind,
            workspace,
            Team || (!Train && PlanFirstBox.Visibility == Visibility.Visible && PlanFirstBox.IsChecked == true),
            BackupBox.Visibility == Visibility.Visible && BackupBox.IsChecked == true,
            GoalBox.Text.Trim())
        {
            Team = Team,
            Train = Train,
            Model = SelectedModel,
            Effort = SelectedEffort,
            // An agent with no effort levels (the custom model) leaves Auto as the card had it, for the agents the task may reach.
            AutoEffort = SelectedAutoEffort || (!EffortCombo.IsEnabled && card.AutoEffort),
            AutoApprove = SelectedAuto,
            RoleId = SelectedRoleId,
            Skills = _skills,
            ProjectId = card.ProjectId,
            Attached = [.. _cardAttached]
        };
        _card = null;
        _cardAttached.Clear();
        ShowAttached();
        RunRequested?.Invoke(plan);
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e) => SendReply(queue: false);

    private void ReplyBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter starts a new line, as in Link's own box; Tab while the agent works keeps the words
        // for after its run.
        if (e.Key == Key.Tab && _running && ReplyBox.Text.Trim().Length > 0)
        {
            e.Handled = true;
            SendReply(queue: true);
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SendReply(queue: false);
    }

    private void ReplyBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ReplyPlaceholder.Visibility = ReplyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Nothing to send leaves the button dim, as Enter does nothing then.
        ContinueButton.IsEnabled = ReplyBox.Text.Trim().Length > 0 || _replyAttached.Count > 0;
    }

    private void SendReply(bool queue)
    {
        var reply = ReplyBox.Text.Trim();
        if (reply.Length == 0 && _replyAttached.Count == 0) return;
        // Pictures alone still say something: the agent is told to look.
        if (reply.Length == 0) reply = "Look at what I attached.";
        IReadOnlyList<string> attached = [.. _replyAttached];
        _replyAttached.Clear();
        ReplyBox.Clear();
        ShowAttached();
        if (queue) QueueRequested?.Invoke(reply, attached);
        else ContinueRequested?.Invoke(reply, attached);
    }

    private void Answer(string words)
    {
        QuestionCard.Visibility = Visibility.Collapsed;
        UpdateReplyHint();
        IReadOnlyList<string> attached = [.. _replyAttached];
        _replyAttached.Clear();
        ShowAttached();
        ContinueRequested?.Invoke(words, attached);
    }

    private void SendChoices_OnClick(object sender, RoutedEventArgs e)
    {
        var chosen = _choices.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();
        if (chosen.Length > 0) Answer(string.Join("; ", chosen));
    }

    private void HandOff_OnClick(object sender, RoutedEventArgs e) => ShowMenu(HandOffButton, [_handOff], PlacementMode.Top);

    private void More_OnClick(object sender, RoutedEventArgs e) => ShowMenu(MoreButton, [_more], PlacementMode.Bottom);

    private void Setup_OnClick(object sender, RoutedEventArgs e) => ShowMenu(SetupButton, _setup, PlacementMode.Top);

    // A click only: the box ticked by ShowLimit is no change of the user's.
    private void LimitBox_OnClick(object sender, RoutedEventArgs e) => _limitToggle?.Changed(LimitBox.IsChecked == true);

    /// <summary>A menu of the actions, a line between groups; the current choice of a group is checked.</summary>
    private static void ShowMenu(Button anchor, IReadOnlyList<IReadOnlyList<PanelAction>> groups, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement };
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

    private void PlanToggle_OnClick(object sender, RoutedEventArgs e)
    {
        _planOpen = !_planOpen;
        PlanList.Visibility = _planOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowPlanLabel();
    }

    private void PlanRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanRow row }) return;
        row.Open = !row.Open;
        if (row.Open) _openSteps.Add(row.Title);
        else _openSteps.Remove(row.Title);
    }

    private void TurnSteps_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadTurn turn }) turn.StepsOpen = !turn.StepsOpen;
    }

    private void TurnFiles_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadTurn turn }) turn.FilesOpen = !turn.FilesOpen;
    }

    private void StepGroup_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepGroup group }) group.Open = !group.Open;
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

    private void CardHarness_OnClick(object sender, RoutedEventArgs e) => HarnessRequested?.Invoke();

    private void LimitRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LimitRow row }) LimitRefreshRequested?.Invoke(row.Provider);
    }

    private void CancelCard_OnClick(object sender, RoutedEventArgs e)
    {
        _card = null;
        _cardAttached.Clear();
        ShowAttached();
        CardCancelled?.Invoke();
    }

    private void Stop_OnClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Decision_OnClick(object sender, RoutedEventArgs e) => DecisionTaken?.Invoke(AlwaysBox.IsChecked == true);

    private void Rating_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var rating)) return;
        RatingCard.Visibility = Visibility.Collapsed;
        RatingChosen?.Invoke(rating);
    }
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();
    private void Escalate_OnClick(object sender, RoutedEventArgs e) => EscalateRequested?.Invoke();
    private void RestartNotNow_OnClick(object sender, RoutedEventArgs e) => ShowRestart(false);
    private void Finish_OnClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();
    private void Cheaper_OnClick(object sender, RoutedEventArgs e) => CheaperRequested?.Invoke();

    private void PlanMode_OnClick(object sender, RoutedEventArgs e) => PlanModeOffRequested?.Invoke();

    private void OpenFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentChangeRow row }) OpenFileRequested?.Invoke(row.Path);
    }
}
