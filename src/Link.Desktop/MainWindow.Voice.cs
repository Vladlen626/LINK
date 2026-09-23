using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Link.Desktop.Models;

namespace Link.Desktop;

public partial class MainWindow
{
    public void StartPushToTalk()
    {
        if (_sending || _voiceState != VoiceState.Idle) return;

        try
        {
            RemoveVoiceContextItems();
            _voiceState = VoiceState.Recording;
            ShowPassive();
            _audio.Start();
            ShellBorder.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            VoiceButton.Background = (Brush)FindResource("ActiveContextBrush");
            VoiceButton.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            ExpandedLinkGlyph.Foreground = (Brush)FindResource("SuccessBrush");
            CollapsedLinkGlyph.Foreground = (Brush)FindResource("SuccessBrush");
            AnimateVoiceGlyphs(start: true);
            SetStatus("Listening — release to finish", "Listening");
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
        ShellBorder.BorderBrush = (Brush)FindResource("GlassBorderBrush");
        VoiceButton.Background = (Brush)FindResource("SubtleGlassBrush");
        VoiceButton.BorderBrush = (Brush)FindResource("GlassBorderSoftBrush");
        ExpandedLinkGlyph.Foreground = (Brush)FindResource("AquaBrush");
        CollapsedLinkGlyph.Foreground = (Brush)FindResource("AquaBrush");
        AnimateVoiceGlyphs(start: false);
        string? audioPath = null;

        try
        {
            audioPath = _audio.Stop();
            RemoveVoiceContextItems();
            RemoveMissingContextFiles();
            SetStatus("Transcribing locally…", "Transcribing");
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
