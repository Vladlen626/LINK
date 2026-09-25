using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private const string SpeakingStatus = "Speaking — Esc stops, push-to-talk interrupts";
    private readonly PromptDraft _draft = new();

    /// <summary>Push-to-talk pressed; during a spoken answer it silences Link and listens right away.</summary>
    public void StartPushToTalk()
    {
        if (!_voice.CanStartListening) return;

        try
        {
            ShowPassive();
            var usedSelectedMicrophone = _voice.StartListening();
            SetStatus(!usedSelectedMicrophone
                ? "Selected microphone is not connected — listening on the Windows default"
                : _voice.DraftNote is { } note ? $"Listening — {note}" : "Listening — release to finish", "Listening");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    public async Task StopPushToTalkAsync()
    {
        if (_voice.Phase != VoicePhase.Listening) return;

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

        if (transcript is null)
        {
            SetPromptText(_draft.Commit(PromptBox.Text, string.Empty));
            SetStatus("No speech heard — hold the key while speaking");
            return;
        }

        // The final pass replaces the live words instead of adding a second version of them.
        SetPromptText(_draft.Commit(PromptBox.Text, transcript.Text));
        SetStatus("Voice transcribed");
        // Send was pressed while the user was still speaking or being transcribed.
        if (transcript.SendRequested) await SendAsync();
    }

    private void OnVoiceDraftChanged(string words) => SetPromptText(_draft.Show(PromptBox.Text, words));

    private void SetPromptText(string text)
    {
        if (PromptBox.Text == text) return;
        PromptBox.Text = text;
        // Dictated words arrive at the end: keep them in view.
        PromptBox.CaretIndex = text.Length;
        PromptBox.ScrollToEnd();
    }

    /// <summary>Typing into words that are still being recognized would mix with them, so the prompt waits.</summary>
    private void UpdatePromptReadOnly() =>
        PromptBox.IsReadOnly = _sending || _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing;

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

        var glyph = (Brush)FindResource(listening ? "SuccessBrush" : speaking ? "AquaBrush" : "BrandBrush");
        ExpandedLinkGlyph.Foreground = glyph;
        CollapsedLinkGlyph.Foreground = glyph;
        AnimateVoiceGlyphs(start: listening || speaking);
        // During a request push-to-talk waits for the answer, unless Link talks: then it interrupts.
        VoiceButton.IsEnabled = phase is not (VoicePhase.Sending or VoicePhase.Waiting);
        UpdateSendButton();

        if (phase == VoicePhase.Finalizing)
        {
            SetStatus(!_voice.IsSpeechModelReady ? "Downloading local speech model (first use)…"
                : _voice.FinalNote is { } note ? $"Transcribing locally… · {note}"
                : "Transcribing locally…", "Transcribing");
            return;
        }

        // While the answer arrives the status line follows the chat; the pulsing glyph shows that Link talks.
        if (_sending) return;
        if (speaking) SetStatus(SpeakingStatus, "Speaking");
        else if (previous == VoicePhase.Speaking && phase == VoicePhase.Idle) SetStatus(ResponseReceivedStatus(), "Done");
    }

    private void AnimateVoiceGlyphs(bool start)
    {
        foreach (var glyph in new[] { ExpandedLinkGlyph, CollapsedLinkGlyph })
        {
            if (glyph.RenderTransform is not ScaleTransform scale) continue;
            if (!start)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                continue;
            }

            var pulse = new DoubleAnimation(1, 1.09, TimeSpan.FromMilliseconds(620))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            // The overlay is a layered window: every frame is copied to system memory, so keep the pulse cheap.
            Timeline.SetDesiredFrameRate(pulse, 24);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
        }
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
