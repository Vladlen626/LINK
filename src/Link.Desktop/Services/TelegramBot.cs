using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Link.Desktop.Services;

/// <summary>
/// Link's tasks in the user's own Telegram bot: what waits for the user comes as a message with buttons (the agent's
/// options, the decision, Stop, No-stop), plain words answer the task, and commands list or start tasks. Link asks
/// Telegram for news itself (long polling), so nothing reaches this computer from outside, whatever VPN the phone uses.
/// Only the chat paired with a code from Settings is heard; the words go through Telegram's servers.
/// </summary>
public sealed class TelegramBot : IDisposable
{
    /// <summary>A call of the Bot API: its method and body; the result, or null when Telegram refused it.</summary>
    public delegate Task<JsonElement?> Api(string method, object body, CancellationToken token);

    private const int MaxText = 3500;
    private const int MessagesKept = 200;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    private readonly Api _api;
    private readonly IRemoteHost _host;
    private readonly Func<long?> _chat;
    private readonly Action<long> _paired;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    // Which task each of Link's messages is about: a reply to one goes to its task.
    private readonly Dictionary<long, Guid> _messages = [];
    private CancellationTokenSource? _stop;
    private string? _code;
    private DateTimeOffset _codeUntil;
    // The task the user's plain words go to: the last one Link wrote about or the user acted on.
    private Guid? _focus;

    /// <param name="chat">The paired chat; null before pairing.</param>
    /// <param name="paired">A chat sent the right code: it is the user's from now on.</param>
    public TelegramBot(Api api, IRemoteHost host, Func<long?> chat, Action<long> paired, Action<string> log)
    {
        _api = api;
        _host = host;
        _chat = chat;
        _paired = paired;
        _log = log;
    }

    public bool Running => _stop is not null;

