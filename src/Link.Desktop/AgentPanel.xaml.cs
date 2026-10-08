using System.Windows;
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
    public string Title { get; init; } = "NEW TASK";
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
}

/// <summary>
/// The agents' tasks in the Studio: the project's tasks, the card before a run, and a task as one thread (the user's words,
/// each run of an agent, notes between) with the box for the next words. It only shows and asks; the window does the
/// work behind each button.
/// </summary>
public partial class AgentPanel : UserControl
{
    private const double ThumbnailHeight = 54;
    private static readonly AgentChoice AutoChoice = new("auto", "Auto — Link picks");
    private static readonly AgentChoice GeneralChoice = new(string.Empty, "General");
    private AgentCardModel? _card;
    private bool _fillingCard;
    // The specialist is the user's once they chose it (a hand-over starts so): the goal's words pick it no more.
    private bool _roleChosen;
    // The goal's new words changed the pick as a press on Run took their focus: that Run shows the pick first.
    private bool _pickChangedByRun;
    // The skills on the card as chips; those the user took off or added stay so when Link picks again.
    private IReadOnlyList<string> _skills = [];
    private readonly HashSet<string> _skillsOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _skillsOn = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _planning;
    private bool _hasTask;
    private bool _planOpen = true;
    private string _replyTarget = "the agent";
    private IReadOnlyList<PanelAction> _handOff = [];
    private IReadOnlyList<PanelAction> _more = [];
    private readonly List<CheckBox> _choices = [];

    // The effort box's own tooltip, which a model without effort levels replaces.
    private readonly object? _effortTip;

    public AgentPanel()
    {
        InitializeComponent();
        _effortTip = EffortCombo.ToolTip;
    }

    public event Action<AgentPlan>? RunRequested;
    public event Action? CardCancelled;
    public event Action? StopRequested;

    /// <summary>The decision card's button; true: remember the allowance for the project.</summary>
    public event Action<bool>? DecisionTaken;

    /// <summary>Start over in a clean session with the agent's note on what did not work.</summary>
    public event Action? RestartRequested;

    /// <summary>The same session goes on with the strongest model.</summary>
    public event Action? EscalateRequested;

    /// <summary>The user's words: a reply after a run, words to steer it with while the agent works, or the answer to its question.</summary>
    public event Action<string>? ContinueRequested;

    /// <summary>Words for after the run that is going on (Tab): the same session goes on with them once it ends.</summary>
    public event Action<string>? QueueRequested;

    public event Action? FinishRequested;

    /// <summary>Update skill on the offer after a finished task: the agent is asked to keep what the task taught.</summary>
    public event Action? LearnRequested;

    /// <summary>No on that offer.</summary>
    public event Action? LearnDeclined;

    /// <summary>The button of the cheaper hint: the card of a hand-off to the custom model is wanted.</summary>
    public event Action? CheaperRequested;

    /// <summary>Plan mode off: the session's next words go on with edits.</summary>
    public event Action? PlanModeOffRequested;
    public event Action<string>? OpenFileRequested;

    /// <summary>Back from a task to the list.</summary>
    public event Action? BackRequested;

    public event Action? NewTaskRequested;
    public event Action<AgentTaskRecord>? TaskSelected;

    /// <summary>Link's own run (the map) of the list's first row is to be shown.</summary>
    public event Action? OwnRunRequested;

    /// <summary>An agent's usage was clicked: it is to be asked for its windows now.</summary>
    public event Action<AgentProviderKind>? LimitRefreshRequested;

    /// <summary>The card's Harness button: the Studio's inspector column is to open.</summary>
    public event Action? HarnessRequested;

    /// <summary>
    /// The list of the project's tasks with the agents' usage, Link's own run (<paramref name="own"/>) first while there
    /// is one; the view shows when nothing else is open.
    /// </summary>
    public void ShowList(IReadOnlyList<TaskRow> tasks, IReadOnlyList<LimitRow> limits, TaskRow? own, string? learn = null)
    {
        TaskList.ItemsSource = own is null ? tasks : tasks.Prepend(own).ToArray();
        TasksEmptyText.Visibility = tasks.Count == 0 && own is null ? Visibility.Visible : Visibility.Collapsed;
        LimitsList.ItemsSource = limits;
        LearnCard.Visibility = learn is null ? Visibility.Collapsed : Visibility.Visible;
        LearnText.Text = learn ?? string.Empty;
    }

