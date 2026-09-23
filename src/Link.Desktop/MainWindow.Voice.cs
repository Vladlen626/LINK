using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Link.Desktop.Models;

namespace Link.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(300);

    public void StartPushToTalk()
    {
        if (_sending || _voiceState != VoiceState.Idle) return;

        try
        {
            RemoveVoiceContextItems();
            _voiceState = VoiceState.Recording;
            ShowPassive();
            var usedSelectedMicrophone = _audio.Start(_settings.Current.MicrophoneDeviceId);
            ShellRim.BorderBrush = (Brush)FindResource("RecordingRimBrush");
            VoiceButton.Background = (Brush)FindResource("RecordingGelBrush");
            VoiceButton.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            ExpandedLinkGlyph.Foreground = (Brush)FindResource("SuccessBrush");
            CollapsedLinkGlyph.Foreground = (Brush)FindResource("SuccessBrush");
            AnimateVoiceGlyphs(start: true);
            SetStatus(usedSelectedMicrophone
                ? "Listening — release to finish"
                : "Selected microphone is not connected — listening on the Windows default", "Listening");
        }
        catch (Exception exception)
        {
            _voiceState = VoiceState.Idle;
            SetError(exception.Message);
        }
    }

    public async Task StopPushToTalkAsync()
    {
        if (_voiceState != VoiceState.Recording) return;

        _voiceState = VoiceState.Transcribing;
        ShellRim.BorderBrush = (Brush)FindResource("RimLightBrush");
        VoiceButton.ClearValue(BackgroundProperty);
        VoiceButton.ClearValue(BorderBrushProperty);
        ExpandedLinkGlyph.Foreground = (Brush)FindResource("BrandBrush");
        CollapsedLinkGlyph.Foreground = (Brush)FindResource("BrandBrush");
        AnimateVoiceGlyphs(start: false);
        string? audioPath = null;

        try
        {
            var recording = _audio.Stop();
            audioPath = recording.FilePath!;
            RemoveVoiceContextItems();
            RemoveMissingContextFiles();
            if (recording.Duration < MinimumSpeechDuration || recording.IsSilent)
            {
                // Whisper invents phrases for silence, so nothing is transcribed without a signal.
                TryDeleteRecording(audioPath);
                audioPath = null;
                if (recording.IsSilent)
                {
                    SetError("No sound from the microphone. Check the input device in Settings → Microphone.");
                }
                else
                {
                    SetStatus("Recording was too short — hold the key while speaking");
                }
                return;
            }

            SetStatus(_speech.IsModelReady
                ? "Transcribing locally…"
                : "Downloading local speech model (first use)…", "Transcribing");
            var transcript = await _speech.TranscribeAsync(audioPath, _settings.Current.VoiceLanguage);
            PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text)
                ? transcript
                : $"{PromptBox.Text.TrimEnd()} {transcript}";

            // The transcript has one canonical home: user input. The browser transport does not send raw audio.
            SetStatus(TryDeleteRecording(audioPath)
                ? "Voice transcribed"
                : "Voice transcribed; temporary recording could not be deleted");
        }
        catch (Exception exception)
        {
            SetError(audioPath is null
                ? exception.Message
                : $"{exception.Message} Recording was kept locally.");
        }
        finally
        {
            RemoveVoiceContextItems();
            _voiceState = VoiceState.Idle;
        }
    }

    private void RemoveVoiceContextItems()
    {
        foreach (var item in _context.Where(item => item.Type == ContextItemType.Voice).ToArray())
        {
            ReleaseContext(item);
            _context.Remove(item);
        }
    }

    private static bool TryDeleteRecording(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
        if (_voiceState == VoiceState.Recording) ((Button)sender).CaptureMouse();
        e.Handled = true;
    }

    private async void Voice_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ((Button)sender).ReleaseMouseCapture();
        await StopPushToTalkAsync();
        e.Handled = true;
    }

    private enum VoiceState
    {
        Idle,
        Recording,
        Transcribing
    }
}
