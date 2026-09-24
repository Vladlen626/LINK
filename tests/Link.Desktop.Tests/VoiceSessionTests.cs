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
        rig.Session.ReportProgress(new ChatProgress { State = ChatProgressState.Waiting });
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

    private sealed class Rig
    {
        public Rig() => Session = new VoiceSession(Settings, Recorder, new FakeSpeechToText(), Speech);

        public AppSettings Settings { get; } = new();
        public FakeRecorder Recorder { get; } = new();
        public FakeTextToSpeech Speech { get; } = new();
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

        /// <summary>Lets any speech end, so a wrongly started answer fails a check instead of hanging the run.</summary>
        public async Task FinishSpeakingAsync(Task answered)
        {
            Speech.Finish();
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

    /// <summary>Speaks until the test calls <see cref="Finish"/>; completes asynchronously, like Windows voices.</summary>
    private sealed class FakeTextToSpeech : ITextToSpeech
    {
        private readonly List<string> _said = [];
        private TaskCompletionSource? _speech;

        public string Said => string.Join(" | ", _said);
        public bool Cancelled { get; private set; }
        public bool IsSpeaking => _speech is not null;

        public IReadOnlyList<SpeechVoice> GetVoices() => [];

        public async Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
        {
            _said.Add(text);
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
}
