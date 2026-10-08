using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        TaskStateBuilt();
        TaskStateBudget();
        CompactSession();
        CustomPresetsKnown();
        CustomSettingsKept();
        CustomAgentSettings();
        CustomOwnAnswers();
        CustomProviderEnvironment();
        CustomProviderArguments();
        await CustomProviderRefusesAsync();
        CustomKeyStaysOut();
        await CustomStartOverAsync();
        CustomCapabilities();
        CustomTaskStateNames();
        PlanTaskTools();
        PlanTaskToolsAcrossRuns();
        PlanKeepsOlderRecords();
        StepGroupsSummary();
        StepMarks();
        TaskChangesCount();
        StatsReport();
        HarnessBuilt();
        HarnessWords();
        HarnessCardLine();
        StudioSettingsKept();
        StudioSettingsOdd();
        CheaperHints();
        FirstRunWords();
        SkillUpdateRequest();
        TeamPackRoundTrip();
        TeamPackLinks();
        TeamPackReplace();
        TeamPackRefusals();
        StepKindsWords();
        TeamPlanSplit();
        TeamPlanPrompt();
        TeamSettingsKept();
        TeamRecordsKept();
        WorkerEvidenceRules();
        WorkerRoutes();
        CompactRuleReasons();
        TaskMetricsCounted();
        RoleSuggestAll();
        StepBriefBuilt();
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
        Program.Equal("budget: 450k by default, in older settings too", (settings.ContextBudget, JsonSerializer.Deserialize<AgentSettings>("{}")!.ContextBudget), (450_000, 450_000));
        var normalized = new[] { 0, -1, 49_999, 2_000_001, 50_000, 550_000, 2_000_000 }.Select(budget =>
        {
            var each = new AgentSettings { ContextBudget = budget };
            each.Normalize();
            return each.ContextBudget;
        });
        Program.Equal("budget: 50k to 2M is kept, anything else is the default",
            string.Join(", ", normalized), "450000, 450000, 450000, 450000, 50000, 550000, 2000000");
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
        Program.Equal("auto failures: a run stopped by the user or a limit ends the count: it already had the raise",
            (AutoEffort.Failures([Run("Failed after 1:00"), Run("Stopped after 0:10")]),
                AutoEffort.Failures([Run("Failed after 1:00"), Run("Stopped at the usage limit after 2:00")])), (0, 0));
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

    /// <summary>The task's state from its record: a section only when the record has something for it, in order, and the next step from the agent, else the plan.</summary>
    private static void TaskStateBuilt()
    {
        var workspace = TempFolder();
        try
        {
            var bare = new AgentTaskRecord { Goal = "Fix the jump", Brief = "Fix the jump, it is too low.", Workspace = workspace };
            Program.Equal("task state: a new task has its goal and brief, no plan, no check and no step",
                TaskState.Build(bare, null, workspace),
                "# Task state\nGoal: Fix the jump\n## Current state\nNo plan written yet.\n## Verified\nNothing checked by Link yet.\n## Next action\nDecide from the state above.\n## Pointers\n- The brief this task began with is below.\n## Brief\nFix the jump, it is too low.");

            Directory.CreateDirectory(Path.Combine(workspace, "docs"));
            File.WriteAllText(ProjectMap.PathIn(workspace), "# Map");
            WriteSkill(workspace, "jump-tuning", "Tune the jump.");
            var at = DateTimeOffset.Now;
            var time = AgentLimits.Time(at);
            var record = new AgentTaskRecord
            {
                Goal = "Make the landing animation",
                Brief = "Make the landing animation",
                Workspace = workspace,
                ChangedFiles = ["tools/anim.py"],
                Plan = [new AgentPlanItem("Find the clip", true, false), new AgentPlanItem("Blend it", false, true)],
                LastCheck = "dotnet test: failed (exit 1)",
                Runs =
                [
                    new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Failed after 1:02", StartedAt = at, Looped = true },
                    new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Done in 3:00", StartedAt = at, Looped = true },
                    new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Stopped after 0:10", StartedAt = at },
                    new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Stopped after 0:20", StartedAt = at, Looped = true },
                    new AgentRunRecord
                    {
                        Provider = AgentProviderKind.Codex, Outcome = "Check failed after 2:00", StartedAt = at,
                        Words = "Half done.\n```link-next\n{\"next\": \"wire it\"}\n```"
                    }
                ]
            };
            record.Add(AgentEntryKind.You, "keep the pose");
            record.Add(AgentEntryKind.Check, "dotnet test: failed");
            record.Add(AgentEntryKind.You, "no new bones");
            var state = TaskState.Build(record, null, workspace);
            Program.Equal("task state: the user's words, the plan, the files, the check, the failed runs, the agent's next step, the pointers, the brief",
                state, string.Join('\n',
                    "# Task state", "Goal: Make the landing animation",
                    "## Decisions", "- keep the pose", "- no new bones",
                    "## Current state", "- [x] Find the clip", "- [ ] Blend it",
                    "## Files touched", "- tools/anim.py",
                    "## Verified", "dotnet test: failed (exit 1)",
                    "## Known problems", $"- Claude Code run {time}: Failed after 1:02 (stuck in a loop)", $"- Codex run {time}: Stopped after 0:20 (stuck in a loop)",
                    $"- Codex run {time}: Check failed after 2:00",
                    "## Next action", "wire it",
                    "## Pointers", "- Project map: docs/map.md", "- Skills: .agents/skills/", "- The brief this task began with is below.",
                    "## Brief", "Make the landing animation"));
            Program.Equal("task state: git's lines added and removed take the place of the list of files",
                TaskState.Build(record, "12\t3\ttools/anim.py", workspace).Contains("## Files touched\n12\t3\ttools/anim.py\n## Verified"), true);

            var planned = new AgentTaskRecord { Goal = "Jump", Brief = "Jump", Workspace = workspace, Plan = record.Plan };
            string Next(AgentTaskRecord each) => TaskState.Build(each, null, workspace).Split("\n## Next action\n")[1].Split('\n')[0];
            Program.Equal("task state: without a word from the agent the next step is the plan's first open one, then nothing to go by",
                (Next(planned), Next(new AgentTaskRecord { Goal = "Jump", Brief = "Jump", Workspace = workspace, Plan = [new AgentPlanItem("Find the clip", true, false)] })),
                ("Blend it", "Decide from the state above."));
            planned.Runs.Add(new AgentRunRecord { Words = "Done.\n```link-next\n{\"next\": \"tune the landing\", \"effort\": \"low\"}\n```" });
            Program.Equal("task state: the agent's own next step comes before the plan, without its effort", Next(planned), "tune the landing");
            planned.Runs.Add(new AgentRunRecord { Words = "Done, nothing left to say." });
            Program.Equal("task state: only the last run's word counts", Next(planned), "Blend it");

            Program.Equal("compact: the history line names what the session held, when known",
                (TaskState.Compacted(182_000), TaskState.Compacted(null)),
                ("Compacted: a fresh session from the task state (was 182k tokens)", "Compacted: a fresh session from the task state"));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>A state over the budget is cut in its order, and what must stay stays whatever the size.</summary>
    private static void TaskStateBudget()
    {
        var workspace = TempFolder();
        try
        {
            AgentTaskRecord Record(int problems, int decisions, int decisionLength, int briefLength, int files)
            {
                var record = new AgentTaskRecord
                {
                    Goal = "Make the landing animation",
                    Brief = new string('b', briefLength),
                    Workspace = workspace,
                    ChangedFiles = [.. Enumerable.Range(1, files).Select(index => $"Assets/Animation/File{index:000}.uasset")],
                    Plan = [new AgentPlanItem("Blend it", false, true)],
                    Runs = [.. Enumerable.Range(1, problems).Select(index => new AgentRunRecord { Outcome = $"Failed after {index}:00" })]
                };
                for (var index = 1; index <= decisions; index++) record.Add(AgentEntryKind.You, $"{index}: " + new string('d', decisionLength));
                return record;
            }

            string[] Section(string state, string title) =>
                state.Split("\n## ").First(part => part.StartsWith(title + "\n", StringComparison.Ordinal)).Split('\n')[1..];

            var small = TaskState.Build(Record(problems: 6, decisions: 2, decisionLength: 100, briefLength: 1000, files: 5), null, workspace);
            Program.Equal("task state budget: a state that fits is whole",
                (Section(small, "Known problems").Length, Section(small, "Decisions").Select(line => line.Length).Max(), Section(small, "Brief")[0].Length, small.Length <= AgentCoordinator.BriefBudget),
                (6, 105, 1000, true));

            var problems = TaskState.Build(Record(problems: 40, decisions: 2, decisionLength: 100, briefLength: 4200, files: 5), null, workspace);
            Program.Equal("task state budget: the older problems go first, the last three stay, the rest is whole",
                (string.Join(", ", Section(problems, "Known problems").Select(line => line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..])),
                    Section(problems, "Decisions").Select(line => line.Length).Max(), Section(problems, "Brief")[0].Length, problems.Length <= AgentCoordinator.BriefBudget),
                ("Failed after 38:00, Failed after 39:00, Failed after 40:00", 105, 4200, true));

            var decisions = TaskState.Build(Record(problems: 40, decisions: 12, decisionLength: 590, briefLength: 1200, files: 5), null, workspace);
            Program.Equal("task state budget: then each decision is cut to 300 characters, the brief is still whole",
                (Section(decisions, "Decisions").Length, Section(decisions, "Decisions").Select(line => line.Length).Distinct().Single(), Section(decisions, "Known problems").Length,
                    Section(decisions, "Brief")[0].Length, decisions.Length <= AgentCoordinator.BriefBudget),
                (12, 303, 3, 1200, true));

            var everything = TaskState.Build(Record(problems: 40, decisions: 8, decisionLength: 590, briefLength: 20_000, files: 100), null, workspace);
            Program.Equal("task state budget: then the brief is cut to 1500 characters and the files to 30 lines; goal, decisions and next action stay",
                (Section(everything, "Brief")[0].Length, Section(everything, "Files touched").Length, Section(everything, "Files touched")[^1], Section(everything, "Decisions").Length,
                    everything.Contains("Goal: Make the landing animation"), Section(everything, "Next action")[0], everything.Length <= AgentCoordinator.BriefBudget),
                (1501, 31, "… and 70 more", 8, true, "Blend it", true));

            var long30 = TaskState.Build(Record(problems: 40, decisions: 30, decisionLength: 590, briefLength: 20_000, files: 100), null, workspace);
            Program.Equal("task state budget: the decisions are never dropped, even when they alone are over the budget",
                (Section(long30, "Decisions").Length, long30.Contains("Goal: Make the landing animation"), Section(long30, "Next action")[0], long30.Length > AgentCoordinator.BriefBudget),
                (30, true, "Blend it", true));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>Compact: the same agent in a new session from the state, as the user edited it, told the task's specialist and skills like any new session.</summary>
    private static void CompactSession()
    {
        var workspace = TempFolder();
        try
        {
            WriteSkill(workspace, "blender-anim", "Animate in Blender.");
            var record = new AgentTaskRecord
            {
                Goal = "Make the landing animation",
                Brief = "Make the landing animation",
                Workspace = workspace,
                RoleId = "rigger-animator",
                Skills = ["blender-anim"]
            };
            var brief = TaskState.CompactBrief(record, null, workspace);
            Program.Equal("compact: the state and then the words to go on with",
                (brief.StartsWith("# Task state\nGoal: Make the landing animation\n", StringComparison.Ordinal), brief.EndsWith($"\n\n{TaskState.GoOn}", StringComparison.Ordinal),
                    TaskState.GoOn), (true, true, "Go on with the task from this state; look at the files touched before changing them; do not redo what is done."));

            var last = new AgentTask
            {
                Provider = AgentProviderKind.Codex, Workspace = workspace, Prompt = "Continue", Access = AgentAccess.Do, AutoApprove = true, ResumeSessionId = "s1",
                Model = "gpt-6-sol", Effort = "high", CheckCommand = "dotnet test", PlanOnly = true, AnswerFile = "docs/map.md", AnswerIsSkill = true,
                RoleId = "qa-playtest", Skills = ["old"], Images = [@"C:\old.png"]
            };
            var task = AgentCoordinator.CompactTask(last, record, "  # Task state, edited \n", [@"C:\shot.png"]);
            Program.Equal("compact: a new session of the same agent with its model, effort, access and check, the edited state as the prompt",
                (task.Provider, task.ResumeSessionId, task.Prompt, task.Model, task.Effort, task.Access, task.AutoApprove, task.CheckCommand, task.PlanOnly, task.AnswerFile, task.AnswerIsSkill,
                    string.Join(",", task.Images)),
                (AgentProviderKind.Codex, (string?)null, "# Task state, edited", "gpt-6-sol", "high", AgentAccess.Do, true, "dotnet test", false, (string?)null, false, @"C:\shot.png"));
            Program.Equal("compact: the task's specialist and skills, not the last run's",
                (task.RoleId, string.Join(",", task.Skills)), ("rigger-animator", "blender-anim"));

            var claude = new ClaudeCodeProvider(() => new AgentSettings(), _ => { });
            var input = claude.StandardInput(task with { Provider = AgentProviderKind.ClaudeCode });
            var order = new[] { "# Agent rules", "## Your role: Rigger / Animator", "Read first: .agents/skills/blender-anim/SKILL.md", "# Task\n\n# Task state" }
                .Select(part => input.IndexOf(part, StringComparison.Ordinal)).ToArray();
            Program.Equal("compact: the new session gets the rules, the role and the skills before the state",
                (order.All(at => at >= 0), order.SequenceEqual(order.Order())), (true, true));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The presets Settings starts a custom model from: an address and models each, and who sees the code.</summary>
    private static void CustomPresetsKnown()
    {
        var named = CustomPresets.All.Where(info => info.Preset != CustomPreset.Other).ToArray();
        Program.Equal("custom presets: every one but Other has an http(s) address and a model",
            named.All(info => (info.BaseUrl.StartsWith("https://", StringComparison.Ordinal) || info.BaseUrl.StartsWith("http://", StringComparison.Ordinal)) &&
                              info.Models.Count > 0 && info.Name.Length > 0 && info.Note.Length > 0), true);
        var other = CustomPresets.Of(CustomPreset.Other);
        Program.Equal("custom presets: Other has neither, and each preset is found by its own name",
            (other.BaseUrl, other.Models.Count, CustomPresets.All.All(info => CustomPresets.Of(info.Preset) == info)), ("", 0, true));
        Program.Equal("custom presets: GLM is first and has the free flash model, no preset reads images",
            (CustomPresets.All[0].Preset, CustomPresets.All[0].Models[0], CustomPresets.All.Any(info => info.Images)), (CustomPreset.ZaiGlm, "glm-4.7-flash", false));
        Program.Equal("custom presets: only Ollama needs no key, and keeps the code on this PC",
            (string.Join(",", CustomPresets.All.Where(info => !info.NeedsKey).Select(info => info.Preset)), CustomPresets.Of(CustomPreset.Ollama).SeesCode),
            ("Ollama", "this PC"));
    }

    /// <summary>The custom model's setup: complete when new, blanks fall back to the preset, and only a safe address takes the key.</summary>
    private static void CustomSettingsKept()
    {
        var fresh = new CustomModelSettings();
        Program.Equal("custom settings: a new one is the GLM preset, complete",
            (fresh.Preset, fresh.BaseUrl, fresh.Model, fresh.DisplayName), (CustomPreset.ZaiGlm, "https://api.z.ai/api/anthropic", "glm-4.7-flash", "GLM (Z.ai)"));
        var blank = new CustomModelSettings { Preset = CustomPreset.Ollama, Name = "  ", BaseUrl = " ", Model = "" };
        blank.Normalize();
        Program.Equal("custom settings: blanks become the preset's address and first model, a blank name is none",
            (blank.BaseUrl, blank.Model, blank.Name, blank.DisplayName), ("http://localhost:11434", "qwen3-coder", (string?)null, "Ollama (local)"));
        var typed = new CustomModelSettings { Preset = CustomPreset.DeepSeek, Name = " Work ", BaseUrl = " https://example.test/anthropic ", Model = " my-model " };
        typed.Normalize();
        Program.Equal("custom settings: typed values are trimmed and kept, the name is what shows",
            (typed.BaseUrl, typed.Model, typed.DisplayName), ("https://example.test/anthropic", "my-model", "Work"));
        var nothing = new CustomModelSettings { Preset = CustomPreset.Other, BaseUrl = "", Model = "" };
        nothing.Normalize();
        var unknown = new CustomModelSettings { Preset = (CustomPreset)9 };
        unknown.Normalize();
        Program.Equal("custom settings: Other has nothing to fall back to, an unknown preset is the GLM one",
            (nothing.BaseUrl, nothing.Model, unknown.Preset), ((string?)null, (string?)null, CustomPreset.ZaiGlm));

        CustomModelSettings At(string address, CustomPreset preset = CustomPreset.ZaiGlm) => new() { Preset = preset, BaseUrl = address, Model = "m" };
        Program.Equal("custom settings: a key goes to https, or to http on this PC, and to nothing else",
            string.Join(" ", new[] { "https://api.z.ai/x", "http://localhost:11434", "http://127.0.0.1:1234/v1", "http://example.test/x", "ftp://example.test", "api.z.ai", "" }
                .Select(address => At(address).HasSafeAddress ? "y" : "n")),
            "y y y n n n n");
        Program.Equal("custom settings: what is missing comes in order: the address, the model, the key",
            (At("http://example.test").Missing(true), new CustomModelSettings { Model = "" }.Missing(true), At("https://api.z.ai").Missing(false),
                At("https://api.z.ai").Missing(true), blank.Missing(false)),
            ("Needs an https:// address (http:// only on this PC)", "Needs a model", "Needs a key", (string?)null, (string?)null));
    }

    /// <summary>The custom model in the agent settings: Claude Code's file, its own model, no effort; older settings load and it is never the default.</summary>
    private static void CustomAgentSettings()
    {
        var settings = new AgentSettings { ClaudePath = @"C:\bin\claude.exe", CodexPath = @"C:\bin\codex.exe", ClaudeEffort = "high", CodexEffort = "low" };
        settings.Custom.Model = "glm-4.7";
        Program.Equal("custom agent: it runs the file of Claude Code, with its own model and no effort",
            (settings.PathFor(AgentProviderKind.Custom), settings.ModelFor(AgentProviderKind.Custom), settings.EffortFor(AgentProviderKind.Custom)),
            (@"C:\bin\claude.exe", "glm-4.7", (string?)null));
        var levels = Enum.GetValues<AgentLevel>().Select(level => settings.Level(AgentProviderKind.Custom, level)).Distinct().ToArray();
        Program.Equal("custom agent: its three levels are one model without an effort", (levels.Length, levels[0].Model, levels[0].Effort), (1, "glm-4.7", (string?)null));
        settings.SetEffort(AgentProviderKind.Custom, "max");
        Program.Equal("custom agent: an effort set for it changes nobody's",
            (settings.ClaudeEffort, settings.CodexEffort, settings.EffortFor(AgentProviderKind.Custom)), ("high", "low", (string?)null));

        Program.Equal("custom agent: the numbers of older settings stay",
            ((int)AgentProviderKind.ClaudeCode, (int)AgentProviderKind.Codex, (int)AgentProviderKind.Custom), (0, 1, 2));
        var older = JsonSerializer.Deserialize<AgentSettings>("""{"DefaultProvider":1,"ClaudeModel":"opus"}""")!;
        older.Normalize();
        Program.Equal("custom agent: settings of an older Link load with the GLM preset and Codex still the default",
            (older.DefaultProvider, older.Custom.Preset, older.Custom.BaseUrl, older.Custom.Model, older.OwnAnswersOnCustom, older.ClaudeModel),
            (AgentProviderKind.Codex, CustomPreset.ZaiGlm, "https://api.z.ai/api/anthropic", "glm-4.7-flash", false, "opus"));
        var hand = JsonSerializer.Deserialize<AgentSettings>("""{"DefaultProvider":2,"Custom":null}""")!;
        hand.Normalize();
        Program.Equal("custom agent: it is never the default, and a missing setup is the GLM one",
            (hand.DefaultProvider, hand.Custom.Preset), (AgentProviderKind.ClaudeCode, CustomPreset.ZaiGlm));

        var saved = new AgentSettings { OwnAnswersOnCustom = true };
        saved.Custom = new CustomModelSettings { Preset = CustomPreset.Ollama, Name = "Home", BaseUrl = "http://localhost:11434", Model = "gpt-oss:20b" };
        var back = JsonSerializer.Deserialize<AgentSettings>(JsonSerializer.Serialize(saved))!;
        Program.Equal("custom agent: the setup is saved with the settings",
            (back.OwnAnswersOnCustom, back.Custom.Preset, back.Custom.Name, back.Custom.BaseUrl, back.Custom.Model),
            (true, CustomPreset.Ollama, "Home", "http://localhost:11434", "gpt-oss:20b"));
        var copy = saved.Clone();
        copy.Custom.Model = "other";
        Program.Equal("custom agent: a copy has its own setup", (copy.OwnAnswersOnCustom, saved.Custom.Model, copy.Custom.Preset), (true, "gpt-oss:20b", CustomPreset.Ollama));
    }

    /// <summary>Link's own answers: on the custom model only when Settings asks and it is set up, and a session stays with an agent that reads it.</summary>
    private static void CustomOwnAnswers()
    {
        var on = new AgentSettings { OwnAnswersOnCustom = true };
        var off = new AgentSettings();
        Program.Equal("own answers: a map goes to the custom model when Settings asks and it is set up",
            on.OwnAnswers(AgentProviderKind.ClaudeCode, inSession: false, customConfigured: true), (AgentProviderKind.Custom, (string?)"glm-4.7-flash", (string?)null));
        Program.Equal("own answers: not set up, it stays on Claude's Quick setup",
            on.OwnAnswers(AgentProviderKind.ClaudeCode, inSession: false, customConfigured: false), (AgentProviderKind.ClaudeCode, (string?)"claude-sonnet-5-5", (string?)"medium"));
        Program.Equal("own answers: asked for nothing, a map is Quick on the project's agent",
            off.OwnAnswers(AgentProviderKind.Codex, inSession: false, customConfigured: true), (AgentProviderKind.Codex, (string?)null, (string?)"low"));
        Program.Equal("own answers: a project whose last task used the custom model does not send its map there unasked",
            off.OwnAnswers(AgentProviderKind.Custom, inSession: false, customConfigured: true), (AgentProviderKind.ClaudeCode, (string?)"claude-sonnet-5-5", (string?)"medium"));
        Program.Equal("own answers: a skill or note of a Claude Code session stays with Claude Code, which wrote the session",
            on.OwnAnswers(AgentProviderKind.ClaudeCode, inSession: true, customConfigured: true).Provider, AgentProviderKind.ClaudeCode);
        Program.Equal("own answers: a Codex session is read by Codex only",
            on.OwnAnswers(AgentProviderKind.Codex, inSession: true, customConfigured: true).Provider, AgentProviderKind.Codex);
        Program.Equal("own answers: a custom model's session goes on there",
            off.OwnAnswers(AgentProviderKind.Custom, inSession: true, customConfigured: true), (AgentProviderKind.Custom, (string?)"glm-4.7-flash", (string?)null));
    }

    /// <summary>What the custom model's Claude Code is given: the server, the key as the token, the model for every name, the budget.</summary>
    private static void CustomProviderEnvironment()
    {
        var settings = new AgentSettings { ContextBudget = 150_000 };
        string? key = "sk-test-0123";
        var custom = new CustomModelProvider(() => settings, () => key, _ => { });
        var task = new AgentTask { Provider = AgentProviderKind.Custom, Workspace = @"C:\Work", Prompt = "Explain", Model = "glm-4.7", Effort = "high", ContextBudget = 150_000 };
        var variables = custom.EnvironmentVariables(task);
        string[] names =
        [
            "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
            "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL"
        ];
        Program.Equal("custom provider: the server, the key as the token, no Anthropic key, the chosen model for every name",
            string.Join(";", names.Select(name => $"{name}={variables[name]}")),
            "ANTHROPIC_BASE_URL=https://api.z.ai/api/anthropic;ANTHROPIC_AUTH_TOKEN=sk-test-0123;ANTHROPIC_API_KEY=;ANTHROPIC_MODEL=glm-4.7;" +
            "ANTHROPIC_DEFAULT_OPUS_MODEL=glm-4.7;ANTHROPIC_DEFAULT_SONNET_MODEL=glm-4.7;ANTHROPIC_DEFAULT_HAIKU_MODEL=glm-4.7");
        Program.Equal("custom provider: only the model's own traffic, a long timeout, and the context budget as for Claude Code",
            (variables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"], variables["API_TIMEOUT_MS"], variables["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]), ("1", "600000", "150000"));
        Program.Equal("custom provider: a task with no model runs on Settings' model",
            custom.EnvironmentVariables(task with { Model = null })["ANTHROPIC_MODEL"], "glm-4.7-flash");
        var wide = task with { ContextBudget = 550_000 };
        var glmWindow = custom.EnvironmentVariables(wide)["CLAUDE_CODE_AUTO_COMPACT_WINDOW"];
        settings.Custom = new CustomModelSettings { Preset = CustomPreset.DeepSeek };
        settings.Custom.Normalize();
        Program.Equal("custom provider: a budget over the server model's window compacts by the window",
            (glmWindow, custom.EnvironmentVariables(wide)["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]), ("200000", "128000"));

        settings.Custom = new CustomModelSettings { Preset = CustomPreset.Ollama, BaseUrl = "http://localhost:11434", Model = "qwen3-coder" };
        key = null;
        var local = custom.EnvironmentVariables(task with { Model = null });
        Program.Equal("custom provider: Ollama is called with a word for a token, and needs no key",
            (local["ANTHROPIC_BASE_URL"], local["ANTHROPIC_AUTH_TOKEN"], local["ANTHROPIC_MODEL"], custom.Configured), ("http://localhost:11434", "ollama", "qwen3-coder", true));

        settings.Custom = new CustomModelSettings();
        Program.Equal("custom provider: GLM is set up with a key, not without, nor at an address a key must not go to",
            (custom.Configured, Set(settings, "sk-1"), Set(settings, null), CustomWithAddress(settings, "http://example.test")), (false, true, false, false));

        bool Set(AgentSettings each, string? value)
        {
            key = value;
            return custom.Configured;
        }

        bool CustomWithAddress(AgentSettings each, string address)
        {
            key = "sk-1";
            each.Custom.BaseUrl = address;
            return custom.Configured;
        }
    }

    /// <summary>Claude Code's arguments for the custom model: its model, never an effort, and no pictures it cannot read.</summary>
    private static void CustomProviderArguments()
    {
        var settings = new AgentSettings();
        var custom = new CustomModelProvider(() => settings, () => "sk-test-0123", _ => { });
        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var task = new AgentTask
        {
            Provider = AgentProviderKind.Custom, Workspace = @"C:\Work", Prompt = "Explain", Access = AgentAccess.Do, Model = "glm-4.7", Effort = "high"
        };
        var arguments = custom.Arguments(task);
        Program.Equal("custom provider: --model is passed and --effort never, the rest is Claude Code's",
            (arguments[arguments.ToList().IndexOf("--model") + 1], arguments.Contains("--effort"), arguments.SequenceEqual(claude.Arguments(task with { Effort = null }))),
            ("glm-4.7", false, true));
        Program.Equal("custom provider: a task with no model still names Settings' model",
            custom.Arguments(task with { Model = null }).Zip(custom.Arguments(task with { Model = null }).Skip(1), (name, value) => name == "--model" ? value : null).OfType<string>().SingleOrDefault(),
            "glm-4.7-flash");

        var shot = task with { Images = [@"C:\Shots\a.png"] };
        Program.Equal("custom provider: pictures it cannot read are left out, and the prompt says so",
            (custom.Arguments(shot).Contains("--add-dir"), custom.Input(shot), claude.Input(shot).Contains("a.png")),
            (false, "Explain\n\nScreenshots were left out: this model does not read images.", true));
        Program.Equal("custom provider: without pictures the prompt is as it was", custom.Input(task), "Explain");
        var started = custom.StandardInput(shot);
        Program.Equal("custom provider: a new session is given the rules and the line, not the picture, and no key",
            (started.Contains("# Agent rules"), started.Contains("Screenshots were left out"), started.Contains("a.png"), started.Contains("sk-test-0123")),
            (true, true, false, false));
        Program.Equal("custom provider: the key is in no argument",
            string.Join(' ', arguments).Contains("sk-test-0123"), false);
    }

    /// <summary>A custom model that is not set up, or has no Claude Code to run through, does not start and says why.</summary>
    private static async Task CustomProviderRefusesAsync()
    {
        var settings = new AgentSettings { ClaudePath = Environment.ProcessPath };
        string? key = null;
        var custom = new CustomModelProvider(() => settings, () => key, _ => { });
        var task = new AgentTask { Provider = AgentProviderKind.Custom, Workspace = AppContext.BaseDirectory, Prompt = "x" };
        Program.Equal("custom provider refuses: it is not ready without its key", custom.Configured, false);
        var message = string.Empty;
        try
        {
            await custom.RunAsync(task, new SyncProgress(_ => { }), CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            message = exception.Message;
        }

        Program.Equal("custom provider refuses: no key, nothing starts, and the message says what is missing",
            message, "GLM (Z.ai): Needs a key. Settings → Agents has the setup.");

        key = "sk-test-0123";
        settings.ClaudePath = Path.Combine(Path.GetTempPath(), "no-such-folder", "claude.exe");
        message = string.Empty;
        try
        {
            await custom.RunAsync(task, new SyncProgress(_ => { }), CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            message = exception.Message;
        }

        Program.Equal("custom provider refuses: without Claude Code it says the custom model runs through it, and not the key",
            (message.StartsWith("Claude Code is not found, and GLM (Z.ai) runs through it.", StringComparison.Ordinal), message.Contains("sk-test-0123")), (true, false));
        Program.Equal("custom provider refuses: limits it has none of", (await custom.ReadLimitsAsync(CancellationToken.None)).Count, 0);
    }

    /// <summary>The Console shows that there is a key and never what it is; Claude Code's dollars for another server's model are not recorded.</summary>
    private static void CustomKeyStaysOut()
    {
        var settings = new AgentSettings { ContextBudget = 150_000 };
        var custom = new CustomModelProvider(() => settings, () => "sk-test-0123", _ => { });
        var task = new AgentTask { Provider = AgentProviderKind.Custom, Workspace = @"C:\Work", Prompt = "Explain", Model = "glm-4.7", ContextBudget = 150_000 };
        var console = new AgentConsole();
        console.Sent("GLM (Z.ai) · new session", "claude", custom.Arguments(task), task.Workspace, custom.EnvironmentVariables(task), custom.StandardInput(task));
        var text = console.After(0).Text;
        Program.Equal("console: the token is shown as ***, an empty key as empty, and the rest as it is",
            (text.Contains("sk-test-0123"), text.Contains("  + ANTHROPIC_AUTH_TOKEN=***\n") || text.Contains("  + ANTHROPIC_AUTH_TOKEN=***\r\n"),
                text.Contains("  + ANTHROPIC_API_KEY=\n") || text.Contains("  + ANTHROPIC_API_KEY=\r\n"),
                text.Contains("ANTHROPIC_BASE_URL=https://api.z.ai/api/anthropic"), text.Contains("CLAUDE_CODE_AUTO_COMPACT_WINDOW=150000")),
            (false, true, true, true, true));

        var priced = Parse(new ClaudeEventParser(), "claude-do.jsonl").Parser.Stats.CostUsd;
        var unpriced = Parse(custom.CreateParser(task), "claude-do.jsonl").Parser.Stats.CostUsd;
        Program.Equal("custom provider: the dollars Claude Code guesses for another server are not counted",
            (priced is > 0, unpriced), (true, (decimal?)null));
    }

    /// <summary>Start over, when the note is written by the custom model: it asks in the Claude Code session, and the new session is the run's own agent's.</summary>
    private static async Task CustomStartOverAsync()
    {
        var claude = new ScriptedProvider((_, _) => new AgentResult { SessionId = "s2", Succeeded = true });
        var custom = new ScriptedProvider((_, _) => new AgentResult { SessionId = "s1", Succeeded = true, Summary = "Tried it twice.\n" }, AgentProviderKind.Custom);
        var agents = new AgentCoordinator([claude, custom], new AgentChanges(Path.GetTempPath()), _ => { });
        var origin = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Fix it", Model = "claude-opus-5-5", Effort = "high" };
        var before = new WorkspaceBefore(origin.Workspace, false, new Dictionary<string, string>(), null, DateTime.UtcNow);
        var run = new AgentRun(origin with { ResumeSessionId = "s1" }, before, new AgentResult { SessionId = "s1" }, []);
        var fresh = (await agents.StartOverAsync(run, origin, ("glm-4.7-flash", null), CancellationToken.None, AgentProviderKind.Custom))!.Value;
        var ask = custom.Tasks.Single();
        Program.Equal("custom start over: the custom model is asked in the session, on its own model and no effort",
            (claude.Tasks.Count, ask.Provider, ask.ResumeSessionId, ask.Model, ask.Effort, ask.Access), (0, AgentProviderKind.Custom, "s1", "glm-4.7-flash", (string?)null, AgentAccess.Analyze));
        Program.Equal("custom start over: the new session is the run's own agent's, with the note",
            (fresh.Task.Provider, fresh.Task.ResumeSessionId, fresh.Note), (AgentProviderKind.ClaudeCode, (string?)null, "Tried it twice."));
    }

    /// <summary>What each agent can do and who sees the code; a provider that says nothing is taken for a subscription agent.</summary>
    private static void CustomCapabilities()
    {
        var settings = new AgentSettings();
        IAgentProvider claude = new ClaudeCodeProvider(() => settings, _ => { });
        IAgentProvider codex = new CodexProvider(() => settings, _ => { });
        IAgentProvider custom = new CustomModelProvider(() => settings, () => null, _ => { });
        IAgentProvider unnamed = ScriptedProvider.Answering("x");
        Program.Equal("capabilities: the subscription agents read images, take an effort, and say who sees the code",
            (claude.Capabilities, codex.Capabilities), (new AgentCapabilities(true, true, true, "Anthropic"), new AgentCapabilities(true, true, true, "OpenAI")));
        Program.Equal("capabilities: the custom model has no subscription, no effort, and the preset's server sees the code",
            custom.Capabilities, new AgentCapabilities(false, false, false, "Z.ai"));
        settings.Custom = new CustomModelSettings { Preset = CustomPreset.Ollama };
        Program.Equal("capabilities: Ollama keeps the code on this PC", custom.Capabilities.SeesCode, "this PC");
        Program.Equal("capabilities: a provider that says nothing is a subscription agent that is ready",
            (unnamed.Capabilities, unnamed.Configured), (AgentCapabilities.Usual, true));

        settings.Custom = new CustomModelSettings { Preset = CustomPreset.DeepSeek, Name = "Cheap one", Model = "my-model" };
        Program.Equal("custom provider: named as Settings names it, with the preset's models and the chosen one, and no efforts",
            (custom.Name, custom.Kind, string.Join(",", custom.Models.Select(model => model.Value)), custom.Efforts.Count, custom.EffortsOf("my-model").Count, custom.OwnSetup),
            ("Cheap one", AgentProviderKind.Custom, "deepseek-chat,deepseek-reasoner,my-model", 0, 0, ((string?)"my-model", (string?)null)));
    }

    /// <summary>The state's known problems name the agent as Settings does, the custom model included.</summary>
    private static void CustomTaskStateNames()
    {
        var workspace = TempFolder();
        try
        {
            var record = new AgentTaskRecord
            {
                Goal = "Fix it",
                Workspace = workspace,
                Runs =
                [
                    new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Failed after 1:00" },
                    new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Failed after 2:00" },
                    new AgentRunRecord { Provider = AgentProviderKind.Custom, Outcome = "Failed after 3:00" }
                ]
            };
            var plain = TaskState.Build(record, null, workspace).Split('\n').Where(line => line.Contains(" run ")).Select(line => line.Split(" run ")[0]).ToArray();
            Program.Equal("task state: the agents are named, the custom model without Settings as such", string.Join("|", plain), "- Claude Code|- Codex|- Custom model");
            var named = TaskState.CompactBrief(record, null, workspace, kind => kind == AgentProviderKind.Custom ? "GLM (Z.ai)" : kind.DefaultName());
            Program.Equal("task state: with Settings' names, the custom model is its own", named.Contains("- GLM (Z.ai) run ") && !named.Contains("Custom model"), true);
        }
        finally
        {
            DeleteFolder(workspace);
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

    private static string Create(string id, string subject, string doing) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"{{{id}}}","name":"TaskCreate","input":{"subject":"{{{subject}}}","description":"d","activeForm":"{{{doing}}}"}}]}}""";

    private static string Created(string id, string text) =>
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"{{{id}}}","content":"{{{text}}}"}]}}""";

    private static string Update(string input) =>
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u","name":"TaskUpdate","input":""" + input + """}]}}""";

    /// <summary>A plan in a line, as the panel marks it: ✓ done, ◐ under way, ○ to do, then the task's number.</summary>
    private static string PlanLine(IEnumerable<AgentPlanItem> plan) =>
        string.Join(", ", plan.Select(item => (item.Done ? "✓" : item.Active ? "◐" : "○") + item.Number));

    /// <summary>Claude Code's TaskCreate and TaskUpdate become the plan; a task is numbered by its result.</summary>
    private static void PlanTaskTools()
    {
        var claude = new ClaudeEventParser();
        IReadOnlyList<AgentPlanItem> Plan(string line) => claude.Read(line).LastOrDefault(agentEvent => agentEvent.Kind == AgentEventKind.Plan)?.Plan ?? [];
        Program.Equal("tasks: a TaskCreate is no step, and its task joins the plan when the result numbers it",
            (claude.Read(Create("c1", "Find the cause", "Finding the cause")).Count, PlanLine(Plan(Created("c1", "Task #1 created successfully: Find the cause")))),
            (0, "○1"));
        claude.Read(Create("c2", "Fix it", "Fixing it"));
        claude.Read(Create("c3", "Run the check", "Running the check"));
        Plan(Created("c2", "Task #2 created successfully: Fix it"));
        Program.Equal("tasks: three created, three numbered", PlanLine(Plan(Created("c3", "Task #3 created successfully: Run the check"))), "○1, ○2, ○3");
        Program.Equal("tasks: TaskUpdate to in_progress marks the task under way, with the agent's words for it",
            (PlanLine(Plan(Update("""{"taskId":"2","status":"in_progress"}"""))), Plan(Update("""{"taskId":"2"}"""))[1].Doing), ("○1, ◐2, ○3", "Fixing it"));
        Program.Equal("tasks: completed", PlanLine(Plan(Update("""{"taskId":"1","status":"completed"}"""))), "✓1, ◐2, ○3");
        var final = Plan(Update("""{"taskId":"3","status":"deleted"}"""));
        Program.Equal("tasks: deleted leaves the plan", (PlanLine(final), string.Join(" / ", final.Select(item => item.Text))), ("✓1, ◐2", "Find the cause / Fix it"));
        Program.Equal("tasks: a new subject and words replace the old ones, a status that is not known changes nothing",
            (Plan(Update("""{"taskId":2,"subject":"Fix it for good","activeForm":"Fixing for good","status":"blocked"}""")) is [_, { Text: "Fix it for good", Doing: "Fixing for good", Active: true }]),
            true);
        Program.Equal("tasks: a task the plan does not know, reading the list and a failed create change nothing",
            (claude.Read(Update("""{"taskId":"9","status":"completed"}""")).Count,
                claude.Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"l","name":"TaskList","input":{}},{"type":"tool_use","id":"g","name":"TaskGet","input":{"taskId":"1"}}]}}""").Count,
                claude.Read(Create("c4", "Broken", "Breaking")).Count + claude.Read("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"c4","is_error":true,"content":"no"}]}}""").Count),
            (0, 0, 0));

        var bare = new ClaudeEventParser();
        bare.Read(Create("a", "First", "Firsting"));
        bare.Read(Create("b", "Second", "Seconding"));
        bare.Read(Created("a", "Created."));
        var counted = bare.Read(Created("b", "Created."));
        Program.Equal("tasks: a result that names no number gives the next in creation order", PlanLine(counted.Single().Plan), "○1, ○2");
        Program.Equal("tasks: the plan event says how many are done",
            claude.Read(Update("""{"taskId":"2","status":"completed"}""")).Single().Text, "Plan: 2 of 2 done");

        var todo = new ClaudeEventParser().Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Fix it","status":"in_progress","activeForm":"Fixing it"}]}}]}}""");
        Program.Equal("tasks: TodoWrite still gives the plan, with the agent's words for the step under way",
            todo.Single().Plan is [{ Text: "Fix it", Active: true, Doing: "Fixing it" }], true);
    }

    /// <summary>A session that goes on has its tasks already; TaskUpdate names them by the numbers they were created with.</summary>
    private static void PlanTaskToolsAcrossRuns()
    {
        var earlier = new[] { new AgentPlanItem("Find the cause", true, false, null, 1), new AgentPlanItem("Fix it", false, true, "Fixing it", 4) };
        var resumed = new ClaudeEventParser(plan: earlier);
        var plan = resumed.Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u","name":"TaskUpdate","input":{"taskId":"4","status":"completed"}}]}}""").Single().Plan;
        Program.Equal("tasks: the next run's TaskUpdate finds the task of the run before", PlanLine(plan), "✓1, ✓4");
        var unnumbered = new ClaudeEventParser(plan: [new AgentPlanItem("Find the cause", false, false), new AgentPlanItem("Fix it", false, false)]);
        Program.Equal("tasks: a plan without numbers (TodoWrite's, Codex's) is numbered in order",
            PlanLine(unnumbered.Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u","name":"TaskUpdate","input":{"taskId":"2","status":"in_progress"}}]}}""").Single().Plan),
            "○1, ◐2");
        var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = "w", Prompt = "p", Plan = earlier };
        const string complete = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u","name":"TaskUpdate","input":{"taskId":"4","status":"completed"}}]}}""";
        Program.Equal("tasks: Claude Code's and the custom model's runs both start from the plan of the session",
            (PlanLine(new ClaudeCodeProvider(() => new AgentSettings(), _ => { }).CreateParser(task).Read(complete).Single().Plan),
                PlanLine(new CustomModelProvider(() => new AgentSettings(), () => "key", _ => { }).CreateParser(task).Read(complete).Single().Plan)),
            ("✓1, ✓4", "✓1, ✓4"));
    }

    /// <summary>Plans saved by an earlier Link have no words for a step under way and no numbers.</summary>
    private static void PlanKeepsOlderRecords()
    {
        var older = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Fix it","Workspace":"C:\\Work","Plan":[{"Text":"Find it","Done":true,"Active":false},{"Text":"Fix it","Done":false,"Active":true}]}""")!;
        Program.Equal("plan: a record of an earlier Link loads with no words and no numbers",
            older.Plan is [{ Text: "Find it", Done: true, Doing: null, Number: null }, { Active: true, Doing: null }], true);
        var saved = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(new AgentTaskRecord
        {
            Goal = "Fix it", Workspace = "C:\\Work", Plan = [new AgentPlanItem("Fix it", false, true, "Fixing it", 2)]
        }))!;
        Program.Equal("plan: the words and the number are saved", saved.Plan.Single(), new AgentPlanItem("Fix it", false, true, "Fixing it", 2));
    }

    /// <summary>The steps between the agent's words are summed up as the agents' own apps do.</summary>
    private static void StepGroupsSummary()
    {
        string[] steps =
        [
            "$ Bash: git status", "$ Bash: dotnet build", "$ Bash: dotnet test", "≡ Read: a.cs", "≡ Read: b.cs", "≡ Read: a.cs",
            "✎ Edit: a.cs", "✎ Edit: a.cs", "⌕ Grep: Jump", "⌕ Glob: *.cs", "▸ ue-mcp: tools call", "▸ WebSearch: jump", "▸ Skill: map", "▸ Skill: rig",
            "Said something", "· Session started"
        ];
        Program.Equal("steps: commands, files read, files edited, searches and other tools in a line, a file twice is one",
            StepGroups.Summarize(steps), "Ran 3 commands, read 2 files, edited 1 file, searched 2, used 4 tools");
        Program.Equal("steps: one of each reads in the singular",
            StepGroups.Summarize(["$ Bash: ls", "≡ Read: a.cs", "✎ Edit: a.cs", "⌕ Grep: x", "▸ Skill: map"]), "Ran 1 command, read 1 file, edited 1 file, searched 1, used 1 tool");
        Program.Equal("steps: the line starts with what the group did first",
            StepGroups.Summarize(["≡ Read: a.cs", "▸ Skill: map"]), "Read 1 file, used 1 tool");
        Program.Equal("steps: the agent's words and notes are no tool call", (StepGroups.Summarize(["Said something", "· Session started", "⚠ Not allowed", "✗ Error"]), StepGroups.KindOf("$5 fee")), ("", (ToolKind?)null));
        Program.Equal("steps: a tool row of an earlier Link, marked ▸ only, counts as a tool used",
            StepGroups.Summarize(["▸ Bash: ls", "▸ Edit: a.cs", "▸ Read: a.cs"]), "Used 3 tools");
    }

    /// <summary>A tool step starts with the mark of what the call did, whichever agent made it.</summary>
    private static void StepMarks()
    {
        AgentEvent Tool(string name, string text, bool search = false) => new(AgentEventKind.Tool, text, name) { IsSearch = search };
        Program.Equal("steps: the marks by tool, Claude's and Codex's",
            string.Join('|', new[]
            {
                Tool("Bash", "Bash: ls"), Tool("PowerShell", "PowerShell: dir"), Tool("command", "dotnet test"), Tool("Read", "Read: a.cs", search: true),
                Tool("Grep", "Grep: x", search: true), Tool("command", "rg Jump", search: true), Tool("Edit", "Edit: a.cs"), Tool("Write", "Write: b.cs"),
                Tool("file_change", "Edit: c.cs"), Tool("mcp__ue-mcp__tools", "ue-mcp: tools"), Tool("mcp", "unreal-mcp: call_tool"), Tool("search", "Search: jump")
            }.Select(StepGroups.Step)),
            "$ Bash: ls|$ PowerShell: dir|$ dotnet test|≡ Read: a.cs|⌕ Grep: x|⌕ rg Jump|✎ Edit: a.cs|✎ Write: b.cs|✎ Edit: c.cs|▸ ue-mcp: tools|▸ unreal-mcp: call_tool|▸ Search: jump");
        var run = Parse(new ClaudeEventParser(), "claude-do.jsonl").Events.Where(agentEvent => agentEvent.Kind == AgentEventKind.Tool).Select(StepGroups.Step);
        Program.Equal("steps: a recorded run of Claude Code in a line (a search by rg counts as a search, not a command)",
            StepGroups.Summarize(run), "Ran 2 commands, read 1 file, edited 1 file, searched 3");
    }

    /// <summary>The header of a task: lines added and removed where git counts them, else the number of files.</summary>
    private static void TaskChangesCount()
    {
        string[] files = ["src/a.cs", "src/b.cs", "logo.png"];
        Program.Equal("changes: git's numbers add up, a binary file counts none",
            TaskChanges.Describe("12\t3\tsrc/a.cs\n169\t9\tsrc/b.cs\n-\t-\tlogo.png\n", files), ("+181 −12", "src/a.cs\nsrc/b.cs\nlogo.png"));
        Program.Equal("changes: without git's numbers, the files", TaskChanges.Describe(null, files).Text, "3 files");
        Program.Equal("changes: Perforce tells the kind of change per file, not lines", TaskChanges.Describe("edit\tsrc/a.cs\nadd\tsrc/b.cs", files).Text, "3 files");
        Program.Equal("changes: one file, none", (TaskChanges.Describe(null, ["a.cs"]).Text, TaskChanges.Describe(null, []).Text, TaskChanges.Describe("", []).Hint), ("1 file", "", ""));
        var many = Enumerable.Range(1, 25).Select(index => $"f{index}.cs").ToArray();
        Program.Equal("changes: the hint names twenty files and counts the rest",
            TaskChanges.Describe(null, many).Hint.Split('\n') is { Length: 21 } lines && lines[^1] == "… and 5 more", true);
        Program.Equal("changes: a nothing-changed answer gives no numbers", TaskChanges.Describe("0\t0\tsrc/a.cs", ["src/a.cs"]).Text, "+0 −0");
    }

    /// <summary>
    /// The project's stats: runs grouped by specialist, agent and model; a task is done where its last run did not fail,
    /// a stop is no failure, the averages leave out what a run did not tell, the steers count once in the last group.
    /// </summary>
    private static void StatsReport()
    {
        static AgentRunRecord RunOf(string? role, AgentProviderKind provider, string? model, string outcome, int seconds, long? input = null, long? output = null) =>
            new() { RoleId = role, Provider = provider, Model = model, Outcome = outcome, Duration = TimeSpan.FromSeconds(seconds), InputTokens = input, OutputTokens = output };
        static AgentTaskRecord TaskOf(AgentTaskStatus status, int steers, params AgentRunRecord[] runs)
        {
            var record = new AgentTaskRecord { Goal = "Goal", Workspace = "w", Status = status, Runs = [.. runs] };
            for (var index = 0; index < steers; index++) record.Add(AgentEntryKind.You, "a word");
            record.Add(AgentEntryKind.Check, "not a steer");
            return record;
        }

        const AgentProviderKind Claude = AgentProviderKind.ClaudeCode;
        const AgentProviderKind Codex = AgentProviderKind.Codex;
        var tasks = new[]
        {
            // Failed first, done on the second run; and failed outright; and finished by the user after a red check.
            TaskOf(AgentTaskStatus.Done, 2, RunOf("rigger", Claude, "opus", "Failed after 1:00", 60, 100_000, 10_000), RunOf("rigger", Claude, "opus", "Done in 3:00", 180, 200_000, 20_000)),
            TaskOf(AgentTaskStatus.Failed, 1, RunOf("rigger", Claude, "opus", "Failed after 2:00", 120)),
            TaskOf(AgentTaskStatus.Finished, 0, RunOf("rigger", Claude, "opus", "Check failed after 4:00", 240, 50_000)),
            TaskOf(AgentTaskStatus.Done, 0, RunOf("rigger", Codex, null, "Done in 1:00", 60, 30_000)),
            // A usage limit, then the other agent finished: the first is no failure, the steer is the last group's.
            TaskOf(AgentTaskStatus.Done, 1, RunOf(null, Claude, "sonnet", "Stopped at the usage limit after 0:30", 30), RunOf(null, Codex, null, "Done in 2:00", 120)),
            // A task of an earlier Link: no time, no tokens. And a task that never ran.
            TaskOf(AgentTaskStatus.Done, 0, RunOf(null, AgentProviderKind.Custom, "glm", "an earlier session", 0)),
            TaskOf(AgentTaskStatus.Stopped, 3)
        };
        string Role(string? id) => id switch { null => "General", "rigger" => "Rigger", _ => id };
        var rows = AgentStatsReport.For(tasks, kind => kind.DefaultName(), Role);
        Program.Equal("stats: role, agent, model | tasks, runs, done, failed | average time, tokens | steers; the busiest group first, then by names",
            string.Join("\n", rows.Select(row => $"{row.Role}|{row.Agent}|{row.Model}|{row.Tasks}|{row.Runs}|{row.Done}|{row.Failed}|{row.AverageTime:m\\:ss}|{row.AverageTokens}|{row.Steers}")),
            string.Join("\n",
                "Rigger|Claude Code|opus|3|4|1|3|2:30|126667|3",
                "General|Claude Code|sonnet|1|1|1|0|0:30||0",
                "General|Codex||1|1|1|0|2:00||1",
                "General|Custom model|glm|1|1|1|0|0:00||0",
                "Rigger|Codex||1|1|1|0|1:00|30000|0"));
        Program.Equal("stats: no tasks, no rows", AgentStatsReport.For([], kind => kind.DefaultName(), Role).Count, 0);
    }

    /// <summary>The inspector's sections from a workspace and a task: each says what the agent gets, and what is missing, without a model.</summary>
    private static void HarnessBuilt()
    {
        var workspace = TempFolder();
        try
        {
            var caps = new AgentCapabilities(true, true, true, "Anthropic");
            HarnessInput Input(AgentTaskRecord? task = null) => new(workspace, "Claude Code", "Opus 5.5 · Medium", "Link picked it: a usual task", caps, 400_000)
            {
                Task = task,
                ContextUsed = 182_000,
                ContextWindow = 1_000_000,
                CheckCommand = "dotnet test",
                ToolRules = ["Read", "Bash(git log:*)"]
            };
            static HarnessSection Section(HarnessView view, string title) => view.Sections.Single(section => section.Title == title);

            var bare = HarnessView.Build(Input());
            Program.Equal("harness: the sections, in order",
                string.Join("|", bare.Sections.Select(section => section.Title)),
                "Setup|Context|Rules|Role|Project memory|Task state|Skills|Injected context|Tools and MCP|Verification|Stats");
            Program.Equal("harness setup: the role (General), the agent, the model and effort, the effort's reason, who sees the code",
                (Section(bare, "Setup").Value, Section(bare, "Setup").Note),
                ("General · Claude Code · Opus 5.5 · Medium", "Effort: Link picked it: a usual task\nSubscription · code goes to Anthropic"));
            Program.Equal("harness setup: an API model says so",
                HarnessView.Build(Input() with { Capabilities = new AgentCapabilities(false, false, false, "Z.ai") }).Sections[0].Note,
                "Effort: Link picked it: a usual task\nAPI, no subscription · code goes to Z.ai");
            Program.Equal("harness context: what the session holds against the budget, the budget told",
                (Section(bare, "Context").Value, Section(bare, "Context").Note!.StartsWith("Budget 400k:", StringComparison.Ordinal)), ("182k / 400k (46%)", true));
            Program.Equal("harness context: nothing told yet",
                Section(HarnessView.Build(Input() with { ContextUsed = null }), "Context").Value, "Not told yet");
            Program.Equal("harness rules: the file beside Link.exe, to open",
                (Section(bare, "Rules").Value, Section(bare, "Rules").FilePath), ("agent-rules.md", AgentRules.File));

            Program.Equal("harness: nothing yet without a task",
                (Section(bare, "Task state").Value, Section(bare, "Task state").Detail, Section(bare, "Injected context").Value, Section(bare, "Injected context").ShowsConsole,
                 Section(bare, "Role").Value),
                ("No task yet", (string?)null, "Nothing yet", true, "General — no role"));

            // Project memory and skills, MCP, check.
            Program.Equal("harness memory: a map that is not there is missing, and not opened",
                (Section(bare, "Project memory").Value, Section(bare, "Project memory").FilePath), ("docs/map.md · missing", (string?)null));
            Directory.CreateDirectory(Path.Combine(workspace, "docs"));
            File.WriteAllText(ProjectMap.PathIn(workspace), "# Map");
            Program.Equal("harness memory: a map that is there opens",
                (Section(HarnessView.Build(Input()), "Project memory").Value, Section(HarnessView.Build(Input()), "Project memory").FilePath),
                ("docs/map.md · exists", ProjectMap.PathIn(workspace)));
            Program.Equal("harness memory: no workspace",
                Section(HarnessView.Build(Input() with { Workspace = null }), "Project memory").Value, "No workspace chosen");

            Program.Equal("harness skills: none",
                Section(bare, "Skills").Value, "None in .agents/skills");
            WriteSkill(workspace, "jump-tuning", "Tune the jump.");
            WriteSkill(workspace, "tidy-up", "Tidy.");
            var task = new AgentTaskRecord
            {
                Goal = "Fix the jump",
                Brief = "Fix the jump.",
                Workspace = workspace,
                RoleId = "rigger-animator",
                Skills = ["jump-tuning"],
                LastCheck = "dotnet test: failed with exit code 1\nerror CS1002"
            };
            task.Add(AgentEntryKind.HandOff, TaskState.Compacted(180_000));
            task.Add(AgentEntryKind.HandOff, "Specialist: General");
            task.Add(AgentEntryKind.HandOff, TaskState.Compacted(null));
            var open = HarnessView.Build(Input(task));
            var skills = Section(open, "Skills");
            Program.Equal("harness skills: the count, the ones to read first marked, with their descriptions",
                (skills.Value, skills.Detail), ("2 skills · 1 to read first", "jump-tuning (read first): Tune the jump.\ntidy-up: Tidy."));
            Program.Equal("harness role: the specialist's name and description, its file to open",
                (Section(open, "Role").Value, Section(open, "Role").Note!.Length > 0, Section(open, "Role").FilePath!.EndsWith("rigger-animator.md", StringComparison.Ordinal)),
                ("Rigger / Animator", true, true));
            Program.Equal("harness role: a role whose file is gone is named by its file",
                Section(HarnessView.Build(Input(new AgentTaskRecord { Goal = "g", Workspace = workspace, RoleId = "gone" })), "Role").Value, "gone (its file is gone)");
            Program.Equal("harness context: the compactions in the task's history are counted",
                Section(open, "Context").Note!.EndsWith("\nCompacted 2 times", StringComparison.Ordinal), true);
            Program.Equal("harness task state: the state Compact starts from",
                Section(open, "Task state").Detail!.StartsWith("# Task state\nGoal: Fix the jump\n", StringComparison.Ordinal), true);
            Program.Equal("harness injected context: the brief Link gave, to read, and the Console",
                (Section(open, "Injected context").Value, Section(open, "Injected context").Detail, Section(open, "Injected context").ShowsConsole),
                ("The brief Link gave: 13 characters", "Fix the jump.", true));

            Program.Equal("harness tools: no .mcp.json, no servers, nothing to open; the access and the project's rules",
                (Section(open, "Tools and MCP").Value, Section(open, "Tools and MCP").FilePath, Section(open, "Tools and MCP").Note, Section(open, "Tools and MCP").Detail),
                ("No MCP servers", (string?)null, "Access: edits: other commands ask you\nTool rules the project allows: 2", "Read\nBash(git log:*)"));
            File.WriteAllText(Path.Combine(workspace, ".mcp.json"), "{\"mcpServers\": {\"unreal\": {\"command\": \"node\"}, \"blender\": {\"url\": \"http://localhost:1\"}}}");
            var tools = Section(HarnessView.Build(Input(task) with { AutoApprove = true }), "Tools and MCP");
            Program.Equal("harness tools: the MCP servers by the workspace's .mcp.json, auto access",
                (tools.Value, tools.FilePath, tools.Note!.Split('\n')[0]),
                ("MCP: unreal, blender", Path.Combine(workspace, ".mcp.json"), "Access: auto: edits and commands without asking"));
            Program.Equal("harness tools: read only",
                Section(HarnessView.Build(Input() with { Access = AgentAccess.Analyze }), "Tools and MCP").Note!.Split('\n')[0], "Access: read only");
            File.WriteAllText(Path.Combine(workspace, ".mcp.json"), "{ not json");
            Program.Equal("harness tools: a .mcp.json that cannot be read gives no servers",
                Section(HarnessView.Build(Input()), "Tools and MCP").Value, "No MCP servers");

            var check = Section(open, "Verification");
            Program.Equal("harness verification: the command, the check as it last ran, its output behind a toggle",
                (check.Value, check.Note, check.Detail), ("dotnet test", "Last: dotnet test: failed with exit code 1", "dotnet test: failed with exit code 1\nerror CS1002"));
            var noCheck = Section(HarnessView.Build(Input() with { CheckCommand = null }), "Verification");
            Program.Equal("harness verification: no command, you check the result",
                (noCheck.Value, noCheck.Note!.StartsWith("A check command", StringComparison.Ordinal)), ("None — you check the result", true));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The words of the inspector that no file decides: the effort's reason, the stats' rows, the compaction count.</summary>
    private static void HarnessWords()
    {
        Program.Equal("harness effort: Link's reason, Link's pick to come, the pin, the CLI's own, a model without an effort",
            string.Join("|",
                HarnessView.EffortWhy(true, true, "a usual task", "medium"), HarnessView.EffortWhy(true, true, null, null), HarnessView.EffortWhy(true, false, null, "high"),
                HarnessView.EffortWhy(true, false, null, null), HarnessView.EffortWhy(false, true, "x", "high")),
            "Link picked it: a usual task|Link picks it for each run|pinned on the card|the CLI's own|this model has no effort setting");

        var caps = AgentCapabilities.Usual;
        HarnessSection StatsOf(params StatsRow[] rows) =>
            HarnessView.Build(new HarnessInput(null, "Codex", "its own model", "the CLI's own", caps, 550_000) { Stats = rows }).Sections.Single(section => section.Title == "Stats");
        Program.Equal("harness stats: none yet", StatsOf().Value, "No runs yet");
        var busy = StatsOf(
            new StatsRow("Rigger", "Claude Code", "opus", 3, 4, 1, 3, TimeSpan.FromSeconds(150), 126_667, 3),
            new StatsRow("General", "Codex", null, 1, 1, 1, 0, TimeSpan.Zero, null, 0));
        Program.Equal("harness stats: the runs in all, each row with what it has to tell",
            (busy.Value, busy.Note), ("5 runs in this project",
                "Rigger · Claude Code · opus — 3 tasks, 1 done, 3 failed, 2:30 a run, 127k tokens a run, 3 steers\nGeneral · Codex · its own model — 1 task, 1 done"));
        var many = StatsOf([.. Enumerable.Range(1, 8).Select(index => new StatsRow("R" + index, "Codex", null, 1, 1, 1, 0, TimeSpan.Zero, null, 0))]);
        Program.Equal("harness stats: only the first five rows show", (many.Value, many.Note!.Split('\n').Length), ("8 runs in this project", 5));

        var task = new AgentTaskRecord { Goal = "g", Workspace = "w" };
        task.Add(AgentEntryKind.HandOff, "Back to Claude Code, its own session");
        Program.Equal("compact count: only the compactions of the history", TaskState.CompactCount(task), 0);
        task.Add(AgentEntryKind.HandOff, TaskState.Compacted(null));
        Program.Equal("compact count: one", TaskState.CompactCount(task), 1);
        Program.Equal("context: the usage is the line without its word", AgentContext.Line(182_000, 1_000_000, 400_000), "Context " + AgentContext.Usage(182_000, 1_000_000, 400_000));
    }

    /// <summary>The card's muted line says the context budget, the skills and the MCP servers the run gets; with none of them it says so.</summary>
    private static void HarnessCardLine()
    {
        Program.Equal("harness card line: the budget, the skills and the MCP servers",
            HarnessView.CardLine(400_000, 5, ["unreal", "blender"]), "Context budget 400k · 5 skills · MCP: unreal, blender");
        Program.Equal("harness card line: one skill; no skills and no servers",
            (HarnessView.CardLine(550_000, 1, ["unreal"]), HarnessView.CardLine(1_000_000, 0, [])),
            ("Context budget 550k · 1 skill · MCP: unreal", "Context budget 1M · no skills · no MCP servers"));
    }

    /// <summary>The Studio window's place and inspector are saved with the settings; an older settings.json has the defaults.</summary>
    private static void StudioSettingsKept()
    {
        static (double?, double?, double, double, bool, bool) Of(StudioSettings studio) =>
            (studio.Left, studio.Top, studio.Width, studio.Height, studio.Maximized, studio.InspectorOpen);

        var older = JsonSerializer.Deserialize<AppSettings>("""{"Opacity":0.7}""")!;
        older.Normalize();
        Program.Equal("studio settings: an older settings.json opens the Studio centered, 1100 x 720, with the inspector open",
            Of(older.Studio), ((double?)null, (double?)null, 1100.0, 720.0, false, true));
        var missing = JsonSerializer.Deserialize<AppSettings>("""{"Studio":null}""")!;
        missing.Normalize();
        Program.Equal("studio settings: a missing section is the defaults", Of(missing.Studio), Of(older.Studio));

        var kept = new AppSettings();
        kept.Studio.Remember(120, 80, 1400, 900, maximized: true);
        kept.Studio.InspectorOpen = false;
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(kept))!;
        back.Normalize();
        Program.Equal("studio settings: the place, the size, maximized and a folded inspector are saved",
            Of(back.Studio), ((double?)120.0, (double?)80.0, 1400.0, 900.0, true, false));
        Program.Equal("studio settings: a place on a screen left of the first is a place",
            Of(Remembered(-1500, 40, 1000, 700, false)), ((double?)-1500.0, (double?)40.0, 1000.0, 700.0, false, true));
    }

    /// <summary>A place that is no place and a size that is none fall back to the centered default, a small one to the least.</summary>
    private static void StudioSettingsOdd()
    {
        Program.Equal("studio settings: a place with a number missing or not a number is none",
            (Remembered(double.NaN, 50, 1000, 700, false).Left, Remembered(50, double.PositiveInfinity, 1000, 700, false).Top,
                new StudioSettings { Left = 10 }.Top),
            ((double?)null, (double?)null, (double?)null));
        Program.Equal("studio settings: a place beyond any screen is none, with its other number",
            (Remembered(500_000, 10, 1000, 700, false).Left, Remembered(500_000, 10, 1000, 700, false).Top), ((double?)null, (double?)null));
        Program.Equal("studio settings: a size under the least is the least, over the most the most, none the default",
            (Remembered(0, 0, 10, 100, false).Width, Remembered(0, 0, 10, 100, false).Height, Remembered(0, 0, 99_999, 99_999, false).Width,
                Remembered(0, 0, 99_999, 99_999, false).Height, Remembered(0, 0, double.NaN, -5, false).Width, Remembered(0, 0, double.NaN, -5, false).Height),
            (760.0, 480.0, 8000.0, 4320.0, 1100.0, 720.0));
    }

    private static StudioSettings Remembered(double left, double top, double width, double height, bool maximized)
    {
        var studio = new StudioSettings();
        studio.Remember(left, top, width, height, maximized);
        return studio;
    }

    /// <summary>The cheaper hint: a plan to carry out, or the custom model's own record for the role; never for the custom model itself or when it is not set up.</summary>
    private static void CheaperHints()
    {
        static AgentTaskRecord TaskOf(AgentProviderKind? provider) => new()
        {
            Goal = "Goal",
            Workspace = "w",
            Runs = provider is { } kind ? [new AgentRunRecord { Provider = kind }] : []
        };
        static StatsRow Row(string role, string agent, int tasks, int runs, int done) =>
            new(role, agent, null, tasks, runs, done, 0, TimeSpan.Zero, null, 0);
        string? Hint(AgentTaskRecord record, string phase, bool configured = true, params StatsRow[] stats) =>
            CheaperHint.For(record, configured, "GLM", "Rigger", stats, phase);

        var claude = TaskOf(AgentProviderKind.ClaudeCode);
        Program.Equal("cheaper hint: a plan to carry out says the custom model could do it",
            Hint(claude, CheaperHint.RunPlan), "GLM could carry out this plan: it is written; hand it over to save your subscription");
        Program.Equal("cheaper hint: no hint when the custom model is not set up",
            Hint(claude, CheaperHint.RunPlan, configured: false), (string?)null);
        Program.Equal("cheaper hint: none for a task the custom model already works on",
            Hint(TaskOf(AgentProviderKind.Custom), CheaperHint.RunPlan), (string?)null);
        Program.Equal("cheaper hint: none for a task that has not run",
            Hint(TaskOf(null), CheaperHint.RunPlan), (string?)null);

        Program.Equal("cheaper hint: the stats speak at three runs with two thirds of the tasks done",
            Hint(claude, "Card", true, Row("Rigger", "GLM", 3, 3, 2)), "GLM: 2 of 3 done for Rigger here");
        Program.Equal("cheaper hint: the rows of the role's models add up",
            Hint(claude, "Card", true, Row("Rigger", "glm", 2, 2, 2), Row("Rigger", "GLM", 2, 2, 1), Row("Rigger", "Codex", 5, 5, 0)),
            "GLM: 3 of 4 done for Rigger here");
        Program.Equal("cheaper hint: two runs are too few",
            Hint(claude, "Card", true, Row("Rigger", "GLM", 2, 2, 2)), (string?)null);
        Program.Equal("cheaper hint: less than two thirds done is no reason",
            Hint(claude, "Card", true, Row("Rigger", "GLM", 4, 4, 2)), (string?)null);
        Program.Equal("cheaper hint: another role's or another agent's record is not this one's",
            Hint(claude, "Card", true, Row("General", "GLM", 5, 5, 5), Row("Rigger", "Codex", 5, 5, 5)), (string?)null);
        Program.Equal("cheaper hint: no stats, no hint", Hint(claude, "Card"), (string?)null);
    }

    /// <summary>Update skill after a finished task asks the same session to update a skill that fits, naming those the task read; plain Save as skill does not.</summary>
    private static void SkillUpdateRequest()
    {
        var workspace = TempFolder();
        try
        {
            WriteSkill(workspace, "game-check", "Check the game.");
            WriteSkill(workspace, "ui-pass", "Walk the screens.");
            var run = new AgentRun(
                new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "Check the game", Access = AgentAccess.Do },
                new WorkspaceBefore(workspace, true, new Dictionary<string, string>(), null, DateTime.UtcNow),
                new AgentResult { SessionId = "work", Succeeded = true },
                []);
            const string Plain = "Save it as a skill.\n\nSkills of the workspace: game-check, ui-pass.";
            Program.Equal("skill update: Save as skill asks nothing more", AgentCoordinator.SkillTask(run, "Save it as a skill.\n")!.Prompt, Plain);
            var update = AgentCoordinator.SkillTask(run, "Save it as a skill.\n", ["ui-pass", "gone-skill"])!;
            Program.Equal("skill update: it asks to update a skill that fits and names those the task read that the workspace has",
                (update.Prompt.StartsWith(Plain + "\n\n", StringComparison.Ordinal), update.Prompt.Contains("Prefer updating a skill of the workspace that fits it over writing a new one (this task worked by ui-pass)."),
                    update.Prompt.Contains("gone-skill"), update.ResumeSessionId, update.AnswerIsSkill),
                (true, true, false, "work", true));
            Program.Equal("skill update: a task that read no skill still asks to update one that fits",
                AgentCoordinator.SkillTask(run, "Save it as a skill.\n", [])!.Prompt.Contains("fits it over writing a new one. Keep it true and short"), true);
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The first-run window says whether each CLI is found and signed in, and where to install the missing one.</summary>
    private static void FirstRunWords()
    {
        const AgentProviderKind Claude = AgentProviderKind.ClaudeCode;
        Program.Equal("first run: found and signed in",
            FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", "2.1", true)), "Found, signed in");
        Program.Equal("first run: found, not signed in names the agent's own command",
            (FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", null, false)),
                FirstRun.Status(AgentProviderKind.Codex, new AgentProviderStatus("codex.exe", null, false))),
            ("Found, not signed in: run 'claude auth login' once", "Found, not signed in: run 'codex login' once"));
        Program.Equal("first run: found, sign-in unknown",
            FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", null, null)), "Found, sign-in unknown");
        Program.Equal("first run: not found", FirstRun.Status(Claude, new AgentProviderStatus(null, null, null)), "Not found");
        Program.Equal("first run: each agent has its own https install page",
            (FirstRun.InstallAddress(Claude).StartsWith("https://", StringComparison.Ordinal),
                FirstRun.InstallAddress(AgentProviderKind.Codex).StartsWith("https://", StringComparison.Ordinal),
                FirstRun.InstallAddress(Claude) != FirstRun.InstallAddress(AgentProviderKind.Codex)),
            (true, true, true));
    }

    /// <summary>A workspace with roles and skills of its own, and the things around them that a pack must never hold.</summary>
    private static string TeamWorkspace()
    {
        var workspace = TempFolder();
        var agents = Path.Combine(workspace, ".agents");
        Directory.CreateDirectory(ProjectRoles.FolderIn(workspace));
        File.WriteAllText(Path.Combine(ProjectRoles.FolderIn(workspace), "qa.md"), "---\nname: QA\n---\nCheck it.");
        File.WriteAllText(Path.Combine(ProjectRoles.FolderIn(workspace), "audio.md"), "---\nname: Audio\n---\nMix it.");
        File.WriteAllText(Path.Combine(ProjectRoles.FolderIn(workspace), "notes.txt"), "not a role");
        WriteSkill(workspace, "game-check", "Run the game's check.");
        WriteSkill(workspace, "ui-pass", "Walk the screens.");
        Directory.CreateDirectory(Path.Combine(ProjectSkills.FolderIn(workspace), "game-check", "scripts"));
        File.WriteAllText(Path.Combine(ProjectSkills.FolderIn(workspace), "game-check", "scripts", "check.ps1"), "echo ok");
        // Not skills, not roles: settings, a key, a log, a folder without a SKILL.md.
        File.WriteAllText(Path.Combine(agents, "settings.json"), "{\"token\":\"secret\"}");
        File.WriteAllText(Path.Combine(agents, "my.key"), "secret");
        File.WriteAllText(Path.Combine(workspace, "link.log"), "log");
        Directory.CreateDirectory(Path.Combine(ProjectSkills.FolderIn(workspace), "drafts"));
        File.WriteAllText(Path.Combine(ProjectSkills.FolderIn(workspace), "drafts", "idea.txt"), "idea");
        return workspace;
    }

    private static string[] PackEntries(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        return [.. zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)];
    }

    private static void MakeZip(string file, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
    }

    /// <summary>A pack holds the roles and skills and pack.json, nothing else, and goes into another workspace as it was.</summary>
    private static void TeamPackRoundTrip()
    {
        var source = TeamWorkspace();
        var target = TempFolder();
        var packs = TempFolder();
        var file = Path.Combine(packs, "team" + TeamPack.Extension);
        try
        {
            Program.Equal("team pack: the counts of roles and skills", TeamPack.Export(source, file), (2, 2));
            Program.Equal("team pack: only roles/*.md, skills/<name>/** and pack.json",
                string.Join("|", PackEntries(file)),
                "pack.json|roles/audio.md|roles/qa.md|skills/game-check/SKILL.md|skills/game-check/scripts/check.ps1|skills/ui-pass/SKILL.md");
            using (var zip = ZipFile.OpenRead(file))
            using (var manifest = JsonDocument.Parse(zip.GetEntry("pack.json")!.Open()))
            {
                var root = manifest.RootElement;
                Program.Equal("team pack: pack.json has the format, the time, the role ids and the skill names",
                    (root.GetProperty("format").GetInt32(), DateTimeOffset.TryParse(root.GetProperty("created").GetString(), out _),
                        string.Join("+", root.GetProperty("roles").EnumerateArray().Select(each => each.GetString())),
                        string.Join("+", root.GetProperty("skills").EnumerateArray().Select(each => each.GetString()))),
                    (1, true, "audio+qa", "game-check+ui-pass"));
            }

            var imported = TeamPack.Import(file, target, _ => throw new InvalidOperationException("nothing there to replace"));
            Program.Equal("team pack: into an empty workspace everything is added",
                (string.Join("|", imported.Added), imported.Replaced.Count, imported.Skipped.Count),
                ("roles/audio.md|roles/qa.md|skills/game-check|skills/ui-pass", 0, 0));
            Program.Equal("team pack: the files come as they were",
                (File.ReadAllText(Path.Combine(ProjectRoles.FolderIn(target), "qa.md")),
                    File.ReadAllText(Path.Combine(ProjectSkills.FolderIn(target), "game-check", "scripts", "check.ps1")),
                    ProjectSkills.In(target).Count, ProjectRoles.Find("qa", target)?.Name),
                ("---\nname: QA\n---\nCheck it.", "echo ok", 2, "QA"));
            Program.Equal("team pack: nothing else lands in the workspace",
                (File.Exists(Path.Combine(target, ".agents", "settings.json")), File.Exists(Path.Combine(target, ".agents", "my.key")),
                    File.Exists(Path.Combine(target, "link.log")), Directory.Exists(Path.Combine(ProjectSkills.FolderIn(target), "drafts"))),
                (false, false, false, false));

            var none = Path.Combine(packs, "none" + TeamPack.Extension);
            Program.Equal("team pack: a workspace with neither roles nor skills gives no file", (TeamPack.Export(Directory.CreateDirectory(Path.Combine(packs, "empty")).FullName, none), File.Exists(none)), ((0, 0), false));
        }
        finally
        {
            DeleteFolder(source);
            DeleteFolder(target);
            DeleteFolder(packs);
        }
    }

    /// <summary>A link in a skill's folder may lead to anything: the pack leaves it out, so it cannot carry a file from elsewhere.</summary>
    private static void TeamPackLinks()
    {
        var workspace = TempFolder();
        var outside = TempFolder();
        var packs = TempFolder();
        try
        {
            WriteSkill(workspace, "x", "A skill.");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            File.WriteAllText(Path.Combine(outside, "SKILL.md"), "---\nname: stolen\n---\n");
            try
            {
                File.CreateSymbolicLink(Path.Combine(ProjectSkills.FolderIn(workspace), "x", "leak.txt"), Path.Combine(outside, "secret.txt"));
                Directory.CreateSymbolicLink(Path.Combine(ProjectSkills.FolderIn(workspace), "linked"), outside);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return; // Windows lets only an administrator or developer mode make a symbolic link.
            }

            var file = Path.Combine(packs, "team" + TeamPack.Extension);
            Program.Equal("team pack: links are left out", (TeamPack.Export(workspace, file), string.Join("|", PackEntries(file))), ((0, 1), "pack.json|skills/x/SKILL.md"));
        }
        finally
        {
            DeleteFolder(workspace);
            DeleteFolder(outside);
            DeleteFolder(packs);
        }
    }

    /// <summary>An existing role or skill is replaced only when the callback says yes to its name; a skill keeps the files of its own.</summary>
    private static void TeamPackReplace()
    {
        var source = TeamWorkspace();
        var target = TeamWorkspace();
        var packs = TempFolder();
        var file = Path.Combine(packs, "team" + TeamPack.Extension);
        try
        {
            File.WriteAllText(Path.Combine(source, ".agents", "roles", "qa.md"), "---\nname: QA\n---\nNew text.");
            File.WriteAllText(Path.Combine(source, ".agents", "roles", "level.md"), "---\nname: Level\n---\nA new role.");
            File.WriteAllText(Path.Combine(ProjectSkills.FolderIn(source), "ui-pass", "SKILL.md"), "---\nname: ui-pass\ndescription: New.\n---\n");
            File.WriteAllText(Path.Combine(ProjectSkills.FolderIn(target), "ui-pass", "local.txt"), "mine");
            TeamPack.Export(source, file);

            var asked = new List<string>();
            var imported = TeamPack.Import(file, target, name =>
            {
                asked.Add(name);
                return name is "roles/qa.md" or "skills/ui-pass";
            });
            Program.Equal("team pack: the callback is asked once for each item that exists, by name",
                string.Join("|", asked.Order(StringComparer.Ordinal)), "roles/audio.md|roles/qa.md|skills/game-check|skills/ui-pass");
            Program.Equal("team pack: added, replaced and kept are told apart",
                (string.Join("|", imported.Added), string.Join("|", imported.Replaced), string.Join("|", imported.Skipped)),
                ("roles/level.md", "roles/qa.md|skills/ui-pass", "roles/audio.md|skills/game-check"));
            Program.Equal("team pack: a yes replaces the file, a no keeps it, a replaced skill keeps its other files",
                (File.ReadAllText(Path.Combine(target, ".agents", "roles", "qa.md")).EndsWith("New text.", StringComparison.Ordinal),
                    File.ReadAllText(Path.Combine(target, ".agents", "roles", "audio.md")),
                    ProjectSkills.In(target).First(skill => skill.Name == "ui-pass").Description,
                    File.ReadAllText(Path.Combine(ProjectSkills.FolderIn(target), "ui-pass", "local.txt"))),
                (true, "---\nname: Audio\n---\nMix it.", "New.", "mine"));
        }
        finally
        {
            DeleteFolder(source);
            DeleteFolder(target);
            DeleteFolder(packs);
        }
    }

    /// <summary>A file that is no pack, a pack of another format, and every way out of .agents are refused before anything is written.</summary>
    private static void TeamPackRefusals()
    {
        var workspace = TempFolder();
        var folder = TempFolder();
        try
        {
            string Import(params (string Name, string Text)[] entries)
            {
                var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + TeamPack.Extension);
                MakeZip(file, entries);
                return Outcome(file);
            }

            string Outcome(string file)
            {
                try
                {
                    TeamPack.Import(file, workspace, _ => true);
                    return "imported";
                }
                catch (InvalidDataException)
                {
                    return "refused";
                }
            }

            const string Manifest = "{\"format\":1}";
            Program.Equal("team pack: a zip without pack.json is refused", Import(("roles/qa.md", "---\nname: QA\n---\n")), "refused");
            Program.Equal("team pack: a pack of another format is refused", Import(("pack.json", "{\"format\":2}"), ("roles/qa.md", "x")), "refused");
            Program.Equal("team pack: a pack.json that is no JSON is refused", Import(("pack.json", "<xml/>"), ("roles/qa.md", "x")), "refused");
            var text = Path.Combine(folder, "not-a-zip" + TeamPack.Extension);
            File.WriteAllText(text, "plain text");
            Program.Equal("team pack: a file that is no zip is refused", Outcome(text), "refused");

            foreach (var bad in new[]
                     {
                         "../evil.md", "roles/../../evil.md", "skills/x/../../../evil.md", "/rooted/evil.md", "C:/evil.md", "roles/C:evil.md",
                         "roles\\..\\..\\evil.md", "skills//x/SKILL.md", "skills/./x/SKILL.md"
                     })
            {
                Program.Equal($"team pack: the entry {bad} is refused", Import(("pack.json", Manifest), ("roles/qa.md", "fine"), (bad, "evil")), "refused");
            }

            Program.Equal("team pack: nothing was written by any refused pack",
                (Directory.Exists(Path.Combine(workspace, ".agents")), File.Exists(Path.Combine(folder, "evil.md")), File.Exists(Path.Combine(workspace, "evil.md"))),
                (false, false, false));
            Program.Equal("team pack: a folder entry, the manifest and files outside roles and skills are not taken",
                Import(("pack.json", Manifest), ("roles/", string.Empty), ("readme.txt", "x"), ("settings.json", "{}"), ("roles/sub/deep.md", "x"), ("roles/qa.txt", "x"), ("skills/stray.md", "x")),
                "imported");
            Program.Equal("team pack: and none of them lands",
                Directory.Exists(Path.Combine(workspace, ".agents")) ? string.Join("|", Directory.GetFiles(Path.Combine(workspace, ".agents"), "*", SearchOption.AllDirectories)) : string.Empty,
                string.Empty);
        }
        finally
        {
            DeleteFolder(workspace);
            DeleteFolder(folder);
        }
    }

    /// <summary>A step is a kind from its words, read as Auto effort reads a brief; each kind starts at its own tier.</summary>
    private static void StepKindsWords()
    {
        string Kinds(params string[] texts) => string.Join(", ", texts.Select(text => StepKinds.Of(text).ToString()));
        Program.Equal("step kinds: renames, typos and imports are mechanical, a README, comments and descriptions are text",
            Kinds("Rename the sprite files to the new scheme", "Fix the typo in the menu", "Reimport the FBX", "Переименуй спрайты", "Update the README",
                "Add comments to the dash code", "Опиши это в документации"),
            "Mechanical, Mechanical, Mechanical, Mechanical, Docs, Docs, Docs");
        Program.Equal("step kinds: design words and an unclear failure come first",
            Kinds("Design a new dash mechanic", "Refactor the movement system", "Спроектируй систему рывка", "Investigate why the game crashes", "Почему падает игра"),
            "Design, Design, Design, Investigate, Investigate");
        Program.Equal("step kinds: tests, reviews and checks",
            Kinds("Add unit tests for the dash", "Review the movement changes", "Check that the build passes", "Проверь результат"), "Test, Review, Review, Review");
        Program.Equal("step kinds: anything else is code, a stem counts only at the start of a word",
            Kinds("Add the dash ability to the movement component", "Fix the jump", "Add an information panel", string.Empty), "Code, Code, Code, Code");
        Program.Equal("step kinds: each kind has its tier",
            string.Join(", ", Enum.GetValues<StepKind>().Select(kind => $"{kind}={StepKinds.TierOf(kind)}")),
            "Design=Strong, Investigate=Strong, Code=Standard, Test=Standard, Review=Standard, Asset=Strong, Mechanical=Light, Docs=Light");
        Program.Equal("step kinds: Auto effort still names the first mechanical word of its list",
            AutoEffort.Choose(EffortPhase.New, "Bump version and add a comment", ["low", "medium", "high"], null, 0, null),
            ((string?)"low", "\"comment\": a mechanical change"));
    }

    /// <summary>The lead's team plan: the last link-plan block, read tolerantly; the answer goes on without it.</summary>
    private static void TeamPlanSplit()
    {
        static string Show(IReadOnlyList<PlannedStep> steps) =>
            string.Join("|", steps.Select(step => $"{step.Text}/{step.Kind}/{step.Worker?.ToString() ?? "-"}/{step.Why}/{string.Join(",", step.Files)}"));
        static string Specialists(IReadOnlyList<PlannedStep> steps) => string.Join("|", steps.Select(step => $"{step.Role ?? "-"}/{step.Check}"));

        const string Good = "```link-plan\n[{\"step\": \"Rename the sprite files\", \"kind\": \"mechanical\", \"worker\": \"light\", \"why\": \"a list of renames\", \"files\": [\"Content/Sprites\"]},\n" +
                            " {\"step\": \"Add the dash ability\", \"kind\": \"code\", \"worker\": \"Standard\"},\n" +
                            " {\"step\": \"Tune the dash feel\", \"kind\": \"design\", \"worker\": \"strong\", \"why\": \"taste\"}]\n```";
        var specialists = ProjectRoles.In(null);
        var (text, steps) = TeamPlan.Split("The plan.\n" + Good);
        Program.Equal("team plan: the answer goes on without the block, the steps are read with kind, worker, why and files",
            (text, Show(steps)),
            ("The plan.", "Rename the sprite files/Mechanical/Light/a list of renames/Content/Sprites|Add the dash ability/Code/Standard//|Tune the dash feel/Design/Strong/taste/"));

        var (_, tolerant) = TeamPlan.Split("""
            ```link-plan
            {"steps": [{"step": "Add unit tests for the dash", "kind": "wizardry", "worker": "genius"}, {"step": "  "}, {"kind": "code"}, "Update the README", 7,
                       {"step": "Fix the jump", "kind": "3", "worker": "2", "files": "Source/Jump.cpp"}, {"step": "Rename it", "kind": "MECHANICAL", "worker": "LIGHT"}]}
            ```
            """);
        Program.Equal("team plan: an unknown kind is classified by words, an unknown worker is none, a step without words or a number is dropped, a bare string is a step",
            Show(tolerant),
            "Add unit tests for the dash/Test/-//|Update the README/Docs/-//|Fix the jump/Code/-//Source/Jump.cpp|Rename it/Mechanical/Light//");

        var many = "```link-plan\n[" + string.Join(",", Enumerable.Range(1, 15).Select(number => $"{{\"step\": \"Step {number}\"}}")) + "]\n```";
        Program.Equal("team plan: at most 12 steps", TeamPlan.Split(many).Steps.Count, 12);

        const string Answer = "I could not plan.\n```link-plan\n[{\"step\": \n```";
        Program.Equal("team plan: JSON that does not read leaves the answer as it is, without steps",
            (TeamPlan.Split(Answer).Text, TeamPlan.Split(Answer).Steps.Count), (Answer, 0));
        Program.Equal("team plan: no block, an empty list and a block that is no list are no plan",
            new[] { "Just an answer.", "```link-plan\n[]\n```", "```link-plan\n{\"note\": 1}\n```", "```link-plan\n[{\"step\": \"\"}]\n```" }
                .Select(answer => TeamPlan.Split(answer) is (var shown, { Count: 0 }) && shown == answer).All(same => same),
            true);

        var (_, specialist) = TeamPlan.Split("""
            ```link-plan
            [{"step": "Model the dash prop", "role": "3d-artist", "check": true}, {"step": "Rig it", "role": "Rigger-Animator"}, {"step": "Animate it", "role": "RIGGER-ANIMATOR", "check": "TRUE"},
             {"step": "Wire it", "role": "ghost", "check": false}, {"step": "Test it", "role": 7, "check": 1}, {"step": "Tidy"}]
            ```
            """, specialists);
        Program.Equal("team plan: a role is one of the workspace's, in the role's own spelling, an unknown one is none; check is true or \"true\", anything else is no check",
            Specialists(specialist), "3d-artist/True|rigger-animator/False|rigger-animator/True|-/False|-/False|-/False");
        Program.Equal("team plan: without the roles of the workspace no role is taken", TeamPlan.Split("```link-plan\n[{\"step\": \"Model it\", \"role\": \"3d-artist\"}]\n```").Steps[0].Role, (string?)null);

        var (rest, last) = TeamPlan.Split("First:\n```link-plan\n[{\"step\": \"Old\"}]\n```\nThen:\n```link-plan\n[{\"step\": \"New\"}]\n```");
        Program.Equal("team plan: the last block is the plan", (rest, Show(last)), ("First:\n```link-plan\n[{\"step\": \"Old\"}]\n```\nThen:", "New/Code/-//"));
    }

    /// <summary>What the lead is asked when the task is planned for a team.</summary>
    private static void TeamPlanPrompt()
    {
        const string Ask = "Make a team plan.";
        Program.Equal("team prompt: the task, the request without its notes, and nothing yet when the project has no evidence",
            TeamPlan.Prompt(" Fix the jump ", "<!-- for whoever edits it -->\nMake a team plan.\n", []), $"Fix the jump\n\n{Ask}\n\nWhat workers did in this project before: nothing yet");
        Program.Equal("team prompt: what each worker did, a line each",
            TeamPlan.Prompt("Fix the jump", Ask, ["GLM: Mechanical 6/6 ✓", "Codex: Code 5/5 ✓"]), $"Fix the jump\n\n{Ask}\n\nWhat workers did in this project before:\n- GLM: Mechanical 6/6 ✓\n- Codex: Code 5/5 ✓");
        AgentRole Role(string id, string name, string description) => new(id, name, description, [], [], null, string.Empty, string.Empty);
        Program.Equal("team prompt: the specialists a step can go to, by id",
            TeamPlan.Prompt("Fix the jump", Ask, [], [Role("3d-artist", "3D Artist", "Modeling and UV."), Role("qa", "QA", "Checks.")]),
            $"Fix the jump\n\n{Ask}\n\nSpecialists for a step's \"role\":\n- 3d-artist: 3D Artist. Modeling and UV.\n- qa: QA. Checks.\n\nWhat workers did in this project before: nothing yet");
        var request = File.ReadAllText(TeamPlan.TaskFile);
        Program.Equal("team prompt: its request ships beside Link and shows the block, the tiers and the check", (request.Contains("```link-plan"), request.Contains("`light`"), request.Contains("\"check\": true")), (true, true, true));
        Program.Equal("handover: the last line that starts with Handed over, in a list or in bold too, or none",
            (TeamPlan.HandedOver("Done.\nHanded over: Content/Dash.fbx, skeleton SK_Nori"), TeamPlan.HandedOver("Done.\n- **Handed over:** Content/Dash.fbx\r\nNothing else."),
                TeamPlan.HandedOver("Handed over: old\nmore text\nHanded over: the new one  "), TeamPlan.HandedOver("We handed over nothing."), TeamPlan.HandedOver("Handed over:   ")),
            ((string?)"Content/Dash.fbx, skeleton SK_Nori", "Content/Dash.fbx", "the new one", null, null));
    }

    /// <summary>The team in the settings and on the records: defaults, older files, odd values, and the 450k budget that came with it.</summary>
    private static void TeamSettingsKept()
    {
        var fresh = new AgentSettings();
        Program.Equal("team settings: on, every tier left to the default, no A/B, Link compacts by itself",
            (fresh.Team.Enabled, fresh.Team.Light, fresh.Team.Standard, fresh.Team.Strong, fresh.Team.Alternate, fresh.AutoCompact),
            (true, (WorkerChoice?)null, (WorkerChoice?)null, (WorkerChoice?)null, false, true));

        var older = JsonSerializer.Deserialize<AgentSettings>("""{"ContextBudget":300000,"PlanOnDeep":false}""")!;
        older.Normalize();
        Program.Equal("team settings: settings of an older Link load with the team on and Compact by Link on",
            (older.Team.Enabled, older.Team.Light, older.AutoCompact, older.ContextBudget), (true, (WorkerChoice?)null, true, 300_000));
        var nothing = JsonSerializer.Deserialize<AgentSettings>("""{"Team":null}""")!;
        nothing.Normalize();
        Program.Equal("team settings: a null team is the default one", (nothing.Team.Enabled, nothing.Team.Light), (true, (WorkerChoice?)null));

        var odd = JsonSerializer.Deserialize<AgentSettings>(
            """{"Team":{"Enabled":false,"Light":{"Provider":2,"Level":0},"Standard":{"Provider":9,"Level":1},"Strong":{"Provider":0,"Level":7}}}""")!;
        odd.Normalize();
        Program.Equal("team settings: a choice is kept, one of an agent or level this Link does not know is the default",
            (odd.Team.Enabled, odd.Team.Light, odd.Team.Standard, odd.Team.Strong), (false, new WorkerChoice(AgentProviderKind.Custom, AgentLevel.Light), (WorkerChoice?)null, (WorkerChoice?)null));

        var copy = new AgentSettings { AutoCompact = false, Team = { Standard = new WorkerChoice(AgentProviderKind.Codex, AgentLevel.Deep), Alternate = true } }.Clone();
        Program.Equal("team settings: a copy keeps them and is its own",
            (copy.AutoCompact, copy.Team.Standard, copy.Team.Alternate, copy.Team.For(WorkerTier.Standard), copy.Team.For(WorkerTier.Light)),
            (false, new WorkerChoice(AgentProviderKind.Codex, AgentLevel.Deep), true, new WorkerChoice(AgentProviderKind.Codex, AgentLevel.Deep), (WorkerChoice?)null));
        var again = JsonSerializer.Deserialize<AgentSettings>(JsonSerializer.Serialize(copy))!;
        Program.Equal("team settings: they are saved", (again.Team.Standard, again.Team.Alternate, again.AutoCompact), (copy.Team.Standard, true, false));

        AppSettings Load(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
            settings.Normalize();
            return settings;
        }

        Program.Equal("budget: 450k is the default in new and older settings",
            (AgentSettings.DefaultContextBudget, new AppSettings().SchemaVersion, Load("""{"SchemaVersion":7}""").Agent.ContextBudget), (450_000, 8, 450_000));
        Program.Equal("budget: schema 8 turns last night's default 550k into 450k, and nothing else",
            (Load("""{"SchemaVersion":7,"Agent":{"ContextBudget":550000}}""").Agent.ContextBudget, Load("""{"SchemaVersion":7,"Agent":{"ContextBudget":800000}}""").Agent.ContextBudget,
                Load("""{"SchemaVersion":8,"Agent":{"ContextBudget":550000}}""").Agent.ContextBudget, Load("""{"SchemaVersion":7,"Agent":{"ContextBudget":550000}}""").SchemaVersion),
            (450_000, 800_000, 550_000, 8));
    }

    /// <summary>The team plan and the mode on the task record, the step on the run; older records load with defaults.</summary>
    private static void TeamRecordsKept()
    {
        var older = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Fix it","Workspace":"C:\\Work","Runs":[{"Provider":0,"Outcome":"Done in 1:00"}]}""")!;
        Program.Equal("team record: a task of an older Link is Solo, with no team plan and no step on its run",
            (older.Mode, older.Team.Count, older.Runs[0].Step, older.Runs[0].Kind, older.Runs[0].Tier),
            (TaskMode.Solo, 0, (int?)null, (StepKind?)null, (WorkerTier?)null));

        var team = new AgentTaskRecord
        {
            Goal = "Add the dash", Workspace = "C:\\Work", Mode = TaskMode.Team,
            Team =
            [
                new TeamStep { Text = "Rename", Kind = StepKind.Mechanical, Tier = WorkerTier.Light, Worker = "GLM", Status = TeamStepStatus.Done, Reason = "Mechanical → GLM", Files = ["Content"] },
                new TeamStep { Text = "Add", Kind = StepKind.Code, Tier = WorkerTier.Standard, Worker = "Codex", Status = TeamStepStatus.AwaitsOk, EscalatedFrom = "GLM", Failure = "Failed after 0:10", Role = "3d-artist", Check = true, HandedOver = "Content/Dash.fbx" }
            ],
            Runs = [new AgentRunRecord { Step = 2, Kind = StepKind.Code, Tier = WorkerTier.Standard, Outcome = "Done in 0:30" }],
            Plan = [new AgentPlanItem("Add", false, true, null, null, "GLM → Codex", "3D Artist")]
        };
        var back = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(team))!;
        Program.Equal("team record: mode, steps, escalation and the run's step are saved",
            (back.Mode, string.Join("|", back.Team.Select(step => $"{step.Text}/{step.Kind}/{step.Tier}/{step.Worker}/{step.Status}/{step.EscalatedFrom}/{step.Failure}/{step.Reason}/{string.Join(",", step.Files)}/{step.Role}/{step.Check}/{step.HandedOver}")),
                back.Runs[0].Step, back.Runs[0].Kind, back.Runs[0].Tier, back.Plan[0].Worker + " · " + back.Plan[0].Role),
            (TaskMode.Team, "Rename/Mechanical/Light/GLM/Done///Mechanical → GLM/Content//False/|Add/Code/Standard/Codex/AwaitsOk/GLM/Failed after 0:10///3d-artist/True/Content/Dash.fbx", (int?)2, (StepKind?)StepKind.Code, (WorkerTier?)WorkerTier.Standard, "GLM → Codex · 3D Artist"));
        Program.Equal("team record: a plan item without a worker is the agent's own",
            (new AgentPlanItem("Fix it", false, true, "Fixing it", 2).Worker, JsonSerializer.Deserialize<AgentPlanItem>("""{"Text":"A","Done":false,"Active":false}""")!.Role),
            ((string?)null, (string?)null));
    }

    /// <summary>Which worker did which kind of step well, learned from checked results: the rules, the words and the file.</summary>
    private static void WorkerEvidenceRules()
    {
        var glm = new Worker(AgentProviderKind.Custom, AgentLevel.Light, "glm-4.7-flash");
        var codex = new Worker(AgentProviderKind.Codex, AgentLevel.Standard, null);
        (bool Blocked, bool Proven) Rule(int done, int failed)
        {
            var evidence = new WorkerEvidence();
            for (var each = 0; each < done; each++) evidence.Record(StepKind.Code, glm, true);
            for (var each = 0; each < failed; each++) evidence.Record(StepKind.Code, glm, false);
            return (evidence.Blocked(StepKind.Code, glm), evidence.Proven(StepKind.Code, glm));
        }

        Program.Equal("evidence: nothing known blocks nobody and proves nobody", Rule(0, 0), (false, false));
        Program.Equal("evidence: blocked at two failures with fewer done than failed",
            (Rule(0, 1), Rule(0, 2), Rule(2, 2), Rule(1, 3), Rule(3, 3)), ((false, false), (true, false), (false, false), (true, false), (false, false)));
        Program.Equal("evidence: proven at three done with failures at most a quarter of them",
            (Rule(2, 0), Rule(3, 0), Rule(3, 1), Rule(4, 1), Rule(8, 2)), ((false, false), (false, true), (false, false), (false, true), (false, true)));

        var evidence = new WorkerEvidence();
        evidence.Record(StepKind.Code, glm, false);
        evidence.Record(StepKind.Code, glm, false);
        Program.Equal("evidence: a kind, an agent and a model are counted apart; no model is the empty one",
            (evidence.Blocked(StepKind.Code, glm), evidence.Blocked(StepKind.Docs, glm), evidence.Blocked(StepKind.Code, glm with { Model = "glm-4.7" }),
                evidence.Blocked(StepKind.Code, glm with { Provider = AgentProviderKind.Codex }), evidence.Blocked(StepKind.Code, glm with { Level = AgentLevel.Deep })),
            (true, false, false, false, true));
        evidence.Record(StepKind.Test, codex, true);
        Program.Equal("evidence: no model and an empty one are one worker", evidence.Counts(StepKind.Test, codex with { Model = string.Empty }), (1, 0));

        Program.Equal("evidence: a run's word on its worker; a Stop, a Steer, a limit and a start that failed say nothing",
            string.Join(",", new[]
            {
                new AgentRunRecord { Outcome = "Done in 0:42" }, new AgentRunRecord { Outcome = "Failed after 1:00" }, new AgentRunRecord { Outcome = "Check failed after 2:00" },
                new AgentRunRecord { Outcome = "Stopped after 0:10" }, new AgentRunRecord { Outcome = "Stopped after 0:20", Looped = true },
                new AgentRunRecord { Outcome = "Stopped at the usage limit after 1:00", Looped = true }, new AgentRunRecord { Outcome = "could not start" }
            }.Select(run => WorkerEvidence.Verdict(run)?.ToString() ?? "none")),
            "True,False,False,none,False,none,none");

        var learned = new WorkerEvidence();
        for (var each = 0; each < 6; each++) learned.Record(StepKind.Mechanical, glm, true);
        foreach (var done in new[] { false, false, true, false }) learned.Record(StepKind.Code, glm, done);
        for (var each = 0; each < 5; each++) learned.Record(StepKind.Code, codex, true);
        Program.Equal("evidence: a line per worker, the busiest first, with done of tried, a tick for proven and a word for blocked",
            string.Join("\n", learned.Summary(provider => provider == AgentProviderKind.Custom ? "GLM" : provider.DefaultName())),
            "GLM glm-4.7-flash: Mechanical 6/6 ✓, Code 1/4 — not given Code here\nCodex: Code 5/5 ✓");
        Program.Equal("evidence: nothing learned, nothing said", new WorkerEvidence().Summary(provider => provider.DefaultName()).Count, 0);

        var folder = TempFolder();
        try
        {
            var file = Path.Combine(folder, "project", "workers.json");
            Program.Equal("evidence: a missing file is no records", WorkerEvidence.Load(file).Records.Count, 0);
            var saved = WorkerEvidence.Load(file);
            saved.Record(StepKind.Code, codex, true, new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
            saved.Record(StepKind.Code, codex, false);
            saved.Record(StepKind.Docs, glm, true);
            var again = WorkerEvidence.Load(file);
            Program.Equal("evidence: each record is saved to the project's file",
                (again.Counts(StepKind.Code, codex), again.Counts(StepKind.Docs, glm), again.Records.Count), ((1, 1), (1, 0), 2));

            var broken = Path.Combine(folder, "broken.json");
            File.WriteAllText(broken, "not json");
            var odd = Path.Combine(folder, "odd.json");
            File.WriteAllText(odd, """[{"Kind":99,"Provider":1,"Model":null,"Done":3,"Failed":0,"LastAt":"2026-10-08T10:00:00+00:00"},{"Kind":2,"Provider":1,"Model":null,"Done":-5,"Failed":1,"LastAt":"2026-10-08T10:00:00+00:00"},null]""");
            Program.Equal("evidence: a file that does not read is no records; an unknown kind is dropped, a count never goes below zero",
                (WorkerEvidence.Load(broken).Records.Count, WorkerEvidence.Load(odd).Counts(StepKind.Code, codex), WorkerEvidence.Load(odd).Records.Count), (0, (0, 1), 1));
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    /// <summary>The worker of each tier from the settings, and who gets a step: by kind, the plan's word, the evidence and what can work now.</summary>
    private static void WorkerRoutes()
    {
        static string Show(Worker worker) => $"{worker.Provider} {worker.Level} {worker.Model ?? "-"}";
        Func<AgentProviderKind, WorkerState> ready = _ => WorkerState.Ready;
        Func<AgentProviderKind, WorkerState> Without(params AgentProviderKind[] missing) => provider => missing.Contains(provider) ? WorkerState.NotSet : WorkerState.Ready;
        string Tiers(AgentSettings settings, Func<AgentProviderKind, WorkerState> state) =>
            string.Join(" | ", WorkerRouting.Workers(settings, state).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {Show(pair.Value)}"));

        var settings = new AgentSettings();
        Program.Equal("workers: Claude Code at Deep leads, Codex takes Standard, the custom model Light",
            Tiers(settings, ready), "Light: Custom Light glm-4.7-flash | Standard: Codex Standard - | Strong: ClaudeCode Deep claude-opus-5-5");
        Program.Equal("workers: without Codex, Claude Code takes Standard; without the custom model, Codex Light",
            (Tiers(settings, Without(AgentProviderKind.Codex)), Tiers(settings, Without(AgentProviderKind.Custom))),
            ("Light: Custom Light glm-4.7-flash | Standard: ClaudeCode Standard - | Strong: ClaudeCode Deep claude-opus-5-5",
                "Light: Codex Light - | Standard: Codex Standard - | Strong: ClaudeCode Deep claude-opus-5-5"));
        Program.Equal("workers: with only Claude Code every tier is Claude Code at its level",
            Tiers(settings, Without(AgentProviderKind.Codex, AgentProviderKind.Custom)),
            "Light: ClaudeCode Light claude-sonnet-5-5 | Standard: ClaudeCode Standard - | Strong: ClaudeCode Deep claude-opus-5-5");

        var chosen = new AgentSettings { DefaultProvider = AgentProviderKind.Codex, Team = { Light = new WorkerChoice(AgentProviderKind.ClaudeCode, AgentLevel.Light), Standard = new WorkerChoice(AgentProviderKind.Custom, AgentLevel.Standard) } };
        Program.Equal("workers: a tier the user chose is that choice, the default agent leads, a choice that is not set up is the default",
            (Tiers(chosen, ready), Tiers(chosen, Without(AgentProviderKind.Custom))),
            ("Light: ClaudeCode Light claude-sonnet-5-5 | Standard: Custom Standard glm-4.7-flash | Strong: Codex Deep -",
                "Light: ClaudeCode Light claude-sonnet-5-5 | Standard: Codex Standard - | Strong: Codex Deep -"));

        var workers = WorkerRouting.Workers(settings, ready);
        var glm = workers[WorkerTier.Light];
        var codex = workers[WorkerTier.Standard];
        var claude = workers[WorkerTier.Strong];
        string Name(AgentProviderKind provider) => provider == AgentProviderKind.Custom ? "GLM" : WorkerRouting.ShortName(provider);
        string Route(StepKind kind, WorkerTier? asked = null, WorkerEvidence? evidence = null, Func<AgentProviderKind, WorkerState>? state = null)
        {
            var (tier, worker, reason) = WorkerRouting.Route(kind, asked, settings, state ?? ready, evidence ?? new WorkerEvidence(), Name);
            return $"{tier} {worker?.Provider.ToString() ?? "nobody"} | {reason}";
        }
        Func<AgentProviderKind, WorkerState> Unavailable(params AgentProviderKind[] limited) => provider => limited.Contains(provider) ? WorkerState.LimitReached : WorkerState.Ready;

        WorkerEvidence Learned(Worker worker, StepKind kind, int done, int failed)
        {
            var evidence = new WorkerEvidence();
            for (var each = 0; each < done; each++) evidence.Record(kind, worker, true);
            for (var each = 0; each < failed; each++) evidence.Record(kind, worker, false);
            return evidence;
        }

        Program.Equal("route: each kind goes to the worker of its tier",
            string.Join("\n", Enum.GetValues<StepKind>().Select(kind => $"{kind}: {Route(kind)}")),
            "Design: Strong ClaudeCode | Design → Claude\nInvestigate: Strong ClaudeCode | Investigate → Claude\nCode: Standard Codex | Code → Codex\n" +
            "Test: Standard Codex | Test → Codex\nReview: Standard Codex | Review → Codex\nAsset: Strong ClaudeCode | Asset → Claude\n" +
            "Mechanical: Light Custom | Mechanical → GLM\nDocs: Light Custom | Docs → GLM");
        Program.Equal("route: the plan's tier is taken, with its word in the reason",
            (Route(StepKind.Code, WorkerTier.Light), Route(StepKind.Mechanical, WorkerTier.Strong)),
            ("Light Custom | Code: the plan chose Light → GLM", "Strong ClaudeCode | Mechanical: the plan chose Strong → Claude"));
        Program.Equal("route: a worker proven for a kind takes it one tier lower, but not what the plan asked for, and only if it can work now",
            (Route(StepKind.Code, null, Learned(glm, StepKind.Code, 3, 0)), Route(StepKind.Code, WorkerTier.Standard, Learned(glm, StepKind.Code, 3, 0)),
                Route(StepKind.Code, null, Learned(glm, StepKind.Code, 3, 0), provider => provider == AgentProviderKind.Custom ? WorkerState.LimitReached : WorkerState.Ready),
                Route(StepKind.Mechanical, null, Learned(glm, StepKind.Mechanical, 9, 0))),
            ("Light Custom | Code: GLM did 3 of 3 here → GLM", "Standard Codex | Code: the plan chose Standard → Codex",
                "Standard Codex | Code → Codex", "Light Custom | Mechanical → GLM"));

        var failing = Learned(glm, StepKind.Code, 1, 3);
        Program.Equal("route: a worker that keeps failing a kind here no longer gets it: the step goes a tier up, and the reason says why",
            (Route(StepKind.Code, WorkerTier.Light, failing), Route(StepKind.Mechanical, null, Learned(glm, StepKind.Mechanical, 0, 2)),
                Route(StepKind.Code, null, failing), Route(StepKind.Docs, null, failing)),
            ("Standard Codex | Code: the plan chose Light, GLM failed it 3 of 4 here → Codex", "Standard Codex | Mechanical: GLM failed it 2 of 2 here → Codex",
                "Standard Codex | Code → Codex", "Light Custom | Docs → GLM"));
        var both = Learned(glm, StepKind.Mechanical, 0, 2);
        both.Record(StepKind.Mechanical, codex, false);
        both.Record(StepKind.Mechanical, codex, false);
        Program.Equal("route: and on, tier by tier, while the next worker fails it too",
            Route(StepKind.Mechanical, null, both), "Strong ClaudeCode | Mechanical: GLM failed it 2 of 2 here, Codex failed it 2 of 2 here → Claude");

        Program.Equal("route: a worker at its limit is passed by, with the reason; an agent that is not set up is not in the team to begin with",
            (Route(StepKind.Code, null, null, Unavailable(AgentProviderKind.Codex)), Route(StepKind.Mechanical, null, null, Without(AgentProviderKind.Custom))),
            ("Strong ClaudeCode | Code: Codex limit reached → Claude", "Light Codex | Mechanical → Codex"));
        Program.Equal("route: Strong with its agent out of reach goes to the other subscription agent at Deep, and says so",
            (Route(StepKind.Design, null, null, Unavailable(AgentProviderKind.ClaudeCode)), Route(StepKind.Design, null, null, Without(AgentProviderKind.ClaudeCode)),
                Route(StepKind.Code, null, null, Unavailable(AgentProviderKind.ClaudeCode, AgentProviderKind.Custom)), Route(StepKind.Design, WorkerTier.Strong, null, Unavailable(AgentProviderKind.ClaudeCode))),
            ("Strong Codex | Design: Claude limit reached → Codex (Strong)", "Strong Codex | Design: Claude is not set up → Codex (Strong)",
                "Standard Codex | Code → Codex", "Strong Codex | Design: the plan chose Strong, Claude limit reached → Codex (Strong)"));
        Program.Equal("route: the stand-in is the other agent whichever leads, and it needs the project's trust too",
            (WorkerRouting.Route(StepKind.Design, null, chosen, Unavailable(AgentProviderKind.Codex), new WorkerEvidence(), Name).Worker is { Provider: AgentProviderKind.ClaudeCode, Level: AgentLevel.Deep, Model: "claude-opus-5-5" },
                Route(StepKind.Design, null, Learned(new Worker(AgentProviderKind.Codex, AgentLevel.Deep, null), StepKind.Design, 0, 2), Unavailable(AgentProviderKind.ClaudeCode))),
            (true, "Strong nobody | Design: Claude limit reached, Codex failed it 2 of 2 here → no agent can take it now"));
        Program.Equal("route: with no agent that can work at Strong there is no worker, and the reason names each",
            (Route(StepKind.Code, null, null, Unavailable(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex)),
                Route(StepKind.Design, null, null, Without(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex))),
            ("Strong nobody | Code: Codex limit reached, Claude limit reached → no agent can take it now",
                "Strong nobody | Design: Claude is not set up, Codex is not set up → no agent can take it now"));
        Program.Equal("route: a worker that failed the kind at Strong stays, with the reason: it is not out of reach",
            Route(StepKind.Design, null, Learned(claude, StepKind.Design, 0, 2)), "Strong ClaudeCode | Design: Claude failed it 2 of 2 here → Claude");
        Program.Equal("route: Design and Asset never go below Standard, whatever the plan asked or the project proved",
            (Route(StepKind.Design, WorkerTier.Light), Route(StepKind.Asset, WorkerTier.Light), Route(StepKind.Design, null, Learned(glm, StepKind.Design, 9, 0)),
                Route(StepKind.Design, null, Learned(codex, StepKind.Design, 3, 0)), Route(StepKind.Docs, WorkerTier.Light)),
            ("Standard Codex | Design: the plan chose Light, Design goes no lower than Standard → Codex",
                "Standard Codex | Asset: the plan chose Light, Asset goes no lower than Standard → Codex", "Strong ClaudeCode | Design → Claude",
                "Standard Codex | Design: Codex did 3 of 3 here → Codex", "Light Custom | Docs: the plan chose Light → GLM"));
        Func<AgentProviderKind, bool> onlyClaudeSees = provider => provider == AgentProviderKind.ClaudeCode;
        (WorkerTier, Worker?, string) Asset(AgentSettings each, Func<AgentProviderKind, WorkerState>? state = null, WorkerTier? asked = null) =>
            WorkerRouting.Route(StepKind.Asset, asked, each, state ?? ready, new WorkerEvidence(), Name, onlyClaudeSees);
        var (_, seeing, seeingReason) = Asset(settings, asked: WorkerTier.Standard);
        var (_, standIn, standInReason) = Asset(settings, Unavailable(AgentProviderKind.ClaudeCode));
        Program.Equal("route: an Asset step goes only to an agent that sees images: the worker without eyes is passed by, and so is a stand-in",
            ($"{seeing?.Provider} | {seeingReason}", $"{standIn?.Provider.ToString() ?? "nobody"} | {standInReason}"),
            ("ClaudeCode | Asset: the plan chose Standard, Codex cannot see images → Claude", "nobody | Asset: Claude limit reached, Codex cannot see images → no agent can take it now"));
        Program.Equal("route: an Asset step in the default setup, where Codex sees images, goes to Claude at Strong and to Codex when Claude is out",
            (Route(StepKind.Asset), Route(StepKind.Asset, null, null, Unavailable(AgentProviderKind.ClaudeCode))),
            ("Strong ClaudeCode | Asset → Claude", "Strong Codex | Asset: Claude limit reached → Codex (Strong)"));
        Program.Equal("route: a step that failed goes one tier up, none above Strong",
            (WorkerRouting.Escalate(WorkerTier.Light), WorkerRouting.Escalate(WorkerTier.Standard), WorkerRouting.Escalate(WorkerTier.Strong)),
            ((WorkerTier?)WorkerTier.Standard, (WorkerTier?)WorkerTier.Strong, (WorkerTier?)null));
        Program.Equal("route: the short names of the agents", (WorkerRouting.ShortName(AgentProviderKind.ClaudeCode), WorkerRouting.ShortName(AgentProviderKind.Codex), WorkerRouting.ShortName(AgentProviderKind.Custom)),
            ("Claude", "Codex", "Custom"));
    }

    /// <summary>When Link starts a fresh session from the task's state: only where the saving is clear.</summary>
    private static void CompactRuleReasons()
    {
        var none = TimeSpan.Zero;
        Program.Equal("compact rule: a session that does not tell its size is resumed", CompactRule.Why(null, 450_000, TimeSpan.FromDays(3)), (string?)null);
        Program.Equal("compact rule: at 80% of the budget the session is too big to re-send",
            (CompactRule.Why(360_000, 450_000, none), CompactRule.Why(359_999, 450_000, none)),
            ("the session holds 360k of 450k: each step of the next run would re-send it; the task's state is far shorter", (string?)null));
        Program.Equal("compact rule: a custom model's window caps the budget, a window above the budget does not raise it",
            (CompactRule.Why(170_000, 450_000, none, 200_000), CompactRule.Why(170_000, 450_000, none), CompactRule.Why(130_000, 150_000, none, 200_000)),
            ("the session holds 170k of 200k: each step of the next run would re-send it; the task's state is far shorter", (string?)null,
                "the session holds 130k of 150k: each step of the next run would re-send it; the task's state is far shorter"));
        Program.Equal("compact rule: after an hour idle the session's cache has expired",
            CompactRule.Why(120_000, 450_000, TimeSpan.FromMinutes(75)),
            "the session's cache has expired (75 min idle): re-sending 120k would cost more than starting from the task's state");
        bool Cold(long held, int budget, int minutes) => CompactRule.Why(held, budget, TimeSpan.FromMinutes(minutes)) is { } why && why.StartsWith("the session's cache", StringComparison.Ordinal);
        Program.Equal("compact rule: only for a session of 100k and a quarter of the budget, idle for an hour",
            (Cold(112_499, 450_000, 120), Cold(112_500, 450_000, 120), Cold(120_000, 450_000, 59), Cold(100_000, 200_000, 60), Cold(99_999, 200_000, 300)),
            (false, true, false, true, false));
        Program.Equal("compact rule: a long idle is told in hours and days; a session near the budget gives the size reason first",
            (CompactRule.Why(150_000, 450_000, TimeSpan.FromHours(50)), CompactRule.Why(150_000, 450_000, TimeSpan.FromHours(5)),
                CompactRule.Why(400_000, 450_000, TimeSpan.FromHours(5))?.StartsWith("the session holds 400k of 450k", StringComparison.Ordinal)),
            ("the session's cache has expired (2 days idle): re-sending 150k would cost more than starting from the task's state",
                "the session's cache has expired (5 h idle): re-sending 150k would cost more than starting from the task's state", (bool?)true));
    }

    /// <summary>A task measured: tokens per agent without the cached part counted twice, the strong agent's apart, steers and escalations.</summary>
    private static void TaskMetricsCounted()
    {
        var at = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var minute = TimeSpan.FromMinutes(1);
        var record = new AgentTaskRecord
        {
            Goal = "Add the dash", Workspace = "C:\\Work", Mode = TaskMode.Team, Status = AgentTaskStatus.Done, LastCheck = "dotnet test: passed",
            Runs =
            [
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Done in 1:00", StartedAt = at, Duration = minute, InputTokens = 1000, CachedTokens = 800, OutputTokens = 100 },
                new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Done in 2:00", StartedAt = at + minute, Duration = 2 * minute, InputTokens = 500, CachedTokens = 0, OutputTokens = 10 },
                // A cached part beyond the input is a slip of the CLI: it is cut to the input.
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Done in 1:00", StartedAt = at + 3 * minute, Duration = minute, InputTokens = 100, CachedTokens = 300 }
            ],
            Team = [new TeamStep { EscalatedFrom = "GLM" }, new TeamStep()]
        };
        record.Entries.Add(new AgentTaskEntry(at, AgentEntryKind.Brief, "Add the dash"));
        record.Entries.Add(new AgentTaskEntry(at + TimeSpan.FromSeconds(30), AgentEntryKind.You, "(steer now) lower"));
        record.Entries.Add(new AgentTaskEntry(at + TimeSpan.FromSeconds(90), AgentEntryKind.You, "(after this step) tests"));
        record.Entries.Add(new AgentTaskEntry(at + 5 * minute, AgentEntryKind.You, "thanks, one more"));
        var metric = TaskMetrics.Of(record, AgentProviderKind.ClaudeCode);
        Program.Equal("metrics: tokens per agent, the cached part apart from the fresh input",
            string.Join(", ", metric.Agents.Select(agent => $"{agent.Provider} {agent.Tokens.Uncached}/{agent.Tokens.Cached}/{agent.Tokens.Output}")), "ClaudeCode 200/900/100, Codex 500/0/10");
        Program.Equal("metrics: ≈ tokens = fresh + a tenth of the cached + five times the output, all of it and the strong agent's apart",
            (Math.Round(metric.Equivalent, 6), Math.Round(metric.StrongEquivalent, 6)), (1340.0, 790.0));
        Program.Equal("metrics: time, runs, the words given while a run went, escalations, the end and the check",
            (metric.Time, metric.Runs, metric.Steers, metric.Escalations, metric.Done, metric.CheckPassed, metric.Mode, metric.Kind),
            (4 * minute, 3, 2, 1, true, (bool?)true, TaskMode.Team, StepKind.Code));
        Program.Equal("metrics: a run that told no tokens has none; a cached part counts only within the input",
            (TokenCount.Of(new AgentRunRecord()), Math.Round(TokenCount.Of(new AgentRunRecord { InputTokens = 50, CachedTokens = 80, OutputTokens = 2 }).Equivalent, 6)),
            (new TokenCount(0, 0, 0), 15.0));

        var failed = new AgentTaskRecord
        {
            Goal = "Fix it", Workspace = "C:\\Work", Status = AgentTaskStatus.CheckFailed, LastCheck = "dotnet test: failed",
            Runs = [new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Check failed after 1:00" }]
        };
        var open = new AgentTaskRecord { Goal = "Fix it", Workspace = "C:\\Work", Status = AgentTaskStatus.Finished, Runs = [new AgentRunRecord { Outcome = "Failed after 1:00" }] };
        Program.Equal("metrics: a failed check is not a pass, no check is none, a finished task whose last run failed is not done, no run is not done",
            (TaskMetrics.Of(failed, AgentProviderKind.ClaudeCode).CheckPassed, TaskMetrics.Of(failed, AgentProviderKind.ClaudeCode).Done, TaskMetrics.Of(open, AgentProviderKind.ClaudeCode).CheckPassed,
                TaskMetrics.Of(open, AgentProviderKind.ClaudeCode).Done, TaskMetrics.Of(new AgentTaskRecord { Goal = "x", Workspace = "w" }, AgentProviderKind.ClaudeCode).Done),
            ((bool?)false, false, (bool?)null, false, false));
    }

    /// <summary>Every specialist a brief names, in the order the work flows: where their words first occur.</summary>
    private static void RoleSuggestAll()
    {
        var roles = ProjectRoles.In(null);
        string Ids(string brief) => string.Join(", ", ProjectRoles.SuggestAll(brief, roles).Select(role => role.Id));
        Program.Equal("suggest all: a brief that goes from a model to its animation to the game names the specialists in that order",
            (Ids("Сделай 3д модель кабана, анимация бега и блюпринт для него"), Ids("Add the Enhanced Input action in a Blueprint, then retarget the animation, then make a low-poly mesh in Blender")),
            ("3d-artist, rigger-animator, unreal-developer", "unreal-developer, rigger-animator, 3d-artist"));
        Program.Equal("suggest all: the user's own words for a model, its animation and the combat system",
            Ids("сделать модель + анимировать и добавить в боевую систему"), "3d-artist, rigger-animator, unreal-developer");
        Program.Equal("suggest all: a role appears once, by its earliest word, and one with no word in the brief is not there",
            (Ids("Retarget the animation, rig the skeleton, then retarget again"), Ids("Rename the variable"), Ids(string.Empty), ProjectRoles.SuggestAll("Fix the animation", []).Count),
            ("rigger-animator", string.Empty, string.Empty, 0));
        AgentRole Role(string id, params string[] keywords) => new(id, id, string.Empty, keywords, [], null, string.Empty, string.Empty);
        Program.Equal("suggest all: roles whose words start at the same place keep their order, and a word counts only at the start of a word",
            (string.Join(", ", ProjectRoles.SuggestAll("jump and run", [Role("b", "jump"), Role("a", "jump", "run")]).Select(role => role.Id)),
                string.Join(", ", ProjectRoles.SuggestAll("the inanimate run", [Role("one", "animat"), Role("two", "run")]).Select(role => role.Id))),
            ("b, a", "two"));
        Program.Equal("suggest all: Suggest still picks the one with the most words",
            ProjectRoles.Suggest("Make a mesh in Blender, then the run cycle animation and locomotion", roles)?.Id, "rigger-animator");
        Program.Equal("stems: where a stem starts, or -1", (Stems.IndexOf("a Jump", "jump"), Stems.IndexOf("inanimate", "animat"), Stems.IndexOf("x", string.Empty)), (2, -1, -1));
    }

    /// <summary>A worker's new session is told the task's state and its step; the one that takes over a failed step is told what failed.</summary>
    private static void StepBriefBuilt()
    {
        var workspace = TempFolder();
        try
        {
            var record = new AgentTaskRecord
            {
                Goal = "Add the dash", Brief = "Add the dash to the movement component.", Workspace = workspace,
                Plan = [new AgentPlanItem("Rename the sprites", true, false, null, null, "GLM"), new AgentPlanItem("Add the dash ability", false, true, null, null, "Codex")]
            };
            var state = TaskState.Build(record, null, workspace);
            var step = new TeamStep { Text = "Add the dash ability to the movement component", Files = ["Source/Movement", "Source/Dash.cpp"] };
            Program.Equal("step brief: the task state, the step with its files, and the limit of the work",
                TaskState.StepBrief(record, null, workspace, step),
                state + "\n\n## Your step\nAdd the dash ability to the movement component\nfiles: Source/Movement, Source/Dash.cpp\n" + TeamPlan.StepEnd);
            Program.Equal("step brief: a step without files has no files line",
                TaskState.StepBrief(record, null, workspace, new TeamStep { Text = "Rename it" }), state + "\n\n## Your step\nRename it\n" + TeamPlan.StepEnd);
            record.Team = [new TeamStep { Text = "Model the prop", Worker = "Claude", HandedOver = "Content/Dash.fbx" }, new TeamStep { Text = "Rig it", Worker = "Codex", HandedOver = "  " }, step,
                new TeamStep { Text = "Wire it", Worker = "GLM", HandedOver = "not yet" }];
            Program.Equal("step brief: what the earlier steps handed over comes before the step, numbered by the plan; a later step's is not told",
                TaskState.StepBrief(record, null, workspace, step),
                state + "\n\n## Handed over by earlier steps\n- Step 1 (Claude): Content/Dash.fbx" +
                "\n\n## Your step\nAdd the dash ability to the movement component\nfiles: Source/Movement, Source/Dash.cpp\n" + TeamPlan.StepEnd);
            record.Team = [];
            step.EscalatedFrom = "GLM";
            step.Failure = " Failed after 0:42\nerror CS0103: the name 'dash' does not exist ";
            Program.Equal("step brief: the worker that takes the step over is told what the one before it ended in",
                TaskState.StepBrief(record, null, workspace, step),
                state + "\n\n## Your step\nAdd the dash ability to the movement component\nfiles: Source/Movement, Source/Dash.cpp\n" +
                "The GLM tried this step and failed: Failed after 0:42\nerror CS0103: the name 'dash' does not exist\n" + TeamPlan.StepEnd);
            step.Failure = null;
            Program.Equal("step brief: an escalation with nothing to tell adds no line",
                TaskState.StepBrief(record, null, workspace, step).Contains("tried this step"), false);
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>An agent whose runs do what the check says, for what Link does around a run; it keeps the tasks it got.</summary>
    private sealed class ScriptedProvider(Func<AgentTask, int, AgentResult> run, AgentProviderKind kind = AgentProviderKind.ClaudeCode) : IAgentProvider
    {
        public List<AgentTask> Tasks { get; } = [];
        public AgentProviderKind Kind => kind;
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
