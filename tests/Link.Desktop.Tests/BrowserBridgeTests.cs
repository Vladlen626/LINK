using System;
using System.Collections.Generic;
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

/// <summary>The bridge as the browser companion sees it: requests handed out, events reported back over HTTP.</summary>
internal static class BrowserBridgeTests
{
    public static async Task RunAsync()
    {
        await StreamedAnswerAsync();
        await RedeliveryAsync();
        await LostTabAsync();
        await CancelAsync();
        await CheckChatAsync();
    }

    private static async Task StreamedAnswerAsync()
    {
        using var rig = await BridgeRig.StartAsync();
        var answer = rig.Send("Hi");
        var delivery = await rig.NextDeliveryAsync();
        Program.Equal("a prompt is handed to the tab", (delivery.Attempt, delivery.Sent), (1, false));
        Program.Equal("while the tab works on it, it is not handed out twice", await rig.PollAsync(), null);

        await rig.ReportAsync(delivery.Id, "sending");
        await rig.ReportAsync(delivery.Id, "waiting");
        await rig.ReportAsync(delivery.Id, "receiving", "Hello");
        await rig.ReportAsync(delivery.Id, "receiving", "Hello. World");
        await rig.ReportAsync(delivery.Id, "completed", "Hello. World.");
        Program.Equal("the finished answer arrives", (await answer).Text, "Hello. World.");
        Program.Equal("the states follow the answer", rig.Progress.States,
            "Sending, Sending, Waiting, Receiving, Receiving, Completed");
        Program.Equal("a late event is turned away as answered", await rig.ReportAsync(delivery.Id, "receiving", "Hello"),
            (404, "completed"));
        Program.Equal("nothing shows after the answer", rig.Progress.States.Split(", ").Length, 6);
    }

    /// <summary>A request lost on its way, or a page reloaded while it answers, gets the request again.</summary>
    private static async Task RedeliveryAsync()
    {
        using var rig = await BridgeRig.StartAsync();
        var answer = rig.Send("Hi", attachment: true);
        var first = await rig.NextDeliveryAsync();
        // Nothing comes back: the companion that got it was replaced (e.g. the extension was reloaded).
        var second = await rig.NextDeliveryAsync();
        Program.Equal("an unanswered delivery is offered again", (second.Id, second.Attempt, second.Sent, second.Attachments),
            (first.Id, 2, false, 1));

        await rig.ReportAsync(second.Id, "sending");
        await rig.ReportAsync(second.Id, "waiting");
        for (var beat = 0; beat < 6; beat++)
        {
            await Task.Delay(BridgeRig.Timings.Redeliver / 3);
            await rig.ReportAsync(second.Id, "heartbeat");
        }

        Program.Equal("a tab that keeps reporting is left alone", await rig.PollAsync(), null);
        // The page reloads: its new companion gets the prompt as sent, without its images, and only watches for the answer.
        var third = await rig.NextDeliveryAsync();
        Program.Equal("a quiet prompt that reached the chat is handed out to be watched", (third.Attempt, third.Sent, third.Attachments),
            (3, true, 0));
        await rig.ReportAsync(third.Id, "waiting");
        await rig.ReportAsync(third.Id, "receiving", "Found it");
        await rig.ReportAsync(third.Id, "waiting");
        await rig.ReportAsync(third.Id, "completed", "Found it.");
        Program.Equal("the picked-up prompt is answered", (await answer).Text, "Found it.");
        Program.Equal("once the answer streams, a prompt picked up again does not step back to waiting", rig.Progress.States,
            "Sending, Sending, Waiting, Waiting, Receiving, Completed");
    }

    /// <summary>No request waits forever: a tab that never answers or goes quiet for good ends it with an error.</summary>
    private static async Task LostTabAsync()
    {
        using var rig = await BridgeRig.StartAsync();
        await Program.ThrowsAsync<InvalidOperationException>("a tab that never answers ends the request",
            () => rig.Send("Hi"));

        var answer = rig.Send("Hi");
        var delivery = await rig.NextDeliveryAsync();
        await rig.ReportAsync(delivery.Id, "waiting");
        var error = await ErrorOfAsync(answer);
        Program.Equal("a tab gone quiet for good ends the request", error?.Contains("Lost contact", StringComparison.Ordinal), true);
        Program.Equal("its tab is told the request failed", await rig.ReportAsync(delivery.Id, "heartbeat"), (404, "failed"));

        var failing = rig.Send("Hi");
        delivery = await rig.NextDeliveryAsync();
        await rig.ReportAsync(delivery.Id, "error", error: "ChatGPT did not take the prompt.", diagnostics: "{\"version\":\"0.7.0\"}");
        Program.Equal("a tab's error ends the request", await ErrorOfAsync(failing), "ChatGPT did not take the prompt.");
        Program.Equal("an error carries what the tab saw", rig.Progress.Last?.Diagnostics, "{\"version\":\"0.7.0\"}");
    }

    private static async Task CancelAsync()
    {
        using var rig = await BridgeRig.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var answer = rig.Send("Hi", cancellation: cancellation.Token);
        var delivery = await rig.NextDeliveryAsync();
        await rig.ReportAsync(delivery.Id, "reconnecting");
        Program.Equal("reconnecting is shown", rig.Progress.Last?.State, ChatProgressState.Reconnecting);
        await rig.ReportAsync(delivery.Id, "waiting");
        cancellation.Cancel();
        await Program.ThrowsAsync<OperationCanceledException>("Stop ends the request", () => answer);
        Program.Equal("the tab is told to stop ChatGPT", await rig.ReportAsync(delivery.Id, "receiving", "Hel"), (404, "cancelled"));
        Program.Equal("nothing shows after Stop", rig.Progress.Last?.State, ChatProgressState.Waiting);
    }

