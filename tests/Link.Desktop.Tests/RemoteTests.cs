using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>Remote control: the Telegram bot against a fake Bot API, and the review window passed through over real loopback HTTP.</summary>
internal static class RemoteTests
{
    public static async Task RunAsync()
    {
        Spent();
        await ServerAsync();
        await TelegramAsync();
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
        TelegramBot Bot() => new(api, download, host, () => chat, paired => chat = paired, _ => { }, folder, cards,
            (_, page) => Task.FromResult<(string?, string?)>(("https://a.trycloudflare.com/view/t/", null)));
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
                Program.Equal("Telegram: words without a task ask which one; words that went get no reaction or message",
                    (Count("sendMessage", "Which task?"), Count("setMessageReaction"), Count("sendMessage", "Sent.")), (1, 0, 0));
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
                await bot.HandleAsync(Update(Text(42, "/account")), default);
                await bot.HandleAsync(Update(Press(42, "ac:1", Last("sendMessage"))), default);
                Program.Equal("Telegram: /account offers Claude's accounts, and the one pressed runs next",
                    (Count("sendMessage", "\"ac:1\""), host.Done.Last(), Count("editMessageText", "Account 2")), (1, "account Account 2 by Telegram", 1));
                await bot.HandleAsync(Update(Press(42, $"pg:{FakeHost.Task:N}:0")), default);
                await bot.HandleAsync(Update(Press(42, $"pa:{FakeHost.Task:N}:0")), default);
                Program.Equal("Telegram: the review window's link opens in a browser, and Apply marks sends the marks to the task",
                    (Count("sendMessage", "\"url\":\"https://a.trycloudflare.com/view/t/\""), Count("sendMessage", $"pa:{FakeHost.Task:N}:0"), host.Done.Last()),
                    (1, 1, "apply page 0 by Telegram"));

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

    /// <summary>The review window's pass-through: a stand-in window answers with the path it was asked for.</summary>
    private static async Task ServerAsync()
    {
        var host = new FakeHost();
        using var server = new RemoteServer(host, _ => { });
        server.Start();
        Program.Equal("Remote: a page that does not open gives no link", await server.ViewPathAsync(FakeHost.Task, 0), null);

        var window = new TcpListener(IPAddress.Loopback, 0);
        window.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var connection = await window.AcceptTcpClientAsync();
                    var stream = connection.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer);
                    var body = Encoding.UTF8.GetBytes("window " + Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1]);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(body);
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
            {
            }
        });

        host.Page = new Uri($"http://127.0.0.1:{((IPEndPoint)window.LocalEndpoint).Port}/");
        var path = await server.ViewPathAsync(FakeHost.Task, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        async Task<(HttpStatusCode, string)> Get(string address)
        {
            var answer = await client.GetAsync(address);
            return (answer.StatusCode, answer.StatusCode == HttpStatusCode.OK ? await answer.Content.ReadAsStringAsync() : string.Empty);
        }

        Program.Equal("Remote: the window passes through by its ticket, with its own requests, and nothing else is served",
            (path?.StartsWith("/view/"), await Get(path!), await Get(path + "api/state?page=1"), (await Get("/view/0123/")).Item1,
                (await Get(path + "//example.com/")).Item1, (await Get("/")).Item1),
            ((bool?)true, (HttpStatusCode.OK, "window /"), (HttpStatusCode.OK, "window /api/state?page=1"), HttpStatusCode.NotFound,
                HttpStatusCode.NotFound, HttpStatusCode.NotFound));
        Program.Equal("Remote: the same window keeps its ticket", await server.ViewPathAsync(FakeHost.Task, 0), path);
        window.Stop();
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
                ["You: Make the jump higher", "Claude Code: done in 2:00 - " + Words],
                [new RemoteTaskPage("Mark changes on the model", "Apply marks", "Apply my open marks")])
            : null);

        /// <summary>The address the review window's page is at; null: it does not open.</summary>
        public Uri? Page { get; set; }
        public string? Account { get; private set; }

        public Task<Uri?> OpenPageAsync(Guid id, int page) => System.Threading.Tasks.Task.FromResult(id == Task && page == 0 ? Page : null);

        public Task<string?> ApplyPageAsync(Guid id, int page, string device) => Did($"apply page {page} by {device}");

        public Task<RemoteAccounts> AccountsAsync() => System.Threading.Tasks.Task.FromResult(new RemoteAccounts([null, "Account 2"], Account));

        public Task<string?> SetAccountAsync(string? account, string device)
        {
            Account = account;
            return Did($"account {account ?? "Main"} by {device}");
        }

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
