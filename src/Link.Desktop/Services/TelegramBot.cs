using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Link's tasks in the user's own Telegram bot. A task the bot writes about has one card that changes silently as the task
/// goes, in a topic of its own when the user turned topics on for the bot; when the task needs the user, a new card comes
/// that notifies, with the buttons it waits for (the agent's options, Allow or Deny, Stop, No-stop, the changes). Words and
/// pictures answer a task, /new starts one once its project and agent are picked, and the pictures a run makes follow its
/// card. Link asks Telegram for news itself (long polling), so nothing reaches this computer from outside, whatever VPN
/// the phone uses. Only the chat paired with a code from Settings is heard; what is said goes through Telegram's servers.
/// </summary>
public sealed class TelegramBot : IDisposable
{
    /// <summary>
    /// A call of the Bot API: its method and body (a <see cref="FileBody"/> uploads a file); the result, or null when
    /// Telegram refused it.
    /// </summary>
    public delegate Task<JsonElement?> Api(string method, object body, CancellationToken token);

    /// <summary>A file the user sent, by the path getFile gave for it; null when it could not be fetched.</summary>
    public delegate Task<byte[]?> Download(string path, CancellationToken token);

    /// <summary>A call's fields, with a file to upload under <paramref name="Field"/>.</summary>
    public sealed record FileBody(IReadOnlyDictionary<string, string> Fields, string Field, string Name, byte[] Bytes);

