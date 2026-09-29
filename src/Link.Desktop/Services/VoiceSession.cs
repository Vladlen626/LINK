using System.Diagnostics;
using System.Globalization;
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
/// The one owner of voice state: the microphone, local transcription with its live draft, the request as far as
/// voice cares and the spoken answer, handed to the voice sentence by sentence while it streams. It has no UI; the
/// overlay renders <see cref="Phase"/>. Used on the UI thread, with the live settings that the Settings window
/// edits in place.
/// </summary>
public sealed class VoiceSession
{
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(300);
    // Detection notices speech a little late, so a hands-free phrase is transcribed from a moment before it.
    private static readonly TimeSpan PhrasePreroll = TimeSpan.FromSeconds(1);
    // Hands-free listening that hears nothing starts afresh now and then: the recording stays short.
    private static readonly TimeSpan QuietRestart = TimeSpan.FromMinutes(2);
    // Quick answers start within a few seconds; a cue before that would only talk over them.
    private static readonly TimeSpan WaitingCueDelay = TimeSpan.FromSeconds(6);
    private static readonly string[] RussianCues = ["Секунду.", "Думаю над ответом.", "Ещё немного."];
    private static readonly string[] EnglishCues = ["One moment.", "Thinking it over.", "Just a little longer."];
    private readonly AppSettings _settings;
    private readonly IAudioRecorder _recorder;
    private readonly IStreamingSpeechToText _streaming;
    private readonly ISpeechToText _speechToText;
    private readonly ITextToSpeech _textToSpeech;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private IStreamingTranscription? _live;
    // The thread listening started on: drafts, phrase ends and timers come back to it.
    private SynchronizationContext? _context;
    // Hands-free the utterance ends at a pause; push-to-talk ends it with the key.
    private bool _handsFree;
    private long _openedAt;
    // Link's processor time when the microphone opened, for link.log.
    private TimeSpan _processorAtOpen;
    // How much of the recording before the phrase is left out.
    private TimeSpan _skip;
    private ITimer? _quiet;
    private VoiceTextChunker? _answer;
    // Everything said of the current answer shares this token, so one cancel silences all of it.
    private CancellationTokenSource? _speaking;
    // Parts handed to the voice and not said yet; the last one's follower completes after all of them.
    private int _unsaid;
    private Task _speech = Task.CompletedTask;
    private Exception? _voiceFailure;
    private long _requestedAt;
    private long? _answerArrivedAt;
    private bool _answerHeard;
    private CancellationTokenSource? _cue;
    private string _promptLanguage = "en";
    // Under Auto the live draft needs a language before anything is said: the one spoken last, at first Windows'.
    private VoiceLanguage _spokenLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
        ? VoiceLanguage.Russian
        : VoiceLanguage.English;
    private int _nextCue;
    private bool _sendRequested;
    private bool _voicePrompt;
    private bool _cuesAllowed;

    public VoiceSession(
        AppSettings settings,
        IAudioRecorder recorder,
        IStreamingSpeechToText streaming,
        ISpeechToText speechToText,
        ITextToSpeech textToSpeech,
        TimeProvider time,
        Action<string>? log = null)
    {
        _settings = settings;
        _recorder = recorder;
        _streaming = streaming;
        _speechToText = speechToText;
        _textToSpeech = textToSpeech;
        _time = time;
        _log = log ?? (_ => { });
    }

    /// <summary>Raised after <see cref="Phase"/> changed, with the phase it left.</summary>
    public event Action<VoicePhase>? PhaseChanged;

    /// <summary>
    /// The words recognized so far while the microphone is open, raised on the thread that started listening.
    /// Each draft replaces the previous one, and the transcript replaces the last.
    /// </summary>
    public event Action<string>? DraftChanged;

    /// <summary>
    /// Hands-free: the user paused after a phrase, and <see cref="FinishListeningAsync"/> turns it into text. Raised
    /// on the thread that started listening.
    /// </summary>
    public event Action? PhraseEnded;

