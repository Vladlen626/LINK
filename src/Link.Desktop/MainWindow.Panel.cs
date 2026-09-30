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

    private bool IsLinkActive => IsActive || _panel?.IsActive == true;

    private void Expand_OnClick(object sender, RoutedEventArgs e) => SetPanelOpen(!_settings.Current.SidePanel.IsOpen);

    private void TaskChip_OnClick(object sender, RoutedEventArgs e)
    {
        SetPanelOpen(true);
        _projectPanel?.FocusTask();
    }

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
        ExpandButton.ToolTip = open ? "Close the panel" : "Open the project panel: task, state, chat, workspace, history";
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

        _projectPanel = new ProjectPanel();
        _projectPanel.TaskChanged += OnTaskChanged;
        _projectPanel.StateChanged += OnStateChanged;
        _projectPanel.EditEnded += _projects.Save;
        _projectPanel.ConversationRequested += LinkConversation;
        _projectPanel.NewChatRequested += withSummary => _ = StartNewChatAsync(withSummary);
        _projectPanel.OpenChatRequested += () => OpenChat(_projects.ActiveProject.LinkedConversation);
        _projectPanel.WorkspaceChosen += OnWorkspaceChosen;
        _projectPanel.MapRequested += () => _ = WriteProjectMapAsync();
        _projectPanel.HistorySelected += OnHistorySelected;
        return panel;
    }

    /// <summary>Shows the active project in the panel, when there is one.</summary>
    private void RefreshProjectPanel()
    {
        if (_panel is null || _projectPanel is null) return;
        var project = _projects.ActiveProject;
        // The agent's card or Activity keeps the panel until the user goes back to the project.
        if (!_panelShowsAgent) _panel.SetContent(project.Name, _projectPanel);
        _projectPanel.ShowProject(project);
        _projectPanel.ShowHistory(_projects.ReadHistory(project).Select(ToHistoryRow));
        _projectPanel.SetBusy(_sending);
        UpdateConnectionState();
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

    private void UpdateTaskChip()
    {
        var task = _projects.ActiveProject.CurrentTask;
        // Direct sends no task.
        TaskChip.Visibility = task.Length == 0 || _settings.Current.Route == RequestRoute.Direct
            ? Visibility.Collapsed
            : Visibility.Visible;
        TaskChipText.Text = task;
        TaskChip.ToolTip = $"Current task, it goes with your messages: {task}\nClick to change it";
    }

    // Typing goes into the project at once, so a request sent meanwhile takes it; it is saved when the field is left.
    private void OnTaskChanged(string task)
    {
        _projects.ActiveProject.CurrentTask = task;
        UpdateTaskChip();
    }

    private void OnStateChanged(string text)
    {
        var state = text.Length == 0 ? null : new ProjectState { Text = text };
        _projects.ActiveProject.State = state;
        _projectPanel?.ShowStateDate(state);
        ShowProjectStateContext();
    }

    private void OnWorkspaceChosen(string? folder)
    {
        _projects.ActiveProject.WorkspaceRoot = folder;
        _projects.Save();
        _projectPanel?.ShowProject(_projects.ActiveProject);
        SetStatus(folder is null ? "Workspace folder forgotten" : $"Workspace: {folder}");
    }

    private void OnHistorySelected(ProjectExchange exchange)
    {
        if (_sending) return;
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
            var history = _projects.AddToHistory(project, new ProjectExchange { Question = question, Answer = answer });
            _projectPanel?.ShowHistory(history.Select(ToHistoryRow));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsReported?.Invoke($"Project history not saved: {exception.Message}");
        }
    }

    private static HistoryRow ToHistoryRow(ProjectExchange exchange)
    {
        var when = exchange.AnsweredAt.ToLocalTime();
        var time = when.Date == DateTime.Today
            ? when.ToString("HH:mm", CultureInfo.InvariantCulture)
            : when.ToString("d MMM", CultureInfo.InvariantCulture);
        return new HistoryRow(exchange.Question, ToPreviewText(exchange.Answer), time, exchange);
    }
}
