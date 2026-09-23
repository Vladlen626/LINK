using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private readonly IChatIntegration _chat;
    private readonly DispatcherTimer _connectionTimer;
    private CancellationTokenSource? _sendCancellation;
    private bool _sending;
    private string _responseText = string.Empty;

    private async void Send_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        RemoveMissingContextFiles();
        if (string.IsNullOrWhiteSpace(PromptBox.Text) && _context.Count == 0)
        {
            SetError("Add text or context before sending.");
            return;
        }

        var conversation = _projects.ActiveProject.LinkedConversation;
        if (conversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
        _projects.Save();
        _sendCancellation = new CancellationTokenSource();
        SetSending(true);
        ShowResponse(ChatProgressState.Sending, string.Empty);
        ContextItem? transientContext = null;

        try
        {
            var externalWindow = _currentApplication.IsExternalWindow(_lastExternalWindow)
                ? _lastExternalWindow
                : IntPtr.Zero;
            var currentApplication = _currentApplication.GetContext(externalWindow);
            if (_settings.Current.AutoCaptureActiveWindowOnSend && externalWindow != IntPtr.Zero)
            {
                try
                {
                    var screenshot = await CaptureTransientWindowAsync(externalWindow);
                    transientContext = new ContextItem
                    {
                        Type = ContextItemType.Screenshot,
                        Label = "Current application",
                        FilePath = screenshot.FilePath,
                        MimeType = "image/png"
                    };
                }
                catch (Exception exception)
                {
                    SetStatus($"Auto screen unavailable: {exception.Message}", "Sending");
                }
            }

            var requestContext = transientContext is null
                ? _context.ToArray()
                : _context.Append(transientContext).ToArray();
            var request = _requests.BuildAndLog(
                _projects.ActiveProject,
                PromptBox.Text,
                requestContext,
                currentApplication);
            var progress = new Progress<ChatProgress>(OnChatProgress);
            var response = await _chat.SendAsync(request, conversation, progress, _sendCancellation.Token);
            ShowResponse(ChatProgressState.Completed, response.Text);
            PromptBox.Clear();
            ClearContext(prune: true);
            SetStatus("Response received", "Done");
            ShowPassive();
        }
        catch (OperationCanceledException) when (_sendCancellation.IsCancellationRequested)
        {
            ShowResponse(ChatProgressState.Error, "Request cancelled.");
            SetError("Request cancelled. Your prompt and context were kept.");
        }
        catch (Exception exception)
        {
            ShowResponse(ChatProgressState.Error, exception.Message);
            SetError($"{exception.Message} Prompt and context were kept.");
        }
        finally
        {
            DeleteTransientContext(transientContext);
            _sendCancellation.Dispose();
            _sendCancellation = null;
            SetSending(false);
            UpdateConnectionState();
        }
    }

    private void DeleteTransientContext(ContextItem? item)
    {
        if (item?.FilePath is null) return;
        try
        {
            File.Delete(item.FilePath);
        }
        catch (IOException)
        {
            // A failed transient cleanup can be handled by the normal screenshot pruning pass.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep sending successful even if Windows briefly retains the capture file.
        }
        finally
        {
            _history.Prune(_context.Where(context => context.FilePath is not null).Select(context => context.FilePath!));
        }
    }

    private void LinkConversation()
    {
        if (_sending) return;
        var dialog = new LinkConversationWindow(_chat, _projects.ActiveProject.LinkedConversation) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedConversation is not { } conversation) return;

        _projects.ActiveProject.LinkedConversation = new ChatConversationLink
        {
            TransportId = _chat.TransportId,
            ConversationId = conversation.ConversationId,
            Title = conversation.Title,
            Url = conversation.Url
        };
        _projects.Save();
        UpdateConnectionState();
        SetStatus($"Linked to {conversation.Title}");
    }

    private void UnlinkConversation()
    {
        if (_sending) return;
        _projects.ActiveProject.LinkedConversation = null;
        _projects.Save();
        UpdateConnectionState();
        SetStatus("Conversation unlinked");
    }

    private void ChatConnection_OnClick(object sender, RoutedEventArgs e) => LinkConversation();

    private void UpdateConnectionState()
    {
        var conversation = _projects.ActiveProject.LinkedConversation;
        var state = _chat.GetConnectionState(conversation);
        ChatConnectionText.Text = state switch
        {
            ChatConnectionState.NoLinkedConversation => "No linked conversation",
            ChatConnectionState.BridgeUnavailable => $"ChatGPT unavailable · {conversation?.Title}",
            ChatConnectionState.ConversationAvailable => $"ChatGPT connected · {conversation?.Title}",
            _ => "ChatGPT unavailable"
        };
        ChatStatusDot.Fill = (Brush)FindResource(state switch
        {
            ChatConnectionState.ConversationAvailable => "SuccessBrush",
            ChatConnectionState.BridgeUnavailable => "ErrorBrush",
            _ => "MutedBrush"
        });
    }

    private void SetSending(bool sending)
    {
        _sending = sending;
        SendButton.IsEnabled = !sending;
        PromptBox.IsReadOnly = sending;
        CurrentTaskBox.IsEnabled = !sending;
        ProjectMenuButton.IsEnabled = !sending;
        ChatConnectionButton.IsEnabled = !sending;
        ContextActionsPanel.IsEnabled = !sending;
        ContextItemsControl.IsEnabled = !sending;
        ScreenButton.IsEnabled = !sending;
        VoiceButton.IsEnabled = !sending;
        ClipboardButton.IsEnabled = !sending;
    }

    private void OnChatProgress(ChatProgress progress)
    {
        ShowResponse(progress.State, progress.Text.Length > 0 ? progress.Text : progress.Error ?? string.Empty);
        SetStatus(progress.State switch
        {
            ChatProgressState.Sending => "Sending to ChatGPT…",
            ChatProgressState.Waiting => "Waiting for ChatGPT…",
            ChatProgressState.Receiving => "Receiving response…",
            ChatProgressState.Completed => "Response received",
            ChatProgressState.Error => $"Error: {progress.Error}",
            _ => "Working…"
        }, progress.State switch
        {
            ChatProgressState.Sending => "Sending",
            ChatProgressState.Waiting => "Waiting",
            ChatProgressState.Receiving => "Receiving",
            ChatProgressState.Completed => "Done",
            _ => "Attention"
        });
    }

    private void ShowResponse(ChatProgressState state, string text)
    {
        ResponsePanel.Visibility = Visibility.Visible;
        ResponseStateText.Text = state switch
        {
            ChatProgressState.Sending => "Sending",
            ChatProgressState.Waiting => "Waiting for response",
            ChatProgressState.Receiving => "Receiving",
            ChatProgressState.Completed => "Assistant",
            ChatProgressState.Error => "Error",
            _ => "Assistant"
        };
        ResponseStateText.Foreground = (Brush)FindResource(state switch
        {
            ChatProgressState.Completed => "SuccessBrush",
            ChatProgressState.Error => "ErrorBrush",
            _ => "MutedBrush"
        });
        if (text.Length > 0 || state is ChatProgressState.Sending or ChatProgressState.Waiting)
        {
            _responseText = text;
            RenderResponse(text);
        }
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
        ResponseDocument.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var codeLines = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[index++]);
                }
                if (index < lines.Length) index++;

                var code = new Paragraph(new Run(string.Join('\n', codeLines)))
                {
                    Margin = new Thickness(0, 3, 0, 10),
                    Padding = new Thickness(9, 7, 9, 7),
                    Background = (Brush)FindResource("ResponseCodeBrush"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5,
                    LineHeight = 18
                };
                ResponseDocument.Blocks.Add(code);
                continue;
            }

            if (TryGetHeading(lines[index], out var headingLevel, out var headingText))
            {
                var heading = new Paragraph
                {
                    Margin = new Thickness(0, index == 0 ? 0 : 5, 0, 5),
                    FontSize = headingLevel switch { 1 => 17, 2 => 15.5, _ => 14.5 },
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = 22
                };
                AddInlineContent(heading.Inlines, headingText);
                ResponseDocument.Blocks.Add(heading);
                index++;
                continue;
            }

            if (TryGetListItem(lines[index], numbered: false, out _))
            {
                index = AddList(lines, index, numbered: false);
                continue;
            }

            if (TryGetListItem(lines[index], numbered: true, out _))
            {
                index = AddList(lines, index, numbered: true);
                continue;
            }

            var paragraphLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index]))
            {
                paragraphLines.Add(lines[index].Trim());
                index++;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
            AddInlineContent(paragraph.Inlines, string.Join(' ', paragraphLines));
            ResponseDocument.Blocks.Add(paragraph);
        }
    }

    private int AddList(string[] lines, int index, bool numbered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 1, 0, 9),
            Padding = new Thickness(0)
        };

        while (index < lines.Length && TryGetListItem(lines[index], numbered, out var itemText))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlineContent(paragraph.Inlines, itemText);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        ResponseDocument.Blocks.Add(list);
        return index;
    }

    private static bool StartsBlock(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        TryGetHeading(line, out _, out _) ||
        TryGetListItem(line, numbered: false, out _) ||
        TryGetListItem(line, numbered: true, out _);

    private static bool TryGetHeading(string line, out int level, out string text)
    {
        var trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            text = string.Empty;
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryGetListItem(string line, bool numbered, out string text)
    {
        var trimmed = line.TrimStart();
        if (!numbered)
        {
            if (trimmed.Length >= 2 && trimmed[1] == ' ' && trimmed[0] is '-' or '*' or '+')
            {
                text = trimmed[2..].Trim();
                return true;
            }
        }
        else
        {
            var separator = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (separator > 0 && trimmed[..separator].All(char.IsDigit))
            {
                text = trimmed[(separator + 2)..].Trim();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    private void AddInlineContent(InlineCollection inlines, string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var codeStart = text.IndexOf('`', index);
            var boldStart = text.IndexOf("**", index, StringComparison.Ordinal);
            var tokenStart = codeStart < 0 ? boldStart : boldStart < 0 ? codeStart : Math.Min(codeStart, boldStart);
            if (tokenStart < 0)
            {
                inlines.Add(new Run(text[index..]));
                return;
            }

            if (tokenStart > index) inlines.Add(new Run(text[index..tokenStart]));
            if (tokenStart == codeStart)
            {
                var end = text.IndexOf('`', codeStart + 1);
                if (end < 0)
                {
                    inlines.Add(new Run(text[tokenStart..]));
                    return;
                }

                inlines.Add(new Run(text[(codeStart + 1)..end])
                {
                    Background = (Brush)FindResource("ResponseCodeBrush"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5
                });
                index = end + 1;
            }
            else
            {
                var end = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inlines.Add(new Run(text[tokenStart..]));
                    return;
                }

                inlines.Add(new Bold(new Run(text[(boldStart + 2)..end])));
                index = end + 2;
            }
        }
    }
}
