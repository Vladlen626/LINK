using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>ChatGPT's own addresses: a stored link is data, so only a ChatGPT page is ever opened for it.</summary>
internal static partial class ChatPageAddress
{
    public const string Origin = "https://chatgpt.com";
    public static readonly Uri Home = new($"{Origin}/");

    public static bool IsChatGpt(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps &&
        address.IsDefaultPort &&
        address.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsChatGpt([NotNullWhen(true)] string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsChatGpt(uri);

    public static bool IsConversationId([NotNullWhen(true)] string? id) => id is not null && ConversationIdPattern().IsMatch(id);

    /// <summary>Where a linked conversation opens: the address it was linked at, or one made from its id.</summary>
    public static Uri Of(ChatConversationLink conversation) =>
        Uri.TryCreate(conversation.Url, UriKind.Absolute, out var address) && IsChatGpt(address)
            ? address
            : new Uri(Home, $"c/{Uri.EscapeDataString(conversation.ConversationId)}");

    /// <summary>
    /// Where a new chat next to <paramref name="conversation"/> starts: the page of its ChatGPT project
    /// (<c>/g/g-p-&lt;id&gt;-&lt;name&gt;/c/…</c> → <c>/g/g-p-&lt;id&gt;/project</c>), else ChatGPT's home.
    /// </summary>
    public static Uri NewChatPlace(ChatConversationLink? conversation)
    {
        if (conversation is null) return Home;
        var project = ProjectPattern().Match(Of(conversation).AbsolutePath);
        return project.Success ? new Uri(Home, $"g/{project.Groups[1].Value}/project") : Home;
    }

    /// <summary>Whether the place is a ChatGPT project's page rather than ChatGPT's home.</summary>
    public static bool IsProjectPlace(Uri place) => ProjectPattern().IsMatch(place.AbsolutePath);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex ConversationIdPattern();

    // A project's id is g-p- and 32 hex digits; its chats add the project's name after a dash.
    [GeneratedRegex("^/g/(g-p-[0-9a-f]+)(?:-[^/]*)?/", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectPattern();
}
