namespace Link.Desktop.Models;

public sealed class AppSettings
{
    private const double DefaultGlassTint = 0.6;
    // Answers are read a little quicker than a voice's normal pace.
    private const double DefaultVoiceReplySpeed = 1.2;
    // Described as warm: the easiest to listen to all day. Any prebuilt voice speaks Russian.
    private const string DefaultGeminiVoice = "Sulafat";

    public int SchemaVersion { get; set; } = 7;
    public double Opacity { get; set; } = DefaultGlassTint;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool BlurBackground { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public VoiceLanguage VoiceLanguage { get; set; } = VoiceLanguage.Auto;
    public string? MicrophoneDeviceId { get; set; }
    public string? MicrophoneDeviceName { get; set; }

    /// <summary>Show the words in the prompt while push-to-talk is held; the final transcript replaces them.</summary>
    public bool LiveTranscription { get; set; } = true;

    /// <summary>Read the answer to a dictated prompt aloud.</summary>
    public bool VoiceReplies { get; set; } = true;

    /// <summary>With <see cref="VoiceReplies"/>, answers to typed prompts are read aloud too.</summary>
    public bool ReadTypedAnswers { get; set; }

    /// <summary>The voice that reads answers first; the ones after it in Auto's order stand in when it cannot.</summary>
    public TtsProvider VoiceReplyProvider { get; set; } = TtsProvider.Auto;

    /// <summary>A Gemini prebuilt voice, kept between answers so that Link always sounds the same.</summary>
    public string GeminiVoice { get; set; } = DefaultGeminiVoice;

    /// <summary>The speaker of the local voice model.</summary>
    public int LocalVoice { get; set; }

    public double VoiceReplySpeed { get; set; } = DefaultVoiceReplySpeed;

    /// <summary>
    /// Hands-free: Link listens by itself whenever it is free, sends the phrase after a pause, answers aloud and
    /// listens again. Off, the microphone opens only with push-to-talk.
    /// </summary>
    public bool ConversationMode { get; set; }

    /// <summary>Short sounds for listening, a phrase heard, sent, an answer ready, a question for the user and an error.</summary>
    public bool SoundCues { get; set; } = true;
    public ResponseEffort ResponseEffort { get; set; } = ResponseEffort.Normal;

    /// <summary>How Send sends: with what Link adds, or Direct with only the text and what was attached.</summary>
    public RequestRoute Route { get; set; } = RequestRoute.Chat;
    public int ScreenshotHistorySize { get; set; } = 5;
    public bool LaunchWithWindows { get; set; }

    /// <summary>Keeps the last request, with its prompt, file paths and window title, in last-request.json.</summary>
    public bool Diagnostics { get; set; }
    public bool AutoCaptureActiveWindowOnSend { get; set; }
    public Guid? ActiveProjectId { get; set; }
    public WindowPlacement Window { get; set; } = new();
    public SidePanelSettings SidePanel { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();

    public void Normalize()
    {
        Window ??= new WindowPlacement();
        Agent ??= new AgentSettings();
        SidePanel ??= new SidePanelSettings();
        Hotkeys ??= new HotkeySettings();
        if (SchemaVersion < 4)
        {
            if (string.Equals(Hotkeys.CaptureRegion, "Ctrl+Alt+R", StringComparison.OrdinalIgnoreCase))
            {
                Hotkeys.CaptureRegion = "Ctrl+Alt+Shift+R";
            }
        }
        if (SchemaVersion < 6)
        {
            // The overlay moved from system acrylic to real see-through glass, so old tint values mean something else.
            Opacity = DefaultGlassTint;
        }
        // 7: Hide replaced the collapsed capsule. Its IsCollapsed flag is no longer read, so Link opens in full.
        SchemaVersion = 7;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.2, 1.0) : DefaultGlassTint;
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        ScreenshotHistorySize = Math.Clamp(ScreenshotHistorySize, 1, 20);
        if (!Enum.IsDefined(ResponseEffort)) ResponseEffort = ResponseEffort.Normal;
        if (!Enum.IsDefined(Route)) Route = RequestRoute.Chat;
        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId))
        {
            MicrophoneDeviceId = null;
            MicrophoneDeviceName = null;
        }
        // Also catches the removed Windows voice (3) in older files.
        if (!Enum.IsDefined(VoiceReplyProvider)) VoiceReplyProvider = TtsProvider.Auto;
        GeminiVoice = string.IsNullOrWhiteSpace(GeminiVoice) ? DefaultGeminiVoice : GeminiVoice.Trim();
        LocalVoice = Math.Max(0, LocalVoice);
        VoiceReplySpeed = double.IsFinite(VoiceReplySpeed)
            ? Math.Round(Math.Clamp(VoiceReplySpeed, 0.5, 2.0), 2)
            : DefaultVoiceReplySpeed;
        Window.Normalize();
        SidePanel.Normalize();
        Hotkeys.Normalize();
        Agent.Normalize();
    }
}

