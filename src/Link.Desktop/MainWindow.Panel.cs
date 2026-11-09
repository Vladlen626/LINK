using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Link.Desktop.Models;

namespace Link.Desktop;

// The project's chats with ChatGPT on the Link window's Chat tab: their list in the sidebar and the shown chat's thread,
// the answer on screen at its end; and the project itself (its note and workspace) in the window's right column.
public partial class MainWindow
{
    // How much of an answer the thread shows; Show whole puts the whole of it under the thread.
    private const int AnswerGlance = 1200;
    // How near its end the thread counts as read to the end: what comes then is followed.
    private const double ThreadEndSlack = 24;
    private readonly ChatsPanel _chatsPanel = new();
    private readonly ProjectPanel _projectPanel = new();
    // A chat of the list shown instead of the one Link talks in; null: the one Link talks in.
    private ChatRow? _shownChat;
    // The thread drawn last: drawn again, it keeps the reader's place.
    private string? _threadKey;
    // An answer Show whole put on screen; null: the answer on screen is the current chat's, coming or its last.
    private ProjectExchange? _shownExchange;
    // The question on its way: it stands at the end of the thread until the chat keeps it with its answer.
    private ThreadUser? _asking;
    // The note's × on its chip: the next question goes without it.
    private bool _noteLeftOut;

    private void NoteChip_OnClick(object sender, RoutedEventArgs e) => ShowProjectSettings();

    /// <summary>The project's settings in the Link window's right column, its note ready for typing.</summary>
    private void ShowProjectSettings()
    {
        ShowLinkWindow();
        ShowProjectColumn();
        _projectPanel.FocusNote();
    }

    private void RemoveNote_OnClick(object sender, RoutedEventArgs e)
    {
        _noteLeftOut = true;
        UpdateNoteChip();
        SetStatus("The next question goes without the project note");
    }

    /// <summary>
    /// The project note as it goes with the next question: first in its context, unless the user left it out of this
    /// question. Null when it does not go.
    /// </summary>
    private ContextItem? ProjectNote(LinkProject project) =>
        project.State is { } note && !_noteLeftOut
            ? new ContextItem
            {
                Type = ContextItemType.ProjectState,
                Label = $"Project note · {note.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}",
                Text = note.Text
            }
            : null;

    /// <summary>The chats' sidebar of the Chat tab and the project's column: wired once, with the window.</summary>
    private void WirePanels()
    {
        _chatsPanel.ChatSelected += OnChatSelected;
        _chatsPanel.NewChatRequested += withSummary => _ = StartNewChatAsync(withSummary);
        _chatsPanel.OpenChatRequested += () => OpenChat(_projects.ActiveProject.LinkedConversation);
        ChatsHost.Child = _chatsPanel;

        _projectPanel.NoteChanged += OnNoteChanged;
        _projectPanel.EditEnded += _projects.Save;
        _projectPanel.WorkspaceChosen += OnWorkspaceChosen;
        _projectPanel.MapRequested += () => _ = WriteProjectMapAsync(_projects.ActiveProject);
        _projectPanel.FileRequested += ShowFile;
        _projectPanel.MemoryRequested += ShowMemory;
        // Saved with the note, when the field is left.
        _projectPanel.CheckCommandChanged += command => _projects.ActiveProject.CheckCommand = command.Length == 0 ? null : command;
        ProjectHost.Child = _projectPanel;
    }

    /// <summary>The active project anew in the window: its column, its tasks, and its chats with the current one shown.</summary>
    private void RefreshProjectPanel()
    {
        RefreshStudioProject();
        _shownChat = null;
        RefreshChats();
        UpdateConnectionState();
    }

    /// <summary>
    /// The project's chats, the one Link talks in first and marked; a new chat waiting for its first message leads.
    /// Answers an earlier Link kept without their chat make one more row at the end. The open chat's thread is drawn.
    /// </summary>
    private void RefreshChats()
    {
        var project = _projects.ActiveProject;
        var history = _projects.ReadHistory(project);
        var current = project.LinkedConversation;
        var rows = new List<ChatRow>();
        if (current is { IsNew: true }) rows.Add(new ChatRow("New chat", "Your next message starts it", string.Empty, null, IsCurrent: true));
        foreach (var chat in project.Chats)
        {
            var last = history.FirstOrDefault(exchange => exchange.ConversationId == chat.ConversationId);
            var title = chat.ConversationId == current?.ConversationId ? current.Title : chat.Title;
            rows.Add(new ChatRow(
                title,
                last?.Question ?? "Nothing asked through Link yet",
                ShortTime(last?.AnsweredAt ?? chat.LastUsedAt),
                chat.ConversationId,
                chat.ConversationId == current?.ConversationId));
        }

        if (history.FirstOrDefault(exchange => exchange.ConversationId is null) is { } earlier)
        {
            rows.Add(new ChatRow("Earlier answers", earlier.Question, ShortTime(earlier.AnsweredAt), null, IsCurrent: false, IsEarlier: true));
        }

        // The shown chat stays open; one that is gone gives way to the chat Link talks in.
        var open = (_shownChat is { } shown ? rows.FirstOrDefault(row => row.ConversationId == shown.ConversationId && row.IsEarlier == shown.IsEarlier) : null)
                   ?? rows.FirstOrDefault(row => row.IsCurrent);
        _shownChat = open is { IsCurrent: false } ? open : null;
        _chatsPanel.ShowChats(rows.Select(row => ReferenceEquals(row, open) ? row with { IsOpen = true } : row).ToList());
        ShowChatThread(open, history);
    }

