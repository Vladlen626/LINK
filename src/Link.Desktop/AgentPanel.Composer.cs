using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Link.Desktop.Services;

namespace Link.Desktop;

// The box at the foot of a task (words, send, its chips and menus) and what is pasted or dropped for it or the card.
public partial class AgentPanel
{
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

    /// <summary>The task's No-stop box at the foot, with the choice it is about to take; null hides it (Link's own answer).</summary>
    public void ShowNoStop(PanelToggle? toggle)
    {
        _noStopToggle = toggle;
        NoStopLiveBox.Visibility = toggle is null ? Visibility.Collapsed : Visibility.Visible;
        NoStopLiveBox.Content = toggle?.Label;
        NoStopLiveBox.ToolTip = toggle?.Hint;
        NoStopLiveBox.IsChecked = toggle?.On == true;
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

    /// <summary>Whether the next words re-plan the team's steps left with its lead: its chip calls that off.</summary>
    public void ShowReplan(bool replanning)
    {
        _replanning = replanning;
        ReplanButton.Visibility = replanning ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplyHint();
        UpdateComposer();
        if (replanning) ReplyBox.Focus();
    }

    /// <summary>What the next words do when it is not the usual reply, in the box's placeholder; null: the usual.</summary>
    public void ShowReplyHint(string? hint)
    {
        _replyHint = hint;
        UpdateReplyHint();
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
                : _replanning
                ? "Tell the team's lead what to change: the steps done stay, it plans the rest anew"
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
        var chips = NoStopLiveBox.Visibility == Visibility.Visible || !_running && (PlanModeButton.Visibility == Visibility.Visible
            || ReplanButton.Visibility == Visibility.Visible
            || HandOffButton.Visibility == Visibility.Visible || FinishButton.Visibility == Visibility.Visible);
        ReplyEditor.Visibility = _replyShown ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.Visibility = _replyShown ? Visibility.Visible : Visibility.Collapsed;
        SetupButton.Visibility = _replyShown && _setup.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ComposerFooter.Margin = new Thickness(0, _replyShown ? 6 : 0, 0, 0);
        ReplyPanel.Visibility = _replyShown || chips ? Visibility.Visible : Visibility.Collapsed;
    }

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
            if (RunImages.IsImage(path) && Thumbnail(path) is { } picture)
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

    private void HandOff_OnClick(object sender, RoutedEventArgs e) => ShowMenu(HandOffButton, [_handOff], PlacementMode.Top);

    private void More_OnClick(object sender, RoutedEventArgs e) => ShowMenu(MoreButton, [_more], PlacementMode.Bottom);

    private void Setup_OnClick(object sender, RoutedEventArgs e) => ShowMenu(SetupButton, _setup, PlacementMode.Top);

    private void NoStopLiveBox_OnClick(object sender, RoutedEventArgs e) => _noStopToggle?.Changed(NoStopLiveBox.IsChecked == true);

    private void Finish_OnClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();

    private void PlanMode_OnClick(object sender, RoutedEventArgs e) => PlanModeOffRequested?.Invoke();

    private void Replan_OnClick(object sender, RoutedEventArgs e) => ReplanOffRequested?.Invoke();
}
