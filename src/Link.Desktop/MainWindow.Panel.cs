using System.Globalization;
using System.Windows;
using Link.Desktop.Models;

namespace Link.Desktop;

// The project's chats with ChatGPT and the project itself (its note and workspace), shown in the Link window: the chats
// on its Chat tab, the project in its right column. Expand on the card opens the window. The card keeps chips of what
// goes with a request.
public partial class MainWindow
{
    // How much of an answer the chat's thread shows; Show in Link has the whole of it.
    private const int AnswerGlance = 1200;
    private ChatsPanel? _chatsPanel;
    private ProjectPanel? _projectPanel;
    // The chat whose thread the Chat tab shows; null: its list.
    private ChatRow? _shownChat;
    // The note's × in the card: the next question goes without it.
    private bool _noteLeftOut;

    private bool IsLinkActive => IsActive || _studio?.IsActive == true;

    private void Expand_OnClick(object sender, RoutedEventArgs e) => ShowLinkWindow();

    private void NoteChip_OnClick(object sender, RoutedEventArgs e) => ShowProjectSettings();

    /// <summary>The card's project in the Link window's column, its note ready for typing.</summary>
    private void ShowProjectSettings()
    {
        ShowLinkWindow();
        // As if chosen in the window's header: a new-task card goes with the project the window leaves.
        if (_cardRun is not null && _projects.ActiveProject.Id != StudioProject.Id) CancelCard();
        ShowStudioProject(_projects.ActiveProject);
        _studio?.ShowProject();
        _projectPanel?.FocusNote();
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

    /// <summary>The chats' and the project's panels, made the first time the Link window wants them; true when just made.</summary>
    private bool EnsureChatAndProject()
    {
        if (_chatsPanel is not null && _projectPanel is not null) return false;
        _chatsPanel = new ChatsPanel();
        _chatsPanel.ChatSelected += OnChatSelected;
        _chatsPanel.NewChatRequested += withSummary => _ = StartNewChatAsync(withSummary);
        _chatsPanel.OpenChatRequested += () => OpenChat(_projects.ActiveProject.LinkedConversation);
        _chatsPanel.AnswerShown += ShowExchange;

        _projectPanel = new ProjectPanel();
        _projectPanel.NoteChanged += OnNoteChanged;
        _projectPanel.EditEnded += _projects.Save;
        _projectPanel.WorkspaceChosen += OnWorkspaceChosen;
        _projectPanel.MapRequested += () => _ = WriteProjectMapAsync(StudioProject);
        _projectPanel.SkillRequested += ShowFile;
        // Saved with the note, when the field is left.
        _projectPanel.CheckCommandChanged += command => StudioProject.CheckCommand = command.Length == 0 ? null : command;
        return true;
    }

    /// <summary>Shows the projects in the Link window, when its panels are made: the Studio's and the card's chats.</summary>
    private void RefreshProjectPanel()
    {
        RefreshStudioProject();
        if (_chatsPanel is null) return;
        _shownChat = null;
        _chatsPanel.ShowListView();
        RefreshChats();
        _chatsPanel.SetBusy(_sending);
        UpdateConnectionState();
    }

    /// <summary>
    /// The project's chats, the one Link talks in first and marked; a new chat waiting for its first message leads.
    /// Answers an earlier Link kept without their chat make one more row at the end.
    /// </summary>
    private void RefreshChats()
    {
        if (_chatsPanel is null) return;
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

        _chatsPanel.ShowChats(rows);
        if (_shownChat is { } shown) ShowChatThread(rows.FirstOrDefault(row => row.ConversationId == shown.ConversationId && row.IsEarlier == shown.IsEarlier) ?? shown);
    }

    /// <summary>
    /// A chat of the list: Link talks in it from now on, as clicking a chat in ChatGPT does, and its thread shows. Not
    /// while a request runs; the answers kept without their chat only show.
    /// </summary>
    private void OnChatSelected(ChatRow row)
    {
        var project = _projects.ActiveProject;
        if (row.ConversationId is { } id && id != project.LinkedConversation?.ConversationId && !_sending &&
            project.Chats.FirstOrDefault(chat => chat.ConversationId == id) is { } chat)
        {
            SwitchChat(chat.ConversationId, chat.Title, chat.Url);
            if (_settings.Current.Route == RequestRoute.Agent) SetRoute(RequestRoute.Chat);
            _shownChat = row with { IsCurrent = true };
            RefreshChats();
            return;
        }

        ShowChatThread(row);
    }

    /// <summary>A chat's thread: what was asked through Link and what came back, the newest at the end.</summary>
    private void ShowChatThread(ChatRow row)
    {
        if (_chatsPanel is null) return;
        _shownChat = row;
        var exchanges = _projects.ReadHistory(_projects.ActiveProject)
            .Where(exchange => row.IsEarlier ? exchange.ConversationId is null : row.ConversationId is not null && exchange.ConversationId == row.ConversationId)
            .Reverse();
        var items = new List<object>();
        foreach (var exchange in exchanges)
        {
            var time = ShortTime(exchange.AnsweredAt);
            items.Add(new ThreadUser(exchange.Question, time, exchange.Carried));
            var answer = ToPreviewText(exchange.Answer);
            items.Add(new ThreadAnswer(answer.Length <= AnswerGlance ? answer : answer[..AnswerGlance] + "…", time, exchange));
        }

        var hint = row.IsEarlier ? "Kept before Link knew which chat an answer came from."
            : row.IsCurrent ? "Your next message in Link goes to this chat."
            : "Open it from the list to talk in it.";
        _chatsPanel.ShowThread(row.Title, items, hint, canOpen: row.IsCurrent);
    }

    /// <summary>An answer from a chat's thread, whole, in the card; its question goes in the status line.</summary>
    private void ShowExchange(ProjectExchange exchange)
    {
        if (_sending) return;
        SetPicture(exchange.Picture);
        ShowResponse(ChatProgressState.Completed, exchange.Answer);
        // The answer may be another chat's than the one Link talks in: its own name goes over it.
        ResponseStateText.Text = _projects.ActiveProject.Chats.FirstOrDefault(chat => chat.ConversationId == exchange.ConversationId) is { } from
            ? $"ChatGPT · {from.Title}"
            : "ChatGPT · an earlier answer";
        ShowAndActivate();
        SetStatus($"From the chat: {exchange.Question}");
    }

    /// <summary>Whose answer the card shows: the chat Link talks in.</summary>
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
        StudioProject.State = note;
        _projectPanel?.ShowNoteDate(note);
        // A changed note goes again, even if the last one was left out.
        _noteLeftOut = false;
        UpdateNoteChip();
    }