    /// <summary>Back to the list: no task is open in the panel.</summary>
    public void ShowListView()
    {
        _hasTask = false;
        ShowView(ListView);
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
        BackupBox.IsChecked = true;
        GoalBox.Text = card.Goal;
        ProjectText.Text = card.ProjectName;
        WorkspaceText.Text = card.Workspace ?? "Not chosen: choose it in the Project tab";
        WorkspaceText.Foreground = (Brush)FindResource(card.Workspace is null ? "ErrorBrush" : "TextBrush");
        HarnessLineText.Text = card.HarnessLine;
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

    /// <summary>Settings were saved while the card is open: its line of what goes with the run, such as the budget, follows.</summary>
    public void UpdateHarnessLine(Func<AgentCardModel, string> line)
    {
        if (_card is null) return;
        _card = _card with { HarnessLine = line(_card) };
        HarnessLineText.Text = _card.HarnessLine;
    }

    /// <summary>The card is gone: back to the open task, or to the list.</summary>
    public void HideCard()
    {
        _card = null;
        ShowView(_hasTask ? TaskView : ListView);
    }

    public void ShowCardWarning(string message)
    {
        CardWarningText.Text = message;
        CardWarningText.Visibility = Visibility.Visible;
    }

    /// <summary>A task opens in the panel: its goal and where it works; its thread comes with <see cref="ShowThread"/>.</summary>
    public void ShowTaskView(string goal, string where)
    {
        _hasTask = true;
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
        ReplyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReplyNoteText.Text = note ?? string.Empty;
        ReplyNoteText.Visibility = !show && note is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The choices of the Hand off ▾ and ⋯ menus; an empty list hides the button.</summary>
    public void ShowMenus(IReadOnlyList<PanelAction> handOff, IReadOnlyList<PanelAction> more)
    {
        _handOff = handOff;
        _more = more;
        HandOffButton.Visibility = handOff.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = more.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Whether Finish is offered: not for Link's own answer, such as a map, nor for a task shown only to be read.</summary>
    public void ShowFinish(bool finish) => FinishButton.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the task's session goes on in plan mode, where only reading and planning work: its chip turns it off.</summary>
    public void ShowPlanMode(bool planning)
    {
        _planning = planning;
        PlanModeButton.Visibility = planning ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplyHint();
    }

    /// <summary>
    /// The agent's plan as it last wrote it, as its own app shows it: ✓ done, ◐ under way (in the agent's words for it,
    /// when it gave any), ○ to do.
    /// </summary>
    public void ShowPlan(IReadOnlyList<AgentPlanItem> plan)
    {
        PlanList.ItemsSource = plan.Select(item => item.Done ? new PlanRow("✓", item.Text, (Brush)FindResource("MutedBrush"))
            : item.Active ? new PlanRow("◐", item.Doing ?? item.Text, (Brush)FindResource("AccentBrush"))
            : new PlanRow("○", item.Text, (Brush)FindResource("TextBrush"))).ToArray();
        PlanToggle.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanList.Visibility = plan.Count > 0 && _planOpen ? Visibility.Visible : Visibility.Collapsed;
        PlanLabel.Text = $"PLAN · {plan.Count(item => item.Done)}/{plan.Count} {(_planOpen ? "▾" : "▸")}";
    }

    /// <summary>What the task changed, beside its standing: "+181 −12" or "5 files", the files as its hint; empty hides it.</summary>
    public void ShowChanges(string text, string files)
    {
        ChangesText.Text = text;
        ChangesText.ToolTip = files.Length == 0 ? null : files;
        ChangesText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What Link gave the agent for the task, word for word; empty hides it.</summary>
    public void ShowBrief(string brief)
    {
        BriefText.Text = brief;
        BriefToggle.Visibility = brief.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (brief.Length == 0) BriefText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The task's tokens by agent, with what the money means on a subscription; empty hides it.</summary>
    public void ShowUsage(string usage, string hint)
    {
        UsageText.Text = usage;
        UsageText.ToolTip = hint;
        UsageText.Visibility = usage.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The usage windows of the agent on the task, under its buttons.</summary>
    public void ShowTaskLimits(IReadOnlyList<LimitRow> limits) => TaskLimitsList.ItemsSource = limits;

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
            content.Children.Add(new TextBlock { Text = option.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (option.Description.Length > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = option.Description,
                    FontSize = 11.5,
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

    /// <summary>A usage limit near or reached, with the ways on; null hides it.</summary>
    public void ShowLimit(string? text, IReadOnlyList<PanelAction> actions)
    {
        LimitCard.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        LimitText.Text = text ?? string.Empty;
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
        ReplyPlaceholder.Text = _running
            ? $"Steer {_replyTarget} now: Enter stops the step and goes on with your words · Tab: after this step"
            : QuestionCard.Visibility == Visibility.Visible
                ? $"Answer {_replyTarget} in your own words"
                : _planning
                    ? $"Reply to {_replyTarget}: the session stays in plan mode, reading and planning only"
                    : $"Reply to {_replyTarget}: the same session goes on with your words";
        ContinueButton.Content = _running ? "Steer now" : "Send";
        ContinueButton.ToolTip = _running
            ? "Stops the current step and goes on in the same session with your words; nothing is read again (Enter) · Tab: After this step, your words wait for it to end"
            : "The same session goes on with your words, with the same access and folder (Enter)";
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var each in new FrameworkElement[] { ListView, CardView, TaskView }) each.Visibility = each == view ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Small pictures of the images that go along; a click shows the file.</summary>
    private void ShowImages(IReadOnlyList<string> images)
    {
        ImagesPanel.Children.Clear();
        foreach (var path in images.Where(File.Exists))
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
            catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
            {
                // A picture that does not read still goes; only its preview is missing.
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

    // The first item of the specialists is General, no role.
    private string? SelectedRoleId => RoleCombo.SelectedItem is AgentChoice { Value.Length: > 0 } role ? role.Value : null;
    private AgentRole? SelectedRole => _card?.Roles.FirstOrDefault(role => role.Id == SelectedRoleId);

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
        RoleCombo.ItemsSource = card.Roles.Select(role => new AgentChoice(role.Id, role.Name)).Prepend(GeneralChoice).ToArray();
        SelectRole(card.RoleId);
        SkillsRow.Visibility = card.WorkspaceSkills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSkills(card.Skills ?? PickedSkills());
    }

    private void SelectRole(string? id)
    {
        var filling = _fillingCard;
        _fillingCard = true;
        RoleCombo.SelectedIndex = Math.Max(0, (_card?.Roles.ToList().FindIndex(role => role.Id == id) ?? -1) + 1);
        _fillingCard = filling;
        var role = SelectedRole;
        RoleCombo.ToolTip = role?.Description ?? "No specialist: the agent works as its rules say";
        RolePickText.Visibility = role is not null && !_roleChosen ? Visibility.Visible : Visibility.Collapsed;
    }

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
        add.Click += (_, _) => ShowMenu(add, [.. others.Select(skill => new PanelAction(skill.Name, skill.Description, () =>
        {
            _skillsOff.Remove(skill.Name);
            _skillsOn.Add(skill.Name);
            ShowSkills([.. _skills, skill.Name]);
        }))], PlacementMode.Bottom);
        SkillsPanel.Children.Add(add);
    }

    private void Role_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCard) return;
        _roleChosen = true;
        SelectRole(SelectedRoleId);
        ShowSkills(PickedSkills());
    }

    /// <summary>
    /// The goal as the user left it: the specialist is picked from the new words while it is still Link's pick, and the
    /// skills are, whoever chose the specialist. A hand-over keeps the task's.
    /// </summary>
    private void Goal_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_card is not { FixedProvider: false } card) return;
        var role = SelectedRoleId;
        if (!_roleChosen && card.Roles.Count > 0) SelectRole(ProjectRoles.Suggest(GoalBox.Text, card.Roles)?.Id);
        // The chips stay as they are unless the pick changed: a click on one lost the focus of the goal, and a chip
        // drawn anew under the mouse would take that click.
        var skills = PickedSkills();
        var changed = role != SelectedRoleId || !skills.SequenceEqual(_skills);
        if (changed) ShowSkills(skills);
        // A press on Run takes the focus before its click: that click is not to run a pick the card has not shown yet.
        _pickChangedByRun = changed && RunButton.IsMouseOver;
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
        PlanFirstBox.Content = "Plan first: read only, then you run the plan";
        PlanFirstBox.ToolTip = "The agent only reads and proposes a plan, on Settings' Strong model; you run it, on the model above, in the same session";
        RunButton.IsEnabled = card.Workspace is not null;
        RunButton.Content = card.RunLabel ?? (PlanFirstBox.IsChecked == true ? "Make a plan" : SelectedAuto ? "Run on auto" : "Run with edits");
    }

    private void Run_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card?.Workspace is not { } workspace || SelectedAgent is not { } agent) return;
        if (string.IsNullOrWhiteSpace(GoalBox.Text))
        {
            ShowCardWarning("Write what the agent should do.");
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
            AgentAccess.Do,
            PlanFirstBox.Visibility == Visibility.Visible && PlanFirstBox.IsChecked == true,
            BackupBox.Visibility == Visibility.Visible && BackupBox.IsChecked == true,
            GoalBox.Text.Trim())
        {
            Model = SelectedModel,
            Effort = SelectedEffort,
            // An agent with no effort levels (the custom model) leaves Auto as the card had it, for the agents the task may reach.
            AutoEffort = SelectedAutoEffort || (!EffortCombo.IsEnabled && _card?.AutoEffort == true),
            AutoApprove = SelectedAuto,
            RoleId = SelectedRoleId,
            Skills = _skills
        };
        _card = null;
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

    private void ReplyBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        ReplyPlaceholder.Visibility = ReplyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SendReply(bool queue)
    {
        var reply = ReplyBox.Text.Trim();
        if (reply.Length == 0) return;
        ReplyBox.Clear();
        if (queue) QueueRequested?.Invoke(reply);
        else ContinueRequested?.Invoke(reply);
    }

    private void Answer(string words)
    {
        QuestionCard.Visibility = Visibility.Collapsed;
        ContinueRequested?.Invoke(words);
    }

    private void SendChoices_OnClick(object sender, RoutedEventArgs e)
    {
        var chosen = _choices.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();
        if (chosen.Length > 0) Answer(string.Join("; ", chosen));
    }

    private void HandOff_OnClick(object sender, RoutedEventArgs e) => ShowMenu(HandOffButton, _handOff, PlacementMode.Top);

    private void More_OnClick(object sender, RoutedEventArgs e) => ShowMenu(MoreButton, _more, PlacementMode.Bottom);

    private static void ShowMenu(Button anchor, IReadOnlyList<PanelAction> actions, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement };
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Label, ToolTip = action.Hint };
            item.Click += (_, _) => action.Run();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void PlanToggle_OnClick(object sender, RoutedEventArgs e)
    {
        _planOpen = !_planOpen;
        PlanList.Visibility = _planOpen ? Visibility.Visible : Visibility.Collapsed;
        if (PlanLabel.Text.Length > 0) PlanLabel.Text = PlanLabel.Text[..^1] + (_planOpen ? "▾" : "▸");
    }

    private void BriefToggle_OnClick(object sender, RoutedEventArgs e)
    {
        var open = BriefText.Visibility != Visibility.Visible;
        BriefText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BriefToggleText.Text = open ? "WHAT LINK GAVE ▾" : "WHAT LINK GAVE ▸";
    }

    private void TurnSteps_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadTurn turn }) turn.StepsOpen = !turn.StepsOpen;
    }

