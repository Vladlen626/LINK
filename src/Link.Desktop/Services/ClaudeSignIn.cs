using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// Claude Code's sign-in run by Link without a console: <c>claude auth login</c> opens the browser and takes the sign-in
/// back by itself. Where the browser cannot hand it back, the page shows a code, which goes to Claude Code as if typed;
/// the page Claude Code prints is kept to open again. Ends when Claude Code does, signed in or not.
/// </summary>
public sealed class ClaudeSignIn : IDisposable
{
    private static readonly Regex Address = new(@"https://\S+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // The prompt has no line break, so what Claude Code says after a code comes on its line.
    private static readonly Regex CodePrompt = new(@"^.*if prompted >\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Process _process;

    private ClaudeSignIn(Process process) => _process = process;

    /// <summary>The sign-in page as Claude Code printed it, once it has: the one that shows a code to paste.</summary>
    public string? Page { get; private set; }

    /// <summary>The last thing Claude Code said: why, when it ends without signing in.</summary>
    public string? Said { get; private set; }

    /// <summary>Done when Claude Code exits: true when it says it signed in.</summary>
    public Task<bool> Ended { get; private set; } = Task.FromResult(false);

    public static ClaudeSignIn Start(string executable, string? account)
    {
        var info = new ProcessStartInfo(executable, ["auth", "login"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var (name, value) in ClaudeAccounts.Variables(account)) info.Environment[name] = value;
        SystemProxy.AddTo(info.Environment);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Claude Code did not start.");
        var signIn = new ClaudeSignIn(process);
        process.OutputDataReceived += (_, e) => signIn.Heard(e.Data);
        process.ErrorDataReceived += (_, e) => signIn.Heard(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        signIn.Ended = SignedInAsync(process);
        return signIn;
    }

    private void Heard(string? line)
    {
        // The line with the page is no reason to give when it ends.
        if (PageIn(line) is { } page) Page ??= page;
        else if (CodePrompt.Replace(line ?? "", "").Trim() is { Length: > 0 } words) Said = words;
    }

    private static async Task<bool> SignedInAsync(Process process)
    {
        await process.WaitForExitAsync();
        try
        {
            return process.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false; // Called off and let go meanwhile.
        }
    }

    /// <summary>The address in a line Claude Code printed; null when it has none.</summary>
    public static string? PageIn(string? line) => line is not null && Address.Match(line) is { Success: true } found ? found.Value : null;

    /// <summary>The code the page shows, as if typed where Claude Code asks for it; false when Claude Code has ended.</summary>
    public bool SendCode(string code)
    {
        try
        {
            if (_process.HasExited) return false;
            // A bare line feed, as a terminal's Enter reaches it: no carriage return left on the code.
            _process.StandardInput.Write(code.Trim() + "\n");
            _process.StandardInput.Flush();
            return true;
        }
        catch (IOException)
        {
            return false; // It ended while the code went.
        }
    }

    /// <summary>A sign-in not finished is called off.</summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended meanwhile.
        }

        _process.Dispose();
    }
}
