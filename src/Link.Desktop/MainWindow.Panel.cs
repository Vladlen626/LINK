using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;

namespace Link.Desktop;

// The panel beside the card: Expand opens it, it follows the card and hides and shows with it. Three tabs, as in the
// apps the user knows: the project's chats with ChatGPT, the agents' tasks, and the project itself (its note and
// workspace). The card keeps chips of what goes with a request.
public partial class MainWindow
{
    // How much of an answer the chat's thread shows; Show in Link has the whole of it.
    private const int AnswerGlance = 1200;
    private SidePanelWindow? _panel;
    private ChatsPanel? _chatsPanel;
    private ProjectPanel? _projectPanel;
    private PanelTab _panelTab = PanelTab.Chats;
    // The chat whose thread the Chats tab shows; null: its list.
    private ChatRow? _shownChat;
    // The note's × in the card: the next question goes without it.
    private bool _noteLeftOut;

    private bool IsLinkActive => IsActive || _panel?.IsActive == true;

    private void Expand_OnClick(object sender, RoutedEventArgs e) => SetPanelOpen(!_settings.Current.SidePanel.IsOpen);

    private void NoteChip_OnClick(object sender, RoutedEventArgs e)
    {
        SetPanelOpen(true);
        ShowPanelTab(PanelTab.Project);
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
    /// question or sends only their own text. Null when it does not go.
    /// </summary>
    private ContextItem? ProjectNote(LinkProject project) =>
        project.State is { } note && !_noteLeftOut && !_onlyMyText
            ? new ContextItem
            {
                Type = ContextItemType.ProjectState,
                Label = $"Project note · {note.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}",
                Text = note.Text
            }
            : null;

    private void SetPanelOpen(bool open)
    {
        _settings.Current.SidePanel.IsOpen = open;
        _settings.Save();
        UpdateExpandButton();
        if (open) ShowPanel();
        else _panel?.Hide();
    }

    private void UpdateExpandButton()
    {
        var open = _settings.Current.SidePanel.IsOpen;
        ExpandIcon.Stroke = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        ExpandButton.ToolTip = open ? "Close the panel" : "Open the panel: the project's chats, the agents' tasks and the project";
    }

    private void ShowPanel()
    {
        if (!IsVisible || !_settings.Current.SidePanel.IsOpen) return;
        if (_panel is null)
        {
            _panel = CreatePanel();
            RefreshProjectPanel();
        }

        _panel.PlaceBeside(this, _settings.Current.SidePanel.Width);
        _panel.Show();
    }

    /// <summary>One of the panel's tabs: the chats, the tasks (the list, a card or a task), or the project.</summary>
    private void ShowPanelTab(PanelTab tab)
    {
        _panelTab = tab;
        if (_panel is null || _projectPanel is null || _chatsPanel is null) return;
        FrameworkElement content = tab switch
        {
            PanelTab.Chats => _chatsPanel,
            PanelTab.Tasks => _agentPanel ??= CreateAgentPanel(),
            _ => _projectPanel
        };
        _panel.ShowTab(tab, _projects.ActiveProject.Name, content);
        UpdateAgentPill();
    }

    private SidePanelWindow CreatePanel()
    {
        var panel = new SidePanelWindow { Owner = this, Topmost = Topmost };
        // Placed and glazed before it is first shown, so it never flashes elsewhere.
        new WindowInteropHelper(panel).EnsureHandle();
        ApplyGlass(panel, panel.ShellBorder);
        // Exiting Link closes the panel too; it stays open for the next start.
        panel.CloseRequested += () =>
        {
            if (!AllowClose) SetPanelOpen(false);
        };
        panel.WidthChosen += width =>
        {
            _settings.Current.SidePanel.Width = width;
            _settings.Save();
            PlacePanel();
        };
        panel.DpiChanged += (_, _) => PlacePanel();
        panel.TabChosen += ShowPanelTab;

        _chatsPanel = new ChatsPanel();
        _chatsPanel.ChatSelected += OnChatSelected;
        _chatsPanel.NewChatRequested += withSummary => _ = StartNewChatAsync(withSummary);
        _chatsPanel.OpenChatRequested += () => OpenChat(_projects.ActiveProject.LinkedConversation);
        _chatsPanel.AnswerShown += ShowExchange;

        _projectPanel = new ProjectPanel();
        _projectPanel.NoteChanged += OnNoteChanged;
        _projectPanel.EditEnded += _projects.Save;
        _projectPanel.WorkspaceChosen += OnWorkspaceChosen;
        _projectPanel.MapRequested += () => _ = WriteProjectMapAsync();
        _projectPanel.SkillRequested += ShowFile;
        // Saved with the note, when the field is left.
        _projectPanel.CheckCommandChanged += command => _projects.ActiveProject.CheckCommand = command.Length == 0 ? null : command;
        return panel;
    }

    /// <summary>Shows the active project in the panel, when there is one.</summary>
    private void RefreshProjectPanel()
    {
        if (_panel is null || _projectPanel is null || _chatsPanel is null) return;
        _projectPanel.ShowProject(_projects.ActiveProject);
        _shownChat = null;
        _chatsPanel.ShowListView();
        RefreshChats();
        _chatsPanel.SetBusy(_sending);
        // Another project's tasks: the list, unless its own task is open.
        if (_agentPanel is not null && _shownTask is not null && _taskProject?.Id != _projects.ActiveProject.Id) ShowTaskList();
        else RefreshTaskList();
        ShowPanelTab(_panelTab);
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

    private void PlacePanel()
    {
        if (_panel is { IsVisible: true }) _panel.PlaceBeside(this, _settings.Current.SidePanel.Width);
    }

    private void FollowCardVisibility()
    {
        if (!IsVisible) _panel?.Hide();
        // Link is drawn first and its panel a moment later, so showing Link is not held up by it.
        else Dispatcher.BeginInvoke(ShowPanel, DispatcherPriority.Background);
    }

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
        _projectPanel?.ShowNoteDate(note);
        // A changed note goes again, even if the last one was left out.
        _noteLeftOut = false;
        UpdateNoteChip();
    }

    private void OnWorkspaceChosen(string? folder)
    {
        _projects.ActiveProject.WorkspaceRoot = folder;
        _projects.Save();
        _projectPanel?.ShowProject(_projects.ActiveProject);
        SetStatus(folder is null ? "Workspace folder forgotten" : $"Workspace: {folder}");
    }

    /// <summary>
    /// Keeps a question and its answer in the project's history, with the chat it was asked in, and puts that chat at
    /// the top of the project's chats; the answer is on screen either way.
    /// </summary>
    private void RememberExchange(LinkProject project, InputSnapshot snapshot, string answer)
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

    /// <summary>When something in the panel happened: the time today, the day before that.</summary>
    private static string ShortTime(DateTimeOffset at)
    {
        var when = at.ToLocalTime();
        return when.Date == DateTime.Today
            ? when.ToString("HH:mm", CultureInfo.InvariantCulture)
            : when.ToString("d MMM", CultureInfo.InvariantCulture);
    }
}
