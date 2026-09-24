using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan MicrophoneTestDuration = TimeSpan.FromSeconds(3);
    private readonly SettingsService _settings;
    private readonly IAudioRecorder _audio;
    private readonly ITextToSpeech _textToSpeech;
    private Button? _capturingHotkey;
    private string? _hotkeyBeforeCapture;
    private AudioInputDevice? _disconnectedMicrophone;
    private CancellationTokenSource? _microphoneTest;
    private IReadOnlyList<SpeechVoice> _voices = [];
    private CancellationTokenSource? _voiceTest;

    public SettingsWindow(SettingsService settings, IAudioRecorder audio, ITextToSpeech textToSpeech)
    {
        _settings = settings;
        _audio = audio;
        _textToSpeech = textToSpeech;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleService.EnableBackdrop(this, ThemeService.IsDark);
        Closing += (_, _) =>
        {
            StopMicrophoneTest();
            _voiceTest?.Cancel();
        };

        var current = settings.Current;
        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        ThemeCombo.SelectedItem = current.Theme;
        OpacitySlider.Value = current.Opacity;
        BlurBackgroundBox.IsChecked = current.BlurBackground;
        VoiceLanguageCombo.ItemsSource = Enum.GetValues<VoiceLanguage>();
        VoiceLanguageCombo.SelectedItem = current.VoiceLanguage;
        LoadMicrophones(current);
        VoiceRepliesBox.IsChecked = current.VoiceReplies;
        ReplySpeedSlider.Value = current.VoiceReplySpeed;
        LoadVoices(current);
        ClickThroughCombo.ItemsSource = new[] { ClickThroughMode.Off, ClickThroughMode.WhenCollapsed };
        ClickThroughCombo.SelectedItem = current.ClickThrough;
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

    private void LoadVoices(AppSettings current)
    {
        try
        {
            _voices = _textToSpeech.GetVoices();
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

        var cancellation = new CancellationTokenSource();
        _voiceTest = cancellation;
        VoiceTestButton.Content = "Stop";
        try
        {
            var voiceName = (ReplyVoiceCombo.SelectedItem as VoiceOption)?.Name;
            var voice = _voices.FirstOrDefault(candidate => candidate.Name == voiceName);
            var russian = voice is not null ? IsRussianVoice(voice) : _voices.Any(IsRussianVoice);
            // An answer being read aloud would talk over the sample.
            _textToSpeech.Stop();
            await _textToSpeech.SpeakAsync(
                russian ? "Привет! Так LINK будет читать ответы." : "Hi! This is how LINK will read its answers.",
                new SpeechOptions(voiceName, russian ? "ru" : "en", ReplySpeedSlider.Value),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped with the button or by closing Settings.
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

    private static string DescribeVoices(IReadOnlyList<SpeechVoice> voices)
    {
        if (voices.Count == 0) return "Windows has no voices installed. Add one in Windows Settings → Time & language → Speech.";
        if (!voices.Any(IsRussianVoice))
        {
            return "No Russian voice is installed, so Russian answers will sound wrong. Add one in Windows Settings → " +
                   "Time & language → Speech → Add voices. Link could use a local neural voice (Piper) instead — not built in yet.";
        }

        return "Link reads the opening of an answer; the rest stays on screen. Push-to-talk interrupts, Esc stops.";
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
        var microphone = MicrophoneCombo.SelectedItem as AudioInputDevice;
        if (microphone != _disconnectedMicrophone)
        {
            current.MicrophoneDeviceId = microphone?.Id;
            current.MicrophoneDeviceName = microphone?.Id is null ? null : microphone.Name;
        }
        current.VoiceReplies = VoiceRepliesBox.IsChecked == true;
        current.VoiceReplyVoice = (ReplyVoiceCombo.SelectedItem as VoiceOption)?.Name;
        current.VoiceReplySpeed = ReplySpeedSlider.Value;
        current.ClickThrough = (ClickThroughMode)(ClickThroughCombo.SelectedItem ?? ClickThroughMode.WhenCollapsed);
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
}
