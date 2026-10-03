using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// What the task card starts from. <see cref="Providers"/>: each agent with its three levels as the user reads them
/// (Light, Standard, Deep with their model and effort) and what is known of its usage limits. A hand-over shows its
/// brief on the same card with <see cref="FixedProvider"/> and its own <see cref="Title"/> and <see cref="RunLabel"/>.
/// </summary>
public sealed record AgentCardModel(
    IReadOnlyList<(AgentProviderKind Kind, string Name, IReadOnlyList<string> Levels, string Limits)> Providers,
    AgentProviderKind Provider,
    string ProjectName,
    string? Workspace,
    bool WorkspaceHasGit,
    string Attachments,
    string Goal,
    string? CheckCommand,
    AgentAccessChoice Access)
{
    public string Title { get; init; } = "TASK FOR THE AGENT";
    public string? RunLabel { get; init; }
    public bool FixedProvider { get; init; }
    public AgentLevel Level { get; init; } = AgentLevel.Standard;
}

/// <summary>A row of the task's steps or plan: its text in its colour.</summary>
public sealed record AgentStepRow(string Text, Brush Brush);

/// <summary>A changed file; the label says how it changed and whether undo can take it back.</summary>
public sealed record AgentChangeRow(string Label, string Path);

/// <summary>A choice the panel offers in a menu or on a card; the window does the work behind it.</summary>
public sealed record PanelAction(string Label, string Hint, Action Run);

/// <summary>
/// The Task tab beside Link: the task card before a run, and the task itself during and after it. It only shows and
/// asks; the window does the work behind each button.
/// </summary>
public partial class AgentPanel : UserControl
{
    private const int StepsKept = 300;
    private readonly ObservableCollection<AgentStepRow> _steps = [];
    private AgentCardModel? _card;
    private bool _showingCard;
    private bool _running;
    private bool _stepsOpen = true;
    private IReadOnlyList<PanelAction> _handOff = [];
    private IReadOnlyList<PanelAction> _more = [];

    public AgentPanel()
    {
        InitializeComponent();
        StepsList.ItemsSource = _steps;
    }

    public event Action<AgentPlan>? RunRequested;
    public event Action? CardCancelled;
    public event Action? StopRequested;

    /// <summary>The decision card's button; true: remember the allowance for the project.</summary>
    public event Action<bool>? DecisionTaken;

    /// <summary>Start over in a clean session with the agent's note on what did not work.</summary>
    public event Action? RestartRequested;

    /// <summary>The same session goes on with the Deep level.</summary>
    public event Action? EscalateRequested;

    /// <summary>The user's words: a reply after a run, a correction while the agent works.</summary>
    public event Action<string>? ContinueRequested;

    /// <summary>Words for after the run that is going on (Tab): the same session goes on with them once it ends.</summary>
    public event Action<string>? QueueRequested;

    public event Action? FinishRequested;
    public event Action? ConsoleRequested;
    public event Action<string>? OpenFileRequested;

    public void ShowCard(AgentCardModel card)
    {
        _card = card;
        _showingCard = true;
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
        AttachmentsText.Text = card.Attachments;
        AttachmentsText.Visibility = card.Attachments.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CardWarningText.Visibility = Visibility.Collapsed;
        ShowLevels(card, card.Level);
        _showingCard = false;
        UpdateCard();
        ShowView(CardView);
        Window.GetWindow(this)?.Activate();
        GoalBox.Focus();
        GoalBox.CaretIndex = GoalBox.Text.Length;
    }

    public void ShowCardWarning(string message)
    {
        CardWarningText.Text = message;
        CardWarningText.Visibility = Visibility.Visible;
    }

    /// <summary>No task: how one starts.</summary>
    public void ShowNoTask() => ShowView(EmptyView);

