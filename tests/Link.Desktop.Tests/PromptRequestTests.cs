using System;
using System.Linq;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>An attempt's request comes from the input snapshot alone: every attempt at it is a new request.</summary>
internal static class PromptRequestTests
{
    public static void Run()
    {
        var state = new ContextItem { Type = ContextItemType.ProjectState, Label = "Project state", Text = "Land is lit by one sun" };
        var clipboard = new ContextItem { Type = ContextItemType.Clipboard, Label = "Clipboard", Text = "error 42" };
        var screen = new ContextItem
        {
            Type = ContextItemType.Screenshot,
            Label = "Current application",
            FilePath = @"C:\Screenshots\window.png",
            MimeType = "image/png"
        };
        var snapshot = new InputSnapshot
        {
            ProjectId = Guid.NewGuid(),
            ProjectName = "Nori",
            CurrentTask = "Fix Land",
            Text = " Why is it dark? ",
            Context = [state, clipboard],
            WorkTarget = new CurrentApplicationContext { ProcessName = "blender", WindowTitle = "Nori.blend" },
            WorkWindow = new IntPtr(42),
            Screen = screen
        };

        var request = RequestService.Build(snapshot, ResponseEffort.Deep);
        Program.Equal("a request is for the snapshot's project", request.ProjectId, snapshot.ProjectId);
        Program.Equal("a request names the project", request.ProjectName, "Nori");
        Program.Equal("a request carries the project's task", request.CurrentTask, "Fix Land");
        Program.Equal("a request carries the prompt without edge spaces", request.Text, "Why is it dark?");
        Program.Equal("a request has the attempt's depth", request.Effort, ResponseEffort.Deep);
        Program.Equal("a request names the work target", request.CurrentApplication?.WindowTitle, "Nori.blend");
        Program.Equal("the work window's screenshot follows the context stack",
            string.Join(", ", request.Context.Select(item => item.Label)), "Project state, Clipboard, Current application");

        var again = RequestService.Build(snapshot, ResponseEffort.Normal);
        Program.Equal("another attempt is another request", again.Id != request.Id, true);
        Program.Equal("another attempt sends the same context", again.Context.SequenceEqual(request.Context), true);
        Program.Equal("an attempt leaves the snapshot as it was", snapshot.Context.Count, 2);

        var summary = RequestService.Build(
            new InputSnapshot { ProjectId = snapshot.ProjectId, ProjectName = "Nori", Text = "Summarize this chat." },
            ResponseEffort.Fast);
        Program.Equal("a snapshot of text alone sends no context", summary.Context.Count, 0);
        Program.Equal("a snapshot without a work target names no application", summary.CurrentApplication is null, true);
    }
}
