using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

internal static class VoiceSessionTests
{
    private const string Answer = "Готово, всё настроено как надо. Второе предложение.\n\n```\ncode\n```";
    private const string Opening = "Готово, всё настроено как надо.";
    private const string Rest = "Второе предложение. Код — на экране.";

    public static async Task RunAsync()
    {
        var rig = new Rig();
        rig.Settings.VoiceReplyProvider = TtsProvider.Local;
        rig.Settings.GeminiVoice = "Sulafat";
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        Program.Equal("request goes out", rig.Session.Phase, VoicePhase.Sending);
        Program.Equal("the voice gets ready while the chat thinks", rig.Speech.Prepared, 1);
        Program.Equal("push-to-talk waits for the request", rig.Session.CanStartListening, false);
        rig.Session.ReportProgress(Waiting());
        Program.Equal("chat accepted the prompt", rig.Session.Phase, VoicePhase.Waiting);
        var answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("dictated prompt is answered aloud", rig.Session.Phase, VoicePhase.Speaking);
        Program.Equal("the whole answer is read, code only pointed to", rig.Speech.Said, $"{Opening} {Rest}");
        Program.Equal("the chosen voices go with it", (rig.Speech.Options[0].Provider, rig.Speech.Options[0].GeminiVoice,
            rig.Speech.Options[0].Language), (TtsProvider.Local, "Sulafat", "ru"));
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("idle after speaking", rig.Session.Phase, VoicePhase.Idle);

        rig = new Rig();
        rig.Session.BeginRequest("Как дела?");
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("typed prompt stays quiet", (rig.Speech.Said, rig.Session.Phase, rig.Speech.Prepared),
            (string.Empty, VoicePhase.Idle, 0));
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        rig.Settings.VoiceReplies = false;
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("voice replies off stays quiet, not even prepared", (rig.Speech.Said, rig.Speech.Prepared), (string.Empty, 0));
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        rig.Settings.ReadTypedAnswers = true;
        rig.Session.BeginRequest("How do I enable Nanite?");
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("a typed prompt is read too when asked, without a spoken cue",
            (rig.Speech.Said, rig.Speech.Options[0].Language), ($"{Opening} {Rest}", "ru"));
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        var listening = rig.Session.ReadAloudAsync(Answer);
        Program.Equal("Listen reads the answer on screen", (rig.Speech.Said, rig.Session.Phase),
            ($"{Opening} {Rest}", VoicePhase.Speaking));
        rig.Session.StopSpeaking();
        await listening;
        Program.Equal("and Stop silences it", (rig.Speech.Cancelled, rig.Session.Phase), (true, VoicePhase.Idle));

        rig = new Rig();
        rig.Settings.VoiceReplies = false;
        await rig.Session.ReadAloudAsync(Answer);
        Program.Equal("with voice replies off Link never speaks", (rig.Speech.Said, rig.Session.CanReadAloud), (string.Empty, false));

        rig = new Rig();
        rig.Speech.Failure = new InvalidOperationException("No voice could speak.");
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        await Program.ThrowsAsync<InvalidOperationException>("a voice that fails is reported with the answer",
            () => rig.Session.CompleteRequestAsync(Answer));
        Program.Equal("idle after the voice failed", rig.Session.Phase, VoicePhase.Idle);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.ForgetVoicePrompt();
        rig.Session.BeginRequest("Как дела?");
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("cleared prompt stays quiet", rig.Speech.Said, string.Empty);
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.EndRequest();
        Program.Equal("failed request ends idle", rig.Session.Phase, VoicePhase.Idle);
        rig.Session.BeginRequest("Как дела?");
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("retried dictated prompt is still answered aloud", rig.Session.Phase, VoicePhase.Speaking);
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        answered = await rig.StartSpeakingAsync();
        rig.Session.StartListening();
        Program.Equal("push-to-talk interrupts speech", rig.Session.Phase, VoicePhase.Listening);
        Program.Equal("interrupted speech is silenced", rig.Speech.Cancelled, true);
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("silenced answer keeps listening", rig.Session.Phase, VoicePhase.Listening);

        rig = new Rig();
        answered = await rig.StartSpeakingAsync();
        rig.Session.StopSpeaking();
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("stop silences and ends idle", (rig.Speech.Cancelled, rig.Session.Phase), (true, VoicePhase.Idle));

        rig = new Rig();
        answered = await rig.StartSpeakingAsync();
        rig.Session.BeginRequest("Как дела?");
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("a new request silences the answer", (rig.Speech.Cancelled, rig.Session.Phase), (true, VoicePhase.Sending));

        await StreamingAsync();
        await OverChatPageAsync();
        await WaitingCuesAsync();
        await LiveDraftAsync();

        rig = new Rig();
        rig.Session.StartListening();
        rig.Session.SendWhenTranscribed();
        var transcript = await rig.Session.FinishListeningAsync();
        Program.Equal("send pressed while speaking is kept", transcript?.SendRequested, true);
        rig.Session.StartListening();
        transcript = await rig.Session.FinishListeningAsync();
        Program.Equal("send request is used once", transcript?.SendRequested, false);

        rig = new Rig();
        rig.Recorder.Recording = rig.Recorder.Recording with { Duration = TimeSpan.FromMilliseconds(100) };
        rig.Session.StartListening();
        Program.Equal("too short recording has no transcript", await rig.Session.FinishListeningAsync(), null);

        rig = new Rig();
        rig.Recorder.Recording = rig.Recorder.Recording with { PeakLevel = 0.001 };
        rig.Session.StartListening();
        await Program.ThrowsAsync<InvalidOperationException>("silent recording is an error",
            () => rig.Session.FinishListeningAsync());
        Program.Equal("idle after a failed recording", rig.Session.Phase, VoicePhase.Idle);
    }

