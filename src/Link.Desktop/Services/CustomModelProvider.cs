using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The custom model: Claude Code pointed at another server with an Anthropic-compatible API (GLM, DeepSeek, OpenRouter,
/// Ollama) by environment variables, so that it has the same harness, tools, MCP and Activity. There is no subscription
/// behind it, so no usage windows, and no effort: the other servers are not sent a thinking setting. The key is read
/// when a run starts and goes only into that process's environment: never into arguments, the log or the Console.
/// </summary>
public sealed class CustomModelProvider : ClaudeCodeProvider
{
    private const string NoImages = "Screenshots were left out: this model does not read images.";

    // Claude Code asks for a model by the name of its family; the custom model answers to all of them.
    private static readonly string[] ModelVariables =
    [
        "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL"
    ];

    private readonly Func<AgentSettings> _settings;
    private readonly Func<string?> _key;

    public CustomModelProvider(Func<AgentSettings> settings, Func<string?> key, Action<string> log, AgentConsole? console = null)
        : base(settings, log, console)
    {
        _settings = settings;
        _key = key;
    }

    public override AgentProviderKind Kind => AgentProviderKind.Custom;
    public override string Name => Custom.DisplayName;
    public override AgentCapabilities Capabilities => new(false, Custom.Info.Images, false, Custom.Info.SeesCode);

    /// <summary>The preset's models, and the one chosen when it is not among them.</summary>
    public override IReadOnlyList<AgentChoice> Models
    {
        get
        {
            var models = Custom.Info.Models.ToList();
            if (Custom.Model is { } chosen && !models.Contains(chosen)) models.Add(chosen);
            return [.. models.Select(model => new AgentChoice(model, model))];
        }
    }

    public override IReadOnlyList<AgentChoice> Efforts => [];

    public override (string? Model, string? Effort) OwnSetup => (Custom.Model, null);

    /// <summary>An address the key may be sent to, a model, and a key where the server wants one.</summary>
    public override bool Configured => Missing is null;

    protected override string NotFound => $"Claude Code is not found, and {Name} runs through it. Install it or choose its file in Settings.";

    protected override string? NotReady => Missing is { } missing ? $"{Name}: {missing}. Settings → Agents has the setup." : null;

    // The status asks for nothing the server could be sent: the CLI's version, and the setup decides "signed in".
    protected override IReadOnlyList<string> SignInCheck => ["--version"];

    protected override bool IsSignedIn(string answer) => Configured;

    public override AgentEventParser CreateParser(AgentTask task) => new ClaudeEventParser(priced: false);

    public override Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentLimit>>([]);

    public override IReadOnlyList<string> Arguments(AgentTask task) => base.Arguments(Prepared(task));

    public override string Input(AgentTask task) =>
        task.Images.Count > 0 && !Custom.Info.Images ? base.Input(Prepared(task)) + "\n\n" + NoImages : base.Input(task);

    /// <summary>
    /// The budget and, for the server: its address, the key, and the model for every name Claude Code might ask for.
    /// An empty <c>ANTHROPIC_API_KEY</c> keeps Claude Code from falling back to the user's own Anthropic login.
    /// </summary>
    public override IReadOnlyDictionary<string, string> EnvironmentVariables(AgentTask task)
    {
        var variables = new Dictionary<string, string>(base.EnvironmentVariables(task))
        {
            ["ANTHROPIC_BASE_URL"] = Custom.BaseUrl ?? string.Empty,
            ["ANTHROPIC_AUTH_TOKEN"] = Custom.Info.NeedsKey ? _key() ?? string.Empty : "ollama",
            ["ANTHROPIC_API_KEY"] = string.Empty,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["API_TIMEOUT_MS"] = "600000"
        };
        var model = Prepared(task).Model ?? string.Empty;
        foreach (var name in ModelVariables) variables[name] = model;
        return variables;
    }

    private CustomModelSettings Custom => _settings().Custom;

    private string? Missing => Custom.Missing(_key() is { Length: > 0 });

    /// <summary>The task as this server gets it: Settings' model unless one was chosen, no effort, and no images where it reads none.</summary>
    private AgentTask Prepared(AgentTask task) =>
        task with { Model = task.Model ?? Custom.Model, Effort = null, Images = Custom.Info.Images ? task.Images : [] };
}
