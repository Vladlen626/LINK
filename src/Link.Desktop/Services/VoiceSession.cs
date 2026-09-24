using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What a voice interaction is doing now; the overlay and its sounds follow this one value.</summary>
public enum VoicePhase
{
    Idle,

    /// <summary>The microphone is open.</summary>
    Listening,

    /// <summary>The recording is being turned into text.</summary>
    Finalizing,

    Sending,

    /// <summary>The chat has the prompt and its answer is not being read aloud yet.</summary>
    Waiting,

    Speaking
}

/// <summary>A finished utterance and whether Send was pressed while it was spoken or transcribed.</summary>
public sealed record VoiceTranscript(string Text, bool SendRequested);

/// <summary>
/// The one owner of voice state: the microphone, local transcription, the request as far as voice cares
/// and the spoken answer. It has no UI; the overlay renders <see cref="Phase"/>. Used on the UI thread,
/// with the live settings that the Settings window edits in place.
/// </summary>
public sealed class VoiceSession
{
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(300);
    private readonly AppSettings _settings;
    private readonly IAudioRecorder _recorder;
    private readonly ISpeechToText _speechToText;
    private readonly ITextToSpeech _textToSpeech;
    private CancellationTokenSource? _speaking;
    private bool _sendRequested;
    private bool _voicePrompt;
    private bool _speakAnswer;

    public VoiceSession(
        AppSettings settings,
        IAudioRecorder recorder,
        ISpeechToText speechToText,
        ITextToSpeech textToSpeech)
    {
        _settings = settings;
        _recorder = recorder;
        _speechToText = speechToText;
        _textToSpeech = textToSpeech;
    }

    /// <summary>Raised after <see cref="Phase"/> changed, with the phase it left.</summary>
    public event Action<VoicePhase>? PhaseChanged;

    public VoicePhase Phase { get; private set; }

    /// <summary>Push-to-talk starts when nothing else is going on, and interrupts an answer being read aloud.</summary>
    public bool CanStartListening => Phase is VoicePhase.Idle or VoicePhase.Speaking;

    public bool IsSpeechModelReady => _speechToText.IsModelReady;

    /// <summary>
    /// Silences an answer being read aloud and opens the microphone.
    /// False: the selected microphone is not connected, so the Windows default one listens.
    /// </summary>
    public bool StartListening()
    {
        if (!CanStartListening) throw new InvalidOperationException("Voice input is busy.");
        SilenceAnswer();
        try
        {
            var usedSelectedMicrophone = _recorder.Start(_settings.MicrophoneDeviceId);
            SetPhase(VoicePhase.Listening);
            return usedSelectedMicrophone;
        }
        catch
        {
            SetPhase(VoicePhase.Idle);
            throw;
        }
    }

    /// <summary>Closes the microphone and transcribes locally; null when the recording was too short to be speech.</summary>
    public async Task<VoiceTranscript?> FinishListeningAsync()
    {
        if (Phase != VoicePhase.Listening) throw new InvalidOperationException("Voice input is not listening.");
        SetPhase(VoicePhase.Finalizing);
        string? audioPath = null;
        try
        {
            var recording = _recorder.Stop();
            audioPath = recording.FilePath!;
            if (recording.Duration < MinimumSpeechDuration || recording.IsSilent)
            {
                // Whisper invents phrases for silence, so nothing is transcribed without a signal.
                DeleteRecording(audioPath);
                audioPath = null;
                if (recording.IsSilent)
                {
                    throw new InvalidOperationException(
                        "No sound from the microphone. Check the input device in Settings → Microphone.");
                }

                return null;
            }

            var text = await _speechToText.TranscribeAsync(audioPath, _settings.VoiceLanguage);
            // The transcript has one canonical home, the prompt: raw audio is never sent.
            DeleteRecording(audioPath);
            _voicePrompt = true;
            return new VoiceTranscript(text, _sendRequested);
        }
        catch (Exception exception) when (audioPath is not null)
        {
            throw new InvalidOperationException($"{exception.Message} Recording was kept locally.", exception);
        }
        finally
        {
            _sendRequested = false;
            SetPhase(VoicePhase.Idle);
        }
    }

    /// <summary>Send was pressed while the user speaks: the transcript goes out as soon as it is ready.</summary>
    public void SendWhenTranscribed()
    {
        if (Phase is VoicePhase.Listening or VoicePhase.Finalizing) _sendRequested = true;
    }

    /// <summary>The prompt no longer holds a transcript, so its answer is not read aloud.</summary>
    public void ForgetVoicePrompt() => _voicePrompt = false;

    /// <summary>A request goes out. The answer to a dictated prompt is read aloud when Voice replies is on.</summary>
    public void BeginRequest()
    {
        SilenceAnswer();
        _speakAnswer = _voicePrompt && _settings.VoiceReplies;
        SetPhase(VoicePhase.Sending);
    }

    public void ReportProgress(ChatProgress progress)
    {
        // The answer is read aloud once it is complete, so while it streams voice is still waiting.
        if (Phase == VoicePhase.Sending && progress.State is (ChatProgressState.Waiting or ChatProgressState.Receiving))
        {
            SetPhase(VoicePhase.Waiting);
        }
    }

    /// <summary>The answer arrived. Completes once its opening has been read aloud, or at once when it is not.</summary>
    public async Task CompleteRequestAsync(string answer)
    {
        if (Phase is not (VoicePhase.Sending or VoicePhase.Waiting)) return;
        if (!_speakAnswer)
        {
            SetPhase(VoicePhase.Idle);
            return;
        }

        var spoken = SpokenAnswer.From(answer);
        var speaking = new CancellationTokenSource();
        _speaking = speaking;
        SetPhase(VoicePhase.Speaking);
        try
        {
            await _textToSpeech.SpeakAsync(
                spoken.Text,
                new SpeechOptions(_settings.VoiceReplyVoice, spoken.Language, _settings.VoiceReplySpeed),
                speaking.Token);
        }
        catch (OperationCanceledException) when (speaking.IsCancellationRequested)
        {
            // Interrupted: whoever silenced the answer has already moved the phase on.
        }
        finally
        {
            speaking.Dispose();
            if (_speaking == speaking)
            {
                _speaking = null;
                SetPhase(VoicePhase.Idle);
            }
        }
    }

    /// <summary>The request failed or was cancelled.</summary>
    public void EndRequest()
    {
        if (Phase is VoicePhase.Sending or VoicePhase.Waiting) SetPhase(VoicePhase.Idle);
    }

    /// <summary>Esc, Stop or the Send hotkey while an answer is read aloud.</summary>
    public void StopSpeaking()
    {
        if (Phase != VoicePhase.Speaking) return;
        SilenceAnswer();
        SetPhase(VoicePhase.Idle);
    }

    private void SilenceAnswer()
    {
        // Forgotten before cancelling, so the silenced answer cannot move the phase when it winds down.
        var speaking = _speaking;
        _speaking = null;
        speaking?.Cancel();
    }

    private void SetPhase(VoicePhase phase)
    {
        if (phase == Phase) return;
        var previous = Phase;
        Phase = phase;
        PhaseChanged?.Invoke(previous);
    }

    private static void DeleteRecording(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The recorder keeps only the latest recordings, so a file Windows still holds is pruned later.
        }
    }
}