    /// <summary>The answer is read while it streams: each sentence once it is complete, the rest when it is in.</summary>
    private static async Task StreamingAsync()
    {
        var rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Waiting());
        rig.Session.ReportProgress(Receiving("Готово, всё настроено"));
        Program.Equal("a sentence being written is not said", (rig.Speech.Said, rig.Session.Phase),
            (string.Empty, VoicePhase.Waiting));
        rig.Session.ReportProgress(Receiving($"{Opening} Второе пред"));
        Program.Equal("a complete sentence is said while the answer streams", (rig.Speech.Said, rig.Session.Phase),
            (Opening, VoicePhase.Speaking));
        rig.Session.ReportProgress(Receiving($"{Opening} Второе предложение.\n\n```\nco"));
        Program.Equal("the next part goes to the voice while the first is still said", rig.Speech.Said,
            $"{Opening} | {Rest}");
        var answered = rig.Session.CompleteRequestAsync(Answer);
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("nothing is said twice", rig.Speech.Said, $"{Opening} | {Rest}");
        Program.Equal("idle after the streamed answer", rig.Session.Phase, VoicePhase.Idle);

        // An instant voice leaves Link between two sentences of the answer, waiting for the next one to stream.
        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Receiving($"{Opening} Вто"));
        Program.Equal("between sentences Link keeps the floor", rig.Session.Phase, VoicePhase.Speaking);
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("the rest is said after a pause", (rig.Speech.Said, rig.Session.Phase),
            ($"{Opening} | {Rest}", VoicePhase.Idle));

        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Receiving($"{Opening} Вто"));
        rig.Session.StartListening();
        Program.Equal("push-to-talk between sentences listens", rig.Session.Phase, VoicePhase.Listening);
        rig.Session.ReportProgress(Receiving($"{Opening} Второе предложение.\n\nЕщё"));
        await rig.Session.FinishListeningAsync();
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("an interrupted answer is not read further", (rig.Speech.Said, rig.Session.Phase),
            (Opening, VoicePhase.Idle));

        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Receiving($"{Opening} Вто"));
        rig.Session.StopSpeaking();
        Program.Equal("stop between sentences ends idle", rig.Session.Phase, VoicePhase.Idle);
        rig.Session.ReportProgress(Receiving($"{Opening} Второе предложение.\n\nЕщё"));
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("a stopped answer is not read further", (rig.Speech.Said, rig.Session.Phase),
            (Opening, VoicePhase.Idle));

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Receiving($"{Opening} Второе предложение. Треть"));
        rig.Session.EndRequest();
        Program.Equal("a failed request silences every part of its answer", (rig.Speech.Cancelled, rig.Speech.Waiting, rig.Session.Phase),
            (true, 0, VoicePhase.Idle));
    }

    /// <summary>
    /// Over the ChatGPT page: the answer is read while the page streams it, each sentence once, and a page that goes
    /// quiet for good ends the wait instead of leaving Link waiting.
    /// </summary>
    private static async Task OverChatPageAsync()
    {
        var rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        using (var chat = new EmbeddedChatTests.EmbeddedRig())
        {
            await chat.Page.AnnounceAsync("chat-1");
            rig.Session.BeginRequest("Как дела?");
            var answer = chat.SendAsync("Как дела?", progress: new SessionProgress(rig.Session));
            var request = await chat.Page.NextRequestAsync();
            await chat.Page.ReportAsync(request.Id, "sending");
            await chat.Page.ReportAsync(request.Id, "waiting");
            Program.Equal("voice waits with the chat", rig.Session.Phase, VoicePhase.Waiting);
            await chat.Page.ReportAsync(request.Id, "receiving", "Всё хорошо, спасибо что спросил. Спаси");
            Program.Equal("a streamed sentence is said before the answer is in", rig.Speech.Said,
                "Всё хорошо, спасибо что спросил.");
            await chat.Page.ReportAsync(request.Id, "completed", "Всё хорошо, спасибо что спросил. Спасибо.\n\nДальше подробности.");
            await rig.Session.CompleteRequestAsync((await answer).Text);
            Program.Equal("the rest follows and nothing is said twice", rig.Speech.Said,
                "Всё хорошо, спасибо что спросил. | Спасибо. Дальше подробности.");
        }

        rig = new Rig();
        await rig.DictateAsync();
        using (var chat = new EmbeddedChatTests.EmbeddedRig())
        {
            await chat.Page.AnnounceAsync("chat-1");
            rig.Session.BeginRequest("Как дела?");
            var answer = chat.SendAsync("Как дела?", progress: new SessionProgress(rig.Session));
            var request = await chat.Page.NextRequestAsync();
            await chat.Page.ReportAsync(request.Id, "waiting");
            await Program.ThrowsAsync<InvalidOperationException>("a lost page ends the spoken request", () => answer);
            rig.Session.EndRequest();
            Program.Equal("voice is idle after a lost page", rig.Session.Phase, VoicePhase.Idle);
        }
    }

    /// <summary>A long wait gets one short cue in the language of the question.</summary>
    private static async Task WaitingCuesAsync()
    {
        var rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Waiting());
        Program.Equal("no cue right after sending", rig.Speech.Said, string.Empty);
        rig.Time.Elapse();
        await Program.EventuallyAsync("a long wait gets a cue", () => rig.Speech.Said, "Секунду.");
        Program.Equal("the cue is speech", rig.Session.Phase, VoicePhase.Speaking);
        rig.Speech.Finish();
        await Program.EventuallyAsync("after the cue Link waits again", () => rig.Session.Phase, VoicePhase.Waiting);
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        Program.Equal("one cue per answer", rig.Speech.Said, "Секунду.");
        var answered = rig.Session.CompleteRequestAsync(Answer);
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("the answer follows the cue", rig.Speech.Said, $"Секунду. | {Opening} {Rest}");

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Waiting());
        rig.Session.ReportProgress(Receiving("Гот"));
        rig.Time.Elapse();
        Program.Equal("no cue once the answer streams", rig.Speech.Said, string.Empty);

        rig = new Rig();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        Program.Equal("typed prompt gets no cue", rig.Speech.Said, string.Empty);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest("Как дела?");
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        await Program.EventuallyAsync("cue before push-to-talk", () => rig.Session.Phase, VoicePhase.Speaking);
        rig.Session.StartListening();
        Program.Equal("push-to-talk interrupts the cue", (rig.Speech.Cancelled, rig.Session.Phase),
            (true, VoicePhase.Listening));
    }

    /// <summary>While the key is held the live words show; speech detection decides whether Whisper runs.</summary>
    private static async Task LiveDraftAsync()
    {
        var rig = new Rig();
        var drafts = new List<string>();
        rig.Session.DraftChanged += drafts.Add;
        rig.Session.StartListening();
        var live = rig.Streaming.Last!;
        rig.Recorder.Capture(new float[800]);
        Program.Equal("the live side hears the one microphone session", live.Samples, 800);
        live.Draft("почему у");
        live.Draft("почему у меня");
        Program.Equal("drafts come while listening", string.Join(" | ", drafts), "почему у | почему у меня");
        var transcript = await rig.Session.FinishListeningAsync();
        live.Draft("почему у меня материал");
        Program.Equal("a draft after the key is dropped", drafts.Count, 2);
        Program.Equal("the transcript comes from the final pass", transcript?.Text, "Как дела?");
        rig.Recorder.Capture(new float[800]);
        Program.Equal("the live side stops with the key", (live.Samples, live.Disposed), (800, true));

        rig = new Rig();
        rig.Streaming.Heard = false;
        rig.Session.StartListening();
        Program.Equal("a recording without speech has no transcript", await rig.Session.FinishListeningAsync(), null);
        Program.Equal("a recording without speech skips Whisper", rig.SpeechToText.Calls, 0);

        rig = new Rig();
        rig.Streaming.Heard = null;
        await rig.DictateAsync();
        Program.Equal("without a detector Whisper runs", rig.SpeechToText.Calls, 1);

        rig = new Rig();
        rig.Settings.LiveTranscription = false;
        rig.Streaming.Note = "live text model is downloading (28 MB, once)";
        rig.Session.StartListening();
        Program.Equal("live text off asks for no draft", rig.Streaming.Starts[0].Draft, false);
        Program.Equal("why the draft is missing is told", rig.Session.DraftNote, "live text model is downloading (28 MB, once)");

        rig = new Rig();
        rig.SpeechToText.Text = "How are you?";
        await rig.DictateAsync();
        rig.Session.StartListening();
        Program.Equal("under Auto the draft follows the last dictation", rig.Streaming.Starts[1].Language, VoiceLanguage.English);
        await rig.Session.FinishListeningAsync();
        rig.Settings.VoiceLanguage = VoiceLanguage.Russian;
        rig.Session.StartListening();
        Program.Equal("a chosen language is kept", rig.Streaming.Starts[2].Language, VoiceLanguage.Russian);

        rig = new Rig();
        rig.Recorder.FailStart = true;
        await Program.ThrowsAsync<InvalidOperationException>("a microphone that fails to open is reported", () =>
        {
            rig.Session.StartListening();
            return Task.CompletedTask;
        });
        Program.Equal("a failed start lets the live side go", (rig.Streaming.Last!.Disposed, rig.Session.Phase),
            (true, VoicePhase.Idle));
    }

    private static ChatProgress Waiting() => new() { State = ChatProgressState.Waiting };

    private static ChatProgress Receiving(string text) => new() { State = ChatProgressState.Receiving, Text = text };

    /// <summary>The chat's progress straight into the session, as the overlay passes it on.</summary>
    private sealed class SessionProgress(VoiceSession session) : IProgress<ChatProgress>
    {
        public void Report(ChatProgress value) => session.ReportProgress(value);
    }

    private sealed class Rig
    {
        public Rig() => Session = new VoiceSession(Settings, Recorder, Streaming, SpeechToText, Speech, Time);

        public AppSettings Settings { get; } = new();
        public FakeRecorder Recorder { get; } = new();
        public FakeStreaming Streaming { get; } = new();
        public FakeSpeechToText SpeechToText { get; } = new();
        public FakeTextToSpeech Speech { get; } = new();
        public ManualTime Time { get; } = new();
        public VoiceSession Session { get; }

        public async Task DictateAsync()
        {
            Session.StartListening();
            await Session.FinishListeningAsync();
        }

        public async Task<Task> StartSpeakingAsync()
        {
            await DictateAsync();
            Session.BeginRequest("Как дела?");
            return Session.CompleteRequestAsync(Answer);
        }

        /// <summary>
        /// Lets each queued part be said until the answer is done; a wrongly started answer fails a check
        /// instead of hanging the run.
        /// </summary>
        public async Task FinishSpeakingAsync(Task answered)
        {
            while (!answered.IsCompleted)
            {
                Speech.Finish();
                await Task.WhenAny(answered, Task.Delay(10));
            }

            await answered;
        }
    }

    private sealed class FakeRecorder : IAudioRecorder
    {
        // Nothing lives here, so deleting the "recording" touches no real file.
        public AudioRecording Recording { get; set; } =
            new(Path.Combine(Path.GetTempPath(), "link-tests-missing", "voice.wav"), TimeSpan.FromSeconds(2), 0.5);

        public bool IsRecording { get; private set; }

        public bool FailStart { get; set; }

        public event Action<double>? LevelChanged
        {
            add { }
            remove { }
        }

        public event Action<float[]>? SamplesCaptured;

        /// <summary>A block arrives from the microphone, as it would on the capture thread.</summary>
        public void Capture(float[] samples) => SamplesCaptured?.Invoke(samples);

        public bool Start(string? deviceId)
        {
            if (FailStart) throw new InvalidOperationException("The microphone did not respond.");
            IsRecording = true;
            return true;
        }

        public AudioRecording Stop(bool save = true)
        {
            IsRecording = false;
            return Recording;
        }

        public void Cancel() => IsRecording = false;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSpeechToText : ISpeechToText
    {
        public string Text { get; set; } = "Как дела?";
        public int Calls { get; private set; }

        public bool IsModelReady => true;

        public string? Note => null;

        public Task<string> TranscribeAsync(string waveFile, VoiceLanguage language, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Text);
        }
    }

    /// <summary>Live recognition whose drafts the test says and whose speech detection it decides.</summary>
    private sealed class FakeStreaming : IStreamingSpeechToText
    {
        public bool? Heard { get; set; } = true;
        public string? Note { get; set; }
        public List<(VoiceLanguage Language, bool Draft)> Starts { get; } = [];
        public FakeTranscription? Last { get; private set; }

        public IStreamingTranscription Start(VoiceLanguage language, bool draft)
        {
            Starts.Add((language, draft));
            Last = new FakeTranscription(Heard, Note);
            return Last;
        }
    }

    private sealed class FakeTranscription(bool? heard, string? note) : IStreamingTranscription
    {
        public event Action<string>? DraftChanged;

        public string? Note => note;
        public int Samples { get; private set; }
        public bool Disposed { get; private set; }

        public void Draft(string words) => DraftChanged?.Invoke(words);

        public void Accept(float[] samples) => Samples += samples.Length;

        public Task<bool?> FinishAsync() => Task.FromResult(heard);

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A voice that queues parts in order, like the real one, and says each until the test calls <see cref="Finish"/>;
    /// an <see cref="Instant"/> voice is done at once, and one with a <see cref="Failure"/> fails every part.
    /// </summary>
    private sealed class FakeTextToSpeech : ITextToSpeech
    {
        private readonly List<string> _said = [];
        private readonly Queue<TaskCompletionSource> _unsaid = new();

        public bool Instant { get; set; }
        public Exception? Failure { get; set; }
        public List<SpeechOptions> Options { get; } = [];
        public int Prepared { get; private set; }

        // Speech may go on on a thread-pool thread while a check reads it.
        public string Said
        {
            get
            {
                lock (_said) return string.Join(" | ", _said);
            }
        }

        public bool Cancelled { get; private set; }

        /// <summary>Parts handed over and neither said nor silenced.</summary>
        public int Waiting
        {
            get
            {
                lock (_said) return _unsaid.Count(part => !part.Task.IsCompleted);
            }
        }

        public bool IsSpeaking => Waiting > 0;

        public Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
        {
            lock (_said)
            {
                _said.Add(text);
                Options.Add(options);
            }

            if (Failure is { } failure) return Task.FromException(failure);
            if (Instant) return Task.CompletedTask;
            var part = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_said) _unsaid.Enqueue(part);
            cancellationToken.Register(() =>
            {
                Cancelled = true;
                part.TrySetCanceled(cancellationToken);
            });
            return part.Task;
        }

        public void Prepare(SpeechOptions options) => Prepared++;

        /// <summary>The part being said is done; the next one starts.</summary>
        public void Finish()
        {
            lock (_said)
            {
                while (_unsaid.TryDequeue(out var part))
                {
                    if (part.TrySetResult()) return;
                }
            }
        }

        public void Stop()
        {
            lock (_said)
            {
                while (_unsaid.TryDequeue(out var part)) part.TrySetCanceled();
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A clock whose timers fire only when the test lets time pass.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        /// <summary>Every timer still set fires, as if its time had come.</summary>
        public void Elapse()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = [.. _timers];
                _timers.Clear();
            }

            foreach (var timer in timers) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _stopped;

            public void Fire()
            {
                if (!_stopped) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _stopped = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
