using System.Windows;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// The chat the project talks in: linked, switched, started anew with a summary, its connection, and checked.
public partial class MainWindow
{
    /// <summary>Ctrl+K: the project's chats in the panel, where one opens and goes on, or a new one starts.</summary>
    private void ShowAllChats()
    {
        SetPanelOpen(true);
        ShowPanelTab(PanelTab.Chats);
        _shownChat = null;
        _chatsPanel?.ShowListView();
    }

    /// <summary>
    /// A chat of those Link's ChatGPT page knows, picked in a dialog (the project menu, ChatGPT's chats… in the panel):
    /// Link talks there from now on. The dialog also starts a new chat or shows the page.
    /// </summary>
    private void LinkConversation()
    {
        if (_sending) return;
        var dialog = new LinkConversationWindow(_chat, _projects.ActiveProject.LinkedConversation) { Owner = this };
        var linked = dialog.ShowDialog() == true;
        // The chat is opened once the dialog is gone: while it is open, Link's other windows take no input.
        if (dialog.OpenChatRequested)
        {
            OpenChat(null);
            return;
        }

        if (dialog.NewChatRequested)
        {
            if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
            _ = StartNewChatAsync(dialog.WithSummary);
            return;
        }

        if (!linked || dialog.SelectedConversation is not { } conversation) return;
        SwitchChat(conversation.ConversationId, conversation.Title, conversation.Url);
        if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
    }

    /// <summary>Link talks in this chat from now on, as clicking a chat in ChatGPT does; its last answer comes back on screen.</summary>
    private void SwitchChat(string conversationId, string title, string url)
    {
        var project = _projects.ActiveProject;
        if (_sending || project.LinkedConversation?.ConversationId == conversationId) return;
        project.LinkedConversation = new ChatConversationLink
        {
            TransportId = _chat.TransportId,
            ConversationId = conversationId,
            Title = title,
            Url = url
        };
        project.RememberChat(project.LinkedConversation);
        _projects.Save();
        UpdateConnectionState();
        ShowProjectResponse(project);
        RefreshChats();
        UpdateRouteButton();
        SetStatus($"Your next message goes to the chat {title}");
    }

    /// <summary>
    /// Moves the project to a new chat, in the ChatGPT project of its current one: the next message starts it and links
    /// the project to it. With a summary, the current chat writes a short one first; it waits in the context stack and
    /// goes with that message, visible and removable like any context.
    /// </summary>
    private async Task StartNewChatAsync(bool withSummary)
    {
        if (_sending) return;
        var project = _projects.ActiveProject;
        var current = project.LinkedConversation;
        ChatConversationLink next;
        try
        {
            next = _chat.NewChat(current);
        }
        catch (InvalidOperationException exception)
        {
            SetError(exception.Message);
            return;
        }

        if (withSummary && current is { IsNew: false })
        {
            var summary = await WriteSummaryAsync(project);
            if (summary is null) return;
            foreach (var old in _context.Where(item => item.Type == ContextItemType.ChatSummary).ToArray()) _context.Remove(old);
            AddContext(new ContextItem { Type = ContextItemType.ChatSummary, Label = "Summary of the previous chat", Text = summary });
        }

        project.LinkedConversation = next;
        _projects.Save();
        UpdateConnectionState();
        RefreshChats();
        // The previous chat's answer stays on screen only when it is the summary that goes on.
        if (!withSummary) ShowProjectResponse(project);
        SetStatus(withSummary
            ? "Summary ready: your next message starts the new chat and takes it along"
            : "Your next message starts a new chat");
    }

