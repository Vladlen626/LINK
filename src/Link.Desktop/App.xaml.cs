using System.Windows;
using System.Windows.Threading;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class App : System.Windows.Application
{
    private const long MaxLogBytes = 1024 * 1024;
    private readonly Queue<DateTimeOffset> _recentErrors = new();
    private readonly Lock _logLock = new();
    private string? _logPath;
    private Mutex? _singleInstance;
    // A second start asks the running Link to show itself through this event.
    private const string ShowRequestName = @"Local\LinkDesktopCompanion.Show";
    private EventWaitHandle? _showRequests;
    private RegisteredWaitHandle? _showWait;
    private SettingsService? _settings;
    private ProjectService? _projects;
    private CurrentApplicationService? _currentApplication;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private AudioRecorderService? _audio;
    private SherpaStreamingSpeechToText? _streamingSpeech;
    private SpeechModelManager? _speechModels;
    private ApiKeyStore? _geminiKey;
    private AgentCoordinator? _agents;
    private RemoteControl? _remote;
    private TextToSpeechService? _textToSpeech;
    private EmbeddedChatIntegration? _chat;
    private ChatPageWindow? _chatPage;
    private MainWindow? _mainWindow;
    private bool _started;
    private bool _disposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = new Mutex(true, @"Local\LinkDesktopCompanion", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Started again, from Start or the taskbar: the running Link comes forward, and may take the foreground.
            if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var running))
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.AllowAnyProcess);
                running.Set();
                running.Dispose();
            }
            else
            {
                MessageBox.Show("Link is already running. Use the tray icon or global show/hide hotkey.", "Link");
            }

            Shutdown();
            return;
        }

        _showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);

        var paths = new StoragePaths();
        _logPath = Path.Combine(paths.Root, "link.log");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };
        _settings = new SettingsService(paths);
        ThemeService.Apply(this, _settings.Current);
        _projects = new ProjectService(paths, _settings);
        var history = new ScreenshotHistoryService(paths, _settings);
        var capture = new ScreenCaptureService(history, Log);
        _currentApplication = new CurrentApplicationService();
        var clipboard = new ClipboardContextService(history);
        var chat = new EmbeddedChatIntegration(paths.CompanionStateFile);
        _chat = chat;
        var attempts = new AttemptCoordinator(chat, _projects, new RequestService(paths, _settings.Current), history, Log);
        _audio = new AudioRecorderService(paths);
        var speechModels = new SpeechModelManager(paths, Log);
        _speechModels = speechModels;
        _streamingSpeech = new SherpaStreamingSpeechToText(speechModels);
        _geminiKey = new ApiKeyStore(paths.GeminiKeyFile, "Link Gemini TTS");
        // One player for the voice and the sound cues; the voice owns it.
        var speaker = new AudioPlayer();
        _textToSpeech = new TextToSpeechService(
            new GeminiTtsProvider(_geminiKey),
            new LocalTtsProvider(speechModels),
            speaker,
            Log);
        var voice = new VoiceSession(
            _settings.Current,
            _audio,
            _streamingSpeech,
            new SpeechToTextService(speechModels),
            _textToSpeech,
            TimeProvider.System,
            Log);
        var cues = new SoundCues(speaker, _settings.Current, () => voice.Phase == VoicePhase.Speaking);
        // Link's own tool packs reach the packs folder where it lacks them, off the startup path.
        _ = Task.Run(() => ToolPacks.Seed(ToolPacks.BuiltInFolder, ToolPacks.Folder));
        // The agents' CLIs start only for a task or a check in Settings, never with Link.
        var settings = _settings;
        var console = new AgentConsole();
        _agents = new AgentCoordinator(
            [
                new ClaudeCodeProvider(() => settings.Current.Agent, Log, console),
                new CodexProvider(() => settings.Current.Agent, Log, console)
            ],
            new AgentChanges(),
            Log,
            console);

        _mainWindow = new MainWindow(
            _settings,
            _projects,
            attempts,
            chat,
            capture,
            _currentApplication,
            history,
            clipboard,
            voice,
            _agents,
            new AgentLimits(paths.AgentLimitsFile),
            new UsageReport(Log),
            cues);
        MainWindow = _mainWindow;
        _mainWindow.SettingsRequested += ShowSettings;
        _mainWindow.NotificationRequested += ShowNotification;
        _mainWindow.LinkStateChanged += (state, status) => _tray?.ShowState(state, status);
        _mainWindow.DiagnosticsReported += Log;
        _textToSpeech.Notice += message => Dispatcher.BeginInvoke(() => _mainWindow?.ShowVoiceNotice(message));
        _mainWindow.Show();
        // Remote control listens only while Settings turn it on, on this computer only.
        _remote = new RemoteControl(_settings, _mainWindow, new RemotePush(new ApiKeyStore(paths.RemotePushKeyFile, "Link remote push"), Log),
            new ApiKeyStore(paths.TelegramTokenFile, "Link Telegram bot"), action => Dispatcher.BeginInvoke(action), UserIdle.Time, Log);
        _remote.Apply();
        // The ChatGPT page loads once the overlay is up, so Link starts as fast as without it.
        Dispatcher.InvokeAsync(() => StartChatPage(chat, paths), DispatcherPriority.ApplicationIdle);

        _hotkeys = new GlobalHotkeyService(_mainWindow, Dispatcher);
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Held += action =>
        {
            _currentApplication.LatchForeground();
            if (action == HotkeyAction.AskAboutScreen) _mainWindow.StartAskingAboutScreen();
            else _mainWindow.StartPushToTalk();
        };
        _hotkeys.Released += async _ => await _mainWindow.StopPushToTalkAsync();
        TryStartHotkeys();

        _tray = new TrayIconService(
            () => Dispatcher.Invoke(_mainWindow.ToggleVisible),
            () => Dispatcher.Invoke(_mainWindow.ShowAgentPanel),
            () => Dispatcher.Invoke(ShowSettings),
            () => Dispatcher.Invoke(ExitApplication));
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequests, (_, _) => Dispatcher.Invoke(_mainWindow.ShowAndActivate), null, Timeout.Infinite, false);
        _started = true;

        if (_settings.FirstStart)
        {
            // Saved now, the settings file is there at the next start: the welcome comes once.
            _settings.Save();
            Dispatcher.InvokeAsync(ShowFirstRun, DispatcherPriority.ApplicationIdle);
        }
    }

    private void ShowFirstRun()
    {
        if (_settings is null || _mainWindow is null || _agents is null) return;
        var window = new FirstRunWindow(_agents.Providers, _settings.Current.Agent, ShowSettings, _mainWindow.AddProjectForFolder);
        if (_mainWindow.IsVisible) window.Owner = _mainWindow;
        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_mainWindow is not null) _mainWindow.AllowClose = true;
        SaveState();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServices();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        if (!_started)
        {
            // Without its window and tray a half-started Link would only hold the single-instance lock.
            e.Handled = true;
            MessageBox.Show($"Link could not start: {e.Exception.Message}\n\nDetails: {_logPath}", "Link",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A tray companion that silently vanishes is worse than one that reports the failure and keeps going.
        // An error that repeats in a burst (e.g. during layout) would loop forever, so that one still ends the app.
        var now = DateTimeOffset.Now;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Peek() < now.AddSeconds(-10)) _recentErrors.Dequeue();
        if (_recentErrors.Count > 5) return;

        e.Handled = true;
        _mainWindow?.ShowNonBlockingWarning($"Unexpected error: {e.Exception.Message} Details are in link.log.");
    }

    private void LogError(Exception exception) => Log(exception.ToString());

    /// <summary>Appends to link.log; the voice logs from its own threads, so writers take turns.</summary>
    private void Log(string text)
    {
        if (_logPath is null) return;
        try
        {
            lock (_logLock)
            {
                var log = new FileInfo(_logPath);
                if (log.Exists && log.Length > MaxLogBytes) log.Delete();
                File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never raise a second failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never raise a second failure.
        }
    }

    private async void OnHotkeyPressed(HotkeyAction action)
    {
        if (_mainWindow is null) return;
        // A global hotkey is an explicit invocation: the app in front is what the user is working on.
        _currentApplication?.LatchForeground();
        switch (action)
        {
            case HotkeyAction.ShowHide:
                _mainWindow.ToggleVisible();
                break;
            case HotkeyAction.Send:
                await _mainWindow.SendFromHotkeyAsync();
                break;
            case HotkeyAction.CaptureDesktop:
                await _mainWindow.CaptureDesktopAsync();
                break;
            case HotkeyAction.CaptureActiveWindow:
                await _mainWindow.CaptureActiveWindowAsync();
                break;
            case HotkeyAction.CaptureRegion:
                await _mainWindow.CaptureRegionAsync();
                break;
            case HotkeyAction.Conversation:
                _mainWindow.ToggleConversation();
                break;
        }
    }

    /// <summary>Starts the embedded ChatGPT page off screen; if it cannot start, Link runs on without its chat.</summary>
    private async void StartChatPage(EmbeddedChatIntegration chat, StoragePaths paths)
    {
        try
        {
            _chatPage = new ChatPageWindow(paths.ChatPageProfile, chat.ComposePageScript());
            chat.Attach(_chatPage);
            await _chatPage.StartAsync();
        }
        catch (Exception exception)
        {
            LogError(exception);
            _mainWindow?.ShowNonBlockingWarning($"The ChatGPT page could not start: {exception.Message}");
        }
    }

    private void ShowNotification(string title, string message, Guid projectId) =>
        _tray?.ShowNotification(title, message, () => Dispatcher.Invoke(() => _mainWindow?.ShowProject(projectId)));

    private void ShowSettings()
    {
        if (_settings is null || _mainWindow is null || _hotkeys is null || _audio is null || _textToSpeech is null
            || _geminiKey is null || _speechModels is null || _agents is null)
        {
            return;
        }

        var previousHotkeys = _settings.Current.Hotkeys.Clone();
        var previousClaudeAccount = _settings.Current.Agent.ClaudeAccount;
        var hotkeysRestarted = false;
        _hotkeys.Stop();
        _mainWindow.PauseConversation(true);
        try
        {
            var window = new SettingsWindow(_settings, _audio, _textToSpeech, _geminiKey, _speechModels, _agents.Providers, _remote);
            if (_mainWindow.IsVisible) window.Owner = _mainWindow;
            if (window.ShowDialog() == true)
            {
                _mainWindow.ApplySettings();
                _remote?.Apply();
                // Another account has limits of its own: the bars show its usage, not the last one's.
                if (_settings.Current.Agent.ClaudeAccount != previousClaudeAccount) _mainWindow.RefreshLimits(Models.AgentProviderKind.ClaudeCode);
                try
                {
                    var failures = _hotkeys.Start(_settings.Current.Hotkeys);
                    hotkeysRestarted = true;
                    if (failures.Count > 0)
                    {
                        _mainWindow.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
                    }
                }
                catch (Exception exception)
                {
                    _settings.Current.Hotkeys = previousHotkeys;
                    _settings.Save();
                    try
                    {
                        _hotkeys.Start(previousHotkeys);
                        hotkeysRestarted = true;
                    }
                    catch (Exception)
                    {
                        // The original binding may have been claimed while Settings was open.
                    }
                    MessageBox.Show(
                        $"The new shortcut binding could not be registered and was reverted:\n{exception.Message}",
                        "Link",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            else
            {
                var failures = _hotkeys.Start(previousHotkeys);
                hotkeysRestarted = true;
                if (failures.Count > 0)
                {
                    _mainWindow.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
                }
            }
        }
        finally
        {
            _mainWindow.PauseConversation(false);
            if (!hotkeysRestarted)
            {
                try
                {
                    _hotkeys.Start(previousHotkeys);
                }
                catch (Exception exception)
                {
                    _mainWindow.ShowNonBlockingWarning($"Global hotkeys could not be restored: {exception.Message}");
                }
            }
        }
    }

    private void TryStartHotkeys()
    {
        if (_hotkeys is null || _settings is null) return;
        try
        {
            var failures = _hotkeys.Start(_settings.Current.Hotkeys);
            if (failures.Count > 0)
            {
                _mainWindow?.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Global hotkeys are unavailable:\n{exception.Message}", "Link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            SaveState();
            _mainWindow.Close();
        }

        DisposeServices();
        Shutdown();
    }

    private void SaveState()
    {
        try
        {
            _mainWindow?.SaveWindowState();
            _projects?.Save();
        }
        catch (Exception)
        {
            // Windows shutdown must not be blocked by a storage or registry error.
        }
    }

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        _audio?.Cancel();
        _hotkeys?.Dispose();
        _currentApplication?.Dispose();
        _audio?.Dispose();
        _streamingSpeech?.Dispose();
        _textToSpeech?.Dispose();
        _chat?.Dispose();
        _remote?.Dispose();
        _chatPage?.Shutdown();
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showRequests?.Dispose();
        _singleInstance?.Dispose();
    }
}
