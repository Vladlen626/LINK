namespace Link.Desktop.Services;

public sealed class StoragePaths
{
    public StoragePaths()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Link");
        Screenshots = Path.Combine(Root, "Screenshots");
        Recordings = Path.Combine(Root, "Recordings");
        Models = Path.Combine(Root, "Models");
        Projects = Path.Combine(Root, "Projects");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Screenshots);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Projects);
    }

    public string Root { get; }
    public string Screenshots { get; }
    public string Recordings { get; }
    public string Models { get; }
    public string Projects { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProjectsFile => Path.Combine(Root, "projects.json");
    public string LastRequestFile => Path.Combine(Root, "last-request.json");

    /// <summary>The embedded ChatGPT page's browser profile, with its session cookies.</summary>
    public string ChatPageProfile => Path.Combine(Root, "WebView2");

    /// <summary>What the embedded page's companion keeps across restarts: the chat modes it set.</summary>
    public string CompanionStateFile => Path.Combine(Root, "companion-state.json");

    /// <summary>Copies of workspaces without git, made before an agent changed them; created when first needed.</summary>
    public string Backups => Path.Combine(Root, "Backups");

    /// <summary>The agents' subscription windows as their CLIs last reported them.</summary>
    public string AgentLimitsFile => Path.Combine(Root, "agent-limits.json");

    /// <summary>The Gemini API key for voice replies, encrypted for this Windows user.</summary>
    public string GeminiKeyFile => Path.Combine(Root, "gemini-key.bin");

    /// <summary>The custom model's API key, encrypted for this Windows user; never in settings.json.</summary>
    public string CustomModelKeyFile => Path.Combine(Root, "custom-model.key");
}