    public VoicePhase Phase { get; private set; }

    /// <summary>
    /// Why the current utterance misses something that is on, e.g. the live draft or the pause detection while its
    /// model downloads.
    /// </summary>
    public string? DraftNote { get; private set; }

    /// <summary>Push-to-talk starts when nothing else is going on, and interrupts Link while it talks.</summary>
    public bool CanStartListening => Phase is VoicePhase.Idle or VoicePhase.Speaking;

    /// <summary>The microphone listens hands-free: the phrase ends at a pause, not with a key.</summary>
    public bool IsHandsFree => Phase == VoicePhase.Listening && _handsFree;

    /// <summary>Hands-free, the user has begun a phrase that has not ended yet.</summary>
    public bool IsPhraseUnderWay => IsHandsFree && _live?.HeardSpeech == true;

    public bool IsSpeechModelReady => _speechToText.IsModelReady;

    /// <summary>What the final pass is waiting on for a better model, e.g. its download; null when nothing.</summary>
    public string? FinalNote => _speechToText.Note;

    /// <summary>
    /// Push-to-talk: silences an answer being read aloud and opens the microphone; the rest of that answer stays on
    /// screen only. False: the selected microphone is not connected, so the Windows default one listens.
    /// </summary>
    public bool StartListening()
    {
        if (!CanStartListening) throw new InvalidOperationException("Voice input is busy.");
        SilenceAnswer();
        return Open(handsFree: false);
    }

    /// <summary>
    /// Conversation: opens the microphone while nothing else goes on; the phrase ends at a pause and raises
    /// <see cref="PhraseEnded"/>. False: the selected microphone is not connected, so the Windows default one listens.
    /// </summary>
    public bool StartHandsFree()
    {
        if (Phase != VoicePhase.Idle) throw new InvalidOperationException("Voice input is busy.");
        return Open(handsFree: true);
    }

    /// <summary>Push-to-talk while listening hands-free: the phrase now ends when the key is let go.</summary>
    public void HoldListening()
    {
        if (!IsHandsFree) return;
        _handsFree = false;
        StopQuietTimer();
        // Before the key there was only the wait, unless a phrase has begun.
        if (_live?.HeardSpeech != true) _skip = Before(_time.GetElapsedTime(_openedAt));
    }

    /// <summary>Closes the microphone without a transcript, e.g. when conversation mode goes off; the live words go too.</summary>
    public void CancelListening()
    {
        if (Phase != VoicePhase.Listening) return;
        _log($"[Voice] {DescribeListening()}, closed without a phrase");
        DraftChanged?.Invoke(string.Empty);
        CloseMicrophone();
        SetPhase(VoicePhase.Idle);
    }