    /// <summary>Check chat completes a prompt only with the answer to that prompt.</summary>
    private static async Task CheckChatAsync()
    {
        using var rig = await BridgeRig.StartAsync();
        var answer = rig.Send("Hi");
        var prompt = await rig.NextDeliveryAsync();
        await rig.ReportAsync(prompt.Id, "sending");

        var early = rig.Bridge.ReadAsync(BridgeRig.Chat, CancellationToken.None);
        var read = await rig.NextDeliveryAsync("read");
        await rig.PostEventAsync(new { requestId = read.Id, state = "snapshot", text = "Old answer.", generating = false });
        Program.Equal("a prompt still on its way is not answered by the chat's last answer",
            ((await early).CompletedWaitingPrompt, answer.IsCompleted), (false, false));

        await rig.ReportAsync(prompt.Id, "waiting");
        var other = rig.Bridge.ReadAsync(BridgeRig.Chat, CancellationToken.None);
        read = await rig.NextDeliveryAsync("read");
        await rig.PostEventAsync(new { requestId = read.Id, state = "snapshot", text = "Other.", generating = false, answerTo = Guid.NewGuid() });
        Program.Equal("an answer to another prompt does not complete this one", (await other).CompletedWaitingPrompt, false);

        var check = rig.Bridge.ReadAsync(BridgeRig.Chat, CancellationToken.None);
        read = await rig.NextDeliveryAsync("read");
        await rig.PostEventAsync(new { requestId = read.Id, state = "snapshot", text = "Done.", generating = false, answerTo = prompt.Id });
        Program.Equal("the chat's finished answer completes the waiting prompt", ((await check).CompletedWaitingPrompt, (await answer).Text),
            (true, "Done."));
        Program.Equal("the tab's own watch then stops quietly", await rig.ReportAsync(prompt.Id, "heartbeat"), (404, "completed"));
    }

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
}

/// <summary>A bridge on a free port with short waits, and the HTTP calls its browser companion makes.</summary>
internal sealed class BridgeRig : IDisposable
{
    public const string Chat = "chat-1";

    public static readonly BridgeTimings Timings = new(
        Acknowledge: TimeSpan.FromMilliseconds(600),
        Redeliver: TimeSpan.FromMilliseconds(250),
        Silence: TimeSpan.FromMilliseconds(900),
        Request: TimeSpan.FromSeconds(10),
        Read: TimeSpan.FromSeconds(2),
        Watchdog: TimeSpan.FromMilliseconds(20));

    private readonly HttpClient _http;

    private BridgeRig()
    {
        Bridge = new BrowserBridgeServer(0, Timings);
        Bridge.Start();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Bridge.BoundPort}/link/") };
        _http.DefaultRequestHeaders.Add("X-Link-Bridge", "1");
    }

    public BrowserBridgeServer Bridge { get; }
    public ProgressLog Progress { get; } = new();

    public static async Task<BridgeRig> StartAsync()
    {
        var rig = new BridgeRig();
        var response = await rig._http.PostAsJsonAsync("announce", new { conversationId = Chat, title = "Chat", url = "https://chatgpt.com/c/chat-1" });
        response.EnsureSuccessStatusCode();
        return rig;
    }

    public Task<ChatResponse> Send(
        string prompt,
        IProgress<ChatProgress>? progress = null,
        bool attachment = false,
        CancellationToken cancellation = default) =>
        Bridge.EnqueueAsync(
            new BrowserBridgeRequest
            {
                RequestId = Guid.NewGuid(),
                ConversationId = Chat,
                Prompt = prompt,
                Attachments = attachment
                    ? [new BrowserBridgeAttachment { FileName = "shot.png", MimeType = "image/png", Base64 = "AA==" }]
                    : []
            },
            progress ?? Progress,
            cancellation);

    /// <summary>What the companion's poll gets: the next request, or null when there is none.</summary>
    public async Task<Delivery?> PollAsync()
    {
        using var response = await _http.GetAsync($"request?conversationId={Chat}");
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return new Delivery(
            root.GetProperty("requestId").GetGuid(),
            root.GetProperty("kind").GetString(),
            root.GetProperty("attempt").GetInt32(),
            root.GetProperty("sent").GetBoolean(),
            root.GetProperty("attachments").GetArrayLength());
    }

    /// <summary>
    /// Polls like the companion until a request comes, for at most a few seconds; of the given kind only, when a
    /// slow run lets another request be offered again meanwhile.
    /// </summary>
    public async Task<Delivery> NextDeliveryAsync(string? kind = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await PollAsync() is { } delivery && (kind is null || delivery.Kind == kind)) return delivery;
            await Task.Delay(20);
        }

        throw new TimeoutException("No request was handed to the tab.");
    }

    public Task<(int Status, string? Reason)> ReportAsync(
        Guid requestId,
        string state,
        string? text = null,
        string? error = null,
        string? diagnostics = null) =>
        PostEventAsync(new { requestId, state, text, error, diagnostics });

    public async Task<(int Status, string? Reason)> PostEventAsync(object bridgeEvent)
    {
        using var response = await _http.PostAsJsonAsync("event", bridgeEvent);
        if (response.IsSuccessStatusCode) return ((int)response.StatusCode, null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ((int)response.StatusCode, json.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
    }

    public void Dispose()
    {
        _http.Dispose();
        Bridge.Dispose();
    }

    public sealed record Delivery(Guid Id, string? Kind, int Attempt, bool Sent, int Attachments);
}

/// <summary>Progress kept in the order the bridge reports it.</summary>
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

    public void Report(ChatProgress value)
    {
        lock (_reports) _reports.Add(value);
    }
}
