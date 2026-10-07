using System.Text;

namespace Link.Desktop.Services;

/// <summary>
/// What Link gives an agent's CLI on each run of a task (the command, its folder and variables, the text on stdin) and
/// every line the CLI writes back, for the Console window: the agents run without a window of their own. Only the
/// current task's and only so much of it, as the app runs all day; the CLIs' own logs keep the rest.
/// </summary>
public sealed class AgentConsole
{
    private const int Limit = 2_000_000;
    private const int LineLimit = 4_000;
    private static readonly string[] SecretNames = ["TOKEN", "KEY", "SECRET", "PASSWORD"];
    private readonly Lock _lock = new();
    private readonly StringBuilder _text = new();

    // Where the kept text starts, counted from Link's start; a reader that is behind it takes the whole text again.
    private long _start;

    /// <summary>A new task: what an earlier one sent and wrote is gone.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            // One past the old end, so that every reader starts over.
            _start += _text.Length + 1;
            _text.Clear();
        }
    }

    /// <summary>A run starts: what Link hands the CLI, written out as it goes.</summary>
    public void Sent(string title, string executable, IReadOnlyList<string> arguments, string folder,
        IReadOnlyDictionary<string, string> variables, string input)
    {
        var text = new StringBuilder()
            .Append("━━ ").Append(DateTime.Now.ToString("HH:mm:ss")).Append(" · ").AppendLine(title)
            .Append("> ").AppendLine(string.Join(' ', arguments.Prepend(executable).Select(Quoted)))
            .Append("  in ").AppendLine(folder);
        foreach (var (name, value) in variables) text.Append("  + ").Append(name).Append('=').AppendLine(Shown(name, value));
        text.AppendLine("── stdin ──").AppendLine(input.TrimEnd()).AppendLine("── output ──");
        Append(text.ToString());
    }

    /// <summary>A line the CLI wrote; stderr is marked with "! ", a very long line is cut.</summary>
    public void Line(string line, bool error = false)
    {
        var shown = line.Length <= LineLimit ? line : $"{line[..LineLimit]}… (+{line.Length - LineLimit} characters)";
        Append((error ? "! " : string.Empty) + shown + Environment.NewLine);
    }

    /// <summary>
    /// The text after <paramref name="read"/>, where a reader got to, and where it ends now. <c>Whole</c>: the reader
    /// fell behind a new task or the cut of old text, and the text is all there is.
    /// </summary>
    public (string Text, long End, bool Whole) After(long read)
    {
        lock (_lock)
        {
            var end = _start + _text.Length;
            return read < _start
                ? (_text.ToString(), end, true)
                : (_text.ToString((int)(read - _start), (int)(end - read)), end, false);
        }
    }

    private void Append(string text)
    {
        lock (_lock)
        {
            _text.Append(text);
            if (_text.Length <= Limit) return;
            // The older half goes at once rather than a little on every line.
            var cut = _text.Length - Limit / 2;
            _text.Remove(0, cut);
            _start += cut;
        }
    }

    /// <summary>A variable's value as the window shows it: a key or token is never written out, only that there is one.</summary>
    internal static string Shown(string name, string value) =>
        value.Length > 0 && SecretNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)) ? "***" : value;

    private static string Quoted(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>']) < 0
            ? argument
            : "\"" + argument.Replace("\"", "\\\"") + "\"";
}