    /// <summary>
    /// Closes the microphone and transcribes locally; null when the recording was too short or held no speech.
    /// </summary>
    public async Task<VoiceTranscript?> FinishListeningAsync()
    {
        if (Phase != VoicePhase.Listening) throw new InvalidOperationException("Voice input is not listening.");
        StopQuietTimer();
        var listening = DescribeListening();
        var finishing = _time.GetTimestamp();
        SetPhase(VoicePhase.Finalizing);
        var live = _live!;
        _live = null;
        string? audioPath = null;
        try
        {
            AudioRecording recording;
            try
            {
                // Whisper hears the phrase without the wait before it: quicker, and nothing to invent words from.
                recording = _recorder.Stop(skip: _skip);
            }
            finally
            {
                // Stop returns after the last block, so the live side has heard everything by now.
                _recorder.SamplesCaptured -= live.Accept;
            }

            audioPath = recording.FilePath!;
            var heard = await live.FinishAsync();
            // Whisper invents phrases for silence and noise, so nothing is transcribed without a signal or speech.
            if (recording.Duration < MinimumSpeechDuration || recording.IsSilent || heard == false)
            {
                DeleteRecording(audioPath);
                audioPath = null;
                _log($"[Voice] {listening}, no speech");
                if (recording.IsSilent)
                {
                    throw new InvalidOperationException(
                        "No sound from the microphone. Check the input device in Settings → Microphone.");
                }

                return null;
            }

            var text = await _speechToText.TranscribeAsync(audioPath, _settings.VoiceLanguage);
            // With the pause before it, this is how long a hands-free phrase waited to go out.
            _log($"[Voice] {listening}, {recording.Duration.TotalSeconds:0.0} s of it transcribed " +
                 $"{_time.GetElapsedTime(finishing).TotalMilliseconds:0} ms after listening ended");
            // The transcript has one canonical home, the prompt: raw audio is never sent.
            DeleteRecording(audioPath);
            _voicePrompt = true;
            // Waiting cues speak the language of the question; the answer's own is not known yet.
            var russian = SpeechTextSanitizer.IsRussian(text);
            _promptLanguage = russian ? "ru" : "en";
            _spokenLanguage = russian ? VoiceLanguage.Russian : VoiceLanguage.English;
            return new VoiceTranscript(text, _sendRequested);
        }
        catch (Exception exception) when (audioPath is not null)
        {
            throw new InvalidOperationException($"{exception.Message} Recording was kept locally.", exception);
        }
        finally
        {
            live.Dispose();
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

    /// <summary>
    /// A request goes out. With Voice replies on, the answer to a dictated prompt is read aloud, and so is the answer
    /// to a typed one when the user asked for that too. In conversation every answer is.
    /// </summary>
    public void BeginRequest(string prompt)
    {
        SilenceAnswer();
        // A typed question speaks its own language; a dictated one was judged on its transcript.
        var language = _voicePrompt ? _promptLanguage : SpeechTextSanitizer.SpeaksRussian(prompt) ? "ru" : "en";
        var conversation = _settings.ConversationMode;
        _answer = conversation || _settings.VoiceReplies && (_voicePrompt || _settings.ReadTypedAnswers)
            ? new VoiceTextChunker(language)
            : null;
        _cuesAllowed = _voicePrompt || conversation;
        _requestedAt = Environment.TickCount64;
        _answerArrivedAt = null;
        _answerHeard = false;
        // A voice failure of an earlier answer is not this one's.
        _speech = Task.CompletedTask;
        _voiceFailure = null;
        // The voice gets ready while the chat thinks: a warm connection or a loaded model saves the first words' wait.
        if (_answer is not null) _textToSpeech.Prepare(SpeechOptions.From(_settings, language));
        SetPhase(VoicePhase.Sending);
    }

    /// <summary>
    /// Whether <see cref="ReadAloudAsync"/> can start now: not while the user speaks or a request is under way.
    /// Listening hands-free for the next phrase gives way to it.
    /// </summary>
    public bool CanReadAloud => (_settings.VoiceReplies || _settings.ConversationMode) &&
                                (Phase is VoicePhase.Idle or VoicePhase.Speaking || IsHandsFree && !IsPhraseUnderWay);

    /// <summary>
    /// The Listen button: reads an answer on screen from its start, like a reply that has just arrived. Completes
    /// once it has been said or silenced; throws when the voice failed.
    /// </summary>
    public async Task ReadAloudAsync(string answer)
    {
        if (!CanReadAloud) return;
        CancelListening();
        SilenceAnswer();
        _speech = Task.CompletedTask;
        _voiceFailure = null;
        var language = SpeechTextSanitizer.SpeaksRussian(answer) ? "ru" : "en";
        if (new VoiceTextChunker(language).Finish(answer) is { } speech) Say(speech);
        await _speech;
        ThrowVoiceFailure();
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
        // A typed question gets no spoken "one moment": the user is looking at the screen anyway.
        if (progress.State == ChatProgressState.Waiting && _cue is null && _cuesAllowed)
        {
            _cue = new CancellationTokenSource();
            _ = SayWaitingCueAsync(_cue.Token);
        }
        else if (progress.State == ChatProgressState.Receiving)
        {
            // The answer itself shows that the chat is working.
            _cue?.Cancel();
            if (progress.Text.Length > 0) _answerArrivedAt ??= Environment.TickCount64;
            if (_answer.Next(progress.Text) is { } sentences) SayAnswer(sentences);
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
        _answerArrivedAt ??= Environment.TickCount64;
        if (reply?.Finish(answer) is { } rest) SayAnswer(rest);
        // Nothing more to say: voice is done with this request unless the user has taken the floor meanwhile.
        else if (_speaking is null && Phase is not (VoicePhase.Listening or VoicePhase.Finalizing)) SetPhase(VoicePhase.Idle);
        await _speech;
        ThrowVoiceFailure();
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

    /// <summary>Opens the microphone with recognition listening along; the phase says Listening once it is open.</summary>
    private bool Open(bool handsFree)
    {
        IStreamingTranscription? live = null;
        try
        {
            // Drafts, phrase ends and timers come from other threads; this session belongs to the one it runs on.
            _context = SynchronizationContext.Current;
            live = StartLive(handsFree);
            var usedSelectedMicrophone = _recorder.Start(_settings.MicrophoneDeviceId);
            _handsFree = handsFree;
            _openedAt = _time.GetTimestamp();
            _processorAtOpen = ProcessorTime();
            _skip = TimeSpan.Zero;
            if (handsFree)
            {
                var quiet = live;
                _quiet = _time.CreateTimer(_ => Post(() => RestartQuiet(quiet)), null, QuietRestart, Timeout.InfiniteTimeSpan);
            }

            SetPhase(VoicePhase.Listening);
            return usedSelectedMicrophone;
        }
        catch
        {
            if (live is not null)
            {
                _recorder.SamplesCaptured -= live.Accept;
                live.Dispose();
            }

            _live = null;
            SetPhase(VoicePhase.Idle);
            throw;
        }
    }

    /// <summary>Starts recognition that listens along with the microphone and hands its drafts to this thread.</summary>
    private IStreamingTranscription StartLive(bool handsFree)
    {
        var live = _streaming.Start(DraftLanguage(), _settings.LiveTranscription, handsFree);
        live.DraftChanged += draft => Post(() => ShowDraft(live, draft));
        live.PhraseEnded += start => Post(() => EndPhrase(live, start));
        _live = live;
        DraftNote = live.Note;
        // The one microphone session feeds the recording and the live recognition alike.
        _recorder.SamplesCaptured += live.Accept;
        return live;
    }

    private void EndPhrase(IStreamingTranscription live, TimeSpan start)
    {
        // The key may have taken the phrase over, or the microphone closed meanwhile.
        if (live != _live || !IsHandsFree) return;
        _skip = Before(start);
        PhraseEnded?.Invoke();
    }

    /// <summary>Hands-free listening that has heard nothing for a while starts afresh; a phrase under way ends first.</summary>
    private void RestartQuiet(IStreamingTranscription live)
    {
        if (live != _live || !IsHandsFree || live.HeardSpeech) return;
        _log($"[Voice] {DescribeListening()}, nothing heard: listening afresh");
        // Words the draft made of the noise go with it.
        DraftChanged?.Invoke(string.Empty);
        CloseMicrophone();
        try
        {
            Open(handsFree: true);
        }
        catch (Exception exception)
        {
            // Idle now: the conversation's next attempt to listen reports the microphone.
            _log($"[Voice] hands-free listening could not start afresh: {exception.Message}");
        }
    }

    /// <summary>Closes the microphone and the recognition listening along, keeping nothing.</summary>
    private void CloseMicrophone()
    {
        StopQuietTimer();
        var live = _live!;
        _live = null;
        _recorder.SamplesCaptured -= live.Accept;
        _recorder.Cancel();
        live.Dispose();
    }

    private void StopQuietTimer()
    {
        _quiet?.Dispose();
        _quiet = null;
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    /// <summary>Where the recording is kept from for speech that begins at <paramref name="speech"/>.</summary>
    private static TimeSpan Before(TimeSpan speech) => speech > PhrasePreroll ? speech - PhrasePreroll : TimeSpan.Zero;

    /// <summary>
    /// For link.log: how long the microphone listened and how busy Link was meanwhile, so that listening hands-free
    /// can be compared with holding the key, e.g. "listened 42.0 s hands-free, Link CPU 1.9% of one core".
    /// </summary>
    private string DescribeListening()
    {
        var listened = _time.GetElapsedTime(_openedAt);
        var busy = listened > TimeSpan.Zero ? (ProcessorTime() - _processorAtOpen) / listened * 100 : 0;
        return $"listened {listened.TotalSeconds:0.0} s {(_handsFree ? "hands-free" : "with the key")}, " +
               $"Link CPU {busy:0.0}% of one core";
    }

    private static TimeSpan ProcessorTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    /// <summary>The live draft's language: the chosen one, or under Auto the language of the last dictation.</summary>
    private VoiceLanguage DraftLanguage() =>
        _settings.VoiceLanguage == VoiceLanguage.Auto ? _spokenLanguage : _settings.VoiceLanguage;

    private void ShowDraft(IStreamingTranscription live, string draft)
    {
        // A draft that arrives after the key was released would overwrite the transcript.
        if (live == _live && Phase == VoicePhase.Listening) DraftChanged?.Invoke(draft);
    }

    private void SilenceAnswer()
    {
        _answer = null;
        _cue?.Cancel();
        _cue = null;
        // Forgotten before cancelling, so the silenced speech cannot move the phase when it winds down.
        var speaking = _speaking;
        _speaking = null;
        _unsaid = 0;
        // The voice drops what waits, stops what is being made and silences what plays, all at once.
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

    private void ThrowVoiceFailure()
    {
        if (_voiceFailure is not { } failure) return;
        _voiceFailure = null;
        throw failure;
    }

    private void SayAnswer(SpokenAnswer part)
    {
        if (_answerArrivedAt is { } arrived && !_answerHeard)
        {
            _answerHeard = true;
            _log($"[TTS] first words of the answer ready {Environment.TickCount64 - arrived} ms after its text began to arrive, " +
                 $"{Environment.TickCount64 - _requestedAt} ms after the request began");
        }

        Say(part);
    }

    /// <summary>
    /// Hands the part to the voice at once: it is said after the parts before it, and made while they still play.
    /// </summary>
    private void Say(SpokenAnswer part)
    {
        var speaking = _speaking ??= new CancellationTokenSource();
        _unsaid++;
        SetPhase(VoicePhase.Speaking);
        _speech = FollowAsync(
            _textToSpeech.SpeakAsync(part.Text, SpeechOptions.From(_settings, part.Language), speaking.Token),
            speaking);
    }

    /// <summary>Waits for a part to be said; once the last one is, the floor goes back to the chat or the user.</summary>
    private async Task FollowAsync(Task said, CancellationTokenSource speaking)
    {
        try
        {
            await said;
        }
        catch (OperationCanceledException)
        {
            // Silenced: whoever silenced it has already moved the phase on.
        }
        catch (Exception exception) when (_speaking == speaking)
        {
            // The voice failed: the rest of the answer stays on screen, and completing it reports the error.
            _voiceFailure ??= exception;
            _answer = null;
            speaking.Cancel();
        }
        catch (Exception)
        {
            // A failure of an answer that was silenced meanwhile is nobody's concern.
        }
        finally
        {
            if (_speaking == speaking && --_unsaid == 0)
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
