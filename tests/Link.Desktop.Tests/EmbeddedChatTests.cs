using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>
/// LINK's embedded ChatGPT page as its companion sees it: announcements, requests handed over the page's message
/// channel, events and storage calls answered. The page itself is played by the test.
/// </summary>
internal static class EmbeddedChatTests
{
    public static async Task RunAsync()
    {
        await StreamedAnswerAsync();
        await OtherChatAsync();
        await LostPageAsync();
        await CancelAsync();
        await RedeliveryAsync();
        await CheckChatAsync();
        await ForeignMessagesAsync();
        await StorageAsync();
        await SignInAsync();
        await LoadFailureAsync();
        await NewChatAsync();
        await NewChatSignedOutAsync();
        await StalePageAsync();
        await RestAsync();
        NewChatPlaces();
        ComposedScript();
    }

    /// <summary>
    /// A new chat in the linked chat's ChatGPT project: the page opens the project's page, the prompt goes without a
    /// conversation, and the conversation ChatGPT starts for it is reported and followed, e.g. to a reloaded page.
    /// </summary>
    private static async Task NewChatAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-1");
        var next = rig.Chat.NewChat(EmbeddedRig.ProjectLink);
        Program.Equal("a new chat starts on the page of the current chat's project",
            (next.IsNew, next.Url), (true, "https://chatgpt.com/g/g-p-0123456789abcdef0123456789abcdef/project"));
        await Program.ThrowsAsync<InvalidOperationException>("a new chat has nothing to check yet",
            () => rig.Chat.CheckAsync(next, CancellationToken.None));

        var answer = rig.SendAsync("Hi", link: next);
        await Program.EventuallyAsync("the page opens the project's new chat", () => rig.Page.Navigations.LastOrDefault()?.AbsoluteUri, next.Url);
        Program.Equal("nothing goes to the chat the page showed before", rig.Page.Requests.Count, 0);
        rig.Page.StartDocument();
        await rig.Page.AnnounceNewChatAsync("copy-2");
        var request = await rig.Page.NextRequestAsync();
        Program.Equal("the prompt goes to the new chat without a conversation", request.ConversationId, string.Empty);

        await rig.Page.ReportAsync(request.Id, "waiting");
        var started = new { conversationId = "chat-9", title = "Рассказ", url = "https://chatgpt.com/g/g-p-0123456789abcdef0123456789abcdef-link/c/chat-9" };
        await rig.Page.ReportAsync(request.Id, "receiving", "Hel", started);
        await Program.EventuallyAsync("the conversation ChatGPT started is named",
            () => rig.Progress.Find(ChatProgressState.ChatStarted)?.StartedConversation?.ConversationId, "chat-9");
        await rig.Page.ReportAsync(request.Id, "receiving", "Hello", started);
        Program.Equal("and only once", rig.Progress.States.Split(", ").Count(state => state == "ChatStarted"), 1);

