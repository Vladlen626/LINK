using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Notices in a run's events that the agent goes round in circles and says so once for each kind of loop. It only
/// marks the run: a strong model often gets out by itself, and a false stop would break working tasks.
/// </summary>
public sealed partial class AgentLoopDetector
{
    // A third identical call with nothing changed in between is no longer a check: its answer cannot differ.
    private const int RepeatedCalls = 3;

    // A third identical error in a row: whatever the agent tries between them does not get past it.
    private const int RepeatedErrors = 3;

    // A dozen searches with no change: the agent keeps looking instead of doing. A read-only run only looks, so this
    // counts only where it may change files.
    private const int SearchesWithoutChange = 12;

    // More than five edits of one file: the same place rewritten over and over.
    private const int EditsOfOneFile = 5;

    // The end of an error holds its cause; the start of a long build log is the same every time.
    private const int ErrorKept = 1000;

    private readonly bool _canChange;
    private readonly Dictionary<string, int> _calls = [];
    private readonly Dictionary<string, int> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _kinds = [];
    private string? _lastError;
    private int _errorsInARow;
    private int _searches;

    /// <param name="canChange">The run may change files, so that searching without a change means something.</param>
    public AgentLoopDetector(bool canChange) => _canChange = canChange;

    /// <summary>The kinds of loop told so far, for link.log: no paths, commands or errors.</summary>
    public IReadOnlyList<string> Kinds => _kinds;

    /// <summary>Takes the run's next event; returns a loop event when this one makes a loop of a kind not told yet.</summary>
    public AgentEvent? Add(AgentEvent agentEvent)
    {
        switch (agentEvent.Kind)
        {
            case AgentEventKind.Tool:
                var key = agentEvent.Detail + "\n" + agentEvent.Arguments;
                var calls = _calls[key] = _calls.GetValueOrDefault(key) + 1;
                if (agentEvent.IsSearch) _searches++;
                if (calls == RepeatedCalls) return Loop("repeated call", $"the same call {calls} times: {agentEvent.Text}");
                if (_canChange && _searches == SearchesWithoutChange) return Loop("searching", $"{_searches} searches in a row without a change");
                break;
            case AgentEventKind.ToolFailed:
                var error = Comparable(agentEvent.Text);
                _errorsInARow = error == _lastError ? _errorsInARow + 1 : 1;
                _lastError = error;
                if (_errorsInARow == RepeatedErrors) return Loop("repeated error", $"the same error {_errorsInARow} times in a row: {Telling(agentEvent.Text)}");
                break;
            case AgentEventKind.Artifact:
                // A change is progress: the same call may now answer differently, and the search starts over.
                _calls.Clear();
                _searches = 0;
                var edits = _edits[agentEvent.Text] = _edits.GetValueOrDefault(agentEvent.Text) + 1;
                if (edits == EditsOfOneFile + 1) return Loop("file edits", $"{Path.GetFileName(agentEvent.Text)} edited {edits} times");
                break;
        }

        return null;
    }

    private AgentEvent? Loop(string kind, string text)
    {
        if (_kinds.Contains(kind)) return null;
        _kinds.Add(kind);
        return new AgentEvent(AgentEventKind.Loop, "Looks like a loop: " + text, kind);
    }

    /// <summary>An error as errors are compared: its end, without numbers (times, counts, lines) and extra spaces.</summary>
    private static string Comparable(string error)
    {
        var text = Spaces().Replace(Digits().Replace(error, string.Empty), " ").Trim();
        return text.Length <= ErrorKept ? text : text[^ErrorKept..];
    }

    /// <summary>The line of an error that tells most: the first that says "error", else the first one.</summary>
    private static string Telling(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A command killed or refused in the sandbox may fail without a word.
        var line = lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "no output";
        return line.Length <= 120 ? line : line[..119] + "…";
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
