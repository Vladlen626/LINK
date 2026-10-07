using System.Text.Json.Serialization;

namespace Link.Desktop.Models;

/// <summary>The servers Settings knows by name; each speaks the Anthropic Messages API that Claude Code can be pointed at.</summary>
public enum CustomPreset
{
    ZaiGlm,
    DeepSeek,
    OpenRouter,
    Ollama,
    Other
}

/// <summary>
/// A preset: the address and models to start from, whether the server wants a key and sees pictures, who gets the user's
/// code, and a note on its price and limits.
/// </summary>
public sealed record CustomPresetInfo(
    CustomPreset Preset,
    string Name,
    string BaseUrl,
    IReadOnlyList<string> Models,
    bool NeedsKey,
    bool Images,
    string SeesCode,
    string Note);

public static class CustomPresets
{
    public static IReadOnlyList<CustomPresetInfo> All { get; } =
    [
        new(CustomPreset.ZaiGlm, "GLM (Z.ai)", "https://api.z.ai/api/anthropic", ["glm-4.7-flash", "glm-4.7"], true, false, "Z.ai",
            "GLM-4.7-Flash is free (about one request a second); keys at z.ai"),
        new(CustomPreset.DeepSeek, "DeepSeek", "https://api.deepseek.com/anthropic", ["deepseek-chat", "deepseek-reasoner"], true, false, "DeepSeek",
            "Cheap, not free; keys at platform.deepseek.com"),
        new(CustomPreset.OpenRouter, "OpenRouter", "https://openrouter.ai/api", ["openai/gpt-oss-120b:free", "z-ai/glm-4.5-air:free"], true, false,
            "OpenRouter and the model's host",
            "Free models end in :free: 20 a minute, 50 a day (1000 after a one-time $10); the list changes — see openrouter.ai/models"),
        new(CustomPreset.Ollama, "Ollama (local)", "http://localhost:11434", ["qwen3-coder", "glm-4.7-flash", "gpt-oss:20b"], false, false, "this PC",
            "Runs on this PC: nothing leaves it; needs Ollama 0.14 or newer and the model pulled"),
        new(CustomPreset.Other, "Custom", string.Empty, [], true, false, "the address below", "Any server with an Anthropic Messages API")
    ];

    public static CustomPresetInfo Of(CustomPreset preset) => All.FirstOrDefault(info => info.Preset == preset) ?? All[0];
}

/// <summary>
/// The third agent's setup: a server with an Anthropic-compatible API and one of its models, which Claude Code is pointed
/// at (see <see cref="Services.CustomModelProvider"/>). The key is not here: it lives in its own encrypted file.
/// </summary>
public sealed class CustomModelSettings
{
    /// <summary>Starts as the default preset, so that a new setup is complete.</summary>
    public CustomPreset Preset { get; set; } = CustomPreset.ZaiGlm;

    /// <summary>What the user calls it, on the card and in the thread; null: the preset's name.</summary>
    public string? Name { get; set; }

    public string? BaseUrl { get; set; } = CustomPresets.Of(CustomPreset.ZaiGlm).BaseUrl;

    public string? Model { get; set; } = CustomPresets.Of(CustomPreset.ZaiGlm).Models[0];

    [JsonIgnore]
    public CustomPresetInfo Info => CustomPresets.Of(Preset);

    [JsonIgnore]
    public string DisplayName => Name ?? Info.Name;

    /// <summary>Whether the key may be sent to the address: https, or http on this PC (Ollama).</summary>
    [JsonIgnore]
    public bool HasSafeAddress =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var address) &&
        (address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback));

    /// <summary>
    /// What is still missing before the model can run, in a few words; null when it is set up. The key lives apart, so
    /// whether there is one is told.
    /// </summary>
    public string? Missing(bool hasKey) =>
        !HasSafeAddress ? "Needs an https:// address (http:// only on this PC)"
        : Model is not { Length: > 0 } ? "Needs a model"
        : Info.NeedsKey && !hasKey ? "Needs a key"
        : null;

    /// <summary>Trimmed; an address or model left empty is the preset's.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Preset)) Preset = CustomPreset.ZaiGlm;
        Name = Trimmed(Name);
        BaseUrl = Trimmed(BaseUrl) ?? Trimmed(Info.BaseUrl);
        Model = Trimmed(Model) ?? Info.Models.FirstOrDefault();
    }

    public CustomModelSettings Clone() => new() { Preset = Preset, Name = Name, BaseUrl = BaseUrl, Model = Model };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
