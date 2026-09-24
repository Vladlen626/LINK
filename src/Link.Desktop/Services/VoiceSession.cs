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

    /// <summary>The chat works on the answer and Link has nothing to say yet.</summary>
    Waiting,

    /// <summary>
    /// Link talks: a short waiting cue, or the answer. Once the answer is being read, the pauses while more of it
    /// streams count too, so push-to-talk and Esc can interrupt until it has been said to the end.
    /// </summary>
    Speaking
}

/// <summary>A finished utterance and whether Send was pressed while it was spoken or transcribed.</summary>
public sealed record VoiceTranscript(string Text, bool SendRequested);

/// <summary>
/// The one owner of voice state: the microphone, local transcription, the request as far as voice cares
/// and the spoken answer, read sentence by sentence while it streams. It has no UI; the overlay renders
/// <see cref="Phase"/>. Used on the UI thread, with the live settings that the Settings window edits in place.
/// </summary>
public sealed class VoiceSession
{
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(300);
    // Quick answers start within a few seconds; a cue before that would only talk over them.
    private static readonly TimeSpan WaitingCueDelay = TimeSpan.FromSeconds(6);
    private static readonly string[] RussianCues = ["Секунду.", "Думаю над ответом.", "Ещё немного."];
    private static readonly string[] EnglishCues = ["One moment.", "Thinking it over.", "Just a little longer."];
    private readonly AppSettings _settings;
    private readonly IAudioRecorder _recorder;
    private readonly ISpeechToText _speechToText;
    private readonly ITextToSpeech _textToSpeech;
    private readonly TimeProvider _time;
    // Said in order, one at a time; parts that queue up while another is said go out together.
    private readonly List<SpokenAnswer> _unsaid = [];
    private SpokenAnswerStream? _answer;
    private Task _speech = Task.CompletedTask;
    private CancellationTokenSource? _speaking;
    private CancellationTokenSource? _cue;
    private string _promptLanguage = "en";
    private int _nextCue;
    private bool _sendRequested;
    private bool _voicePrompt;

    public VoiceSession(
        AppSettings settings,
        IAudioRecorder recorder,
        ISpeechToText speechToText,
        ITextToSpeech textToSpeech,
        TimeProvider time)
    {
        _settings = settings;
        _recorder = recorder;
        _speechToText = speechToText;
        _textToSpeech = textToSpeech;
        _time = time;
    }

    /// <summary>Raised after <see cref="Phase"/> changed, with the phase it left.</summary>
    public event Action<VoicePhase>? PhaseChanged;

    public VoicePhase Phase { get; private set; }

    /// <summary>Push-to-talk starts when nothing else is going on, and interrupts Link while it talks.</summary>
    public bool CanStartListening => Phase is VoicePhase.Idle or VoicePhase.Speaking;

    public bool IsSpeechModelReady => _speechToText.IsModelReady;

