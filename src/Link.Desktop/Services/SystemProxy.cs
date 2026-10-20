using Microsoft.Win32;

namespace Link.Desktop.Services;

/// <summary>
/// The proxy Windows is set to (a VPN app's, say), handed to the command-line agents Link starts. They read only
/// HTTPS_PROXY, so started from Explorer they would go direct, and where only the proxy gets through, the sign-in and
/// every run would be refused. The Claude app does the same for the Claude Code it runs.
/// </summary>
public static class SystemProxy
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // A process's environment on Windows ignores case, so these cover https_proxy and the rest too.
    private static readonly string[] Variables = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    /// <summary>Adds the proxy Windows uses to a process's environment, unless it names one already.</summary>
    public static void AddTo(IDictionary<string, string?> environment)
    {
        if (Variables.Any(environment.ContainsKey) || Read() is not { } proxy) return;
        environment["HTTPS_PROXY"] = proxy;
        environment["HTTP_PROXY"] = proxy;
        // Servers on this computer (a review page, an MCP server) stay direct.
        environment.TryAdd("NO_PROXY", "localhost,127.0.0.1,::1");
    }

    /// <summary>The proxy for https in Windows' settings; null when it is off. A proxy script (PAC) is not read.</summary>
    private static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
        return key?.GetValue("ProxyEnable") is int and not 0 ? Parse(key.GetValue("ProxyServer") as string) : null;
    }

    /// <summary>
    /// The address in Windows' proxy setting: all of it, or its https part (else http) when it names one per protocol,
    /// as "http=host:port;https=host:port".
    /// </summary>
    public static string? Parse(string? server)
    {
        var parts = (server ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var address = parts is [var only] && !only.Contains('=') ? only : Part(parts, "https") ?? Part(parts, "http");
        if (string.IsNullOrEmpty(address)) return null;
        return address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address;
    }

    private static string? Part(string[] parts, string scheme) =>
        parts.FirstOrDefault(part => part.StartsWith(scheme + "=", StringComparison.OrdinalIgnoreCase))?[(scheme.Length + 1)..];
}
