using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What the first-run window tells about an agent's CLI. Link neither installs nor signs in to an agent.</summary>
public static class FirstRun
{
    /// <summary>Where the agent's CLI is installed from.</summary>
    public static string InstallAddress(AgentProviderKind kind) => kind == AgentProviderKind.Codex
        ? "https://github.com/openai/codex"
        : "https://code.claude.com/docs/en/setup";

    /// <summary>The agent's state in a few words: found and signed in, found but not signed in, or not found.</summary>
    public static string Status(AgentProviderKind kind, AgentProviderStatus status)
    {
        if (!status.Installed) return "Not found";
        var signIn = kind == AgentProviderKind.Codex ? "codex login" : "claude auth login";
        return status.SignedIn switch
        {
            true => "Found, signed in",
            false => $"Found, not signed in: run '{signIn}' once",
            null => "Found, sign-in unknown"
        };
    }
}
