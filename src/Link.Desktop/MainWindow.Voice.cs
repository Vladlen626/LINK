using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;

namespace Link.Desktop;

public partial class MainWindow
{
    public void StartPushToTalk()
    {
        if (_audio.IsRecording || _stoppingVoice) return;

        try
        {
            ShowPassive();
            _audio.Start();
            RecordingPanel.Visibility = Visibility.Visible;
            ShellBorder.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            VoiceButton.Background = (Brush)FindResource("ActiveContextBrush");
            VoiceButton.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            VoiceStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
            CollapsedStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
            StatusText.Text = "Recording voice…";
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    public async Task StopPushToTalkAsync()
    {
        if (!_audio.IsRecording || _stoppingVoice) return;

        _stoppingVoice = true;
        RecordingPanel.Visibility = Visibility.Collapsed;
        ShellBorder.BorderBrush = (Brush)FindResource("GlassBorderBrush");
        VoiceButton.Background = (Brush)FindResource("SubtleGlassBrush");
        VoiceButton.BorderBrush = (Brush)FindResource("ControlBorderBrush");
        VoiceStatusIndicator.Fill = (Brush)FindResource("AccentBrush");
        CollapsedStatusIndicator.Fill = (Brush)FindResource("AccentBrush");
        string? audioPath = null;

        try
        {
            audioPath = _audio.Stop();
            RemoveMissingContextFiles();
            StatusText.Text = "Transcribing locally…";
            var transcript = await _speech.TranscribeAsync(audioPath, _settings.Current.VoiceLanguage);
            PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text)
                ? transcript
                : $"{PromptBox.Text.TrimEnd()} {transcript}";
            _context.Add(new ContextItem
            {
                Type = ContextItemType.Voice,
                Label = "Voice",
                Text = transcript,
                FilePath = audioPath,
                MimeType = "audio/wav"
            });
            StatusText.Text = "Voice transcribed";
        }
        catch (Exception exception)
        {
            if (audioPath is not null)
            {
                _context.Add(new ContextItem
                {
                    Type = ContextItemType.Voice,
                    Label = "Voice audio",
                    FilePath = audioPath,
                    MimeType = "audio/wav"
                });
            }
            SetError(exception.Message);
        }
        finally
        {
            _stoppingVoice = false;
        }
    }

    private void Voice_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ((Button)sender).CaptureMouse();
        StartPushToTalk();
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
