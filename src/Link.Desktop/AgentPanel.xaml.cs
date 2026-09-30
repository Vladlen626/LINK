using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>What the confirmation card starts from; <see cref="CheckCommand"/> is the project's check after edits.</summary>
public sealed record AgentCardModel(
    IReadOnlyList<(AgentProviderKind Kind, string Name)> Providers,
    AgentProviderKind Provider,
    string ProjectName,
    string? Workspace,
    bool WorkspaceHasGit,
    string Attachments,
    string Goal,
    string? CheckCommand);

/// <summary>A row of Activity: a step, a message or a problem, in its colour.</summary>
public sealed record AgentStepRow(string Text, Brush Brush);

/// <summary>A changed file in Activity; the label says how it changed and whether undo can take it back.</summary>
public sealed record AgentChangeRow(string Label, string Path);

/// <summary>
/// The agent's side of the panel beside Link: the confirmation card before a task and Activity during and after it.
/// It only shows and asks; the card does the work behind each button.
/// </summary>
public partial class AgentPanel : UserControl
{
    private const int StepsKept = 300;
    private readonly ObservableCollection<AgentStepRow> _steps = [];
    private AgentCardModel? _card;
    private bool _showingCard;

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

    public event Action? ToChatRequested;
    public event Action? UndoRequested;

    /// <summary>Start over in a clean session with the agent's note on what did not work.</summary>
    public event Action? RestartRequested;
    public event Action<string>? OpenFileRequested;
    public event Action? Closed;

    /// <summary>Whether Activity shows a task, running or finished, rather than the card.</summary>
    public bool ShowsActivity => ActivityView.Visibility == Visibility.Visible;

    public void ShowCard(AgentCardModel card)
    {
        _card = card;
        _showingCard = true;
        ProviderCombo.ItemsSource = card.Providers.Select(provider => provider.Name).ToArray();
        ProviderCombo.SelectedIndex = Math.Max(0, card.Providers.ToList().FindIndex(provider => provider.Kind == card.Provider));
        // Read only by default: edits are chosen for this task, never assumed.
        AccessCombo.SelectedIndex = 0;
        PlanFirstBox.IsChecked = false;
        BackupBox.IsChecked = true;
        GoalBox.Text = card.Goal;
        ProjectText.Text = card.ProjectName;
        WorkspaceText.Text = card.Workspace ?? "Not chosen: choose it in the project panel";
        WorkspaceText.Foreground = (Brush)FindResource(card.Workspace is null ? "ErrorBrush" : "TextBrush");
        AttachmentsText.Text = card.Attachments;
        AttachmentsText.Visibility = card.Attachments.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CardWarningText.Visibility = Visibility.Collapsed;
        _showingCard = false;
        UpdateCard();
        ActivityView.Visibility = Visibility.Collapsed;
        CardView.Visibility = Visibility.Visible;
        Window.GetWindow(this)?.Activate();
        GoalBox.Focus();
        GoalBox.CaretIndex = GoalBox.Text.Length;
    }

    public void ShowCardWarning(string message)
    {
        CardWarningText.Text = message;
        CardWarningText.Visibility = Visibility.Visible;
    }

    public void ShowActivity(string header, string where)
    {
        _steps.Clear();
        HeaderText.Text = header;
        WhereText.Text = where;
        WhereText.ToolTip = where;
        CurrentStepText.Text = "Starting…";
        DecisionCard.Visibility = Visibility.Collapsed;
        RestartCard.Visibility = Visibility.Collapsed;
        ShowChanges([]);
        ResultLabel.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        ShowStats(string.Empty);
        LogText.Visibility = Visibility.Collapsed;
        UndoButton.Visibility = Visibility.Collapsed;
        SetRunning(true);
        CardView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Visible;
    }

    public void SetHeader(string header) => HeaderText.Text = header;

