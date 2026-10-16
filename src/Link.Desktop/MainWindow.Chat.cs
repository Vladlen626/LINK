using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Chat: Send takes the prompt, the chips and the screen as one snapshot to ChatGPT, one attempt at a time.
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
    private readonly AttemptCoordinator _attempts;
    private readonly DispatcherTimer _connectionTimer;
    // The answer on screen: each block's Markdown source with its document block, reused while the answer streams.
    private readonly List<(string Source, Block Block)> _responseBlocks = [];
    private MarkdownRenderer? _markdown;
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
    // The attempt whose answer, error or Stop is on screen: the redirect buttons send its snapshot again.
    private (InputSnapshot Snapshot, RequestRoute Route, ResponseEffort Effort)? _attemptOnScreen;
    // Its question back in the prompt to be corrected: Send asks it again from the same snapshot.
    private (InputSnapshot Snapshot, RequestRoute Route, ResponseEffort Effort)? _editing;

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
        // Hands-free, Send ends the phrase now instead of at its pause; the prompt goes out with or without one.
        if (_voice.IsHandsFree)
        {
            await FinishPhraseAsync(send: true);
            return;
        }

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
        if (_editing is { } edited && !string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            await SendCorrectedAsync(edited);
            return;
        }

        if (_settings.Current.Route == RequestRoute.Agent)
        {
            await SendToAgentAsync(newTask: false);
            return;
        }

        RemoveMissingContextFiles();
        // What Link adds by itself (the project state, the app) is not a question.
        if (string.IsNullOrWhiteSpace(PromptBox.Text) &&
            _context.All(item => item.Type is ContextItemType.ProjectState or ContextItemType.Application))
        {
            SetError("Add text or context before sending.");
            return;
        }

        var project = _projects.ActiveProject;
        if (project.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var prompt = PromptBox.Text;
        var route = _onlyMyText ? RequestRoute.Direct : RequestRoute.Chat;
        var answered = await AttemptAsync(
            prompt,
            route,
            _settings.Current.ResponseEffort,
            () => TakeSnapshotAsync(project, prompt, route, AutoFrameOn));
        if (answered) ClearSentInput(prompt);
    }

    /// <summary>
    /// The held "ask about the screen" hotkey let go: the words go by Chat at once, with a fresh frame of the work window
    /// whatever Auto says, and the answer is read aloud like any dictated one.
    /// </summary>
    private async Task AskAboutScreenAsync()
    {
        if (_sending) return;
        var project = _projects.ActiveProject;
        if (project.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var prompt = PromptBox.Text;
        var answered = await AttemptAsync(
            prompt,
            RequestRoute.Chat,
            _settings.Current.ResponseEffort,
            () => TakeSnapshotAsync(project, prompt, RequestRoute.Chat, freshFrame: true));
        if (answered) ClearSentInput(prompt);
    }

    /// <summary>
    /// The answered input leaves the prompt and the context stack. Link's own chips come back, and "only my text" was
    /// for that question only.
    /// </summary>
    private void ClearSentInput(string prompt)
    {
        ClearSentPrompt(prompt);
        ClearContext(prune: true);
        _noteLeftOut = false;
        SetOnlyMyText(false);
    }

    private void ClearSentPrompt(string prompt)
    {
        // Words dictated after interrupting the spoken answer are the next message: only the sent prompt goes.
        var sent = prompt.TrimEnd();
        PromptBox.Text = PromptBox.Text.StartsWith(sent, StringComparison.Ordinal)
            ? PromptBox.Text[sent.Length..].Trim()
            : string.Empty;
    }

    /// <summary>
    /// The input as it is now: the prompt, the context stack, the work target while its chip stands, and with
    /// <paramref name="freshFrame"/> a screenshot of the work window. Direct sends no screenshot of its own.
    /// </summary>
    private async Task<InputSnapshot> TakeSnapshotAsync(LinkProject project, string prompt, RequestRoute route, bool freshFrame)
    {
        // The app Link was invoked from, not whatever took the focus since (the browser, Link's own captures).
        var workTarget = _currentApplication.WorkTarget;
        var workWindow = _currentApplication.IsExternalWindow(workTarget) ? workTarget : IntPtr.Zero;
        var named = _context.Any(item => item.Type == ContextItemType.Application);
        IEnumerable<ContextItem> context = _context.Where(item => item.Type != ContextItemType.Application);
        if (ProjectNote(project) is { } note) context = context.Prepend(note);
        var snapshot = new InputSnapshot
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Text = prompt.Trim(),
            // The app's chip only says that its name and title go; they travel as the work target.
            Context = [.. context],
            WorkTarget = named ? _currentApplication.GetContext(workWindow) : null,
            WorkWindow = workWindow
        };
        if (!freshFrame || route == RequestRoute.Direct || workWindow == IntPtr.Zero)
        {
            return snapshot;
        }

        try
        {
            return snapshot with { Screen = await CaptureWorkWindowAsync(workWindow) };
        }
        catch (Exception exception)
        {
            SetStatus($"Auto screen unavailable: {exception.Message}");
            return snapshot;
        }
    }

    /// <summary>
    /// One attempt: the snapshot is taken once it is under way (a screenshot is Link's own work, and Stop can come
    /// meanwhile), the coordinator sends it, and the answer is drawn, read aloud and announced here. A failure or
    /// Stop is shown too; the input stays where it is. Whatever the end, the redirect buttons can send the snapshot
    /// again. Returns whether the answer came.
    /// </summary>
    private async Task<bool> AttemptAsync(
        string prompt,
        RequestRoute route,
        ResponseEffort effort,
        Func<Task<InputSnapshot>> takeSnapshot)
    {
        var project = _projects.ActiveProject;
        // A fresh screenshot that cannot be taken leaves the buttons with the attempt they were for.
        var previous = _attemptOnScreen;
        InputSnapshot? snapshot = null;
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        _chatNotice = null;
        _receivingSince = null;
        var latency = new RequestLatency();
        // A correction or typed words end with any new attempt.
        _editing = null;
        _typing = false;
        // The microphone waits while the chat answers and Link speaks.
        StopHandsFree();
        _voice.BeginRequest(prompt);
        SetSending(true);
        _chatHeardAt = DateTime.UtcNow;
        ShowResponse(ChatProgressState.Sending, string.Empty);
        ShowSaw(null);

        try
        {
            snapshot = await takeSnapshot();
            ShowSaw(RequestService.Build(snapshot, route, effort, _settings.Current.ConversationMode));
            _attempts.Keep(snapshot);
            _cues.Play(SoundCue.Sent);
            var response = await _attempts.SendAsync(
                snapshot,
                route,
                effort,
                _settings.Current.ConversationMode,
                latency,
                OnChatProgress,
                cancellation.Token);
            ShowResponse(ChatProgressState.Completed, response.Text);
            RememberExchange(project, snapshot, response.Text);
            if (!IsVisible) _unseen = LinkState.Done;
            SetStatus(SlowChatHint(response.Text) ?? ResponseReceivedStatus());
            NotifyIfAway(project, "Answer ready", ToPreviewText(response.Text));
            // An answer read aloud while it arrived is its own signal: the cue keeps quiet then.
            _cues.Play(SoundCue.Done);
            _ = ReadAnswerAloudAsync(response.Text);
            if (_voice.Phase == VoicePhase.Speaking) SetStatus(SpeakingStatus);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowResponse(ChatProgressState.Cancelled, "Request cancelled.");
            SetStatus("Request cancelled. Your prompt and context were kept.");
            return false;
        }
        catch (Exception exception)
        {
            ShowResponse(ChatProgressState.Error, exception.Message);
            // The prompt waits to be sent again: the user decides what to do with it.
            if (!IsVisible) _unseen = LinkState.NeedsDecision;
            SetError($"{exception.Message} Prompt and context were kept.");
            _cues.Play(SoundCue.Error);
            NotifyIfAway(project, "Request failed", exception.Message);
            return false;
        }
        finally
        {
            // After a failure or Stop; an answer being read aloud keeps its phase.
            _voice.EndRequest();
            _sendCancellation = null;
            cancellation.Dispose();
            SetSending(false);
            UpdateConnectionState();
            _attemptOnScreen = snapshot is null ? previous : (snapshot, route, effort);
            UpdateRedirectButtons();
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
        // Someone looking at Link, its panel or the Studio already sees the result.
        if (IsLinkActive) return;
        NotificationRequested?.Invoke($"{title} · {project.Name}", message, project.Id);
    }

    // A one-glance preview for a notification or the history: plain words without Markdown markers or code fences.
    private static string ToPreviewText(string markdown) =>
        string.Join(' ', markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("```", StringComparison.Ordinal) && !MarkdownRenderer.IsRuleLine(line))
            .Select(line => line.TrimStart('#', '>', '-', '*', '+', ' ')
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)));

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void SetSending(bool sending)
    {
        _sending = sending;
        UpdateSendButton();
        UpdatePromptReadOnly();
        ProjectMenuButton.IsEnabled = !sending;
        _chatsPanel?.SetBusy(sending);
        ContextActionsPanel.IsEnabled = !sending;
        ContextItemsControl.IsEnabled = !sending;
        SeesRow.IsEnabled = !sending;
        ClipboardButton.IsEnabled = !sending;
        ReportState();
        if (sending) return;
        // After the request's own ending (the sent prompt leaves the prompt), before the microphone opens again.
        Dispatcher.InvokeAsync(SendWaitingPhrase);
        ScheduleListening();
    }

    private void UpdateSendButton()
    {
        // While a request runs or Link talks, the send orb becomes Stop; Esc does the same, silencing Link first.
        var speaking = _voice.Phase == VoicePhase.Speaking;
        var stop = _sending || speaking;
        SendIcon.Data = stop ? StopIconGeometry : (Geometry)FindResource("SendIconGeometry");
        SendIcon.Fill = stop ? SendIcon.Stroke : Brushes.Transparent;
        SendButton.ToolTip = speaking ? "Stop reading aloud (Esc)"
            : _sending ? "Stop request (Esc)"
            : _settings.Current.Route == RequestRoute.Agent ? $"Open the task card for {_agents.Provider(StartProvider).Name} (Enter)"
            : "Send to ChatGPT (Enter)";
    }

    private void OnChatProgress(ChatProgress progress)
    {
        _chatHeardAt = DateTime.UtcNow;
        // The coordinator has linked the project to the chat its prompt started.
        if (progress.State == ChatProgressState.ChatStarted)
        {
            UpdateConnectionState();
            return;
        }

        if (progress.State == ChatProgressState.Receiving && _receivingSince is null)
        {
            _receivingSince = DateTime.UtcNow;
            _receivingFrom = progress.Text.Length;
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
}
