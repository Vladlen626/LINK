using System.Diagnostics;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class RequestService
{
    private readonly StoragePaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public RequestService(StoragePaths paths) => _paths = paths;

    public string BuildAndLog(LinkProject project, string text, IEnumerable<ContextItem> context)
    {
        var request = new PromptRequest
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            CurrentTask = project.CurrentTask,
            Text = text.Trim(),
            Context = context.ToArray()
        };

        var json = JsonSerializer.Serialize(request, _json);
        File.WriteAllText(_paths.LastRequestFile, json);
        Debug.WriteLine(json);
        return json;
    }
}
