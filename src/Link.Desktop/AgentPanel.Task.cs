using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// An open task: its header, thread and plan, and the cards in it (question, decision, limit, rating, restart).
public partial class AgentPanel
{
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
        RestartCard.Visibility = Visibility.Collapsed;
        LimitCard.Visibility = Visibility.Collapsed;
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
        // Words opened stay open while the thread is drawn again.
        foreach (var words in items.OfType<ThreadUser>()) words.Open = _openWords.Contains(words.Key);
        ThreadList.ItemsSource = items;
        Dispatcher.BeginInvoke(ThreadScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The running turn grew: the thread follows it while the user is at its end.</summary>
    public void FollowThread()
    {
        if (ThreadScroll.VerticalOffset >= ThreadScroll.ScrollableHeight - 24) ThreadScroll.ScrollToEnd();
    }

    /// <summary>
    /// The agent's plan as it last wrote it, as its own app shows it: ✓ done, ● under way (in the agent's words for it,
    /// when it gave any), ○ to do; a step by its first sentence, the rest a click away. A team's step also names who does
    /// it and as which specialist, in the colour of that specialist's jellyfish, found by name among
    /// <paramref name="roles"/>, and why that worker under its words. Its worker chip opens <paramref name="stepMenu"/>
    /// for the step, made when it is clicked.
    /// </summary>
    public void ShowPlan(IReadOnlyList<AgentPlanItem> plan, IReadOnlyList<AgentRole>? roles = null,
        Func<int, IReadOnlyList<IReadOnlyList<PanelAction>>>? stepMenu = null)
    {
        _stepMenu = stepMenu;
        var done = (Brush)FindResource("SuccessBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var text = (Brush)FindResource("TextBrush");
        PlanList.ItemsSource = plan.Select((item, index) =>
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
                Detail = detail, Worker = item.Worker ?? string.Empty, RoleName = item.Role ?? string.Empty, RoleBrush = color, Index = index,
                Open = _openSteps.Contains(title)
            };
        }).ToArray();
        _planDone = plan.Count(item => item.Done);
        _planCount = plan.Count;
        PlanCard.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanList.Visibility = _planOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowPlanLabel();
    }

    private void ShowPlanLabel() => PlanLabel.Text = $"Plan · {_planDone} of {_planCount} {(_planOpen ? "▾" : "▸")}";

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

    /// <summary>
    /// A choice for the user; <paramref name="canRemember"/> offers "always for this project", <paramref name="refusable"/>
    /// a Deny beside the button.
    /// </summary>
    public void ShowDecision(string text, string button, bool canRemember, bool refusable = false)
    {
        DecisionText.Text = text;
        DecisionButton.Content = button;
        AlwaysBox.IsChecked = false;
        AlwaysBox.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        DenyButton.Visibility = refusable ? Visibility.Visible : Visibility.Collapsed;
        DecisionCard.Visibility = Visibility.Visible;
    }

    public void HideDecision() => DecisionCard.Visibility = Visibility.Collapsed;

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

    // A click only: the box ticked by ShowLimit is no change of the user's.
    private void LimitBox_OnClick(object sender, RoutedEventArgs e) => _limitToggle?.Changed(LimitBox.IsChecked == true);

    private void PlanToggle_OnClick(object sender, RoutedEventArgs e)
    {
        _planOpen = !_planOpen;
        PlanList.Visibility = _planOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowPlanLabel();
    }

    // The chip sits in the row's button: its click is the menu's alone, not the row's opening too.
    private void PlanWorker_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlanRow row } chip && _stepMenu is { } menu) ShowMenu(chip, menu(row.Index), PlacementMode.Bottom);
    }

    private void PlanRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanRow row }) return;
        row.Open = !row.Open;
        if (row.Open) _openSteps.Add(row.Title);
        else _openSteps.Remove(row.Title);
    }

    private void UserWords_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThreadUser words }) return;
        words.Open = !words.Open;
        if (words.Open) _openWords.Add(words.Key);
        else _openWords.Remove(words.Key);
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

    private void Stop_OnClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Decision_OnClick(object sender, RoutedEventArgs e) => DecisionTaken?.Invoke(AlwaysBox.IsChecked == true);

    private void Deny_OnClick(object sender, RoutedEventArgs e) => DecisionRefused?.Invoke();
    private void Restart_OnClick(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();
    private void Escalate_OnClick(object sender, RoutedEventArgs e) => EscalateRequested?.Invoke();
    private void RestartNotNow_OnClick(object sender, RoutedEventArgs e) => ShowRestart(false);

    /// <summary>
    /// A page the agent opened opens in the browser, served again first by the skill's own command (a review window's
    /// server ends a while after its tab closes): no agent run for it. One Link cannot bring back is the agent's: the
    /// words for it wait in the reply box.
    /// </summary>
    private async void OpenPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalPages.Page page } button) return;
        button.IsEnabled = false;
        var address = await LocalPages.ReopenAsync(page, _card?.Workspace)
            ?? (await LocalPages.IsUpAsync(page.Address) ? page.Address : null);
        button.IsEnabled = true;
        if (address is null && _replyShown && !_running)
        {
            var words = $"The page {page.Address} does not open any more: open it again.";
            ReplyBox.Text = ReplyBox.Text.Trim().Length == 0 ? words : ReplyBox.Text.TrimEnd() + "\n" + words;
            ReplyBox.CaretIndex = ReplyBox.Text.Length;
            ReplyBox.Focus();
            return;
        }

        MarkdownText.Open(address ?? page.Address);
    }

    // The page's reply ("Apply marks"): the user's work there goes to the agent without typing it; mid-run it waits its turn.
    private async void PageReply_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalPages.Page { Reply: { } reply } } button) return;
        button.IsEnabled = false;
        // The open marks go with the words: the agent spends no turn of its own on fetching them.
        var text = await LocalPages.ReplyTextAsync(reply, _card?.Workspace);
        button.IsEnabled = true;
        if (_running) QueueRequested?.Invoke(text, []);
        else if (_replyShown) ContinueRequested?.Invoke(text, []);
    }

    private void OpenFile_OnClick(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.DataContext switch
        {
            AgentChangeRow row => row.Path,
            ThreadImage image => image.Path,
            _ => null
        };
        if (path is not null) OpenFileRequested?.Invoke(path);
    }
}
