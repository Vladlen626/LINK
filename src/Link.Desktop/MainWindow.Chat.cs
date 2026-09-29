using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private static readonly Geometry StopIconGeometry = CreateFrozenGeometry("M8,8 L16,8 L16,16 L8,16 Z");
    // When a waiting prompt has heard nothing from the chat for this long, Link looks at the chat itself: the tab's
    // watch may have missed the end of the answer. A chat that still thinks or writes is only looked at.
    private static readonly TimeSpan QuietChatCheck = TimeSpan.FromSeconds(20);
    // ChatGPT itself answers slowly in a chat that has grown very long, while a new chat answers at hundreds of
    // characters a second; an answer this slow over this long gets a hint to start one.
    private const double SlowChatCharactersPerSecond = 25;
    private static readonly TimeSpan SlowChatSample = TimeSpan.FromSeconds(15);
    // Asked of the chat a project leaves: short, because a long chat writes slowly.
    private const string SummaryPrompt =
        "Summarize this conversation so that the work can continue in a new chat: the goal, key decisions and facts, " +
        "the current state, the next step and open questions. At most 120 words, only the summary, no preamble. " +
        "Write it in the language of this conversation.";
    private static readonly (ResponseEffort Effort, string Label, string Description)[] EffortOptions =
    [
        (ResponseEffort.Fast, "Fast", "Fast — short answer, no long thinking"),
        (ResponseEffort.Normal, "Normal", "Normal — the chat's usual answer"),
        (ResponseEffort.Deep, "Deep", "Deep — thinks longer, more thorough")
    ];
    private readonly IChatIntegration _chat;
    private readonly DispatcherTimer _connectionTimer;
    // The answer on screen: each block's Markdown source with its document block, reused while the answer streams.
    private readonly List<(string Source, Block Block)> _responseBlocks = [];
    private CancellationTokenSource? _sendCancellation;
    private bool _sending;
    private string _responseText = string.Empty;
    private ChatProgressState _responseState;
    private int _nextResponseBlock;
    private string? _chatNotice;
    private ChatConnectionState _connectionState;
    private DateTime _chatHeardAt;
    private bool _checkingChat;
    // When the answer began to arrive and how much of it came at once, for the slow-chat hint.
    private DateTime? _receivingSince;
    private int _receivingFrom;
    private RequestLatency _latency = new();

    /// <summary>What a check of the chat saw on the page, for link.log.</summary>
    public event Action<string>? DiagnosticsReported;

    /// <summary>
    /// The global Send hotkey: works from any app and never cancels a running request.
    /// While an answer is read aloud it only silences Link.
    /// </summary>
    public async Task SendFromHotkeyAsync()
    {
        if (_selectingRegion) return;
        if (_voice.Phase == VoicePhase.Speaking)
        {
            _voice.StopSpeaking();
            return;
        }

        // The progress and any "nothing to send" message must be visible even when Link was hidden.
        ShowPassive();
        // One message at a time: words dictated while an answer arrives wait in the prompt.
        if (_sending) return;
        await SendOrQueueAsync();
    }

    private async void Send_OnClick(object sender, RoutedEventArgs e)
    {
        // Silencing Link comes first; the answer keeps arriving on screen and the next press stops the request.
        if (_voice.Phase == VoicePhase.Speaking)
        {
            _voice.StopSpeaking();
            return;
        }

        if (_sending)
        {
            CancelSend();
            return;
        }

        await SendOrQueueAsync();
    }

    private async Task SendOrQueueAsync()
    {
        if (_voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing)
        {
            // Push-to-talk was just released: send as soon as the transcript is in the prompt.
            _voice.SendWhenTranscribed();
            SetStatus("Will send when the transcript is ready…");
            return;
        }

        await SendAsync();
    }

    private void CancelSend()
    {
        if (_sendCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        SetStatus("Cancelling…");
    }

    private async Task SendAsync()
    {
        if (_sending) return;
        RemoveMissingContextFiles();
        // The project state alone is not a question.
        if (string.IsNullOrWhiteSpace(PromptBox.Text) && _context.All(item => item.Type == ContextItemType.ProjectState))
        {
            SetError("Add text or context before sending.");
            return;
        }

        var project = _projects.ActiveProject;
        var conversation = project.LinkedConversation;
        if (conversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        _chatNotice = null;
        _receivingSince = null;
        _latency = new RequestLatency();
        _voice.BeginRequest(PromptBox.Text);
        SetSending(true);
        _chatHeardAt = DateTime.UtcNow;
        ShowResponse(ChatProgressState.Sending, string.Empty);
        ContextItem? transientContext = null;

        try
        {
            // The app Link was invoked from, not whatever took the focus since (the browser, Link's own captures).
            var workTarget = _currentApplication.WorkTarget;
            var externalWindow = _currentApplication.IsExternalWindow(workTarget) ? workTarget : IntPtr.Zero;
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
                    SetStatus($"Auto screen unavailable: {exception.Message}");
                }
            }

            var requestContext = transientContext is null
                ? _context.ToArray()
                : _context.Append(transientContext).ToArray();
            var prompt = PromptBox.Text;
            var request = _requests.BuildAndLog(
                project,
                prompt,
                requestContext,
                currentApplication,
                _settings.Current.ResponseEffort);
            var progress = new Progress<ChatProgress>(OnChatProgress);
            _latency.HandedOver();
            var response = await _chat.SendAsync(request, conversation, progress, cancellation.Token);
            // A new chat is linked by now: the answer belongs to the conversation it started.
            RefreshLinkedTitle(project);
            project.LastResponse = new ProjectResponse
            {
                ConversationId = project.LinkedConversation?.ConversationId ?? conversation.ConversationId,
                Text = response.Text
            };
            _projects.Save();
            ShowResponse(ChatProgressState.Completed, response.Text);
            DiagnosticsReported?.Invoke(_latency.Describe("answer", response.Text.Length));
            // Words dictated after interrupting the spoken answer are the next message: only the sent prompt goes.
            var sent = prompt.TrimEnd();
            PromptBox.Text = PromptBox.Text.StartsWith(sent, StringComparison.Ordinal)
                ? PromptBox.Text[sent.Length..].Trim()
                : string.Empty;
            ClearContext(prune: true);
            ShowProjectStateContext();
            RememberExchange(project, prompt, requestContext, response.Text);
            if (!IsVisible) _unseen = LinkState.Done;
            SetStatus(SlowChatHint(response.Text) ?? ResponseReceivedStatus());
            NotifyIfAway(project, "Answer ready", ToPreviewText(response.Text));
            _ = ReadAnswerAloudAsync(response.Text);
            if (_voice.Phase == VoicePhase.Speaking) SetStatus(SpeakingStatus);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowResponse(ChatProgressState.Cancelled, "Request cancelled.");
            DiagnosticsReported?.Invoke(_latency.Describe("cancelled"));
            SetStatus("Request cancelled. Your prompt and context were kept.");
        }
        catch (Exception exception)
        {
            ShowResponse(ChatProgressState.Error, exception.Message);
            DiagnosticsReported?.Invoke(_latency.Describe($"failed ({exception.Message})"));
            // The prompt waits to be sent again: the user decides what to do with it.
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            SetError($"{exception.Message} Prompt and context were kept.");
            NotifyIfAway(project, "Request failed", exception.Message);
        }
        finally
        {
            // After a failure or Stop; an answer being read aloud keeps its phase.
            _voice.EndRequest();
            DeleteTransientContext(transientContext);
            _sendCancellation = null;
            cancellation.Dispose();
            SetSending(false);
            UpdateConnectionState();
        }
    }

    private string ResponseReceivedStatus() =>
        _chatNotice is null ? "Response received" : $"Response received · {_chatNotice}";

    /// <summary>A hint to start a new chat when ChatGPT typed this answer out slowly: its chat has grown too long.</summary>
    private string? SlowChatHint(string answer)
    {
        if (_receivingSince is not { } since || DateTime.UtcNow - since < SlowChatSample) return null;
        var rate = (answer.Length - _receivingFrom) / (DateTime.UtcNow - since).TotalSeconds;
        return rate < SlowChatCharactersPerSecond
            ? $"Slow answer ({rate:0} characters/s): this chat has grown long, a new one is faster (chat row → New chat)"
            : null;
    }

    private async Task ReadAnswerAloudAsync(string answer)
    {
        try
        {
            await _voice.CompleteRequestAsync(answer);
        }
        catch (Exception exception)
        {
            SetError($"The answer could not be read aloud: {exception.Message}");
        }
    }

    private void NotifyIfAway(LinkProject project, string title, string message)
    {
        // Someone looking at Link already sees the result.
        if (IsVisible && IsLinkActive) return;
        NotificationRequested?.Invoke($"{title} · {project.Name}", message, project.Id);
    }

    // A one-glance preview for a notification or the history: plain words without Markdown markers or code fences.
    private static string ToPreviewText(string markdown) =>
        string.Join(' ', markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("```", StringComparison.Ordinal) && !IsRuleLine(line))
            .Select(line => line.TrimStart('#', '>', '-', '*', '+', ' ')
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)));

    private void Effort_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EffortButton, Placement = PlacementMode.Bottom };
        foreach (var option in EffortOptions)
        {
            var item = new MenuItem
            {
                Header = option.Description,
                IsCheckable = true,
                IsChecked = option.Effort == _settings.Current.ResponseEffort
            };
            item.Click += (_, _) => SetEffort(option.Effort);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void SetEffort(ResponseEffort effort)
    {
        _settings.Current.ResponseEffort = effort;
        _settings.Save();
        UpdateEffortButton();
        SetStatus($"Response effort: {EffortText.Text} — applies to the next message");
    }

    private void UpdateEffortButton()
    {
        var option = EffortOptions.First(option => option.Effort == _settings.Current.ResponseEffort);
        EffortText.Text = option.Label;
        EffortButton.ToolTip = $"Response effort: {option.Description}";
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
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
        var linked = dialog.ShowDialog() == true;
        // The chat is opened once the dialog is gone: while it is open, Link's other windows take no input.
        if (dialog.OpenChatRequested)
        {
            OpenChat(null);
            return;
        }

        if (dialog.NewChatRequested)
        {
            _ = StartNewChatAsync(dialog.WithSummary);
            return;
        }

        if (!linked || dialog.SelectedConversation is not { } conversation) return;

        _projects.ActiveProject.LinkedConversation = new ChatConversationLink
        {
            TransportId = _chat.TransportId,
            ConversationId = conversation.ConversationId,
            Title = conversation.Title,
            Url = conversation.Url
        };
        _projects.Save();
        UpdateConnectionState();
        ShowProjectResponse(_projects.ActiveProject);
        SetStatus($"Linked to {conversation.Title}");
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
            var summary = await WriteSummaryAsync(project, current);
            if (summary is null) return;
            foreach (var old in _context.Where(item => item.Type == ContextItemType.ChatSummary).ToArray()) _context.Remove(old);
            AddContext(new ContextItem { Type = ContextItemType.ChatSummary, Label = "Summary of the previous chat", Text = summary });
        }

        project.LinkedConversation = next;
        _projects.Save();
        UpdateConnectionState();
        // The previous chat's answer stays on screen only when it is the summary that goes on.
        if (!withSummary) ShowProjectResponse(project);
        SetStatus(withSummary
            ? "Summary ready: your next message starts the new chat and takes it along"
            : "Your next message starts a new chat");
    }

    /// <summary>The current chat's summary of itself, or null when it could not be written (the reason is shown).</summary>
    private async Task<string?> WriteSummaryAsync(LinkProject project, ChatConversationLink conversation)
    {
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        _chatNotice = null;
        _receivingSince = null;
        SetSending(true);
        _chatHeardAt = DateTime.UtcNow;
        ShowResponse(ChatProgressState.Sending, string.Empty);
        SetStatus("Asking the current chat for a summary…");
        try
        {
            // Fast: a summary needs no long thinking, and a long chat writes slowly as it is.
            var request = _requests.BuildAndLog(project, SummaryPrompt, [], null, ResponseEffort.Fast);
            var response = await _chat.SendAsync(request, conversation, new Progress<ChatProgress>(OnChatProgress), cancellation.Token);
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

    /// <summary>The prompt started the project's new chat: the project is linked to that conversation from now on.</summary>
    private void LinkStartedChat(AvailableChatConversation? started)
    {
        var project = _projects.ActiveProject;
        if (started is null || project.LinkedConversation is not { IsNew: true } link) return;
        project.LinkedConversation = new ChatConversationLink
        {
            TransportId = link.TransportId,
            ConversationId = started.ConversationId,
            Title = started.Title,
            Url = started.Url
        };
        _projects.Save();
        UpdateConnectionState();
    }

    /// <summary>ChatGPT names a new chat once it has answered, and a chat can be renamed there: the link follows.</summary>
    private void RefreshLinkedTitle(LinkProject project)
    {
        if (project.LinkedConversation is not { IsNew: false } link) return;
        var seen = _chat.GetAvailableConversations().FirstOrDefault(conversation => conversation.ConversationId == link.ConversationId);
        if (seen is null || seen.Title == link.Title) return;
        project.LinkedConversation = new ChatConversationLink
        {
            TransportId = link.TransportId,
            ConversationId = link.ConversationId,
            Title = seen.Title,
            Url = link.Url
        };
    }

    private void UnlinkConversation()
    {
        if (_sending) return;
        _projects.ActiveProject.LinkedConversation = null;
        _projects.Save();
        UpdateConnectionState();
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
        _projectPanel?.ShowConnection(text, dot, conversation is { IsNew: false });

        if (state == _connectionState) return;
        _connectionState = state;
        // The chat row lives in the panel, which may be closed: trouble with the chat shows in the status line too.
        if (_sending) ReportState();
        else if (state == ChatConnectionState.SignInRequired) SetStatus("Sign in to ChatGPT: panel → Open chat");
        else if (state == ChatConnectionState.BridgeUnavailable) SetStatus(text);
        else ReportState();
    }

    private void SetSending(bool sending)
    {
        _sending = sending;
        UpdateSendButton();
        UpdatePromptReadOnly();
        ProjectMenuButton.IsEnabled = !sending;
        _projectPanel?.SetBusy(sending);
        ContextActionsPanel.IsEnabled = !sending;
        ContextItemsControl.IsEnabled = !sending;
        ScreenButton.IsEnabled = !sending;
        ClipboardButton.IsEnabled = !sending;
        ReportState();
    }

    private void UpdateSendButton()
    {
        // While a request runs or Link talks, the send orb becomes Stop; Esc does the same, silencing Link first.
        var speaking = _voice.Phase == VoicePhase.Speaking;
        var stop = _sending || speaking;
        SendIcon.Data = stop ? StopIconGeometry : (Geometry)FindResource("SendIconGeometry");
        SendIcon.Fill = stop ? SendIcon.Stroke : Brushes.Transparent;
        SendButton.ToolTip = speaking ? "Stop reading aloud (Esc)" : _sending ? "Stop request (Esc)" : "Send to linked chat";
    }

    private void OnChatProgress(ChatProgress progress)
    {
        _chatHeardAt = DateTime.UtcNow;
        if (progress.State == ChatProgressState.ChatStarted)
        {
            LinkStartedChat(progress.StartedConversation);
            return;
        }

        _latency.Heard(progress);
        if (progress.State == ChatProgressState.Receiving && _receivingSince is null)
        {
            _receivingSince = DateTime.UtcNow;
            _receivingFrom = progress.Text.Length;
        }

        if (progress.Diagnostics is { } diagnostics)
        {
            DiagnosticsReported?.Invoke($"Chat {progress.State.ToString().ToLowerInvariant()}: {diagnostics}");
        }

        _voice.ReportProgress(progress);
        if (progress.Notice is not null)
        {
            // Kept for the final status line; the next progress state would hide it right away.
            _chatNotice = progress.Notice;
            SetStatus(progress.Notice);
            return;
        }

        ShowResponse(progress.State, progress.Text.Length > 0 ? progress.Text : progress.Error ?? string.Empty);
        // While the user dictates the next message, the status line stays with the microphone.
        if (_voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        SetStatus(progress.State switch
        {
            ChatProgressState.Sending => "Sending to ChatGPT…",
            ChatProgressState.Waiting => "Waiting for ChatGPT…",
            ChatProgressState.Receiving => "Receiving response…",
            ChatProgressState.Reconnecting => "Reconnecting to ChatGPT…",
            ChatProgressState.Completed => "Response received",
            ChatProgressState.Error => $"Error: {progress.Error}",
            _ => "Working…"
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
            ChatProgressState.Reconnecting => "Reconnecting",
            ChatProgressState.Completed => "Assistant",
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
        // An answer belongs to the conversation it came from: after relinking the project it is not shown.
        if (project.LastResponse is not { } response ||
            response.ConversationId != project.LinkedConversation?.ConversationId)
        {
            _responseText = string.Empty;
            RenderResponse(string.Empty);
            ResponsePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowResponse(ChatProgressState.Completed, response.Text);
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

    private void RenderResponse(string text)
    {
        _nextResponseBlock = 0;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            var start = index;
            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var codeLines = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[index++]);
                }
                if (index < lines.Length) index++;

                AddResponseBlock(lines, start, index, () => new Paragraph(new Run(string.Join('\n', codeLines)))
                {
                    Margin = new Thickness(0, 3, 0, 10),
                    Padding = new Thickness(9, 7, 9, 7),
                    Background = (Brush)FindResource("ResponseCodeBrush"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5,
                    LineHeight = 18
                });
                continue;
            }

            if (TryGetHeading(lines[index], out var headingLevel, out var headingText))
            {
                index++;
                AddResponseBlock(lines, start, index, () =>
                {
                    var heading = new Paragraph
                    {
                        Margin = new Thickness(0, start == 0 ? 0 : 5, 0, 5),
                        FontSize = headingLevel switch { 1 => 17, 2 => 15.5, _ => 14.5 },
                        FontWeight = FontWeights.SemiBold,
                        LineHeight = 22
                    };
                    AddInlineContent(heading.Inlines, headingText);
                    return heading;
                });
                continue;
            }

            if (IsRuleLine(lines[index]))
            {
                index++;
                AddResponseBlock(lines, start, index, () => new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 11),
                    BorderBrush = (Brush)FindResource("HairlineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1,
                    LineHeight = 1
                });
                continue;
            }

            if (IsTableLine(lines[index]))
            {
                while (index < lines.Length && IsTableLine(lines[index])) index++;
                AddResponseBlock(lines, start, index, () => CreateTable(lines[start..index]));
                continue;
            }

            if (IsQuoteLine(lines[index]))
            {
                while (index < lines.Length && IsQuoteLine(lines[index])) index++;
                AddResponseBlock(lines, start, index, () =>
                {
                    var quote = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 9),
                        Padding = new Thickness(9, 1, 0, 1),
                        BorderBrush = (Brush)FindResource("AquaBrush"),
                        BorderThickness = new Thickness(2.5, 0, 0, 0),
                        Foreground = (Brush)FindResource("MutedBrush")
                    };
                    var quoteLines = lines[start..index].Select(line => line.TrimStart()[1..].Trim());
                    AddInlineContent(quote.Inlines, string.Join(' ', quoteLines.Where(line => line.Length > 0)));
                    return quote;
                });
                continue;
            }

            if (TryGetListItem(lines[index], numbered: false, out _) || TryGetListItem(lines[index], numbered: true, out _))
            {
                var numbered = !TryGetListItem(lines[index], numbered: false, out _);
                while (index < lines.Length && TryGetListItem(lines[index], numbered, out _)) index++;
                AddResponseBlock(lines, start, index, () => CreateList(lines[start..index], numbered));
                continue;
            }

            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index])) index++;
            AddResponseBlock(lines, start, index, () =>
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
                AddInlineContent(paragraph.Inlines, string.Join(' ', lines[start..index].Select(line => line.Trim())));
                return paragraph;
            });
        }

        RemoveResponseBlocks(from: _nextResponseBlock);
    }

    /// <summary>
    /// Shows the next block of the answer. A streaming answer only grows at its end, so a block whose source is
    /// unchanged keeps its rendered form and layout; from the first changed block on everything is built anew.
    /// </summary>
    private void AddResponseBlock(string[] lines, int start, int end, Func<Block> create)
    {
        var source = string.Join('\n', lines, start, end - start);
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

    private System.Windows.Documents.List CreateList(IEnumerable<string> lines, bool numbered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 1, 0, 9),
            Padding = new Thickness(0)
        };

        foreach (var line in lines)
        {
            TryGetListItem(line, numbered, out var itemText);
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlineContent(paragraph.Inlines, itemText);
            list.ListItems.Add(new ListItem(paragraph));
        }

        return list;
    }

    private static bool StartsBlock(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        TryGetHeading(line, out _, out _) ||
        IsRuleLine(line) ||
        IsTableLine(line) ||
        IsQuoteLine(line) ||
        TryGetListItem(line, numbered: false, out _) ||
        TryGetListItem(line, numbered: true, out _);

    private static bool IsRuleLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && (trimmed.All(ch => ch == '-') || trimmed.All(ch => ch == '*') || trimmed.All(ch => ch == '_'));
    }

    private static bool IsTableLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 1 && trimmed[0] == '|' && trimmed.IndexOf('|', 1) > 0;
    }

    private static bool IsQuoteLine(string line) => line.TrimStart().StartsWith('>');

    private Paragraph CreateTable(IReadOnlyList<string> lines)
    {
        // Monospace columns keep tables readable without a grid control in the compact overlay.
        var rows = lines
            .Select(SplitTableRow)
            .Where(cells => !cells.All(cell => cell.Length > 0 && cell.All(ch => ch is '-' or ':')))
            .ToList();
        var columns = rows.Max(row => row.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(column => rows.Max(row => column < row.Count ? row[column].Length : 0))
            .ToArray();
        var formatted = rows
            .Select(row => string.Join("  ", Enumerable.Range(0, columns)
                .Select(column => (column < row.Count ? row[column] : string.Empty).PadRight(widths[column]))).TrimEnd())
            .ToList();

        var table = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            Background = (Brush)FindResource("ResponseCodeBrush"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            LineHeight = 18
        };
        table.Inlines.Add(new Bold(new Run(formatted[0])));
        if (formatted.Count > 1) table.Inlines.Add(new Run("\n" + string.Join('\n', formatted.Skip(1))));
        return table;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return System.Text.RegularExpressions.Regex.Split(trimmed, @"(?<!\\)\|")
            .Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal))
            .ToList();
    }

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
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!TryReadInline(text, index, out var inline, out var next))
            {
                index++;
                continue;
            }

            if (index > plainStart) inlines.Add(new Run(text[plainStart..index]));
            inlines.Add(inline);
            index = plainStart = next;
        }

        if (plainStart < text.Length) inlines.Add(new Run(text[plainStart..]));
    }

    private bool TryReadInline(string text, int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Inline? inline, out int next)
    {
        inline = null;
        next = index;
        if (text[index] == '`')
        {
            var end = text.IndexOf('`', index + 1);
            if (end < 0) return false;
            inline = new Run(text[(index + 1)..end])
            {
                Background = (Brush)FindResource("ResponseCodeBrush"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12.5
            };
            next = end + 1;
            return true;
        }

        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*')
        {
            var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
            if (end < 0) return false;
            var bold = new Bold();
            AddInlineContent(bold.Inlines, text[(index + 2)..end]);
            inline = bold;
            next = end + 2;
            return true;
        }

        if (text[index] == '[')
        {
            var labelEnd = text.IndexOf(']', index + 1);
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(') return false;
            var targetEnd = text.IndexOf(')', labelEnd + 2);
            if (targetEnd < 0 ||
                !Uri.TryCreate(text[(labelEnd + 2)..targetEnd], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            var link = new Hyperlink
            {
                NavigateUri = uri,
                Foreground = (Brush)FindResource("LinkTextBrush"),
                ToolTip = uri.AbsoluteUri
            };
            AddInlineContent(link.Inlines, text[(index + 1)..labelEnd]);
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                OpenLink(e.Uri);
            };
            inline = link;
            next = targetEnd + 1;
            return true;
        }

        return false;
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
