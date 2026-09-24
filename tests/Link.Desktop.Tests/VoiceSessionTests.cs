using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

internal static class VoiceSessionTests
{
    private const string Answer = "Готово. Второе предложение.\n\n```\ncode\n```";

    public static async Task RunAsync()
    {
        var rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        Program.Equal("request goes out", rig.Session.Phase, VoicePhase.Sending);
        Program.Equal("push-to-talk waits for the request", rig.Session.CanStartListening, false);
        rig.Session.ReportProgress(Waiting());
        Program.Equal("chat accepted the prompt", rig.Session.Phase, VoicePhase.Waiting);
        var answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("dictated prompt is answered aloud", rig.Session.Phase, VoicePhase.Speaking);
        Program.Equal("only the opening is read", rig.Speech.Said, "Готово. Второе предложение. Подробности на экране.");
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("idle after speaking", rig.Session.Phase, VoicePhase.Idle);

        rig = new Rig();
        rig.Session.BeginRequest();
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("typed prompt stays quiet", (rig.Speech.Said, rig.Session.Phase), (string.Empty, VoicePhase.Idle));
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        rig.Settings.VoiceReplies = false;
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("voice replies off stays quiet", rig.Speech.Said, string.Empty);
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.ForgetVoicePrompt();
        rig.Session.BeginRequest();
        answered = rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("cleared prompt stays quiet", rig.Speech.Said, string.Empty);
        await rig.FinishSpeakingAsync(answered);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.EndRequest();
        Program.Equal("failed request ends idle", rig.Session.Phase, VoicePhase.Idle);
        rig.Session.BeginRequest();
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
        rig.Session.BeginRequest();
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("a new request silences the answer", (rig.Speech.Cancelled, rig.Session.Phase), (true, VoicePhase.Sending));

        await StreamingAsync();
        await WaitingCuesAsync();

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
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Waiting());
        rig.Session.ReportProgress(Receiving("Готово"));
        Program.Equal("a sentence being written is not said", (rig.Speech.Said, rig.Session.Phase),
            (string.Empty, VoicePhase.Waiting));
        rig.Session.ReportProgress(Receiving("Готово. Второе пред"));
        Program.Equal("a complete sentence is said while the answer streams", (rig.Speech.Said, rig.Session.Phase),
            ("Готово.", VoicePhase.Speaking));
        rig.Session.ReportProgress(Receiving("Готово. Второе предложение.\n\n```\nco"));
        var answered = rig.Session.CompleteRequestAsync(Answer);
        await rig.FinishSpeakingAsync(answered);
        Program.Equal("what queued up and the rest follow in one go", rig.Speech.Said,
            "Готово. | Второе предложение. Подробности на экране.");
        Program.Equal("idle after the streamed answer", rig.Session.Phase, VoicePhase.Idle);

        // An instant voice leaves Link between two sentences of the answer, waiting for the next one to stream.
        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Receiving("Готово. Вто"));
        Program.Equal("between sentences Link keeps the floor", rig.Session.Phase, VoicePhase.Speaking);
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("the rest is said after a pause", (rig.Speech.Said, rig.Session.Phase),
            ("Готово. | Второе предложение. Подробности на экране.", VoicePhase.Idle));

        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Receiving("Готово. Вто"));
        rig.Session.StartListening();
        Program.Equal("push-to-talk between sentences listens", rig.Session.Phase, VoicePhase.Listening);
        rig.Session.ReportProgress(Receiving("Готово. Второе предложение.\n\nЕщё"));
        await rig.Session.FinishListeningAsync();
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("an interrupted answer is not read further", (rig.Speech.Said, rig.Session.Phase),
            ("Готово.", VoicePhase.Idle));

        rig = new Rig();
        rig.Speech.Instant = true;
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Receiving("Готово. Вто"));
        rig.Session.StopSpeaking();
        Program.Equal("stop between sentences ends idle", rig.Session.Phase, VoicePhase.Idle);
        rig.Session.ReportProgress(Receiving("Готово. Второе предложение.\n\nЕщё"));
        await rig.Session.CompleteRequestAsync(Answer);
        Program.Equal("a stopped answer is not read further", (rig.Speech.Said, rig.Session.Phase),
            ("Готово.", VoicePhase.Idle));

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Receiving("Готово. Вто"));
        rig.Session.EndRequest();
        Program.Equal("a failed request silences its answer", (rig.Speech.Cancelled, rig.Session.Phase),
            (true, VoicePhase.Idle));
    }

    /// <summary>A long wait gets one short cue in the language of the question.</summary>
    private static async Task WaitingCuesAsync()
    {
        var rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
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
        Program.Equal("the answer follows the cue", rig.Speech.Said,
            "Секунду. | Готово. Второе предложение. Подробности на экране.");

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Waiting());
        rig.Session.ReportProgress(Receiving("Гот"));
        rig.Time.Elapse();
        Program.Equal("no cue once the answer streams", rig.Speech.Said, string.Empty);

        rig = new Rig();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        Program.Equal("typed prompt gets no cue", rig.Speech.Said, string.Empty);

        rig = new Rig();
        await rig.DictateAsync();
        rig.Session.BeginRequest();
        rig.Session.ReportProgress(Waiting());
        rig.Time.Elapse();
        await Program.EventuallyAsync("cue before push-to-talk", () => rig.Session.Phase, VoicePhase.Speaking);
        rig.Session.StartListening();
        Program.Equal("push-to-talk interrupts the cue", (rig.Speech.Cancelled, rig.Session.Phase),
            (true, VoicePhase.Listening));
    }

    private static ChatProgress Waiting() => new() { State = ChatProgressState.Waiting };

    private static ChatProgress Receiving(string text) => new() { State = ChatProgressState.Receiving, Text = text };

    private sealed class Rig
    {
        public Rig() => Session = new VoiceSession(Settings, Recorder, new FakeSpeechToText(), Speech, Time);

        public AppSettings Settings { get; } = new();
        public FakeRecorder Recorder { get; } = new();
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
            Session.BeginRequest();
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

        public event Action<double>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool Start(string? deviceId)
        {
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
        public bool IsModelReady => true;

        public Task<string> TranscribeAsync(string waveFile, VoiceLanguage language, CancellationToken cancellationToken = default) =>
            Task.FromResult("Как дела?");
    }

    /// <summary>
    /// Speaks until the test calls <see cref="Finish"/> and completes asynchronously, like Windows voices;
    /// an <see cref="Instant"/> voice is done at once.
    /// </summary>
    private sealed class FakeTextToSpeech : ITextToSpeech
    {
        private readonly List<string> _said = [];
        private TaskCompletionSource? _speech;

        public bool Instant { get; set; }

        // Speech may go on on a thread-pool thread while a check reads it.
        public string Said
        {
            get
            {
                lock (_said) return string.Join(" | ", _said);
            }
        }

        public bool Cancelled { get; private set; }
        public bool IsSpeaking => _speech is not null;

        public IReadOnlyList<SpeechVoice> GetVoices() => [];

        public async Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
        {
            lock (_said) _said.Add(text);
            if (Instant) return;
            var speech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _speech = speech;
            using (cancellationToken.Register(() =>
                   {
                       Cancelled = true;
                       speech.TrySetCanceled(cancellationToken);
                   }))
            {
                await speech.Task;
            }
        }

        public void Finish() => _speech?.TrySetResult();

        public void Stop() => Finish();

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
