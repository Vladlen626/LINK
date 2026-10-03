using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class RequestService
{
    private readonly StoragePaths _paths;
    private readonly AppSettings _settings;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public RequestService(StoragePaths paths, AppSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    /// <summary>
    /// The request of one attempt at a snapshot: every attempt is a new request from the same input. Direct carries
    /// only the text and the context the user attached; the chat's mode still follows the depth.
    /// </summary>
    public static PromptRequest Build(InputSnapshot snapshot, RequestRoute route, ResponseEffort effort, bool conversation)
    {
        var direct = route == RequestRoute.Direct;
        return new PromptRequest
        {
            ProjectId = snapshot.ProjectId,
            ProjectName = snapshot.ProjectName,
            Route = route,
            Text = snapshot.Text.Trim(),
            Effort = effort,
            Conversation = conversation && !direct,
            CurrentApplication = direct ? null : snapshot.WorkTarget,
            Context = direct ? [.. snapshot.Context.Where(item => item.Type != ContextItemType.ProjectState)]
                : snapshot.Screen is { } screen ? [.. snapshot.Context, screen]
                : [.. snapshot.Context]
        };
    }

    /// <summary>
    /// With diagnostics on, the request is kept in last-request.json: its prompt, file paths and window title.
    /// Otherwise no such file is left behind.
    /// </summary>
    public void Log(PromptRequest request)
    {
        try
        {
            if (_settings.Diagnostics)
            {
                AtomicJsonFile.Write(_paths.LastRequestFile, request, _json);
                return;
            }

            File.Delete(_paths.LastRequestFile);
            File.Delete($"{_paths.LastRequestFile}.bak");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic file is not worth failing a message over.
        }
    }
}
