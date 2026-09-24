using System;
using System.Text;
using System.Threading.Tasks;

namespace Link.Desktop.Tests;

/// <summary>Runs every check and prints what failed; the exit code is the number of failures.</summary>
internal static class Program
{
    // The checks take milliseconds; a stuck await must fail the run, not hang CI.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static int _checks;
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        SpokenAnswerTests.Run();
        var voiceChecks = VoiceSessionTests.RunAsync();
        if (await Task.WhenAny(voiceChecks, Task.Delay(Timeout)) != voiceChecks)
        {
            Console.WriteLine($"FAIL voice session checks did not finish in {Timeout.TotalSeconds} s.");
            return _failures + 1;
        }

        await voiceChecks;
        Console.WriteLine(_failures == 0
            ? $"All {_checks} checks passed."
            : $"{_failures} of {_checks} checks failed.");
        return _failures;
    }

    public static void Equal<T>(string name, T actual, T expected)
    {
        _checks++;
        if (Equals(actual, expected)) return;
        _failures++;
        Console.WriteLine($"FAIL {name}\n  expected: {expected}\n  actual:   {actual}");
    }

    /// <summary>Checks a value that work finishing on another thread (e.g. the end of speech) settles soon.</summary>
    public static async Task EventuallyAsync<T>(string name, Func<T> actual, T expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Equals(actual(), expected) && DateTime.UtcNow < deadline) await Task.Delay(5);
        Equal(name, actual(), expected);
    }

    public static async Task ThrowsAsync<TException>(string name, Func<Task> action) where TException : Exception
    {
        Exception? error = null;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            error = exception;
        }

        Equal(name, error?.GetType().Name ?? "no exception", typeof(TException).Name);
    }
}
