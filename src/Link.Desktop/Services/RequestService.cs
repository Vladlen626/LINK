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

    /// <summary>The request of one attempt at a snapshot: every attempt is a new request from the same input.</summary>
    public static PromptRequest Build(InputSnapshot snapshot) => new()
    {
        ProjectId = snapshot.ProjectId,
        ProjectName = snapshot.ProjectName,
        Text = snapshot.Text.Trim(),
        CurrentApplication = snapshot.WorkTarget,
        Context = snapshot.Screen is { } screen ? [.. snapshot.Context, screen] : [.. snapshot.Context]
    };

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
