using System.Threading.Channels;
using Link.Desktop.Models;
using SherpaOnnx;

namespace Link.Desktop.Services;

/// <summary>
/// sherpa-onnx on the CPU: Silero VAD tells whether an utterance holds speech and, hands-free, where a phrase ends;
/// a streaming Zipformer writes the live draft. The recognizer takes about two seconds to load, so it stays loaded between utterances and goes after
/// a while without voice input; audio that arrives while it loads waits and is caught up at once.
/// </summary>
public sealed class SherpaStreamingSpeechToText : IStreamingSpeechToText, IDisposable
{
    private const int SampleRate = 16000;
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);
    // The fading end of the speech still counts as speech, and blocks come every 50 ms: a phrase is heard to end
    // about 0.1 s after this, 0.65 s after the last sound (measured on Link's local voice).
    private static readonly TimeSpan PhrasePause = TimeSpan.FromMilliseconds(550);
    private readonly SpeechModelManager _models;
    private readonly object _gate = new();
    private readonly Timer _idle;
    private SpeechModel? _recognizerModel;
    private Task<OnlineRecognizer>? _recognizer;
    private int _recognizerUsers;
    private bool _disposed;

    public SherpaStreamingSpeechToText(SpeechModelManager models)
    {
        _models = models;
        _idle = new Timer(_ => ReleaseIdleRecognizer());
    }

    public IStreamingTranscription Start(VoiceLanguage language, bool draft, bool handsFree)
    {
        // Speech detection works unseen, so while its small model downloads nothing needs saying, unless hands-free
        // listening needs it to hear where a phrase ends.
        var detectorStatus = _models.Prepare(SpeechModel.SileroVad);
        var detector = detectorStatus is null ? _models.PathOf(SpeechModel.SileroVad, "silero_vad.onnx") : null;
        var detectorNote = handsFree && detectorStatus is not null ? $"pause detection {detectorStatus}" : null;
        if (!draft) return new Transcription(this, detector, handsFree, null, detectorNote);

        var model = language == VoiceLanguage.Russian ? SpeechModel.StreamingRussian : SpeechModel.StreamingEnglish;
        return _models.Prepare(model) is { } status
            ? new Transcription(this, detector, handsFree, null, detectorNote ?? $"live text {status}")
            : new Transcription(this, detector, handsFree, AcquireRecognizer(model), detectorNote);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idle.Dispose();
            if (_recognizerUsers == 0) UnloadRecognizer();
        }
    }

    /// <summary>The loaded recognizer, or the one now loading; null while another utterance still holds a different one.</summary>
    private Task<OnlineRecognizer>? AcquireRecognizer(SpeechModel model)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_recognizerModel != model || _recognizer is null || _recognizer.IsFaulted)
            {
                if (_recognizerUsers > 0) return null;
                UnloadRecognizer();
                _recognizerModel = model;
                _recognizer = Task.Run(() => LoadRecognizer(model));
            }

            _recognizerUsers++;
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            return _recognizer;
        }
    }

    private void ReturnRecognizer()
    {
        lock (_gate)
        {
            // An all-day tray app gives the memory back when voice input rests.
            if (--_recognizerUsers == 0 && !_disposed) _idle.Change(IdleRelease, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReleaseIdleRecognizer()
    {
        lock (_gate)
        {
            if (_recognizerUsers == 0) UnloadRecognizer();
        }
    }

    private void UnloadRecognizer()
    {
        var recognizer = _recognizer;
        _recognizer = null;
        _recognizerModel = null;
        // One that is still loading goes as soon as it is there.
        recognizer?.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Dispose();
        }, TaskScheduler.Default);
    }

    private OnlineRecognizer LoadRecognizer(SpeechModel model)
    {
        var config = new OnlineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = _models.PathOf(model, "encoder.onnx");
        config.ModelConfig.Transducer.Decoder = _models.PathOf(model, "decoder.onnx");
        config.ModelConfig.Transducer.Joiner = _models.PathOf(model, "joiner.onnx");
        config.ModelConfig.Tokens = _models.PathOf(model, "tokens.txt");
        // One core decodes about thirty times faster than people speak.
        config.ModelConfig.NumThreads = 1;
        // A pause closes a segment, so a long dictation never grows one hypothesis without end.
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4F;
        config.Rule2MinTrailingSilence = 1.2F;
        config.Rule3MinUtteranceLength = 20F;
        return new OnlineRecognizer(config);
    }

    private static VoiceActivityDetector CreateDetector(string model, bool handsFree)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = model;
        if (handsFree)
        {
            // An open microphone acts on clear speech only. A pause ends the phrase: it adds straight to the wait
            // for the answer, and pauses longer than about 0.8 s are already felt.
            config.SileroVad.Threshold = 0.5F;
            config.SileroVad.MinSilenceDuration = (float)PhrasePause.TotalSeconds;
            // By default a phrase longer than five seconds is cut at its next short breath.
            config.SileroVad.MaxSpeechDuration = 60F;
        }
        else
        {
            // It only decides whether Whisper runs at all, so doubtful audio counts as speech.
            config.SileroVad.Threshold = 0.3F;
        }

        return new VoiceActivityDetector(config, bufferSizeInSeconds: 10);
    }

    /// <summary>One utterance, worked on by one task that owns the native streams from start to end.</summary>
    private sealed class Transcription : IStreamingTranscription
    {
        private readonly SherpaStreamingSpeechToText _owner;
        private readonly string? _detectorModel;
        private readonly bool _handsFree;
        private readonly Task<OnlineRecognizer>? _recognizer;
        private readonly Channel<float[]> _samples =
            Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource _abandoned = new();
        private readonly Task<bool?> _work;
        private volatile bool _finishing;
        private volatile bool _heardSpeech;
        private bool _phraseEnded;
        // Words of the segments the recognizer has closed, and the whole draft last raised.
        private string _closed = string.Empty;
        private string _draft = string.Empty;

        public Transcription(
            SherpaStreamingSpeechToText owner,
            string? detectorModel,
            bool handsFree,
            Task<OnlineRecognizer>? recognizer,
            string? note)
        {
            _owner = owner;
            _detectorModel = detectorModel;
            _handsFree = handsFree;
            _recognizer = recognizer;
            Note = note;
            _work = Task.Run(ListenAsync);
        }

        public event Action<string>? DraftChanged;

        public event Action<TimeSpan>? PhraseEnded;

        public string? Note { get; }

        public bool HeardSpeech => _heardSpeech;

        public void Accept(float[] samples) => _samples.Writer.TryWrite(samples);

        public Task<bool?> FinishAsync()
        {
            // The draft stops with the key: the final pass replaces it anyway.
            _finishing = true;
            _samples.Writer.TryComplete();
            return _work;
        }

        public void Dispose()
        {
            _samples.Writer.TryComplete();
            _abandoned.Cancel();
        }

        private async Task<bool?> ListenAsync()
        {
            VoiceActivityDetector? detector = null;
            OnlineStream? stream = null;
            // Samples that arrive while the recognizer loads.
            var waiting = new List<float[]>();
            try
            {
                bool? heard = null;
                if (_detectorModel is not null)
                {
                    detector = CreateDetector(_detectorModel, _handsFree);
                    heard = false;
                }

                await foreach (var samples in _samples.Reader.ReadAllAsync(_abandoned.Token))
                {
                    if (detector is not null)
                    {
                        detector.AcceptWaveform(samples);
                        if (detector.IsSpeechDetected()) heard = true;
                        // Only whether there was speech matters, and hands-free where the first phrase began.
                        int? phraseStart = null;
                        while (!detector.IsEmpty())
                        {
                            heard = true;
                            if (_handsFree && !_phraseEnded) phraseStart ??= detector.Front().Start;
                            detector.Pop();
                        }

                        if (heard == true) _heardSpeech = true;
                        // A closed segment is speech followed by the pause: the phrase is over.
                        if (phraseStart is { } start)
                        {
                            _phraseEnded = true;
                            PhraseEnded?.Invoke(TimeSpan.FromSeconds(start / (double)SampleRate));
                        }
                    }

                    if (_recognizer is null || _recognizer.IsFaulted || _finishing) continue;
                    if (stream is null)
                    {
                        waiting.Add(samples);
                        if (!_recognizer.IsCompletedSuccessfully) continue;
                        stream = _recognizer.Result.CreateStream();
                        foreach (var block in waiting) stream.AcceptWaveform(SampleRate, block);
                        waiting.Clear();
                    }
                    else
                    {
                        stream.AcceptWaveform(SampleRate, samples);
                    }

                    UpdateDraft(_recognizer.Result, stream);
                }

                if (detector is not null)
                {
                    detector.Flush();
                    if (!detector.IsEmpty()) heard = true;
                }

                // Words the detector missed still count: a final pass too many beats a lost sentence.
                return _draft.Length > 0 ? true : heard;
            }
            catch (Exception)
            {
                // Listening along is a preview and must never break dictation: without it, Whisper still runs.
                _samples.Writer.TryComplete();
                return null;
            }
            finally
            {
                stream?.Dispose();
                detector?.Dispose();
                if (_recognizer is not null) _owner.ReturnRecognizer();
            }
        }

        private void UpdateDraft(OnlineRecognizer recognizer, OnlineStream stream)
        {
            while (recognizer.IsReady(stream)) recognizer.Decode(stream);
            var words = recognizer.GetResult(stream).Text.Trim();
            if (recognizer.IsEndpoint(stream))
            {
                _closed = Join(_closed, words);
                words = string.Empty;
                recognizer.Reset(stream);
            }

            // The English model writes capitals only; a lower-case draft reads the same in both languages.
            var draft = Join(_closed, words).ToLowerInvariant();
            if (draft == _draft) return;
            _draft = draft;
            DraftChanged?.Invoke(draft);
        }

        private static string Join(string first, string second) =>
            first.Length == 0 ? second : second.Length == 0 ? first : $"{first} {second}";
    }
}
