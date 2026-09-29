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
    private IReadOnlyList<SpeechVoice> _voices = [];
    private CancellationTokenSource? _voiceTest;

    public SettingsWindow(
        SettingsService settings,
        IAudioRecorder audio,
        TextToSpeechService textToSpeech,
        GeminiKeyStore geminiKey,
        SpeechModelManager models)
    {
        _settings = settings;
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
        LoadMicrophones(current);
        VoiceRepliesBox.IsChecked = current.VoiceReplies;
        ReadTypedAnswersBox.IsChecked = current.ReadTypedAnswers;
        ReplySpeedSlider.Value = current.VoiceReplySpeed;
        LoadVoiceReplies(current);
        LoadVoices(current);
        HistorySizeBox.Text = current.ScreenshotHistorySize.ToString();
        AlwaysOnTopBox.IsChecked = current.AlwaysOnTop;
        LaunchWithWindowsBox.IsChecked = current.LaunchWithWindows;
        SetHotkey(ShowHideHotkey, current.Hotkeys.ShowHide);
        SetHotkey(PushToTalkHotkey, current.Hotkeys.PushToTalk);
        SetHotkey(SendHotkey, current.Hotkeys.Send);
        SetHotkey(DesktopHotkey, current.Hotkeys.CaptureDesktop);
        SetHotkey(WindowHotkey, current.Hotkeys.CaptureActiveWindow);
        SetHotkey(RegionHotkey, current.Hotkeys.CaptureRegion);
        VersionText.Text = $"Version {GetInformationalVersion()}";
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
            new(TtsProvider.Local, "Local voice (offline)"),
            new(TtsProvider.Windows, "System (Windows voices)")
        ];
        ProviderCombo.ItemsSource = providers;
        ProviderCombo.ToolTip = "Auto: Gemini when a key is saved, else the local voice once downloaded, else Windows. " +
                                "Whichever voice fails, the next one takes over.";
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

    private void LoadVoices(AppSettings current)
    {
        try
        {
            _voices = WindowsTtsProvider.InstalledVoices();
            ReplyVoiceText.Text = DescribeVoices(_voices);
        }
        catch (Exception exception)
        {
            ReplyVoiceText.Text = $"Windows voices could not be listed: {exception.Message}";
        }

        var options = new List<VoiceOption> { new(null, "Auto — matches the answer's language") };
        options.AddRange(_voices.Select(voice => new VoiceOption(voice.Name, $"{voice.Name} · {voice.Language}")));
        if (current.VoiceReplyVoice is { } savedName && _voices.All(voice => voice.Name != savedName))
        {
            // Keep an uninstalled choice instead of silently switching it to Auto on Save.
            options.Add(new VoiceOption(savedName, $"{savedName} (not installed)"));
        }

        ReplyVoiceCombo.ItemsSource = options;
        ReplyVoiceCombo.SelectedItem = options.First(option => option.Name == current.VoiceReplyVoice);
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
            await _textToSpeech.SpeakAsync(
                options.Language == "ru" ? VoiceTestPhrase : "Hi! I'm LINK. This is how I will answer you aloud.",
                options,
                cancellation.Token);
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

    /// <summary>The voice as chosen in this window, saved or not; Russian unless a Windows voice that cannot speak it is forced.</summary>
    private SpeechOptions ChosenSpeech()
    {
        var provider = (ProviderCombo.SelectedItem as ProviderOption)?.Provider ?? TtsProvider.Auto;
        var windowsVoice = (ReplyVoiceCombo.SelectedItem as VoiceOption)?.Name;
        var chosenWindowsVoice = _voices.FirstOrDefault(voice => voice.Name == windowsVoice);
        var russian = provider != TtsProvider.Windows
            || (chosenWindowsVoice is not null ? IsRussianVoice(chosenWindowsVoice) : _voices.Any(IsRussianVoice));
        return new SpeechOptions(
            provider,
            (GeminiVoiceCombo.SelectedItem as VoiceOption)?.Name ?? _settings.Current.GeminiVoice,
            (LocalVoiceCombo.SelectedItem as LocalVoiceOption)?.Speaker ?? 0,
            windowsVoice,
            russian ? "ru" : "en",
            ReplySpeedSlider.Value);
    }

    private static string DescribeVoices(IReadOnlyList<SpeechVoice> voices)
    {
        if (voices.Count == 0) return "Windows has no voices installed. Add one in Windows Settings → Time & language → Speech.";
        var reading = "Link reads the answer while it arrives; code and tables stay on screen. Push-to-talk interrupts, Esc stops.";
        return voices.Any(IsRussianVoice)
            ? reading
            : $"{reading} Windows has no Russian voice, so it is a poor last resort for Russian: use Gemini or the local voice.";
    }

    private static bool IsRussianVoice(SpeechVoice voice) =>
        voice.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

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
            GetHotkey(RegionHotkey)
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
        current.VoiceReplyVoice = speech.WindowsVoice;
        current.VoiceReplySpeed = speech.Speed;
        current.ScreenshotHistorySize = historySize;
        current.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        current.LaunchWithWindows = LaunchWithWindowsBox.IsChecked == true;
        current.Hotkeys.ShowHide = GetHotkey(ShowHideHotkey);
        current.Hotkeys.PushToTalk = GetHotkey(PushToTalkHotkey);
        current.Hotkeys.Send = GetHotkey(SendHotkey);
        current.Hotkeys.CaptureDesktop = GetHotkey(DesktopHotkey);
        current.Hotkeys.CaptureActiveWindow = GetHotkey(WindowHotkey);
        current.Hotkeys.CaptureRegion = GetHotkey(RegionHotkey);

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

    /// <summary>A reply voice choice; a null <see cref="Name"/> picks a voice for the answer's language.</summary>
    private sealed record VoiceOption(string? Name, string Label)
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
