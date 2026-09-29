using Link.Desktop.Models;

namespace Link.Desktop.Services;

public enum SoundCue
{
    Listening,

    /// <summary>A hands-free phrase ended: Link heard it and is on its way.</summary>
    Heard,
    Sent,
    NeedsDecision,
    Done,
    Error
}

/// <summary>
/// Short tones for what happens while the user looks elsewhere, at most 150 ms each and made in code. They play through
/// the voice's player; while Link speaks they keep quiet, except the one that asks for the user.
/// </summary>
public sealed class SoundCues(IAudioOutput output, AppSettings settings, Func<bool> speaking)
{
    private const int SampleRate = 24000;
    // Soft: about a fifth of full scale, well under the voice.
    private const double Volume = 0.2;
    // (from Hz, to Hz, milliseconds): a note holds its pitch, a sweep glides, zero is a pause.
    private static readonly Dictionary<SoundCue, (double From, double To, int Milliseconds)[]> Shapes = new()
    {
        [SoundCue.Listening] = [(880, 880, 60)],
        // The microphone closing: a short glide down, the mirror of Sent's glide up.
        [SoundCue.Heard] = [(1100, 800, 70)],
        [SoundCue.Sent] = [(600, 1200, 90)],
        [SoundCue.NeedsDecision] = [(1319, 1319, 55), (0, 0, 20), (1319, 1319, 55)],
        [SoundCue.Done] = [(1047, 1047, 60), (1568, 1568, 80)],
        [SoundCue.Error] = [(440, 440, 70), (330, 330, 80)]
    };
    private readonly Dictionary<SoundCue, PcmAudio> _tones = [];

    public void Play(SoundCue cue)
    {
        if (!settings.SoundCues || (speaking() && cue != SoundCue.NeedsDecision)) return;
        if (!_tones.TryGetValue(cue, out var tone))
        {
            tone = Synthesize(cue);
            _tones[cue] = tone;
        }

        IAudioSegment segment;
        try
        {
            segment = output.Begin();
        }
        catch (ObjectDisposedException)
        {
            // Link is closing.
            return;
        }

        // Nobody waits for a tone: a device that cannot play it is not worth an error.
        segment.Started.ContinueWith(Observe, TaskContinuationOptions.OnlyOnFaulted);
        segment.Finished.ContinueWith(Observe, TaskContinuationOptions.OnlyOnFaulted);
        segment.Write(tone);
        segment.Complete();
    }

    /// <summary>A cue as 16-bit mono PCM: each part rises in 5 ms and fades out to its end, so nothing clicks.</summary>
    internal static PcmAudio Synthesize(SoundCue cue)
    {
        var parts = Shapes[cue];
        var data = new byte[parts.Sum(part => SampleRate * part.Milliseconds / 1000) * 2];
        var offset = 0;
        foreach (var (from, to, milliseconds) in parts)
        {
            var count = SampleRate * milliseconds / 1000;
            var phase = 0.0;
            for (var index = 0; index < count; index++, offset += 2)
            {
                var progress = (double)index / count;
                phase += 2 * Math.PI * (from + (to - from) * progress) / SampleRate;
                var envelope = Math.Min(1, index / (SampleRate * 0.005)) * (1 - progress);
                var sample = (short)(Math.Sin(phase) * envelope * Volume * short.MaxValue);
                data[offset] = (byte)sample;
                data[offset + 1] = (byte)(sample >> 8);
            }
        }

        return new PcmAudio(data, SampleRate);
    }

    private static void Observe(Task task) => _ = task.Exception;
}
