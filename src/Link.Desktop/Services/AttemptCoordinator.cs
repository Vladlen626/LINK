using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Leads one attempt at an input snapshot: builds its request, sends it to the project's chat, times each step for
/// link.log and keeps the answer as the project's last response. The window takes the snapshot before and draws,
/// reads aloud and announces the answer after.
/// </summary>
public sealed class AttemptCoordinator
{
    private readonly IChatIntegration _chat;
    private readonly ProjectService _projects;
    private readonly RequestService _requests;
    private readonly Action<string> _log;

    public AttemptCoordinator(IChatIntegration chat, ProjectService projects, RequestService requests, Action<string> log)
    {
        _chat = chat;
        _projects = projects;
        _requests = requests;
        _log = log;
    }

    /// <summary>
    /// Sends the snapshot to its project's chat at a depth and returns the answer; throws when the chat fails or
    /// the request is cancelled. The latency started when the user asked, before Link's own work; progress is
    /// reported on the calling thread.
    /// </summary>
    public async Task<ChatResponse> SendAsync(
        InputSnapshot snapshot,
        ResponseEffort effort,
        RequestLatency latency,
        Action<ChatProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var project = _projects.Projects.First(project => project.Id == snapshot.ProjectId);
        var conversation = project.LinkedConversation
            ?? throw new InvalidOperationException("Link this project to a ChatGPT conversation first.");
        var request = RequestService.Build(snapshot, effort);
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
                Text = response.Text
            };
            _projects.Save();
            _log(latency.Describe("answer", response.Text.Length));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log(latency.Describe("cancelled"));
            throw;
        }
        catch (Exception exception)
        {
            _log(latency.Describe($"failed ({exception.Message})"));
            throw;
        }
    }

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
