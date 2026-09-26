using System.Diagnostics;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The life of LINK's requests to the ChatGPT page: a request is offered to the page's companion, followed through the
/// events it reports and ended when it is answered, fails, goes quiet or is stopped. The page is reached by
/// <see cref="ChatPageHost"/>.
/// </summary>
internal sealed class ChatRequestBroker : IDisposable
{
    // Requests that ended recently stay known, so a page still working on one learns why LINK dropped it.
    private const int EndedRequestsKept = 64;
    private const string Unresponsive = "The ChatGPT page did not respond. Open chat shows it; try again once it is there.";
    private readonly object _gate = new();
    private readonly BridgeTimings _timings;
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<Guid, string> _ended = [];
    private readonly Queue<Guid> _endedOrder = new();

    public ChatRequestBroker(BridgeTimings timings) => _timings = timings;

    /// <summary>A request was queued for a conversation: its page may take it now rather than at its next look.</summary>
    public event Action<string>? RequestQueued;

    public bool HasPending
    {
        get
        {
            lock (_gate) return _pending.Count > 0;
        }
    }

    /// <summary>
    /// Sends a prompt to the page and waits for the answer. Every way it can end is watched: a page that never speaks
    /// up, one that goes quiet for good, a request that takes too long. A page quiet about a prompt for a while is
    /// offered it again, so a reloaded page or companion picks it up where it stands.
    /// </summary>
    public async Task<ChatResponse> SendAsync(
        ChatPageRequest request,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = Enqueue(request, progress);
        progress?.Report(new ChatProgress { State = ChatProgressState.Sending });
        // From the moment Stop is pressed, what the page still sends is turned away, and the page stops ChatGPT.
        using var cancelled = cancellationToken.Register(() => End(pending, EndReason.Cancelled));

        // Every exit removes the request, so a failed or cancelled prompt can never be picked up later.
        try
        {
            while (true)
            {
                if (pending.Completion.Task.IsCompleted) return await pending.Completion.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (FindStall(pending) is { } problem)
                {
                    End(pending, EndReason.Failed);
                    // The answer may have arrived just now; then it is returned after all.
                    if (!pending.Completion.Task.IsCompleted) throw new InvalidOperationException(problem);
                    continue;
                }

                await Task.WhenAny(pending.Completion.Task, Task.Delay(_timings.Watchdog, cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Stop may end the wait before its callback has run; the page must still learn it was a Stop.
            if (cancellationToken.IsCancellationRequested) End(pending, EndReason.Cancelled);
            Forget(pending);
        }
    }

    /// <summary>What the page shows now; the page answers at once, even while a prompt there still waits.</summary>
    public async Task<ChatSnapshot> ReadAsync(string conversationId, CancellationToken cancellationToken)
    {
        var request = new ChatPageRequest
        {
            RequestId = Guid.NewGuid(),
            ConversationId = conversationId,
            Kind = ChatPageRequest.ReadKind,
            Prompt = string.Empty
        };
        var pending = Enqueue(request, null);
        try
        {
            return await pending.Snapshot.Task.WaitAsync(_timings.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(Unresponsive, exception);
        }
        finally
        {
            End(pending, EndReason.Failed);
            Forget(pending);
        }
    }

    /// <summary>
    /// The request the page should work on next. One is offered until the page speaks up about it, and again whenever
    /// the page has gone quiet about it: a page or companion that was replaced meanwhile picks it up where it stands.
    /// </summary>
    /// <param name="conversationId">The conversation the page shows; empty for a new chat's page.</param>
    public ChatPageDelivery? NextDelivery(string conversationId)
    {
        lock (_gate)
        {
            var pending = _pending.Values
                .Where(candidate => candidate.ConversationId == conversationId &&
                                    candidate.EndReason is null &&
                                    (candidate.OfferedAt is not { } offeredAt ||
                                     Since(Math.Max(offeredAt, candidate.HeardAt ?? offeredAt)) >= _timings.Redeliver))
                .OrderBy(candidate => candidate.QueuedAt)
                .FirstOrDefault();
            if (pending is null) return null;

            pending.OfferedAt = Stopwatch.GetTimestamp();
            pending.Offers++;
            var request = pending.Request;
            // A prompt that reached the chat is only watched when offered again, so its images are not sent twice.
            return new ChatPageDelivery(
                request.RequestId,
                pending.ConversationId,
                request.Kind,
                request.Prompt,
                request.Effort,
                pending.Sent ? [] : request.Attachments,
                pending.Offers,
                pending.Sent);
        }
    }

    /// <summary>
    /// A new companion took over the conversation's page (it was reloaded): its requests are offered to it at once
    /// instead of after the usual quiet.
    /// </summary>
    public void OfferAgain(string conversationId)
    {
        lock (_gate)
        {
            foreach (var pending in _pending.Values.Where(pending => pending.ConversationId == conversationId))
            {
                pending.OfferedAt = null;
            }
        }
    }

    /// <summary>
    /// Applies what the page reports. False when the request is no longer active; <paramref name="endReason"/> then
    /// tells the page why, and only a cancelled request makes it stop ChatGPT.
    /// </summary>
    public bool Report(ChatPageEvent pageEvent, out string endReason)
    {
        PendingRequest? pending;
        string? ended;
        lock (_gate)
        {
            if (_pending.TryGetValue(pageEvent.RequestId, out pending)) pending.HeardAt = Stopwatch.GetTimestamp();
            _ended.TryGetValue(pageEvent.RequestId, out ended);
        }

        if (pending is not null && ApplyEvent(pending, pageEvent))
        {
            endReason = string.Empty;
            return true;
        }

        endReason = pending?.EndReason ?? ended ?? "unknown";
        return false;
    }

    public void Dispose()
    {
        PendingRequest[] pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var request in pending)
        {
            lock (request)
            {
                request.EndReason ??= EndReason.Failed;
                var stopped = new InvalidOperationException("LINK's connection to ChatGPT was closed.");
                request.Completion.TrySetException(stopped);
                request.Snapshot.TrySetException(stopped);
            }
        }
    }

    private PendingRequest Enqueue(ChatPageRequest request, IProgress<ChatProgress>? progress)
    {
        var pending = new PendingRequest(request, progress);
        lock (_gate)
        {
            _pending.Add(request.RequestId, pending);
        }

        RequestQueued?.Invoke(request.ConversationId);
        return pending;
    }

    /// <summary>Why a waiting prompt cannot be answered any more, or null while it still can.</summary>
    private string? FindStall(PendingRequest pending)
    {
        lock (_gate)
        {
            if (pending.HeardAt is not { } heardAt)
            {
                return Since(pending.QueuedAt) > _timings.Acknowledge ? Unresponsive : null;
            }

            if (Since(heardAt) > _timings.Silence)
            {
                return pending.Sent
                    ? "Lost contact with the ChatGPT page while it answered. The answer stays in the chat: Check chat reads it."
                    : "Lost contact with the ChatGPT page before the prompt was sent. Try again.";
            }

            return Since(pending.QueuedAt) > _timings.Request ? "Timed out waiting for ChatGPT's answer." : null;
        }
    }

    private void Forget(PendingRequest pending)
    {
        lock (_gate)
        {
            _pending.Remove(pending.Request.RequestId);
            _ended[pending.Request.RequestId] = pending.EndReason ?? EndReason.Failed;
            _endedOrder.Enqueue(pending.Request.RequestId);
            while (_endedOrder.Count > EndedRequestsKept) _ended.Remove(_endedOrder.Dequeue());
        }
    }

    /// <summary>Applies what the page reports; false when the request has already ended.</summary>
    private bool ApplyEvent(PendingRequest pending, ChatPageEvent pageEvent)
    {
        var state = pageEvent.State.ToLowerInvariant();
        if (state == "snapshot") return ApplySnapshot(pending, pageEvent);

        lock (pending)
        {
            if (pending.EndReason is not null) return false;
            Adopt(pending, pageEvent.Conversation);
            switch (state)
            {
                case "heartbeat":
                    // Keeps the page informed that the request is still wanted; an ended one is turned away.
                    break;
                case "reconnecting":
                    pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Reconnecting });
                    break;
                case "notice":
                    if (!string.IsNullOrWhiteSpace(pageEvent.Notice))
                    {
                        pending.Progress?.Report(new ChatProgress
                        {
                            State = ChatProgressState.Sending,
                            Notice = pageEvent.Notice.Trim()
                        });
                    }
                    break;
                case "sending":
                    pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Sending });
                    break;
                case "waiting":
                    pending.Sent = true;
                    // A page that picked the prompt up again reports waiting once more; the answer so far stays.
                    if (!pending.Receiving) pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Waiting });
                    break;
                case "receiving":
                    pending.Sent = true;
                    pending.Receiving = true;
                    pending.Progress?.Report(new ChatProgress
                    {
                        State = ChatProgressState.Receiving,
                        Text = pageEvent.Text ?? string.Empty
                    });
                    break;
                case "completed":
                    var text = pageEvent.Text?.Trim() ?? string.Empty;
                    if (text.Length == 0) Fail(pending, "ChatGPT returned an empty response.", pageEvent.Diagnostics);
                    else Complete(pending, text, pageEvent.Diagnostics);
                    break;
                case "error":
                    Fail(
                        pending,
                        string.IsNullOrWhiteSpace(pageEvent.Error)
                            ? "The ChatGPT page could not complete the request."
                            : pageEvent.Error.Trim(),
                        pageEvent.Diagnostics);
                    break;
            }

            return true;
        }
    }

    /// <summary>
    /// A prompt to a new chat learns the conversation ChatGPT started for it: the request follows it from then on, e.g.
    /// to a reloaded page, and LINK links the project to it.
    /// </summary>
    private void Adopt(PendingRequest pending, ChatPageConversation? conversation)
    {
        if (conversation is null ||
            !ChatPageAddress.IsConversationId(conversation.ConversationId) ||
            !ChatPageAddress.IsChatGpt(conversation.Url))
        {
            return;
        }

        lock (_gate)
        {
            if (pending.ConversationId.Length > 0) return;
            pending.ConversationId = conversation.ConversationId!;
        }

        pending.Progress?.Report(new ChatProgress
        {
            State = ChatProgressState.ChatStarted,
            StartedConversation = new AvailableChatConversation
            {
                ConversationId = conversation.ConversationId!,
                Title = string.IsNullOrWhiteSpace(conversation.Title) ? "ChatGPT conversation" : conversation.Title.Trim(),
                Url = conversation.Url!,
                LastSeenAt = DateTimeOffset.Now
            }
        });
    }

    private bool ApplySnapshot(PendingRequest read, ChatPageEvent snapshot)
    {
        if (read.EndReason is not null) return false;
        var answer = snapshot.Text?.Trim() ?? string.Empty;
        var completedWaitingPrompt = !snapshot.Generating && answer.Length > 0 &&
                                     CompleteWaitingPrompts(read.Request.ConversationId, answer, snapshot.AnswerTo);
        lock (read)
        {
            if (read.EndReason is not null) return false;
            read.EndReason = EndReason.Completed;
            read.Snapshot.TrySetResult(new ChatSnapshot
            {
                Text = answer,
                Generating = snapshot.Generating,
                CompletedWaitingPrompt = completedWaitingPrompt,
                Diagnostics = snapshot.Diagnostics
            });
            return true;
        }
    }

    private static bool Complete(PendingRequest pending, string text, string? diagnostics)
    {
        lock (pending)
        {
            if (pending.EndReason is not null) return false;
            pending.EndReason = EndReason.Completed;
            pending.Progress?.Report(new ChatProgress
            {
                State = ChatProgressState.Completed,
                Text = text,
                Diagnostics = diagnostics
            });
            pending.Completion.TrySetResult(new ChatResponse { Text = text });
            return true;
        }
    }

    private static void Fail(PendingRequest pending, string error, string? diagnostics)
    {
        lock (pending)
        {
            if (pending.EndReason is not null) return;
            pending.EndReason = EndReason.Failed;
            pending.Progress?.Report(new ChatProgress
            {
                State = ChatProgressState.Error,
                Error = error,
                Diagnostics = diagnostics
            });
            pending.Completion.TrySetException(new InvalidOperationException(error));
        }
    }

    private static void End(PendingRequest pending, string reason)
    {
        lock (pending)
        {
            pending.EndReason ??= reason;
        }
    }

    /// <summary>
    /// A finished answer read from the chat completes the prompt there that still waits: the page's own watch missed
    /// the answer's end. Only a prompt that reached the chat is answered by what the chat shows after it, and when
    /// the page says which prompt the answer belongs to, only that one.
    /// </summary>
    private bool CompleteWaitingPrompts(string conversationId, string answer, Guid? answerTo)
    {
        PendingRequest[] waiting;
        lock (_gate)
        {
            waiting = _pending.Values
                .Where(pending => pending.Sent &&
                                  pending.Request.Kind != ChatPageRequest.ReadKind &&
                                  pending.ConversationId == conversationId &&
                                  (answerTo is null || pending.Request.RequestId == answerTo))
                .ToArray();
        }

        var completed = false;
        foreach (var pending in waiting) completed |= Complete(pending, answer, null);
        return completed;
    }

    private static TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    /// <summary>Why a request ended, as the page is told when it still reports on it.</summary>
    private static class EndReason
    {
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    private sealed class PendingRequest
    {
        public PendingRequest(ChatPageRequest request, IProgress<ChatProgress>? progress)
        {
            Request = request;
            Progress = progress;
            ConversationId = request.ConversationId;
        }

        public ChatPageRequest Request { get; }
        public IProgress<ChatProgress>? Progress { get; }
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();

        // Written under the broker's gate: the conversation (empty until a new chat has one), when the request was
        // offered to a page, how often, and when a page last spoke.
        public string ConversationId { get; set; }
        public long? OfferedAt { get; set; }
        public int Offers { get; set; }
        public long? HeardAt { get; set; }

        // Written under the request's own lock, together with its progress, so nothing is reported after the end.
        /// <summary>The prompt reached the chat: what the chat shows after it is its answer.</summary>
        public bool Sent { get; set; }
        public bool Receiving { get; set; }
        public string? EndReason { get; set; }

        public TaskCompletionSource<ChatResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatSnapshot> Snapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>How long LINK waits for a ChatGPT page at each step.</summary>
/// <param name="Acknowledge">For the page's first word about a prompt.</param>
/// <param name="Redeliver">Of quiet about a request before it is offered to the page again.</param>
/// <param name="Silence">Of quiet about a prompt before its page counts as lost.</param>
/// <param name="Request">For the whole answer.</param>
/// <param name="Read">For a check of the chat.</param>
/// <param name="Watchdog">Between checks of the waits above.</param>
internal sealed record BridgeTimings(
    TimeSpan Acknowledge,
    TimeSpan Redeliver,
    TimeSpan Silence,
    TimeSpan Request,
    TimeSpan Read,
    TimeSpan Watchdog)
{
    public static BridgeTimings Default { get; } = new(
        Acknowledge: TimeSpan.FromSeconds(20),
        Redeliver: TimeSpan.FromSeconds(10),
        Silence: TimeSpan.FromSeconds(45),
        Request: TimeSpan.FromMinutes(10),
        Read: TimeSpan.FromSeconds(15),
        Watchdog: TimeSpan.FromSeconds(1));
}

/// <summary>A LINK request as the page's companion gets it.</summary>
internal sealed class ChatPageRequest
{
    /// <summary>A read of what the chat shows now, answered by the page at once instead of a prompt.</summary>
    public const string ReadKind = "read";

    public required Guid RequestId { get; init; }

    /// <summary>Empty for a prompt that starts a new chat.</summary>
    public required string ConversationId { get; init; }
    public string Kind { get; init; } = "prompt";
    public required string Prompt { get; init; }
    public string Effort { get; init; } = "normal";
    public IReadOnlyList<ChatPageAttachment> Attachments { get; init; } = [];
}

internal sealed class ChatPageAttachment
{
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required string Base64 { get; init; }
}

/// <summary>A request as it is handed to the page: how often it was offered, and whether it already reached the chat.</summary>
internal sealed record ChatPageDelivery(
    Guid RequestId,
    string ConversationId,
    string Kind,
    string Prompt,
    string Effort,
    IReadOnlyList<ChatPageAttachment> Attachments,
    int Attempt,
    bool Sent);

/// <summary>What the page's companion reports about a request.</summary>
internal sealed class ChatPageEvent
{
    public Guid RequestId { get; init; }
    public required string State { get; init; }
    public string? Text { get; init; }
    public string? Error { get; init; }
    public string? Notice { get; init; }
    public bool Generating { get; init; }

    /// <summary>The prompt a snapshot's answer belongs to, when the page knows it.</summary>
    public Guid? AnswerTo { get; init; }

    /// <summary>The conversation a prompt to a new chat started, once ChatGPT has given it an id.</summary>
    public ChatPageConversation? Conversation { get; init; }

    public string? Diagnostics { get; init; }
}

/// <summary>A conversation as a ChatGPT page names it; data from the page, checked before use.</summary>
internal sealed class ChatPageConversation
{
    public string? ConversationId { get; init; }
    public string? Title { get; init; }
    public string? Url { get; init; }
}
