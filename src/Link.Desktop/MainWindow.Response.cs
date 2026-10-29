using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// The answer on screen: drawn block by block as it streams, the picture ChatGPT makes, read aloud, copied, and its tasks
// for the agent.
public partial class MainWindow
{
    // ChatGPT gives no progress of a picture: the bar fills by time, half way after this many seconds, never quite full.
    private const double PictureHalfSeconds = 45;

    // The picture ChatGPT makes or made for the answer on screen; null when the answer has none.
    private ResponsePicture? _responsePicture;
    private DispatcherTimer? _pictureTimer;

    private void ShowResponse(ChatProgressState state, string text)
    {
        // Whatever comes on screen is not the attempt the redirects were for; an attempt names itself when it ends.
        _attemptOnScreen = null;
        RedirectPanel.Visibility = Visibility.Collapsed;
        ResponsePanel.Visibility = Visibility.Visible;
        ResponseStateText.Text = state switch
        {
            ChatProgressState.Sending => "Sending",
            ChatProgressState.Waiting => "Waiting for response",
            ChatProgressState.Receiving => "Receiving",
            ChatProgressState.Reconnecting => "Reconnecting",
            ChatProgressState.Completed => AnswerSource(),
            ChatProgressState.Cancelled => "Cancelled",
            ChatProgressState.Error => "Error",
            _ => "Assistant"
        };
        ResponseStateText.Foreground = (Brush)FindResource(state == ChatProgressState.Error ? "ErrorBrush" : "MutedBrush");
        ResponseStateDot.Fill = (Brush)FindResource(state switch
        {
            ChatProgressState.Completed => "SuccessOrbBrush",
            ChatProgressState.Cancelled => "IdleOrbBrush",
            ChatProgressState.Error => "ErrorOrbBrush",
            _ => "BusyOrbBrush"
        });
        if (text.Length > 0 || _responsePicture is not null || state is ChatProgressState.Sending or ChatProgressState.Waiting)
        {
            _responseText = text;
            RenderResponse(text);
        }

        _responseState = state;
        UpdateReadAloudButton();
        TickPicture();
    }

    /// <summary>Whether the answer on screen comes with a picture ChatGPT made; set before the answer is shown.</summary>
    private void SetPicture(bool made) => _responsePicture = made ? new ResponsePicture { Made = true } : null;

    /// <summary>ChatGPT has begun a picture: the answer shows its card, which counts the time until the answer ends.</summary>
    private void ShowPictureComing()
    {
        if (_responsePicture is not null) return;
        _responsePicture = new ResponsePicture();
        ResponsePanel.Visibility = Visibility.Visible;
        ResponseStateText.Text = "Creating image";
        RenderResponse(_responseText);
        TickPicture();
    }

    /// <summary>
    /// Moves the card of a picture still being made, once a second and only while it is: the overlay redraws for each
    /// tick.
    /// </summary>
    private void TickPicture()
    {
        var going = _sending && _responseState is not (ChatProgressState.Completed or ChatProgressState.Cancelled or ChatProgressState.Error);
        var making = _responsePicture is { Made: false };
        if (making) UpdatePictureCard(_responsePicture!, going);
        if (going && making)
        {
            if (_pictureTimer is null)
            {
                _pictureTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _pictureTimer.Tick += (_, _) => TickPicture();
            }

            _pictureTimer.Start();
        }
        else
        {
            _pictureTimer?.Stop();
        }
    }

    private static void UpdatePictureCard(ResponsePicture picture, bool going)
    {
        var seconds = (DateTime.UtcNow - picture.StartedAt).TotalSeconds;
        if (picture.Label is { } label)
        {
            label.Text = picture.Made ? "Image ready" : going ? $"Creating image… {seconds:0} s" : "Image not finished";
        }

        if (picture.Bar is { } bar) bar.Value = seconds / (seconds + PictureHalfSeconds);
    }

    /// <summary>Listen reads the answer on screen aloud; while Link talks the same button stops it.</summary>
    private void UpdateReadAloudButton()
    {
        var speaking = _voice.Phase == VoicePhase.Speaking;
        var canListen = _responseState == ChatProgressState.Completed && _responseText.Length > 0 && _voice.CanReadAloud;
        ReadAloudButton.Visibility = speaking || canListen ? Visibility.Visible : Visibility.Collapsed;
        ReadAloudButton.Content = speaking ? "Stop" : "Listen";
        ReadAloudButton.ToolTip = speaking ? "Stop reading aloud (Esc)" : "Read this answer aloud";
    }

    private async void ReadAloud_OnClick(object sender, RoutedEventArgs e)
    {
        if (_voice.Phase == VoicePhase.Speaking)
        {
            _voice.StopSpeaking();
            return;
        }

        try
        {
            await _voice.ReadAloudAsync(_responseText);
        }
        catch (Exception exception)
        {
            SetError($"The answer could not be read aloud: {exception.Message}");
        }
    }

