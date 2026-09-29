using System.Diagnostics;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// How long one request took at each step, for link.log: Link's own work (an auto screenshot, building the request),
/// the chat page taking the prompt, the first words of the answer and how fast the rest of it came.
/// </summary>
public sealed class RequestLatency
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long? _handedOver;
    private long? _accepted;
    private long? _firstText;
    private int _firstTextLength;

    /// <summary>Link has built the request and hands it to the chat.</summary>
    public void HandedOver() => _handedOver ??= Stopwatch.GetTimestamp();

    public void Heard(ChatProgress progress)
    {
        var now = Stopwatch.GetTimestamp();
        if (progress.State is ChatProgressState.Waiting or ChatProgressState.Receiving) _accepted ??= now;
        if (progress.State != ChatProgressState.Receiving || progress.Text.Length == 0 || _firstText is not null) return;
        _firstText = now;
        _firstTextLength = progress.Text.Length;
    }

    /// <summary>
    /// E.g. "[Chat] answer of 812 chars (Chat, Normal): Link 45 ms, accepted 310 ms, first text 2140 ms, 250 chars/s,
    /// total 5300 ms", where the attempt is the route and depth.
    /// </summary>
    public string Describe(string attempt, string outcome, int? answerLength = null)
    {
        var steps = new List<string>();
        if (_handedOver is { } handedOver) steps.Add($"Link {Milliseconds(handedOver)} ms");
        if (_accepted is { } accepted) steps.Add($"accepted {Milliseconds(accepted)} ms");
        if (_firstText is { } firstText)
        {
            steps.Add($"first text {Milliseconds(firstText)} ms");
            var writing = Stopwatch.GetElapsedTime(firstText).TotalSeconds;
            // Speed needs a stretch of writing; an answer that came at once has none.
            if (answerLength is { } length && writing > 0.5) steps.Add($"{(length - _firstTextLength) / writing:0} chars/s");
        }

        steps.Add($"total {Milliseconds(Stopwatch.GetTimestamp())} ms");
        var what = answerLength is { } chars ? $"{outcome} of {chars} chars" : outcome;
        return $"[Chat] {what} ({attempt}): {string.Join(", ", steps)}";
    }

    private long Milliseconds(long timestamp) => (long)Stopwatch.GetElapsedTime(_started, timestamp).TotalMilliseconds;
}
