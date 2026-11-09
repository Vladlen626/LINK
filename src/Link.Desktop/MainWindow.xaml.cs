using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ProjectService _projects;
    private readonly ScreenCaptureService _capture;
    private readonly CurrentApplicationService _currentApplication;
    private readonly ScreenshotHistoryService _history;
    private readonly ClipboardContextService _clipboard;
    private readonly VoiceSession _voice;
    private readonly SoundCues _cues;
    private readonly ObservableCollection<ContextItem> _context = [];
    // What came while Link was hidden, an answer or a failed request; it lasts until Link is shown.
    private LinkState? _unseen;
    private bool _loadingProject = true;

    public MainWindow(
        SettingsService settings,
        ProjectService projects,
        AttemptCoordinator attempts,
        IChatIntegration chat,
        ScreenCaptureService capture,
        CurrentApplicationService currentApplication,
        ScreenshotHistoryService history,
        ClipboardContextService clipboard,
        VoiceSession voice,
        AgentCoordinator agents,
        AgentLimits limits,
        UsageReport usage,
        UsageHistory spending,
        SoundCues cues,
        string cardDraftFile)
    {
        _cardDraftFile = cardDraftFile;
        _usage = usage;
        _spending = spending;
        _settings = settings;
        _projects = projects;
        _attempts = attempts;
        _chat = chat;
        _capture = capture;
        _currentApplication = currentApplication;
        _history = history;
        _clipboard = clipboard;
        _voice = voice;
        _cues = cues;
        _agents = agents;
        _limits = limits;

        InitializeComponent();
        _voice.PhaseChanged += OnVoicePhaseChanged;
        _voice.DraftChanged += OnVoiceDraftChanged;
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) =>
        {
            UpdateConnectionState();
            CheckQuietChat();
        };
        // Screenshots show in the Sees line; the chips are everything else that goes along.
        ContextItemsControl.ItemsSource = new ListCollectionView(_context)
        {
            Filter = item => ((ContextItem)item).Type != ContextItemType.Screenshot
        };
        _context.CollectionChanged += Context_OnCollectionChanged;
        _currentApplication.WorkTargetChanged += OnWorkTargetChanged;
        SetUpWindow();
        LoadProject(_projects.ActiveProject);
        _loadingProject = false;
        UpdateRouteButton();

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
        IsVisibleChanged += OnVisibleChanged;
        Loaded += OnLoaded;
        Activated += OnActivated;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _currentApplication.WorkTargetChanged -= OnWorkTargetChanged;
            _connectionTimer.Stop();
            _sendCancellation?.Cancel();
            _sendCancellation?.Dispose();
        };
    }

    public event Action? SettingsRequested;

    /// <summary>A Windows notification is due: title, message, the project it opens and the agent's task, if it is of one.</summary>
    public event Action<string, string, Guid, Guid?>? NotificationRequested;

    /// <summary>What Link is up to, or its status line, changed: the tray shows both while Link is hidden.</summary>
    public event Action<LinkState, string>? LinkStateChanged;

    public bool AllowClose { get; set; }

    public void ApplySettings()
    {
        UpdateSees();
        // Voice replies may have been switched off or on.
        UpdateReadAloudButton();
        // The default agent or the access the card starts with may have changed.
        UpdateRouteButton();
        // The agents' models and levels, Auto or the context budget may have changed: an open card and the
        // Studio's header and inspector follow, without losing what the user typed or picked on the card.
        _agentPanel?.RefreshSetup();
        RefreshStudio();
    }

    /// <summary>The show/hide hotkey: Link in front hides; hidden, minimized or behind other windows, it comes forward on Chat.</summary>
    public void ToggleVisible()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized)
        {
            HideLinkWindow();
        }
        else if (OverExclusiveFullscreen())
        {
            NotificationRequested?.Invoke(
                "Link stays hidden over this game",
                "It would throw the game out of exclusive fullscreen. Switch the game to borderless to use Link over it.",
                _projects.ActiveProject.Id,
                null);
        }
        else
        {
            ShowAndActivate();
        }
    }

    public void ShowNonBlockingWarning(string message) => SetStatus(message);

    /// <summary>
    /// Brings Link up on a project's answer when its notification is clicked, or on the Studio tab with the agent's task
    /// the notification was about.
    /// </summary>
    public void ShowProject(Guid projectId, Guid? taskId = null)
    {
        var work = taskId is { } id ? WorkOf(id) : null;
        if (!_sending && _projects.Projects.FirstOrDefault(project => project.Id == projectId) is { } project)
        {
            if (project.Id != _projects.ActiveProject.Id) SwitchProject(project);
            else if (work is null && ResponsePanel.Visibility != Visibility.Visible) ShowProjectResponse(project);
        }

        if (work is null)
        {
            ShowAndActivate();
            return;
        }

        ShowLinkWindow(StudioTab.Studio);
        OpenAgentTask(work.Record);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.PaintFrame(this);
        ApplySettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateConnectionState();
        _connectionTimer.Start();
        // A task left waiting for a usage limit's reset goes on waiting; its file is read once Link has nothing else to do.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RearmContinue));
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        // Shown, Link tells its news itself.
        _unseen = null;
        ReportState();
        RefreshWorkPreview();
        // The Harness and the Spend are not built while Link is hidden.
        RefreshStudio();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Coming back from an app the user worked in makes it the target; watching the linked chat does not.
        _currentApplication.LatchRecent(_chat.IsConversationWindow);
        RefreshWorkPreview();
        // Back from editing a rules, role or skill file: the inspector reads it again.
        RefreshStudio();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _sendCancellation?.Cancel();
        SaveCardDraft();
        KeepRunsCutShort();
        StopAllRuns();
        _attempts.Forget();
        ClearContext(prune: true);
    }

    /// <summary>The Link window in front on its Chat tab, the cursor in the input.</summary>
    public void ShowAndActivate()
    {
        ShowLinkWindow(StudioTab.Chat);
        PromptBox.Focus();
    }

    /// <summary>Hides Link completely: work already started goes on, and the show/hide hotkey brings it back.</summary>
    private void HideLinkWindow()
    {
        SaveWindowState();
        Hide();
    }

    /// <summary>
    /// Shows Link on its Chat tab without taking the focus from the app in front. Over a game in exclusive fullscreen Link
    /// stays hidden: a window would throw the game out of full screen, so the answer comes by voice and notification.
    /// </summary>
    private void ShowPassive()
    {
        if (IsVisible || OverExclusiveFullscreen()) return;
        ShowTab(StudioTab.Chat);
        ShowActivated = false;
        Show();
        ShowActivated = true;
    }

    private LinkState State =>
        _sending || AnyRunning ? LinkState.Working
        : _connectionState == ChatConnectionState.SignInRequired ? LinkState.NeedsDecision
        : _unseen ?? LinkState.Idle;

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        ReportState();
    }

    private void SetError(string message) => SetStatus($"Error: {message}");

    private void ReportState() => LinkStateChanged?.Invoke(State, StatusText.Text);

    private void Context_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ContextItemsControl.Visibility = _context.All(item => item.Type == ContextItemType.Screenshot)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateSees();
    }

    private void AddContext(ContextItem item)
    {
        if (item.FilePath is not null) _history.Pin(item.FilePath);
        _context.Add(item);
    }

    private void ReleaseContext(ContextItem item)
    {
        if (item.FilePath is not null) _history.Unpin(item.FilePath);
    }

    private void ClearContext(bool prune)
    {
        foreach (var item in _context) ReleaseContext(item);
        _context.Clear();
        _shotPicture = null;
        if (prune) _history.Prune();
    }

    private void RemoveMissingContextFiles()
    {
        foreach (var item in _context.Where(item => item.FilePath is not null && !File.Exists(item.FilePath)).ToArray())
        {
            ReleaseContext(item);
            _context.Remove(item);
        }
    }

    private void LoadProject(LinkProject project)
    {
        UpdateProjectTabs();
        // The Agent route names the project's own agent.
        UpdateRouteButton();
        _noteLeftOut = false;
        UpdateNoteChip();
        ShowApplicationContext();
        UpdateConnectionState();
        // A request still under way keeps its answer on screen; it is of the project it was sent in.
        if (!_sending) ShowProjectResponse(project);
        RefreshProjectPanel();
    }

    private void SwitchProject(LinkProject project)
    {
        if (_loadingProject || project.Id == _projects.ActiveProject.Id) return;
        _projects.SetActive(project);
        LoadProject(project);
        _projects.Save();
        RearmContinue();
    }

    private void CreateProject()
    {
        var dialog = new CreateProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        // A card is made for its project: it goes with the project Link leaves.
        if (_cardRun is not null) CancelCard();
        var project = _projects.Create(dialog.ProjectName);
        LoadProject(project);
        // A new project starts with its note and workspace.
        ShowProjectSettings();
    }

    /// <summary>A project for a folder, named by it and working there; it becomes the current one. Returns its name.</summary>
    public string AddProjectForFolder(string folder)
    {
        if (_cardRun is not null) CancelCard();
        var project = _projects.Create(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        project.WorkspaceRoot = folder;
        _projects.Save();
        LoadProject(project);
        return project.Name;
    }

    private void RenameCurrentProject()
    {
        var dialog = new CreateProjectWindow(_projects.ActiveProject.Name, isRename: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _projects.Rename(_projects.ActiveProject, dialog.ProjectName);
        UpdateProjectTabs();
        RefreshProjectPanel();
    }

    private void RemoveCurrentProject()
    {
        if (_projects.Projects.Count <= 1) return;
        var project = _projects.ActiveProject;
        var result = MessageBox.Show(
            $"Remove ‘{project.Name}’ from Link? Its local data folder will be kept.",
            "Remove project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (_cardRun is not null) CancelCard();
        var next = _projects.Remove(project);
        _attempts.Forget(project.Id);
        LoadProject(next);
        SetStatus("Project removed; local files were kept");
    }

    private void Settings_OnClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Clipboard_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        try
        {
            var item = _clipboard.Read();
            RemoveMissingContextFiles();
            foreach (var previous in _context.Where(context => context.Type == ContextItemType.Clipboard).ToArray())
            {
                ReleaseContext(previous);
                _context.Remove(previous);
            }
            AddContext(item);
            SetStatus("Clipboard attached");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void RemoveContext_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Guid id }) return;
        var item = _context.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return;

        ReleaseContext(item);
        _context.Remove(item);
        _history.Prune();
    }

    private void PromptBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(PromptBox.Text);
        PromptPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // A cleared prompt no longer holds the dictated words, so its answer is not read aloud.
        if (!empty) return;
        _voice.ForgetVoicePrompt();
    }

    private async void PromptBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter keeps the TextBox newline. IME composition arrives as ImeProcessed.
        if (e.Key != Key.Return || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        // Ctrl+Enter: a new task for the agent, through its card.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_sending && !e.IsRepeat) await SendToAgentAsync(newTask: true);
            return;
        }

        // The send button may mean Stop right now; Enter only ever sends.
        if (!_sending && !e.IsRepeat) await SendOrQueueAsync();
    }

    /// <summary>
    /// The window's keys, wherever the focus is: Ctrl+K the chats, as in the chat apps; Ctrl+1 and Ctrl+2 the tabs; Ctrl+B
    /// the sidebar; Ctrl+Shift+H the Harness; Ctrl+Shift+U the Spend; Ctrl+N a new task.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var keys = Keyboard.Modifiers;
        if (keys == ModifierKeys.Control && e.Key == Key.K) ShowAllChats();
        else if (keys == ModifierKeys.Control && e.Key is Key.D1 or Key.NumPad1) ShowTab(StudioTab.Chat);
        else if (keys == ModifierKeys.Control && e.Key is Key.D2 or Key.NumPad2) ShowTab(StudioTab.Studio);
        else if (keys == ModifierKeys.Control && e.Key == Key.B) ShowSidebar(!Place.SidebarOpen);
        else if (keys == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.H) ToggleSide(StudioSide.Harness);
        else if (keys == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.U) ToggleSide(StudioSide.Spend);
        else if (keys == ModifierKeys.Control && e.Key == Key.N)
        {
            ShowTab(StudioTab.Studio);
            _agentPanel?.RequestNewTask();
        }
        else return;
        e.Handled = true;
    }

    /// <summary>
    /// Esc, once what has the focus let it pass (a menu, a box of its own): like the Stop orb, it silences Link first;
    /// on the Chat tab it then stops the request, and with nothing going on hides Link.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_voice.Phase == VoicePhase.Speaking) _voice.StopSpeaking();
        else if (Viewer.Visibility == Visibility.Visible) CloseViewer();
        else if (Place.Tab != StudioTab.Chat) return;
        else if (_sending) CancelSend();
        else if (_voice.Phase == VoicePhase.Idle) HideLinkWindow();
        else return;
        e.Handled = true;
    }
}
