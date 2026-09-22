using System.Windows;
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
            ShellBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 102, 119));
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
        ShellBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(102, 62, 70, 88));
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
}
