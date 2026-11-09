using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Link.Desktop;

// Voice in the Studio: the card's goal and the task's composer take dictation as the chat's box does. Link's window listens
// and writes; the panel only says which box is in view and what its buttons and keys ask for.
public partial class AgentPanel
{
    /// <summary>A microphone button is held: Link listens.</summary>
    public event Action? VoiceStarted;

    /// <summary>The microphone button is let go: the phrase goes into the box in view.</summary>
    public event Action? VoiceStopped;

    /// <summary>Enter in the composer (Ctrl+Enter on the card) while Link listens: the words go once they are written.</summary>
    public event Action? VoiceSendRequested;

    /// <summary>The box in view that takes dictation: the card's goal, else the open task's composer; none over the list.</summary>
    public TextBox? DictationBox =>
        CardView.Visibility == Visibility.Visible ? GoalBox
        : ReplyPanel.Visibility == Visibility.Visible ? ReplyBox
        : null;

    /// <summary>
    /// Dictated words are in the box: the card picks its kit from them; when Enter was pressed while the user spoke, they go
    /// as Enter sends them, if the box is still the one in view.
    /// </summary>
    public void TakeDictation(TextBox box, bool send)
    {
        if (box == GoalBox) PickFromGoal();
        if (!send || box != DictationBox) return;
        if (box == GoalBox) Run_OnClick(RunButton, new RoutedEventArgs());
        else SendReply(queue: false);
    }

    /// <summary>The microphone buttons glow while Link listens; during a chat request they wait, as the chat's does.</summary>
    public void ShowVoice(bool listening, bool enabled)
    {
        foreach (var button in new[] { CardVoiceButton, ReplyVoiceButton })
        {
            button.IsEnabled = enabled;
            if (listening)
            {
                button.Background = (Brush)FindResource("RecordingGelBrush");
                button.BorderBrush = (Brush)FindResource("SuccessBorderBrush");
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(BorderBrushProperty);
            }
        }
    }

    private void Voice_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        VoiceStarted?.Invoke();
        ((Button)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Voice_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ((Button)sender).ReleaseMouseCapture();
        VoiceStopped?.Invoke();
        e.Handled = true;
    }

    /// <summary>A box Link writes the live words into takes no keys but its send, which waits for the words.</summary>
    private bool KeyWhileDictating(TextBox box, KeyEventArgs e, bool send)
    {
        if (!box.IsReadOnly) return false;
        if (send)
        {
            e.Handled = true;
            VoiceSendRequested?.Invoke();
        }

        return true;
    }
}
