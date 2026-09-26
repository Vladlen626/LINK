using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// LINK's end of its embedded ChatGPT page. The page runs the browser companion (content.js) on a stand-in for the
/// extension API (embedded-host.js): what the companion announces, reports and keeps in storage comes here, and LINK's
/// requests go to it from the broker. Everything the page sends is data from a web page: a message without the
/// session's token is dropped, and nothing in one is followed as an instruction.
/// </summary>
internal sealed partial class ChatPageHost : IDisposable
{
    // The companion announces its page every 2.5 s; one quiet for longer is gone.
    private static readonly TimeSpan CompanionLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    // A page kept for hours runs ChatGPT's code of that morning and holds what it collected since; it is loaded afresh
    // when nobody needs it.
    private static readonly TimeSpan PageLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(10);
    private const int MaxMessageLength = 4 * 1024 * 1024;
    private const int MaxStoredValueLength = 16 * 1024;
    private const int MaxStoredEntries = 500;
    private const int MaxConversations = 30;
    private const string SignInRequired = "ChatGPT is not signed in. Press Open chat, sign in there, then send again.";
    private readonly object _gate = new();
    private readonly ChatRequestBroker _broker;
    private readonly string _stateFile;
    private readonly TimeSpan _deliveryInterval;
    private readonly byte[] _token;
    private readonly Timer _timer;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, JsonElement> _storage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AvailableChatConversation> _conversations = new(StringComparer.Ordinal);
    private IChatPage? _page;
    private Companion? _companion;
    private long _documentStartedAt;
    private string? _loadError;
    private TaskCompletionSource _heard = NewSignal();
    private Task _saving = Task.CompletedTask;
    private long _nextMessageId;
    private bool _disposed;

    /// <param name="broker">Follows the requests the page works on.</param>
    /// <param name="stateFile">Keeps the chat modes the companion set, across restarts.</param>
    /// <param name="deliveryInterval">How often a waiting request is offered to a page that has gone quiet about it.</param>
    /// <param name="token">The session's secret that the page's messages carry.</param>
    public ChatPageHost(ChatRequestBroker broker, string stateFile, TimeSpan deliveryInterval, string token)
    {
        _broker = broker;
        _stateFile = stateFile;
        _deliveryInterval = deliveryInterval;
        _token = Encoding.UTF8.GetBytes(token);
        var saved = AtomicJsonFile.Read<Dictionary<string, JsonElement>>(stateFile, _json);
        foreach (var (key, value) in saved ?? [])
        {
            if (IsKeptKey(key)) _storage[key] = value;
        }

        _timer = new Timer(_ => OnTimer(), null, IdleCheck, Timeout.InfiniteTimeSpan);
        _broker.RequestQueued += OnRequestQueued;
    }

    public ChatConnectionState State
    {
        get
        {
            lock (_gate)
            {
                if (_companion is { } companion && IsFresh(companion))
                {
                    return companion.SignedIn == false
                        ? ChatConnectionState.SignInRequired
                        : ChatConnectionState.ConversationAvailable;
                }

                // Until the first page has loaded, or while a new one loads, the page is on its way.
                if (_loadError is not null) return ChatConnectionState.BridgeUnavailable;
                return _page is null || _documentStartedAt == 0 || Since(_documentStartedAt) < OpenTimeout
                    ? ChatConnectionState.Connecting
                    : ChatConnectionState.BridgeUnavailable;
            }
        }
    }

    /// <summary>The conversations the page has shown this session, the one on screen first.</summary>
    public IReadOnlyList<AvailableChatConversation> Conversations
    {
        get
        {
            lock (_gate)
            {
                return _conversations.Values.OrderByDescending(conversation => conversation.LastSeenAt).ToArray();
            }
        }
    }

    public void Attach(IChatPage page)
    {
        page.MessageReceived += OnMessage;
        page.DocumentStarted += OnDocumentStarted;
        page.LoadFailed += OnLoadFailed;
        lock (_gate) _page = page;
        Signal();
    }