        // The page reloads mid-answer: the request follows the new conversation there, as a sent prompt to watch.
        rig.Page.StartDocument();
        await rig.Page.AnnounceAsync("chat-9", instance: "copy-3");
        var again = await rig.Page.NextRequestAsync(within: EmbeddedRig.Timings.Redeliver / 2);
        Program.Equal("a reloaded page gets the prompt in its new conversation", (again.Id, again.ConversationId, again.Sent),
            (request.Id, "chat-9", true));
        await rig.Page.ReportAsync(again.Id, "completed", "Hello.");
        Program.Equal("the new chat's answer arrives", (await answer).Text, "Hello.");
    }

    private static async Task NewChatSignedOutAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-1");
        var answer = rig.SendAsync("Hi", link: rig.Chat.NewChat(null));
        await Program.EventuallyAsync("the page opens ChatGPT's home for a chat outside projects",
            () => rig.Page.Navigations.LastOrDefault()?.AbsoluteUri, "https://chatgpt.com/");
        rig.Page.StartDocument();
        await rig.Page.AnnounceNewChatAsync("copy-2", signedIn: false);
        var error = await ErrorOfAsync(answer);
        Program.Equal("a new chat without a signed-in account says so", error?.StartsWith("ChatGPT is not signed in", StringComparison.Ordinal), true);
    }

    /// <summary>A page kept for hours is loaded afresh, but only while nothing waits on it and the user does not look at it.</summary>
    private static async Task StalePageAsync()
    {
        var aged = new PageTimings(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        using (var rig = new EmbeddedRig(pageTimings: aged))
        {
            rig.Page.StartDocument();
            await rig.Page.AnnounceAsync("chat-1");
            await Program.EventuallyAsync("an idle page past its lifetime is loaded again, on its chat",
                () => rig.Page.Navigations.FirstOrDefault()?.AbsoluteUri, "https://chatgpt.com/c/chat-1");
        }

        using (var rig = new EmbeddedRig(pageTimings: aged))
        {
            rig.Page.StartDocument();
            rig.Page.Reveal();
            await rig.Page.AnnounceAsync("chat-1");
            await Task.Delay(400);
            Program.Equal("a page the user looks at is left alone", rig.Page.Navigations.Count, 0);
        }

        using (var rig = new EmbeddedRig(pageTimings: aged))
        {
            rig.Page.StartDocument();
            await rig.Page.AnnounceAsync("chat-1", busy: Guid.NewGuid().ToString());
            await Task.Delay(400);
            Program.Equal("and so is a page busy with an answer", rig.Page.Navigations.Count, 0);
        }
    }

    /// <summary>A page LINK does not need rests; it is awake before it gets a request and while LINK waits on it.</summary>
    private static async Task RestAsync()
    {
        using var rig = new EmbeddedRig(pageTimings: EmbeddedRig.PageTimingsForTests with { Rest = TimeSpan.FromMilliseconds(200) });
        await rig.Page.AnnounceAsync("chat-1");
        Program.Equal("a page just started is awake", rig.Page.IsResting, false);
        await Program.EventuallyAsync("a page nobody needs rests", () => rig.Page.IsResting, true);

        var answer = rig.SendAsync("Hi");
        var request = await rig.Page.NextRequestAsync();
        Program.Equal("the page is woken before it gets the request", rig.Page.RestingWhenRequested, false);
        await rig.Page.ReportAsync(request.Id, "waiting");
        await Task.Delay(400);
        Program.Equal("it stays awake while LINK waits for the answer", rig.Page.IsResting, false);
        await rig.Page.ReportAsync(request.Id, "completed", "Hello.");
        await answer;
        Program.Equal("and is awake for a follow-up", rig.Page.IsResting, false);
        await Program.EventuallyAsync("it rests again once LINK has not needed it for a while", () => rig.Page.IsResting, true);
    }

    private static void NewChatPlaces()
    {
        Program.Equal("a chat outside projects gets its new one on ChatGPT's home",
            ChatPageAddress.NewChatPlace(EmbeddedRig.Link).AbsoluteUri, "https://chatgpt.com/");
        Program.Equal("a project's new chat stays in that project",
            ChatPageAddress.NewChatPlace(new ChatConversationLink
            {
                TransportId = ChatTransports.Embedded,
                ConversationId = string.Empty,
                Title = "New chat",
                Url = "https://chatgpt.com/g/g-p-0123456789abcdef0123456789abcdef/project"
            }).AbsoluteUri,
            "https://chatgpt.com/g/g-p-0123456789abcdef0123456789abcdef/project");
        Program.Equal("a link outside ChatGPT never becomes a place", ChatPageAddress.NewChatPlace(new ChatConversationLink
        {
            TransportId = ChatTransports.Embedded,
            ConversationId = "chat-1",
            Title = "Chat",
            Url = "https://evil.example/g/g-p-0123456789abcdef0123456789abcdef-x/c/chat-1"
        }).AbsoluteUri, "https://chatgpt.com/");
    }

    private static async Task StreamedAnswerAsync()
    {
        using var rig = new EmbeddedRig();
        Program.Equal("a page that has not spoken yet is connecting", rig.State, ChatConnectionState.Connecting);
        await rig.Page.AnnounceAsync("chat-1");
        Program.Equal("a signed-in page with a companion is connected", rig.State, ChatConnectionState.ConversationAvailable);
        Program.Equal("the page's chat can be linked", rig.Chat.GetAvailableConversations().Single().ConversationId, "chat-1");

        var answer = rig.SendAsync("Hi");
        var request = await rig.Page.NextRequestAsync();
        Program.Equal("the prompt is handed to the page", (request.Prompt, request.Attempt, request.Sent), ("Hi", 1, false));
        Program.Equal("a page on the linked chat is not sent elsewhere", rig.Page.Navigations.Count, 0);

        await rig.Page.ReportAsync(request.Id, "sending");
        await rig.Page.ReportAsync(request.Id, "waiting");
        await rig.Page.ReportAsync(request.Id, "receiving", "Hello");
        Program.Equal("an event is taken", await rig.Page.ReportAsync(request.Id, "completed", "Hello."), "ok");
        Program.Equal("the finished answer arrives", (await answer).Text, "Hello.");
        Program.Equal("the states follow the answer", rig.Progress.States, "Sending, Sending, Waiting, Receiving, Completed");
        Program.Equal("a late event is turned away as answered", await rig.Page.ReportAsync(request.Id, "heartbeat"), "404 completed");
    }

    /// <summary>A page on another chat is taken to the linked one before the prompt is handed over.</summary>
    private static async Task OtherChatAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-2");
        var answer = rig.SendAsync("Hi");
        await Program.EventuallyAsync("the page is taken to the linked chat", () => rig.Page.Navigations.LastOrDefault()?.AbsoluteUri,
            "https://chatgpt.com/c/chat-1");
        Program.Equal("nothing is handed over before it is there", rig.Page.Requests.Count, 0);

        rig.Page.StartDocument();
        await rig.Page.AnnounceAsync("chat-1", instance: "copy-2");
        var request = await rig.Page.NextRequestAsync();
        await rig.Page.ReportAsync(request.Id, "completed", "Done.");
        Program.Equal("the prompt is answered in the linked chat", (await answer).Text, "Done.");
    }

    /// <summary>No request waits forever: a page that never answers or goes quiet for good ends it with an error.</summary>
    private static async Task LostPageAsync()
    {
        using (var rig = new EmbeddedRig())
        {
            await rig.Page.AnnounceAsync("chat-1");
            var error = await ErrorOfAsync(rig.SendAsync("Hi"));
            Program.Equal("a page that never answers ends the request",
                error?.StartsWith("The ChatGPT page did not respond", StringComparison.Ordinal), true);
        }

        using (var rig = new EmbeddedRig())
        {
            await rig.Page.AnnounceAsync("chat-1");
            var answer = rig.SendAsync("Hi");
            var request = await rig.Page.NextRequestAsync();
            await rig.Page.ReportAsync(request.Id, "waiting");
            var error = await ErrorOfAsync(answer);
            Program.Equal("a page gone quiet for good ends the request", error?.StartsWith("Lost contact", StringComparison.Ordinal), true);
            Program.Equal("its page is told the request failed", await rig.Page.ReportAsync(request.Id, "heartbeat"), "404 failed");
        }

        using (var rig = new EmbeddedRig())
        {
            await rig.Page.AnnounceAsync("chat-1");
            var answer = rig.SendAsync("Hi");
            var request = await rig.Page.NextRequestAsync();
            await rig.Page.ReportAsync(request.Id, "error", error: "ChatGPT did not take the prompt.", diagnostics: "{\"version\":\"1\"}");
            Program.Equal("the page's error ends the request", await ErrorOfAsync(answer), "ChatGPT did not take the prompt.");
            Program.Equal("an error carries what the page saw", rig.Progress.Last?.Diagnostics, "{\"version\":\"1\"}");
        }
    }

    private static async Task CancelAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-1");
        using var cancellation = new CancellationTokenSource();
        var answer = rig.SendAsync("Hi", cancellation.Token);
        var request = await rig.Page.NextRequestAsync();
        await rig.Page.ReportAsync(request.Id, "waiting");
        cancellation.Cancel();
        await Program.ThrowsAsync<OperationCanceledException>("Stop ends the request", () => answer);
        Program.Equal("the page is told to stop ChatGPT", await rig.Page.ReportAsync(request.Id, "receiving", "Hel"), "404 cancelled");
    }

    /// <summary>A page that goes quiet gets the request again; a reloaded one gets it at once.</summary>
    private static async Task RedeliveryAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-1");
        var answer = rig.SendAsync("Hi", attachment: true);
        var first = await rig.Page.NextRequestAsync();
        var second = await rig.Page.NextRequestAsync();
        Program.Equal("an unanswered request is offered again", (second.Id, second.Attempt, second.Attachments), (first.Id, 2, 1));

        await rig.Page.ReportAsync(second.Id, "waiting");
        rig.Page.StartDocument();
        await rig.Page.AnnounceAsync("chat-1", instance: "copy-2");
        var third = await rig.Page.NextRequestAsync(within: EmbeddedRig.Timings.Redeliver / 2);
        Program.Equal("a reloaded page gets the sent prompt at once, without its images, to watch it",
            (third.Id, third.Sent, third.Attachments), (first.Id, true, 0));
        await rig.Page.ReportAsync(third.Id, "completed", "Found it.");
        Program.Equal("the picked-up prompt is answered", (await answer).Text, "Found it.");
    }

    private static async Task CheckChatAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-1");
        var check = rig.Chat.CheckAsync(EmbeddedRig.Link, CancellationToken.None);
        var read = await rig.Page.NextRequestAsync();
        Program.Equal("Check chat asks the page to read the chat", read.Kind, "read");
        await rig.Page.CallAsync(new { type = "link-event", @event = new { requestId = read.Id, state = "snapshot", text = "Answer.", generating = false } });
        Program.Equal("the chat's answer comes back", (await check).Text, "Answer.");
    }

    /// <summary>Only the page's companion is listened to: other messages are dropped unread.</summary>
    private static async Task ForeignMessagesAsync()
    {
        using var rig = new EmbeddedRig();
        rig.Page.Send(new { token = "guessed", id = 1, kind = "send", message = new { type = "link-announce", conversation = Conversation("chat-1"), signedIn = true, instanceId = "x" } });
        rig.Page.Send(new { id = 2, kind = "send", message = new { type = "link-announce", conversation = Conversation("chat-1"), signedIn = true, instanceId = "x" } });
        rig.Page.Receive("not json");
        await Task.Delay(50);
        Program.Equal("a message without the session's token gets no answer", rig.Page.Replies.Count, 0);
        Program.Equal("and changes nothing", rig.Chat.GetAvailableConversations().Count, 0);

        await rig.Page.AnnounceAsync("chat-1", url: "https://evil.example/c/chat-1");
        Program.Equal("a conversation outside ChatGPT is not offered for linking", rig.Chat.GetAvailableConversations().Count, 0);
    }

    /// <summary>Where a prompt stands is kept for the session; the chat modes LINK set survive a restart.</summary>
    private static async Task StorageAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.CallAsync(new { op = "set", items = new Dictionary<string, object>
        {
            ["effort:chat-1"] = "fast",
            ["mode:chat-1"] = "Высокий",
            ["request:0f1e"] = new { phase = "sent" },
            ["anything"] = "dropped"
        } }, kind: "storage");
        var all = await rig.Page.CallAsync(new { op = "get", keys = (string[]?)null }, kind: "storage");
        Program.Equal("the page gets back what it stored", string.Join(",", all.EnumerateObject().Select(item => item.Name).Order()),
            "effort:chat-1,mode:chat-1,request:0f1e");
        var mode = await rig.Page.CallAsync(new { op = "get", keys = new[] { "mode:chat-1", "effort:chat-2" } }, kind: "storage");
        Program.Equal("a get returns what it names", mode.GetProperty("mode:chat-1").GetString(), "Высокий");
        await rig.Page.CallAsync(new { op = "remove", keys = "mode:chat-1" }, kind: "storage");

        await Program.EventuallyAsync("only the chat modes are kept on disk", () => rig.StoredKeys(), "effort:chat-1");
        using var restarted = new EmbeddedRig(rig.StateFile);
        var kept = await restarted.Page.CallAsync(new { op = "get", keys = (string[]?)null }, kind: "storage");
        Program.Equal("after a restart the page finds the modes", string.Join(",", kept.EnumerateObject().Select(item => item.Name)),
            "effort:chat-1");
    }

    private static async Task SignInAsync()
    {
        using var rig = new EmbeddedRig();
        await rig.Page.AnnounceAsync("chat-2", signedIn: false);
        Program.Equal("a page that offers to log in needs the user to sign in", rig.State, ChatConnectionState.SignInRequired);
        await Program.ThrowsAsync<InvalidOperationException>("a prompt fails at once while nobody is signed in", () => rig.SendAsync("Hi"));
        await rig.Page.AnnounceAsync("chat-2", signedIn: null);
        Program.Equal("a page that cannot tell yet keeps the last word", rig.State, ChatConnectionState.SignInRequired);
    }

    private static async Task LoadFailureAsync()
    {
        using var rig = new EmbeddedRig();
        rig.Page.StartDocument();
        rig.Page.Fail("InternetDisconnected");
        Program.Equal("a page that could not load shows ChatGPT as unavailable", rig.State, ChatConnectionState.BridgeUnavailable);

        // The connection is back: a prompt loads the page again instead of failing on the old error.
        var answer = rig.SendAsync("Hi");
        await Program.EventuallyAsync("the page is loaded again for the prompt", () => rig.Page.Navigations.Count, 1);
        rig.Page.StartDocument();
        await rig.Page.AnnounceAsync("chat-1");
        var request = await rig.Page.NextRequestAsync();
        await rig.Page.ReportAsync(request.Id, "completed", "Back.");
        Program.Equal("and the prompt is answered", (await answer).Text, "Back.");

        await rig.Page.AnnounceAsync("chat-2");
        var failing = rig.SendAsync("Hi");
        await Program.EventuallyAsync("the page is sent to the linked chat", () => rig.Page.Navigations.Count, 2);
        rig.Page.StartDocument();
        rig.Page.Fail("HostNameNotResolved");
        var error = await ErrorOfAsync(failing);
        Program.Equal("a page that cannot load says why", error?.Contains("HostNameNotResolved", StringComparison.Ordinal), true);
        Program.Equal("and LINK shows ChatGPT as unavailable", rig.State, ChatConnectionState.BridgeUnavailable);
    }

    /// <summary>The page's script: the companion on LINK's end of the channel, only in ChatGPT's own top-level page.</summary>
    private static void ComposedScript()
    {
        var script = ChatPageScript.Compose("secret-token");
        var config = JsonSerializer.Serialize(new { token = "secret-token", version = ChatPageScript.Version });
        Program.Equal("the script runs only in ChatGPT's top-level page",
            script.Contains("if (window.top !== window || location.origin !== \"https://chatgpt.com\") return;", StringComparison.Ordinal), true);
        Program.Equal("LINK's end of the channel gets the token and LINK's version",
            script.Contains($"createLinkHostApi({config})", StringComparison.Ordinal), true);
        Program.Equal("the companion itself is in it", script.Contains("const host = linkHostApi;", StringComparison.Ordinal), true);
        Program.Equal("the channel comes before the companion",
            script.IndexOf("function createLinkHostApi", StringComparison.Ordinal) < script.IndexOf("const startCompanion", StringComparison.Ordinal),
            true);
    }

    private static object Conversation(string id, string? url = null) =>
        new { conversationId = id, title = $"Chat {id}", url = url ?? $"https://chatgpt.com/c/{id}" };

    private static async Task<string?> ErrorOfAsync(Task<ChatResponse> answer)
    {
        try
        {
            await answer;
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>An embedded integration with short waits on a page the test plays.</summary>
    internal sealed class EmbeddedRig : IDisposable
    {
        public static readonly PageTimings PageTimingsForTests = PageTimings.Default with { Delivery = TimeSpan.FromMilliseconds(20) };

        public static readonly BridgeTimings Timings = new(
            Acknowledge: TimeSpan.FromMilliseconds(600),
            Redeliver: TimeSpan.FromMilliseconds(250),
            Silence: TimeSpan.FromMilliseconds(900),
            Request: TimeSpan.FromSeconds(10),
            Read: TimeSpan.FromSeconds(2),
            Watchdog: TimeSpan.FromMilliseconds(20));

        public static readonly ChatConversationLink Link = new()
        {
            TransportId = ChatTransports.Browser,
            ConversationId = "chat-1",
            Title = "Chat chat-1",
            Url = "https://chatgpt.com/c/chat-1"
        };

        /// <summary>The same chat in a ChatGPT project, as the project's chats are addressed.</summary>
        public static readonly ChatConversationLink ProjectLink = new()
        {
            TransportId = ChatTransports.Embedded,
            ConversationId = "chat-1",
            Title = "Chat chat-1",
            Url = "https://chatgpt.com/g/g-p-0123456789abcdef0123456789abcdef-link/c/chat-1"
        };

        private readonly bool _ownsState;
        private readonly List<string> _screenshots = [];

        public EmbeddedRig(string? stateFile = null, PageTimings? pageTimings = null)
        {
            _ownsState = stateFile is null;
            StateFile = stateFile ?? Path.Combine(Path.GetTempPath(), $"link-companion-state-{Guid.NewGuid():N}.json");
            Chat = new EmbeddedChatIntegration(StateFile, Timings, pageTimings ?? PageTimingsForTests, FakeChatPage.Token);
            Chat.Attach(Page);
        }

        public EmbeddedChatIntegration Chat { get; }
        public FakeChatPage Page { get; } = new();
        public ProgressLog Progress { get; } = new();
        public string StateFile { get; }
        public ChatConnectionState State => Chat.GetConnectionState(Link);

        public Task<ChatResponse> SendAsync(
            string prompt,
            CancellationToken cancellation = default,
            bool attachment = false,
            ChatConversationLink? link = null,
            IProgress<ChatProgress>? progress = null) =>
            Chat.SendAsync(
                new PromptRequest
                {
                    ProjectId = Guid.NewGuid(),
                    ProjectName = "General",
                    Text = prompt,
                    Context = attachment ? [Screenshot()] : []
                },
                link ?? Link,
                progress ?? Progress,
                cancellation);

        /// <summary>The keys of the companion's storage kept on disk.</summary>
        public string StoredKeys()
        {
            try
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(StateFile));
                return string.Join(",", saved.RootElement.EnumerateObject().Select(item => item.Name));
            }
            catch (IOException)
            {
                return "not written yet";
            }
        }

        public void Dispose()
        {
            Chat.Dispose();
            var files = _ownsState ? [StateFile, $"{StateFile}.bak", .. _screenshots] : _screenshots;
            foreach (var file in files)
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private ContextItem Screenshot()
        {
            var path = Path.Combine(Path.GetTempPath(), $"link-shot-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
            _screenshots.Add(path);
            return new ContextItem { Type = ContextItemType.Screenshot, Label = "Screen", FilePath = path, MimeType = "image/png" };
        }
    }
}

/// <summary>
/// The embedded page as its host sees it, played by a test: what LINK posts to it, and what its companion sends back
/// with the session's token.
/// </summary>
internal sealed class FakeChatPage : IChatPage
{
    public const string Token = "test-token";
    private readonly List<JsonElement> _posted = [];
    private readonly List<Uri> _navigations = [];
    private int _nextCall;
    private int _requestsTaken;

    public event Action<string>? MessageReceived;
    public event Action? DocumentStarted;
    public event Action<string>? LoadFailed;

    public bool IsRevealed { get; private set; }

    public bool IsResting { get; private set; }

    /// <summary>Whether the page rested when LINK last handed it a request.</summary>
    public bool? RestingWhenRequested { get; private set; }

    public IReadOnlyList<Uri> Navigations
    {
        get
        {
            lock (_navigations) return [.. _navigations];
        }
    }

    /// <summary>The requests LINK has handed to the page so far.</summary>
    public IReadOnlyList<JsonElement> Requests => Posted("message").Select(message => message.GetProperty("message")).ToArray();

    public IReadOnlyList<JsonElement> Replies => Posted("reply");

    public void Post(string json)
    {
        using var message = JsonDocument.Parse(json);
        lock (_posted) _posted.Add(message.RootElement.Clone());
        if (message.RootElement.GetProperty("kind").GetString() == "message") RestingWhenRequested = IsResting;
    }

    public void Navigate(Uri address)
    {
        lock (_navigations) _navigations.Add(address);
    }

    public void Reveal() => IsRevealed = true;

    public void Rest(bool resting) => IsResting = resting;

    public void StartDocument() => DocumentStarted?.Invoke();

    public void Fail(string error) => LoadFailed?.Invoke(error);

    public void Receive(string text) => MessageReceived?.Invoke(text);

    public void Send(object message) => Receive(JsonSerializer.Serialize(message));

    /// <summary>A call of the companion's stand-in API: LINK's reply, or a failure when none comes.</summary>
    public async Task<JsonElement> CallAsync(object fields, string kind = "send")
    {
        var id = Interlocked.Increment(ref _nextCall);
        // A companion message travels whole; a storage call carries its fields beside the envelope's.
        var call = kind == "send"
            ? new Dictionary<string, object?> { ["message"] = fields }
            : JsonSerializer.SerializeToElement(fields).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value);
        call["token"] = Token;
        call["id"] = id;
        call["kind"] = kind;
        Send(call);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var reply = Replies.FirstOrDefault(candidate => candidate.GetProperty("replyTo").GetInt32() == id);
            if (reply.ValueKind != JsonValueKind.Undefined) return reply.GetProperty("response");
            await Task.Delay(5);
        }

        throw new TimeoutException("LINK did not answer the page.");
    }

    public Task<JsonElement> AnnounceAsync(
        string conversationId,
        bool? signedIn = true,
        string instance = "copy-1",
        string? url = null,
        string? busy = null) =>
        CallAsync(new
        {
            type = "link-announce",
            conversation = new { conversationId, title = $"Chat {conversationId}", url = url ?? $"https://chatgpt.com/c/{conversationId}" },
            busy,
            signedIn,
            instanceId = instance
        });

    /// <summary>The companion of a new chat's page, which shows no conversation yet.</summary>
    public Task<JsonElement> AnnounceNewChatAsync(string instance, bool? signedIn = true) =>
        CallAsync(new { type = "link-announce", conversation = (object?)null, busy = (string?)null, signedIn, instanceId = instance });

    /// <summary>
    /// Reports on a request as the companion does: "ok", or the status and reason LINK turned it away with. A new chat's
    /// events carry the conversation ChatGPT started for it.
    /// </summary>
    public async Task<string> ReportAsync(
        Guid requestId,
        string state,
        string? text = null,
        object? conversation = null,
        string? error = null,
        string? diagnostics = null)
    {
        var response = await CallAsync(new
        {
            type = "link-event",
            @event = new { requestId, state, text, conversation, error, diagnostics }
        });
        return response.GetProperty("ok").GetBoolean()
            ? "ok"
            : $"{response.GetProperty("status").GetInt32()} {response.GetProperty("reason").GetString()}";
    }

    /// <summary>The next request LINK hands to the page, within a few seconds or the given time.</summary>
    public async Task<Delivery> NextRequestAsync(TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var requests = Requests;
            if (requests.Count > _requestsTaken)
            {
                var request = requests[_requestsTaken++].GetProperty("request");
                return new Delivery(
                    request.GetProperty("requestId").GetGuid(),
                    request.GetProperty("conversationId").GetString(),
                    request.GetProperty("kind").GetString(),
                    request.GetProperty("prompt").GetString(),
                    request.GetProperty("attempt").GetInt32(),
                    request.GetProperty("sent").GetBoolean(),
                    request.GetProperty("attachments").GetArrayLength());
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("No request was handed to the page.");
    }

    private IReadOnlyList<JsonElement> Posted(string kind)
    {
        lock (_posted)
        {
            return _posted.Where(message => message.GetProperty("kind").GetString() == kind).ToArray();
        }
    }

    public sealed record Delivery(Guid Id, string? ConversationId, string? Kind, string? Prompt, int Attempt, bool Sent, int Attachments);
}

/// <summary>Progress kept in the order LINK reports it.</summary>
internal sealed class ProgressLog : IProgress<ChatProgress>
{
    private readonly List<ChatProgress> _reports = [];

    public string States
    {
        get
        {
            lock (_reports) return string.Join(", ", _reports.Select(report => report.State));
        }
    }

    public ChatProgress? Last
    {
        get
        {
            lock (_reports) return _reports.LastOrDefault();
        }
    }

    public ChatProgress? Find(ChatProgressState state)
    {
        lock (_reports) return _reports.FirstOrDefault(report => report.State == state);
    }

    public void Report(ChatProgress value)
    {
        lock (_reports) _reports.Add(value);
    }
}