    /// <summary>A task opens or a run starts: its goal, who works and where; the run's own rows start empty.</summary>
    public void ShowActivity(string goal, string header, string where)
    {
        _steps.Clear();
        GoalText.Text = goal;
        GoalText.ToolTip = goal;
        HeaderText.Text = header;
        WhereText.Text = where;
        WhereText.ToolTip = where;
        CurrentStepText.Text = "Starting…";
        DecisionCard.Visibility = Visibility.Collapsed;
        RestartCard.Visibility = Visibility.Collapsed;
        LimitCard.Visibility = Visibility.Collapsed;
        ResultLabel.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        ShowStats(string.Empty);
        SetRunning(true);
        ShowView(ActivityView);
    }

    public void SetHeader(string header) => HeaderText.Text = header;

    public void SetRunning(bool running)
    {
        _running = running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        HandOffButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StartOverButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        FinishButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        // While the agent works its steps are what to watch; afterwards the result is.
        SetStepsOpen(running);
        UpdateReplyHint();
        if (running) ReplyPanel.Visibility = Visibility.Visible;
    }

    /// <summary>The reply box, offered while a session can go on.</summary>
    public void ShowReply(bool show) => ReplyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The choices of the Hand off ▾ and More ▾ menus; an empty list hides the button.</summary>
    public void ShowMenus(IReadOnlyList<PanelAction> handOff, IReadOnlyList<PanelAction> more)
    {
        _handOff = handOff;
        _more = more;
        if (_running) return;
        HandOffButton.Visibility = handOff.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = more.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Whether Start over and Finish are offered: not for Link's own answer, such as a map.</summary>
    public void ShowTaskButtons(bool startOver, bool finish)
    {
        if (_running) return;
        StartOverButton.Visibility = startOver ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A batch of events at once: the tab redraws at most a few times a second, however fast they come.</summary>
    public void AddSteps(IEnumerable<AgentEvent> events)
    {
        var atEnd = StepsScroll.VerticalOffset >= StepsScroll.ScrollableHeight - 4;
        foreach (var agentEvent in events)
        {
            if (agentEvent.Kind is AgentEventKind.Tool or AgentEventKind.Status) CurrentStepText.Text = agentEvent.Text;
            // The result has its own place, a change is listed under Changed, failed calls are counted under the result,
            // and the plan and the usage have their own sections.
            if (agentEvent.Kind is AgentEventKind.Completed or AgentEventKind.Artifact or AgentEventKind.ToolFailed
                or AgentEventKind.Plan or AgentEventKind.Limit)
            {
                continue;
            }

            var (prefix, brush) = agentEvent.Kind switch
            {
                AgentEventKind.Tool => ("▸ ", "MutedBrush"),
                AgentEventKind.Output => (string.Empty, "TextBrush"),
                AgentEventKind.NeedsInput => ("⚠ ", "AccentBrush"),
                AgentEventKind.Loop => ("⟳ ", "AccentBrush"),
                AgentEventKind.Error => ("✗ ", "ErrorBrush"),
                _ => ("· ", "MutedBrush")
            };
            _steps.Add(new AgentStepRow(prefix + agentEvent.Text, (Brush)FindResource(brush)));
        }

        while (_steps.Count > StepsKept) _steps.RemoveAt(0);
        StepsToggleText.Text = $"STEPS ({_steps.Count}) {(_stepsOpen ? "▾" : "▸")}";
        if (atEnd && _stepsOpen) StepsScroll.ScrollToEnd();
    }

    /// <summary>The agent's plan as it last wrote it: done, under way, to do.</summary>
    public void ShowPlan(IReadOnlyList<AgentPlanItem> plan)
    {
        PlanList.ItemsSource = plan.Select(item => new AgentStepRow(
            (item.Done ? "✓ " : item.Active ? "▸ " : "○ ") + item.Text,
            (Brush)FindResource(item.Done ? "MutedBrush" : item.Active ? "AccentBrush" : "TextBrush"))).ToArray();
        PlanLabel.Text = $"AGENT'S PLAN · {plan.Count(item => item.Done)} OF {plan.Count}";
        PlanLabel.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The task's history, oldest first; empty hides it.</summary>
    public void ShowHistory(IReadOnlyList<string> lines)
    {
        HistoryList.ItemsSource = lines;
        HistoryLabel.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What Link gave the agent for the task, word for word; empty hides it.</summary>
    public void ShowBrief(string brief)
    {
        BriefText.Text = brief;
        BriefToggle.Visibility = brief.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (brief.Length == 0) BriefText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The task's tokens by agent and the agents' usage windows; empty hides it.</summary>
    public void ShowUsage(string usage)
    {
        UsageText.Text = usage;
        var shown = usage.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageLabel.Visibility = shown;
        UsageText.Visibility = shown;
    }

    /// <summary>The usage's tooltip: what the money the CLI reports means on a subscription.</summary>
    public void SetUsageHint(string hint) => UsageText.ToolTip = hint;

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

    public void ShowChanges(IReadOnlyList<AgentChangeRow> changes)
    {
        ChangesList.ItemsSource = changes;
        ChangesLabel.Text = $"CHANGED ({changes.Count})";
        ChangesLabel.Visibility = changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowResult(string text, string currentStep)
    {
        CurrentStepText.Text = currentStep;
        ResultText.Text = text;
        var shown = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResultLabel.Visibility = shown;
        ResultText.Visibility = shown;
        SetRunning(false);
    }

    /// <summary>The run's numbers in one line under its result; empty hides it.</summary>
    public void ShowStats(string text)
    {
        StatsText.Text = text;
        StatsText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>Start over, and Escalate when the task is not already on Deep.</summary>
    public void ShowRestart(bool show, bool escalate = false)
    {
        RestartCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EscalateButton.Visibility = escalate ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Link's own step around a run, such as asking for a note; while it runs, Stop shows and the choices hide.</summary>
    public void ShowStep(string step, bool running)
    {
        CurrentStepText.Text = step;
        SetRunning(running);
        if (!running) return;
        DecisionCard.Visibility = Visibility.Collapsed;
        RestartCard.Visibility = Visibility.Collapsed;
        // Link's own step, such as asking for a note, takes no words for the agent.
        ReplyPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Who the words in the box go to, and what happens to them.</summary>
    public void SetReplyTarget(string agentName)
    {
        _replyTarget = agentName;
        UpdateReplyHint();
    }

    private string _replyTarget = "the agent";

    private void UpdateReplyHint()
    {
        ReplyPlaceholder.Text = _running
            ? $"Correct {_replyTarget}: Enter stops the step and goes on with your words · Tab: after this run"
            : $"Reply to {_replyTarget}: the same session goes on with your words";
        ContinueButton.Content = _running ? "Correct" : "Reply";
        ContinueButton.ToolTip = _running
            ? "The agent stops the step it is on and goes on with your words in the same session (Enter); Tab keeps them for after this run"
            : "The same session goes on with your words, with the same access and folder (Enter)";
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var each in new FrameworkElement[] { CardView, ActivityView, EmptyView }) each.Visibility = each == view ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStepsOpen(bool open)
    {
        _stepsOpen = open;
        StepsList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        StepsToggleText.Text = $"STEPS ({_steps.Count}) {(open ? "▾" : "▸")}";
    }

    private AgentProviderKind SelectedProvider =>
        _card is { } card && ProviderCombo.SelectedIndex >= 0 ? card.Providers[ProviderCombo.SelectedIndex].Kind : AgentProviderKind.ClaudeCode;

    // The items follow AgentAccessChoice: edit, auto.
    private bool SelectedAuto => AccessCombo.SelectedIndex == 1;

    // The items follow AgentLevel: light, standard, deep.
    private AgentLevel SelectedLevel => LevelCombo.SelectedIndex >= 0 ? (AgentLevel)LevelCombo.SelectedIndex : AgentLevel.Standard;

    private void Card_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_showingCard || _card is not { } card) return;
        // Another agent: its levels, keeping the one chosen.
        if (sender == ProviderCombo) ShowLevels(card, SelectedLevel);
        UpdateCard();
    }

    private void ShowLevels(AgentCardModel card, AgentLevel level)
    {
        var provider = card.Providers.First(agent => agent.Kind == SelectedProvider);
        LevelCombo.ItemsSource = provider.Levels;
        LevelCombo.SelectedIndex = (int)level;
        LimitsText.Text = provider.Limits;
        LimitsText.Visibility = provider.Limits.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCard()
    {
        if (_card is not { } card) return;
        BackupBox.Visibility = !card.WorkspaceHasGit ? Visibility.Visible : Visibility.Collapsed;
        CheckText.Text = $"After the edits Link runs the check itself, and the agent is told so: {card.CheckCommand}. " +
                         $"While it fails, the agent gets its output, {AgentCoordinator.CheckRetries} times at most.";
        CheckText.Visibility = card.CheckCommand is not null ? Visibility.Visible : Visibility.Collapsed;
        PlanFirstBox.Content = "Plan first: the agent only reads and proposes, then you run it" +
                               (card.Providers.Count > 0 ? " (the plan on Deep, the work on the level above)" : string.Empty);
        RunButton.IsEnabled = card.Workspace is not null;
        RunButton.Content = card.RunLabel ?? (PlanFirstBox.IsChecked == true ? "Make a plan" : SelectedAuto ? "Run on auto" : "Run with edits");
    }

    private void Run_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card?.Workspace is not { } workspace) return;
        if (string.IsNullOrWhiteSpace(GoalBox.Text))
        {
            ShowCardWarning("Write what the agent should do.");
            return;
        }

        RunRequested?.Invoke(new AgentPlan(
            SelectedProvider,
            workspace,
            AgentAccess.Do,
            PlanFirstBox.Visibility == Visibility.Visible && PlanFirstBox.IsChecked == true,
            BackupBox.Visibility == Visibility.Visible && BackupBox.IsChecked == true,
            GoalBox.Text.Trim())
        {
            Level = SelectedLevel,
            AutoApprove = SelectedAuto
        });
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

    private void HandOff_OnClick(object sender, RoutedEventArgs e) => ShowMenu(HandOffButton, _handOff);

    private void More_OnClick(object sender, RoutedEventArgs e) => ShowMenu(MoreButton, _more);

    private static void ShowMenu(Button anchor, IReadOnlyList<PanelAction> actions)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Label, ToolTip = action.Hint };
            item.Click += (_, _) => action.Run();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void StepsToggle_OnClick(object sender, RoutedEventArgs e) => SetStepsOpen(!_stepsOpen);

    private void BriefToggle_OnClick(object sender, RoutedEventArgs e)
    {
        var open = BriefText.Visibility != Visibility.Visible;
        BriefText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BriefToggleText.Text = open ? "WHAT LINK GAVE ▾" : "WHAT LINK GAVE ▸";
    }

    private void CancelCard_OnClick(object sender, RoutedEventArgs e) => CardCancelled?.Invoke();
    private void Stop_OnClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Decision_OnClick(object sender, RoutedEventArgs e) => DecisionTaken?.Invoke(AlwaysBox.IsChecked == true);
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();
    private void Escalate_OnClick(object sender, RoutedEventArgs e) => EscalateRequested?.Invoke();
    private void RestartNotNow_OnClick(object sender, RoutedEventArgs e) => ShowRestart(false);
    private void Finish_OnClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();
    private void Console_OnClick(object sender, RoutedEventArgs e) => ConsoleRequested?.Invoke();

    private void OpenFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentChangeRow row }) OpenFileRequested?.Invoke(row.Path);
    }
}