/// <summary>The panel beside Link: whether it opens with Link and how wide it is.</summary>
public sealed class SidePanelSettings
{
    public const double MinWidth = 260;
    public const double MaxWidth = 640;
    private const double DefaultWidth = 300;

    public bool IsOpen { get; set; }
    public double Width { get; set; } = DefaultWidth;

    public void Normalize() =>
        Width = double.IsFinite(Width) && Width > 0 ? Math.Clamp(Width, MinWidth, MaxWidth) : DefaultWidth;
}

public sealed class WindowPlacement
{
    public const double MinWidth = 320;
    public const double MaxWidth = 1200;
    public const double MinHeight = 200;
    public const double MaxHeight = 4320;
    private const double DefaultWidth = 340;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = DefaultWidth;

    /// <summary>
    /// How tall the expanded window may grow, set by dragging its bottom edge. The window still fits its content,
    /// so only long answers reach this height. Null keeps the compact default.
    /// </summary>
    public double? HeightLimit { get; set; }

    public void Normalize()
    {
        if (Left is { } left && !double.IsFinite(left)) Left = null;
        if (Top is { } top && !double.IsFinite(top)) Top = null;
        Width = double.IsFinite(Width) && Width > 0 ? Math.Clamp(Width, MinWidth, MaxWidth) : DefaultWidth;
        if (HeightLimit is { } height)
        {
            HeightLimit = double.IsFinite(height) && height > 0 ? Math.Clamp(height, MinHeight, MaxHeight) : null;
        }
    }
}

public sealed class HotkeySettings
{
    public string ShowHide { get; set; } = "Ctrl+Alt+Space";
    public string PushToTalk { get; set; } = "Ctrl+Alt+V";
    public string Send { get; set; } = "Ctrl+Alt+Enter";
    public string CaptureDesktop { get; set; } = "Ctrl+Alt+D";
    public string CaptureActiveWindow { get; set; } = "Ctrl+Alt+W";
    public string CaptureRegion { get; set; } = "Ctrl+Alt+Shift+R";

    // A registered hotkey takes its chord from every app, and IDEs use many Ctrl+Alt+letter ones.
    public string Conversation { get; set; } = "Ctrl+Alt+Shift+Space";

    public void Normalize()
    {
        ShowHide = DefaultIfEmpty(ShowHide, "Ctrl+Alt+Space");
        PushToTalk = DefaultIfEmpty(PushToTalk, "Ctrl+Alt+V");
        Send = DefaultIfEmpty(Send, "Ctrl+Alt+Enter");
        CaptureDesktop = DefaultIfEmpty(CaptureDesktop, "Ctrl+Alt+D");
        CaptureActiveWindow = DefaultIfEmpty(CaptureActiveWindow, "Ctrl+Alt+W");
        CaptureRegion = DefaultIfEmpty(CaptureRegion, "Ctrl+Alt+Shift+R");
        Conversation = DefaultIfEmpty(Conversation, "Ctrl+Alt+Shift+Space");
    }

    public HotkeySettings Clone() => new()
    {
        ShowHide = ShowHide,
        PushToTalk = PushToTalk,
        Send = Send,
        CaptureDesktop = CaptureDesktop,
        CaptureActiveWindow = CaptureActiveWindow,
        CaptureRegion = CaptureRegion,
        Conversation = Conversation
    };

    private static string DefaultIfEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum VoiceLanguage
{
    Auto,
    Russian,
    English
}

/// <summary>Who turns replies into speech. Auto: Gemini when it has a key, then the local voice.</summary>
public enum TtsProvider
{
    Auto,
    Gemini,
    Local
}
