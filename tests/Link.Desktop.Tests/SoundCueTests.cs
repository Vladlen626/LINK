using System;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>Link's short sound cues: brief and click-free, quiet while Link talks and when switched off.</summary>
internal static class SoundCueTests
{
    public static void Run()
    {
        foreach (var cue in Enum.GetValues<SoundCue>())
        {
            var tone = SoundCues.Synthesize(cue);
            var milliseconds = tone.Data.Length / 2 * 1000 / tone.SampleRate;
            Program.Equal($"the {cue} cue lasts at most 150 ms", milliseconds is > 0 and <= 150, true);
            Program.Equal($"the {cue} cue starts and ends near silence",
                Math.Abs(Sample(tone, 0)) < 300 && Math.Abs(Sample(tone, tone.Data.Length / 2 - 1)) < 300, true);
        }

        var output = new RecordingOutput();
        var settings = new AppSettings();
        var speaking = false;
        var cues = new SoundCues(output, settings, () => speaking);
        cues.Play(SoundCue.Done);
        Program.Equal("a cue plays through the voice's player", output.Played, 1);

        speaking = true;
        cues.Play(SoundCue.Done);
        cues.Play(SoundCue.Error);
        Program.Equal("cues keep quiet while Link speaks", output.Played, 1);
        cues.Play(SoundCue.NeedsDecision);
        Program.Equal("a cue that asks for the user is heard over Link's voice", output.Played, 2);

        speaking = false;
        settings.SoundCues = false;
        cues.Play(SoundCue.NeedsDecision);
        Program.Equal("cues switched off in Settings stay silent", output.Played, 2);
    }

    private static short Sample(PcmAudio audio, int index) => (short)(audio.Data[index * 2] | audio.Data[index * 2 + 1] << 8);

    /// <summary>Counts the stretches that came with audio.</summary>
    private sealed class RecordingOutput : IAudioOutput
    {
        public int Played { get; private set; }

        public IAudioSegment Begin() => new Segment(this);

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        private sealed class Segment(RecordingOutput output) : IAudioSegment
        {
            private int _bytes;

            public Task Started => Task.CompletedTask;
            public Task Finished => Task.CompletedTask;

            public void SetPrebuffer(TimeSpan audio)
            {
            }

            public void Write(PcmAudio audio) => _bytes += audio.Data.Length;

            public void Complete()
            {
                if (_bytes > 0) output.Played++;
            }
        }
    }
}