    /// <summary>The current chat's summary of itself, or null when it could not be written (the reason is shown).</summary>
    private async Task<string?> WriteSummaryAsync(LinkProject project)
    {
        StopHandsFree();
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        _chatNotice = null;
        _receivingSince = null;
        SetSending(true);
        _chatHeardAt = DateTime.UtcNow;
        SetPicture(false);
        ShowResponse(ChatProgressState.Sending, string.Empty);
        SetStatus("Asking the current chat for a summary…");
        try
        {
            var snapshot = new InputSnapshot
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                Text = SummaryPrompt
            };
            // Fast: a summary needs no long thinking, and a long chat writes slowly as it is.
            var response = await _attempts.SendAsync(
                snapshot,
                RequestRoute.Chat,
                ResponseEffort.Fast,
                spoken: false,
                new RequestLatency(),
                OnChatProgress,
                cancellation.Token);
            ShowResponse(ChatProgressState.Completed, response.Text);
            return response.Text;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowResponse(ChatProgressState.Cancelled, "Summary cancelled.");
            SetStatus("Summary cancelled; the project stays with its chat.");
            return null;
        }
        catch (Exception exception)
        {
            ShowResponse(ChatProgressState.Error, exception.Message);
            SetError($"No summary: {exception.Message} The project stays with its chat.");
            return null;
        }
        finally
        {
            _sendCancellation = null;
            cancellation.Dispose();
            SetSending(false);
        }
    }

    private void UnlinkConversation()
    {
        if (_sending) return;
        _projects.ActiveProject.LinkedConversation = null;
        _projects.Save();
        UpdateConnectionState();
        RefreshChats();
        ShowProjectResponse(_projects.ActiveProject);
        SetStatus("Conversation unlinked");
    }

    private void OpenChat(ChatConversationLink? conversation)
    {
        try
        {
            _chat.OpenChat(conversation);
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void UpdateConnectionState()
    {
        var conversation = _projects.ActiveProject.LinkedConversation;
        var state = _chat.GetConnectionState(conversation);
        var text = state switch
        {
            ChatConnectionState.NoLinkedConversation => "No linked conversation",
            ChatConnectionState.Connecting => $"Connecting to ChatGPT… · {conversation?.Title}",
            ChatConnectionState.BridgeUnavailable => $"ChatGPT unavailable · {conversation?.Title}",
            ChatConnectionState.SignInRequired => "Sign in to ChatGPT: Open chat",
            ChatConnectionState.ConversationAvailable => $"ChatGPT connected · {conversation?.Title}",
            _ => "ChatGPT unavailable"
        };
        var dot = (Brush)FindResource(state switch
        {
            ChatConnectionState.ConversationAvailable => "SuccessOrbBrush",
            ChatConnectionState.BridgeUnavailable or ChatConnectionState.SignInRequired => "ErrorOrbBrush",
            _ => "IdleOrbBrush"
        });
        ChatStatusDot.Fill = dot;
        ProjectMenuButton.ToolTip = $"{text}\nSwitch project";
        // The chat Link talks in may have changed or been named: the destination says it.
        UpdateRouteButton();
        _chatsPanel?.ShowConnection(text, dot, conversation is { IsNew: false });

        if (state == _connectionState) return;
        _connectionState = state;
        if (state == ChatConnectionState.SignInRequired) _cues.Play(SoundCue.NeedsDecision);
        // The chat row lives in the panel, which may be closed: trouble with the chat shows in the status line too.
        if (_sending) ReportState();
        else if (state == ChatConnectionState.SignInRequired) SetStatus("Sign in to ChatGPT: panel → Open chat");
        else if (state == ChatConnectionState.BridgeUnavailable) SetStatus(text);
        else ReportState();
    }

    private async void CheckChat_OnClick(object sender, RoutedEventArgs e) => await CheckChatAsync(automatic: false);

    /// <summary>Runs every two seconds: a waiting prompt the chat has been quiet about for a while gets it checked.</summary>
    private void CheckQuietChat()
    {
        if (!_sending || _checkingChat || DateTime.UtcNow - _chatHeardAt < QuietChatCheck) return;
        _chatHeardAt = DateTime.UtcNow;
        _ = CheckChatAsync(automatic: true);
    }

    /// <summary>
    /// Reads the linked chat's answer to its last prompt. A waiting prompt that the chat has finished answering is
    /// completed with it by the transport and ends as usual; otherwise the answer is shown and kept for the project.
    /// </summary>
    private async Task CheckChatAsync(bool automatic)
    {
        var project = _projects.ActiveProject;
        if (project.LinkedConversation is not { } conversation)
        {
            if (!automatic) SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        _checkingChat = true;
        CheckChatButton.IsEnabled = false;
        if (!automatic) SetStatus("Checking the chat…");
        try
        {
            var snapshot = await _chat.CheckAsync(conversation, CancellationToken.None);
            // A finished answer that Link missed is what the log is for; routine automatic looks stay out of it.
            var finished = snapshot.Text.Length > 0 && !snapshot.Generating;
            if (snapshot.Diagnostics is { } diagnostics && (!automatic || finished))
            {
                DiagnosticsReported?.Invoke($"Check chat ({(automatic ? "automatic" : "button")}): {diagnostics}");
            }

            if (snapshot.CompletedWaitingPrompt || automatic) return;
            if (snapshot.Text.Length == 0)
            {
                SetStatus(snapshot.Generating ? "ChatGPT is still working on the answer" : "The chat has no answer to its last prompt yet");
            }
            else if (snapshot.Generating)
            {
                ShowResponse(ChatProgressState.Receiving, snapshot.Text);
                SetStatus("ChatGPT is still writing — this is the answer so far");
            }
            else if (_sending)
            {
                SetStatus("The prompt has not reached the chat yet; its last answer belongs to an earlier one");
            }
            else
            {
                project.LastResponse = new ProjectResponse { ConversationId = conversation.ConversationId, Text = snapshot.Text };
                _projects.Save();
                SetPicture(false);
                ShowResponse(ChatProgressState.Completed, snapshot.Text);
                SetStatus("Answer taken from the chat");
            }
        }
        catch (Exception exception)
        {
            if (!automatic) SetError(exception.Message);
        }
        finally
        {
            _checkingChat = false;
            CheckChatButton.IsEnabled = true;
        }
    }
}
