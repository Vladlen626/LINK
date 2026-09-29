using System.Diagnostics;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class RequestService
{
    private readonly StoragePaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public RequestService(StoragePaths paths) => _paths = paths;

    /// <summary>The request of one attempt at a snapshot: every attempt is a new request from the same input.</summary>
    public static PromptRequest Build(InputSnapshot snapshot, ResponseEffort effort) => new()
    {
        ProjectId = snapshot.ProjectId,
        ProjectName = snapshot.ProjectName,
        CurrentTask = snapshot.CurrentTask,
        Text = snapshot.Text.Trim(),
        Effort = effort,
        CurrentApplication = snapshot.WorkTarget,
        Context = snapshot.Screen is { } screen ? [.. snapshot.Context, screen] : [.. snapshot.Context]
    };

    public void Log(PromptRequest request)
    {
        var json = JsonSerializer.Serialize(request, _json);
        AtomicJsonFile.Write(_paths.LastRequestFile, request, _json);
        Debug.WriteLine(json);
    }
}
