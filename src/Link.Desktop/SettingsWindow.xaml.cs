using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan MicrophoneTestDuration = TimeSpan.FromSeconds(3);
    private const string VoiceTestPhrase = "Привет! Я LINK. Так я буду отвечать голосом, коротко и по делу.";
    private const string GeminiPrivacyNote =
        "Gemini gets only the words Link reads aloud: no screenshots, clipboard, files or chat. " +
        "On the free tier Google may use them to improve its products.";
    private readonly SettingsService _settings;
    private readonly IAudioRecorder _audio;
    private readonly TextToSpeechService _textToSpeech;
    private readonly GeminiKeyStore _geminiKey;
    private readonly SpeechModelManager _models;
    private readonly DispatcherTimer _downloadTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Button? _capturingHotkey;
    private string? _hotkeyBeforeCapture;
    private AudioInputDevice? _disconnectedMicrophone;
    private CancellationTokenSource? _microphoneTest;
    private CancellationTokenSource? _voiceTest;
    private readonly IAgentProvider[] _agents;
    // The agents' files as chosen here; they apply on Save, like the rest.
    private readonly AgentSettings _agentPaths;
    // What each model or effort list shows after its default, to read the choice back on Save.
    private readonly Dictionary<ComboBox, List<AgentChoice>> _choices = [];
    private readonly CancellationTokenSource _agentChecks = new();

    public SettingsWindow(
        SettingsService settings,
        IAudioRecorder audio,
        TextToSpeechService textToSpeech,
        GeminiKeyStore geminiKey,
        SpeechModelManager models,
        IEnumerable<IAgentProvider> agents)
    {
        _settings = settings;
        _agents = agents.ToArray();
        _agentPaths = settings.Current.Agent.Clone();
        _audio = audio;
        _textToSpeech = textToSpeech;
        _geminiKey = geminiKey;
        _models = models;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleService.EnableBackdrop(this, ThemeService.IsDark);
        _downloadTimer.Tick += (_, _) => ShowLocalVoiceState();
        Closing += (_, _) =>
        {
            StopMicrophoneTest();
            _voiceTest?.Cancel();
            _downloadTimer.Stop();
        };

        var current = settings.Current;
        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        ThemeCombo.SelectedItem = current.Theme;
        OpacitySlider.Value = current.Opacity;
        BlurBackgroundBox.IsChecked = current.BlurBackground;
        VoiceLanguageCombo.ItemsSource = Enum.GetValues<VoiceLanguage>();
        VoiceLanguageCombo.SelectedItem = current.VoiceLanguage;
        LiveTranscriptionBox.IsChecked = current.LiveTranscription;
        ConversationModeBox.IsChecked = current.ConversationMode;
        LoadMicrophones(current);
        VoiceRepliesBox.IsChecked = current.VoiceReplies;
        ReadTypedAnswersBox.IsChecked = current.ReadTypedAnswers;
        ReplySpeedSlider.Value = current.VoiceReplySpeed;
        LoadVoiceReplies(current);
        HistorySizeBox.Text = current.ScreenshotHistorySize.ToString();
        AlwaysOnTopBox.IsChecked = current.AlwaysOnTop;
        LaunchWithWindowsBox.IsChecked = current.LaunchWithWindows;
        SoundCuesBox.IsChecked = current.SoundCues;
        DiagnosticsBox.IsChecked = current.Diagnostics;
        OutlineCapturedWindowBox.IsChecked = current.OutlineCapturedWindow;
        SetHotkey(ShowHideHotkey, current.Hotkeys.ShowHide);
        SetHotkey(PushToTalkHotkey, current.Hotkeys.PushToTalk);
        SetHotkey(SendHotkey, current.Hotkeys.Send);
        SetHotkey(DesktopHotkey, current.Hotkeys.CaptureDesktop);
        SetHotkey(WindowHotkey, current.Hotkeys.CaptureActiveWindow);
        SetHotkey(RegionHotkey, current.Hotkeys.CaptureRegion);
        SetHotkey(ConversationHotkey, current.Hotkeys.Conversation);
        SetHotkey(AskAboutScreenHotkey, current.Hotkeys.AskAboutScreen);
        VersionText.Text = $"Version {GetInformationalVersion()}";
        DefaultAgentCombo.ItemsSource = _agents.Select(agent => agent.Name).ToArray();
        DefaultAgentCombo.SelectedIndex = Math.Max(0, Array.FindIndex(_agents, agent => agent.Kind == current.Agent.DefaultProvider));
        DefaultAccessCombo.SelectedIndex = current.Agent.DefaultAccess == AgentAccessChoice.Auto ? 1 : 0;
        var claude = _agents.First(agent => agent.Kind == AgentProviderKind.ClaudeCode);
        var codex = _agents.First(agent => agent.Kind == AgentProviderKind.Codex);
        ShowChoices(ClaudeModelCombo, claude.Models, current.Agent.ClaudeModel, "Default — Claude Code's own");
        ShowChoices(CodexModelCombo, codex.Models, current.Agent.CodexModel, "Default — Codex's config");
        ShowChoices(ClaudeEffortCombo, claude.Efforts, current.Agent.ClaudeEffort, "Default — Claude Code's own");
        ShowChoices(CodexEffortCombo, codex.Efforts, current.Agent.CodexEffort, "Default — Codex's config");
        ShowLevel(ClaudeLightModelCombo, ClaudeLightEffortCombo, claude, current.Agent.ClaudeLight, "Claude Code's own");
        ShowLevel(ClaudeDeepModelCombo, ClaudeDeepEffortCombo, claude, current.Agent.ClaudeDeep, "Claude Code's own");
        ShowLevel(CodexLightModelCombo, CodexLightEffortCombo, codex, current.Agent.CodexLight, "Codex's config");
        ShowLevel(CodexDeepModelCombo, CodexDeepEffortCombo, codex, current.Agent.CodexDeep, "Codex's config");
        Closing += (_, _) => _agentChecks.Cancel();
        Loaded += (_, _) => _ = CheckAgentsAsync();
    }

    /// <summary>
    /// A model or effort list after the agent's own default, the saved value selected; a saved value the list lacks,
    /// such as a model set by hand, stays as an item of its own.
    /// </summary>
    private void ShowChoices(ComboBox combo, IEnumerable<AgentChoice> choices, string? value, string ownDefault)
    {
        var list = choices.ToList();
        if (value is not null && list.All(choice => choice.Value != value)) list.Add(new AgentChoice(value, value));
        _choices[combo] = list;
        combo.ItemsSource = list.Select(choice => choice.Label).Prepend(ownDefault).ToArray();
        combo.SelectedIndex = value is null ? 0 : list.FindIndex(choice => choice.Value == value) + 1;
    }

    private string? Chosen(ComboBox combo) => combo.SelectedIndex > 0 ? _choices[combo][combo.SelectedIndex - 1].Value : null;

    /// <summary>A level's model and effort combos; "Default" leaves either to the agent's own settings.</summary>
    private void ShowLevel(ComboBox model, ComboBox effort, IAgentProvider agent, AgentLevelChoice level, string own)
    {
        ShowChoices(model, agent.Models, level.Model, $"Default — {own}");
        ShowChoices(effort, agent.Efforts, level.Effort, "Default");
    }

    /// <summary>
    /// Runs each agent's CLI for its version and sign-in, off the UI thread; the files chosen here are tried before
    /// they are saved.
    /// </summary>
    private async Task CheckAgentsAsync()
    {
        foreach (var agent in _agents)
        {
            var text = agent.Kind == AgentProviderKind.ClaudeCode ? ClaudeStatusText : CodexStatusText;
            text.Text = "Checking…";
            var chosen = _agentPaths.PathFor(agent.Kind);
            try
            {
                var status = await agent.CheckAsync(chosen, _agentChecks.Token);
                text.Text = !status.Installed
                    ? chosen is null ? "Not found. Install it, or choose its file." : $"Not found: {chosen}"
                    : $"{(status.SignedIn switch { true => "Signed in", false => "Not signed in", null => "Sign-in unknown" })}" +
                      $" · {status.Version ?? "version unknown"}\n{status.ExecutablePath}";
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ChooseAgent_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<AgentProviderKind>(tag, out var kind)) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = kind == AgentProviderKind.ClaudeCode ? "Choose claude.exe" : "Choose codex.exe",
            Filter = "Programs (*.exe;*.cmd)|*.exe;*.cmd"
        };
        if (dialog.ShowDialog(this) != true) return;
        SetAgentPath(kind, dialog.FileName);
    }

    private void AutoAgent_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AgentProviderKind>(tag, out var kind)) SetAgentPath(kind, null);
    }

    private void SetAgentPath(AgentProviderKind kind, string? path)
    {
        if (kind == AgentProviderKind.ClaudeCode) _agentPaths.ClaudePath = path;
        else _agentPaths.CodexPath = path;
        _ = CheckAgentsAsync();
    }

    private void LoadMicrophones(AppSettings current)
    {
        IReadOnlyList<AudioInputDevice> devices = [];
        string? defaultName = null;
        try
        {
            devices = AudioInputDevices.GetAll();
            defaultName = AudioInputDevices.GetDefaultName();
        }
        catch (COMException exception)
        {
            MicrophoneTestText.Text = $"Microphones could not be listed: {exception.Message}";
        }

        var options = new List<AudioInputDevice>
        {
            new(null, defaultName is null ? "Windows default" : $"Windows default — {defaultName}")
        };
        options.AddRange(devices);
        if (current.MicrophoneDeviceId is { } savedId && devices.All(device => device.Id != savedId))
        {
            // Keep an unplugged choice instead of silently switching it to the default on Save.
            _disconnectedMicrophone = new AudioInputDevice(savedId, $"{current.MicrophoneDeviceName ?? "Saved microphone"} (not connected)");
            options.Add(_disconnectedMicrophone);
        }

        MicrophoneCombo.ItemsSource = options;
        MicrophoneCombo.SelectedItem = options.FirstOrDefault(option => option.Id == current.MicrophoneDeviceId) ?? options[0];
    }

    private void LoadVoiceReplies(AppSettings current)
    {
        ProviderOption[] providers =
        [
            new(TtsProvider.Auto, "Auto — the best that works"),
            new(TtsProvider.Gemini, "Gemini (online, free tier)"),
            new(TtsProvider.Local, "Local voice (offline)")
        ];
        ProviderCombo.ItemsSource = providers;
        ProviderCombo.ToolTip = "Auto: Gemini when a key is saved, else the local voice once downloaded. " +
                                "If Gemini fails, the local voice takes over.";
        ProviderCombo.SelectedItem = providers.First(option => option.Provider == current.VoiceReplyProvider);

        var geminiVoices = GeminiTtsProvider.Voices
            .Select(voice => new VoiceOption(voice.Name, $"{voice.Name} — {voice.Description}"))
            .ToList();
        // A voice set by hand (e.g. a designed voice_… id) stays selectable.
        if (geminiVoices.All(option => option.Name != current.GeminiVoice)) geminiVoices.Add(new VoiceOption(current.GeminiVoice, current.GeminiVoice));
        GeminiVoiceCombo.ItemsSource = geminiVoices;
        GeminiVoiceCombo.SelectedItem = geminiVoices.First(option => option.Name == current.GeminiVoice);

        var localVoices = Enumerable.Range(0, Math.Max(LocalTtsProvider.Speakers, current.LocalVoice + 1))
            .Select(speaker => new LocalVoiceOption(speaker, LocalTtsProvider.VoiceName(speaker)))
            .ToArray();
        LocalVoiceCombo.ItemsSource = localVoices;
        LocalVoiceCombo.SelectedItem = localVoices[current.LocalVoice];

        ShowGeminiKeyState();
        ShowLocalVoiceState();
    }

    private void ShowGeminiKeyState()
    {
        var hasKey = _geminiKey.HasKey;
        GeminiKeyRemoveButton.IsEnabled = hasKey;
        GeminiKeyText.Text = hasKey
            ? $"A key is saved, encrypted for your Windows account. {GeminiPrivacyNote}"
            : $"Optional: a free key from aistudio.google.com/apikey gives Link a natural voice. {GeminiPrivacyNote}";
    }

    private void GeminiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        GeminiKeySaveButton.IsEnabled = GeminiKeyBox.Password.Trim().Length > 0;

    private void GeminiKeySave_OnClick(object sender, RoutedEventArgs e) => SaveTypedGeminiKey();

    private void GeminiKeyRemove_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _geminiKey.Clear();
            ShowGeminiKeyState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GeminiKeyText.Text = $"The key could not be removed: {exception.Message}";
        }
    }

    /// <summary>Stores a key typed into the box; Test voice and Save changes use it too, so it is never left behind.</summary>
    private bool SaveTypedGeminiKey()
    {
        var key = GeminiKeyBox.Password.Trim();
        if (key.Length == 0) return true;
        try
        {
            _geminiKey.Save(key);
            GeminiKeyBox.Clear();
            ShowGeminiKeyState();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            GeminiKeyText.Text = $"The key could not be saved: {exception.Message}";
            return false;
        }
    }

    private void ShowLocalVoiceState()
    {
        var model = SpeechModel.Supertonic;
        if (_models.IsReady(model))
        {
            _downloadTimer.Stop();
            LocalDownloadButton.Visibility = Visibility.Collapsed;
            LocalVoiceCombo.IsEnabled = true;
            return;
        }

        LocalVoiceCombo.IsEnabled = false;
        var (received, failure) = _models.DownloadState(model);
        LocalDownloadButton.IsEnabled = received is null;
        // Megabytes move visibly even on a slow line, where a percent would sit still for many seconds.
        LocalDownloadButton.Content = received is { } bytes ? $"Downloading {bytes / 1_000_000} / {model.Size / 1_000_000} MB"
            : failure is not null ? "Retry download"
            : $"Download {model.Size / 1_000_000} MB";
        LocalDownloadButton.ToolTip = failure is not null && received is null
            ? $"The download failed: {failure}. It continues where it stopped."
            : "A free offline voice for Russian and English; it holds about 300 MB of memory while it speaks.";
        if (received is not null) _downloadTimer.Start();
        else _downloadTimer.Stop();
    }

    private void LocalDownload_OnClick(object sender, RoutedEventArgs e)
    {
        _ = _models.EnsureAsync(SpeechModel.Supertonic);
        ShowLocalVoiceState();
        _downloadTimer.Start();
    }

    private async void VoiceTest_OnClick(object sender, RoutedEventArgs e)
    {
        if (_voiceTest is not null)
        {
            _voiceTest.Cancel();
            return;
        }

        if (!SaveTypedGeminiKey()) return;
        var cancellation = new CancellationTokenSource();
        _voiceTest = cancellation;
        VoiceTestButton.Content = "Stop";
        ReplyVoiceText.Text = "Speaking…";
        try
        {
            var options = ChosenSpeech();
            // An answer being read aloud would talk over the sample.
            _textToSpeech.Stop();
            await _textToSpeech.SpeakAsync(VoiceTestPhrase, options, cancellation.Token);
            // When another voice stood in, the reason is right here rather than a mystery.
            ReplyVoiceText.Text = _textToSpeech.LastSpeaker is not { } speaker ? string.Empty
                : _textToSpeech.Unavailability(options.Provider) is { } reason ? $"That was the {speaker} ({reason})."
                : $"That was the {speaker}.";
        }
        catch (OperationCanceledException)
        {
            // Stopped with the button or by closing Settings.
            ReplyVoiceText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            ReplyVoiceText.Text = exception.Message;
        }
        finally
        {
            _voiceTest = null;
            cancellation.Dispose();
            VoiceTestButton.Content = "Test voice";
        }
    }

    /// <summary>The voice as chosen in this window, saved or not; the sample speaks Russian.</summary>
    private SpeechOptions ChosenSpeech() => new(
        (ProviderCombo.SelectedItem as ProviderOption)?.Provider ?? TtsProvider.Auto,
        (GeminiVoiceCombo.SelectedItem as VoiceOption)?.Name ?? _settings.Current.GeminiVoice,
        (LocalVoiceCombo.SelectedItem as LocalVoiceOption)?.Speaker ?? 0,
        "ru",
        ReplySpeedSlider.Value);

    private async void MicrophoneTest_OnClick(object sender, RoutedEventArgs e)
    {
        if (_microphoneTest is not null) return;
        var cancellation = new CancellationTokenSource();
        _microphoneTest = cancellation;
        MicrophoneTestButton.IsEnabled = false;
        MicrophoneCombo.IsEnabled = false;
        _audio.LevelChanged += OnMicrophoneLevel;
        var started = false;
        try
        {
            var device = MicrophoneCombo.SelectedItem as AudioInputDevice;
            var usedSelectedDevice = _audio.Start(device?.Id);
            started = true;
            MicrophoneTestText.Text = usedSelectedDevice
                ? "Listening — say something…"
                : "This microphone is not connected — testing the Windows default. Say something…";
            await Task.Delay(MicrophoneTestDuration, cancellation.Token);
            var recording = _audio.Stop(save: false);
            MicrophoneTestText.Text = recording.IsSilent
                ? "No sound was picked up. Choose another microphone, unmute it, or allow desktop apps to use it in Windows Settings → Privacy → Microphone."
                : $"Works — peak level {recording.PeakLevel:P0}.";
        }
        catch (OperationCanceledException)
        {
            // The window closed during the test; the recorder was already released.
        }
        catch (Exception exception)
        {
            if (started) _audio.Cancel();
            MicrophoneTestText.Text = exception.Message;
        }
        finally
        {
            _audio.LevelChanged -= OnMicrophoneLevel;
            _microphoneTest = null;
            cancellation.Dispose();
            SetMicrophoneLevel(0);
            MicrophoneTestButton.IsEnabled = true;
            MicrophoneCombo.IsEnabled = true;
        }
    }

    private void StopMicrophoneTest()
    {
        if (_microphoneTest is not { } test) return;
        test.Cancel();
        _audio.Cancel();
    }

    private void OnMicrophoneLevel(double peak) => Dispatcher.BeginInvoke(() => SetMicrophoneLevel(peak));

    private void SetMicrophoneLevel(double peak)
    {
        // -60..0 dBFS fills the bar, so a quiet microphone still visibly moves it.
        var level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        if (MicrophoneLevelFill.RenderTransform is ScaleTransform scale) scale.ScaleX = level;
    }

    private static string GetInformationalVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var buildId = assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        return $"{version} · build {buildId}";
    }

    private void HotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button hotkeyButton) return;
        if (_capturingHotkey != hotkeyButton)
        {
            EndHotkeyCapture(restorePrevious: true);
            _capturingHotkey = hotkeyButton;
            _hotkeyBeforeCapture = GetHotkey(hotkeyButton);
            SetHotkey(hotkeyButton, "Press shortcut…");
            hotkeyButton.BorderBrush = (Brush)FindResource("AccentBorderBrush");
            hotkeyButton.Background = (Brush)FindResource("ActiveContextBrush");
        }

        hotkeyButton.Focus();
        ErrorText.Text = string.Empty;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey is null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndHotkeyCapture(restorePrevious: true);
            ErrorText.Text = string.Empty;
            return;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var hasModifier = modifiers != ModifierKeys.None;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert;
        if (!hasModifier && !isFunctionKey)
        {
            ErrorText.Text = "Use Ctrl, Alt, Shift or Win with ordinary keys.";
            return;
        }

        ErrorText.Text = string.Empty;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        // Key.Enter and Key.Return are the same value; show the name printed on the keyboard.
        parts.Add(key == Key.Return ? "Enter" : key.ToString());
        SetHotkey(_capturingHotkey, string.Join('+', parts));
        EndHotkeyCapture(restorePrevious: false);
    }

    private void EndHotkeyCapture(bool restorePrevious)
    {
        if (_capturingHotkey is null) return;
        if (restorePrevious && _hotkeyBeforeCapture is not null)
        {
            SetHotkey(_capturingHotkey, _hotkeyBeforeCapture);
        }

        _capturingHotkey.ClearValue(BorderBrushProperty);
        _capturingHotkey.ClearValue(BackgroundProperty);
        _capturingHotkey = null;
        _hotkeyBeforeCapture = null;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture(restorePrevious: true);
        if (!int.TryParse(HistorySizeBox.Text, out var historySize) || historySize is < 1 or > 20)
        {
            ErrorText.Text = "Screenshot history must be between 1 and 20.";
            return;
        }

        var hotkeys = new[]
        {
            GetHotkey(ShowHideHotkey),
            GetHotkey(PushToTalkHotkey),
            GetHotkey(SendHotkey),
            GetHotkey(DesktopHotkey),
            GetHotkey(WindowHotkey),
            GetHotkey(RegionHotkey),
            GetHotkey(ConversationHotkey),
            GetHotkey(AskAboutScreenHotkey)
        };
        if (hotkeys.Any(string.IsNullOrWhiteSpace))
        {
            ErrorText.Text = "Each action must have a unique hotkey.";
            return;
        }

        try
        {
            var gestures = hotkeys.Select(HotkeyGesture.Parse).ToArray();
            if (gestures.Any(gesture => !gesture.IsSafeGlobalShortcut))
            {
                ErrorText.Text = "Global shortcuts must include a modifier or use a function key.";
                return;
            }

            // Compare keys, not text: "Ctrl+Alt+Enter" and "Ctrl+Alt+Return" are the same shortcut.
            if (gestures.DistinctBy(gesture => (gesture.VirtualKey, gesture.Control, gesture.Alt, gesture.Shift, gesture.Windows))
                    .Count() != gestures.Length)
            {
                ErrorText.Text = "Each action must have a unique hotkey.";
                return;
            }
        }
        catch (FormatException exception)
        {
            ErrorText.Text = exception.Message;
            return;
        }

        var current = _settings.Current;
        current.Theme = (AppTheme)(ThemeCombo.SelectedItem ?? AppTheme.System);
        current.Opacity = OpacitySlider.Value;
        current.BlurBackground = BlurBackgroundBox.IsChecked == true;
        current.VoiceLanguage = (VoiceLanguage)(VoiceLanguageCombo.SelectedItem ?? VoiceLanguage.Auto);
        current.LiveTranscription = LiveTranscriptionBox.IsChecked == true;
        current.ConversationMode = ConversationModeBox.IsChecked == true;
        var microphone = MicrophoneCombo.SelectedItem as AudioInputDevice;
        if (microphone != _disconnectedMicrophone)
        {
            current.MicrophoneDeviceId = microphone?.Id;
            current.MicrophoneDeviceName = microphone?.Id is null ? null : microphone.Name;
        }
        if (!SaveTypedGeminiKey()) return;
        var speech = ChosenSpeech();
        current.VoiceReplies = VoiceRepliesBox.IsChecked == true;
        current.ReadTypedAnswers = ReadTypedAnswersBox.IsChecked == true;
        current.VoiceReplyProvider = speech.Provider;
        current.GeminiVoice = speech.GeminiVoice;
        current.LocalVoice = speech.LocalVoice;
        current.VoiceReplySpeed = speech.Speed;
        current.ScreenshotHistorySize = historySize;
        current.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        current.LaunchWithWindows = LaunchWithWindowsBox.IsChecked == true;
        current.SoundCues = SoundCuesBox.IsChecked == true;
        current.Diagnostics = DiagnosticsBox.IsChecked == true;
        current.OutlineCapturedWindow = OutlineCapturedWindowBox.IsChecked == true;
        current.Hotkeys.ShowHide = GetHotkey(ShowHideHotkey);
        current.Hotkeys.PushToTalk = GetHotkey(PushToTalkHotkey);
        current.Hotkeys.Send = GetHotkey(SendHotkey);
        current.Hotkeys.CaptureDesktop = GetHotkey(DesktopHotkey);
        current.Hotkeys.CaptureActiveWindow = GetHotkey(WindowHotkey);
        current.Hotkeys.CaptureRegion = GetHotkey(RegionHotkey);
        current.Hotkeys.Conversation = GetHotkey(ConversationHotkey);
        current.Hotkeys.AskAboutScreen = GetHotkey(AskAboutScreenHotkey);
        current.Agent.DefaultProvider = DefaultAgentCombo.SelectedIndex >= 0 ? _agents[DefaultAgentCombo.SelectedIndex].Kind : current.Agent.DefaultProvider;
        current.Agent.ClaudePath = _agentPaths.ClaudePath;
        current.Agent.CodexPath = _agentPaths.CodexPath;
        current.Agent.DefaultAccess = DefaultAccessCombo.SelectedIndex == 0 ? AgentAccessChoice.Edit : AgentAccessChoice.Auto;
        current.Agent.ClaudeModel = Chosen(ClaudeModelCombo);
        current.Agent.CodexModel = Chosen(CodexModelCombo);
        current.Agent.SetEffort(AgentProviderKind.ClaudeCode, Chosen(ClaudeEffortCombo));
        current.Agent.SetEffort(AgentProviderKind.Codex, Chosen(CodexEffortCombo));
        current.Agent.ClaudeLight = new AgentLevelChoice { Model = Chosen(ClaudeLightModelCombo), Effort = Chosen(ClaudeLightEffortCombo) };
        current.Agent.ClaudeDeep = new AgentLevelChoice { Model = Chosen(ClaudeDeepModelCombo), Effort = Chosen(ClaudeDeepEffortCombo) };
        current.Agent.CodexLight = new AgentLevelChoice { Model = Chosen(CodexLightModelCombo), Effort = Chosen(CodexLightEffortCombo) };
        current.Agent.CodexDeep = new AgentLevelChoice { Model = Chosen(CodexDeepModelCombo), Effort = Chosen(CodexDeepEffortCombo) };
        current.Agent.Normalize();

        try
        {
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture(restorePrevious: true);
        DialogResult = false;
    }

    private static string GetHotkey(Button button) =>
        button.Content is TextBlock text ? text.Text : string.Empty;

    private static void SetHotkey(Button button, string value)
    {
        if (button.Content is TextBlock text) text.Text = value;
    }

    /// <summary>A Gemini voice choice.</summary>
    private sealed record VoiceOption(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record LocalVoiceOption(int Speaker, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ProviderOption(TtsProvider Provider, string Label)
    {
        public override string ToString() => Label;
    }
}
