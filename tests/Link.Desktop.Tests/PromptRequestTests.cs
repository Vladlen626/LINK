using System;
using System.Linq;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>
/// An attempt's request comes from the input snapshot alone: every attempt at it is a new request. Chat adds what Link
/// knows; Direct is the text and the attached context only.
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
            CurrentTask = "Fix Land",
            Text = " Why is it dark? ",
            Context = [state, clipboard, region],
            WorkTarget = new CurrentApplicationContext { ProcessName = "blender", WindowTitle = "Nori.blend" },
            WorkWindow = new IntPtr(42),
            Screen = screen
        };

        var chat = RequestService.Build(snapshot, RequestRoute.Chat, ResponseEffort.Deep);
        Program.Equal("a request is for the snapshot's project", chat.ProjectId, snapshot.ProjectId);
        Program.Equal("a request names the project", chat.ProjectName, "Nori");
        Program.Equal("a request goes by its route", chat.Route, RequestRoute.Chat);
        Program.Equal("a request carries the project's task", chat.CurrentTask, "Fix Land");
        Program.Equal("a request carries the prompt without edge spaces", chat.Text, "Why is it dark?");
        Program.Equal("a request has the attempt's depth", chat.Effort, ResponseEffort.Deep);
        Program.Equal("a request names the work target", chat.CurrentApplication?.WindowTitle, "Nori.blend");
        Program.Equal("the work window's screenshot follows the context stack", Labels(chat), "Project state, Clipboard, Region, Current application");
        Program.Equal(
            "Chat adds the depth hint, the project, the work window and the task",
            ChatPrompt.Format(chat),
            """
            Why is it dark?

            [Deep answer: think hard about this and give a thorough, carefully reasoned answer.]

            ---
            Context
            Project: Nori

            Application: blender

            Window: Nori.blend

            Current task: Fix Land

            Project state:
            Land is lit by one sun

            Clipboard:
            error 42
            """.ReplaceLineEndings("\n"));

        var again = RequestService.Build(snapshot, RequestRoute.Chat, ResponseEffort.Normal);
        Program.Equal("another attempt is another request", again.Id != chat.Id, true);
        Program.Equal("another attempt sends the same context", again.Context.SequenceEqual(chat.Context), true);
        Program.Equal("an attempt leaves the snapshot as it was", snapshot.Context.Count, 3);

        var direct = RequestService.Build(snapshot, RequestRoute.Direct, ResponseEffort.Deep);
        Program.Equal("Direct goes by its route", direct.Route, RequestRoute.Direct);
        Program.Equal("Direct keeps the depth for the chat's mode", direct.Effort, ResponseEffort.Deep);
        Program.Equal("Direct sends only the attached context", Labels(direct), "Clipboard, Region");
        Program.Equal("Direct names no work target", direct.CurrentApplication is null, true);
        Program.Equal("Direct carries no task", direct.CurrentTask, string.Empty);
        Program.Equal(
            "Direct is the text and the attached text alone",
            ChatPrompt.Format(direct),
            """
            Why is it dark?

            ---
            Context
            Clipboard:
            error 42
            """.ReplaceLineEndings("\n"));

        // Whatever a request carries, Direct's text has none of Link's lines.
        var linkLines = new PromptRequest
        {
            ProjectId = snapshot.ProjectId,
            ProjectName = "Nori",
            Route = RequestRoute.Direct,
            CurrentTask = "Fix Land",
            Text = "Why?",
            Effort = ResponseEffort.Fast,
            CurrentApplication = snapshot.WorkTarget
        };
        Program.Equal("Direct writes no project, window, task or depth hint", ChatPrompt.Format(linkLines), "Why?");

        var summary = RequestService.Build(
            new InputSnapshot { ProjectId = snapshot.ProjectId, ProjectName = "Nori", Text = "Summarize this chat." },
            RequestRoute.Chat,
            ResponseEffort.Fast);
        Program.Equal("a snapshot of text alone sends no context", summary.Context.Count, 0);
        Program.Equal("a snapshot without a work target names no application", summary.CurrentApplication is null, true);
    }

    private static string Labels(PromptRequest request) => string.Join(", ", request.Context.Select(item => item.Label));
}