    public void SetRunning(bool running)
    {
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { ToChatButton, LogButton, NewTaskButton }) button.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        if (running) UndoButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>A batch of events at once: Activity redraws at most a few times a second, however fast they come.</summary>
    public void AddSteps(IEnumerable<AgentEvent> events)
    {
        var atEnd = StepsScroll.VerticalOffset >= StepsScroll.ScrollableHeight - 4;
        foreach (var agentEvent in events)
        {
            if (agentEvent.Kind is AgentEventKind.Tool or AgentEventKind.Status) CurrentStepText.Text = agentEvent.Text;
            // The result has its own place, a change is listed under Changed and failed calls are counted under the result.
            if (agentEvent.Kind is AgentEventKind.Completed or AgentEventKind.Artifact or AgentEventKind.ToolFailed) continue;
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
        if (atEnd) StepsScroll.ScrollToEnd();
    }

    public void ShowChanges(IReadOnlyList<AgentChangeRow> changes)
    {
        ChangesList.ItemsSource = changes;
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

    /// <summary>The task's numbers in one line under its result; empty hides it.</summary>
    public void ShowStats(string text)
    {
        StatsText.Text = text;
        StatsText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowLog(string log) => LogText.Text = log;

    /// <summary>A choice for the user in Activity; <paramref name="canRemember"/> offers "always for this project".</summary>
    public void ShowDecision(string text, string button, bool canRemember)
    {
        DecisionText.Text = text;
        DecisionButton.Content = button;
        AlwaysBox.IsChecked = false;
        AlwaysBox.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        DecisionCard.Visibility = Visibility.Visible;
    }

    public void HideDecision() => DecisionCard.Visibility = Visibility.Collapsed;

    public void ShowRestart(bool show) => RestartCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Link's own step around a run, such as asking for a note; while it runs, Stop shows and the choices hide.</summary>
    public void ShowStep(string step, bool running)
    {
        CurrentStepText.Text = step;
        SetRunning(running);
        if (!running) return;
        DecisionCard.Visibility = Visibility.Collapsed;
        RestartCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Offers to undo the changes or restore the copy; null hides the button.</summary>
    public void ShowUndo(string? label, string tooltip = "")
    {
        UndoButton.Content = label;
        UndoButton.ToolTip = tooltip;
        UndoButton.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private AgentProviderKind SelectedProvider =>
        _card is { } card && ProviderCombo.SelectedIndex >= 0 ? card.Providers[ProviderCombo.SelectedIndex].Kind : AgentProviderKind.ClaudeCode;

    private AgentAccess SelectedAccess => AccessCombo.SelectedIndex == 1 ? AgentAccess.Do : AgentAccess.Analyze;

    private void Card_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_showingCard) UpdateCard();
    }

    private void UpdateCard()
    {
        if (_card is not { } card) return;
        var edits = SelectedAccess == AgentAccess.Do;
        BackupBox.Visibility = edits && !card.WorkspaceHasGit ? Visibility.Visible : Visibility.Collapsed;
        CheckText.Text = $"After the edits Link runs the check itself, and the agent is told so: {card.CheckCommand}. " +
                         $"While it fails, the agent gets its output, {AgentCoordinator.CheckRetries} times at most.";
        CheckText.Visibility = edits && card.CheckCommand is not null ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = card.Workspace is not null;
        RunButton.Content = PlanFirstBox.IsChecked == true ? "Make a plan" : edits ? "Run with edits" : "Run";
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
            SelectedAccess,
            PlanFirstBox.IsChecked == true,
            BackupBox.Visibility == Visibility.Visible && BackupBox.IsChecked == true,
            GoalBox.Text.Trim()));
    }

    private void CancelCard_OnClick(object sender, RoutedEventArgs e) => CardCancelled?.Invoke();
    private void Stop_OnClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Decision_OnClick(object sender, RoutedEventArgs e) => DecisionTaken?.Invoke(AlwaysBox.IsChecked == true);
    private void ToChat_OnClick(object sender, RoutedEventArgs e) => ToChatRequested?.Invoke();
    private void Undo_OnClick(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();
    private void Close_OnClick(object sender, RoutedEventArgs e) => Closed?.Invoke();

    private void Log_OnClick(object sender, RoutedEventArgs e) =>
        LogText.Visibility = LogText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OpenFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentChangeRow row }) OpenFileRequested?.Invoke(row.Path);
    }
}
