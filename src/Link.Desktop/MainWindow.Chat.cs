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
    /// Ask again ▾: the question on screen again, deeper, with a fresh screen of its window or with only its text, or
    /// back in the prompt to be corrected. Only what would change something is offered.
    /// </summary>
    private void AskAgain_OnClick(object sender, RoutedEventArgs e)
    {
        if (_attemptOnScreen is not { } shown) return;
        var (snapshot, route, effort) = shown;
        var menu = new ContextMenu { PlacementTarget = AskAgainButton, Placement = PlacementMode.Bottom };
        if (effort != ResponseEffort.Deep) AddAgain(menu, "Deeper", "The same question, thought over longer", Redirect.Deeper);
        if (snapshot.WorkWindow != IntPtr.Zero)
        {
            AddAgain(menu, $"With a fresh screen of {snapshot.WorkTarget?.ProcessName ?? "its window"}",
                "The same question with a new screenshot of the window it was asked about", Redirect.FreshScreen);
        }

        if (route != RequestRoute.Direct) AddAgain(menu, "Only my text", "The same question with only its text and what you attached", Redirect.OnlyText);
        menu.Items.Add(new Separator());
        var edit = new MenuItem
        {
            Header = "Edit the question",
            ToolTip = "Back into the prompt to correct, e.g. a misheard word; it is asked again with the same screen and context"
        };
        edit.Click += (_, _) => EditQuestion();
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    private void AddAgain(ContextMenu menu, string header, string tooltip, Redirect redirect)
    {
        var item = new MenuItem { Header = header, ToolTip = tooltip };
        item.Click += async (_, _) => await AskAgainAsync(redirect);
        menu.Items.Add(item);
    }

    private enum Redirect
    {
        Deeper,
        FreshScreen,
        OnlyText
    }

    /// <summary>
    /// The snapshot on screen goes again to the same chat, deeper, with only its text or with a fresh screenshot of its
    /// work window. The prompt and the clipboard are not read again.
    /// </summary>
    private async Task AskAgainAsync(Redirect redirect)
    {
        StopHandsFree();
        // While the user dictates, the words wait for the next message.
        if (_sending || _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        if (_attemptOnScreen is not { } shown) return;
        if (_projects.ActiveProject.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var (snapshot, route, effort) = shown;
        var fresh = redirect == Redirect.FreshScreen;
        if (redirect == Redirect.Deeper) effort = ResponseEffort.Deep;
        else if (redirect == Redirect.OnlyText) route = RequestRoute.Direct;
        // "Only my text" sends no screenshot of its own, so a fresh one goes with Link's chips.
        else route = RequestRoute.Chat;

        // After a failure or Stop the prompt still holds this input: once answered, it goes like a sent one.
        var prompt = PromptBox.Text;
        var kept = prompt.Trim() == snapshot.Text &&
                   _context.Select(item => item.Id).SequenceEqual(snapshot.Context.Select(item => item.Id));
        var answered = await AttemptAsync(
            snapshot.Text,
            route,
            effort,
            fresh ? () => WithFreshScreenAsync(snapshot) : () => Task.FromResult(snapshot));
        if (answered && kept) ClearSentInput(prompt);
    }

    private async Task<InputSnapshot> WithFreshScreenAsync(InputSnapshot snapshot) =>
        snapshot with { Id = Guid.NewGuid(), Screen = await CaptureWorkWindowAsync(snapshot.WorkWindow) };

    /// <summary>
    /// Edit the question: it goes back into the prompt to be corrected, e.g. a word the voice misheard. Send then asks
    /// it again from the same snapshot: its screen, context and window go as they were, and nothing is dictated or
    /// captured again.
    /// </summary>
    private void EditQuestion()
    {
        StopHandsFree();
        if (_sending || _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        if (_attemptOnScreen is not { } shown) return;
        _editing = shown;
        SetPromptText(shown.Snapshot.Text);
        PromptBox.Focus();
        SetStatus("Correct the question — Enter asks again with the same screen and context, Esc cancels");
    }

    /// <summary>Esc while correcting: the question leaves the prompt, and the answer's ✎ Edit brings it back.</summary>
    private void CancelEdit()
    {
        _editing = null;
        SetPromptText(string.Empty);
        SetStatus("Edit cancelled");
        ScheduleListening();
    }

    private async Task SendCorrectedAsync((InputSnapshot Snapshot, RequestRoute Route, ResponseEffort Effort) edited)
    {
        if (_projects.ActiveProject.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var (snapshot, route, effort) = edited;
        var prompt = PromptBox.Text;
        var answered = await AttemptAsync(
            prompt,
            route,
            effort,
            () => Task.FromResult(snapshot with { Id = Guid.NewGuid(), Text = prompt.Trim() }));
        // The context stack was not part of this attempt, so it stays for the next message.
        if (answered) ClearSentPrompt(prompt);
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

    /// <summary>Under an attempt's answer, error or Stop: Ask again ▾ and → Agent task, both from its snapshot.</summary>
    private void UpdateRedirectButtons() =>
        RedirectPanel.Visibility = _attemptOnScreen is null ? Visibility.Collapsed : Visibility.Visible;

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

    /// <summary>The menu of a label under the prompt: one line per option, the current one checked.</summary>
    private static void ShowOptions<T>(Button button, (T Value, string Label, string Description)[] options, T current, Action<T> choose)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var option in options)
        {
            var item = new MenuItem { Header = option.Description, IsCheckable = true, IsChecked = Equals(option.Value, current) };
            item.Click += (_, _) => choose(option.Value);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Where Send sends: ChatGPT in the chat named, or the project's agent. What goes along is the chips; other chats
    /// are in the panel's Chats (Ctrl+K).
    /// </summary>
    private void Route_OnClick(object sender, RoutedEventArgs e)
    {
        var open = TaskTakesWords ? _task : null;
        ShowOptions(RouteButton,
        [
            (RequestRoute.Chat, "ChatGPT", $"ChatGPT — the chat {ChatName(_projects.ActiveProject.LinkedConversation)}, with the chips you see"),
            (RequestRoute.Agent, "Agent", open is not null
                ? $"Agent — the task «{Shorten(open.Goal, 40)}» goes on with your words"
                : "Agent — a card for a new task first; nothing runs before Run")
        ], _settings.Current.Route, SetRoute);
    }

    /// <summary>Ctrl+K: the project's chats in the panel, where one opens and goes on, or a new one starts.</summary>
    private void ShowAllChats()
    {
        SetPanelOpen(true);
        ShowPanelTab(PanelTab.Chats);
        _shownChat = null;
        _chatsPanel?.ShowListView();
    }

    private void SetRoute(RequestRoute route)
    {
        _settings.Current.Route = route;
        _settings.Save();
        UpdateRouteButton();
        // What an agent's task does not carry is not shown as going along.
        ShowApplicationContext();
        UpdateSees();
        SetStatus($"Send goes to {DestinationName} now");
    }

    /// <summary>
    /// Where Send goes, said where the user looks: ChatGPT's chat; on the Agent route the open task, as its next words
    /// (steered now while the agent works), or a card for a new task when none is open.
    /// </summary>
    private void UpdateRouteButton()
    {
        var agent = _settings.Current.Route == RequestRoute.Agent;
        var open = agent && TaskTakesWords ? _task : null;
        var starting = StartProvider;
        var provider = _runningTask?.Provider ?? open?.LastRun?.Provider ?? starting;
        var chat = _projects.ActiveProject.LinkedConversation;
        RouteText.Text = !agent ? $"ChatGPT · {Shorten(ChatName(chat), 30)}"
            : open is not null ? $"{ShortName(provider)} · {Shorten(open.Goal, 30)}"
            : $"New {ShortName(starting)} task";
        RouteButton.ToolTip = (!agent
                ? $"Send asks ChatGPT in {(chat is { IsNew: false } ? "the chat " + chat.Title : "a new chat")}, with the chips you see."
                : open is not null
                    ? $"Send gives your words and chips to the task «{open.Goal}»: {(AgentRunning ? "steer now, the agent stops its step and goes on with them" : "it goes on in the same session")}. Ctrl+Enter opens a card for a new task instead."
                    : $"Send opens a task card for {_agents.Provider(starting).Name}; nothing runs before Run.") +
            "\nClick: ChatGPT or the agent · Ctrl+K: the chats";
        PromptPlaceholder.Text = !agent
            ? chat is { IsNew: false } ? $"Ask in «{Shorten(chat.Title, 40)}»…" : "Ask in a new chat…"
            : open is not null
                ? AgentRunning ? $"Steer {ShortName(provider)} now: it stops the step and goes on with your words…" : $"Your words for {ShortName(provider)}: the task goes on…"
                : $"A new task for {ShortName(starting)}: Enter shows its card first…";
        SendHintText.Text = !agent ? "sends to ChatGPT"
            : open is null ? "opens a task card"
            : AgentRunning ? "steers the agent now"
            : "goes on with the task";
        UpdateSendButton();
    }

    /// <summary>A chat as the user knows it: its title, or what the next message does without one.</summary>
    private static string ChatName(ChatConversationLink? chat) => chat is { IsNew: false } ? chat.Title : "new chat";

    /// <summary>Who the next message goes to, in the words of the status line.</summary>
    private string DestinationName => _settings.Current.Route != RequestRoute.Agent
        ? _projects.ActiveProject.LinkedConversation is { IsNew: false } chat ? $"ChatGPT, the chat {chat.Title}" : "ChatGPT, a new chat"
        : TaskTakesWords && _task is { } open ? $"the task «{Shorten(open.Goal, 40)}»"
        : $"a task card for {_agents.Provider(StartProvider).Name}";

    private void Effort_OnClick(object sender, RoutedEventArgs e) =>
        ShowOptions(EffortButton, EffortOptions, _settings.Current.ResponseEffort, SetEffort);

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
