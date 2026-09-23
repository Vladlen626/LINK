using System.Windows;
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
        var request = _requests.BuildAndLog(_projects.ActiveProject, PromptBox.Text, _context);
        _sendCancellation = new CancellationTokenSource();
        SetSending(true);
        ShowResponse(ChatProgressState.Sending, string.Empty);

        try
        {
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
            _sendCancellation.Dispose();
            _sendCancellation = null;
            SetSending(false);
            UpdateConnectionState();
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
            ResponseText.Text = text;
        }
    }

    private void CopyResponse_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResponseText.Text)) return;
        try
        {
            Clipboard.SetText(ResponseText.Text);
            SetStatus("Response copied");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void CloseResponse_OnClick(object sender, RoutedEventArgs e) =>
        ResponsePanel.Visibility = Visibility.Collapsed;
}
