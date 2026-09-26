namespace Link.Desktop.Services;

/// <summary>LINK's embedded ChatGPT page as its host talks to it: the WebView2 window, or a stand-in in tests.</summary>
internal interface IChatPage
{
    /// <summary>A message the page's companion posted, as its JSON text.</summary>
    event Action<string>? MessageReceived;

    /// <summary>The page began a new document: whatever its companion did there is gone.</summary>
    event Action? DocumentStarted;

    /// <summary>The page could not load; why, in a few words.</summary>
    event Action<string>? LoadFailed;

    /// <summary>Whether the user has the page in front of them.</summary>
    bool IsRevealed { get; }

    /// <summary>Posts a message to the page's companion; one posted while a document loads is lost.</summary>
    void Post(string json);

    void Navigate(Uri address);

    /// <summary>Brings the page in front of the user.</summary>
    void Reveal();
}