    /// <summary>
    /// Silences an answer being read aloud and opens the microphone; the rest of that answer stays on screen only.
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
            // Waiting cues speak the language of the question; the answer's own is not known yet.
            _promptLanguage = SpokenAnswer.IsRussian(text) ? "ru" : "en";
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
        _answer = _voicePrompt && _settings.VoiceReplies ? new SpokenAnswerStream() : null;
        // A voice failure of an earlier answer is not this one's.
        _speech = Task.CompletedTask;
        SetPhase(VoicePhase.Sending);
    }

    /// <summary>
    /// The chat's progress. A long wait gets one short cue; a streaming answer is read as its sentences complete.
    /// </summary>
    public void ReportProgress(ChatProgress progress)
    {
        if (Phase == VoicePhase.Sending && progress.State is (ChatProgressState.Waiting or ChatProgressState.Receiving))
        {
            SetPhase(VoicePhase.Waiting);
        }

        if (_answer is null) return;
        if (progress.State == ChatProgressState.Waiting && _cue is null)
        {
            _cue = new CancellationTokenSource();
            _ = SayWaitingCueAsync(_cue.Token);
        }
        else if (progress.State == ChatProgressState.Receiving)
        {
            // The answer itself shows that the chat is working.
            _cue?.Cancel();
            if (_answer.Next(progress.Text) is { } sentences) Say(sentences);
        }
    }

    /// <summary>
    /// The whole answer arrived: what was not said of it yet is read aloud. Completes once that has been said,
    /// or at once when the answer is not read aloud; throws when the voice failed.
    /// </summary>
    public async Task CompleteRequestAsync(string answer)
    {
        var reply = _answer;
        _answer = null;
        _cue?.Cancel();
        if (reply?.Finish(answer) is { } rest) Say(rest);
        // Nothing more to say: voice is done with this request unless the user has taken the floor meanwhile.
        else if (_speaking is null && Phase is not (VoicePhase.Listening or VoicePhase.Finalizing)) SetPhase(VoicePhase.Idle);
        await _speech;
    }

    /// <summary>
    /// The request is over. After a failure or Stop an answer still arriving is not read any further;
    /// a complete one keeps being read to its end.
    /// </summary>
    public void EndRequest()
    {
        if (_answer is null && Phase is not (VoicePhase.Sending or VoicePhase.Waiting)) return;
        SilenceAnswer();
        SetPhase(VoicePhase.Idle);
    }

    /// <summary>Esc, Stop or the Send hotkey while Link talks: the rest of the answer stays on screen only.</summary>
    public void StopSpeaking()
    {
        if (Phase != VoicePhase.Speaking) return;
        SilenceAnswer();
        SetPhase(VoicePhase.Idle);
    }

    private void SilenceAnswer()
    {
        _answer = null;
        _unsaid.Clear();
        _cue?.Cancel();
        _cue = null;
        // Forgotten before cancelling, so the silenced speech cannot move the phase when it winds down.
        var speaking = _speaking;
        _speaking = null;
        speaking?.Cancel();
    }

    private async Task SayWaitingCueAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(WaitingCueDelay, _time, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The answer may have started just as the delay ran out.
        if (cancellation.IsCancellationRequested) return;
        var cues = _promptLanguage == "ru" ? RussianCues : EnglishCues;
        Say(new SpokenAnswer(cues[_nextCue++ % cues.Length], _promptLanguage));
    }

    private void Say(SpokenAnswer part)
    {
        _unsaid.Add(part);
        if (_speaking is null) _speech = SpeakAsync();
    }

    /// <summary>Says the queued parts one after another until none is left or Link is silenced.</summary>
    private async Task SpeakAsync()
    {
        var speaking = new CancellationTokenSource();
        _speaking = speaking;
        SetPhase(VoicePhase.Speaking);
        try
        {
            while (_speaking == speaking && _unsaid.Count > 0)
            {
                var language = _unsaid[0].Language;
                var parts = _unsaid.TakeWhile(part => part.Language == language).ToArray();
                _unsaid.RemoveRange(0, parts.Length);
                await _textToSpeech.SpeakAsync(
                    string.Join(' ', parts.Select(part => part.Text)),
                    new SpeechOptions(_settings.VoiceReplyVoice, language, _settings.VoiceReplySpeed),
                    speaking.Token);
            }
        }
        catch (OperationCanceledException) when (speaking.IsCancellationRequested)
        {
            // Silenced: whoever silenced it has already moved the phase on.
        }
        catch when (_speaking == speaking)
        {
            // The voice failed: the rest of the answer stays on screen, and completing it reports the error.
            _answer = null;
            _unsaid.Clear();
            throw;
        }
        finally
        {
            speaking.Dispose();
            if (_speaking == speaking)
            {
                _speaking = null;
                // Between the sentences of an answer that still streams, the floor stays Link's.
                SetPhase(_answer is null ? VoicePhase.Idle
                    : _answer.HasStarted ? VoicePhase.Speaking
                    : VoicePhase.Waiting);
            }
        }
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