    /// <summary>The Bot API over HTTPS with a token kept apart; the token is never logged.</summary>
    public static Api Over(HttpClient http, Func<string?> token, Action<string> log) => async (method, body, cancel) =>
    {
        var key = token() ?? throw new InvalidOperationException("No Telegram bot token");
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{key}/{method}", body, Json, cancel);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel);
        if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return json.RootElement.GetProperty("result").Clone();
        log($"[Telegram] {method} refused: {(json.RootElement.TryGetProperty("description", out var why) ? why.GetString() : response.StatusCode.ToString())}");
        return null;
    };

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        _ = PollAsync(_stop.Token);
        _log("[Telegram] Bot started");
    }

    public void Stop()
    {
        if (_stop is null) return;
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
            _codeUntil = DateTimeOffset.Now + RemotePairing.CodeLife;
            return _code;
        }
    }

    /// <summary>A task needs the user or is through: a message about it, with the buttons it waits for.</summary>
    public void Alert(Guid task, string title, string text)
    {
        if (_stop is not { } stop || _chat() is not { } chat) return;
        _ = SendTaskAsync(chat, task, $"{title}\n\n{text}", stop.Token);
    }

    private async Task PollAsync(CancellationToken token)
    {
        long offset = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
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
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
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

    /// <summary>One update: the user's words or command, or a button pressed; anyone else's is ignored.</summary>
    internal async Task HandleAsync(JsonElement update, CancellationToken token)
    {
        if (update.TryGetProperty("callback_query", out var press))
        {
            if (press.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64() == _chat()) await PressedAsync(press, token);
            return;
        }

        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var textElement)) return;
        var chat = message.GetProperty("chat").GetProperty("id").GetInt64();
        var text = textElement.GetString()?.Trim() ?? string.Empty;
        if (chat != _chat())
        {
            // The code alone, or after /start.
            if (Pairs(text.Split(' ')[^1]))
            {
                _paired(chat);
                _log("[Telegram] A chat is paired");
                await SendAsync(chat, "Linked to Link. " + Help, null, token);
            }

            return;
        }

        var (command, rest) = text.StartsWith('/') ? (text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant(), text.Split(' ', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty) : (string.Empty, text);
        switch (command)
        {
            case "/start" or "/help":
                await SendAsync(chat, Help, null, token);
                return;
            case "/tasks":
                await SendTasksAsync(chat, token);
                return;
            case "/spent":
                var spent = await _host.UsageAsync();
                await SendAsync(chat, spent is null ? "Link has not counted yet: open the Studio on the computer." : $"{spent.Line}\n\n{spent.Hint}", null, token);
                return;
            case "/new" or "/plan" or "/auto":
                if (rest.Length == 0)
                {
                    await SendAsync(chat, $"Write the task after {command}.", null, token);
                    return;
                }

                var started = await _host.StartAsync(new RemoteNewTask(null, rest, command == "/plan", command == "/auto"), "Telegram");
                await SendAsync(chat, started ?? "Started in the active project: you get a message when it needs you or is through.", null, token);
                return;
            case "":
                // A reply to Link's message goes to its task; other words to the task Link last wrote about.
                var task = message.TryGetProperty("reply_to_message", out var replied) && TaskOf(replied.GetProperty("message_id").GetInt64()) is { } of ? of : _focus;
                if (task is not { } id)
                {
                    await SendAsync(chat, "Which task? Open one in /tasks, or reply to a message about it.", null, token);
                    return;
                }

                var error = await _host.ReplyAsync(id, rest, "Telegram");
                await SendAsync(chat, error ?? "Sent.", null, token);
                return;
            default:
                await SendAsync(chat, Help, null, token);
                return;
        }
    }

    private const string Help =
        "Your words answer the task Link last wrote about (or reply to its message). " +
        "/tasks — the tasks · /new <task> — a new task in the active project · /plan <task> — plan first · " +
        "/auto <task> — No-stop · /spent — what the agents spent.";

    private bool Pairs(string text)
    {
        lock (_lock)
        {
            if (_code is null || DateTimeOffset.Now > _codeUntil || text != _code) return false;
            _code = null;
            return true;
        }
    }

    /// <summary>A button: it acts on its task, says how it went, and leaves its message without buttons.</summary>
    private async Task PressedAsync(JsonElement press, CancellationToken token)
    {
        var data = press.TryGetProperty("data", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        var parts = data.Split(':');
        var message = press.GetProperty("message");
        var chat = message.GetProperty("chat").GetProperty("id").GetInt64();
        string? error;
        if (parts.Length < 2 || !Guid.TryParseExact(parts[1], "N", out var id))
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
                "s" => await _host.StopAsync(id, "Telegram"),
                "n" => await _host.SetNoStopAsync(id, number == 1, "Telegram"),
                "v" => null,
                _ => "Unknown button"
            };
            if (parts[0] == "v") await SendTaskAsync(chat, id, null, token);
            else if (error is null) await _api("editMessageReplyMarkup", new { chat_id = chat, message_id = message.GetProperty("message_id").GetInt64(), reply_markup = new { inline_keyboard = Array.Empty<object>() } }, token);
        }

        await _api("answerCallbackQuery", new { callback_query_id = press.GetProperty("id").GetString(), text = error ?? "Done" }, token);
    }

    /// <summary>The option the button stood for, if the task still asks the same.</summary>
    private async Task<string?> AnswerAsync(Guid id, int option)
    {
        var view = await _host.TaskAsync(id);
        if (view?.Question?.Options.ElementAtOrDefault(option) is not { } chosen) return "The question has changed: open the task again";
        return await _host.ReplyAsync(id, chosen.Label, "Telegram");
    }

    private async Task SendTasksAsync(long chat, CancellationToken token)
    {
        var tasks = (await _host.TasksAsync()).Take(10).ToArray();
        if (tasks.Length == 0)
        {
            await SendAsync(chat, "No tasks yet: /new <task> starts one.", null, token);
            return;
        }

        var rows = tasks.Select(task => new[] { Button($"{(task.Waits is not null ? "⚠ " : task.Working ? "⏳ " : string.Empty)}{Short(task.Goal, 50)}", $"v:{task.Id:N}") });
        await SendAsync(chat, "Tasks (⚠ needs you, ⏳ working):", rows, token);
    }

    /// <summary>A task as a message: how it stands, its last words and what it waits for, with the buttons for it.</summary>
    private async Task SendTaskAsync(long chat, Guid id, string? header, CancellationToken token)
    {
        if (await _host.TaskAsync(id) is not { } view)
        {
            await SendAsync(chat, header ?? "The task is not in Link's list any more.", null, token);
            return;
        }

        var task = view.Task;
        var lines = new List<string> { header ?? $"«{task.Goal}»", $"{task.Project} · {task.Agent ?? "no agent yet"} · {task.Status}" };
        if (header is null && task.LastWords.Length > 0) lines.Add(Short(task.LastWords, 1500));
        var rows = new List<object[]>();
        if (view.Question is { } question)
        {
            lines.Add("❓ " + question.Text);
            rows.AddRange(question.Options.Select((option, index) => new[] { Button($"{index + 1}. {Short(option.Label, 60)}", $"q:{id:N}:{index}") }));
        }

        if (view.Decision is { } decision)
        {
            lines.Add("❗ " + decision.Text);
            rows.Add(decision.CanRemember
                ? [Button(decision.Button, $"d:{id:N}:0"), Button(decision.Button + " · always", $"d:{id:N}:1")]
                : [Button(decision.Button, $"d:{id:N}:0")]);
        }

        if (view.Limit is { } limit) lines.Add("⏸ " + limit);
        if (view.NoStopPending is { } pending) lines.Add($"No-stop takes «{pending}» in a few seconds.");
        if (view.TakesWords) lines.Add("Reply with words to answer it.");
        var foot = new List<object> { Button(task.NoStop ? "No-stop: on ✓" : "No-stop: off", $"n:{id:N}:{(task.NoStop ? 0 : 1)}"), Button("🔄", $"v:{id:N}") };
        if (task.Working) foot.Insert(0, Button("⏹ Stop", $"s:{id:N}"));
        rows.Add([.. foot]);
        _focus = id;
        if (await SendAsync(chat, string.Join("\n\n", lines), rows, token) is { } sent) Remember(sent, id);
    }

    /// <summary>Sends a message, with buttons in rows when given; the message's id, or null when it did not go.</summary>
    private async Task<long?> SendAsync(long chat, string text, IEnumerable<object[]>? rows, CancellationToken token)
    {
        var sent = await _api("sendMessage", new
        {
            chat_id = chat,
            text = Short(text, MaxText),
            reply_markup = rows is null ? null : new { inline_keyboard = rows.ToArray() }
        }, token);
        return sent is { } message && message.TryGetProperty("message_id", out var id) ? id.GetInt64() : null;
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
        lock (_lock) return _messages.TryGetValue(message, out var task) ? task : null;
    }

    private static object Button(string text, string data) => new { text, callback_data = data };

    private static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
