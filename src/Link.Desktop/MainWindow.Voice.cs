using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private const string SpeakingStatus = "Speaking — Esc stops, push-to-talk interrupts";
    private readonly PromptDraft _draft = new();
    private bool _conversationPaused;
    // In conversation, a phrase that ended while an answer was still arriving: it goes once that request is over.
    private bool _phraseWaiting;
    // A phrase is being transcribed and sent; the microphone waits for it.
    private bool _phraseInFlight;
    // The user types in the prompt: conversation pauses until they send, clear it or leave it.
    private bool _typing;
    // The held "ask about the screen" hotkey: the phrase goes once transcribed, with a fresh frame of the work window.
    private bool _askingAboutScreen;

    /// <summary>"Ask about the screen" held: Link listens as with push-to-talk, and letting go sends the words at once.</summary>
    public void StartAskingAboutScreen()
    {
        if (_sending)
        {
            SetStatus("An answer is on its way: ask about the screen once it is in");
            return;
        }

        StartPushToTalk();
        _askingAboutScreen = _voice.Phase == VoicePhase.Listening && !_voice.IsHandsFree;
    }

    /// <summary>Push-to-talk pressed; during a spoken answer it silences Link and listens right away.</summary>
    public void StartPushToTalk()
    {
        // Listening hands-free, the key takes the phrase over: it ends when the key is let go.
        if (_voice.IsHandsFree)
        {
            _voice.HoldListening();
            AnimateVoiceGlyph(start: true);
            SetStatus("Listening — release to finish");
            return;
        }

        if (!_voice.CanStartListening) return;

        try
        {
            ShowPassive();
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

    public async Task StopPushToTalkAsync()
    {
        // A phrase listened to hands-free ends at its pause, not with a key.
        if (_voice.Phase != VoicePhase.Listening || _voice.IsHandsFree) return;
        await FinishPhraseAsync(send: false);
    }

    /// <summary>Conversation mode on or off: the button, the hotkey.</summary>
    public void ToggleConversation()
    {
        var on = !_settings.Current.ConversationMode;
        _settings.Current.ConversationMode = on;
        _settings.Save();
        // Heard also while Link is hidden, where the mode is switched by its hotkey.
        if (on) _cues.Play(SoundCue.Listening);
        SetStatus(on ? "Conversation on — just speak, a pause sends; Link listens whenever it is free"
            : "Conversation off — the microphone listens only with push-to-talk");
        // A microphone that does not open turns the mode off again and says why.
        ApplyConversation();
    }

    /// <summary>Follows the conversation setting: listens when it is on, closes the microphone when it went off.</summary>
    private void ApplyConversation()
    {
        var on = _settings.Current.ConversationMode;
        ConversationDot.Fill = (Brush)FindResource(on ? "SuccessOrbBrush" : "IdleOrbBrush");
        ConversationText.Foreground = (Brush)FindResource(on ? "TextBrush" : "MutedBrush");
        ConversationButton.ToolTip = (on ? "Conversation on: Link listens by itself, answers aloud and listens again"
            : "Conversation: Link listens by itself, answers aloud and listens again") +
            $" · {_settings.Current.Hotkeys.Conversation}";
        // Conversation speaks its answers even with voice replies off.
        UpdateReadAloudButton();
        if (on) ListenForNextPhrase();
        else StopHandsFree();
    }

    private void Conversation_OnClick(object sender, RoutedEventArgs e) => ToggleConversation();

    /// <summary>While Settings is open its microphone test needs the microphone, so the conversation waits.</summary>
    public void PauseConversation(bool paused)
    {
        _conversationPaused = paused;
        if (paused) StopHandsFree();
        else ScheduleListening();
    }

    /// <summary>
    /// Conversation mode: whenever nothing else goes on, Link listens for the next phrase by itself. Looked at once
    /// the current step is over, so a phrase that has just ended goes out before the microphone opens again.
    /// </summary>
    private void ScheduleListening() => Dispatcher.InvokeAsync(ListenForNextPhrase, DispatcherPriority.Background);

    private void ListenForNextPhrase()
    {
        // Typing or correcting in the prompt pauses the conversation: those words go by Enter.
        if (!_settings.Current.ConversationMode || _conversationPaused || _sending || _phraseInFlight || _typing ||
            _editing is not null || _voice.Phase != VoicePhase.Idle)
        {
            return;
        }

        try
        {
            var usedSelectedMicrophone = _voice.StartHandsFree();
            SetStatus(!usedSelectedMicrophone ? "Selected microphone is not connected — listening on the Windows default"
                : _voice.DraftNote is { } note ? $"Listening — {note}"
                : "Listening — just speak, a pause sends");
        }
        catch (Exception exception)
        {
            // A microphone that does not open would fail again on every turn.
            _settings.Current.ConversationMode = false;
            _settings.Save();
            ApplyConversation();
            SetError($"Conversation off: {exception.Message}");
        }
    }

    /// <summary>A request of Link's own buttons, or the mode going off, closes the hands-free microphone.</summary>
    private void StopHandsFree()
    {
        if (_voice.IsHandsFree) _voice.CancelListening();
    }

    private async void OnPhraseEnded()
    {
        // At once: the pause alone told Link the phrase is over, and the user should not wonder whether it heard.
        _cues.Play(SoundCue.Heard);
        // A phrase spoken hands-free is about the window in front, as if a hotkey had been pressed there.
        _currentApplication.LatchForeground();
        await FinishPhraseAsync(send: false);
    }

    /// <summary>
    /// Turns the phrase into text in the prompt. It goes out when <paramref name="send"/> asks for it, when Send was
    /// pressed while the user spoke, and always in conversation.
    /// </summary>
    private async Task FinishPhraseAsync(bool send)
    {
        // Until the phrase is on its way, the microphone does not open for the next one.
        _phraseInFlight = true;
        try
        {
            await TranscribeAndSendAsync(send);
        }
        finally
        {
            _phraseInFlight = false;
            ScheduleListening();
        }
    }

    private async Task TranscribeAndSendAsync(bool send)
    {
        var conversation = _settings.Current.ConversationMode;
        var askingAboutScreen = _askingAboutScreen;
        _askingAboutScreen = false;
        VoiceTranscript? transcript;
        try
        {
            transcript = await _voice.FinishListeningAsync();
        }
        catch (Exception exception)
        {
            if (conversation)
            {
                // In conversation nothing waits for the keyboard: the user simply says it again.
                SetPromptText(_draft.Commit(PromptBox.Text, string.Empty));
                SetError($"Didn't catch that — say it again. {exception.Message}");
                return;
            }

            // The live words may be all that is left of the dictation, so they stay as typed text.
            _draft.Keep();
            SetError(exception.Message);
            return;
        }

        // The final pass replaces the live words instead of adding a second version of them.
        SetPromptText(_draft.Commit(PromptBox.Text, transcript?.Text ?? string.Empty));
        if (askingAboutScreen)
        {
            if (transcript is null) SetStatus("No speech heard — hold the key while you ask");
            else await AskAboutScreenAsync();
            return;
        }

        if (transcript is null && !send)
        {
            SetStatus(conversation ? "Didn't catch that — say it again" : "No speech heard — hold the key while speaking");
            return;
        }

        if (transcript is not null) SetStatus("Voice transcribed");
        if (!send && !transcript!.SendRequested && !_settings.Current.ConversationMode) return;
        // Push-to-talk interrupted an answer that is still being written: the new question follows it.
        if (_sending && _settings.Current.ConversationMode)
        {
            _phraseWaiting = true;
            SetStatus("Voice transcribed — it goes out once the answer is in");
            return;
        }

        await SendAsync();
    }

    /// <summary>A request is over: the phrase that waited for it goes out, unless conversation went off or it was cleared.</summary>
    private async void SendWaitingPhrase()
    {
        if (!_phraseWaiting) return;
        _phraseWaiting = false;
        if (_settings.Current.ConversationMode && !string.IsNullOrWhiteSpace(PromptBox.Text)) await SendAsync();
    }

    private void OnVoiceDraftChanged(string words) => SetPromptText(_draft.Show(PromptBox.Text, words));

    /// <summary>Another voice stands in for the chosen one, e.g. after Gemini's free limit; the answer is not held up.</summary>
    public void ShowVoiceNotice(string message)
    {
        if (_voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        SetStatus(message);
    }

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

    /// <summary>
    /// A key typed into the prompt takes over from hands-free listening, unless a phrase is under way; the prompt is
    /// editable before the key reaches it. Clicking into the prompt alone keeps the conversation going.
    /// </summary>
    private void YieldToTyping(KeyEventArgs e)
    {
        if (!_voice.IsHandsFree || _voice.IsPhraseUnderWay) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Return or Key.Escape or Key.Tab or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _typing = true;
        StopHandsFree();
        SetStatus("Typing — conversation pauses until you send or leave the prompt");
    }

    private void PromptBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _typing = false;
        ScheduleListening();
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
        // Listening hands-free may last all day: the open microphone shows without redrawing the layered window.
        AnimateVoiceGlyph(start: listening && !_voice.IsHandsFree || speaking);
        // During a request push-to-talk waits for the answer, unless Link talks or in conversation: then it interrupts.
        VoiceButton.IsEnabled = _voice.CanStartListening || phase == VoicePhase.Listening;
        UpdateSendButton();
        UpdateReadAloudButton();
        if (phase == VoicePhase.Idle) ScheduleListening();

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
        // The overlay is a layered window: every frame is copied to system memory, so keep the pulse cheap.
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
