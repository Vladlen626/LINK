using System;
using System.Linq;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>
/// An attempt's request comes from the input snapshot alone: every attempt at it is a new request, with what Link
/// knows.
/// </summary>
internal static class PromptRequestTests
{
    public static void Run()
    {
        var state = new ContextItem { Type = ContextItemType.ProjectState, Label = "Project state", Text = "Land is lit by one sun" };
        var clipboard = new ContextItem { Type = ContextItemType.Clipboard, Label = "Clipboard", Text = "error 42" };
        var region = new ContextItem
        {
            Type = ContextItemType.Screenshot,
            Label = "Region",
            FilePath = @"C:\Screenshots\region.png",
            MimeType = "image/png"
        };
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
            Text = " Why is it dark? ",
            Context = [state, clipboard, region],
            WorkTarget = new CurrentApplicationContext { ProcessName = "blender", WindowTitle = "Nori.blend" },
            WorkWindow = new IntPtr(42),
            Screen = screen
        };

        var chat = RequestService.Build(snapshot);
        Program.Equal("a request is for the snapshot's project", chat.ProjectId, snapshot.ProjectId);
        Program.Equal("a request names the project", chat.ProjectName, "Nori");
        Program.Equal("a request carries the prompt without edge spaces", chat.Text, "Why is it dark?");
        Program.Equal("a request names the work target", chat.CurrentApplication?.WindowTitle, "Nori.blend");
        Program.Equal("the work window's screenshot follows the context stack", Labels(chat), "Project state, Clipboard, Region, Current application");
        Program.Equal(
            "Chat adds the project, the work window and the task",
            ChatPrompt.Format(chat),
            """
            Why is it dark?

            ---
            Context
            Project: Nori

            Application: blender

            Window: Nori.blend

            Project state:
            Land is lit by one sun

            Clipboard:
            error 42
            """.ReplaceLineEndings("\n"));

        var again = RequestService.Build(snapshot);
        Program.Equal("another attempt is another request", again.Id != chat.Id, true);
        Program.Equal("another attempt sends the same context", again.Context.SequenceEqual(chat.Context), true);
        Program.Equal("an attempt leaves the snapshot as it was", snapshot.Context.Count, 3);

        var summary = RequestService.Build(
            new InputSnapshot { ProjectId = snapshot.ProjectId, ProjectName = "Nori", Text = "Summarize this chat." });
        Program.Equal("a snapshot of text alone sends no context", summary.Context.Count, 0);
        Program.Equal("a snapshot without a work target names no application", summary.CurrentApplication is null, true);
    }

    private static string Labels(PromptRequest request) => string.Join(", ", request.Context.Select(item => item.Label));
}