    private void StepGroup_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepGroup group }) group.Open = !group.Open;
    }

    private void TaskRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskRow row }) return;
        if (row.Record is { } record) TaskSelected?.Invoke(record);
        else OwnRunRequested?.Invoke();
    }

    private void NewTask_OnClick(object sender, RoutedEventArgs e) => NewTaskRequested?.Invoke();

    private void CardHarness_OnClick(object sender, RoutedEventArgs e) => HarnessRequested?.Invoke();

    private void LimitRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LimitRow row }) LimitRefreshRequested?.Invoke(row.Provider);
    }
    private void Back_OnClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void CancelCard_OnClick(object sender, RoutedEventArgs e)
    {
        _card = null;
        CardCancelled?.Invoke();
    }

    private void Stop_OnClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Decision_OnClick(object sender, RoutedEventArgs e) => DecisionTaken?.Invoke(AlwaysBox.IsChecked == true);
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();
    private void Escalate_OnClick(object sender, RoutedEventArgs e) => EscalateRequested?.Invoke();
    private void RestartNotNow_OnClick(object sender, RoutedEventArgs e) => ShowRestart(false);
    private void Finish_OnClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();
    private void Cheaper_OnClick(object sender, RoutedEventArgs e) => CheaperRequested?.Invoke();
    private void Learn_OnClick(object sender, RoutedEventArgs e) => LearnRequested?.Invoke();
    private void LearnNo_OnClick(object sender, RoutedEventArgs e) => LearnDeclined?.Invoke();

    private void PlanMode_OnClick(object sender, RoutedEventArgs e) => PlanModeOffRequested?.Invoke();

    private void OpenFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentChangeRow row }) OpenFileRequested?.Invoke(row.Path);
    }
}
