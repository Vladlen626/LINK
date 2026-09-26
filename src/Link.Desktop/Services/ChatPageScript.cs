using System.Reflection;
using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>
/// The script LINK's embedded ChatGPT page runs in every new document: the companion (companion/content.js) on LINK's
/// end of the page's message channel (companion/embedded-host.js). Both files are built into LINK, so the page always
/// runs the companion this LINK was built with.
/// </summary>
internal static class ChatPageScript
{
    public static string Compose(string token)
    {
        var config = JsonSerializer.Serialize(new { token, version = Version });
        return $$"""
            (() => {
            // Only ChatGPT's own top-level page: the script runs in every document the page opens, frames too.
            if (window.top !== window || location.origin !== "{{ChatPageAddress.Origin}}") return;
            {{Read("embedded-host.js")}}
            const linkHostApi = createLinkHostApi({{config}});
            if (!linkHostApi) return;
            // The companion starts once the page has its DOM.
            const startCompanion = () => {
            {{Read("content.js")}}
            };
            if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", startCompanion, { once: true });
            else startCompanion();
            })();
            """;
    }

    /// <summary>The companion's version is LINK's own: it names the build in the page's diagnostics.</summary>
    public static string Version { get; } =
        typeof(ChatPageScript).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static string Read(string name)
    {
        using var stream = typeof(ChatPageScript).Assembly.GetManifestResourceStream($"ChatPage.{name}") ??
                           throw new InvalidOperationException($"The companion's {name} is not built into LINK.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