    /// <summary>
    /// Makes the page show the conversation and waits until its companion there speaks up. A page on another chat
    /// is taken there; a page still loading is first given the time to say where it is.
    /// </summary>
    public async Task OpenAsync(ChatConversationLink conversation, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var navigated = false;
        while (true)
        {
            Task heard;
            IChatPage? navigate = null;
            lock (_gate)
            {
                if (_companion is { } companion && IsFresh(companion))
                {
                    if (companion.Conversation?.ConversationId == conversation.ConversationId) return;
                    if (companion.SignedIn == false) throw new InvalidOperationException(SignInRequired);
                }

                if (navigated && _loadError is { } error)
                {
                    throw new InvalidOperationException(
                        $"ChatGPT did not load ({error}). Check the connection; Open chat shows the page.");
                }

                var loading = _companion is null && _loadError is null && Since(_documentStartedAt) < CompanionLifetime;
                if (!navigated && _page is not null && !loading)
                {
                    navigate = _page;
                    // Only how this navigation ends counts, not a failure of the page before it.
                    _loadError = null;
                }

                heard = _heard.Task;
            }

            if (navigate is not null)
            {
                navigate.Navigate(ChatPageAddress.Of(conversation));
                navigated = true;
            }

            var remaining = OpenTimeout - Stopwatch.GetElapsedTime(started);
            try
            {
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await heard.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(navigated
                    ? "The linked chat did not open in ChatGPT. Open chat shows the page; the chat may have been deleted."
                    : "ChatGPT is still starting. Try again in a moment.");
            }
        }
    }