    /// <summary>
    /// A chat of the list: Link talks in it from now on, as clicking a chat in ChatGPT does, and its thread shows. While a
    /// request runs its thread only shows, as do the answers kept without their chat.
    /// </summary>
    private void OnChatSelected(ChatRow row)
    {
        var project = _projects.ActiveProject;
        if (row.ConversationId is { } id && id != project.LinkedConversation?.ConversationId && !_sending &&
            project.Chats.FirstOrDefault(chat => chat.ConversationId == id) is { } chat)
        {
            SwitchChat(chat.ConversationId, chat.Title, chat.Url);
            if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
            return;
        }

        _shownChat = row.IsCurrent ? null : row;
        RefreshChats();
    }

    /// <summary>
    /// A chat's thread: what was asked through Link and what came back, the newest at the end, then the answer on screen
    /// when it is this chat's. No chat: the project has none to talk in yet.
    /// </summary>
    private void ShowChatThread(ChatRow? row, IReadOnlyList<ProjectExchange> history)
    {
        var atEnd = ThreadAtEnd();
        // The answer on screen stands under its own chat's thread: the current chat's, or the one Show whole was in.
        var answerHere = _shownExchange is { } shown ? row is not null && Holds(row, shown) : row?.IsCurrent != false;
        AnswerHost.Visibility = answerHere ? Visibility.Visible : Visibility.Collapsed;
        var answerShown = answerHere && ResponsePanel.Visibility == Visibility.Visible;
        var exchanges = row is null ? [] : history.Where(exchange => Holds(row, exchange)).Reverse().ToList();
        var items = new List<object>();
        for (var i = 0; i < exchanges.Count; i++)
        {
            var exchange = exchanges[i];
            var time = ShortTime(exchange.AnsweredAt);
            items.Add(new ThreadUser(exchange.Question, time, exchange.Carried));
            // The answer on screen stands whole after its question.
            if (i == exchanges.Count - 1 && answerShown && exchange.Answer == _responseText) continue;
            var answer = ToPreviewText(exchange.Answer);
            items.Add(new ThreadAnswer(answer.Length <= AnswerGlance ? answer : answer[..AnswerGlance] + "…", time, exchange));
        }

        if (_asking is not null && answerHere) items.Add(_asking);
        ThreadTitle.Text = row?.Title ?? "No chat yet";
        ThreadTitle.ToolTip = ThreadTitle.Text;
        ThreadHintText.Text = row is null ? "Link a ChatGPT chat to the project: its menu → Link ChatGPT conversation…"
            : row.IsEarlier ? "Kept before Link knew which chat an answer came from."
            : row.IsCurrent ? "Your next message in Link goes to this chat."
            : "Click it in the list again to talk in it.";
        OpenThreadButton.Visibility = row is { IsCurrent: true } ? Visibility.Visible : Visibility.Collapsed;
        ThreadList.ItemsSource = items;
        ThreadEmptyText.Visibility = items.Count == 0 && !answerShown ? Visibility.Visible : Visibility.Collapsed;

        // A thread opened anew starts at its end, and so does one read to its end; read higher up, it stays put.
        var key = $"{_projects.ActiveProject.Id}/{row?.ConversationId}/{row?.IsEarlier}";
        if (key != _threadKey || atEnd) Dispatcher.BeginInvoke(ThreadScroll.ScrollToEnd, DispatcherPriority.Background);
        _threadKey = key;
    }

    /// <summary>Whether an answer was given in the chat of this row: the earlier answers' row holds those of no chat.</summary>
    private static bool Holds(ChatRow row, ProjectExchange exchange) =>
        row.IsEarlier ? exchange.ConversationId is null : row.ConversationId is not null && exchange.ConversationId == row.ConversationId;

    private bool ThreadAtEnd() => ThreadScroll.VerticalOffset >= ThreadScroll.ScrollableHeight - ThreadEndSlack;

    /// <summary>The question on its way at the end of the thread; null: none, the chat kept it or it did not go.</summary>
    private void ShowAsking(InputSnapshot? snapshot)
    {
        if (snapshot is null && _asking is null) return;
        _asking = null;
        if (snapshot is not null)
        {
            var (question, carried) = Asked(snapshot);
            _asking = new ThreadUser(question, ShortTime(DateTimeOffset.Now), carried);
        }

        RefreshChats();
    }

