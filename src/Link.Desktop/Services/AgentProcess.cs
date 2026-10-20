using System.Diagnostics;
using System.Text;
using Link.Desktop.Windows;

namespace Link.Desktop.Services;

/// <summary>An agent CLI to start: its file, arguments, the folder it works in and the text it reads from stdin.</summary>
public sealed record AgentCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string Input)
{
    /// <summary>The whole argument line, passed as it is instead of <see cref="Arguments"/>.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Variables set for the process on top of Link's own.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// A command line of the user's, run exactly as written through cmd.exe, the one shell: <c>/s</c> takes away only
    /// the quotes put around it here.
    /// </summary>
    public static AgentCommand Shell(string commandLine, string workingDirectory) =>
        new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], workingDirectory, string.Empty)
        {
            CommandLine = "/d /s /c \"" + commandLine + "\""
        };
}

/// <summary>How the process ended, with the tail of what it wrote for the user to look at.</summary>
public sealed record AgentProcessResult(int ExitCode, bool Stopped, string Log);

/// <summary>
/// Runs an agent CLI without a window: the task goes in on stdin, stdout is read line by line (JSONL) and stderr
/// alongside it, both asynchronously so that neither pipe fills and stalls the other. The process and its children
/// live in a job: Stop, the end of the run and Link's exit end them all.
/// </summary>
public static class AgentProcess
{
    /// <summary>The log keeps the last this many characters of stdout and stderr together.</summary>
    public const int LogLimit = 256 * 1024;

    private static readonly TimeSpan LeftoverGrace = TimeSpan.FromSeconds(2);

    /// <param name="talk">
    /// Given, stdin stays open after the task for the caller's answers while the CLI works, until the caller closes it or
    /// the process ends.
    /// </param>
    public static async Task<AgentProcessResult> RunAsync(
        AgentCommand command,
        Action<string> onLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken,
        AgentInput? talk = null)
    {
        using var process = new Process { StartInfo = StartInfo(command) };
        using var job = new ProcessJob();
        var log = new BoundedLog(LogLimit);
        process.Start();
        try
        {
            job.Add(process);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var errors = PumpAsync(process.StandardError, line =>
        {
            log.Add("! " + line);
            onErrorLine?.Invoke(line);
        });
        var output = PumpAsync(process.StandardOutput, line =>
        {
            log.Add(line);
            onLine(line);
        });
        var stopped = false;
        await using (cancellationToken.Register(() =>
                     {
                         stopped = true;
                         Kill(process);
                     }))
        {
            // The task goes in while the process runs: a CLI that ends before it read a long task, leaving a child that
            // holds stdin open, would otherwise keep the write and the run waiting for that child.
            var input = talk is null ? WriteInputAsync(process, command.Input) : talk.OpenAsync(process.StandardInput, command.Input);
            await process.WaitForExitAsync(CancellationToken.None);
            if (talk is not null) await talk.CloseAsync();
            // A process the agent left running (a dev server, a watcher) holds the pipes open and would keep the run
            // going: after a moment for the last lines, the job ends it.
            var pumps = Task.WhenAll(output, errors, input);
            if (await Task.WhenAny(pumps, Task.Delay(LeftoverGrace, CancellationToken.None)) != pumps) job.Dispose();
            await pumps;
        }

        return new AgentProcessResult(process.ExitCode, stopped, log.ToString());
    }

    internal static ProcessStartInfo StartInfo(AgentCommand command)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = command.WorkingDirectory
        };
        foreach (var (name, value) in command.EnvironmentVariables) info.Environment[name] = value;
        if (command.CommandLine is { } line)
        {
            info.FileName = command.Executable;
            info.Arguments = line;
        }
        else if (command.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // A batch wrapper runs only through cmd.exe, which parses the whole line itself. The task goes on stdin,
            // so the arguments are flags, paths and tool rules; anything cmd would treat as syntax is refused.
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.Arguments = "/d /s /c \"" + string.Join(' ', command.Arguments.Prepend(command.Executable).Select(QuoteForCmd)) + "\"";
        }
        else
        {
            info.FileName = command.Executable;
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        }

        return info;
    }

    internal static string QuoteForCmd(string argument)
    {
        if (argument.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"This argument cannot be passed through cmd.exe safely: {argument}");
        }

        return argument.Length > 0 && argument.IndexOfAny([' ', '\t', '(', ')', ',', ';', '=']) < 0
            ? argument
            : "\"" + argument + "\"";
    }

    private static async Task WriteInputAsync(Process process, string input)
    {
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process ended before it read its task; its exit code and stderr tell why.
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine(line);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone; the job ends anything it left behind.
        }
    }

    /// <summary>The last characters of a long output: a long run must not grow Link's memory without end.</summary>
    private sealed class BoundedLog(int limit)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();

        public void Add(string line)
        {
            lock (_lock)
            {
                _text.AppendLine(line);
                if (_text.Length > limit) _text.Remove(0, _text.Length - limit);
            }
        }

        public override string ToString()
        {
            lock (_lock) return _text.ToString();
        }
    }
}

/// <summary>
/// The stdin of a run that is talked with while it works (<see cref="AgentProcess.RunAsync"/>): the task goes in first,
/// then answers line by line, from any thread, until it is closed. A CLI that ended takes no more and raises nothing.
/// </summary>
public sealed class AgentInput
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;

    internal async Task OpenAsync(StreamWriter writer, string first)
    {
        _writer = writer;
        await SendAsync(first);
    }

    /// <summary>One line; a line feed ends it whatever the system's own.</summary>
    public async Task SendAsync(string line)
    {
        await _gate.WaitAsync();
        try
        {
            if (_writer is null) return;
            await _writer.WriteAsync(line + "\n");
            await _writer.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _writer = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The CLI's stdin ends: it finishes what it does and exits.</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _writer?.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
        finally
        {
            _writer = null;
            _gate.Release();
        }
    }
}

/// <summary>
/// A CLI talked to line by line while its stdin stays open, such as Codex's app-server: a request goes in, its answer
/// is read from stdout. The process lives in a job; disposing ends it with its children.
/// </summary>
public sealed class AgentTalk : IDisposable
{
    private readonly Process _process;
    private readonly ProcessJob _job = new();
    private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

    private AgentTalk(AgentCommand command)
    {
        _process = new Process { StartInfo = AgentProcess.StartInfo(command) };
        _process.Start();
        _job.Add(_process);
        _ = PumpAsync();
        // stderr is read and dropped, so that a chatty CLI never stalls on a full pipe.
        _ = _process.StandardError.ReadToEndAsync();
    }

    public static AgentTalk Start(AgentCommand command) => new(command);

    public async Task SendAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>The first line from now on that <paramref name="wanted"/> takes; null when the CLI ended without one.</summary>
    public async Task<string?> AnswerAsync(Func<string, bool> wanted, CancellationToken cancellationToken)
    {
        while (await _lines.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_lines.Reader.TryRead(out var line))
            {
                if (wanted(line)) return line;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _job.Dispose();
        _process.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line) _lines.Writer.TryWrite(line);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Ended while read.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }
}
