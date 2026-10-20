using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly ApiKeyStore _geminiKey;
    private readonly SpeechModelManager _models;
    private readonly DispatcherTimer _downloadTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Button? _capturingHotkey;
    private string? _hotkeyBeforeCapture;
    private AudioInputDevice? _disconnectedMicrophone;
    private CancellationTokenSource? _microphoneTest;
    private CancellationTokenSource? _voiceTest;
    private readonly IAgentProvider[] _agents;
    // The agents' files and Claude account as chosen here, which the checks try.
    private readonly AgentSettings _agentPaths;
    // What each model or effort list shows after its default, to read the choice back when saving.
    private readonly Dictionary<ComboBox, List<AgentChoice>> _choices = [];
    // The context budgets Settings offers; a saved one the list lacks, such as one set by hand, joins it.
    private static readonly int[] ContextBudgets = [AgentSettings.DefaultContextBudget, 200_000, 300_000, 400_000, 450_000, 600_000, 800_000, 1_000_000];
    // What the context budget box lists, to read the choice back when saving.
    private readonly List<int> _budgets;
    private readonly CancellationTokenSource _agentChecks = new();
    // What the Claude account box lists, null for the user's own.
    private List<string?> _claudeAccounts = [];
    private readonly RemoteControl? _remote;
    // Every change is saved a moment after it is made, so a slider or typing saves once; true while the values load.
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;

    /// <summary>The settings were saved: the app applies them at once.</summary>
    public event Action? Saved;

    public SettingsWindow(
        SettingsService settings,
        IAudioRecorder audio,
        TextToSpeechService textToSpeech,
        ApiKeyStore geminiKey,
        SpeechModelManager models,
        IEnumerable<IAgentProvider> agents,
        RemoteControl? remote)
    {
        _settings = settings;
        _remote = remote;
        _agents = agents.ToArray();
        _agentPaths = settings.Current.Agent.Clone();
        _audio = audio;
        _textToSpeech = textToSpeech;
        _geminiKey = geminiKey;
        _models = models;
        InitializeComponent();
        SectionList.SelectedIndex = 0;
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        _downloadTimer.Tick += (_, _) => ShowLocalVoiceState();
        _saveTimer.Tick += (_, _) => SaveChanges();
        var edited = new RoutedEventHandler(OnEdited);
        AddHandler(ToggleButton.CheckedEvent, edited);
        AddHandler(ToggleButton.UncheckedEvent, edited);
        AddHandler(Selector.SelectionChangedEvent, edited);
        AddHandler(RangeBase.ValueChangedEvent, edited);
        AddHandler(TextBoxBase.TextChangedEvent, edited);
        Closing += (_, _) =>
        {
            EndHotkeyCapture(restorePrevious: true);
            if (_saveTimer.IsEnabled) SaveChanges();
            SaveTypedGeminiKey();
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
        _budgets = [.. ContextBudgets.Append(current.Agent.ContextBudget).Distinct().Order()];
        ContextBudgetCombo.ItemsSource = _budgets.Select(budget => budget > 0 ? AgentContext.Tokens(budget) : "Auto — the CLI's own (best)").ToArray();
        ContextBudgetCombo.SelectedIndex = _budgets.IndexOf(current.Agent.ContextBudget);
        AutoEffortBox.IsChecked = current.Agent.AutoEffort;
        LeadReviewBox.IsChecked = current.Agent.Team.LeadReview;
        ArtReviewBox.IsChecked = current.Agent.Team.ArtReview;
        AlternateBox.IsChecked = current.Agent.Team.Alternate;
        AutoLowerBox.IsChecked = current.Agent.Team.AutoLower;
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
        ShowClaudeAccounts();
        RemoteBox.IsChecked = current.Remote.Enabled;
        RemotePortBox.Text = current.Remote.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowRemote();
        if (_remote is not null)
        {
            _remote.Paired += ShowRemote;
            Closed += (_, _) => _remote.Paired -= ShowRemote;
        }
        Closing += (_, _) => _agentChecks.Cancel();
        Loaded += (_, _) => _ = CheckAgentsAsync();
        _loading = false;
    }

    /// <summary>
    /// A box ticked, a choice made, a slider moved or a field typed in: saved a moment later. The lists and scroll bars
    /// inside other controls are not edits.
    /// </summary>
    private void OnEdited(object sender, RoutedEventArgs e)
    {
        if (!_loading && e.OriginalSource is CheckBox or ComboBox or Slider or TextBox) SaveSoon();
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>One section at a time, as the list on the left picks it.</summary>
    private void Sections_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        foreach (var section in Pages.Children.OfType<FrameworkElement>())
        {
            section.Visibility = section.Name == name ? Visibility.Visible : Visibility.Collapsed;
        }
        PageScroll.ScrollToTop();
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
            var (text, dot) = agent.Kind == AgentProviderKind.ClaudeCode ? (ClaudeStatusText, ClaudeStatusDot) : (CodexStatusText, CodexStatusDot);
            text.Text = "Checking…";
            dot.Fill = (Brush)FindResource("IdleOrbBrush");
            var chosen = _agentPaths.PathFor(agent.Kind);
            try
            {
                var status = await agent.CheckAsync(_agentPaths, _agentChecks.Token);
                text.Text = !status.Installed
                    ? chosen is null ? "Not found. Install it, or choose its file." : $"Not found: {chosen}"
                    : $"{(status.SignedIn switch { true => "Signed in", false => "Not signed in", null => "Sign-in unknown" })}" +
                      (status.SignedIn == true && status.Account is { } account ? $" as {account}" : string.Empty) +
                      $" · {status.Version ?? "version unknown"}";
                // The line stays short; the file it runs is under the pointer.
                text.ToolTip = status.Installed ? status.ExecutablePath : null;
                dot.Fill = (Brush)FindResource(!status.Installed ? "ErrorOrbBrush" : status.SignedIn == true ? "SuccessOrbBrush" : "BusyOrbBrush");
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
        SaveSoon();
        _ = CheckAgentsAsync();
    }

    /// <summary>The Claude accounts, the user's own first, the chosen one selected.</summary>
    private void ShowClaudeAccounts()
    {
        _claudeAccounts = [null, .. ClaudeAccounts.Names()];
        ClaudeAccountCombo.ItemsSource = _claudeAccounts.Select(account => account ?? "Main (~\\.claude)").ToArray();
        ClaudeAccountCombo.SelectedIndex = Math.Max(0, _claudeAccounts.FindIndex(account =>
            string.Equals(account, _agentPaths.ClaudeAccount, StringComparison.OrdinalIgnoreCase)));
    }

    private void ClaudeAccountCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClaudeAccountCombo.SelectedIndex < 0) return;
        var account = _claudeAccounts[ClaudeAccountCombo.SelectedIndex];
        if (account == _agentPaths.ClaudeAccount) return;
        _agentPaths.ClaudeAccount = account;
        _ = CheckAgentsAsync();
    }

    private async void AddClaudeAccount_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _agentPaths.ClaudeAccount = await ClaudeAccounts.AddAsync();
            ShowClaudeAccounts();
            await SignInClaudeAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ClaudeStatusText.Text = $"Could not add the account: {exception.Message}";
        }
    }

    private async void SignInClaude_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await SignInClaudeAsync();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            ClaudeStatusText.Text = $"Could not open the sign-in: {exception.Message}";
        }
    }

    /// <summary>The chosen account's sign-in in a console; the status is checked again when it closes.</summary>
    private async Task SignInClaudeAsync()
    {
        if (AgentExecutables.Find(AgentProviderKind.ClaudeCode, _agentPaths.ClaudePath) is not { } executable)
        {
            ClaudeStatusText.Text = "Not found. Install it, or choose its file.";
            return;
        }

        ClaudeStatusText.Text = "Signing in: finish it in the browser…";
        await ClaudeAccounts.SignInAsync(executable, _agentPaths.ClaudeAccount);
        await CheckAgentsAsync();
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
            // Keep an unplugged choice instead of silently switching it to the default when saving.
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

    /// <summary>Stores a key typed into the box; Test voice and closing Settings use it too, so it is never left behind.</summary>
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
        SaveSoon();
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

    /// <summary>Remote control as it stands: where the page is, and the paired devices, each with Remove.</summary>
    private void ShowRemote()
    {
        RemoteStatusText.Text = _remote is null ? "Remote control is not available."
            : _remote.Problem is { } problem ? problem
            : _remote.Address is { } address ? $"On: the page is at {address} on this computer."
            : "Off: tick the box to start it.";
        ShowTelegram();
        RemoteDevicesPanel.Children.Clear();
        foreach (var device in _remote?.Devices.ToArray() ?? [])
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{device.Name} · paired {device.AddedAt:d MMM yyyy}", VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Style = (Style)FindResource("ChipButtonStyle"), Content = "Remove", ToolTip = "The device can no longer control Link" };
            remove.Click += (_, _) =>
            {
                _remote!.Remove(device);
                ShowRemote();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            RemoteDevicesPanel.Children.Add(row);
        }
    }

    /// <summary>The Telegram bot as it stands: off, waiting for its code, or paired with a chat.</summary>
    private void ShowTelegram(string? code = null)
    {
        var (running, paired) = _remote?.Telegram ?? (false, false);
        TelegramUnlinkButton.Visibility = running || paired ? Visibility.Visible : Visibility.Collapsed;
        TelegramStatusText.Text = code is not null ? $"Send {code} to your bot in Telegram within {RemotePairing.CodeLife.TotalMinutes:0} minutes."
            : paired && running ? "On: your chat gets a message when a task needs you while you are away; /help in the bot lists the rest."
            : running ? "The bot runs, no chat is paired: press Pair Telegram for a code."
            : "Off. In Telegram, @BotFather → /newbot gives a token: paste it here and press Pair Telegram.";
    }

    private void TelegramPair_OnClick(object sender, RoutedEventArgs e)
    {
        if (_remote is null) return;
        var (code, problem) = _remote.PairTelegram(TelegramTokenBox.Password);
        TelegramTokenBox.Clear();
        if (problem is not null)
        {
            TelegramStatusText.Text = problem;
            return;
        }

        ShowTelegram(code);
    }

    private void TelegramUnlink_OnClick(object sender, RoutedEventArgs e)
    {
        _remote?.UnlinkTelegram();
        ShowTelegram();
    }

    private void RemotePair_OnClick(object sender, RoutedEventArgs e)
    {
        if (_remote is null) return;
        RemoteCodeText.Text = _remote.NewCode();
        RemoteCodeText.ToolTip = $"Enter it on the page within {RemotePairing.CodeLife.TotalMinutes:0} minutes; it is good for one device";
    }

    /// <summary>The hotkeys as recorded here, or why they cannot be used.</summary>
    private string? HotkeyProblem()
    {
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
        if (hotkeys.Any(string.IsNullOrWhiteSpace)) return "Each action must have a unique hotkey.";
        try
        {
            var gestures = hotkeys.Select(HotkeyGesture.Parse).ToArray();
            if (gestures.Any(gesture => !gesture.IsSafeGlobalShortcut)) return "Global shortcuts must include a modifier or use a function key.";
            // Compare keys, not text: "Ctrl+Alt+Enter" and "Ctrl+Alt+Return" are the same shortcut.
            return gestures.DistinctBy(gesture => (gesture.VirtualKey, gesture.Control, gesture.Alt, gesture.Shift, gesture.Windows))
                .Count() != gestures.Length ? "Each action must have a unique hotkey." : null;
        }
        catch (FormatException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Saves everything as it stands here and lets the app apply it. A value that is not valid keeps the saved one, and
    /// the line at the foot says why until it is put right.
    /// </summary>
    private void SaveChanges()
    {
        _saveTimer.Stop();
        var problems = new List<string>();
        var current = _settings.Current;
        if (int.TryParse(HistorySizeBox.Text, out var historySize) && historySize is >= 1 and <= 20) current.ScreenshotHistorySize = historySize;
        else problems.Add("Screenshot history must be between 1 and 20.");
        if (int.TryParse(RemotePortBox.Text, out var remotePort) && remotePort is >= 1024 and <= 65535) current.Remote.Port = remotePort;
        else problems.Add("The remote port must be between 1024 and 65535.");
        if (HotkeyProblem() is { } hotkeyProblem) problems.Add(hotkeyProblem);
        else
        {
            current.Hotkeys.ShowHide = GetHotkey(ShowHideHotkey);
            current.Hotkeys.PushToTalk = GetHotkey(PushToTalkHotkey);
            current.Hotkeys.Send = GetHotkey(SendHotkey);
            current.Hotkeys.CaptureDesktop = GetHotkey(DesktopHotkey);
            current.Hotkeys.CaptureActiveWindow = GetHotkey(WindowHotkey);
            current.Hotkeys.CaptureRegion = GetHotkey(RegionHotkey);
            current.Hotkeys.Conversation = GetHotkey(ConversationHotkey);
            current.Hotkeys.AskAboutScreen = GetHotkey(AskAboutScreenHotkey);
        }

        current.Remote.Enabled = RemoteBox.IsChecked == true;
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
        var speech = ChosenSpeech();
        current.VoiceReplies = VoiceRepliesBox.IsChecked == true;
        current.ReadTypedAnswers = ReadTypedAnswersBox.IsChecked == true;
        current.VoiceReplyProvider = speech.Provider;
        current.GeminiVoice = speech.GeminiVoice;
        current.LocalVoice = speech.LocalVoice;
        current.VoiceReplySpeed = speech.Speed;
        current.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        current.LaunchWithWindows = LaunchWithWindowsBox.IsChecked == true;
        current.SoundCues = SoundCuesBox.IsChecked == true;
        current.Diagnostics = DiagnosticsBox.IsChecked == true;
        current.OutlineCapturedWindow = OutlineCapturedWindowBox.IsChecked == true;
        current.Agent.DefaultProvider = DefaultAgentCombo.SelectedIndex >= 0 ? _agents[DefaultAgentCombo.SelectedIndex].Kind : current.Agent.DefaultProvider;
        current.Agent.ClaudePath = _agentPaths.ClaudePath;
        current.Agent.CodexPath = _agentPaths.CodexPath;
        current.Agent.ClaudeAccount = _agentPaths.ClaudeAccount;
        current.Agent.DefaultAccess = DefaultAccessCombo.SelectedIndex == 0 ? AgentAccessChoice.Edit : AgentAccessChoice.Auto;
        current.Agent.ContextBudget = ContextBudgetCombo.SelectedIndex >= 0 ? _budgets[ContextBudgetCombo.SelectedIndex] : current.Agent.ContextBudget;
        current.Agent.AutoEffort = AutoEffortBox.IsChecked == true;
        current.Agent.Team.LeadReview = LeadReviewBox.IsChecked == true;
        current.Agent.Team.ArtReview = ArtReviewBox.IsChecked == true;
        current.Agent.Team.Alternate = AlternateBox.IsChecked == true;
        current.Agent.Team.AutoLower = AutoLowerBox.IsChecked == true;
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
            Saved?.Invoke();
            // Remote control may have started, stopped or moved to another port.
            ShowRemote();
        }
        catch (Exception exception)
        {
            problems.Add(exception.Message);
        }

        ErrorText.Text = string.Join(" ", problems);
        SavedText.Visibility = problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

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
