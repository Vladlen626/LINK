using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    // The next question goes with only the user's text and attachments.
    private bool _onlyMyText;
    // What came while Link was hidden, an answer or a failed request; it lasts until Link is shown.
    private LinkState? _unseen;
    private IntPtr _windowHandle;
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
        SoundCues cues)
    {
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
        _voice.PhraseEnded += OnPhraseEnded;
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
        LoadProject(_projects.ActiveProject);
        _loadingProject = false;
        UpdateRouteButton();
        UpdateEffortButton();
        UpdateExpandButton();

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;
        LocationChanged += (_, _) => PlacePanel();
        SizeChanged += (_, _) => PlacePanel();
        DpiChanged += (_, _) => PlacePanel();
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

    /// <summary>A Windows notification is due: title, message and the project it opens.</summary>
    public event Action<string, string, Guid>? NotificationRequested;

    /// <summary>What Link is up to, or its status line, changed: the tray shows both while Link is hidden.</summary>
    public event Action<LinkState, string>? LinkStateChanged;

    public bool AllowClose { get; set; }

    public void ApplySettings()
    {
        Opacity = 1;
        Topmost = _settings.Current.AlwaysOnTop;
        ApplyGlass(this, ShellBorder);
        if (_panel is not null)
        {
            _panel.Topmost = Topmost;
            ApplyGlass(_panel, _panel.ShellBorder);
        }

        UpdateSees();
        // Voice replies may have been switched off or on.
        UpdateReadAloudButton();
        HideButton.ToolTip = $"Hide Link · {_settings.Current.Hotkeys.ShowHide} brings it back";
        // The default agent or the access the card starts with may have changed.
        UpdateRouteButton();
        ApplyConversation();
        // The agents' models and levels, Auto or the context budget may have changed: an open card and the
        // Studio's header and inspector follow, without losing what the user typed or picked on the card.
        _agentPanel?.RefreshSetup();
        RefreshStudio();
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            HideOverlay();
        }
        else if (OverExclusiveFullscreen())
        {
            NotificationRequested?.Invoke(
                "Link stays hidden over this game",
                $"It would throw the game out of exclusive fullscreen. Hold {_settings.Current.Hotkeys.AskAboutScreen} to ask about the screen by voice, or switch the game to borderless.",
                _projects.ActiveProject.Id);
        }
        else
        {
            ShowAndActivate();
        }
    }

    public void ShowNonBlockingWarning(string message) => SetStatus(message);

    /// <summary>The card's glass, for the card and its panel alike.</summary>
    private void ApplyGlass(Window window, Border shell)
    {
        // With Windows transparency effects off the glass turns solid, like the rest of the system.
        var transparent = ThemeService.IsTransparencyEnabled;
        var blurRequested = transparent && _settings.Current.BlurBackground;
        var blurred = WindowStyleService.EnableGlass(window, blurRequested);
        if (FindResource("PanelBrush") is not Brush source) return;

        // Glass setting 0.2..1 maps to a 25..95% tint; unblurred glass needs more tint to keep text readable.
        var tint = source.Clone();
        var opacity = 0.25 + (_settings.Current.Opacity - 0.2) * 0.875;
        tint.Opacity = !transparent ? 1 : blurRequested && !blurred ? Math.Max(opacity, 0.85) : opacity;
        shell.Background = tint;
    }

    /// <summary>Brings Link up on a project's answer, e.g. when its notification is clicked.</summary>
    public void ShowProject(Guid projectId)
    {
        if (!_sending && _projects.Projects.FirstOrDefault(project => project.Id == projectId) is { } project)
        {
            if (project.Id != _projects.ActiveProject.Id) SwitchProject(project);
            else if (ResponsePanel.Visibility != Visibility.Visible) ShowProjectResponse(project);
        }

        ShowAndActivate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowProcedure);
        ApplySettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
        UpdateConnectionState();
        _connectionTimer.Start();
        // Conversation mode left on listens again from the start.
        ApplyConversation();
        // A task left waiting for a usage limit's reset goes on waiting; its file is read once Link has nothing else to do.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RearmContinue));
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // Shown, Link tells its news itself.
            _unseen = null;
            ReportState();
            RefreshWorkPreview();
        }

        FollowCardVisibility();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Coming back from an app the user worked in makes it the target; watching the linked chat does not.
        _currentApplication.LatchRecent(_chat.IsConversationWindow);
        RefreshWorkPreview();
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
        KeepRunsCutShort();
        StopAllRuns();
        _attempts.Forget();
        ClearContext(prune: true);
    }

    public void ShowAndActivate()
    {
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        PromptBox.Focus();
    }

    /// <summary>Hides Link completely: work already started goes on, and the show/hide hotkey brings it back.</summary>
    private void HideOverlay()
    {
        SaveWindowState();
        Hide();
    }

    /// <summary>
    /// Shows Link without taking the focus from the app in front. Over a game in exclusive fullscreen Link stays hidden:
    /// a window would throw the game out of full screen, so the answer comes by voice and notification.
    /// </summary>
    private void ShowPassive()
    {
        if (IsVisible || OverExclusiveFullscreen()) return;
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

    /// <summary>"Only my text": the next question goes without Link's chips, its frame and its hints; the toggle shows it.</summary>
    private void SetOnlyMyText(bool on)
    {
        _onlyMyText = on;
        OnlyMyTextDot.Fill = (Brush)FindResource(on ? "BusyOrbBrush" : "IdleOrbBrush");
        OnlyMyTextText.Foreground = (Brush)FindResource(on ? "TextBrush" : "MutedBrush");
        OnlyMyTextButton.ToolTip = on
            ? "On for the next question: only your text and what you attached go. Click to send Link's chips too"
            : "Send the next question with only your text and what you attached, without Link's chips";
        UpdateNoteChip();
        ShowApplicationContext();
        UpdateSees();
    }

    private void OnlyMyText_OnClick(object sender, RoutedEventArgs e)
    {
        SetOnlyMyText(!_onlyMyText);
        SetStatus(_onlyMyText ? "The next question goes with only your text and attachments" : "Link's chips go with the next question");
    }

    private void RemoveMissingContextFiles()
    {
        foreach (var item in _context.Where(item => item.FilePath is not null && !File.Exists(item.FilePath)).ToArray())
        {
            ReleaseContext(item);
            _context.Remove(item);
        }
    }

    private void TitleBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindParent<ButtonBase>(source) is not null || FindParent<ComboBox>(source) is not null || FindParent<TextBox>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            HideOverlay();
            e.Handled = true;
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;

        e.Handled = true;
        var handle = _windowHandle != IntPtr.Zero ? _windowHandle : new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(handle, NativeMethods.WmNcLButtonDown, new IntPtr(NativeMethods.HtCaption), IntPtr.Zero);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void LoadProject(LinkProject project)
    {
        // A correction belongs to the other project's snapshot.
        _editing = null;
        ProjectNameText.Text = project.Name;
        // The Agent route names the project's own agent.
        UpdateRouteButton();
        _noteLeftOut = false;
        UpdateNoteChip();
        ShowApplicationContext();
        UpdateConnectionState();
        ShowProjectResponse(project);
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

    private void ProjectMenu_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = MenuPlacement.Under(ProjectMenuButton);
        foreach (var project in _projects.Projects)
        {
            var item = new MenuItem
            {
                Header = project.Name,
                IsCheckable = true,
                IsChecked = project.Id == _projects.ActiveProject.Id,
                StaysOpenOnClick = false
            };
            item.Click += (_, _) => SwitchProject(project);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "Create project…" };
        create.Click += (_, _) => CreateProject();
        menu.Items.Add(create);
        var rename = new MenuItem { Header = "Rename current…" };
        rename.Click += (_, _) => RenameCurrentProject();
        menu.Items.Add(rename);
        var delete = new MenuItem { Header = "Remove current…", IsEnabled = _projects.Projects.Count > 1 };
        delete.Click += (_, _) => RemoveCurrentProject();
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        var linkConversation = new MenuItem { Header = "Link ChatGPT conversation…" };
        linkConversation.Click += (_, _) => LinkConversation();
        menu.Items.Add(linkConversation);
        var unlinkConversation = new MenuItem
        {
            Header = "Unlink conversation",
            IsEnabled = _projects.ActiveProject.LinkedConversation is not null
        };
        unlinkConversation.Click += (_, _) => UnlinkConversation();
        menu.Items.Add(unlinkConversation);
        menu.IsOpen = true;
    }

    private void CreateProject()
    {
        var dialog = new CreateProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var project = _projects.Create(dialog.ProjectName);
        LoadProject(project);
        // A new project starts with its note and workspace.
        SetPanelOpen(true);
        ShowPanelTab(PanelTab.Project);
        _projectPanel?.FocusNote();
    }

    /// <summary>A project for a folder, named by it and working there; it becomes the current one. Returns its name.</summary>
    public string AddProjectForFolder(string folder)
    {
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
        ProjectNameText.Text = _projects.ActiveProject.Name;
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

        var next = _projects.Remove(project);
        _attempts.Forget(project.Id);
        LoadProject(next);
        SetStatus("Project removed; local files were kept");
    }

    private void Hide_OnClick(object sender, RoutedEventArgs e) => HideOverlay();
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
        // Nothing typed any more: the conversation listens again.
        if (_typing && !PromptBox.IsReadOnly)
        {
            _typing = false;
            ScheduleListening();
        }
    }

    private async void PromptBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        YieldToTyping(e);
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+K, as in the chat apps: the chats, to open another or start a new one.
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ShowAllChats();
            return;
        }

        if (e.Key != Key.Escape) return;
        // Like the Stop orb: silence Link first, then stop the request; with nothing going on Esc hides Link, which
        // goes on listening in conversation.
        if (_voice.Phase == VoicePhase.Speaking) _voice.StopSpeaking();
        else if (_sending) CancelSend();
        else if (_editing is not null) CancelEdit();
        else if (_voice.Phase == VoicePhase.Idle || _voice.IsHandsFree) HideOverlay();
        else return;
        e.Handled = true;
    }

}