    /// <summary>
    /// Opens a new chat at <paramref name="place"/> (ChatGPT's home or a project's page) and waits until the companion
    /// of that fresh page speaks up, so the prompt that starts the chat goes nowhere else.
    /// </summary>
    public async Task OpenNewChatAsync(Uri place, CancellationToken cancellationToken)
    {
        IChatPage page;
        string? before;
        lock (_gate)
        {
            page = _page ?? throw new InvalidOperationException("ChatGPT is still starting. Try again in a moment.");
            before = _companion?.InstanceId;
            _loadError = null;
        }

        page.Navigate(place);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task heard;
            lock (_gate)
            {
                if (_companion is { } companion && IsFresh(companion) && companion.InstanceId != before)
                {
                    if (companion.SignedIn == false) throw new InvalidOperationException(SignInRequired);
                    if (companion.Conversation is null) return;
                }

                if (_loadError is { } error)
                {
                    throw new InvalidOperationException(
                        $"ChatGPT did not load ({error}). Check the connection; Open chat shows the page.");
                }

                heard = _heard.Task;
            }

            var remaining = OpenTimeout - Stopwatch.GetElapsedTime(started);
            try
            {
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await heard.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The new chat did not open in ChatGPT. Open chat shows the page.");
            }
        }
    }

    /// <summary>Shows the page to the user, on the linked conversation when there is one.</summary>
    public void Reveal(ChatConversationLink? conversation)
    {
        IChatPage page;
        bool shown;
        lock (_gate)
        {
            page = _page ?? throw new InvalidOperationException("ChatGPT is still starting. Try again in a moment.");
            shown = conversation is null || _companion?.Conversation?.ConversationId == conversation.ConversationId;
        }

        if (!shown) page.Navigate(ChatPageAddress.Of(conversation!));
        page.Reveal();
    }

    public void Dispose()
    {
        IChatPage? page;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            page = _page;
            _page = null;
        }

        _broker.RequestQueued -= OnRequestQueued;
        _timer.Dispose();
        if (page is null) return;
        page.MessageReceived -= OnMessage;
        page.DocumentStarted -= OnDocumentStarted;
        page.LoadFailed -= OnLoadFailed;
    }

    private void OnDocumentStarted()
    {
        lock (_gate)
        {
            _companion = null;
            _loadError = null;
            _documentStartedAt = Stopwatch.GetTimestamp();
        }
    }

    private void OnLoadFailed(string error)
    {
        lock (_gate) _loadError = error;
        Signal();
    }

    private void OnMessage(string text)
    {
        if (text.Length > MaxMessageLength) return;
        JsonElement? id;
        string? kind;
        object? response;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasToken(root)) return;
            id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;
            kind = TextOf(root, "kind");
            try
            {
                response = kind switch
                {
                    "send" => Receive(root.GetProperty("message")),
                    "storage" => Store(root),
                    // The page's answers to LINK's messages: a request it took is followed through its events.
                    _ => null
                };
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                response = new { ok = false, error = "LINK could not read the message." };
            }
        }
        catch (JsonException)
        {
            return;
        }

        if (id is { } replyTo) Post(new { kind = "reply", replyTo, response });
    }

    private object Receive(JsonElement message) => TextOf(message, "type") switch
    {
        "link-announce" => Announce(message),
        "link-event" => Report(message.GetProperty("event").Deserialize<ChatPageEvent>(_json) ??
                               throw new JsonException("An event is missing.")),
        _ => new { ok = false, error = "LINK does not know this message." }
    };

    /// <summary>The companion says what its page shows: the conversation, whether it is busy and signed in.</summary>
    private object Announce(JsonElement message)
    {
        var instanceId = TextOf(message, "instanceId") ?? string.Empty;
        var conversation = ConversationOf(message);
        bool? signedIn = message.TryGetProperty("signedIn", out var signed) && signed.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? signed.GetBoolean()
            : null;
        var busy = message.TryGetProperty("busy", out var request) && request.ValueKind == JsonValueKind.String;
        Companion? previous;
        lock (_gate)
        {
            previous = _companion;
            // A page that has not rendered yet cannot tell; until it can, the last word stands.
            _companion = new Companion(instanceId, conversation, signedIn ?? previous?.SignedIn, busy, Stopwatch.GetTimestamp());
            _loadError = null;
            if (conversation is not null) Remember(conversation);
        }

        Signal();
        // A companion new to the page (it was reloaded) takes over the requests waiting there at once.
        if (conversation is not null && previous?.InstanceId != instanceId) _broker.OfferAgain(conversation.ConversationId);
        Pump();
        return new { ok = true };
    }

    private object Report(ChatPageEvent pageEvent) =>
        _broker.Report(pageEvent, out var reason) ? new { ok = true } : new { ok = false, status = 404, reason };

    /// <summary>The companion's storage: where a prompt stands (kept for this session) and the chat modes it set (kept).</summary>
    private object? Store(JsonElement root)
    {
        switch (TextOf(root, "op"))
        {
            case "get":
                var wanted = KeysOf(root.GetProperty("keys"));
                lock (_gate)
                {
                    return (wanted ?? [.. _storage.Keys])
                        .Where(_storage.ContainsKey)
                        .Distinct()
                        .ToDictionary(key => key, key => _storage[key]);
                }
            case "set":
                var items = root.GetProperty("items");
                if (items.ValueKind != JsonValueKind.Object) return null;
                var keep = false;
                lock (_gate)
                {
                    foreach (var item in items.EnumerateObject())
                    {
                        if (!IsStorageKey(item.Name) || item.Value.GetRawText().Length > MaxStoredValueLength) continue;
                        if (!_storage.ContainsKey(item.Name) && _storage.Count >= MaxStoredEntries) continue;
                        _storage[item.Name] = item.Value.Clone();
                        keep |= IsKeptKey(item.Name);
                    }
                }

                if (keep) Save();
                return null;
            case "remove":
                var removed = false;
                lock (_gate)
                {
                    foreach (var key in KeysOf(root.GetProperty("keys")) ?? [])
                    {
                        removed |= _storage.Remove(key) && IsKeptKey(key);
                    }
                }

                if (removed) Save();
                return null;
            default:
                return null;
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            var kept = _storage.Where(pair => IsKeptKey(pair.Key)).ToDictionary();
            // Written off the page's thread, one after another, so the file ends with the latest modes.
            _saving = _saving.ContinueWith(_ => Write(kept), TaskScheduler.Default);
        }
    }

    private void Write(Dictionary<string, JsonElement> kept)
    {
        try
        {
            AtomicJsonFile.Write(_stateFile, kept, _json);
        }
        catch (IOException)
        {
            // The modes are only a convenience: the companion checks the chat's mode anyway.
        }
        catch (UnauthorizedAccessException)
        {
            // The modes are only a convenience: the companion checks the chat's mode anyway.
        }
    }

    private void OnRequestQueued(string conversationId) => Pump();

    private void OnTimer()
    {
        Pump();
        RefreshIfStale();
    }

    /// <summary>
    /// Hands the page the requests for the conversation it shows. While LINK waits for an answer, it looks again every
    /// few moments: a request the page went quiet about is offered again.
    /// </summary>
    private void Pump()
    {
        IChatPage? page;
        string? conversationId;
        lock (_gate)
        {
            if (_disposed) return;
            page = _page;
            // A page without a conversation is a new chat, and takes the prompt that starts one.
            conversationId = _companion is { } companion && IsFresh(companion)
                ? companion.Conversation?.ConversationId ?? string.Empty
                : null;
        }

        if (page is not null && conversationId is not null)
        {
            while (_broker.NextDelivery(conversationId) is { } delivery)
            {
                Post(new
                {
                    kind = "message",
                    id = Interlocked.Increment(ref _nextMessageId),
                    message = new { type = "link-request", request = delivery }
                });
            }
        }

        lock (_gate)
        {
            if (!_disposed) _timer.Change(_broker.HasPending ? _deliveryInterval : IdleCheck, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Loads a page kept for hours afresh, while nothing waits on it and the user does not look at it.</summary>
    private void RefreshIfStale()
    {
        IChatPage? page;
        Uri address;
        lock (_gate)
        {
            if (_page is null || _companion is not { Busy: false } companion || _broker.HasPending ||
                Since(_documentStartedAt) < PageLifetime)
            {
                return;
            }

            page = _page;
            address = Uri.TryCreate(companion.Conversation?.Url, UriKind.Absolute, out var url) && ChatPageAddress.IsChatGpt(url)
                ? url
                : ChatPageAddress.Home;
        }

        if (!page.IsRevealed) page.Navigate(address);
    }

    private void Post(object message)
    {
        IChatPage? page;
        lock (_gate) page = _page;
        page?.Post(JsonSerializer.Serialize(message, _json));
    }

    private void Signal()
    {
        TaskCompletionSource heard;
        lock (_gate)
        {
            heard = _heard;
            _heard = NewSignal();
        }

        heard.TrySetResult();
    }

    /// <summary>Keeps the conversation, newest first; the oldest one goes when there are too many.</summary>
    private void Remember(AvailableChatConversation conversation)
    {
        _conversations[conversation.ConversationId] = conversation;
        if (_conversations.Count <= MaxConversations) return;
        var oldest = _conversations.Values.MinBy(known => known.LastSeenAt)!;
        _conversations.Remove(oldest.ConversationId);
    }

    private bool HasToken(JsonElement root) =>
        root.TryGetProperty("token", out var token) &&
        token.ValueKind == JsonValueKind.String &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token.GetString()!), _token);

    private static AvailableChatConversation? ConversationOf(JsonElement message)
    {
        if (!message.TryGetProperty("conversation", out var conversation) || conversation.ValueKind != JsonValueKind.Object) return null;
        var id = TextOf(conversation, "conversationId");
        var url = TextOf(conversation, "url");
        if (!ChatPageAddress.IsConversationId(id) || !ChatPageAddress.IsChatGpt(url)) return null;
        var title = TextOf(conversation, "title")?.Trim();
        return new AvailableChatConversation
        {
            ConversationId = id,
            Title = string.IsNullOrEmpty(title) ? "ChatGPT conversation" : title[..Math.Min(title.Length, 200)],
            Url = url!,
            LastSeenAt = DateTimeOffset.Now
        };
    }

    /// <summary>The keys a storage call names: null for all of them.</summary>
    private static IReadOnlyList<string>? KeysOf(JsonElement keys) => keys.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => [keys.GetString()!],
        JsonValueKind.Array => keys.EnumerateArray()
            .Where(key => key.ValueKind == JsonValueKind.String)
            .Select(key => key.GetString()!)
            .ToArray(),
        _ => []
    };

    private static string? TextOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsFresh(Companion companion) => Since(companion.HeardAt) < CompanionLifetime;

    private static bool IsStorageKey(string key) => StorageKeyPattern().IsMatch(key);

    /// <summary>The chat modes the companion set are kept across restarts; where a prompt stands only for the session.</summary>
    private static bool IsKeptKey(string key) =>
        IsStorageKey(key) && (key.StartsWith("effort:", StringComparison.Ordinal) || key.StartsWith("mode:", StringComparison.Ordinal));

    private static TimeSpan Since(long timestamp) => timestamp == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(timestamp);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [GeneratedRegex("^(request|effort|mode):[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex StorageKeyPattern();

    /// <summary>What the page's companion last said, and when.</summary>
    private sealed record Companion(
        string InstanceId,
        AvailableChatConversation? Conversation,
        bool? SignedIn,
        bool Busy,
        long HeardAt);
}
