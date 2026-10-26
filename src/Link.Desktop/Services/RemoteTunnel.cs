using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The remote page on the internet, while the user asks for it: a Cloudflare quick tunnel (<c>cloudflared</c>, no
/// account, no port opened on the router) gives an HTTPS address that changes with each start. Only the user's word in
/// the paired Telegram chat starts it; the page still wants a paired device for everything.
/// </summary>
public sealed partial class RemoteTunnel(Action<string> log) : IDisposable
{
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(45);
    private readonly Lock _lock = new();
    private Process? _process;
    private TaskCompletionSource<string?>? _address;

    public static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Link", "Tools", "cloudflared.exe");

    /// <summary>The page's address on the internet, starting the tunnel if it is not up; null and why when it cannot.</summary>
    public async Task<(string? Address, string? Problem)> StartAsync(int port)
    {
        if (!File.Exists(Executable)) return (null, $"cloudflared is not installed: put cloudflared.exe in {Path.GetDirectoryName(Executable)}");
        Task<string?> address;
        lock (_lock)
        {
            if (_process is not { HasExited: false } || _address is null) Launch(port);
            address = _address!.Task;
        }

        var found = await Task.WhenAny(address, Task.Delay(StartWait)) == address ? await address : null;
        if (found is not null) return (found, null);
        Stop();
        return (null, "The tunnel did not come up: Cloudflare may be out of reach from this network. link.log has its words.");
    }

    private void Launch(int port)
    {
        _process?.Dispose();
        var address = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        // cloudflared writes its log, the address among it, to stderr.
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not { } text) return;
            if (Address().Match(text) is { Success: true } match && address.TrySetResult(match.Value)) log($"[Remote] On the internet at {match.Value}");
            else if (text.Contains(" ERR ", StringComparison.Ordinal)) log("[Tunnel] " + text);
        };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            address.TrySetResult(null);
            log("[Remote] The tunnel stopped");
        };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (Win32Exception exception)
        {
            log($"[Remote] cloudflared did not start: {exception.Message}");
            address.TrySetResult(null);
        }

        _process = process;
        _address = address;
    }

    /// <summary>The page is off the internet.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // It ended on its own meanwhile.
            }

            _process?.Dispose();
            _process = null;
            _address = null;
        }
    }

    public void Dispose() => Stop();

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