    private void UserWords_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadUser words }) words.Open = !words.Open;
    }

    private void ShowAnswer_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadAnswer answer }) ShowExchange(answer.Exchange);
    }

    private void OpenThread_OnClick(object sender, RoutedEventArgs e) => OpenChat(_projects.ActiveProject.LinkedConversation);

    /// <summary>An answer from a chat's thread, whole, at the end of that thread; its question goes in the status line.</summary>
    private void ShowExchange(ProjectExchange exchange)
    {
        if (_sending) return;
        // What went with the latest question did not go with this one.
        ShowSaw(null);
        SetPicture(exchange.Picture);
        ShowResponse(ChatProgressState.Completed, exchange.Answer, exchange);
        // The answer may be another chat's than the one Link talks in: its own name goes over it.
        ResponseStateText.Text = _projects.ActiveProject.Chats.FirstOrDefault(chat => chat.ConversationId == exchange.ConversationId) is { } from
            ? $"ChatGPT · {from.Title}"
            : "ChatGPT · an earlier answer";
        SetStatus($"From the chat: {exchange.Question}");
        // An answer is read from its start: its top comes into sight once the thread is laid out and scrolled.
        Dispatcher.BeginInvoke(() => ResponsePanel.BringIntoView(new Rect(0, 0, 1, 1)), DispatcherPriority.ContextIdle);
    }

    /// <summary>Whose answer the panel shows: the chat Link talks in.</summary>
    private string AnswerSource() =>
        _projects.ActiveProject.LinkedConversation is { IsNew: false } chat ? $"ChatGPT · {chat.Title}" : "ChatGPT";

    /// <summary>The note's chip shows its first line, the focus, while the note goes with the next question.</summary>
    private void UpdateNoteChip()
    {
        var note = _projects.ActiveProject.State?.Text ?? string.Empty;
        NoteChipRow.Visibility = ProjectNote(_projects.ActiveProject) is null ? Visibility.Collapsed : Visibility.Visible;
        NoteChipText.Text = note.Split('\n')[0].Trim();
        NoteChip.ToolTip = $"The project note goes with your questions and tasks:\n{note}\n\nClick to change it";
    }

    // Typing goes into the project at once, so a request sent meanwhile takes it; it is saved when the field is left.
    private void OnNoteChanged(string text)
    {
        var note = text.Length == 0 ? null : new ProjectState { Text = text };
        _projects.ActiveProject.State = note;
        _projectPanel.ShowNoteDate(note);
        // A changed note goes again, even if the last one was left out.
        _noteLeftOut = false;
        UpdateNoteChip();
    }

    private void OnWorkspaceChosen(string? folder)
    {
        _projects.ActiveProject.WorkspaceRoot = folder;
        _projects.Save();
        _projectPanel.ShowProject(_projects.ActiveProject);
        UpdateProjectTabs();
        SetStatus(folder is null ? "Workspace folder forgotten" : $"Workspace: {folder}");
    }

    /// <summary>
    /// Keeps a question and its answer in the project's history, with the chat it was asked in, and puts that chat at
    /// the top of the project's chats; the answer is on screen either way.
    /// </summary>
    private void RememberExchange(LinkProject project, InputSnapshot snapshot, string answer, bool picture)
    {
        var (question, carried) = Asked(snapshot);
        var chat = project.LinkedConversation is { IsNew: false } link ? link : null;
        try
        {
            _projects.AddToHistory(project, new ProjectExchange
            {
                Question = question,
                Answer = answer,
                Picture = picture,
                ConversationId = chat?.ConversationId,
                Carried = carried
            });
            if (chat is not null)
            {
                project.RememberChat(chat);
                _projects.Save();
            }

            if (project == _projects.ActiveProject) RefreshChats();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsReported?.Invoke($"Project history not saved: {exception.Message}");
        }
    }

    /// <summary>A question as its chat keeps it: its words and what went with them, or what it carried when it had none.</summary>
    private static (string Question, string Carried) Asked(InputSnapshot snapshot)
    {
        // A request of context alone is remembered by what it carried.
        var carried = (snapshot.Screen is { } screen ? snapshot.Context.Append(screen) : snapshot.Context)
            .Where(item => item.Type != ContextItemType.ProjectState)
            .Select(item => item.Label)
            .ToArray();
        return snapshot.Text.Length > 0 ? (snapshot.Text, string.Join(" · ", carried)) : (string.Join(", ", carried), string.Empty);
    }

    /// <summary>When something in the chats happened: the time today, the day before that.</summary>
    private static string ShortTime(DateTimeOffset at)
    {
        var when = at.ToLocalTime();
        return when.Date == DateTime.Today
            ? when.ToString("HH:mm", CultureInfo.InvariantCulture)
            : when.ToString("d MMM", CultureInfo.InvariantCulture);
    }
}
