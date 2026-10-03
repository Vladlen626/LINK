using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;

namespace Link.Desktop;

// The panel beside the card: Expand opens it, it follows the card and hides and shows with it. It holds the
// project's task, state, chat, workspace and history; the card keeps chips of what goes with a request.
public partial class MainWindow
{
    private SidePanelWindow? _panel;
    private ProjectPanel? _projectPanel;
    private PanelTab _panelTab = PanelTab.Project;
    // The note's × in the card: the next question goes without it.
    private bool _noteLeftOut;

    private bool IsLinkActive => IsActive || _panel?.IsActive == true;

    private void Expand_OnClick(object sender, RoutedEventArgs e) => SetPanelOpen(!_settings.Current.SidePanel.IsOpen);

    private void NoteChip_OnClick(object sender, RoutedEventArgs e)
    {
        SetPanelOpen(true);
        ShowProjectTab();
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
        ExpandButton.ToolTip = open ? "Close the panel" : "Open the panel: the project (note, chat, workspace, history) and the agent's task";
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

    private void ShowProjectTab() => ShowPanelTab(PanelTab.Project);

    /// <summary>One of the panel's two tabs: the project, or the agent's task (its card, the task, or how one starts).</summary>
    private void ShowPanelTab(PanelTab tab)
    {
        _panelTab = tab;
        if (_panel is null || _projectPanel is null) return;
        FrameworkElement content = _projectPanel;
        if (tab == PanelTab.Task)
        {
            _agentPanel ??= CreateAgentPanel();
            if (_task is null && !AgentRunning && _cardRun is null) _agentPanel.ShowNoTask();
            content = _agentPanel;
        }

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
        panel.TabChosen += tab =>
        {
            if (tab == PanelTab.Task) ShowAgentPanel();
            else ShowPanelTab(PanelTab.Project);
        };

        _projectPanel = new ProjectPanel();
        _projectPanel.NoteChanged += OnNoteChanged;
        _projectPanel.EditEnded += _projects.Save;
        _projectPanel.ConversationRequested += LinkConversation;
        _projectPanel.NewChatRequested += withSummary => _ = StartNewChatAsync(withSummary);
        _projectPanel.OpenChatRequested += () => OpenChat(_projects.ActiveProject.LinkedConversation);
        _projectPanel.WorkspaceChosen += OnWorkspaceChosen;
        _projectPanel.MapRequested += () => _ = WriteProjectMapAsync();
        _projectPanel.SkillRequested += ShowFile;
        // Saved with the task and the state, when the field is left.
        _projectPanel.CheckCommandChanged += command => _projects.ActiveProject.CheckCommand = command.Length == 0 ? null : command;
        _projectPanel.HistorySelected += OnHistorySelected;
        return panel;
    }

    /// <summary>Shows the active project in the panel, when there is one.</summary>
    private void RefreshProjectPanel()
    {
        if (_panel is null || _projectPanel is null) return;
        _projectPanel.ShowProject(_projects.ActiveProject);
        ShowProjectHistory();
        _projectPanel.SetBusy(_sending);
        ShowPanelTab(_panelTab);
        UpdateConnectionState();
    }

    /// <summary>One history of the project: the chat's answers and the agent's tasks, newest first.</summary>
    private void ShowProjectHistory()
    {
        if (_projectPanel is null) return;
        var project = _projects.ActiveProject;
        var chat = (Geometry)FindResource("ChatIconGeometry");
        var agent = (Geometry)FindResource("AgentIconGeometry");
        var answers = _projects.ReadHistory(project).Select(exchange => new HistoryRow(
            exchange.Question, ToPreviewText(exchange.Answer), ShortTime(exchange.AnsweredAt), "Show this answer in Link", chat,
            exchange.AnsweredAt, Exchange: exchange));
        var tasks = _projects.ReadAgentTasks(project).Select(task => new HistoryRow(
            task.Goal,
            $"{StatusWords(task.Status)} · {(task.LastRun is { } run ? AgentName(run.Provider) + " · " : string.Empty)}{ToPreviewText(task.LastWords)}",
            ShortTime(task.UpdatedAt),
            "Open the task: its history and result; a reply takes up its last session",
            agent,
            task.UpdatedAt,
            Task: task));
        _projectPanel.ShowHistory(answers.Concat(tasks).OrderByDescending(row => row.At));
    }

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

    private void OnHistorySelected(HistoryRow row)
    {
        if (row.Task is { } task)
        {
            OpenAgentTask(task);
            return;
        }

        if (_sending || row.Exchange is not { } exchange) return;
        ShowResponse(ChatProgressState.Completed, exchange.Answer);
        SetStatus($"From the history: {exchange.Question}");
    }

    /// <summary>Keeps a question and its answer in the project's history; the answer is on screen either way.</summary>
    private void RememberExchange(LinkProject project, InputSnapshot snapshot, string answer)
    {
        // A request of context alone is remembered by what it carried.
        var carried = snapshot.Screen is { } screen ? snapshot.Context.Append(screen) : snapshot.Context;
        var question = snapshot.Text is { Length: > 0 } text
            ? text
            : string.Join(", ", carried.Where(item => item.Type != ContextItemType.ProjectState).Select(item => item.Label));
        try
        {
            _projects.AddToHistory(project, new ProjectExchange { Question = question, Answer = answer });
            if (project == _projects.ActiveProject) ShowProjectHistory();
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