    private void OnWorkspaceChosen(string? folder)
    {
        StudioProject.WorkspaceRoot = folder;
        _projects.Save();
        _projectPanel?.ShowProject(StudioProject);
        SetStatus(folder is null ? "Workspace folder forgotten" : $"Workspace: {folder}");
    }

    /// <summary>
    /// Keeps a question and its answer in the project's history, with the chat it was asked in, and puts that chat at
    /// the top of the project's chats; the answer is on screen either way.
    /// </summary>
    private void RememberExchange(LinkProject project, InputSnapshot snapshot, string answer, bool picture)
    {
        // A request of context alone is remembered by what it carried.
        var carried = (snapshot.Screen is { } screen ? snapshot.Context.Append(screen) : snapshot.Context)
            .Where(item => item.Type != ContextItemType.ProjectState)
            .Select(item => item.Label)
            .ToArray();
        var question = snapshot.Text is { Length: > 0 } text ? text : string.Join(", ", carried);
        var chat = project.LinkedConversation is { IsNew: false } link ? link : null;
        try
        {
            _projects.AddToHistory(project, new ProjectExchange
            {
                Question = question,
                Answer = answer,
                Picture = picture,
                ConversationId = chat?.ConversationId,
                Carried = snapshot.Text.Length > 0 ? string.Join(" · ", carried) : string.Empty
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

    /// <summary>When something in the chats happened: the time today, the day before that.</summary>
    private static string ShortTime(DateTimeOffset at)
    {
        var when = at.ToLocalTime();
        return when.Date == DateTime.Today
            ? when.ToString("HH:mm", CultureInfo.InvariantCulture)
            : when.ToString("d MMM", CultureInfo.InvariantCulture);
    }
}
