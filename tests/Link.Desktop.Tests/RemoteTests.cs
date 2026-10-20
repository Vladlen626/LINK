using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>Remote control: pairing, and the page and API over real loopback HTTP with a fake window.</summary>
internal static class RemoteTests
{
    public static async Task RunAsync()
    {
        Pairing();
        Spent();
        PushMessage();
        await ServerAsync();
        await TelegramAsync();
    }

    /// <summary>A notification's body as RFC 8291's Appendix A gives it for its keys, salt and words.</summary>
    private static void PushMessage()
    {
        static byte[] B(string text) => RemotePush.FromBase64Url(text);
        var serverPublic = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var server = System.Security.Cryptography.ECDiffieHellman.Create(new System.Security.Cryptography.ECParameters
        {
            Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            D = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new System.Security.Cryptography.ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] }
        });
        var body = RemotePush.Encrypt(B("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24"),
            B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"), B("BTBZMqHH6r4Tts7J_aSIgg"), server, B("DGv6ra1nlYgDCS1FRnbzlw"));
        Program.Equal("Push: the message is sealed as RFC 8291's example", RemotePush.Base64Url(body),
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
            "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
    }

    /// <summary>ccusage 20's JSON, as its sections report and its active block give it.</summary>
    private static void Spent()
    {
        var at = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.FromHours(3));
        const string report = """
            {"daily": [{"period": "2026-10-14", "totalCost": 5.5, "totalTokens": 10, "agents": []},
                       {"period": "2026-10-15", "totalCost": 98.92, "totalTokens": 255510582, "agents": [{"agent": "claude", "totalCost": 98.92}]}],
             "weekly": [{"period": "2026-10-12", "totalCost": 300.25, "totalTokens": 1, "agents": []}],
             "monthly": [{"period": "2026-10", "totalCost": 625.84, "totalTokens": 2,
                          "agents": [{"agent": "claude", "totalCost": 544.8}, {"agent": "codex", "totalCost": 81.04}]}],
             "session": [{"agent": "claude", "period": "959f05d5-2426-4385-ba3c-4c319fef6423", "totalCost": 3.18},
                         {"agent": "codex", "period": "2026/10/14/rollout-2026-10-14T23-33-24-01a13c1e-a947-78a3-8d15-68a53b4f24ee", "totalCost": 2.17}],
             "totals": {"totalCost": 625.84}}
            """;
        const string active = """
            {"blocks": [{"isActive": true, "costUSD": 10.78, "endTime": "2026-10-15T19:00:00.000Z",
                         "burnRate": {"costPerHour": 3.11}, "projection": {"remainingMinutes": 89, "totalCost": 15.4}}]}
            """;
        var spent = UsageReport.Parse(report, active, at);
        Program.Equal("Spent: today, the week and the month, each agent's month",
            (spent.Line, spent.Week?.Cost, spent.Month?.ByAgent["codex"]), ("Spent today $98.92 · month $625.84", 300.25, 81.04));
        Program.Equal("Spent: the 5-hour block with its rate and where it comes to",
            (spent.Block?.Cost, spent.Block?.CostPerHour, spent.Block?.Projected, spent.Hint.Contains("≈ $15.40")), (10.78, 3.11, 15.4, true));
        Program.Equal("Spent: a task's sessions by Claude's id and Codex's thread id; unknown ones are no cost",
            (spent.CostOf(["959f05d5-2426-4385-ba3c-4c319fef6423", "01a13c1e-a947-78a3-8d15-68a53b4f24ee", "959f05d5-2426-4385-ba3c-4c319fef6423"]), spent.CostOf(["other"])),
            (3.18 + 2.17, (double?)null));
        Program.Equal("Spent: an old day is no today, and no block is none",
            (UsageReport.Parse(report, null, at.AddDays(1)).Today, UsageReport.Parse(report, """{"blocks": []}""", at).Block), ((UsagePeriod?)null, (UsageBlock?)null));
    }

    private static void Pairing()
    {
        var now = DateTimeOffset.Now;
        var saved = new List<RemoteDevice>();
        var pairing = new RemotePairing([], saved.Add, () => now);
        Program.Equal("Remote: no code, no pairing", pairing.Pair("123456", "Phone"), null);
        var code = pairing.NewCode();
        Program.Equal("Remote: a code has six digits", code.Length == 6 && code.All(char.IsDigit), true);
        Program.Equal("Remote: a wrong code pairs nothing", pairing.Pair(code == "000000" ? "111111" : "000000", "Phone"), null);
        var token = pairing.Pair(code, " Phone ");
        Program.Equal("Remote: the right code gives a long token, kept only as its hash",
            (token?.Length > 40, saved.Count, saved[0].Name, saved[0].TokenHash == RemotePairing.Hash(token!), saved[0].TokenHash.Contains(token!)),
            (true, 1, "Phone", true, false));
        Program.Equal("Remote: the token names its device", pairing.DeviceOf(token), "Phone");
        Program.Equal("Remote: a code is used up", pairing.Pair(code, "Other"), null);
        Program.Equal("Remote: another token is no device", pairing.DeviceOf("x" + token), null);

        var late = pairing.NewCode();
        now += RemotePairing.CodeLife + TimeSpan.FromSeconds(1);
        Program.Equal("Remote: an old code pairs nothing", pairing.Pair(late, "Late"), null);

        now = DateTimeOffset.Now;
        var guessed = pairing.NewCode();
        for (var i = 0; i < RemotePairing.Tries; i++) pairing.Pair(guessed == "000000" ? "111111" : "000000", "Guess");
        Program.Equal("Remote: wrong tries end the code", pairing.Pair(guessed, "Guess"), null);

        pairing.Reset([]);
        Program.Equal("Remote: a removed device is out at once", pairing.DeviceOf(token), null);

        var settings = new RemoteSettings
        {
            Port = 80,
            Devices = [new RemoteDevice { Name = " ", TokenHash = new string('a', 64), Push = new("http://push.example/1", "k", "a") }, new RemoteDevice { TokenHash = "short" }]
        };
        settings.Normalize();
        Program.Equal("Remote settings: a bad port, bad devices and a push address without HTTPS are mended",
            (settings.Port, settings.Devices.Count, settings.Devices[0].Name, settings.Devices[0].Push), (RemoteSettings.DefaultPort, 1, "Device", (RemotePushSubscription?)null));
    }

    /// <summary>
    /// The Telegram bot against a fake Bot API: pairing, the new-task picker, buttons, words and pictures in, the task's card
    /// in its topic with the run's pictures, its edits as the task changes, and the topic kept across restarts.
    /// </summary>
    private static async Task TelegramAsync()
    {
        var host = new FakeHost();
        var calls = new List<(string Method, string Body, long Id)>();
        long? chat = null;
        long nextMessage = 100;
        TelegramBot.Api api = async (method, body, token) =>
        {
            // The long poll waits until the bot stops, as Telegram's does while nothing happens.
            if (method == "getUpdates") await Task.Delay(Timeout.Infinite, token);
            lock (calls)
            {
                var id = nextMessage++;
                calls.Add((method, JsonSerializer.Serialize(body), id));
                return JsonDocument.Parse(method switch
                {
                    "getMe" => """{"has_topics_enabled": true}""",
                    "createForumTopic" => """{"message_thread_id": 77}""",
                    "getFile" => """{"file_path": "photos/a.png"}""",
                    _ => $$"""{"message_id": {{id}}}"""
                }).RootElement;
            }
        };
        TelegramBot.Download download = (_, _) => Task.FromResult<byte[]?>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2]);
        var folder = Path.Combine(Path.GetTempPath(), "link-telegram-" + Guid.NewGuid().ToString("N")[..8]);
        var cards = Path.Combine(folder, "cards.json");
        TelegramBot Bot() => new(api, download, host, () => chat, paired => chat = paired, _ => { }, folder, cards);
        long Last(string method)
        {
            lock (calls) return calls.Last(call => call.Method == method).Id;
        }

        int Count(string method, string? having = null)
        {
            lock (calls) return calls.Count(call => call.Method == method && (having is null || call.Body.Contains(having)));
        }

        static JsonElement Update(string json) => JsonDocument.Parse(json).RootElement;
        static string Text(long from, string text, string more = "") =>
            $$"""{ "update_id": 1, "message": { "message_id": 7, "chat": { "id": {{from}} }, "text": "{{text}}"{{more}} } }""";
        static string Press(long from, string data, long message = 9) =>
            $$"""{ "update_id": 2, "callback_query": { "id": "c1", "data": "{{data}}", "message": { "message_id": {{message}}, "chat": { "id": {{from}} } } } }""";

        try
        {
            using (var bot = Bot())
            {
                await bot.HandleAsync(Update(Text(42, "/tasks")), default);
                Program.Equal("Telegram: an unpaired chat is not heard", (calls.Count, host.Done.Count), (0, 0));
                var code = bot.NewCode();
                await bot.HandleAsync(Update(Text(42, "/start " + code)), default);
                Program.Equal("Telegram: the chat that sends Settings' code is paired", (chat, calls.Single().Method), ((long?)42, "sendMessage"));
                await bot.HandleAsync(Update(Text(13, code)), default);
                Program.Equal("Telegram: a code pairs once", chat, (long?)42);

                await bot.HandleAsync(Update(Text(42, "/new Add a dash")), default);
                var dash = Last("sendMessage");
                Program.Equal("Telegram: /new offers the agents, by Settings or each with its strength, for the only project",
                    (Count("sendMessage", "na:ClaudeCode:Light"), Count("sendMessage", "\"na\""), host.Done.Count), (1, 1, 0));
                await bot.HandleAsync(Update(Text(42, "/auto Add a roll")), default);
                var roll = Last("sendMessage");
                await bot.HandleAsync(Update(Press(42, "na", dash)), default);
                await bot.HandleAsync(Update(Press(42, "na:Codex:Deep", roll)), default);
                Program.Equal("Telegram: an older picker starts nothing; the agent picked starts the task and the picker says so",
                    (Count("answerCallbackQuery", "This one is over"), Count("editMessageText", "Starting")), (1, 1));
                await bot.HandleAsync(Update(Text(42, "Words before any task")), default);
                await bot.HandleAsync(Update(Press(42, $"q:{FakeHost.Task:N}:0")), default);
                await bot.HandleAsync(Update(Text(42, "Only one jump")), default);
                await bot.HandleAsync(Update(Press(42, $"d:{FakeHost.Task:N}:1")), default);
                await bot.HandleAsync(Update(Press(42, $"r:{FakeHost.Task:N}")), default);
                await bot.HandleAsync(Update(Press(13, $"s:{FakeHost.Task:N}")), default);
                Program.Equal("Telegram: the picked agent, the option's button, words, the decision and Deny reach the task; another chat's button does not",
                    string.Join(" | ", host.Done),
                    "start Add a roll False True Codex Deep 0 by Telegram | reply Keep the double jump by Telegram | " +
                    "reply Only one jump by Telegram | decide True by Telegram | refuse by Telegram");
                Program.Equal("Telegram: words without a task ask which one; words that went get a reaction",
                    (Count("sendMessage", "Which task?"), Count("setMessageReaction")), (1, 1));
                Program.Equal("Telegram: a pressed button answers and leaves a message that is no card without buttons",
                    (Count("answerCallbackQuery"), Count("editMessageReplyMarkup")), (5, 3));

                await bot.HandleAsync(Update("""
                    { "update_id": 3, "message": { "message_id": 8, "chat": { "id": 42 }, "caption": "Like this",
                      "photo": [{ "file_id": "small" }, { "file_id": "big" }] } }
                    """), default);
                Program.Equal("Telegram: a photo's biggest size is kept with Link's pictures and goes to the task with its caption",
                    (Count("getFile", "big"), Directory.GetFiles(folder, "*-telegram-*.png").Length, host.Done.Last()), (1, 1, "reply Like this +1 picture by Telegram"));
                await bot.HandleAsync(Update("""{ "update_id": 4, "message": { "message_id": 10, "chat": { "id": 42 }, "voice": { "file_id": "v" } } }"""), default);
                Program.Equal("Telegram: a voice note is answered, not sent", (Count("sendMessage", "voice notes"), host.Done.Count), (1, 6));
                await bot.HandleAsync(Update(Press(42, $"f:{FakeHost.Task:N}")), default);
                Program.Equal("Telegram: Changes sends the run's changes as a .patch file", Count("sendDocument", ".patch"), 1);

                var made = Path.Combine(folder, "made.png");
                await File.WriteAllBytesAsync(made, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                var checkFrame = Path.Combine(folder, "check.png");
                File.Copy(made, checkFrame);
                host.Pictures = [made, checkFrame];
                bot.Start();
                await Program.EventuallyAsync("Telegram: started, it sets its commands", () => Count("setMyCommands") > 0, true);
                bot.Alert(FakeHost.Task, "Claude Code needs you · Make the jump", "Which jump?");
                await Program.EventuallyAsync("Telegram: an alert posts the task's card in a topic of its own, with its buttons and one picture of the run", () =>
                    (Count("createForumTopic"), Count("sendMessage", "\"message_thread_id\":77"), Count("sendMessage", $"q:{FakeHost.Task:N}:0"),
                        Count("sendMessage", $"r:{FakeHost.Task:N}"), Count("sendPhoto")), (1, 1, 1, 1, 1));
                var card = Last("sendMessage");

                host.Words = "Done the jump";
                host.Updated += TimeSpan.FromMinutes(1);
                host.Change();
                await Program.EventuallyAsync("Telegram: the card follows its task in place", () => Count("editMessageText", "Done the jump"), 1);
                Program.Equal("Telegram: the card shows the task's plan, the step under way and its course",
                    (Count("editMessageText", "Plan, 1 of 2 done"), Count("editMessageText", "Now: Fixing the jump"), Count("editMessageText", "Before:")), (1, 1, 1));
                var edits = Count("editMessageText");
                await bot.HandleAsync(Update(Press(42, $"v:{FakeHost.Task:N}", card)), default);
                Program.Equal("Telegram: refreshing a card that shows the task as it is changes nothing and sends no picture again",
                    (Count("editMessageText"), Count("sendPhoto")), (edits, 1));
                bot.Stop();
            }

            using var again = Bot();
            await again.HandleAsync(Update(Text(42, "In the topic", ", \"message_thread_id\": 77")), default);
            Program.Equal("Telegram: after a restart, words in the task's topic still go to it", host.Done.Last(), "reply In the topic by Telegram");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task ServerAsync()
    {
        var host = new FakeHost();
        RemotePushSubscription? subscribed = null;
        var pairing = new RemotePairing([], _ => { }, subscribed: (_, subscription) => subscribed = subscription);
        using var server = new RemoteServer(host, pairing, _ => { }, () => "KEY");
        Program.Equal("Remote: the server starts on a free port", server.Start(0), null);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };

        var page = await client.GetAsync("/");
        Program.Equal("Remote: the page is served without a token",
            (page.StatusCode, page.Content.Headers.ContentType?.MediaType, (await page.Content.ReadAsStringAsync()).Contains("app.js")),
            (HttpStatusCode.OK, "text/html", true));
        var icon = await client.GetByteArrayAsync("/icon.png");
        Program.Equal("Remote: the icon is Link's own as PNG", icon.Take(4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), true);
        Program.Equal("Remote: the API wants a paired device", (await client.GetAsync("/api/tasks")).StatusCode, HttpStatusCode.Unauthorized);
        Program.Equal("Remote: a wrong code is refused",
            (await client.PostAsJsonAsync("/api/pair", new { code = "x", name = "Phone" })).StatusCode, HttpStatusCode.Unauthorized);

        var paired = await client.PostAsJsonAsync("/api/pair", new { code = pairing.NewCode(), name = "Phone" });
        var token = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var tasks = await client.GetFromJsonAsync<JsonElement>("/api/tasks");
        Program.Equal("Remote: a paired device reads the tasks", (tasks.GetArrayLength(), tasks[0].GetProperty("goal").GetString()), (1, "Make the jump"));
        Program.Equal("Remote: a task's view, and no other",
            ((await client.GetAsync($"/api/tasks/{FakeHost.Task}")).StatusCode, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}")).StatusCode),
            (HttpStatusCode.OK, HttpStatusCode.NotFound));

        Program.Equal("Remote: the spent line", (await client.GetFromJsonAsync<JsonElement>("/api/usage")).GetProperty("line").GetString(), "Spent today $1.00 · month $9.00");
        await client.PostAsJsonAsync($"/api/tasks/{FakeHost.Task}/reply", new { words = "Keep the double jump" });
        await client.PostAsJsonAsync($"/api/tasks/{FakeHost.Task}/decide", new { always = true });
        await client.PostAsJsonAsync($"/api/tasks/{FakeHost.Task}/refuse", new { });
        await client.PostAsJsonAsync($"/api/tasks/{FakeHost.Task}/nostop", new { on = true });
        await client.PostAsJsonAsync("/api/tasks", new { goal = " Add a dash ", planFirst = true, noStop = true, attached = new[] { "C:/secret.txt" } });
        var refused = await client.PostAsJsonAsync($"/api/tasks/{FakeHost.Task}/stop", new { });
        Program.Equal("Remote: the actions reach the window with the device's name; the page attaches no files of this computer",
            string.Join(" | ", host.Done),
            "reply Keep the double jump by Phone | decide True by Phone | refuse by Phone | nostop True by Phone | start Add a dash True True - - 0 by Phone | stop by Phone");
        Program.Equal("Remote: a refused action says why",
            (refused.StatusCode, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()), (HttpStatusCode.Conflict, "The task is not running"));

        Program.Equal("Remote: the page gets Link's push key", (await client.GetFromJsonAsync<JsonElement>("/api/push")).GetProperty("publicKey").GetString(), "KEY");
        var plain = await client.PostAsJsonAsync("/api/push", new { endpoint = "http://push.example/1", keys = new { p256dh = "k", auth = "a" } });
        await client.PostAsJsonAsync("/api/push", new { endpoint = "https://push.example/1", keys = new { p256dh = "k", auth = "a" } });
        Program.Equal("Remote: a device's push subscription is kept, over HTTPS only", (plain.StatusCode, subscribed), (HttpStatusCode.BadRequest, new RemotePushSubscription("https://push.example/1", "k", "a")));

        var version = (await client.GetFromJsonAsync<JsonElement>("/api/events?since=-1")).GetProperty("version").GetInt64();
        var waiting = client.GetFromJsonAsync<JsonElement>($"/api/events?since={version}");
        await Task.Delay(50);
        host.Change();
        var changed = await waiting;
        Program.Equal("Remote: a change answers the page that waits", changed.GetProperty("version").GetInt64(), version + 1);

        server.Stop();
        Program.Equal("Remote: stopped, it no longer listens", server.Port, null);
    }

    private sealed class FakeHost : IRemoteHost
    {
        public static readonly Guid Task = Guid.NewGuid();
        public List<string> Done { get; } = [];
        public string Words { get; set; } = "Which jump?";
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
        public IReadOnlyList<string> Pictures { get; set; } = [];

        public event Action? RemoteChanged;

        public event Action<Guid, string, string>? RemoteAlert { add { } remove { } }

        public void Change() => RemoteChanged?.Invoke();

        private RemoteTask Row => new(Task, "Game", "Make the jump", "Needs you", "Claude Code", false, "your answer", false, Updated, Words);

        public Task<IReadOnlyList<RemoteProject>> ProjectsAsync() =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<RemoteProject>>([new RemoteProject(Guid.NewGuid(), "Game", true, true)]);

        public Task<IReadOnlyList<RemoteTask>> TasksAsync() => System.Threading.Tasks.Task.FromResult<IReadOnlyList<RemoteTask>>([Row]);

        public Task<RemoteTaskView?> TaskAsync(Guid id) => System.Threading.Tasks.Task.FromResult(id == Task
            ? new RemoteTaskView(Row, [], new RemoteQuestion("Which jump?", [new AgentOption("Keep the double jump", "")], false),
                new RemoteDecision("Run the tests?", "Allow", true, true), null, null, ["Jump.cs"], "", true,
                "Fixing the jump - Edit Player.cs", [new AgentPlanItem("Find the jump", true, false), new AgentPlanItem("Fix the jump", false, true)],
                ["You: Make the jump higher", "Claude Code: done in 2:00 - " + Words])
            : null);

        public Task<string?> StartAsync(RemoteNewTask task, string device) =>
            Did($"start {task.Goal} {task.PlanFirst} {task.NoStop} {task.Provider?.ToString() ?? "-"} {task.Level?.ToString() ?? "-"} {task.Attached?.Count ?? 0} by {device}");

        public Task<string?> ReplyAsync(Guid id, string words, string device, IReadOnlyList<string>? images = null) =>
            Did($"reply {words}{(images is { Count: > 0 } ? $" +{images.Count} picture" : "")} by {device}");

        public Task<string?> DecideAsync(Guid id, bool always, string device) => Did($"decide {always} by {device}");
        public Task<string?> RefuseAsync(Guid id, string device) => Did($"refuse by {device}");

        public Task<string?> StopAsync(Guid id, string device)
        {
            Done.Add($"stop by {device}");
            return System.Threading.Tasks.Task.FromResult<string?>("The task is not running");
        }

        public Task<string?> SetNoStopAsync(Guid id, bool on, string device) => Did($"nostop {on} by {device}");

        public Task<RemoteUsage?> UsageAsync() => System.Threading.Tasks.Task.FromResult<RemoteUsage?>(new RemoteUsage("Spent today $1.00 · month $9.00", "hint"));
        public Task<IReadOnlyList<string>> ImagesAsync(Guid id) => System.Threading.Tasks.Task.FromResult(Pictures);
        public Task<string?> DiffAsync(Guid id) => System.Threading.Tasks.Task.FromResult<string?>("diff --git a/Jump.cs b/Jump.cs");

        private Task<string?> Did(string what)
        {
            Done.Add(what);
            return System.Threading.Tasks.Task.FromResult<string?>(null);
        }
    }
}