    /// <summary>Shows the project's last answer, or hides the panel when its linked chat has none yet.</summary>
    private void ShowProjectResponse(LinkProject project)
    {
        // What went with that answer is not known any more.
        ShowSaw(null);
        // An answer belongs to the chat it came from: another chat shows its own last answer, a new one none.
        var chat = project.LinkedConversation is { IsNew: false } link ? link.ConversationId : null;
        var last = project.LastResponse is { } response && response.ConversationId == chat ? response : null;
        var exchange = last is null && chat is not null
            ? _projects.ReadHistory(project).FirstOrDefault(exchange => exchange.ConversationId == chat)
            : null;
        var text = last?.Text ?? exchange?.Answer;
        SetPicture(last?.Picture ?? exchange?.Picture ?? false);
        if (text is null)
        {
            _attemptOnScreen = null;
            _responseText = string.Empty;
            RenderResponse(string.Empty);
            ResponsePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowResponse(ChatProgressState.Completed, text);
    }

    private void CopyResponse_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_responseText)) return;
        try
        {
            Clipboard.SetText(_responseText);
            SetStatus("Response copied");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void CloseResponse_OnClick(object sender, RoutedEventArgs e) =>
        ResponsePanel.Visibility = Visibility.Collapsed;

    private void RenderResponse(string text)
    {
        _nextResponseBlock = 0;
        // The picture goes first, as ChatGPT shows it; the text under it grows without redrawing it.
        if (_responsePicture is { } picture)
        {
            AddResponseBlock(picture.Made ? "\0picture made" : "\0picture coming", () => CreatePictureBlock(picture));
        }

        // A task ChatGPT wrote for the agent: a card with Give to agent, once its block is complete.
        _markdown ??= new MarkdownRenderer(this, OpenLink);
        _markdown.Render(text, AddResponseBlock, (language, code) =>
            language.Equals("link-task", StringComparison.OrdinalIgnoreCase) ? () => CreateTaskBlock(code.Trim()) : null);
        RemoveResponseBlocks(from: _nextResponseBlock);
    }

    /// <summary>
    /// Shows the next block of the answer. A streaming answer only grows at its end, so a block whose source is
    /// unchanged keeps its rendered form and layout; from the first changed block on everything is built anew.
    /// </summary>
    private void AddResponseBlock(string source, Func<Block> create)
    {
        if (_nextResponseBlock < _responseBlocks.Count)
        {
            if (_responseBlocks[_nextResponseBlock].Source == source)
            {
                _nextResponseBlock++;
                return;
            }

            RemoveResponseBlocks(from: _nextResponseBlock);
        }

        var block = create();
        ResponseDocument.Blocks.Add(block);
        _responseBlocks.Add((source, block));
        _nextResponseBlock++;
    }

    private void RemoveResponseBlocks(int from)
    {
        for (var index = _responseBlocks.Count - 1; index >= from; index--)
        {
            ResponseDocument.Blocks.Remove(_responseBlocks[index].Block);
        }

        _responseBlocks.RemoveRange(from, _responseBlocks.Count - from);
    }

    /// <summary>A link-task block of an answer: the brief ChatGPT wrote for the agent, and the button that takes it there.</summary>
    private Block CreateTaskBlock(string brief)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            Background = (Brush)FindResource("ResponseCodeBrush"),
            BorderBrush = (Brush)FindResource("AccentBorderBrush"),
            BorderThickness = new Thickness(1)
        };
        section.Blocks.Add(new Paragraph(new Bold(new Run("Task for the agent")))
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 12,
            Foreground = (Brush)FindResource("AccentBrush")
        });
        section.Blocks.Add(new Paragraph(new Run(brief)) { Margin = new Thickness(0), FontSize = 13 });
        var give = new Button
        {
            Content = "Give to agent",
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "A task card with this brief: you check it, nothing runs before Run"
        };
        give.Click += (_, _) => GiveToAgent(brief);
        section.Blocks.Add(new BlockUIContainer(give));
        return section;
    }

    /// <summary>
    /// The picture's card: how long ChatGPT has been making it, or that it is ready. The picture itself stays in ChatGPT,
    /// so the card opens the chat there: the user looks at it, saves it or goes on in the same chat.
    /// </summary>
    private Block CreatePictureBlock(ResponsePicture picture)
    {
        var content = new StackPanel { Width = 240 };
        picture.Label = new TextBlock { FontSize = 13 };
        content.Children.Add(picture.Label);
        picture.Bar = null;
        if (!picture.Made)
        {
            picture.Bar = new ProgressBar
            {
                Style = (Style)FindResource("UsageBarStyle"),
                Maximum = 1,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("AccentBrush")
            };
            content.Children.Add(picture.Bar);
        }

        content.Children.Add(new TextBlock
        {
            Text = "Open in ChatGPT",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("AccentBrush")
        });
        UpdatePictureCard(picture, going: true);
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("ResponseCodeBrush"),
            BorderBrush = (Brush)FindResource("AccentBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Open the chat in Link's ChatGPT window: look at the picture, save it or go on there",
            Child = content
        };
        // A chat the prompt has just started has no address yet: the window shows the page as it is.
        card.MouseLeftButtonUp += (_, _) =>
            OpenChat(_projects.ActiveProject.LinkedConversation is { IsNew: false } chat ? chat : null);
        return new BlockUIContainer(card) { Margin = new Thickness(0, 2, 0, 10) };
    }

    /// <summary>The picture of the answer on screen, as its card shows it.</summary>
    private sealed class ResponsePicture
    {
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        /// <summary>ChatGPT has made it; otherwise it is being made, or was when the answer ended.</summary>
        public bool Made { get; init; }

        public TextBlock? Label { get; set; }

        /// <summary>The bar that fills by time while the picture is made.</summary>
        public ProgressBar? Bar { get; set; }
    }

    private void OpenLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.IsFile ? uri.LocalPath : uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }
}
