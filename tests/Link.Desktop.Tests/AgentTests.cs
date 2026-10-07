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
        ContextMeter();
        ContextBudget();
        Loops();
        Arguments();
        HandOff();
        await StreamAsync();
        await ConsoleAsync();
        await LoopRunAsync();
        await FailureAsync();
        await StopAsync();
        await LeftoversAsync();
        await GitUndoAsync();
        P4Status();
        Questions();
        Images();
        if (Environment.GetEnvironmentVariable("LINK_TEST_P4_WORKSPACE") is { Length: > 0 } p4Workspace)
        {
            await P4WorkspaceAsync(p4Workspace);
        }
        await CopyRestoreAsync();
        await MapAsync();
        await SkillAsync();
        ProjectCheck();
        await CheckCommandAsync();
        await CheckRetriesAsync();
        await StartOverAsync();
        ContinueAndPermissions();
        Choices();
        NextSteps();
        AutoEffortPolicy();
        AutoEffortFailures();
        AutoEffortSaved();
        RolesRead();
        RoleSuggest();
        RoleSession();
        RoleSkills();
        RolesSaved();
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
        Program.Equal("agent settings: trimmed, blank is the CLI's own, an unknown access is auto",
            (settings.ClaudeModel, settings.CodexEffort, settings.DefaultAccess, settings.Clone().ClaudeModel), ("opus", (string?)null, AgentAccessChoice.Auto, "opus"));
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

        var fresh = (await agents.StartOverAsync(looped, origin, (null, null), CancellationToken.None))!.Value;
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
        Program.Equal("start over: Stop while the note is asked starts nothing", (await stopping.StartOverAsync(looped, origin, (null, null), CancellationToken.None)).HasValue, false);
        var unasked = new ScriptedProvider((_, _) => new AgentResult());
        var sessionless = await new AgentCoordinator([unasked], agents.Changes, _ => { })
            .StartOverAsync(looped with { Result = new AgentResult() }, origin, (null, null), CancellationToken.None);
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

    private static void P4Status()
    {
        var root = @"C:\Work\BigSteps";
        var opened = $"""
            ... depotFile //BigSteps/main/Content/Hero.uasset
            ... clientFile {root}\Content\Hero.uasset
            ... action edit

            ... depotFile //BigSteps/main/Config/Old.ini
            ... clientFile {root}\Config\Old.ini
            ... action delete

            """;
        var reconcile = $"""
            ... depotFile //BigSteps/main/Content/Hero.uasset
            ... clientFile {root}\Content\Hero.uasset
            ... action edit

            ... depotFile //BigSteps/main/Notes.txt
            ... clientFile {root}\Notes.txt
            ... action add

            """;
        var status = AgentChanges.ParseP4Status(opened, reconcile)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{Path.GetFileName(entry.Path)}:{entry.Code}");
        Program.Equal("p4: opened and reconcile preview become one status",
            string.Join(',', status), "Old.ini:D,Hero.uasset:M,Notes.txt:??");
        Program.Equal("p4: a Perforce workspace is versioned",
            new WorkspaceBefore(root, false, new Dictionary<string, string>(), null, DateTime.UtcNow, true).IsVersioned, true);
        var described = AgentChanges.SplitChangelistLine("Сделал переход из приземления в бег.\n\n**Changelist:** Nori blends from landing into the run, no foot sliding\n");
        Program.Equal("p4: the agent's English line becomes the changelist's description and leaves the answer",
            described, ("Сделал переход из приземления в бег.", (string?)"Nori blends from landing into the run, no foot sliding"));
        Program.Equal("p4: an answer without the line keeps itself and has no description",
            AgentChanges.SplitChangelistLine("Done."), ("Done.", (string?)null));
        Program.Equal("p4: an expired ticket or none is no workspace status, a file not opened is",
            (AgentChanges.SignedOut("Your session has expired, please login again."),
                AgentChanges.SignedOut("Perforce password (P4PASSWD) invalid or unset."),
                AgentChanges.SignedOut("//BigSteps/main/... - file(s) not opened on this client.")), (true, true, false));

        // In a workspace with a .p4config: the agent may open files with p4 only with edits, Codex reaches the server.
        var workspace = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".p4config"), "P4CLIENT=test");
            var nested = Directory.CreateDirectory(Path.Combine(workspace, "Content")).FullName;
            Program.Equal("p4: a folder below a .p4config is in Perforce, one elsewhere is not",
                (AgentChanges.IsPerforce(nested), AgentChanges.IsPerforce(Path.GetTempPath())), (true, false));
            var project = new LinkProject { Name = "Game", DataFolder = workspace, AgentReadTools = ["Read"] };
            Program.Equal("p4: edits may open files, read only only looks",
                (AgentCoordinator.Tools(project, nested, AgentAccess.Do).Contains("Bash(p4 edit:*)"),
                    AgentCoordinator.Tools(project, nested, AgentAccess.Analyze).Contains("Bash(p4 edit:*)"),
                    AgentCoordinator.Tools(project, nested, AgentAccess.Analyze).Contains("Bash(p4 opened:*)")),
                (true, false, true));
            Program.Equal("p4: never submit, revert or sync on the agent's own",
                AgentCoordinator.Tools(project, nested, AgentAccess.Do).Any(rule => rule.Contains("submit") || rule.Contains("revert") || rule.Contains("sync")),
                false);
            var codex = new CodexProvider(() => new AgentSettings(), _ => { });
            var task = new AgentTask { Provider = AgentProviderKind.Codex, Workspace = nested, Prompt = "Fix", Access = AgentAccess.Do };
            Program.Equal("p4: Codex with edits may reach the Perforce server, read only stays shut",
                (string.Join(' ', codex.Arguments(task)).Contains("-c sandbox_workspace_write.network_access=true"),
                    string.Join(' ', codex.Arguments(task with { Access = AgentAccess.Analyze })).Contains("network_access")),
                (true, false));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The agent's question as its rules ask it, as Claude's own tool writes it, and what is not one.</summary>
    private static void Questions()
    {
        var (text, question) = AgentQuestion.Split("""
            Two ways fit.

            ```link-ask
            {"question": "Which jump stays?", "options": [{"label": "Double jump", "description": "Less to change"}, "Single jump"]}
            ```
            """);
        Program.Equal("question: the block leaves the answer and becomes a choice",
            (text, question?.Question, string.Join("|", question?.Options.Select(option => $"{option.Label}/{option.Description}") ?? []), question?.MultiSelect),
            ("Two ways fit.", "Which jump stays?", "Double jump/Less to change|Single jump/", false));
        var claude = AgentQuestion.Split("""
            ```link-ask
            {"questions": [{"question": "Which files?", "header": "Files", "multiSelect": true, "options": [{"label": "Maps"}, {"label": "Blueprints"}]}]}
            ```
            """).Question;
        Program.Equal("question: Claude's own form, several at once", (claude?.Question, claude?.MultiSelect), ("Which files?", true));
        Program.Equal("question: broken JSON or no block leaves the answer as it is",
            (AgentQuestion.Split("```link-ask\n{oops\n```").Question, AgentQuestion.Split("Done.").Text), ((AgentQuestion?)null, "Done."));
    }

    /// <summary>Images reach both agents: Codex's list of them ends before the stdin marker, on resume too.</summary>
    private static void Images()
    {
        var codex = new CodexProvider(() => new AgentSettings(), _ => { });
        var task = new AgentTask
        {
            Provider = AgentProviderKind.Codex,
            Workspace = @"C:\Work",
            Prompt = "Look",
            Images = [@"C:\Shots\a.png", @"C:\Shots\b.png"]
        };
        var fresh = string.Join(' ', codex.Arguments(task));
        Program.Equal("images: Codex gets each image before its options, the prompt still comes on stdin",
            (fresh.StartsWith(@"exec --image C:\Shots\a.png --image C:\Shots\b.png --json", StringComparison.Ordinal), fresh.EndsWith(" -", StringComparison.Ordinal)),
            (true, true));
        Program.Equal("images: a reply with a screenshot reaches a resumed Codex session",
            string.Join(' ', codex.Arguments(task with { ResumeSessionId = "t1" })).StartsWith(@"exec resume t1 --image C:\Shots\a.png", StringComparison.Ordinal), true);
        var claude = new ClaudeCodeProvider(() => new AgentSettings(), _ => { });
        Program.Equal("images: Claude is told where they are and may read their folder",
            (claude.Input(task with { Provider = AgentProviderKind.ClaudeCode }).Contains(@"C:\Shots\b.png"),
                string.Join(' ', claude.Arguments(task with { Provider = AgentProviderKind.ClaudeCode })).Contains(@"--add-dir C:\Shots")),
            (true, true));
        var snapshot = new InputSnapshot
        {
            ProjectId = Guid.NewGuid(),
            ProjectName = "Game",
            Text = "It floats again",
            Context = [new ContextItem { Type = ContextItemType.ProjectState, Label = "Project note", Text = "Jumps" }]
        };
        Program.Equal("images: words for a running task leave the note out, a new task takes it",
            (AgentCoordinator.Words(snapshot, newTask: false).Words, AgentCoordinator.Words(snapshot, newTask: true).Words.Contains("Jumps")),
            ("It floats again", true));
    }

    private static async Task P4WorkspaceAsync(string workspace)
    {
        var changes = new AgentChanges(Path.Combine(Path.GetTempPath(), "link-p4-integration-backups"));
        var before = await changes.CaptureAsync(workspace, backup: true, CancellationToken.None);
        Program.Equal("p4 integration: .p4config workspace is detected without a backup",
            (before.IsPerforce, before.Backup), (true, (string?)null));
        Program.Equal("p4 integration: clean workspace has no agent changes",
            (await changes.CompareAsync(before, CancellationToken.None)).Count, 0);
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "link-agent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteSkill(string workspace, string name, string description)
    {
        var folder = Path.Combine(ProjectSkills.FolderIn(workspace), name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# Steps\n");
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

        var mcp = new ClaudeEventParser().Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__ue-mcp__tools","input":{"action":"call","category":"level","method":"place_actor","args":{"actorClass":"PointLight"}}}]}}""");
        Program.Equal("Claude: an MCP call shows its server, its tool and what it asks", mcp.Single().Text, "ue-mcp: tools call level place_actor");
    }

    private static void CodexFixtures()
    {
        var read = Parse(new CodexEventParser(), "codex-read.jsonl");
        Program.Equal("Codex: the thread id is the session", read.Parser.SessionId, "01a0f0b4-da92-79a3-92ed-1ae382b67719");
        Program.Equal("Codex: a command shows itself, not its PowerShell wrapper",
            read.Events.First(e => e.Kind == AgentEventKind.Tool).Text.StartsWith("git status --short;"), true);
        Program.Equal("Codex: the newer wrapper, a quoted path and the command in single quotes, shows the command too",
            (CodexEventParser.Unwrap(@"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" 'p4 set P4CONFIG=.p4config'"),
                CodexEventParser.Unwrap(@"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -Command 'Get-Content ''a b.txt'''"),
                CodexEventParser.Unwrap("dotnet build")),
            ("p4 set P4CONFIG=.p4config", "Get-Content 'a b.txt'", "dotnet build"));
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

        var mcp = new CodexEventParser().Read("""{"type":"item.completed","item":{"id":"i1","type":"mcp_tool_call","server":"unreal-mcp","tool":"call_tool","arguments":{"toolset":"ActorTools","tool":"SpawnActor","arguments":{"label":"Lamp"}},"status":"completed"}}""");
        Program.Equal("Codex: an MCP call shows its server, its tool and what it asks", mcp.Single().Text, "unreal-mcp: call_tool ActorTools SpawnActor");
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

        foreach (var (command, runs) in new[]
                 {
                     ("node tools/checks/verify_wall_climb.mjs", true),
                     ("timeout 280 node tools/checks/verify_wall_climb.mjs 2>&1 | tail -40", true),
                     ("cd Big_Steps && uv run --no-project tools/blender_mcp.py py tools/blender/shots.py Nori_Jump_Land 1-12", true),
                     ("MSYS_NO_PATHCONV=1 node tools/ue.mjs compile /Game/Hero", true),
                     ("dotnet test", true),
                     ("cat > \"$TEMP/read.mjs\" <<'EOF'", false),
                     ("git status --short", false),
                     ("rg -n Stamina", false)
                 })
        {
            Program.Equal($"runs code: {command}", AgentEventParser.RunsCode(command), runs);
        }
    }

    /// <summary>
    /// What the session holds: Claude Code's usage of each message (told once per size) and the window of its result,
    /// Codex's token count in the log of its session; the line the task shows.
    /// </summary>
    private static void ContextMeter()
    {
        // Claude Code writes a message's content blocks as lines of their own, each with the message's usage.
        var claude = new ClaudeEventParser();
        var events = new List<AgentEvent>();
        foreach (var line in new[]
                 {
                     """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Looking."}],"usage":{"input_tokens":5,"cache_creation_input_tokens":2000,"cache_read_input_tokens":90000,"output_tokens":10}}}""",
                     """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a.cs"}}],"usage":{"input_tokens":5,"cache_creation_input_tokens":2000,"cache_read_input_tokens":90000,"output_tokens":10}}}""",
                     """{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"Found it."}],"usage":{"input_tokens":6,"cache_creation_input_tokens":1000,"cache_read_input_tokens":120000,"output_tokens":40}}}""",
                     """{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":380000}}""",
                     """{"type":"assistant","message":{"id":"m3","content":[{"type":"text","text":"Done."}],"usage":{"input_tokens":3,"cache_creation_input_tokens":40000,"cache_read_input_tokens":0,"output_tokens":20}}}""",
                     """{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"s1","num_turns":3,"usage":{"input_tokens":14,"cache_creation_input_tokens":43000,"cache_read_input_tokens":210000,"output_tokens":70},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":900,"outputTokens":50,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"contextWindow":200000},"claude-opus-5-5":{"inputTokens":14,"outputTokens":70,"cacheReadInputTokens":210000,"cacheCreationInputTokens":43000,"contextWindow":1000000}}}"""
                 })
        {
            events.AddRange(claude.Read(line));
        }

        Program.Equal("Claude context: the input with the cache, once per size; the main model's window comes with the result",
            string.Join(", ", events.Where(e => e.Kind == AgentEventKind.Context).Select(e => $"{e.ContextTokens}/{e.ContextWindow}")),
            "92005/, 121006/, 40003/, 40003/1000000");
        Program.Equal("Claude context: the compaction is a step of Activity with the size it began at",
            events.Where(e => e.Text.StartsWith("Claude Code compacted", StringComparison.Ordinal)).Select(e => (e.Kind, e.Text)).Single(),
            (AgentEventKind.Status, "Claude Code compacted the context (was 380k)"));
        Program.Equal("Claude context: the run keeps the last size and the window",
            (claude.Stats.ContextTokens, claude.Stats.ContextWindow), ((long?)40003, (long?)1_000_000));

        // A format that lacks what is looked for gives nothing, not a guess: the recorded run has a window but no usage per message.
        var recorded = Parse(new ClaudeEventParser(), "claude-do.jsonl");
        Program.Equal("Claude context: without usage there is no event, and no size in the numbers",
            (recorded.Events.Count(e => e.Kind == AgentEventKind.Context), recorded.Parser.Stats.ContextTokens, recorded.Parser.Stats.ContextWindow),
            (0, (long?)null, (long?)null));
        var bare = new ClaudeEventParser();
        Program.Equal("Claude context: a compaction without its size is still told",
            bare.Read("""{"type":"system","subtype":"compact_boundary"}""").Single().Text, "Claude Code compacted the context");
        Program.Equal("Claude context: a message with no usage, or an empty one, tells nothing",
            (bare.Read("""{"type":"assistant","message":{"content":[{"type":"text","text":"x"}]}}""").Count(e => e.Kind == AgentEventKind.Context),
                bare.Read("""{"type":"assistant","message":{"content":[],"usage":{"input_tokens":0}}}""").Count),
            (0, 0));

        // Codex: the last count of the log, with the window; a count with no info, or text that only names the field, is passed over.
        var folder = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "2026", "10", "07"));
            var rate = ""","rate_limits":{"limit_id":"codex","primary":{"used_percent":12.0,"window_minutes":300,"resets_at":4102444800},"secondary":{"used_percent":31.0,"window_minutes":10080,"resets_at":4102444800}}""";
            var file = Path.Combine(folder, "2026", "10", "07", "rollout-2026-10-07T10-00-00-abc.jsonl");
            File.WriteAllLines(file,
            [
                """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":60000,"output_tokens":1500},"last_token_usage":{"input_tokens":60000,"cached_input_tokens":50000,"output_tokens":1500,"total_tokens":61500},"model_context_window":258400}""" + rate + "}}",
                """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":242000,"output_tokens":1840},"last_token_usage":{"input_tokens":182000,"cached_input_tokens":150000,"output_tokens":340,"total_tokens":182340},"model_context_window":258400}""" + rate + "}}",
                """{"type":"response_item","payload":{"type":"message","content":[{"type":"output_text","text":"the \"token_count\" event has \"last_token_usage\" in it"}]}}""",
                """{"type":"event_msg","payload":{"type":"token_count","info":null""" + rate + "}}"
            ]);
            var report = CodexProvider.SessionReport(folder, "abc");
            Program.Equal("Codex context: the last count of the session's log gives the size and the window",
                (report.Context?.Kind, report.Context?.ContextTokens, report.Context?.ContextWindow),
                ((AgentEventKind?)AgentEventKind.Context, (long?)182340, (long?)258400));
            Program.Equal("Codex context: the limits are still read from the same log", report.Limits.Count, 2);

            File.WriteAllLines(file, ["""{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5}}}}"""]);
            Program.Equal("Codex context: a log without a last request tells none",
                (CodexProvider.SessionReport(folder, "abc").Context, CodexProvider.SessionReport(folder, "other").Context, CodexProvider.SessionReport(Path.Combine(folder, "none"), "abc").Context),
                ((AgentEvent?)null, (AgentEvent?)null, (AgentEvent?)null));
        }
        finally
        {
            DeleteFolder(folder);
        }

        // A level, not a sum: the run that goes on says where the session stands now.
        var held = new AgentStats { ContextTokens = 300_000, ContextWindow = 1_000_000 };
        var again = held.Add(new AgentStats { ContextTokens = 120_000 });
        Program.Equal("stats: the continuation's context wins, the window stays when it names none",
            (again.ContextTokens, again.ContextWindow, held.Add(new AgentStats()).ContextTokens), ((long?)120_000, (long?)1_000_000, (long?)300_000));

        Program.Equal("context line: against the budget, or the model's window when that is smaller",
            (AgentContext.Line(182_000, null, 400_000), AgentContext.Line(150_000, 200_000, 400_000), AgentContext.Line(300_000, 1_000_000, 400_000),
                AgentContext.Line(500_000, null, 1_000_000)),
            ("Context 182k / 400k (46%)", "Context 150k / 200k (75%)", "Context 300k / 400k (75%)", "Context 500k / 1M (50%)"));

        var record = JsonSerializer.Deserialize<AgentRunRecord>("""{"Provider":1,"Outcome":"done"}""")!;
        var kept = JsonSerializer.Deserialize<AgentRunRecord>(JsonSerializer.Serialize(new AgentRunRecord { ContextTokens = 182_000, ContextWindow = 200_000 }))!;
        Program.Equal("run record: an older one loads without a context, a new one keeps it",
            (record.ContextTokens, record.ContextWindow, kept.ContextTokens, kept.ContextWindow), ((long?)null, (long?)null, (long?)182_000, (long?)200_000));
    }

    /// <summary>The context budget: Settings' choice and its limits, and how it reaches each CLI.</summary>
    private static void ContextBudget()
    {
        var settings = new AgentSettings();
        Program.Equal("budget: 550k by default, in older settings too", (settings.ContextBudget, JsonSerializer.Deserialize<AgentSettings>("{}")!.ContextBudget), (550_000, 550_000));
        var normalized = new[] { 0, -1, 49_999, 2_000_001, 50_000, 550_000, 2_000_000 }.Select(budget =>
        {
            var each = new AgentSettings { ContextBudget = budget };
            each.Normalize();
            return each.ContextBudget;
        });
        Program.Equal("budget: 50k to 2M is kept, anything else is the default",
            string.Join(", ", normalized), "550000, 550000, 550000, 550000, 50000, 550000, 2000000");
        Program.Equal("budget: a copy keeps it", new AgentSettings { ContextBudget = 800_000 }.Clone().ContextBudget, 800_000);

        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var codex = new CodexProvider(() => settings, _ => { });
        var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Explain" };
        Program.Equal("budget: Claude Code compacts at it, through its auto-compact window",
            (claude.EnvironmentVariables(task with { ContextBudget = 400_000 }).GetValueOrDefault("CLAUDE_CODE_AUTO_COMPACT_WINDOW"),
                claude.EnvironmentVariables(task).ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW")),
            ("400000", false));
        Program.Equal("budget: Claude Code takes no argument for it",
            claude.Arguments(task with { ContextBudget = 400_000 }).SequenceEqual(claude.Arguments(task)), true);

        var codexTask = task with { Provider = AgentProviderKind.Codex };
        Program.Equal("budget: Codex compacts at nine tenths of it, on resume too, and without a budget leaves it alone",
            (string.Join(' ', codex.Arguments(codexTask with { ContextBudget = 400_000 })).Contains("-c model_auto_compact_token_limit=360000"),
                string.Join(' ', codex.Arguments(codexTask with { ContextBudget = 400_000, ResumeSessionId = "t1" })).Contains("-c model_auto_compact_token_limit=360000"),
                string.Join(' ', codex.Arguments(codexTask)).Contains("compact")),
            (true, true, false));
    }

    /// <summary>The agent's word for its next step, as its rules ask it, and what Link does with the block.</summary>
    private static void NextSteps()
    {
        var (text, next) = AgentNext.Split("""
            The jump is in.

            ```link-next
            {"next": "wire the jump to Enhanced Input", "effort": "Medium", "why": "plain wiring by the plan"}
            ```
            """);
        Program.Equal("next: the block leaves the answer and becomes the step, its effort lowercased",
            (text, next?.Step, next?.Effort, next?.Why, next?.Line),
            ("The jump is in.", "wire the jump to Enhanced Input", "medium", "plain wiring by the plan", "Next: wire the jump to Enhanced Input · medium"));

        var (_, unknown) = AgentNext.Split("```link-next\n{\"next\": \"tidy up\", \"effort\": \"ultra\"}\n```");
        Program.Equal("next: an effort the CLIs do not take is dropped, the step stays", (unknown?.Step, unknown?.Effort, unknown?.Why, unknown?.Line), ("tidy up", (string?)null, "", "Next: tidy up"));

        var (unchanged, broken) = AgentNext.Split("Done.\n```link-next\n{oops\n```");
        Program.Equal("next: broken JSON leaves the answer as it is", (unchanged, broken), ("Done.\n```link-next\n{oops\n```", (AgentNext?)null));
        Program.Equal("next: no block, a block without a step and another kind of block leave the answer as it is",
            (AgentNext.Split("Done.").Text, AgentNext.Split("```link-next\n{\"effort\": \"high\"}\n```").Next, AgentNext.Split("```link-ask\n{\"question\": \"Which?\"}\n```").Next),
            ("Done.", (AgentNext?)null, (AgentNext?)null));

        var (kept, last) = AgentNext.Split("```link-next\n{\"next\": \"first\"}\n```\nMore.\n```link-next\n{\"next\": \"second\", \"effort\": \"high\"}\n```");
        Program.Equal("next: the last block is the step", (last?.Step, last?.Effort, kept.Contains("first", StringComparison.Ordinal), kept.Contains("second", StringComparison.Ordinal)),
            ("second", "high", true, false));

        var record = new AgentTaskRecord { Goal = "Jump", Brief = "Jump", Workspace = @"C:\Work", LastWords = "Half done.\n```link-next\n{\"next\": \"wire it\", \"effort\": \"low\"}\n```" };
        var handOver = AgentCoordinator.HandOffBrief(record, "Claude Code", numstat: null);
        var back = AgentCoordinator.ReturnBrief(record, "Codex", numstat: null);
        Program.Equal("next: the hand-over briefs carry the words without the block",
            (handOver.Contains("Half done.", StringComparison.Ordinal), handOver.Contains("link-next", StringComparison.Ordinal),
                back.Contains("Half done.", StringComparison.Ordinal), back.Contains("link-next", StringComparison.Ordinal)),
            (true, false, true, false));
    }

    /// <summary>Auto effort: each rule of the policy in its order, and the efforts of the CLI it answers with.</summary>
    private static void AutoEffortPolicy()
    {
        string[] all = ["low", "medium", "high", "xhigh", "max"];
        (string? Effort, string Reason) Choose(EffortPhase phase, string brief = "Add a button", string? last = null, int failures = 0,
            AgentNext? next = null, string? role = null, string[]? efforts = null) =>
            AutoEffort.Choose(phase, brief, efforts ?? all, last, failures, next, role);

        Program.Equal("auto: a failed run raises the effort one step above the last",
            (Choose(EffortPhase.Reply, last: "medium", failures: 1), Choose(EffortPhase.Reply, last: "low", failures: 2).Effort, Choose(EffortPhase.Reply, last: "high", failures: 1).Effort),
            (("high", "raised after a failed run"), "medium", "xhigh"));
        Program.Equal("auto: a stem counts only at the start of a word",
            (Choose(EffortPhase.New, "Add an information panel").Effort, Choose(EffortPhase.New, "An important fix to the menu").Effort,
                Choose(EffortPhase.New, "Read the stack trace and fix the menu").Effort, Choose(EffortPhase.New, "Import the new FBX").Reason),
            ("medium", "medium", "medium", "\"import\": a mechanical change"));
        Program.Equal("auto: the raise never goes above xhigh, and starts from high when the last effort is not known",
            (Choose(EffortPhase.Reply, last: "xhigh", failures: 1).Effort, Choose(EffortPhase.Reply, last: "max", failures: 1).Effort, Choose(EffortPhase.Steer, failures: 1).Effort),
            ("xhigh", "xhigh", "high"));
        Program.Equal("auto: starting over raises it too, with its own reason",
            Choose(EffortPhase.Restart, last: "low"), ("medium", "raised: starting over after a stuck run"));
        Program.Equal("auto: a failure beats the agent's word and the phase",
            Choose(EffortPhase.RunPlan, last: "medium", failures: 1, next: new AgentNext("tidy up", "low", "mechanical")).Effort, "high");

        var asked = new AgentNext("wire the jump", "high", "a new mechanic");
        Program.Equal("auto: the agent's word is taken, with its reason",
            Choose(EffortPhase.Reply, last: "low", next: asked), ("high", "the agent asked: a new mechanic"));
        Program.Equal("auto: the agent's word beats the phase and the brief",
            (Choose(EffortPhase.RunPlan, next: asked).Effort, Choose(EffortPhase.New, brief: "Rename the field", next: asked).Effort), ("high", "high"));
        Program.Equal("auto: the agent's word stops at xhigh, and says so without a why",
            Choose(EffortPhase.Reply, next: new AgentNext("dig", "max", " ")), ("xhigh", "the agent asked"));
        var longWhy = Choose(EffortPhase.Reply, next: new AgentNext("dig", "low", new string('x', 90))).Reason;
        Program.Equal("auto: a long why is cut to about sixty characters",
            (longWhy.Length, longWhy.EndsWith("…", StringComparison.Ordinal)), ("the agent asked: ".Length + 60, true));
        Program.Equal("auto: a step without an effort is no word at all", Choose(EffortPhase.RunPlan, next: new AgentNext("dig", null, "x")), ("medium", "carrying out the approved plan"));

        Program.Equal("auto: carrying out the plan is medium", Choose(EffortPhase.RunPlan, brief: "Rewrite the system", last: "xhigh"), ("medium", "carrying out the approved plan"));
        Program.Equal("auto: words after a run keep its effort",
            (Choose(EffortPhase.Reply, last: "high"), Choose(EffortPhase.Steer, brief: "Rename it", last: "xhigh").Effort), (("high", "same as the last run"), "xhigh"));
        Program.Equal("auto: words with no last effort go by the brief", Choose(EffortPhase.Reply, brief: "Rename the field").Effort, "low");

        Program.Equal("auto: a hard task is high, with the word that said so",
            (Choose(EffortPhase.New, brief: "The app crashes on start"), Choose(EffortPhase.New, brief: "Build a save system").Reason, Choose(EffortPhase.HandOff, brief: "Investigate the stutter").Effort),
            (("high", "\"crash\": an unclear failure"), "\"system\": a design task", "high"));
        Program.Equal("auto: a mechanical task is low", Choose(EffortPhase.New, brief: "Rename the Player class"), ("low", "\"rename\": a mechanical change"));
        Program.Equal("auto: the brief is read in any case", Choose(EffortPhase.New, brief: "REFACTOR the input").Effort, "high");
        Program.Equal("auto: high wins over low", Choose(EffortPhase.New, brief: "Rewrite the docs"), ("high", "\"rewrite\": a design task"));
        Program.Equal("auto: Russian stems",
            (Choose(EffortPhase.New, brief: "Переименуй поле"), Choose(EffortPhase.New, brief: "Почему падает игра"), Choose(EffortPhase.New, brief: "Спроектируй систему").Effort,
                Choose(EffortPhase.New, brief: "Опечатка в описании").Effort, Choose(EffortPhase.New, brief: "Переименуй систему").Effort),
            (("low", "\"переимен\": a mechanical change"), ("high", "\"почему\": an unclear failure"), "high", "low", "high"));
        Program.Equal("auto: nothing to go by is medium, or the role's usual effort",
            (Choose(EffortPhase.New), Choose(EffortPhase.New, role: "High"), Choose(EffortPhase.New, role: "max").Effort, Choose(EffortPhase.New, role: "ultra").Effort),
            (("medium", "a usual task"), ("high", "the role's usual effort"), "xhigh", "medium"));
        Program.Equal("auto: the role does not beat the brief", Choose(EffortPhase.New, brief: "Rename it", role: "high").Effort, "low");

        string[] short3 = ["low", "medium", "high"];
        Program.Equal("auto: an effort the CLI lacks becomes the nearest, the cheaper of two",
            (Choose(EffortPhase.Reply, next: new AgentNext("dig", "xhigh", "x"), efforts: short3).Effort,
                Choose(EffortPhase.Reply, next: new AgentNext("dig", "xhigh", "x"), efforts: ["low", "medium", "high", "max"]).Effort,
                Choose(EffortPhase.Reply, next: new AgentNext("dig", "low", "x"), efforts: ["medium", "high"]).Effort),
            ("high", "high", "medium"));
        Program.Equal("auto: a failure on the CLI's top effort stays on it", Choose(EffortPhase.Reply, last: "high", failures: 1, efforts: short3).Effort, "high");
        Program.Equal("auto: the answer is the CLI's own value", Choose(EffortPhase.Reply, last: "medium", efforts: ["Low", "Medium", "High"]).Effort, "Medium");
        Program.Equal("auto: a list with values of its own is mapped by the known ones",
            Choose(EffortPhase.Reply, next: new AgentNext("dig", "high", "x"), efforts: ["minimal", "low", "medium"]).Effort, "medium");
        Program.Equal("auto: no efforts to choose from leaves the CLI its own",
            Choose(EffortPhase.New, brief: "Rename it", efforts: []), ((string?)null, "\"rename\": a mechanical change"));
    }

    /// <summary>The runs that count as failed for Auto: failed, the check failed, or stopped in a loop; a good run clears them.</summary>
    private static void AutoEffortFailures()
    {
        static AgentRunRecord Run(string outcome, bool looped = false) => new() { Outcome = outcome, Looped = looped };
        Program.Equal("auto failures: none before a first run, none after a good one",
            (AutoEffort.Failures([]), AutoEffort.Failures([Run("Failed after 1:00"), Run("Done in 2:10")])), (0, 0));
        Program.Equal("auto failures: a failed run and a failed check each count",
            (AutoEffort.Failures([Run("Done in 1:00"), Run("Failed after 1:00")]), AutoEffort.Failures([Run("Failed after 1:00"), Run("Check failed after 3:00")])), (1, 2));
        Program.Equal("auto failures: a run stopped by the user is neither good nor bad",
            (AutoEffort.Failures([Run("Failed after 1:00"), Run("Stopped after 0:10")]), AutoEffort.Failures([Run("Done in 1:00"), Run("Stopped after 0:10")])), (1, 0));
        Program.Equal("auto failures: stopped in a loop counts, done after a loop does not",
            (AutoEffort.Failures([Run("Stopped after 4:00", looped: true)]), AutoEffort.Failures([Run("Done in 5:00", looped: true)])), (1, 0));
    }

    /// <summary>Auto effort in what is saved: Settings' switch, the task's pin and the run's reason; older files load as before.</summary>
    private static void AutoEffortSaved()
    {
        Program.Equal("auto effort: on by default, in older settings too, kept by a copy",
            (new AgentSettings().AutoEffort, JsonSerializer.Deserialize<AgentSettings>("{}")!.AutoEffort, JsonSerializer.Deserialize<AgentSettings>("""{"AutoEffort":false}""")!.AutoEffort,
                new AgentSettings { AutoEffort = false }.Clone().AutoEffort),
            (true, true, false, false));

        // As the project's store writes and reads them.
        var options = new JsonSerializerOptions { WriteIndented = true };
        var older = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Fix it","Workspace":"C:\\Work","Runs":[{"Provider":0,"Effort":"high","Outcome":"Done in 1:00"}]}""", options)!;
        Program.Equal("auto effort: a task of an older Link keeps its own effort", (older.AutoEffort, older.Runs[0].Effort, older.Runs[0].EffortReason, older.Runs[0].Looped),
            (false, "high", (string?)null, false));
        var saved = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(new AgentTaskRecord
        {
            Goal = "Fix it",
            Workspace = @"C:\Work",
            AutoEffort = true,
            Runs = [new AgentRunRecord { Effort = "medium", EffortReason = "carrying out the approved plan", Looped = true, Outcome = "Stopped after 4:00" }]
        }, options), options)!;
        Program.Equal("auto effort: the task's pin and the run's reason and loop are kept",
            (saved.AutoEffort, saved.Runs[0].EffortReason, saved.Runs[0].Looped), (true, "carrying out the approved plan", true));
    }

    /// <summary>Specialists as files: the eight that ship beside Link, a workspace's own that add or replace by file name, and junk skipped.</summary>
    private static void RolesRead()
    {
        var builtIn = ProjectRoles.In(null);
        Program.Equal("roles: eight ship beside Link, by name",
            string.Join("|", builtIn.Select(role => role.Name)),
            "3D Artist|Build / Performance|Level Artist|QA / Playtest|Rigger / Animator|Technical Designer|UI/UX Designer|Unreal Developer");
        Program.Equal("roles: each has a description, keywords, skills, an effort and a body without the editor's note",
            builtIn.All(role => role.Description.Length > 0 && role.Keywords.Count > 0 && role.Skills.Count > 0 && role.Effort is not null &&
                                role.Body.StartsWith("## Duties", StringComparison.Ordinal) && !role.Body.Contains("<!--")), true);
        var rigger = builtIn.First(role => role.Id == "rigger-animator");
        Program.Equal("roles: a file's front matter is read, a list by its commas",
            (rigger.Name, rigger.Effort, string.Join("+", rigger.Skills), rigger.Keywords.Contains("animat"), rigger.FilePath.EndsWith("rigger-animator.md", StringComparison.Ordinal)),
            ("Rigger / Animator", "medium", "blender-anim+unreal-animbp", true, true));

        var workspace = TempFolder();
        try
        {
            var folder = ProjectRoles.FolderIn(workspace);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "qa-playtest.md"),
                "<!-- the game's own checker -->\n---\nname: QA of this game\ndescription: Our checker.\nkeywords: smoke, плейтест\nskills: game-check\neffort: high\n---\nCheck by the game's script.\n");
            File.WriteAllText(Path.Combine(folder, "audio.md"), "---\nname: Audio\ndescription: Sound.\nkeywords: sound, звук\n---\nMix the sound.");
            File.WriteAllText(Path.Combine(folder, "plain.md"), "Just notes, no front matter.");
            File.WriteAllText(Path.Combine(folder, "nameless.md"), "---\ndescription: No name.\nkeywords: x\n---\nBody");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "---\nname: Not a role\n---\nBody");
            var roles = ProjectRoles.In(workspace);
            Program.Equal("roles: a workspace file adds a role; junk, a file without a name and other extensions are skipped",
                string.Join("|", roles.Select(role => role.Id)),
                "3d-artist|audio|build-engineer|level-artist|qa-playtest|rigger-animator|technical-designer|ui-ux-designer|unreal-developer");
            var own = roles.First(role => role.Id == "qa-playtest");
            Program.Equal("roles: a workspace file of a built-in one's name replaces it",
                (own.Name, own.Effort, string.Join("+", own.Keywords), string.Join("+", own.Skills), own.Body), ("QA of this game", "high", "smoke+плейтест", "game-check", "Check by the game's script."));
            Program.Equal("roles: Find by file name, none for General and for a role whose file is gone",
                (ProjectRoles.Find("audio", workspace)?.Name, ProjectRoles.Find("AUDIO", workspace)?.Name, ProjectRoles.Find("audio", null)?.Name, ProjectRoles.Find(null, workspace)?.Name, ProjectRoles.Find("gone", workspace)?.Name),
                ("Audio", "Audio", (string?)null, (string?)null, (string?)null));
            Program.Equal("roles: no workspace folder, no workspace at all", (ProjectRoles.In(Path.Combine(workspace, "none")).Count, ProjectRoles.In(null).Count), (8, 8));
        }
        finally
        {
            DeleteFolder(workspace);
        }

        var (fields, body) = ProjectSkills.FrontMatter("---\nname: x\nkeywords: a, b\ndescription: >\n  folded\n  in one\nempty:\n---\n\nBody\nmore");
        Program.Equal("front matter: fields, a folded value in one line, a blank one left out, the body after",
            (fields["name"], fields["keywords"], fields["description"], fields.ContainsKey("empty"), body), ("x", "a, b", "folded in one", false, "Body\nmore"));
        Program.Equal("front matter: a file without it is all body",
            ProjectSkills.FrontMatter("No header\nhere").Body + "|" + ProjectSkills.FrontMatter("No header").Fields.Count, "No header\nhere|0");
    }

    /// <summary>The specialist Link suggests from the brief: the most keywords, by word starts, in English or Russian; none is General.</summary>
    private static void RoleSuggest()
    {
        var roles = ProjectRoles.In(null);
        string? Id(string brief) => ProjectRoles.Suggest(brief, roles)?.Id;
        Program.Equal("suggest: a Russian brief about an animation is the rigger",
            (Id("Переделай анимацию прыжка Нори"), Id("Fix the foot sliding in the run cycle")), ("rigger-animator", "rigger-animator"));
        Program.Equal("suggest: a build problem is the build engineer",
            (Id("Почему падает билд при упаковке"), Id("Cook errors when packaging for Windows")), ("build-engineer", "build-engineer"));
        Program.Equal("suggest: each of the others by its own words",
            (Id("Make a low-poly mesh in Blender and unwrap it"), Id("Блокаут локации и освещение"), Id("Нужен плейтест и баг-репорт"), Id("Write a spec with acceptance criteria"),
                Id("Draw the HUD in Figma"), Id("Add an Enhanced Input action in a Blueprint")),
            ("3d-artist", "level-artist", "qa-playtest", "technical-designer", "ui-ux-designer", "unreal-developer"));
        Program.Equal("suggest: the role with more of its keywords wins", Id("Retarget the run cycle animation and fix the camera"), "rigger-animator");
        Program.Equal("suggest: case does not matter", Id("PACKAGING FAILS"), "build-engineer");
        Program.Equal("suggest: a keyword counts once, and only at the start of a word",
            (Id("The inanimate object"), Id("Rename the variable"), Id(string.Empty), ProjectRoles.Suggest("Fix the animation", []) is null), ((string?)null, (string?)null, (string?)null, true));

        AgentRole Role(string id, params string[] keywords) => new(id, id, string.Empty, keywords, [], null, string.Empty, string.Empty);
        Program.Equal("suggest: a tie goes to the earlier role",
            (ProjectRoles.Suggest("a jump and a run", [Role("one", "jump"), Role("two", "run")])?.Id, ProjectRoles.Suggest("a jump and a run", [Role("two", "run"), Role("one", "jump")])?.Id), ("one", "two"));
        Program.Equal("suggest: the same word twice scores once",
            ProjectRoles.Suggest("jump jump jump run", [Role("one", "jump"), Role("two", "run", "walk", "jump")])?.Id, "two");
    }

    /// <summary>What a new session is told of its specialist and skills, where, and what a change of specialist says.</summary>
    private static void RoleSession()
    {
        var rigger = ProjectRoles.Find("rigger-animator", null)!;
        var workspace = TempFolder();
        try
        {
            WriteSkill(workspace, "blender-anim", "Animate in Blender.");
            WriteSkill(workspace, "other", "Something else.");
            var text = ProjectRoles.SessionText(rigger, ["blender-anim", "unreal-animbp"], workspace);
            Program.Equal("role text: the name and the body, then the skills the workspace has, as relative files",
                (text.StartsWith("## Your role: Rigger / Animator\n## Duties", StringComparison.Ordinal), text.EndsWith("\n\nRead first: .agents/skills/blender-anim/SKILL.md", StringComparison.Ordinal),
                    text.Contains("unreal-animbp"), text.Contains("<!--")),
                (true, true, false, false));
            Program.Equal("role text: a role without skills has no line, no role and no skills nothing, skills alone only the line",
                (ProjectRoles.SessionText(rigger, [], workspace).Contains("Read first"), ProjectRoles.SessionText(null, [], workspace), ProjectRoles.SessionText(null, ["blender-anim", "other"], workspace)),
                (false, string.Empty, "Read first: .agents/skills/blender-anim/SKILL.md, .agents/skills/other/SKILL.md"));
            var switched = ProjectRoles.SwitchText(rigger, ["blender-anim"], workspace);
            Program.Equal("role text: a change of specialist says from now on, then the same text",
                (switched.StartsWith("From now on you work on this task as Rigger / Animator.\n\n## Your role: Rigger / Animator\n", StringComparison.Ordinal), switched.EndsWith("blender-anim/SKILL.md", StringComparison.Ordinal)),
                (true, true));
            Program.Equal("role text: back to General says the role no longer applies",
                (ProjectRoles.SwitchText(null, [], workspace).Contains("without a specialist role"), ProjectRoles.SwitchText(null, [], workspace).Contains("## Your role")), (true, false));

            var withRole = AgentRules.Before("Fix it", workspace, listSkills: true, rigger, ["blender-anim"]);
            var order = new[] { "# Agent rules", "## Your role: Rigger / Animator", "Read first: .agents/skills/blender-anim/SKILL.md", "## Skills of this workspace", "\n\n# Task\n\nFix it" }
                .Select(part => withRole.IndexOf(part, StringComparison.Ordinal)).ToArray();
            Program.Equal("role text: in a new session after the rules, before the skills list and the task",
                (order.All(at => at >= 0), order.SequenceEqual(order.Order()), withRole.EndsWith("\n\n# Task\n\nFix it", StringComparison.Ordinal)), (true, true, true));
            var plain = AgentRules.Before("Fix it", workspace, listSkills: false);
            Program.Equal("role text: General with no skills adds nothing, nor do skills the workspace lacks",
                (plain.Contains("## Your role"), plain == AgentRules.Before("Fix it", workspace, listSkills: false, null, ["gone"])), (false, true));

            var claude = new ClaudeCodeProvider(() => new AgentSettings(), _ => { });
            var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "Fix it", RoleId = "rigger-animator", Skills = ["blender-anim"] };
            Program.Equal("role text: the provider gives a new session its role and skills, and a resumed one has them already",
                (claude.StandardInput(task).Contains("## Your role: Rigger / Animator"), claude.StandardInput(task).Contains("Read first: .agents/skills/blender-anim/SKILL.md"),
                    claude.StandardInput(task with { ResumeSessionId = "s1" }), claude.StandardInput(task with { RoleId = "gone", Skills = [] }).Contains("Your role")),
                (true, true, "Fix it", false));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The skills picked for a task: the role's own first, then those whose words are in the brief, at most four.</summary>
    private static void RoleSkills()
    {
        ProjectSkill[] skills =
        [
            new("blender-anim", "Animate and export rigs from Blender.", string.Empty),
            new("unreal-animbp", "Animation Blueprints in Unreal.", string.Empty),
            new("level-lighting", "Lighting of a level, Lumen settings.", string.Empty),
            new("jump-tuning", "Tune the jump height of the character.", string.Empty),
            new("save-system", "Save games to slots.", string.Empty),
            new("sound-mix", "Mix the sound.", string.Empty)
        ];
        var rigger = ProjectRoles.Find("rigger-animator", null)!;
        string Picked(string brief, AgentRole? role, params ProjectSkill[] among) => string.Join(",", ProjectRoles.SkillsFor(brief, role, among.Length > 0 ? among : skills));

        Program.Equal("skills: the role's own first, then the brief's words", Picked("Redo the jump", rigger), "blender-anim,unreal-animbp,jump-tuning");
        Program.Equal("skills: without a role only the brief's words; none when none match",
            (Picked("Fix the lighting", null), Picked("Rename the field", null)), ("level-lighting", string.Empty));
        Program.Equal("skills: the role's skills the workspace lacks are skipped",
            Picked("Fix the lighting", rigger, skills[2], skills[5]), "level-lighting");
        Program.Equal("skills: at most four, in the workspace's order on a tie", Picked("jump lighting sound save blender", null), "blender-anim,level-lighting,jump-tuning,save-system");
        Program.Equal("skills: the more words of a skill the brief has, the earlier",
            Picked("Mix the sound of the jump and tune it", null), "sound-mix,jump-tuning");
        Program.Equal("skills: a word counts at its start, in any case, and only a name's whole parts or a description's long words",
            (Picked("JUMPING", null), Picked("a jumper", null), Picked("unjump", null), Picked("an anim", null)), ("jump-tuning", "jump-tuning", string.Empty, "blender-anim"));

        Program.Equal("skills: words any text has tie no skill to a task",
            Picked("Fix the menu in the project, using the docs", null, new ProjectSkill("ue-blueprint", "Edit Blueprint graphs of the project using ue.mjs.", string.Empty)),
            string.Empty);

        AgentRole Role(string id, string skill, params string[] keywords) => new(id, id, string.Empty, keywords, string.IsNullOrEmpty(skill) ? [] : [skill], null, string.Empty, string.Empty);
        ProjectSkill[] measuring = [new("pie-check", "Measure foot sliding in PIE.", string.Empty), new("other", "Something else.", string.Empty)];
        Program.Equal("skills: a skill whose description has the role's keyword goes with the role",
            (Picked("Anything", Role("r", string.Empty, "foot slid"), measuring), Picked("Anything", Role("r", string.Empty, "lighting"), measuring)), ("pie-check", string.Empty));
        Program.Equal("skills: a role skill is named as the workspace has it, once", Picked("Anything", Role("r", "BLENDER-ANIM"), skills[0], skills[0]), "blender-anim");
    }

    /// <summary>The specialist and skills in what is saved: older tasks load as General with no skills.</summary>
    private static void RolesSaved()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var older = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Fix it","Workspace":"C:\\Work","Runs":[{"Provider":0,"Outcome":"Done in 1:00"}]}""", options)!;
        Program.Equal("roles saved: a task of an older Link is General with no skills", (older.RoleId, older.Skills.Count, older.Runs[0].RoleId), ((string?)null, 0, (string?)null));
        var saved = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(new AgentTaskRecord
        {
            Goal = "Fix it",
            Workspace = @"C:\Work",
            RoleId = "rigger-animator",
            Skills = ["blender-anim", "unreal-animbp"],
            Runs = [new AgentRunRecord { RoleId = "qa-playtest", Outcome = "Done in 1:00" }]
        }, options), options)!;
        Program.Equal("roles saved: the task's specialist and skills and the run's specialist are kept",
            (saved.RoleId, string.Join("+", saved.Skills), saved.Runs[0].RoleId), ("rigger-animator", "blender-anim+unreal-animbp", "qa-playtest"));
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

        // In a game editor the work is scripts and MCP calls, not edits: reading between them is no search loop, and a
        // check run again after each build is no repeated call. The same check three times with nothing between is.
        var read = (string file) => new AgentEvent(AgentEventKind.Tool, "Read: " + file, "Read") { IsSearch = true, Arguments = "file_path=" + file };
        var run = (string script) => new AgentEvent(AgentEventKind.Tool, "Bash: node " + script, "Bash") { IsWork = true, Arguments = "command=node " + script };
        var editor = new AgentLoopDetector(canChange: true);
        var editorWork = Enumerable.Range(0, 3)
            .SelectMany(round => Enumerable.Range(0, 8).Select(index => read($"tools/checks/part{round}{index}.mjs"))
                .Concat([run("apply_wall_climb.mjs update"), run("verify_wall_climb.mjs"), new AgentEvent(AgentEventKind.Tool, "ue-mcp: tools call blueprint compile", "mcp") { IsWork = true, Arguments = "compile" }]))
            .Select(editor.Add).OfType<AgentEvent>().ToList();
        Program.Equal("loops: reads between scripts and MCP calls, and a check after each build, are no loop", editorWork.Count, 0);
        var same = new AgentLoopDetector(canChange: true);
        Program.Equal("loops: the same check three times with nothing done between is one",
            Enumerable.Repeat(run("verify_wall_climb.mjs"), 3).Select(same.Add).OfType<AgentEvent>().Select(loop => loop.Detail).SingleOrDefault(),
            "repeated call");
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
        // Codex's own config is the user's: an empty home keeps their MCP servers out of the lines compared here.
        var home = TempFolder();
        var before = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", home);
            ArgumentLines(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", before);
            DeleteFolder(home);
        }
    }

    private static void ArgumentLines(string codexHome)
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
            string.Join(' ', claude.Arguments(analyze)).Contains("--disallowedTools Agent Task AskUserQuestion Edit Write NotebookEdit") &&
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

        var game = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(game, ".mcp.json"), """
                {"mcpServers": {
                  "unreal-mcp": {"type": "http", "url": "http://127.0.0.1:8000/mcp"},
                  "ue-mcp": {"command": "npx", "args": ["-y", "ue-mcp@1.3.9", "C:/Games/Nori/Nori.uproject"], "env": {"UE_MCP_FEEDBACK_MODE": "defer"}, "timeout": 600000},
                  "events": {"type": "sse", "url": "http://127.0.0.1:9000/sse"},
                  "it's": {"command": "named"},
                  "quoted": {"command": "x", "args": ["it's"]}
                }}
                """);
            var mcp = string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex, Workspace = game, ResumeSessionId = "t1" }));
            Program.Equal("Codex gets the workspace's MCP servers from .mcp.json, on resume too, but not SSE or what TOML cannot take",
                mcp.Contains("-c mcp_servers.unreal-mcp.url='http://127.0.0.1:8000/mcp'") &&
                mcp.Contains("-c mcp_servers.ue-mcp.command='npx' -c mcp_servers.ue-mcp.args=['-y', 'ue-mcp@1.3.9', 'C:/Games/Nori/Nori.uproject'] " +
                             "-c mcp_servers.ue-mcp.env.UE_MCP_FEEDBACK_MODE='defer' -c mcp_servers.ue-mcp.tool_timeout_sec=600") &&
                !mcp.Contains("mcp_servers.events") && !mcp.Contains("named") && !mcp.Contains("mcp_servers.quoted"), true);

            File.WriteAllText(Path.Combine(codexHome, "config.toml"), """
                model = "gpt-6-sol"
                [mcp_servers.node_repl]
                command = 'node_repl.exe'
                [mcp_servers.node_repl.env]
                CODEX_HOME = 'C:\Users\me\.codex'
                [mcp_servers.blender]
                command = "uv"
                [mcp_servers.blender.tools.execute_blender_code]
                approval_mode = "approve"
                [mcp_servers.ue-mcp]
                command = "node"
                [mcp_servers."two words"]
                command = "x"
                """);
            var own = string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex, Workspace = game }));
            Program.Equal("Codex: the user's own MCP servers stay off in Link's runs, those of .mcp.json on",
                own.Contains("-c mcp_servers.node_repl.enabled=false -c mcp_servers.blender.enabled=false") &&
                !own.Contains("mcp_servers.ue-mcp.enabled") && !own.Contains("two words"), true);
            Program.Equal("Codex: a workspace without .mcp.json runs with none of the user's servers",
                string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex })).Contains("-c mcp_servers.blender.enabled=false"), true);

            var servers = string.Join(' ', claude.Arguments(analyze with { Workspace = game }));
            Program.Equal("Claude: MCP servers only from the workspace's .mcp.json, none without it",
                (servers.Contains($"--strict-mcp-config --mcp-config {Path.Combine(game, ".mcp.json")}"),
                    string.Join(' ', claude.Arguments(analyze)).Contains("--strict-mcp-config") && !claude.Arguments(analyze).Contains("--mcp-config")),
                (true, true));
        }
        finally
        {
            DeleteFolder(game);
        }

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

    /// <summary>The console holds what Link sent each run and every line back; a new task clears it; a long line is cut.</summary>
    private static async Task ConsoleAsync()
    {
        var console = new AgentConsole();
        await new FakeProvider("stream", new AgentSettings { ClaudePath = Environment.ProcessPath }, _ => { }, console)
            .RunAsync(Task(), new SyncProgress(_ => { }), CancellationToken.None);
        var (text, end, _) = console.After(0);
        Program.Equal("console: the command and the whole stdin first, then every line the CLI wrote",
            text.Contains("Fake · new session") && text.Contains($"{FakeFlag} stream") &&
            text.IndexOf("── stdin ──", StringComparison.Ordinal) < text.IndexOf("fake task", StringComparison.Ordinal) &&
            text.IndexOf("── output ──", StringComparison.Ordinal) < text.IndexOf("fake-session", StringComparison.Ordinal), true);
        Program.Equal("console: a reader that has caught up gets nothing again", console.After(end).Text, string.Empty);
        console.Line(new string('x', 5000), error: true);
        Program.Equal("console: stderr is marked and a very long line is cut",
            console.After(end).Text is var line && line.StartsWith("! xxx", StringComparison.Ordinal) && line.Contains("… (+1000 characters)"), true);
        console.Clear();
        Program.Equal("console: a new task starts it again for every reader", console.After(end) is { Text: "", Whole: true }, true);
    }

    /// <summary>
    /// The other agent takes the task over in a new session of its own, with a brief made from the task's record: the
    /// task, the user's words, the plan, the files, the check, how the first one ended and its last steps, in budget.
    /// </summary>
    private static void HandOff()
    {
        var first = new AgentTask
        {
            Provider = AgentProviderKind.ClaudeCode,
            Workspace = @"C:\Work",
            Prompt = "These tools are allowed now: Bash(x). Continue the task.",
            Access = AgentAccess.Do,
            AutoApprove = true,
            ResumeSessionId = "s1",
            CheckCommand = "dotnet test"
        };
        var record = new AgentTaskRecord
        {
            Goal = "Make the landing animation",
            Brief = "Make the landing animation",
            Workspace = @"C:\Work",
            ChangedFiles = ["tools/anim.py"],
            Plan = [new AgentPlanItem("Find the clip", true, false), new AgentPlanItem("Blend it", false, true)],
            Steps = ["Edit: anim.py", "Said: half done"],
            LastWords = "You've hit your limit · resets 7pm",
            LastCheck = "dotnet test: failed (exit 1)"
        };
        record.Add(AgentEntryKind.You, "keep the pose");
        var brief = AgentCoordinator.HandOffBrief(record, "Claude Code", numstat: null);
        Program.Equal("hand-over brief: the task, the user's words, the plan, the files, the check, how it ended, its steps",
            brief.StartsWith("Make the landing animation\n\nYou take this task over from Claude Code", StringComparison.Ordinal) &&
            brief.Contains("What the user said on the way, oldest first:\n- keep the pose") &&
            brief.Contains("- [x] Find the clip\n- [ ] Blend it") && brief.Contains("Files changed so far:\n- tools/anim.py") &&
            brief.Contains("The project's check as it last ran:\ndotnet test: failed") &&
            brief.Contains("How Claude Code ended:\nYou've hit your limit") && brief.Contains("- Edit: anim.py\n- Said: half done"), true);
        record.Steps = [.. Enumerable.Range(1, 200).Select(index => $"Read: file{index}.cs " + new string('x', 80))];
        var trimmed = AgentCoordinator.HandOffBrief(record, "Claude Code", numstat: null);
        Program.Equal("hand-over brief: a long task keeps to its budget by its oldest steps, the user's words stay",
            trimmed.Length <= AgentCoordinator.BriefBudget && trimmed.Contains("keep the pose") && !trimmed.Contains("file1.cs"), true);
        var task = AgentCoordinator.HandOffTask(first, AgentProviderKind.Codex, brief, []);
        Program.Equal("hand-over: a new session of the other agent, in the same folder with the same access and check",
            (task.Provider, task.ResumeSessionId, task.Workspace, task.Access, task.AutoApprove, task.CheckCommand, task.Prompt),
            (AgentProviderKind.Codex, (string?)null, @"C:\Work", AgentAccess.Do, true, "dotnet test", brief));
        Program.Equal("return: the first agent is told what the other did",
            AgentCoordinator.ReturnBrief(record, "Codex", numstat: null).StartsWith("While you were away, Codex worked on this task", StringComparison.Ordinal),
            true);

        // The agents' own plans and usage windows, as their CLIs write them.
        var claude = new ClaudeEventParser();
        var plan = claude.Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Find the cause","status":"completed","activeForm":"Finding"},{"content":"Fix it","status":"in_progress","activeForm":"Fixing"}]}}]}}""");
        Program.Equal("Claude's TodoWrite is the agent's plan, not a step",
            plan.Single().Kind == AgentEventKind.Plan && plan.Single().Plan.Count == 2 && plan.Single().Plan[0].Done && plan.Single().Plan[1].Active, true);
        var limits = claude.Read("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.92,"resetsAt":4102444800,"unifiedWindows":{"five_hour":{"utilization":0.92,"resetsAt":4102444800},"seven_day":{"utilization":0.31,"resetsAt":4102444800}}},"session_id":"s1"}""");
        Program.Equal("Claude's rate_limit_event gives its windows with the share used",
            limits.Single().Limits.Select(limit => $"{limit.Window} {limit.UsedPercent:0}").Aggregate((a, b) => a + ", " + b), "5 h 92, week 31");
        var codex = new CodexEventParser();
        var codexPlan = codex.Read("""{"type":"item.updated","item":{"id":"i1","type":"todo_list","items":[{"text":"Read the map","completed":true},{"text":"Edit the camera","completed":false}]}}""");
        Program.Equal("Codex's todo list is the agent's plan, the first open step under way",
            codexPlan.Single().Plan.Select(item => item.Active).SequenceEqual([false, true]), true);
        var folder = Path.Combine(Path.GetTempPath(), "link-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "2026", "10", "03"));
        File.WriteAllLines(Path.Combine(folder, "2026", "10", "03", "rollout-2026-10-03T16-12-39-abc.jsonl"),
        [
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":12.0,"window_minutes":300,"resets_at":4102444800},"secondary":{"used_percent":100.0,"window_minutes":10080,"resets_at":4102444800}}}}"""
        ]);
        var windows = CodexProvider.SessionReport(folder, "abc").Limits;
        Directory.Delete(folder, recursive: true);
        Program.Equal("Codex's session log gives its 5-hour and weekly windows, a full one reached",
            windows.Select(limit => $"{limit.Window} {limit.UsedPercent:0} {limit.Reached}").Aggregate((a, b) => a + ", " + b), "5 h 12 False, week 100 True");

        // Usage asked anew: Claude Code's /usage text and Codex's app-server answer.
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 4, 12, 0, 0)));
        var claudeUsage = ClaudeCodeProvider.UsageLimits("""
            Total cost:            $0.0000
            Current session: 23% used · resets Oct 4, 3pm (Europe/Moscow)
            Current week (all models): 45% used · resets Oct 9, 10:30am (Europe/Moscow)
            Current week (Sonnet only): 4% used
            """, now);
        Program.Equal("usage: Claude's /usage lines become the 5-hour and weekly windows, a model's week apart",
            string.Join(", ", claudeUsage.Select(limit => $"{limit.Window} {limit.UsedPercent:0} {limit.ResetsAt?.LocalDateTime:MM-dd HH:mm}")),
            "5 h 23 10-04 15:00, week 45 10-09 10:30, week, Sonnet 4 ");
        Program.Equal("usage: a reset in another year names it, one without a year that has passed is next year's",
            (ClaudeCodeProvider.ResetTime("Jan 2, 2027, 3pm", now)?.LocalDateTime, ClaudeCodeProvider.ResetTime("Jan 2, 9am", now)?.LocalDateTime.Year,
                ClaudeCodeProvider.ResetTime("soon", now)),
            ((DateTime?)new DateTime(2027, 1, 2, 15, 0, 0), (int?)2027, (DateTimeOffset?)null));
        var codexUsage = CodexProvider.AccountLimits(
            """{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":300,"resetsAt":4102444800},"secondary":{"usedPercent":31,"windowDurationMins":10080,"resetsAt":4102444800},"rateLimitReachedType":null}}}""",
            now);
        Program.Equal("usage: Codex's app-server gives percents, so 1 is one percent, not all",
            string.Join(", ", codexUsage.Select(limit => $"{limit.Window} {limit.UsedPercent:0} {limit.Reached}")), "5 h 1 False, week 31 False");
        Program.Equal("usage: an error answer gives no windows", CodexProvider.AccountLimits("""{"id":2,"error":{"code":-32603}}""", now).Count, 0);

        var store = new AgentLimits(Path.Combine(Path.GetTempPath(), "link-limits-" + Guid.NewGuid().ToString("N") + ".json"));
        store.Update(windows);
        Program.Equal("limits: a full window means the agent is near its limit and waits for the reset",
            (store.IsNear(AgentProviderKind.Codex), store.ReachedUntil(AgentProviderKind.Codex) is not null, store.IsNear(AgentProviderKind.ClaudeCode)),
            (true, true, false));
        Program.Equal("an earlier Link's session becomes a task of one run",
            AgentTaskRecord.FromSession(new AgentSessionRecord { SessionId = "s9", Workspace = @"C:\Work", Goal = "Fix it", Summary = "Done." }) is
                { Goal: "Fix it", LastWords: "Done.", Runs: [{ SessionId: "s9" }] }, true);
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
    private sealed class FakeProvider(string scenario, AgentSettings settings, Action<string> log, AgentConsole? console = null)
        : CliAgentProvider(() => settings, log, console)
    {
        public override AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
        public override string Name => "Fake";
        public override IReadOnlyList<AgentChoice> Efforts => [];
        public override IReadOnlyList<AgentChoice> Models => [];
        protected override IReadOnlyList<string> SignInCheck => [];
        public override IReadOnlyList<string> Arguments(AgentTask task) => [FakeFlag, scenario];
        public override AgentEventParser CreateParser(AgentTask task) => new ClaudeEventParser();
        protected override bool IsSignedIn(string answer) => true;
        public override Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<AgentLimit>>([]);
    }

    /// <summary>An agent whose runs do what the check says, for what Link does around a run; it keeps the tasks it got.</summary>
    private sealed class ScriptedProvider(Func<AgentTask, int, AgentResult> run) : IAgentProvider
    {
        public List<AgentTask> Tasks { get; } = [];
        public AgentProviderKind Kind => AgentProviderKind.ClaudeCode;
        public string Name => "Scripted";
        public IReadOnlyList<AgentChoice> Efforts => [];
        public IReadOnlyList<AgentChoice> Models => [];
        public IReadOnlyList<AgentChoice> EffortsOf(string? model) => [];
        public (string? Model, string? Effort) OwnSetup => (null, null);

        public Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<AgentLimit>>([]);

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
