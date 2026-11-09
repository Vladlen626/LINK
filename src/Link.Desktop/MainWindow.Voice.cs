using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private const string SpeakingStatus = "Speaking — Esc stops, push-to-talk interrupts";
    private readonly PromptDraft _draft = new();

    // The Studio box the phrase goes into (the card's goal, a task's composer), chosen when the key is pressed; none: the chat's.
    private TextBox? _studioBox;

    private TextBox VoiceBox => _studioBox ?? PromptBox;

    /// <summary>Push-to-talk pressed; during a spoken answer it silences Link and listens right away.</summary>
    public void StartPushToTalk()
    {
        if (!_voice.CanStartListening) return;

        try
        {
            ShowPassive();
            ChooseVoiceBox();
            var usedSelectedMicrophone = _voice.StartListening();
            _cues.Play(SoundCue.Listening);
            SetStatus(!usedSelectedMicrophone
                ? "Selected microphone is not connected — listening on the Windows default"
                : _voice.DraftNote is { } note ? $"Listening — {note}" : "Listening — release to finish");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    /// <summary>
    /// On the Studio tab the words go into the box in view, the card's goal or the task's composer; over the task list there
    /// is none, so Link turns to its chat.
    /// </summary>
    private void ChooseVoiceBox()
    {
        _studioBox = Place.Tab == StudioTab.Studio ? _agentPanel?.DictationBox : null;
        if (Place.Tab == StudioTab.Studio && _studioBox is null) ShowTab(StudioTab.Chat);
    }

    public async Task StopPushToTalkAsync()
    {
        if (_voice.Phase != VoicePhase.Listening) return;
        await TranscribeAndSendAsync();
    }

    /// <summary>Turns the phrase into text in the prompt; it goes out when Send was pressed while the user spoke.</summary>
    private async Task TranscribeAndSendAsync()
    {
        VoiceTranscript? transcript;
        try
        {
            transcript = await _voice.FinishListeningAsync();
        }
        catch (Exception exception)
        {
            // The live words may be all that is left of the dictation, so they stay as typed text.
            _draft.Keep();
            SetError(exception.Message);
            return;
        }

        // The final pass replaces the live words instead of adding a second version of them.
        var box = VoiceBox;
        ShowVoiceText(box, _draft.Commit(box.Text, transcript?.Text ?? string.Empty));
        if (transcript is null)
        {
            SetStatus("No speech heard — hold the key while speaking");
            return;
        }

        SetStatus("Voice transcribed");
        if (_studioBox is { } studio) _agentPanel?.TakeDictation(studio, transcript.SendRequested);
        else if (transcript.SendRequested) await SendAsync();
    }

    private void OnVoiceDraftChanged(string words) => ShowVoiceText(VoiceBox, _draft.Show(VoiceBox.Text, words));

    /// <summary>Another voice stands in for the chosen one, e.g. after Gemini's free limit; the answer is not held up.</summary>
    public void ShowVoiceNotice(string message)
    {
        if (_voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        SetStatus(message);
    }

    private static void ShowVoiceText(TextBox box, string text)
    {
        if (box.Text == text) return;
        box.Text = text;
        // Dictated words arrive at the end: keep them in view.
        box.CaretIndex = text.Length;
        box.ScrollToEnd();
    }

    /// <summary>Typing into words that are still being recognized would mix with them, so the box they go into waits.</summary>
    private void UpdatePromptReadOnly()
    {
        var dictating = _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing;
        PromptBox.IsReadOnly = _sending || dictating && _studioBox is null;
        if (_studioBox is { } studio) studio.IsReadOnly = dictating;
    }

    private void OnVoicePhaseChanged(VoicePhase previous)
    {
        var phase = _voice.Phase;
        var listening = phase == VoicePhase.Listening;
        var speaking = phase == VoicePhase.Speaking;
        UpdatePromptReadOnly();
        ShellRim.BorderBrush = (Brush)FindResource(listening ? "RecordingRimBrush" : "RimLightBrush");
        if (listening)
        {
            VoiceButton.Background = (Brush)FindResource("RecordingGelBrush");
            VoiceButton.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
        }
        else
        {
            VoiceButton.ClearValue(BackgroundProperty);
            VoiceButton.ClearValue(BorderBrushProperty);
        }

        LinkGlyph.Foreground = (Brush)FindResource(listening ? "SuccessBrush" : speaking ? "AquaBrush" : "BrandBrush");
        AnimateVoiceGlyph(start: listening || speaking);
        // During a request push-to-talk waits for the answer, unless Link talks: then it interrupts.
        VoiceButton.IsEnabled = _voice.CanStartListening || phase == VoicePhase.Listening;
        _agentPanel?.ShowVoice(listening, VoiceButton.IsEnabled);
        UpdateSendButton();
        UpdateReadAloudButton();

        if (phase == VoicePhase.Finalizing)
        {
            SetStatus(!_voice.IsSpeechModelReady ? "Downloading local speech model (first use)…"
                : _voice.FinalNote is { } note ? $"Transcribing locally… · {note}"
                : "Transcribing locally…");
            return;
        }

        // While the answer arrives the status line follows the chat; the pulsing glyph shows that Link talks.
        if (_sending) return;
        if (speaking) SetStatus(SpeakingStatus);
        else if (previous == VoicePhase.Speaking && phase == VoicePhase.Idle) SetStatus(ResponseReceivedStatus());
    }

    private void AnimateVoiceGlyph(bool start)
    {
        if (LinkGlyph.RenderTransform is not ScaleTransform scale) return;
        if (!start)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            return;
        }

        var pulse = new DoubleAnimation(1, 1.09, TimeSpan.FromMilliseconds(620))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        // Each frame of the pulse redraws the window while the microphone listens: a low frame rate keeps it cheap.
        Timeline.SetDesiredFrameRate(pulse, 24);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
    }

    private void Voice_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        StartPushToTalk();
        if (_voice.Phase == VoicePhase.Listening) ((Button)sender).CaptureMouse();
        e.Handled = true;
    }

    private async void Voice_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ((Button)sender).ReleaseMouseCapture();
        await StopPushToTalkAsync();
        e.Handled = true;
    }
}
