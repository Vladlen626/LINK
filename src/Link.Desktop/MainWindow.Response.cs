using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// The answer on screen: drawn block by block as it streams, read aloud, copied, and its tasks for the agent.
public partial class MainWindow
{
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
        if (text.Length > 0 || state is ChatProgressState.Sending or ChatProgressState.Waiting)
        {
            _responseText = text;
            RenderResponse(text);
        }

        _responseState = state;
        UpdateReadAloudButton();
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
        var text = project.LastResponse is { } response && response.ConversationId == chat ? response.Text
            : chat is not null ? _projects.ReadHistory(project).FirstOrDefault(exchange => exchange.ConversationId == chat)?.Answer
            : null;
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
            FontSize = 11.5,
            Foreground = (Brush)FindResource("AccentBrush")
        });
        section.Blocks.Add(new Paragraph(new Run(brief)) { Margin = new Thickness(0), FontSize = 12.5 });
        var give = new Button
        {
            Content = "Give to agent",
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "A task card with this brief: you check it, nothing runs before Run"
        };
        give.Click += (_, _) => GiveToAgent(brief);
        section.Blocks.Add(new BlockUIContainer(give));
        return section;
    }

    private void OpenLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }
}