    /// <summary>How long Settings' pairing code is good.</summary>
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);

    private const int MaxText = 3500;
    // An alert's words up to this go on its card, leaving the card room for the task's state and question.
    private const int AlertOnCard = 2500;
    private const int MessagesKept = 200;
    private const int PlanShown = 15;
    private const int CardsKept = 100;
    private const int ProjectsShown = 20;
    // One picture tells what a run made: the one its answer names, or the newest; the rest are in Link's thread.
    private const int PicturesPerRun = 1;
    // Telegram takes a photo up to 10 MB and any other file up to 50 MB.
    private const int MaxPhoto = 10 << 20;
    private const int MaxFile = 50 << 20;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuickAnswer = TimeSpan.FromMilliseconds(400);
    // Changes come in bursts: the cards are edited once they settle, and every half minute while a task works.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WhileWorking = TimeSpan.FromSeconds(30);

    private readonly Api _api;
    private readonly Download _download;
    private readonly IRemoteHost _host;
    private readonly Func<long?> _chat;
    private readonly Action<long> _paired;
    private readonly Action<string> _log;
    private readonly string _pictures;
    private readonly string _cardsFile;
    private readonly Func<Guid, int, Task<(string? Url, string? Problem)>>? _pageLink;
    private readonly Lock _lock = new();
    // One thing at a time with Telegram: an update, an alert, or the cards following their tasks.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // A task changed: the cards follow once things settle.
    private readonly SemaphoreSlim _changed = new(0, 1);
    // Which task each of Link's messages is about: a reply to one goes to its task.
    private readonly Dictionary<long, Guid> _messages = [];
    private TelegramCards _cards;
    private CancellationTokenSource? _stop;
    private string? _code;
    private DateTimeOffset _codeUntil;
    // The task the user's plain words go to: the last one Link posted a card of or the user acted on.
    private Guid? _focus;
    // Whether the user turned topics on for the bot (in @BotFather): each task then has a topic of its own.
    private bool _topics;
    private bool _working;
    private Draft? _draft;
    // A task started from Telegram until its card is posted: its first line, and when it was started.
    private (string Goal, DateTimeOffset At)? _starting;

    /// <param name="chat">The paired chat; null before pairing.</param>
    /// <param name="paired">A chat sent the right code: it is the user's from now on.</param>
    /// <param name="pictures">Where the pictures the user sends are kept.</param>
    /// <param name="cardsFile">Where the cards are kept across restarts.</param>
    /// <param name="pageLink">A page of a task's last run (a model's review window) as a link that opens anywhere, else why not; null: none.</param>
    public TelegramBot(Api api, Download download, IRemoteHost host, Func<long?> chat, Action<long> paired, Action<string> log,
        string pictures, string cardsFile, Func<Guid, int, Task<(string? Url, string? Problem)>>? pageLink = null)
    {
        _pageLink = pageLink;
        _api = api;
        _download = download;
        _host = host;
        _chat = chat;
        _paired = paired;
        _log = log;
        _pictures = pictures;
        _cardsFile = cardsFile;
        _cards = AtomicJsonFile.Read<TelegramCards>(cardsFile) is { } kept && kept.Chat == chat() ? kept.Normalize() : new TelegramCards { Chat = chat() };
    }

    public bool Running => _stop is not null;

    /// <summary>The Bot API over HTTPS with a token kept apart; the token is never logged.</summary>
    public static Api Over(HttpClient http, Func<string?> token, Action<string> log) => async (method, body, cancel) =>
    {
        var key = token() ?? throw new InvalidOperationException("No Telegram bot token");
        using HttpContent content = body is FileBody file ? Multipart(file) : JsonContent.Create(body, options: Json);
        using var response = await http.PostAsync($"https://api.telegram.org/bot{key}/{method}", content, cancel);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel);
        if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return json.RootElement.GetProperty("result").Clone();
        log($"[Telegram] {method} refused: {(json.RootElement.TryGetProperty("description", out var why) ? why.GetString() : response.StatusCode.ToString())}");
        return null;
    };

    /// <summary>The files the user sends, from Telegram's file server with the same token (up to 20 MB there).</summary>
    public static Download FilesOver(HttpClient http, Func<string?> token) => async (path, cancel) =>
    {
        var key = token() ?? throw new InvalidOperationException("No Telegram bot token");
        using var response = await http.GetAsync($"https://api.telegram.org/file/bot{key}/{path}", cancel);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancel) : null;
    };

    private static MultipartFormDataContent Multipart(FileBody file)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in file.Fields) content.Add(new StringContent(value), name);
        content.Add(new ByteArrayContent(file.Bytes), file.Field, file.Name);
        return content;
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _host.RemoteChanged += Changed;
        // Off the window's thread: the bot never holds it up.
        _ = Task.Run(() => PollAsync(token));
        _ = Task.Run(() => FollowAsync(token));
        _log("[Telegram] Bot started");
    }

    public void Stop()
    {
        if (_stop is null) return;
        _host.RemoteChanged -= Changed;
        _stop.Cancel();
        _stop.Dispose();
        _stop = null;
        _log("[Telegram] Bot stopped");
    }

    public void Dispose() => Stop();

    /// <summary>A code to send the bot, good for ten minutes: the chat that sends it becomes the user's.</summary>
    public string NewCode()
    {
        lock (_lock)
        {
            _code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            _codeUntil = DateTimeOffset.Now + CodeLife;
            return _code;
        }
    }

    /// <summary>
    /// A task needs the user or is through: a new card about it that notifies, with the buttons it waits for. A run that
    /// opened a review window sends its link too: reviewing is what the task waits for.
    /// </summary>
    public void Alert(Guid task, string title, string text)
    {
        if (_stop is not { } stop || _chat() is not { } chat) return;
        var token = stop.Token;
        _ = Task.Run(() => GatedAsync(async () =>
        {
            var head = $"<b>{TelegramHtml.Escape(title)}</b>";
            var words = TelegramHtml.FromMarkdown(text);
            if (words.Length <= AlertOnCard) await ShowAsync(chat, task, $"{head}\n\n{words}", fresh: true, token);
            else
            {
                // A long answer goes whole, in messages of its own above the card, which a card would cut off.
                var card = CardOf(task);
                if (await _host.TaskAsync(task) is { } view) card.Topic ??= await TopicAsync(chat, view.Task.Goal, token);
                foreach (var part in TelegramHtml.Parts(text, MaxText)) await SendAsync(chat, card.Topic, part, null, token, html: true);
                await ShowAsync(chat, task, head, fresh: true, token);
            }

            if (_pageLink is not null && await _host.TaskAsync(task) is { Pages.Count: > 0, Task.Working: false }) await SendPageAsync(chat, task, 0, token);
        }, token));
    }

    /// <summary>A review window's link with a button to open it, and the button that sends the user's marks to the task.</summary>
    private async Task<string?> SendPageAsync(long chat, Guid id, int index, CancellationToken token)
    {
        if (_pageLink is null || await _host.TaskAsync(id) is not { } view || view.Pages.ElementAtOrDefault(index) is not { } page) return "The page is gone: open the task again";
        var (url, problem) = await _pageLink(id, index);
        if (url is null) return problem;
        var row = new List<object> { new { text = "🔗 " + page.Label, url } };
        if (page.ReplyLabel is { } reply && view.TakesWords) row.Add(Button(reply, $"pa:{id:N}:{index}"));
        var sent = await SendAsync(chat, _cards.Cards.TryGetValue(id, out var card) && card.Topic > 0 ? card.Topic : null,
            $"{page.Label}: {url}\nOpens in any browser while Link runs; mark the model there, then {page.ReplyLabel ?? "write to the task"}.", [[.. row]], token);
        if (sent is { } posted) Remember(posted, id);
        return null;
    }

    private async Task PollAsync(CancellationToken token)
    {
        long offset = 0;
        var ready = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                ready = ready || await ReadyAsync(token);
                var updates = await _api("getUpdates", new { offset, timeout = 50, allowed_updates = new[] { "message", "callback_query" } }, token);
                if (updates is not { ValueKind: JsonValueKind.Array } list)
                {
                    await Task.Delay(Retry, token);
                    continue;
                }

                foreach (var update in list.EnumerateArray())
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    await HandleAsync(update, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
                                                  or IOException or UnauthorizedAccessException)
            {
                _log($"[Telegram] {exception.GetType().Name}: {exception.Message}");
                try
                {
                    await Task.Delay(Retry, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Whether the user turned topics on for the bot, and its commands in Telegram's menu; false when Telegram did not answer.</summary>
    private async Task<bool> ReadyAsync(CancellationToken token)
    {
        if (await _api("getMe", new { }, token) is not { } me) return false;
        _topics = me.TryGetProperty("has_topics_enabled", out var topics) && topics.ValueKind == JsonValueKind.True;
        await _api("setMyCommands", new { commands = Commands.Select(command => new { command = command.Name, description = command.Words }) }, token);
        return true;
    }

    private static readonly (string Name, string Words)[] Commands =
    [
        ("tasks", "The tasks, newest first"),
        ("new", "A new task: /new and what to do"),
        ("plan", "A new task that plans first"),
        ("team", "A new task the team plans and works"),
        ("auto", "A new task in No-stop"),
        ("spent", "What the agents spent"),
        ("account", "Which Claude account new runs use"),
        ("help", "How the bot works")
    ];

    /// <summary>One update: the user's words, picture or command, or a button pressed; anyone else's is ignored.</summary>
    internal async Task HandleAsync(JsonElement update, CancellationToken token)
    {
        var pressed = update.TryGetProperty("callback_query", out var press) ? new Press(Words(press, "id") ?? string.Empty) : null;
        if (pressed is not null) _ = AnswerSoonAsync(pressed, token);
        await _gate.WaitAsync(token);
        try
        {
            if (pressed is not null)
            {
                if (press.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64() == _chat()) await PressedAsync(press, pressed, token);
            }
            else if (update.TryGetProperty("message", out var message))
            {
                await HeardAsync(message, token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HeardAsync(JsonElement message, CancellationToken token)
    {
        var chat = message.GetProperty("chat").GetProperty("id").GetInt64();
        var text = (Words(message, "text") ?? Words(message, "caption") ?? string.Empty).Trim();
        if (chat != _chat())
        {
            // The code alone, or after /start.
            if (Pairs(text.Split(' ')[^1]))
            {
                _paired(chat);
                // A new chat has none of the old one's cards.
                _cards = new TelegramCards { Chat = chat };
                Save();
                _log("[Telegram] A chat is paired");
                await SendAsync(chat, null, "Linked to Link. " + Help, null, token);
            }

            return;
        }

        // Link answers in the topic it was written in.
        long? topic = message.TryGetProperty("message_thread_id", out var thread) ? thread.GetInt64() : null;
        if (message.TryGetProperty("voice", out _) || message.TryGetProperty("video_note", out _))
        {
            await SendAsync(chat, topic, "Link does not understand voice notes yet: type, or use the keyboard's dictation.", null, token);
            return;
        }

        IReadOnlyList<string> pictures = [];
        if (PictureIn(message) is { } file)
        {
            if (await SavedAsync(file, token) is not { } saved)
            {
                await SendAsync(chat, topic, "Link could not take that picture: send a photo, or a PNG, JPEG, GIF or WebP file under 20 MB.", null, token);
                return;
            }

            pictures = [saved];
        }
        else if (text.Length == 0)
        {
            return;
        }

        var (command, rest) = text.StartsWith('/') ? (text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant(), text.Split(' ', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty) : (string.Empty, text);
        switch (command)
        {
            case "/start" or "/help":
                await SendAsync(chat, topic, Help, null, token);
                return;
            case "/tasks":
                await SendTasksAsync(chat, topic, token);
                return;
            case "/spent":
                var spent = await _host.UsageAsync();
                await SendAsync(chat, topic, spent is null ? "Link has not counted yet: open the Studio on the computer." : $"{spent.Line}\n\n{spent.Hint}", null, token);
                return;
            case "/new" or "/plan" or "/auto" or "/team":
                await DraftAsync(chat, topic, command, rest, pictures, token);
                return;
            case "/account":
                var accounts = await _host.AccountsAsync();
                await SendAsync(chat, topic, accounts.Names.Count < 2 ? "One Claude account: add another in Link's Settings." : "Which Claude account should new runs use?",
                    [.. accounts.Names.Select((name, index) => new[] { Button((name ?? "Main") + (name == accounts.Active ? " ✓" : string.Empty), $"ac:{index}") })], token);
                return;
            case "":
                // Words in a task's topic or in reply to a message about it go to that task; others to the task of the last card.
                if ((TaskIn(message, topic) ?? _focus) is not { } id)
                {
                    await SendAsync(chat, topic, "Which task? Open one in /tasks, or reply to a message about it.", null, token);
                    return;
                }

                var error = await _host.ReplyAsync(id, rest.Length > 0 ? rest : "Look at this picture.", "Telegram", pictures.Count > 0 ? pictures : null);
                // The card changing as the task takes the words says they went: a reaction stayed an unread mark in every topic.
                if (error is not null) await SendAsync(chat, topic, error, null, token);
                return;
            default:
                await SendAsync(chat, topic, Help, null, token);
                return;
        }
    }

    private const string Help =
        "Each task Link writes about has a card here that changes as the task goes; when the task needs you, a new card comes. " +
        "Turn on topics for this bot in @BotFather, and each task gets a topic of its own. Words or a picture answer the task " +
        "of the topic, of the message you reply to, or of the last card. /tasks — the tasks · /new <task> — a new task: pick " +
        "the project and the agent · /plan <task> — plan first · /team <task> — the team plans it · /auto <task> — No-stop · /spent — what the agents spent · " +
        "/account — which Claude account new runs use.";

    private bool Pairs(string text)
    {
        lock (_lock)
        {
            if (_code is null || DateTimeOffset.Now > _codeUntil || text != _code) return false;
            _code = null;
            return true;
        }
    }

    /// <summary>The task of the topic a message is in, or of Link's message it replies to.</summary>
    private Guid? TaskIn(JsonElement message, long? topic)
    {
        if (topic is { } thread && _cards.Cards.Where(card => card.Value.Topic == thread).Select(card => (Guid?)card.Key).FirstOrDefault() is { } inTopic)
        {
            return inTopic;
        }

        return message.TryGetProperty("reply_to_message", out var replied) ? TaskOf(replied.GetProperty("message_id").GetInt64()) : null;
    }

    /// <summary>The file of the picture a message carries: a photo's biggest size, or a file that is an image.</summary>
    private static string? PictureIn(JsonElement message) =>
        message.TryGetProperty("photo", out var sizes) && sizes.GetArrayLength() > 0 ? Words(sizes[sizes.GetArrayLength() - 1], "file_id")
        : message.TryGetProperty("document", out var document) && Words(document, "mime_type")?.StartsWith("image/", StringComparison.Ordinal) == true
            ? Words(document, "file_id")
            : null;

    /// <summary>A picture the user sent, fetched from Telegram and kept with Link's pictures; null when it is none.</summary>
    private async Task<string?> SavedAsync(string file, CancellationToken token)
    {
        if (await _api("getFile", new { file_id = file }, token) is not { } found || Words(found, "file_path") is not { } path) return null;
        if (await _download(path, token) is not { } bytes) return null;
        try
        {
            return ImageFiles.Save(_pictures, bytes, "telegram");
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>/new, /plan, /team or /auto: the project to start in when there is a choice, then the agent.</summary>
    private async Task DraftAsync(long chat, long? topic, string command, string goal, IReadOnlyList<string> pictures, CancellationToken token)
    {
        if (goal.Length == 0)
        {
            await SendAsync(chat, topic, $"Write the task after {command}.", null, token);
            return;
        }

        var projects = (await _host.ProjectsAsync()).Where(project => project.HasWorkspace).OrderByDescending(project => project.Active).Take(ProjectsShown).ToArray();
        if (projects.Length == 0)
        {
            await SendAsync(chat, topic, "No project has a folder to work in: give one a folder on the computer first.", null, token);
            return;
        }

        var draft = new Draft(goal, command == "/plan", command == "/auto", pictures, projects) { Project = projects.Length == 1 ? projects[0] : null, Team = command == "/team" };
        var (text, rows) = Picker(draft);
        draft.Message = await SendAsync(chat, topic, text, rows, token) ?? 0;
        _draft = draft;
    }

    /// <summary>The picker's step: the project, then the agent and how strong.</summary>
    private static (string Text, List<object[]> Rows) Picker(Draft draft)
    {
        var goal = $"«{Short(draft.Goal, 300)}»";
        if (draft.Project is not { } project)
        {
            return ($"{goal}\n\nWhich project?",
                [.. draft.Projects.Select((each, index) => new[] { Button(each.Active ? each.Name + " ✓" : each.Name, $"np:{index}") })]);
        }

        // A team's lead and workers are Settings' own, so a team has no agent to pick.
        if (draft.Team) return ($"{goal} in {project.Name}\n\nThe team plans it: its lead writes the plan, you run it.", [[Button("👥 Team ✓", "nt")], [Button("Plan the team", "na")]]);
        var rows = new List<object[]> { new[] { Button("As Settings say", "na") } };
        rows.AddRange(Enum.GetValues<AgentLevel>().Select(level => Enum.GetValues<AgentProviderKind>()
            .Select(agent => Button($"{(agent == AgentProviderKind.ClaudeCode ? "Claude" : "Codex")} · {level}", $"na:{agent}:{level}")).ToArray()));
        rows.Add([Button("👥 Team", "nt")]);
        return ($"{goal} in {project.Name}\n\nWhich agent, and how strong? Or the team.", rows);
    }

    /// <summary>A picker's button: the project is taken, or the agent, and then the task starts.</summary>
    private async Task<string?> PickedAsync(long chat, long message, string[] parts, CancellationToken token)
    {
        if (_draft is not { } draft || draft.Message != message) return "This one is over: write /new again";
        if (parts[0] is "np" or "nt")
        {
            if (parts[0] == "nt") draft.Team = !draft.Team;
            else if (int.TryParse(parts.ElementAtOrDefault(1), out var index) && draft.Projects.ElementAtOrDefault(index) is { } project) draft.Project = project;
            else return "Unknown button";
            var (text, rows) = Picker(draft);
            await _api("editMessageText", new { chat_id = chat, message_id = message, text, reply_markup = Keyboard(rows) }, token);
            return null;
        }

        _draft = null;
        AgentProviderKind? agent = parts.Length == 3 && Enum.TryParse<AgentProviderKind>(parts[1], out var named) ? named : null;
        AgentLevel? level = parts.Length == 3 && Enum.TryParse<AgentLevel>(parts[2], out var strength) ? strength : null;
        var at = DateTimeOffset.Now;
        var error = await _host.StartAsync(new RemoteNewTask(draft.Project?.Id, draft.Goal, draft.PlanFirst, draft.NoStop, agent, level, draft.Pictures, draft.Team), "Telegram");
        if (error is null) _starting = (draft.Goal.Split('\n')[0].Trim(), at);
        await _api("editMessageText", new
        {
            chat_id = chat,
            message_id = message,
            text = error ?? $"Starting «{Short(draft.Goal, 300)}» in {draft.Project?.Name}: its card comes {(_topics ? "in a topic of its own" : "here")}.",
            reply_markup = Keyboard([])
        }, token);
        return error;
    }

    /// <summary>
    /// A button: it acts on its task and says how it went. On the task's card the card then shows what changed; another
    /// message about the task is left without buttons.
    /// </summary>
    private async Task PressedAsync(JsonElement press, Press pressed, CancellationToken token)
    {
        var parts = (Words(press, "data") ?? string.Empty).Split(':');
        var message = press.GetProperty("message");
        var chat = message.GetProperty("chat").GetProperty("id").GetInt64();
        var pressedOn = message.GetProperty("message_id").GetInt64();
        string? error;
        if (parts[0] is "np" or "nt" or "na")
        {
            error = await PickedAsync(chat, pressedOn, parts, token);
        }
        else if (parts[0] == "ac")
        {
            var accounts = await _host.AccountsAsync();
            var index = parts.Length > 1 && int.TryParse(parts[1], out var picked) && picked >= 0 && picked < accounts.Names.Count ? picked : -1;
            error = index < 0 ? "The accounts have changed: write /account again" : await _host.SetAccountAsync(accounts.Names[index], "Telegram");
            if (error is null) await _api("editMessageText", new { chat_id = chat, message_id = pressedOn, text = $"New runs use Claude: {accounts.Names[index] ?? "Main"}." }, token);
        }
        else if (parts.Length < 2 || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            error = "Unknown button";
        }
        else
        {
            _focus = id;
            var number = parts.Length > 2 && int.TryParse(parts[2], out var parsed) ? parsed : 0;
            error = parts[0] switch
            {
                "q" => await AnswerAsync(id, number),
                "d" => await _host.DecideAsync(id, number == 1, "Telegram"),
                "r" => await _host.RefuseAsync(id, "Telegram"),
                "s" => await _host.StopAsync(id, "Telegram"),
                "n" => await _host.SetNoStopAsync(id, number == 1, "Telegram"),
                "f" => await SendDiffAsync(chat, id, token),
                "pg" => await SendPageAsync(chat, id, number, token),
                "pa" => await _host.ApplyPageAsync(id, number, "Telegram"),
                "v" => null,
                _ => "Unknown button"
            };
            var onCard = _cards.Cards.TryGetValue(id, out var card) && card.Message == pressedOn;
            if (parts[0] == "v")
            {
                await ShowAsync(chat, id, null, fresh: !onCard, token);
            }
            else if (error is null && parts[0] is not ("f" or "pg"))
            {
                if (!onCard) await TakeButtonsAsync(chat, pressedOn, token);
                if (card is not null) await ShowAsync(chat, id, null, fresh: false, token);
            }
        }

        // Answered already, so the button stopped spinning: what went wrong comes as a message.
        if (!await AnswerAsync(pressed, error ?? "Done", token) && error is not null)
        {
            await SendAsync(chat, message.TryGetProperty("message_thread_id", out var thread) ? thread.GetInt64() : null, error, null, token);
        }
    }

    /// <summary>A pressed button, which Telegram answers once.</summary>
    private sealed class Press(string id)
    {
        public string Id { get; } = id;

        public int Answered;
    }

    /// <summary>
    /// Telegram spins a pressed button until it hears back: an action that takes longer (a card to post, others before it)
    /// is answered at once without words.
    /// </summary>
    private async Task AnswerSoonAsync(Press pressed, CancellationToken token)
    {
        try
        {
            await Task.Delay(QuickAnswer, token);
            await AnswerAsync(pressed, null, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            _log($"[Telegram] {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>Answers the press unless it was answered; whether this did.</summary>
    private async Task<bool> AnswerAsync(Press pressed, string? text, CancellationToken token)
    {
        if (Interlocked.Exchange(ref pressed.Answered, 1) != 0) return false;
        await _api("answerCallbackQuery", new { callback_query_id = pressed.Id, text }, token);
        return true;
    }

    /// <summary>The option the button stood for, if the task still asks the same.</summary>
    private async Task<string?> AnswerAsync(Guid id, int option)
    {
        var view = await _host.TaskAsync(id);
        if (view?.Question?.Options.ElementAtOrDefault(option) is not { } chosen) return "The question has changed: open the task again";
        return await _host.ReplyAsync(id, chosen.Label, "Telegram");
    }

    /// <summary>The changes of the task's last run as a .patch file beside its card.</summary>
    private async Task<string?> SendDiffAsync(long chat, Guid id, CancellationToken token)
    {
        if (await _host.DiffAsync(id) is not { Length: > 0 } diff) return "No changes Link can show now";
        var topic = _cards.Cards.TryGetValue(id, out var card) ? card.Topic : null;
        var file = new FileBody(Fields(chat, topic), "document", $"changes-{DateTime.Now:yyyyMMdd-HHmm}.patch", Encoding.UTF8.GetBytes(diff));
        return await _api("sendDocument", file, token) is null ? "Telegram did not take the file" : null;
    }

    private async Task SendTasksAsync(long chat, long? topic, CancellationToken token)
    {
        var tasks = (await _host.TasksAsync()).Take(10).ToArray();
        if (tasks.Length == 0)
        {
            await SendAsync(chat, topic, "No tasks yet: /new <task> starts one.", null, token);
            return;
        }

        var rows = tasks.Select(task => new[] { Button($"{(task.Waits is not null ? "⚠ " : task.Working ? "⏳ " : string.Empty)}{Short(task.Goal, 50)}", $"v:{task.Id:N}") });
        await SendAsync(chat, topic, "Tasks (⚠ needs you, ⏳ working):", rows, token);
    }

    /// <summary>The bot's own work, one thing at a time with the updates; a failure is logged, never thrown.</summary>
    private async Task GatedAsync(Func<Task> work, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                await work();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
                                              or IOException or UnauthorizedAccessException)
        {
            _log($"[Telegram] {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void Changed()
    {
        // Raised on the window's thread only, so nothing else adds to the count in between.
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    /// <summary>The cards follow their tasks: once a change settles, and every half minute while one of them works.</summary>
    private async Task FollowAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                if (!await _changed.WaitAsync(WhileWorking, token) && !_working) continue;
                await Task.Delay(Settle, token);
                await GatedAsync(() => RefreshAsync(token), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Each card whose task changed shows it as it is now, and a task started from Telegram gets its card.</summary>
    private async Task RefreshAsync(CancellationToken token)
    {
        if (_chat() is not { } chat) return;
        var rows = await _host.TasksAsync();
        if (_starting is { } starting && rows.FirstOrDefault(row => !_cards.Cards.ContainsKey(row.Id) && row.Updated >= starting.At &&
                starting.Goal.StartsWith(row.Goal.TrimEnd('…'), StringComparison.Ordinal)) is { } started)
        {
            _starting = null;
            await ShowAsync(chat, started.Id, null, fresh: true, token);
        }

        foreach (var row in rows.Where(row => _cards.Cards.TryGetValue(row.Id, out var card) && (row.Working || row.Updated != card.Seen)))
        {
            await ShowAsync(chat, row.Id, null, fresh: false, token);
        }

        _working = rows.Any(row => row.Working && _cards.Cards.ContainsKey(row.Id));
    }

    /// <summary>
    /// The task's card as the task is now: edited in place when it changed, else posted anew (fresh, or when the old one
    /// cannot be edited) with the old one's buttons taken off. A header makes the new card notify; all else comes silently.
    /// The pictures the task's last run made follow the card.
    /// </summary>
    private async Task ShowAsync(long chat, Guid id, string? header, bool fresh, CancellationToken token)
    {
        if (await _host.TaskAsync(id) is not { } view)
        {
            if (fresh) await SendAsync(chat, null, header is null ? "The task is not in Link's list any more." : TelegramHtml.Plain(header), null, token);
            return;
        }

        var (text, rows) = Card(view, null);
        var shown = text + JsonSerializer.Serialize(rows);
        var card = CardOf(id);
        card.Seen = view.Task.Updated;
        if (!fresh && card.Message is { } message)
        {
            if (shown == card.Shown) return;
            if (await _api("editMessageText", new { chat_id = chat, message_id = message, text, parse_mode = "HTML", reply_markup = Keyboard(rows) }, token) is not null)
            {
                card.Shown = shown;
                await PicturesAsync(chat, id, card, token);
                Save();
                return;
            }
        }

        if (card.Message is { } old) await TakeButtonsAsync(chat, old, token);
        card.Topic ??= await TopicAsync(chat, view.Task.Goal, token);
        if (header is not null) text = Card(view, header).Text;
        var sent = await SendAsync(chat, card.Topic, text, rows, token, loud: header is not null, html: true);
        if (sent is null && card.Topic > 0)
        {
            // The topic is gone: the task's cards come to the chat itself from now on.
            card.Topic = 0;
            sent = await SendAsync(chat, null, text, rows, token, loud: header is not null, html: true);
        }

        card.Message = sent;
        card.Shown = shown;
        if (sent is { } posted) Remember(posted, id);
        _focus = id;
        await PicturesAsync(chat, id, card, token);
        Save();
    }

    /// <summary>A task's card: how it stands, its last words and what it waits for, with the buttons for it.</summary>
    private static (string Text, List<object[]> Rows) Card(RemoteTaskView view, string? header)
    {
        var task = view.Task;
        var id = task.Id;
        // HTML: the agent's words (its last ones and the course) are Markdown made Telegram's tags; the rest is escaped.
        var lines = new List<string> { header ?? TelegramHtml.Escape($"«{task.Goal}»"), TelegramHtml.Escape($"{task.Project} · {task.Agent ?? "no agent yet"} · {task.Status}") };
        if (view.Plan.Count > 0)
        {
            lines.Add($"Plan, {view.Plan.Count(item => item.Done)} of {view.Plan.Count} done:\n" + string.Join('\n', view.Plan.Take(PlanShown)
                .Select(item => $"{(item.Done ? "✓" : item.Active ? "▶" : "·")} {TelegramHtml.Escape(Short(item.Text, 80))}")) + (view.Plan.Count > PlanShown ? "\n…" : string.Empty));
        }

        if (view.Now is { } now) lines.Add("▶ Now: " + TelegramHtml.Escape(Short(now, 200)));
        // The course goes where the user opened the task; an alert is about what it waits for.
        var course = lines.Count;
        if (header is null && view.Before.Count == 0 && task.LastWords.Length > 0) lines.Add(TelegramHtml.FromMarkdown(Short(task.LastWords, 1500)));
        var rows = new List<object[]>();
        if (view.Question is { } question)
        {
            lines.Add("❓ " + TelegramHtml.Escape(question.Text));
            rows.AddRange(question.Options.Select((option, index) => new[] { Button($"{index + 1}. {Short(option.Label, 60)}", $"q:{id:N}:{index}") }));
        }

        if (view.Decision is { } decision)
        {
            lines.Add("❗ " + TelegramHtml.Escape(decision.Text));
            var buttons = new List<object> { Button(decision.Button, $"d:{id:N}:0") };
            if (decision.CanRemember) buttons.Add(Button(decision.Button + " · always", $"d:{id:N}:1"));
            if (decision.CanRefuse) buttons.Add(Button("Deny", $"r:{id:N}"));
            rows.Add([.. buttons]);
        }

        if (view.Limit is { } limit) lines.Add("⏸ " + TelegramHtml.Escape(limit));
        if (view.NoStopPending is { } pending) lines.Add(TelegramHtml.Escape($"No-stop takes «{pending}» in a few seconds."));
        if (view.TakesWords) lines.Add("Write to answer it: words or a picture.");
        if (!task.Working) rows.AddRange(view.Pages.Select((page, index) => new[] { Button("🔗 " + Short(page.Label, 50), $"pg:{id:N}:{index}") }));
        var foot = new List<object> { Button(task.NoStop ? "No-stop: on ✓" : "No-stop: off", $"n:{id:N}:{(task.NoStop ? 0 : 1)}") };
        if (task.Working) foot.Insert(0, Button("⏹ Stop", $"s:{id:N}"));
        rows.Add([.. foot]);
        rows.Add(view.ChangedFiles.Count > 0 ? [Button("Changes", $"f:{id:N}"), Button("🔄", $"v:{id:N}")] : [Button("🔄", $"v:{id:N}")]);
        if (header is null && view.Before.Count > 0)
        {
            // The newest lines that fit beside the rest: the question and its buttons are never cut off.
            var room = MaxText - string.Join("\n\n", lines).Length - 20;
            var before = view.Before.Reverse().Select(TelegramHtml.FromMarkdown).TakeWhile(line => (room -= line.Length + 3) > 0).Reverse().ToArray();
            if (before.Length > 0) lines.Insert(course, "<b>Before:</b>\n" + string.Join('\n', before.Select(line => "• " + line)));
        }

        return (TelegramHtml.Fit(string.Join("\n\n", lines), MaxText), rows);
    }

    private TelegramCard CardOf(Guid id)
    {
        if (_cards.Cards.TryGetValue(id, out var card)) return card;
        if (_cards.Cards.Count >= CardsKept) _cards.Cards.Remove(_cards.Cards.MinBy(each => each.Value.Seen).Key);
        return _cards.Cards[id] = new TelegramCard();
    }

    /// <summary>A topic of the task's own when the user turned topics on for the bot; 0 for none.</summary>
    private async Task<long> TopicAsync(long chat, string goal, CancellationToken token) =>
        _topics && await _api("createForumTopic", new { chat_id = chat, name = Short(goal, 100) }, token) is { } topic &&
        topic.TryGetProperty("message_thread_id", out var thread)
            ? thread.GetInt64()
            : 0;

    /// <summary>The picture the task's last run made that its card has not had: as a photo, a big one as a file.</summary>
    private async Task PicturesAsync(long chat, Guid id, TelegramCard card, CancellationToken token)
    {
        var pictures = await _host.ImagesAsync(id);
        foreach (var path in pictures.Except(card.Pictures).Take(PicturesPerRun))
        {
            var size = new FileInfo(path).Length;
            if (size > MaxFile) continue;
            var photo = size <= MaxPhoto;
            await _api(photo ? "sendPhoto" : "sendDocument",
                new FileBody(Fields(chat, card.Topic), photo ? "photo" : "document", Path.GetFileName(path), await File.ReadAllBytesAsync(path, token)), token);
        }

        // All the run's pictures are seen, so the others do not trickle in later.
        card.Pictures = [.. pictures];
    }

    /// <summary>An upload's fields: the chat, the task's topic, and silently.</summary>
    private static Dictionary<string, string> Fields(long chat, long? topic)
    {
        var fields = new Dictionary<string, string> { ["chat_id"] = chat.ToString(CultureInfo.InvariantCulture), ["disable_notification"] = "true" };
        if (topic is { } thread && thread > 0) fields["message_thread_id"] = thread.ToString(CultureInfo.InvariantCulture);
        return fields;
    }

    /// <summary>
    /// Sends a message, in a topic and with buttons in rows when given, silently unless loud; the message's id, or null when
    /// it did not go. <paramref name="html"/>: the text is Telegram's HTML, already fit (a card).
    /// </summary>
    private async Task<long?> SendAsync(long chat, long? topic, string text, IEnumerable<object[]>? rows, CancellationToken token, bool loud = false, bool html = false)
    {
        var sent = await _api("sendMessage", new
        {
            chat_id = chat,
            message_thread_id = topic > 0 ? topic : null,
            text = html ? text : Short(text, MaxText),
            parse_mode = html ? "HTML" : null,
            reply_markup = rows is null ? null : Keyboard(rows),
            disable_notification = loud ? (bool?)null : true
        }, token);
        // Telegram refuses HTML it cannot parse: the card still goes, as plain text.
        if (sent is null && html) return await SendAsync(chat, topic, TelegramHtml.Plain(text), rows, token, loud);
        return sent is { } message && message.TryGetProperty("message_id", out var id) ? id.GetInt64() : null;
    }

    private Task TakeButtonsAsync(long chat, long message, CancellationToken token) =>
        _api("editMessageReplyMarkup", new { chat_id = chat, message_id = message, reply_markup = Keyboard([]) }, token);

    private void Save()
    {
        try
        {
            AtomicJsonFile.Write(_cardsFile, _cards, Json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"[Telegram] The cards were not kept: {exception.Message}");
        }
    }

    private void Remember(long message, Guid task)
    {
        lock (_lock)
        {
            _messages[message] = task;
            if (_messages.Count > MessagesKept) _messages.Remove(_messages.Keys.Min());
        }
    }

    private Guid? TaskOf(long message)
    {
        lock (_lock)
        {
            if (_messages.TryGetValue(message, out var task)) return task;
        }

        return _cards.Cards.Where(card => card.Value.Message == message).Select(card => (Guid?)card.Key).FirstOrDefault();
    }

    private static object Keyboard(IEnumerable<object[]> rows) => new { inline_keyboard = rows.ToArray() };

    private static object Button(string text, string data) => new { text, callback_data = data };

    private static string? Words(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    /// <summary>A task being started from Telegram: its words and pictures, until its project and agent are picked.</summary>
    private sealed record Draft(string Goal, bool PlanFirst, bool NoStop, IReadOnlyList<string> Pictures, IReadOnlyList<RemoteProject> Projects)
    {
        public RemoteProject? Project { get; set; }

        public bool Team { get; set; }

        // The picker's message: a press on an older one starts nothing.
        public long Message { get; set; }
    }
}

/// <summary>The bot's cards, kept across restarts so a task keeps its topic and its one live message.</summary>
internal sealed class TelegramCards
{
    /// <summary>The chat they are in: a newly paired chat starts without them.</summary>
    public long? Chat { get; set; }

    public Dictionary<Guid, TelegramCard> Cards { get; set; } = [];

    public TelegramCards Normalize()
    {
        Cards ??= [];
        foreach (var card in Cards.Values)
        {
            card.Shown ??= string.Empty;
            card.Pictures ??= [];
        }

        return this;
    }
}

internal sealed class TelegramCard
{
    /// <summary>The task's topic; 0 when it has none (topics are off for the bot, or the topic is gone).</summary>
    public long? Topic { get; set; }

    public long? Message { get; set; }

    /// <summary>What the card shows, its words and buttons: the same is not edited again.</summary>
    public string Shown { get; set; } = string.Empty;

    /// <summary>When the task last changed, as the card saw it.</summary>
    public DateTimeOffset Seen { get; set; }

    /// <summary>The last run's pictures the card has had.</summary>
    public List<string> Pictures { get; set; } = [];
}
