using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>
/// The script LINK's embedded ChatGPT page runs in every new document: the browser companion's content.js on the
/// stand-in for the extension API from browser-extension/embedded-host.js. Both files are built into LINK, so the page
/// always runs the companion LINK was built with.
/// </summary>
internal static class ChatPageScript
{
    public static string Compose(string token)
    {
        var config = JsonSerializer.Serialize(new { token, version = CompanionVersion() });
        return $$"""
            (() => {
            // Only ChatGPT's own top-level page: the script runs in every document the page opens, frames too.
            if (window.top !== window || location.origin !== "{{ChatPageAddress.Origin}}") return;
            {{Read("embedded-host.js")}}
            const linkHostApi = createLinkHostApi({{config}});
            if (!linkHostApi) return;
            // The companion starts once the page has its DOM, as it does from the extension.
            const startCompanion = () => {
            {{Read("content.js")}}
            };
            if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", startCompanion, { once: true });
            else startCompanion();
            })();
            """;
    }

    /// <summary>The version in the companion's manifest.</summary>
    public static string CompanionVersion()
    {
        using var manifest = JsonDocument.Parse(Read("manifest.json"));
        return manifest.RootElement.GetProperty("version").GetString() ?? "unknown";
    }

    private static string Read(string name)
    {
        using var stream = typeof(ChatPageScript).Assembly.GetManifestResourceStream($"ChatPage.{name}") ??
                           throw new InvalidOperationException($"The companion's {name} is not built into LINK.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
