using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Leads one attempt at an input snapshot: builds its request, sends it to the project's chat, times each step for
/// link.log and keeps the answer as the project's last response. The window takes the snapshot before and draws,
/// reads aloud and announces the answer after. The last snapshots of each project are kept with their files, so a
/// new attempt at one needs no new input.
/// </summary>
public sealed class AttemptCoordinator
{
    private const int SnapshotsPerProject = 3;
    private readonly IChatIntegration _chat;
    private readonly ProjectService _projects;
    private readonly RequestService _requests;
    private readonly ScreenshotHistoryService _history;
    private readonly Action<string> _log;
    private readonly Dictionary<Guid, List<InputSnapshot>> _snapshots = [];

    public AttemptCoordinator(
        IChatIntegration chat,
        ProjectService projects,
        RequestService requests,
        ScreenshotHistoryService history,
        Action<string> log)
    {
        _chat = chat;
        _projects = projects;
        _requests = requests;
        _history = history;
        _log = log;
    }

    /// <summary>
    /// Keeps a snapshot alive for attempts at it: its files stay out of the screenshot history's pruning. A project
    /// keeps its last three; an older one is let go.
    /// </summary>
    public void Keep(InputSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(snapshot.ProjectId, out var kept)) _snapshots[snapshot.ProjectId] = kept = [];
        if (kept.Any(other => other.Id == snapshot.Id)) return;
        foreach (var file in FilesOf(snapshot)) _history.Pin(file);
        kept.Add(snapshot);
        if (kept.Count <= SnapshotsPerProject) return;
        LetGo(kept[0]);
        kept.RemoveAt(0);
        _history.Prune();
    }

    /// <summary>Lets go of a project's snapshots, or of every project's when Link exits.</summary>
    public void Forget(Guid? projectId = null)
    {
        foreach (var (id, kept) in _snapshots.Where(pair => projectId is null || pair.Key == projectId).ToArray())
        {
            foreach (var snapshot in kept) LetGo(snapshot);
            _snapshots.Remove(id);
        }

        _history.Prune();
    }

    /// <summary>
    /// Sends the snapshot to its project's chat by a route and depth and returns the answer; throws when the chat
    /// fails or the request is cancelled. <paramref name="spoken"/>: asked in conversation mode, where the answer is
    /// read aloud. The latency started when the user asked, before Link's own work; progress is reported on the
    /// calling thread.
    /// </summary>
    public async Task<ChatResponse> SendAsync(
        InputSnapshot snapshot,
        RequestRoute route,
        ResponseEffort effort,
        bool spoken,
        RequestLatency latency,
        Action<ChatProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var project = _projects.Projects.First(project => project.Id == snapshot.ProjectId);
        // Chat and Direct both go to the project's chat.
        var conversation = project.LinkedConversation
            ?? throw new InvalidOperationException("Link this project to a ChatGPT conversation first.");
        var request = RequestService.Build(snapshot, route, effort, spoken);
        var attempt = request.Conversation ? $"{route}, {effort}, conversation" : $"{route}, {effort}";
        _requests.Log(request);
        var progress = new Progress<ChatProgress>(update =>
        {
            if (update.State == ChatProgressState.ChatStarted) LinkStartedChat(project, update.StartedConversation);
            else latency.Heard(update);
            if (update.Diagnostics is { } diagnostics)
            {
                _log($"Chat {update.State.ToString().ToLowerInvariant()}: {diagnostics}");
            }

            onProgress(update);
        });
        latency.HandedOver();
        try
        {
            var response = await _chat.SendAsync(request, conversation, progress, cancellationToken);
            // A new chat is linked by now: the answer belongs to the conversation it started.
            RefreshLinkedTitle(project);
            project.LastResponse = new ProjectResponse
            {
                ConversationId = project.LinkedConversation?.ConversationId ?? conversation.ConversationId,
                Text = response.Text,
                Picture = response.Picture
            };
            _projects.Save();
            _log(latency.Describe(attempt, "answer", response.Text.Length));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log(latency.Describe(attempt, "cancelled"));
            throw;
        }
        catch (Exception exception)
        {
            _log(latency.Describe(attempt, $"failed ({exception.Message})"));
            throw;
        }
    }

    /// <summary>
    /// The screenshot the snapshot took itself is deleted, like the work window's screenshot always was after a
    /// message; the files it shared with the context stack go back to the screenshot history.
    /// </summary>
    private void LetGo(InputSnapshot snapshot)
    {
        foreach (var file in FilesOf(snapshot)) _history.Unpin(file);
        if (snapshot.Screen?.FilePath is not { } screen) return;
        try
        {
            File.Delete(screen);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows may hold the file a moment longer; the history's pruning removes it later.
        }
    }

    private static IEnumerable<string> FilesOf(InputSnapshot snapshot) =>
        snapshot.Context.Append(snapshot.Screen).Select(item => item?.FilePath).OfType<string>();

    /// <summary>The prompt started the project's new chat: the project is linked to that conversation from now on.</summary>
    private void LinkStartedChat(LinkProject project, AvailableChatConversation? started)
    {
        if (started is null || project.LinkedConversation is not { IsNew: true } link) return;
        project.LinkedConversation = new ChatConversationLink
        {
            TransportId = link.TransportId,
            ConversationId = started.ConversationId,
            Title = started.Title,
            Url = started.Url
        };
        _projects.Save();
    }

    /// <summary>ChatGPT names a new chat once it has answered, and a chat can be renamed there: the link follows.</summary>
    private void RefreshLinkedTitle(LinkProject project)
    {
        if (project.LinkedConversation is not { IsNew: false } link) return;
        var seen = _chat.GetAvailableConversations().FirstOrDefault(conversation => conversation.ConversationId == link.ConversationId);
        if (seen is null || seen.Title == link.Title) return;
        project.LinkedConversation = new ChatConversationLink
        {
            TransportId = link.TransportId,
            ConversationId = link.ConversationId,
            Title = seen.Title,
            Url = link.Url
        };
    }
}
