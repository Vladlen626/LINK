using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>
/// The agent adapters read the events of real runs (the fixtures), and the runner handles a CLI that streams, stalls,
/// breaks off, writes junk or fails. The fake CLI is this test program itself, started with <c>--fake-agent</c>.
/// </summary>
internal static class AgentTests
{
    public const string FakeFlag = "--fake-agent";

    public static async Task RunAsync()
    {
        ClaudeFixtures();
        CodexFixtures();
        Stats();
        Loops();
        Arguments();
        await StreamAsync();
        await LoopRunAsync();
        await FailureAsync();
        await StopAsync();
        await LeftoversAsync();
        await GitUndoAsync();
        await CopyRestoreAsync();
        await MapAsync();
        await SkillAsync();
        ProjectCheck();
        await CheckCommandAsync();
        await CheckRetriesAsync();
        await StartOverAsync();
        ContinueAndPermissions();
        Choices();
    }

    /// <summary>Settings offers each CLI's own effort levels up to max, Claude's models and Codex's list of the account.</summary>
    private static void Choices()
    {
        var settings = new AgentSettings();
        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var codex = new CodexProvider(() => settings, _ => { });
        Program.Equal("choices: efforts lowest first, max the highest, no ultra (it works through subagents)",
            (string.Join(",", claude.Efforts.Select(effort => effort.Value)), string.Join(",", codex.Efforts.Select(effort => effort.Value))),
            ("low,medium,high,xhigh,max", "low,medium,high,xhigh,max"));
        Program.Equal("choices: Claude's models by full name, Opus 5.5 first", claude.Models[0], new AgentChoice("claude-opus-5-5", "Opus 5.5"));

        var home = TempFolder();
        var before = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", home);
            Program.Equal("choices: no Codex list, no models", codex.Models.Count, 0);
            File.WriteAllText(Path.Combine(home, "models_cache.json"),
                """{"models":[{"slug":"gpt-6-sol","display_name":"GPT-6-Sol","visibility":"list"},{"slug":"gpt-reserve","visibility":"hide"},{"slug":"gpt-5.5","visibility":"list"}]}""");
            Program.Equal("choices: Codex offers the models its list shows, by their names",
                string.Join(",", codex.Models.Select(model => $"{model.Value}={model.Label}")), "gpt-6-sol=GPT-6-Sol,gpt-5.5=gpt-5.5");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", before);
            DeleteFolder(home);
        }
    }

    /// <summary>A reply goes on in the run's session; a rule that changes things never stays in the read-only list.</summary>
    private static void ContinueAndPermissions()
    {
        var run = new AgentRun(
            new AgentTask
            {
                Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Fix", Access = AgentAccess.Do,
                Images = [@"C:\shot.png"], Model = "opus", Effort = "high", AutoApprove = true
            },
            new WorkspaceBefore(@"C:\Work", true, new Dictionary<string, string>(), null, DateTime.UtcNow),
            new AgentResult { SessionId = "s1", Succeeded = true },
            []);
        var next = AgentCoordinator.ContinueTask(run, "  and the jump too \n");
        Program.Equal("continue: the same session, access, model and effort, the reply as the prompt, no images",
            (next!.ResumeSessionId, next.Prompt, next.Access, next.Model, next.Effort, next.AutoApprove, next.Images.Count),
            ("s1", "and the jump too", AgentAccess.Do, "opus", "high", true, 0));
        Program.Equal("continue: words given while the agent worked say it was stopped for them",
            AgentCoordinator.Interrupted("  use the old jump  ").Contains("stopped you to say this:\nuse the old jump\n"), true);
        Program.Equal("continue: nothing without a reply or a session",
            (AgentCoordinator.ContinueTask(run, "  ") is null, AgentCoordinator.ContinueTask(run with { Result = new AgentResult() }, "go") is null), (true, true));

        Program.Equal("permissions: file tools and a whole shell change things, one command or a read does not",
            string.Join(",", new[] { "Edit", "Write", "PowerShell", "Bash", "Bash(git log:*)", "PowerShell(uv run:*)", "Read", "mcp__blender__get_scene" }
                .Select(rule => AgentPermissions.ChangesThings(rule) ? "c" : "r")),
            "c,c,c,c,r,r,r,r");
        var project = new LinkProject
        {
            Name = "Bigsteps", DataFolder = @"C:\Data", AgentReadTools = ["Edit", "Write", "PowerShell", "Read"], AgentDoTools = ["Write"]
        };
        project.Normalize();
        Program.Equal("permissions: what an older Link let into read only moves to the Do list",
            (string.Join(",", project.AgentReadTools), string.Join(",", project.AgentDoTools)), ("Read", "Write,Edit,PowerShell"));

        var settings = new AgentSettings { ClaudeModel = " opus ", CodexEffort = " ", DefaultAccess = (AgentAccessChoice)9 };
        settings.Normalize();
        Program.Equal("agent settings: trimmed, blank is the CLI's own, an unknown access is read only",
            (settings.ClaudeModel, settings.CodexEffort, settings.DefaultAccess, settings.Clone().ClaudeModel), ("opus", (string?)null, AgentAccessChoice.ReadOnly, "opus"));
    }

    /// <summary>Start over with a note: the looping session is asked read only, and a new session gets the original task and the note.</summary>
    private static async Task StartOverAsync()
    {
        const string Note = "Tried Floor twice; the tests need Assert first.";
        var agent = new ScriptedProvider((task, _) => task.Prompt == AgentCoordinator.NoteRequest
            ? new AgentResult { SessionId = "s1", Succeeded = true, Summary = Note + "\n" }
            : new AgentResult { SessionId = "s2", Succeeded = true });
        var agents = new AgentCoordinator([agent], new AgentChanges(Path.GetTempPath()), _ => { });
        var origin = new AgentTask
        {
            Provider = AgentProviderKind.ClaudeCode,
            Workspace = @"C:\Work\Sample",
            Prompt = "Fix the rounding",
            Images = ["shot.png"],
            Access = AgentAccess.Do,
            AllowedTools = ["Read"],
            CheckCommand = "dotnet test"
        };
        // The task went on after "allow and retry" and looped there.
        var last = origin with { ResumeSessionId = "s1", Prompt = "These tools are allowed now. Continue the task.", Images = [], AllowedTools = ["Read", "Bash(dotnet test)"] };
        var before = new WorkspaceBefore(origin.Workspace, false, new Dictionary<string, string>(), null, DateTime.UtcNow);
        var looped = new AgentRun(last, before, new AgentResult { SessionId = "s1", Loops = ["repeated error"] }, []);

        var fresh = (await agents.StartOverAsync(looped, origin, CancellationToken.None))!.Value;
        var ask = agent.Tasks.Single();
        Program.Equal("start over: the note is asked in the same session, read only, with no tools or images",
            (ask.ResumeSessionId, ask.Access, ask.AllowedTools.Count, ask.Images.Count), ("s1", AgentAccess.Analyze, 0, 0));
        Program.Equal("start over: a new session with the original goal and images, and the note",
            (fresh.Task.ResumeSessionId, fresh.Task.Prompt, fresh.Task.Images.Count, fresh.Note),
            ((string?)null, "Fix the rounding\n\nA note from an earlier attempt at this task, which did not work out:\n" + Note, 1, Note));
        Program.Equal("start over: the access, folder, allowed tools and check of the run go on",
            (fresh.Task.Access, fresh.Task.Workspace, string.Join(",", fresh.Task.AllowedTools), fresh.Task.CheckCommand),
            (AgentAccess.Do, @"C:\Work\Sample", "Read,Bash(dotnet test)", "dotnet test"));

        var stopping = new AgentCoordinator([new ScriptedProvider((_, _) => new AgentResult { Stopped = true })], agents.Changes, _ => { });
        Program.Equal("start over: Stop while the note is asked starts nothing", (await stopping.StartOverAsync(looped, origin, CancellationToken.None)).HasValue, false);
        var unasked = new ScriptedProvider((_, _) => new AgentResult());
        var sessionless = await new AgentCoordinator([unasked], agents.Changes, _ => { })
            .StartOverAsync(looped with { Result = new AgentResult() }, origin, CancellationToken.None);
        Program.Equal("start over: without a session to ask, the task starts over without a note",
            (unasked.Tasks.Count, sessionless?.Task.Prompt, sessionless?.Note), (0, "Fix the rounding", string.Empty));
    }

    private static void ProjectCheck()
    {
        var project = new LinkProject { Name = "Nori", DataFolder = "C:\\Data", CheckCommand = "dotnet build" };
        var snapshot = new InputSnapshot { ProjectId = project.Id, ProjectName = project.Name, Text = "Fix the camera" };
        var edits = AgentCoordinator.BuildTask(new AgentPlan(AgentProviderKind.Codex, "C:\\Work", AgentAccess.Do, false, false, "Fix the camera"), snapshot, project);
        Program.Equal("check: with edits the agent is told that Link runs the check",
            edits.Prompt.EndsWith(AgentCoordinator.CheckNote("dotnet build"), StringComparison.Ordinal) && edits.Prompt.Contains("`dotnet build`"), true);
        var readOnly = AgentCoordinator.BuildTask(new AgentPlan(AgentProviderKind.Codex, "C:\\Work", AgentAccess.Analyze, false, false, "Fix the camera"), snapshot, project);
        Program.Equal("check: read only is told nothing of it", readOnly.Prompt, "Fix the camera");

        var old = JsonSerializer.Deserialize<LinkProject>("""{"Name":"Nori","DataFolder":"C:\\Data"}""")!;
        old.Normalize();
        Program.Equal("project: older JSON loads without a check", old.CheckCommand, null);
        var blank = new LinkProject { Name = "a", DataFolder = "b", CheckCommand = "  " };
        blank.Normalize();
        Program.Equal("project: a blank check is none", blank.CheckCommand, null);
        var padded = new LinkProject { Name = "a", DataFolder = "b", CheckCommand = " dotnet build\n" };
        padded.Normalize();
        Program.Equal("project: the check is kept trimmed", padded.CheckCommand, "dotnet build");
    }

    /// <summary>The check runs exactly as written through cmd.exe, keeps its exit code and its last lines, and ends on time or on Stop.</summary>
    private static async Task CheckCommandAsync()
    {
        var folder = AppContext.BaseDirectory;
        var passed = await AgentCheck.RunAsync("echo \"a b\"&& echo c", folder, TimeSpan.FromSeconds(10), CancellationToken.None);
        Program.Equal("check: quotes and && reach cmd as written", (passed.Outcome, passed.Tail), (AgentCheckOutcome.Passed, "\"a b\"\nc"));
        var failed = await AgentCheck.RunAsync("echo bad 1>&2 & exit /b 3", folder, TimeSpan.FromSeconds(10), CancellationToken.None);
        Program.Equal("check: a failure keeps its exit code and what it wrote to stderr",
            (failed.Outcome, failed.ExitCode, failed.Tail, failed.ForTheAgent), (AgentCheckOutcome.Failed, 3, "bad", true));
        var missing = await AgentCheck.RunAsync("echo x", Path.Combine(folder, "no such folder"), TimeSpan.FromSeconds(10), CancellationToken.None);
        Program.Equal("check: a check that cannot start is not the agent's to fix", (missing.Outcome, missing.ForTheAgent), (AgentCheckOutcome.NotStarted, false));

        var sleep = $"\"{Environment.ProcessPath}\" {FakeFlag} sleep";
        var clock = Stopwatch.StartNew();
        var hung = await AgentCheck.RunAsync(sleep, folder, TimeSpan.FromSeconds(1), CancellationToken.None);
        Program.Equal("check: a check that hangs ends at its time limit", (hung.Outcome, clock.Elapsed < TimeSpan.FromSeconds(8)), (AgentCheckOutcome.TimedOut, true));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopped = await AgentCheck.RunAsync(sleep, folder, TimeSpan.FromMinutes(1), stop.Token);
        Program.Equal("check: Stop ends it", (stopped.Outcome, stopped.ForTheAgent), (AgentCheckOutcome.Stopped, false));
    }

    /// <summary>After Do a red check goes back to the agent's session, twice at most; read only and a plan are not checked.</summary>
    private static async Task CheckRetriesAsync()
    {
        var root = TempFolder();
        var workspace = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(workspace);
            var changes = new AgentChanges(Path.Combine(root, "backups"));
            // The agent fixes the state on its second run; the check looks for it.
            var fixing = new ScriptedProvider((_, number) =>
            {
                File.WriteAllText(Path.Combine(workspace, "state.txt"), number >= 2 ? "fixed" : "broken");
                return new AgentResult { SessionId = "s1", Succeeded = true, Summary = $"run {number}", Stats = new AgentStats { Turns = 3 } };
            });
            var log = new List<string>();
            var task = new AgentTask
            {
                Provider = AgentProviderKind.ClaudeCode,
                Workspace = workspace,
                Prompt = "fix",
                Access = AgentAccess.Do,
                Images = ["shot.png"],
                CheckCommand = "findstr /c:fixed state.txt"
            };
            var run = await new AgentCoordinator([fixing], changes, log.Add).RunAsync(task, false, null, new SyncProgress(_ => { }), CancellationToken.None);
            var retry = fixing.Tasks[^1];
            Program.Equal("check: a red check goes back to the agent's session with the check's end",
                (fixing.Tasks.Count, retry.ResumeSessionId, retry.Prompt.Contains("findstr /c:fixed state.txt` failed with exit code 1"), retry.Images.Count),
                (2, "s1", true, 0));
            Program.Equal("check: green after the fix, with the numbers of both runs", (run.Check?.Passed, run.Result.Stats.Turns, run.Result.Summary), ((bool?)true, 6, "run 2"));
            Program.Equal("check: link.log has how it went, not the command or its output",
                log.Count(line => line.StartsWith("[Agent] check", StringComparison.Ordinal)) == 2 && !log.Any(line => line.Contains("findstr") || line.Contains("broken")),
                true);

            var never = new ScriptedProvider((_, _) => new AgentResult { SessionId = "s2", Succeeded = true });
            var red = await new AgentCoordinator([never], changes, _ => { })
                .RunAsync(task with { CheckCommand = "exit /b 5" }, false, null, new SyncProgress(_ => { }), CancellationToken.None);
            Program.Equal("check: two retries, then the red check ends the task",
                (never.Tasks.Count, red.Check?.Outcome, red.Check?.ExitCode), (3, (AgentCheckOutcome?)AgentCheckOutcome.Failed, (int?)5));

            foreach (var (name, quiet) in new[] { ("read only", task with { Access = AgentAccess.Analyze }), ("a plan", task with { PlanOnly = true }) })
            {
                var agent = new ScriptedProvider((_, _) => new AgentResult { SessionId = "s3", Succeeded = true });
                var none = await new AgentCoordinator([agent], changes, _ => { }).RunAsync(quiet, false, null, new SyncProgress(_ => { }), CancellationToken.None);
                Program.Equal($"check: {name} runs none", (none.Check, agent.Tasks.Count), ((AgentCheckResult?)null, 1));
            }
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    /// <summary>The map: the agent answers read only, Link writes the answer, and it is the run's one change, undone in git.</summary>
    private static async Task MapAsync()
    {
        Program.Equal("map: its task ships beside Link", File.Exists(ProjectMap.TaskFile), true);
        Program.Equal("map: a plain answer is kept as it is", AgentCoordinator.Unfenced("# Map\n\n- src: code\n"), "# Map\n\n- src: code");
        var root = TempFolder();
        var workspace = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(workspace);
            await GitAsync(workspace, "init", "-q");
            File.WriteAllText(Path.Combine(workspace, "a.txt"), "a");
            await GitAsync(workspace, "add", ".");
            await GitAsync(workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "start");
            var agents = new AgentCoordinator([ScriptedProvider.Answering("```markdown\n# Sample\n\n- a.txt: the letter a\n```")],
                new AgentChanges(Path.Combine(root, "backups")), _ => { });
            var task = new AgentTask
            {
                Provider = AgentProviderKind.ClaudeCode,
                Workspace = workspace,
                Prompt = "map",
                AnswerFile = ProjectMap.RelativePath
            };
            var events = new List<AgentEvent>();
            var run = await agents.RunAsync(task, backup: false, previous: null, new SyncProgress(events.Add), CancellationToken.None);
            Program.Equal("map: Link writes the answer without its code block",
                File.ReadAllText(ProjectMap.PathIn(workspace)), "# Sample\n\n- a.txt: the letter a\n");
            Program.Equal("map: the file is the run's one change, new and undoable",
                string.Join(",", run.Changes.Select(change => $"{Path.GetFileName(change.Path)}:{change.Kind}:{change.Undoable}")), "map.md:??:True");
            Program.Equal("map: Activity says Link wrote it", events.Any(e => e.Kind == AgentEventKind.Status && e.Text.StartsWith("Link wrote docs/map.md")), true);
            await agents.Changes.UndoAsync(run.Before, run.Changes, CancellationToken.None);
            Program.Equal("map: undo takes it away", File.Exists(ProjectMap.PathIn(workspace)), false);

            var failed = await new AgentCoordinator([ScriptedProvider.Answering("# Half a map", succeeded: false)], agents.Changes, _ => { })
                .RunAsync(task, backup: false, previous: null, new SyncProgress(_ => { }), CancellationToken.None);
            Program.Equal("map: a failed run writes nothing", (File.Exists(ProjectMap.PathIn(workspace)), failed.Changes.Count), (false, 0));
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    /// <summary>Save as skill: the session answers read only, Link writes the SKILL.md under its name, and only a good name, and a replace the user allowed.</summary>
    private static async Task SkillAsync()
    {
        Program.Equal("skill: its task ships beside Link", File.Exists(ProjectSkills.TaskFile), true);
        var root = TempFolder();
        var workspace = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(workspace);
            await GitAsync(workspace, "init", "-q");
            File.WriteAllText(Path.Combine(workspace, "a.txt"), "a");
            await GitAsync(workspace, "add", ".");
            await GitAsync(workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "start");
            var work = new AgentRun(
                new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "Check the game", Access = AgentAccess.Do, CheckCommand = "check" },
                new WorkspaceBefore(workspace, true, new Dictionary<string, string>(), null, DateTime.UtcNow),
                new AgentResult { SessionId = "work", Succeeded = true },
                []);
            var task = AgentCoordinator.SkillTask(work, "Save it as a skill.\n")!;
            Program.Equal("skill: the work's session is asked, read only, without its check",
                (task.ResumeSessionId, task.Access, task.AnswerIsSkill, task.CheckCommand, task.Prompt),
                ("work", AgentAccess.Analyze, true, (string?)null, "Save it as a skill.\n\nSkills of the workspace: none yet."));

            const string answer = "---\nname: game-check\ndescription: Check the game in a browser.\n---\n\n1. Run tools/check.ps1.";
            var changes = new AgentChanges(Path.Combine(root, "backups"));
            var events = new List<AgentEvent>();
            var run = await new AgentCoordinator([ScriptedProvider.Answering("```markdown\n" + answer + "\n```")], changes, _ => { })
                .RunAsync(task, false, null, new SyncProgress(events.Add), CancellationToken.None);
            var file = Path.Combine(ProjectSkills.FolderIn(workspace), "game-check", "SKILL.md");
            Program.Equal("skill: Link writes it under its name, the run's one change",
                (File.ReadAllText(file), string.Join(",", run.Changes.Select(change => Path.GetFileName(change.Path)))), (answer + "\n", "SKILL.md"));
            Program.Equal("skill: the next ask names the skills there are", AgentCoordinator.SkillTask(work, "Save")!.Prompt.EndsWith("workspace: game-check."), true);

            var asked = new List<string>();
            var kept = await new AgentCoordinator([ScriptedProvider.Answering(answer.Replace("tools/check.ps1", "other"))], changes, _ => { })
                .RunAsync(task, false, null, new SyncProgress(_ => { }), CancellationToken.None, name =>
                {
                    asked.Add(name);
                    return false;
                });
            Program.Equal("skill: one of the same name stays unless the user says replace",
                (string.Join(",", asked), File.ReadAllText(file).Contains("other"), kept.Changes.Count(change => change.Path == file)), ("game-check", false, 0));
            await new AgentCoordinator([ScriptedProvider.Answering(answer.Replace("tools/check.ps1", "other"))], changes, _ => { })
                .RunAsync(task, false, null, new SyncProgress(_ => { }), CancellationToken.None, _ => true);
            Program.Equal("skill: replaced when the user says so", File.ReadAllText(file).Contains("other"), true);

            var badEvents = new List<AgentEvent>();
            var bad = await new AgentCoordinator([ScriptedProvider.Answering(answer.Replace("game-check", "../Game Check"))], changes, _ => { })
                .RunAsync(task, false, null, new SyncProgress(badEvents.Add), CancellationToken.None, _ => true);
            Program.Equal("skill: a bad name writes nothing and says why",
                (Directory.GetDirectories(ProjectSkills.FolderIn(workspace)).Length, badEvents.Any(e => e.Kind == AgentEventKind.Error && e.Text.Contains("../Game Check"))), (1, true));
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    private static async Task GitUndoAsync()
    {
        var workspace = TempFolder();
        try
        {
            await GitAsync(workspace, "init", "-q");
            File.WriteAllText(Path.Combine(workspace, "clean.txt"), "clean");
            File.WriteAllText(Path.Combine(workspace, "mine.txt"), "mine");
            await GitAsync(workspace, "add", ".");
            await GitAsync(workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "start");
            File.WriteAllText(Path.Combine(workspace, "mine.txt"), "my edit");
            var changes = new AgentChanges(Path.Combine(workspace, "backups"));
            var before = await changes.CaptureAsync(workspace, backup: true, CancellationToken.None);
            Program.Equal("git: a repository needs no copy", (before.IsGit, before.Backup), (true, (string?)null));

            // The agent's work: one clean file changed, one new, and the user's own edit changed further.
            File.WriteAllText(Path.Combine(workspace, "clean.txt"), "agent");
            File.WriteAllText(Path.Combine(workspace, "new.txt"), "agent");
            File.WriteAllText(Path.Combine(workspace, "mine.txt"), "my edit and the agent's");
            File.SetLastWriteTimeUtc(Path.Combine(workspace, "mine.txt"), DateTime.UtcNow.AddMinutes(1));
            var found = await changes.CompareAsync(before, CancellationToken.None);
            Program.Equal("git: the agent's changes are found",
                string.Join(",", found.OrderBy(change => change.Path).Select(change => $"{Path.GetFileName(change.Path)}:{change.Undoable}")),
                "clean.txt:True,mine.txt:False,new.txt:True");

            await changes.UndoAsync(before, found, CancellationToken.None);
            Program.Equal("git: undo puts back a clean file", File.ReadAllText(Path.Combine(workspace, "clean.txt")), "clean");
            Program.Equal("git: undo deletes a new file", File.Exists(Path.Combine(workspace, "new.txt")), false);
            Program.Equal("git: undo keeps a file the user had changed", File.ReadAllText(Path.Combine(workspace, "mine.txt")), "my edit and the agent's");
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    private static async Task CopyRestoreAsync()
    {
        var root = TempFolder();
        var workspace = Path.Combine(root, "scene");
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "bin"));
            File.WriteAllText(Path.Combine(workspace, "scene.blend"), "land v1");
            File.WriteAllText(Path.Combine(workspace, "bin", "skipped.dll"), "build output");
            var changes = new AgentChanges(Path.Combine(root, "backups"));
            var before = await changes.CaptureAsync(workspace, backup: true, CancellationToken.None);
            Program.Equal("copy: a folder without git is copied before Do", before is { IsGit: false, Backup: not null }, true);
            Program.Equal("copy: build output is not copied", File.Exists(Path.Combine(before.Backup!, "bin", "skipped.dll")), false);

            File.WriteAllText(Path.Combine(workspace, "scene.blend"), "land v2");
            File.WriteAllText(Path.Combine(workspace, "land.fbx"), "export");
            var found = await changes.CompareAsync(before, CancellationToken.None);
            Program.Equal("copy: changed and added files are found",
                string.Join(",", found.Select(change => Path.GetFileName(change.Path)).Order()), "land.fbx,scene.blend");
            await changes.RestoreAsync(before, found, CancellationToken.None);
            Program.Equal("copy: restore brings the file back", File.ReadAllText(Path.Combine(workspace, "scene.blend")), "land v1");
            Program.Equal("copy: restore deletes what the agent added", File.Exists(Path.Combine(workspace, "land.fbx")), false);
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "link-agent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteFolder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true })!;
        await git.WaitForExitAsync();
    }

    private static void ClaudeFixtures()
    {
        var read = Parse(new ClaudeEventParser(), "claude-read.jsonl");
        Program.Equal("Claude: the session id comes from init", read.Parser.SessionId, "5f0c7a2e-1b3d-4c8e-9a61-0d2f4e6b8c10");
        Program.Equal("Claude: init names the model and MCP servers",
            read.Events[0].Text, "Model claude-sonnet-5-5; MCP: blender (connected)");
        Program.Equal("Claude: a read is a tool step", read.Events.Count(e => e.Kind == AgentEventKind.Tool), 1);
        Program.Equal("Claude: a read changes nothing", read.Parser.ChangedFiles.Count, 0);
        Program.Equal("Claude: the answer is the summary", read.Parser.Summary.StartsWith("VoiceSession listens"), true);
        Program.Equal("Claude: success completes", read.Events[^1].Kind, AgentEventKind.Completed);
        Program.Equal("Claude: junk and unknown events are skipped", read.Events.Count, 4);

        var denied = Parse(new ClaudeEventParser(), "claude-denied.jsonl");
        Program.Equal("Claude: only an edit that succeeded is a change",
            string.Join(";", denied.Parser.ChangedFiles), @"C:\Work\Sample\src\Round.cs");
        Program.Equal("Claude: a refused command is allowed by its own rule",
            string.Join(";", denied.Parser.Denials), "Bash(dotnet test)");
        Program.Equal("Claude: a refusal asks the user",
            denied.Events.Count(e => e.Kind == AgentEventKind.NeedsInput), 1);
        Program.Equal("Claude: a run with refusals still completes", denied.Parser.Failed, false);
    }

    private static void CodexFixtures()
    {
        var read = Parse(new CodexEventParser(), "codex-read.jsonl");
        Program.Equal("Codex: the thread id is the session", read.Parser.SessionId, "01a0f0b4-da92-79a3-92ed-1ae382b67719");
        Program.Equal("Codex: a command shows itself, not its PowerShell wrapper",
            read.Events.First(e => e.Kind == AgentEventKind.Tool).Text.StartsWith("git status --short;"), true);
        Program.Equal("Codex: the last message is the summary", read.Parser.Summary.Contains("81 lines"), true);
        Program.Equal("Codex: a turn completes", read.Events[^1].Kind, AgentEventKind.Completed);
        Program.Equal("Codex: reading changes nothing", read.Parser.ChangedFiles.Count, 0);

        var write = Parse(new CodexEventParser(), "codex-write.jsonl");
        Program.Equal("Codex: a file change is an artifact",
            string.Join(";", write.Parser.ChangedFiles), @"C:\Work\Sample\notes\agent-check.txt");

        var error = Parse(new CodexEventParser(), "codex-error.jsonl");
        Program.Equal("Codex: a failed turn fails the run", error.Parser.Failed, true);
        Program.Equal("Codex: the error shows the API's own message",
            error.Events.First(e => e.Kind == AgentEventKind.Error).Text,
            "The 'no-such-model-xyz' model is not supported when using Codex with a ChatGPT account.");

        // Recorded 30.09: three reconnects in the middle of a task that then completed.
        var reconnect = Parse(new CodexEventParser(), "codex-reconnect.jsonl");
        Program.Equal("Codex: a reconnect on the way does not fail a turn that completes",
            (reconnect.Parser.Failed, reconnect.Events.Count(e => e.Kind == AgentEventKind.Error), reconnect.Events.Count(e => e.Kind == AgentEventKind.Status && e.Text.StartsWith("Reconnecting"))),
            (false, 0, 2));
        Program.Equal("Codex: an error with no completed turn still fails the run",
            Parse(new CodexEventParser(), "codex-error.jsonl").Parser.Failed, true);

        var refused = new CodexEventParser();
        refused.ReadError("2026-09-30T05:14:25Z ERROR codex_core::tools::router: error=patch rejected: writing is blocked by read-only sandbox; rejected by user approval settings");
        Program.Equal("Codex: a write the read-only sandbox blocked offers Do",
            string.Join(";", refused.Denials), "workspace-write");
    }

    /// <summary>A task's numbers from its events: the Do fixtures follow the recorded format with searches, a failed build and two edits.</summary>
    private static void Stats()
    {
        var claude = Parse(new ClaudeEventParser(), "claude-do.jsonl").Parser.Stats;
        Program.Equal("Claude stats: turns, calls, searches, searches before the first change, failed calls",
            (claude.Turns, claude.ToolCalls, claude.Searches, claude.SearchesBeforeChange, claude.ToolErrors), (10, 9, 5, (int?)4, 1));
        Program.Equal("Claude stats: all the input with the cache, the output and the cost come from the result",
            (claude.InputTokens, claude.CachedTokens, claude.OutputTokens, claude.CostUsd),
            ((long?)157371, (long?)148230, (long?)2105, (decimal?)0.1834m));
        var read = Parse(new ClaudeEventParser(), "claude-read.jsonl").Parser.Stats;
        Program.Equal("Claude stats: a run that changed nothing has no searches before a change",
            (read.Searches, read.SearchesBeforeChange, read.InputTokens), (1, (int?)null, (long?)12));
        Program.Equal("Claude stats: a failed edit and a refused command are tool errors",
            Parse(new ClaudeEventParser(), "claude-denied.jsonl").Parser.Stats.ToolErrors, 2);
        Program.Equal("link.log: the numbers whole, in one order",
            CliAgentProvider.LogStats(claude),
            "turns 10, tools 9, searches 5 (4 before a change), tool errors 1, tokens in 157371 (cached 148230) out 2105, cost $0.1834");

        var codex = Parse(new CodexEventParser(), "codex-do.jsonl").Parser.Stats;
        Program.Equal("Codex stats: commands and patches are calls, a search is told by its program",
            (codex.Turns, codex.ToolCalls, codex.Searches, codex.SearchesBeforeChange, codex.ToolErrors), (1, 7, 3, (int?)2, 1));
        Program.Equal("Codex stats: tokens come from the turn, the cost is not reported",
            (codex.InputTokens, codex.CachedTokens, codex.OutputTokens, codex.CostUsd),
            ((long?)182340, (long?)160512, (long?)3120, (decimal?)null));
        Program.Equal("Codex stats: git status, even with a read after it, is not a search",
            Parse(new CodexEventParser(), "codex-read.jsonl").Parser.Stats.Searches, 0);
        var resumed = Parse(new CodexEventParser(resumed: true), "codex-do.jsonl").Parser.Stats;
        Program.Equal("Codex stats: a resumed run reports the session's tokens and says so",
            (resumed.SessionTokens, CliAgentProvider.LogStats(resumed).EndsWith("out 3120 for the session", StringComparison.Ordinal)), (true, true));
        var first = new AgentStats { InputTokens = 100, CachedTokens = 50, OutputTokens = 10 };
        Program.Equal("stats: a continuation that counts its own run adds up",
            first.Add(new AgentStats { InputTokens = 40, CachedTokens = 30, OutputTokens = 5 }).InputTokens, (long?)140);
        var session = first.Add(new AgentStats { InputTokens = 180, CachedTokens = 120, OutputTokens = 20, SessionTokens = true });
        Program.Equal("stats: a continuation that counts the session already holds the first run",
            (session.InputTokens, session.OutputTokens, session.SessionTokens), ((long?)180, (long?)20, true));

        foreach (var (command, search) in new[]
                 {
                     ("rg -n Cents src", true),
                     ("cd C:/Work/Sample && grep -rn Cents .", true),
                     ("Get-ChildItem -Recurse -Filter *.cs | Select-Object FullName", true),
                     (@"& 'C:\Tools\rg.exe' Cents", true),
                     ("git grep -n Cents", true),
                     ("dotnet build 2>&1 | Select-String error", false),
                     ("git status --short; Get-Content a.cs", false),
                     ("npm test", false),
                     ("", false)
                 })
        {
            Program.Equal($"search command: {command}", AgentEventParser.IsSearchCommand(command), search);
        }
    }

    /// <summary>The loop check on runs in the recorded format: each kind of loop is told once, working runs get none.</summary>
    private static void Loops()
    {
        Program.Equal("loops: a working Claude Do run has none", LoopsIn(new ClaudeEventParser(), "claude-do.jsonl", canChange: true).Count, 0);
        Program.Equal("loops: nor a working Codex one", LoopsIn(new CodexEventParser(), "codex-do.jsonl", canChange: true).Count, 0);

        var claude = LoopsIn(new ClaudeEventParser(), "claude-loop.jsonl", canChange: true);
        Program.Equal("loops: the same read three times, then the same test error three times in a row",
            string.Join(", ", claude.Select(loop => loop.Detail)), "repeated call, repeated error");
        Program.Equal("loops: the label names the call", claude[0].Text, @"Looks like a loop: the same call 3 times: Read: C:\Work\Sample\src\Round.cs");
        Program.Equal("loops: the label shows the error's telling line, not its exit code",
            claude[1].Text.StartsWith(@"Looks like a loop: the same error 3 times in a row: C:\Work\Sample\tests\RoundTests.cs(9,9): error CS0103", StringComparison.Ordinal), true);

        var codex = LoopsIn(new CodexEventParser(), "codex-loop.jsonl", canChange: true);
        Program.Equal("loops: a dozen searches without a change are one; six edits of one file after them are none",
            string.Join(" | ", codex.Select(loop => loop.Text)),
            "Looks like a loop: 12 searches in a row without a change");
        Program.Equal("loops: a read-only run is not told off for searching",
            LoopsIn(new CodexEventParser(), "codex-loop.jsonl", canChange: false).Any(loop => loop.Detail == "searching"), false);
    }

    private static List<AgentEvent> LoopsIn(AgentEventParser parser, string fixture, bool canChange)
    {
        var detector = new AgentLoopDetector(canChange);
        return Parse(parser, fixture).Events.Select(detector.Add).OfType<AgentEvent>().ToList();
    }

    /// <summary>A recorded loop played by the fake CLI: the label reaches Activity in its place, the kinds reach the result and the [Agent] line.</summary>
    private static async Task LoopRunAsync()
    {
        var log = new List<string>();
        var events = new List<AgentEvent>();
        var result = await new FakeProvider("claude-loop.jsonl", new AgentSettings { ClaudePath = Environment.ProcessPath }, log.Add)
            .RunAsync(Task() with { Access = AgentAccess.Do }, new SyncProgress(events.Add), CancellationToken.None);
        var label = events.FindIndex(e => e.Kind == AgentEventKind.Loop);
        Program.Equal("loop run: the label comes right after the third identical read",
            label > 0 && events[label - 1] is { Kind: AgentEventKind.Tool, Detail: "Read" } && events.Count(e => e.Kind == AgentEventKind.Loop) == 2, true);
        Program.Equal("loop run: the kinds are in the result", string.Join(", ", result.Loops), "repeated call, repeated error");
        Program.Equal("loop run: the [Agent] line names the kinds, not the file",
            log.Count == 1 && log[0].EndsWith("; looks like a loop: repeated call, repeated error", StringComparison.Ordinal) && !log[0].Contains("Round.cs"), true);
    }

    private static void Arguments()
    {
        var settings = new AgentSettings();
        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var codex = new CodexProvider(() => settings, _ => { });
        var analyze = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Explain" };
        var line = string.Join(' ', claude.Arguments(analyze));
        Program.Equal("Claude Analyze runs in dontAsk", line.Contains("--permission-mode dontAsk"), true);
        Program.Equal("Claude never bypasses permissions",
            new[] { analyze, analyze with { Access = AgentAccess.Do }, analyze with { PlanOnly = true } }
                .Any(task => string.Join(' ', claude.Arguments(task)).Contains("bypass", StringComparison.OrdinalIgnoreCase)),
            false);
        var doTask = analyze with { Access = AgentAccess.Do, ResumeSessionId = "s1", AllowedTools = ["Bash(dotnet test)"] };
        Program.Equal("Claude Do accepts edits, resumes and ends with the allowed tools",
            string.Join(' ', claude.Arguments(doTask)).EndsWith("--resume s1 --allowedTools Bash(dotnet test)") &&
            claude.Arguments(doTask).Contains("acceptEdits"),
            true);
        Program.Equal("Claude plan-first runs in plan mode even with Do",
            claude.Arguments(doTask with { PlanOnly = true }).Contains("plan"), true);

        Program.Equal("Codex Analyze is read-only",
            string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex })).Contains("--sandbox read-only"), true);
        Program.Equal("Codex Do writes only the workspace",
            string.Join(' ', codex.Arguments(analyze with { Access = AgentAccess.Do })).Contains("--sandbox workspace-write"), true);
        var featuresOff = string.Join(' ', CodexProvider.FeaturesOff.Select(feature => $"-c features.{feature}=false"));
        Program.Equal("Codex resume sets the sandbox through config and keeps the desktop app's extras off",
            string.Join(' ', codex.Arguments(analyze with { ResumeSessionId = "t1" })),
            $"exec resume t1 --json --skip-git-repo-check -c sandbox_mode=read-only {featuresOff} -");
        Program.Equal("Codex: a new session runs without the desktop app's plugins, computer use and subagents",
            string.Join(' ', codex.Arguments(analyze)).Contains(featuresOff) && featuresOff.Contains("features.plugins=false") &&
            featuresOff.Contains("features.computer_use=false") && featuresOff.Contains("features.multi_agent=false"), true);
        Program.Equal("Codex plan-first stays read-only",
            codex.Arguments(analyze with { Access = AgentAccess.Do, PlanOnly = true }).Contains("read-only"), true);

        // Model, effort, auto and subagents.
        var chosen = analyze with { Model = "claude-opus-5-5", Effort = "high" };
        Program.Equal("Claude takes the model and effort, and runs without subagents",
            string.Join(' ', claude.Arguments(chosen)).Contains("--model claude-opus-5-5 --effort high --disallowedTools Agent Task"), true);
        Program.Equal("Claude without a model or effort leaves them to its own settings",
            claude.Arguments(analyze).Any(argument => argument is "--model" or "--effort"), false);
        Program.Equal("Claude read only denies the file tools whatever the project allows",
            string.Join(' ', claude.Arguments(analyze)).Contains("--disallowedTools Agent Task Edit Write NotebookEdit") &&
            !string.Join(' ', claude.Arguments(analyze with { Access = AgentAccess.Do })).Contains("Write"), true);
        Program.Equal("Claude auto runs in its auto mode, only with edits",
            (claude.Arguments(analyze with { Access = AgentAccess.Do, AutoApprove = true }).Contains("auto"),
                claude.Arguments(analyze with { AutoApprove = true }).Contains("auto")), (true, false));
        var codexChosen = string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex, Model = "gpt-6-sol", Effort = "xhigh", ResumeSessionId = "t1" }));
        Program.Equal("Codex takes the model and effort through config, on resume too",
            codexChosen.Contains("-c model=gpt-6-sol -c model_reasoning_effort=xhigh"), true);
        Program.Equal("Codex auto sends what would leave the sandbox to its automatic review, only with edits",
            (string.Join(' ', codex.Arguments(analyze with { Access = AgentAccess.Do, AutoApprove = true })).Contains("-c approval_policy=on-request -c approvals_reviewer=auto_review"),
                string.Join(' ', codex.Arguments(analyze with { AutoApprove = true })).Contains("approval")), (true, false));
        Rules(claude, codex);
        Program.Equal("cmd.exe quoting leaves a plain flag alone and quotes a path with spaces",
            AgentProcess.QuoteForCmd("--json") + " " + AgentProcess.QuoteForCmd(@"C:\My Work"), "--json \"C:\\My Work\"");
    }

    /// <summary>Every new session starts with the rules; Claude gets the workspace's skills listed, Codex finds them itself.</summary>
    private static void Rules(ClaudeCodeProvider claude, CodexProvider codex)
    {
        Program.Equal("rules: they ship beside Link", File.Exists(AgentRules.File), true);
        var workspace = TempFolder();
        try
        {
            var skill = Path.Combine(ProjectSkills.FolderIn(workspace), "browser-check");
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: browser-check\ndescription: >\n  Check the game\n  in a browser.\n---\n\n# Steps\n");
            Directory.CreateDirectory(Path.Combine(ProjectSkills.FolderIn(workspace), "empty"));
            Program.Equal("skills: a folder without SKILL.md is not one, a folded description is one line",
                string.Join("|", ProjectSkills.In(workspace).Select(s => $"{s.Name}: {s.Description}")), "browser-check: Check the game in a browser.");

            var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "Fix the camera" };
            var input = claude.StandardInput(task);
            Program.Equal("rules: a new session gets them before the task, without the editor's note",
                input.StartsWith("# ", StringComparison.Ordinal) && input.EndsWith("\n\n# Task\n\nFix the camera", StringComparison.Ordinal) && !input.Contains("<!--"), true);
            Program.Equal("rules: Claude gets the workspace's skills listed",
                input.Contains("- `.agents/skills/browser-check/SKILL.md`: Check the game in a browser."), true);
            Program.Equal("rules: Codex finds the skills itself", codex.StandardInput(task).Contains("browser-check"), false);
            Program.Equal("rules: a resumed session has them already", claude.StandardInput(task with { ResumeSessionId = "s1" }), "Fix the camera");
        }
        finally
        {
            DeleteFolder(workspace);
        }

        var repository = Path.Combine(TempFolder(), "it's a repo");
        try
        {
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            Directory.CreateDirectory(Path.Combine(repository, "game"));
            var inRepository = codex.EnvironmentVariables(new AgentTask { Provider = AgentProviderKind.Codex, Workspace = Path.Combine(repository, "game"), Prompt = "x" });
            Program.Equal("Codex: git in its sandbox takes the workspace's repository as safe, a quote in the path escaped",
                inRepository.TryGetValue("GIT_CONFIG_PARAMETERS", out var parameters) &&
                parameters.EndsWith("'safe.directory=" + repository.Replace('\\', '/').Replace("'", @"'\''") + "'", StringComparison.Ordinal), true);
            Program.Equal("Codex: a folder without git gets no git settings",
                codex.EnvironmentVariables(new AgentTask { Provider = AgentProviderKind.Codex, Workspace = Path.GetTempPath(), Prompt = "x" }).Count, 0);
            Program.Equal("Claude runs as the user and needs no git settings",
                claude.EnvironmentVariables(new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = repository, Prompt = "x" }).Count, 0);
        }
        finally
        {
            DeleteFolder(Path.GetDirectoryName(repository)!);
        }

        Program.Equal("skills: names as both agents accept them",
            string.Join(",", new[] { "browser-check", "a1", "Browser", "a--b", "-a", "a_b", "" }.Select(ProjectSkills.IsName)),
            "True,True,False,False,False,False,False");
    }

    private static async Task StreamAsync()
    {
        var log = new List<string>();
        var events = new List<AgentEvent>();
        var result = await new FakeProvider("stream", new AgentSettings { ClaudePath = Environment.ProcessPath }, log.Add)
            .RunAsync(Task(), new SyncProgress(events.Add), CancellationToken.None);
        Program.Equal("fake CLI: events arrive in order, junk skipped",
            string.Join(",", events.Select(e => e.Kind)), "Status,Tool,Output,Completed");
        Program.Equal("fake CLI: a clean exit succeeds", result.Succeeded, true);
        Program.Equal("fake CLI: the session id is kept", result.SessionId, "fake-session");
        Program.Equal("fake CLI: the run's numbers are in the result", (result.Stats.Turns, result.Stats.ToolCalls, result.Stats.Searches), (1, 1, 1));
        Program.Equal("the [Agent] line has the numbers and not the task",
            log.Count == 1 && log[0].Contains("turns 1, tools 1, searches 1") && !log[0].Contains("fake task"), true);
    }

    private static async Task FailureAsync()
    {
        var (result, events) = await RunFakeAsync("fail");
        Program.Equal("fake CLI: a non-zero exit fails", (result.Succeeded, result.ExitCode), (false, 3));
        Program.Equal("fake CLI: a broken-off line is skipped", events.Count, 1);
        Program.Equal("fake CLI: stderr explains a failure without a result", result.Summary, "fake failure on stderr");
    }

    private static async Task StopAsync()
    {
        using var stop = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        var progress = new SyncProgress(agentEvent =>
        {
            events.Add(agentEvent);
            if (agentEvent.Kind == AgentEventKind.Output) stop.Cancel();
        });
        var clock = Stopwatch.StartNew();
        var result = await Provider("hang").RunAsync(Task(), progress, stop.Token);
        Program.Equal("Stop ends a stalled agent at once", clock.Elapsed < TimeSpan.FromSeconds(5), true);
        Program.Equal("a stopped run says so", (result.Stopped, result.Succeeded), (true, false));
        await ChildEndedAsync("Stop ends the agent's child process too", events);
    }

    private static async Task LeftoversAsync()
    {
        var (_, events) = await RunFakeAsync("linger");
        await ChildEndedAsync("a child the agent left running ends with the run", events);
    }

    private static async Task ChildEndedAsync(string name, IReadOnlyList<AgentEvent> events)
    {
        var pid = events.Where(e => e.Kind == AgentEventKind.Output && e.Text.StartsWith("child ", StringComparison.Ordinal))
            .Select(e => int.Parse(e.Text[6..]))
            .FirstOrDefault();
        await Program.EventuallyAsync(name, () => pid != 0 && !IsRunning(pid), true);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<(AgentResult Result, List<AgentEvent> Events)> RunFakeAsync(string scenario)
    {
        var events = new List<AgentEvent>();
        var result = await Provider(scenario).RunAsync(Task(), new SyncProgress(events.Add), CancellationToken.None);
        return (result, events);
    }

    private static AgentTask Task() =>
        new() { Provider = AgentProviderKind.ClaudeCode, Workspace = AppContext.BaseDirectory, Prompt = "fake task" };

    private static FakeProvider Provider(string scenario) =>
        new(scenario, new AgentSettings { ClaudePath = Environment.ProcessPath }, _ => { });

    private static (AgentEventParser Parser, List<AgentEvent> Events) Parse(AgentEventParser parser, string fixture)
    {
        var events = new List<AgentEvent>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))) events.AddRange(parser.Read(line));
        return (parser, events);
    }

    /// <summary>Claude Code's parser on a fake CLI: this program, which plays a scenario instead of running tests.</summary>
    private sealed class FakeProvider(string scenario, AgentSettings settings, Action<string> log) : CliAgentProvider(() => settings, log)
    {
        public override AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
        public override string Name => "Fake";
        public override IReadOnlyList<AgentChoice> Efforts => [];
        public override IReadOnlyList<AgentChoice> Models => [];
        protected override IReadOnlyList<string> SignInCheck => [];
        public override IReadOnlyList<string> Arguments(AgentTask task) => [FakeFlag, scenario];
        public override AgentEventParser CreateParser(AgentTask task) => new ClaudeEventParser();
        protected override bool IsSignedIn(string answer) => true;
    }

    /// <summary>An agent whose runs do what the check says, for what Link does around a run; it keeps the tasks it got.</summary>
    private sealed class ScriptedProvider(Func<AgentTask, int, AgentResult> run) : IAgentProvider
    {
        public List<AgentTask> Tasks { get; } = [];
        public AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
        public string Name => "Scripted";
        public IReadOnlyList<AgentChoice> Efforts => [];
        public IReadOnlyList<AgentChoice> Models => [];

        public static ScriptedProvider Answering(string answer, bool succeeded = true) =>
            new((_, _) => new AgentResult { SessionId = "answer", Summary = answer, Succeeded = succeeded });

        public Task<AgentProviderStatus> CheckAsync(string? chosenPath, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new AgentProviderStatus(null, null, null));

        public Task<AgentResult> RunAsync(AgentTask task, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
        {
            Tasks.Add(task);
            return System.Threading.Tasks.Task.FromResult(run(task, Tasks.Count));
        }
    }

    /// <summary>Reports on the reporting thread: the checks need no dispatcher.</summary>
    private sealed class SyncProgress(Action<AgentEvent> report) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value)
        {
            lock (this) report(value);
        }
    }

    /// <summary>The fake agent: prints Claude-style events for a scenario; "sleep" is the child it starts.</summary>
    public static int Fake(string scenario)
    {
        switch (scenario)
        {
            case "sleep":
                Thread.Sleep(TimeSpan.FromMinutes(2));
                return 0;
            case "stream":
                Console.In.ReadToEnd();
                Say(new { type = "system", subtype = "init", session_id = "fake-session", model = "fake" });
                Console.WriteLine("garbage that is not JSON");
                Thread.Sleep(50);
                Say(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", id = "t1", name = "Read", input = new { file_path = "a.cs" } } } } });
                Thread.Sleep(50);
                Say(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = "done" } } } });
                Say(new { type = "result", subtype = "success", is_error = false, result = "done", session_id = "fake-session", num_turns = 1 });
                return 0;
            case "fail":
                Console.In.ReadToEnd();
                Say(new { type = "system", subtype = "init", session_id = "fake-session" });
                Console.Out.Write("{\"type\":\"assistant\",\"message\":{\"con");
                Console.Out.Flush();
                Console.Error.WriteLine("fake failure on stderr");
                return 3;
            case "hang":
            case "linger":
                var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [FakeFlag, "sleep"]) { UseShellExecute = false, CreateNoWindow = true })!;
                Say(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = $"child {child.Id}" } } } });
                if (scenario == "hang") Thread.Sleep(TimeSpan.FromMinutes(2));
                return 0;
            case not null when scenario.EndsWith(".jsonl", StringComparison.Ordinal):
                // A recorded run played back as the CLI would write it.
                Console.In.ReadToEnd();
                foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", scenario))) Console.WriteLine(line);
                return 0;
            default:
                return 2;
        }
    }

    private static void Say(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }
}
