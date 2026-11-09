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
        SpendSplit();
        OneLoop();
        OneLoopHelpers();
        SpendReportOf();
        ReviewDiff();
        UsageHistoryDays();
        ContextBudget();
        Loops();
        Arguments();
        HandOff();
        await MidRunWordsAsync();
        await StreamAsync();
        await LoopRunAsync();
        await FailureAsync();
        await StopAsync();
        await LeftoversAsync();
        await GitUndoAsync();
        PerforceWorkspace();
        Questions();
        Images();
        await NoVersionControlAsync();
        await MapAsync();
        await FolderRunsAsync();
        ProjectCheck();
        await CheckCommandAsync();
        await CheckRetriesAsync();
        ContinueAndPermissions();
        Choices();
        NextSteps();
        await LocalPagesAsync();
        NoStopRules();
        AutoEffortPolicy();
        AutoEffortFailures();
        AutoEffortSaved();
        RolesRead();
        RolePick();
        RoleSession();
        RoleKit();
        RoleSkills();
        RolesSaved();
        TaskStateBuilt();
        TaskStateBudget();
        CompactSession();
        RemovedCustomModel();
        OwnAnswersLevel();
        PlanTaskTools();
        PlanTaskToolsAcrossRuns();
        PlanKeepsOlderRecords();
        StepGroupsSummary();
        StepMarks();
        TaskChangesCount();
        StatsReport();
        ReflectionLines();
        HarnessBuilt();
        HarnessWords();
        HarnessCardLine();
        StudioSettingsKept();
        StudioSettingsOdd();
        CardDraftKept();
        FirstRunWords();
        TeamPackRoundTrip();
        TeamPackLinks();
        TeamPackReplace();
        TeamPackRefusals();
        PlanHeadline();
        KnowledgePackLearned();
        KnowledgePackRoundTrip();
        KnowledgePackRefusals();
        CompactRuleReasons();
        TaskMetricsCounted();
        TaskExported();
        RoleSuggestAll();
        OldTrainingLoads();
        RunImagesFound();
        ToolPacksUsed();
        RoleColors();
        RoleColorFiles();
        HarnessContextMeter();
        StudioSidebarKept();
        LimitsHold();
        StartAgentRule();
        UsageSpentMeasured();
        HandoversKeepTheBrief();
        CompactAfterWait();
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
        Program.Equal("choices: Claude's families first, then full names, Haiku 5.5 among them",
            (claude.Models[0], claude.Models.Any(model => model.Value == "claude-haiku-5-5")), (new AgentChoice("opus", "Opus · latest"), true));

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
                Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Fix",
                Images = [@"C:\shot.png"], Model = "opus", Effort = "high", AutoApprove = true
            },
            new WorkspaceBefore(@"C:\Work", true, new Dictionary<string, string>(), DateTime.UtcNow),
            new AgentResult { SessionId = "s1", Succeeded = true },
            []);
        var next = AgentCoordinator.ContinueTask(run, "  and the jump too \n");
        Program.Equal("continue: the same session, model and effort, the reply as the prompt, no images",
            (next!.ResumeSessionId, next.Prompt, next.Model, next.Effort, next.AutoApprove, next.Images.Count),
            ("s1", "and the jump too", "opus", "high", true, 0));
        Program.Equal("continue: words given while the agent worked say it was stopped for them",
            AgentCoordinator.Interrupted("  use the old jump  ").Contains("stopped you to say this:\nuse the old jump\n"), true);
        Program.Equal("continue: nothing without a reply or a session",
            (AgentCoordinator.ContinueTask(run, "  ") is null, AgentCoordinator.ContinueTask(run with { Result = new AgentResult() }, "go") is null), (true, true));

        var project = new LinkProject
        {
            Name = "Bigsteps", DataFolder = @"C:\Data", AgentReadTools = ["Edit", "Write", "PowerShell", "Read"], AgentDoTools = ["Write"]
        };
        project.Normalize();
        Program.Equal("permissions: an older Link's read-only list joins the one list, without repeats",
            (string.Join(",", project.AgentReadTools), string.Join(",", project.AgentDoTools)), ("", "Write,Edit,PowerShell,Read"));

        var settings = new AgentSettings { ClaudeModel = " opus ", CodexEffort = " ", DefaultAccess = (AgentAccessChoice)9 };
        settings.Normalize();
        Program.Equal("agent settings: trimmed, blank is the CLI's own, an unknown access is auto",
            (settings.ClaudeModel, settings.CodexEffort, settings.DefaultAccess, settings.Clone().ClaudeModel), ("opus", (string?)null, AgentAccessChoice.Auto, "opus"));
    }

    private static void ProjectCheck()
    {
        var project = new LinkProject { Name = "Nori", DataFolder = "C:\\Data", CheckCommand = "dotnet build" };
        var snapshot = new InputSnapshot { ProjectId = project.Id, ProjectName = project.Name, Text = "Fix the camera" };
        var edits = AgentCoordinator.BuildTask(new AgentPlan(AgentProviderKind.Codex, "C:\\Work", false, "Fix the camera"), snapshot, project);
        Program.Equal("check: the agent is told that Link runs the check",
            edits.Prompt.EndsWith(AgentCoordinator.CheckNote("dotnet build"), StringComparison.Ordinal) && edits.Prompt.Contains("`dotnet build`"), true);
        var old = JsonSerializer.Deserialize<LinkProject>("""{"Name":"Nori","DataFolder":"C:\\Data"}""")!;
        old.Normalize();
        Program.Equal("project: older JSON loads without a check", old.CheckCommand, null);
        // The agent an earlier Link kept per project is gone: its word in projects.json is skipped, the rest loads.
        var kept = JsonSerializer.Deserialize<LinkProject>("""{"Name":"Nori","DataFolder":"C:\\Data","AgentProvider":1,"WorkspaceRoot":"C:\\Work"}""")!;
        Program.Equal("project: an earlier Link's chosen agent is skipped when the project loads",
            (kept.Name, kept.WorkspaceRoot, JsonSerializer.Serialize(kept).Contains("AgentProvider", StringComparison.Ordinal)), ("Nori", "C:\\Work", false));
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
            var changes = new AgentChanges();
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
                Images = ["shot.png"],
                CheckCommand = "findstr /c:fixed state.txt"
            };
            var run = await new AgentCoordinator([fixing], changes, log.Add).RunAsync(task, null, new SyncProgress(_ => { }), CancellationToken.None);
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
                .RunAsync(task with { CheckCommand = "exit /b 5" }, null, new SyncProgress(_ => { }), CancellationToken.None);
            Program.Equal("check: two retries, then the red check ends the task",
                (never.Tasks.Count, red.Check?.Outcome, red.Check?.ExitCode), (3, (AgentCheckOutcome?)AgentCheckOutcome.Failed, (int?)5));

            foreach (var (name, quiet) in new[] { ("a plan", task with { PlanOnly = true }) })
            {
                var agent = new ScriptedProvider((_, _) => new AgentResult { SessionId = "s3", Succeeded = true });
                var none = await new AgentCoordinator([agent], changes, _ => { }).RunAsync(quiet, null, new SyncProgress(_ => { }), CancellationToken.None);
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
                new AgentChanges(), _ => { });
            var task = new AgentTask
            {
                Provider = AgentProviderKind.ClaudeCode,
                Workspace = workspace,
                Prompt = "map",
                AnswerFile = ProjectMap.RelativePath
            };
            var events = new List<AgentEvent>();
            var run = await agents.RunAsync(task, previous: null, new SyncProgress(events.Add), CancellationToken.None);
            Program.Equal("map: Link writes the answer without its code block",
                File.ReadAllText(ProjectMap.PathIn(workspace)), "# Sample\n\n- a.txt: the letter a\n");
            Program.Equal("map: the file is the run's one change, new and undoable",
                string.Join(",", run.Changes.Select(change => $"{Path.GetFileName(change.Path)}:{change.Kind}:{change.Undoable}")), "map.md:??:True");
            Program.Equal("map: Activity says Link wrote it", events.Any(e => e.Kind == AgentEventKind.Status && e.Text.StartsWith("Link wrote docs/map.md")), true);
            await agents.Changes.UndoAsync(run.Before, run.Changes, CancellationToken.None);
            Program.Equal("map: undo takes it away", File.Exists(ProjectMap.PathIn(workspace)), false);

            var failed = await new AgentCoordinator([ScriptedProvider.Answering("# Half a map", succeeded: false)], agents.Changes, _ => { })
                .RunAsync(task, previous: null, new SyncProgress(_ => { }), CancellationToken.None);
            Program.Equal("map: a failed run writes nothing", (File.Exists(ProjectMap.PathIn(workspace)), failed.Changes.Count), (false, 0));
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    /// <summary>
    /// Tasks side by side: runs that edit go one at a time in a folder, and a folder inside another is the same; what
    /// changes while a read-only run reads is never its own.
    /// </summary>
    private static async Task FolderRunsAsync()
    {
        var root = TempFolder();
        var workspace = Path.Combine(root, "game");
        Program.Equal("folders: the same folder, one inside the other, a drive's root, and a sibling",
            (AgentCoordinator.SameFolder(workspace, workspace + Path.DirectorySeparatorChar), AgentCoordinator.SameFolder(Path.Combine(workspace, "Art"), workspace),
                AgentCoordinator.SameFolder(Path.GetPathRoot(workspace)!, workspace), AgentCoordinator.SameFolder(workspace, workspace + "2")),
            (true, true, true, false));
        try
        {
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "a.txt"), "a");
            // Another task's run writes a file while this one works.
            var agents = new AgentCoordinator([new ScriptedProvider((task, _) =>
            {
                File.WriteAllText(Path.Combine(task.Workspace, $"other-{task.PlanOnly}.txt"), "theirs");
                return new AgentResult { SessionId = "s1", Succeeded = true };
            })], new AgentChanges(), _ => { });
            var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "look" };
            var plan = await agents.RunAsync(task with { PlanOnly = true }, previous: null, new SyncProgress(_ => { }), CancellationToken.None);
            Program.Equal("folders: every run, a plan too, finds what changed during it: no run is read only",
                string.Join(",", plan.Changes.Select(change => Path.GetFileName(change.Path))), "other-True.txt");
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
            var changes = new AgentChanges();
            var before = await changes.CaptureAsync(workspace, CancellationToken.None);
            Program.Equal("git: a repository is found", before.IsGit, true);

            // The agent's work: one clean file changed, one new, and the user's own edit changed further.
            File.WriteAllText(Path.Combine(workspace, "clean.txt"), "agent");
            File.WriteAllText(Path.Combine(workspace, "new.txt"), "agent");
            File.WriteAllText(Path.Combine(workspace, "mine.txt"), "my edit and the agent's");
            File.SetLastWriteTimeUtc(Path.Combine(workspace, "mine.txt"), DateTime.UtcNow.AddMinutes(1));
            var found = await changes.CompareAsync(before, CancellationToken.None);
            Program.Equal("git: the agent's changes are found",
                string.Join(",", found.OrderBy(change => change.Path).Select(change => $"{Path.GetFileName(change.Path)}:{change.Undoable}")),
                "clean.txt:True,mine.txt:False,new.txt:True");
            // The next run of the task changes one of those again and adds one: its turn shows only these two.
            var marks = AgentChanges.Marks(found);
            File.WriteAllText(Path.Combine(workspace, "new.txt"), "agent, again");
            File.SetLastWriteTimeUtc(Path.Combine(workspace, "new.txt"), DateTime.UtcNow.AddMinutes(2));
            File.WriteAllText(Path.Combine(workspace, "next.txt"), "agent");
            var next = await changes.CompareAsync(before, CancellationToken.None);
            Program.Equal("git: a run's own changes are told from the task's",
                (next.Count, string.Join(",", AgentChanges.Own(next, marks).Select(change => Path.GetFileName(change.Path)).Order())), (4, "new.txt,next.txt"));
            File.Delete(Path.Combine(workspace, "next.txt"));
            var diff = await changes.DiffAsync(before, found, stat: false, 20_000, CancellationToken.None) ?? string.Empty;
            Program.Equal("git: a run's diff is its own work, the user's edit before it not; the user's index is as it was",
                (before.DiffsOwnWork, diff.Contains("-my edit", StringComparison.Ordinal), diff.Contains("+my edit and the agent's", StringComparison.Ordinal),
                    diff.Contains("-mine", StringComparison.Ordinal), diff.Contains("+agent", StringComparison.Ordinal), await GitAsync(workspace, "diff", "--cached", "--quiet")),
                (true, true, true, false, true, 0));

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

    private static async Task NoVersionControlAsync()
    {
        var root = TempFolder();
        var workspace = Path.Combine(root, "scene");
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "bin"));
            File.WriteAllText(Path.Combine(workspace, "scene.blend"), "land v1");
            File.WriteAllText(Path.Combine(workspace, "old.txt"), "old");
            File.WriteAllText(Path.Combine(workspace, "bin", "skipped.dll"), "build output");
            var changes = new AgentChanges();
            var before = await changes.CaptureAsync(workspace, CancellationToken.None);
            Program.Equal("no vcs: a folder without git is not versioned", before.IsGit, false);

            File.WriteAllText(Path.Combine(workspace, "scene.blend"), "land v2, longer");
            File.WriteAllText(Path.Combine(workspace, "land.fbx"), "export");
            File.WriteAllText(Path.Combine(workspace, "bin", "skipped.dll"), "build output again");
            File.Delete(Path.Combine(workspace, "old.txt"));
            Program.Equal("no vcs: a run's changes are told by size and time, build output not, and none is undoable",
                string.Join(",", (await changes.CompareAsync(before, CancellationToken.None))
                    .Select(change => $"{Path.GetFileName(change.Path)}:{change.Kind}:{change.Undoable}").Order()),
                "land.fbx:??:False,old.txt:D:False,scene.blend:M:False");
        }
        finally
        {
            DeleteFolder(root);
        }
    }

    private static void PerforceWorkspace()
    {
        // In a workspace with a .p4config: the agent may open files with p4 only with edits, Codex reaches the server.
        var workspace = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".p4config"), "P4CLIENT=test");
            var nested = Directory.CreateDirectory(Path.Combine(workspace, "Content")).FullName;
            Program.Equal("p4: a folder below a .p4config is in Perforce, one elsewhere is not",
                (AgentChanges.IsPerforce(nested), AgentChanges.IsPerforce(Path.GetTempPath())), (true, false));
            var project = new LinkProject { Name = "Game", DataFolder = workspace, AgentDoTools = ["Read"] };
            Program.Equal("p4: the agent may look and open files, beside the project's rules",
                (AgentCoordinator.Tools(project, nested).Contains("Bash(p4 edit:*)"), AgentCoordinator.Tools(project, nested).Contains("Bash(p4 opened:*)"),
                    AgentCoordinator.Tools(project, nested).Contains("Read")),
                (true, true, true));
            Program.Equal("p4: never submit, revert or sync on the agent's own",
                AgentCoordinator.Tools(project, nested).Any(rule => rule.Contains("submit") || rule.Contains("revert") || rule.Contains("sync")),
                false);
            var codex = new CodexProvider(() => new AgentSettings(), _ => { });
            var task = new AgentTask { Provider = AgentProviderKind.Codex, Workspace = nested, Prompt = "Fix" };
            Program.Equal("p4: Codex may reach the Perforce server, in a plan too",
                (string.Join(' ', codex.Arguments(task)).Contains("-c sandbox_workspace_write.network_access=true"),
                    string.Join(' ', codex.Arguments(task with { PlanOnly = true })).Contains("network_access")),
                (true, true));
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

    /// <summary>Runs git in the folder; its exit code.</summary>
    private static async Task<int> GitAsync(string folder, params string[] arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true })!;
        await git.WaitForExitAsync();
        return git.ExitCode;
    }

    private static void ClaudeFixtures()
    {
        var read = Parse(new ClaudeEventParser(), "claude-read.jsonl");
        Program.Equal("Claude: the session id comes from init", read.Parser.SessionId, "5f0c7a2e-1b3d-4c8e-9a61-0d2f4e6b8c10");
        Program.Equal("Claude: init names the model and MCP servers",
            read.Events[0].Text, "Model claude-sonnet-5-5; MCP: blender (connected)");
        Program.Equal("Claude: the model the session runs on is kept, for a family it was asked by", read.Parser.Model, "claude-sonnet-5-5");
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
        Program.Equal("Codex: PowerShell the agent calls inside Codex's own shows the command, its escaped backslashes single",
            CodexEventParser.Unwrap("""powershell.exe -Command "\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command '& \"C:\\Blender\\blender.exe\" -b'" """),
            """& "C:\Blender\blender.exe" -b""");
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
            "turns 10, tools 9, helpers 0, searches 5 (4 before a change), tool errors 1, tokens in 157371 (cached 148230) out 2105, cost $0.1834");

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
    /// Where a run's tokens went: Claude Code tells each request's size and cache, so the growth splits between what
    /// came in and a lost cache shows as cold; a subagent's messages are its own; a compaction leaves the CLI's prompt
    /// and the summary; Codex's text sizes stand in until its log tells the first request; pictures by their pixels.
    /// </summary>
    /// <summary>
    /// The days of work: runs of one day, project and agent add up, another agent is a day of its own, days past 90 go
    /// as the next run is saved, and the file reads back as it was written.
    /// </summary>
    private static void UsageHistoryDays()
    {
        var folder = TempFolder();
        try
        {
            var file = Path.Combine(folder, "usage-history.json");
            var project = Guid.NewGuid();
            var today = DateTimeOffset.Now;
            var history = new UsageHistory(file);
            AgentRunRecord Run(AgentProviderKind provider, DateTimeOffset at, long input, long cached, long output) => new()
            {
                Provider = provider, StartedAt = at, Duration = TimeSpan.FromMinutes(2), InputTokens = input, CachedTokens = cached,
                OutputTokens = output, CostUsd = 0.5m, Spent = [new UsageSpent("5 h", 1.5)],
                Spend = SpendMeter.Split(new SpendParts { Files = 1 }, new TokenCount(input - cached, cached, output))
            };
            history.Add(project, Run(AgentProviderKind.ClaudeCode, today.AddDays(-120), 1000, 0, 0));
            history.Add(project, Run(AgentProviderKind.ClaudeCode, today, 2000, 1000, 10));
            history.Add(project, Run(AgentProviderKind.ClaudeCode, today, 1000, 0, 0));
            history.Add(project, Run(AgentProviderKind.Codex, today, 500, 0, 0));
            history.Add(Guid.NewGuid(), Run(AgentProviderKind.Codex, today, 700, 0, 0));
            var days = new UsageHistory(file).Since(DateOnly.FromDateTime(DateTime.Today).AddDays(-200), project);
            var claude = days.Single(day => day.Provider == AgentProviderKind.ClaudeCode);
            Program.Equal("Usage history: one day per project and agent, an old day dropped, read back from the file",
                (days.Count, claude.Runs, claude.Uncached, claude.Cached, claude.Output, claude.CostUsd, claude.Time, claude.Windows["5 h"]),
                (2, 2, 2000L, 1000L, 10L, (decimal?)1.0m, TimeSpan.FromMinutes(4), 3.0));
            Program.Equal("Usage history: the day's split adds up to its ≈ tokens", (claude.Spend.Files, claude.Equivalent), (2100L, 2150.0));
            Program.Equal("Usage history: all projects together", new UsageHistory(file).Since(DateOnly.FromDateTime(DateTime.Today)).Count, 3);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void ReviewDiff()
    {
        var git = DiffReview.Parse("""
            diff --git a/src/a.cs b/src/a.cs
            index 1111111..2222222 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -10,4 +10,5 @@ class A
             using System;
            --- old comment
            +var x = 2;
            +var y = 3;
             }
            diff --git a/logo.png b/logo.png
            Binary files a/logo.png and b/logo.png differ
            diff --git a/gone.cs b/gone.cs
            deleted file mode 100644
            --- a/gone.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            """);
        var a = git[0];
        Program.Equal("Review: a git diff's files, their numbers, and each line's place in the old and the new file; a removed line may start with --",
            (git.Count, a.Path, a.Added, a.Removed, a.Lines[2].Kind, a.Lines[2].Old, a.Lines[3].New, a.Lines[5].Old, a.Lines[5].New, git[1].Lines[0].Kind, git[2].Path, git[2].Removed),
            (3, "src/a.cs", 2, 1, DiffLineKind.Removed, (int?)11, (int?)11, (int?)12, (int?)13, DiffLineKind.Note, "gone.cs", 1));

        var p4 = DiffReview.Parse("--- //depot/a.cs\t2026-01-01\n+++ C:\\ws\\a.cs\t2026-01-02\n@@ -1,2 +1,2 @@\n-one\n+uno\n two\n--- //depot/b.cs\t2026-01-01\n+++ C:\\ws\\b.cs\t2026-01-02\n@@ -3 +3 @@\n-x\n+y\n?? new.txt");
        Program.Equal("Review: Perforce's files follow each other with no header; the hunk's counts tell where one ends; an untracked file is a note",
            (p4.Count, p4[0].Path, p4[0].Lines.Count, p4[1].Path, p4[1].Added, p4[2].Path, p4[2].Lines[0].Kind),
            (3, "C:\\ws\\a.cs", 4, "C:\\ws\\b.cs", 1, "new.txt", DiffLineKind.Note));

        var added = DiffReview.NewFile("new.cs", "one\r\ntwo\n");
        Program.Equal("Review: a new file is all added lines", (added.Added, added.Lines[1].New, added.Lines[1].Text), (2, (int?)2, "two"));

        var message = DiffReview.Message([
            new ReviewComment("src/a.cs", a.Lines[3], "Name it count"),
            new ReviewComment(null, null, "Keep it small"),
            new ReviewComment("src/a.cs", a.Lines[2], "Why was this removed?\nIt explained the hack"),
            new ReviewComment("src/a.cs", a.Lines[1], "   ")
        ]);
        Program.Equal("Review: the comments as the agent's next words, the general one first, each line quoted with its place; an empty one left out",
            message, """
            Review of your changes. Address each comment, then say what you changed:

            - Keep it small

            - src/a.cs:11
              `+ var x = 2;`
              Name it count

            - src/a.cs:11 (removed line)
              `- -- old comment`
              Why was this removed?
              It explained the hack
            """.Replace("\r\n", "\n"));
    }

    private static void SpendReportOf()
    {
        var now = DateTimeOffset.Now;
        var task = new AgentTaskRecord
        {
            Goal = "Fix the build",
            Workspace = "C:\\work",
            Runs =
            [
                new AgentRunRecord
                {
                    Provider = AgentProviderKind.ClaudeCode, StartedAt = now.AddHours(-1), Duration = TimeSpan.FromMinutes(3), InputTokens = 60_000,
                    OutputTokens = 100, Spend = new SpendParts { Files = 50_000, Base = 10_000 }, Rereads = [new FileReads("src/a.cs", 3, 12_000)],
                    SearchesBeforeChange = 20, ColdTokens = 25_000, CacheMinutes = 5, ContextTokens = 210_000, ContextCurve = [10, 20],
                    Effort = "high", Spent = [new UsageSpent("5 h", 3.0)], Turns = 4, ToolCalls = 9
                },
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, StartedAt = now.AddMinutes(-30), Duration = TimeSpan.FromMinutes(2), InputTokens = 1000, Judge = true }
            ]
        };
        var report = SpendReport.Of(task, _ => "Claude Code", 250_000, hasMap: false, canCompact: true);
        Program.Equal("Spend report: the split runs add up, a run from before the split is its tokens' worth, the last curve is drawn",
            (report.Parts.Files, report.Unsplit, report.WindowPoints, report.Turns, report.Budget, report.CurveLabel, report.Runs[1].Name, report.Runs[1].Parts.Base),
            (50_000L, 1000.0, (double?)3.0, 4, (int?)250, "Last run · 2 requests", "Review", 1000L));
        Program.Equal("Spend report: the largest tips first, each fix offered once",
            string.Join(" | ", report.Tips.Select(tip => $"{tip.Fix}: {tip.Text}")),
            "Compact: The context reached 210k of the 250k budget | None: The cache went cold: ~25k read again at full price"
            + " | Map: a.cs was read 3 times: ~8k read again | None: 20 searches before the first edit");
        Program.Equal("Spend report: the cache stays warm five minutes after the last worker's run, not the review's", report.CacheWarmUntil, now.AddMinutes(-60 + 3 + 5));

        AgentLimit Limit(double used, bool reached) => new(AgentProviderKind.ClaudeCode, reached ? "week" : "5 h", used, now.AddHours(2.5), reached, now);
        var gauges = SpendReport.Gauges([Limit(60, false), Limit(100, true)], task, now);
        Program.Equal("Spend report: a window used faster than it resets warns; the task's part counts since it began",
            (gauges[0].Label, gauges[0].Used, gauges[0].Mine, gauges[0].Warn, gauges[0].Note.StartsWith("full ", StringComparison.Ordinal), gauges[1].Warn, gauges[1].Note.StartsWith("reached", StringComparison.Ordinal)),
            ("5-hour window", 60.0, 3.0, true, true, true, true));

        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = SpendReport.Days([new UsageDay { Day = today.AddDays(-2), Runs = 1, Spend = new SpendParts { Files = 100 } }, new UsageDay { Day = today, Runs = 2, Uncached = 1000 }],
            today.AddDays(-3), today);
        Program.Equal("Spend report: every day of the range is there, an empty one too, and a day without a split counts as base",
            (days.Count, days[0].Runs, days[1].Parts.Files, days[3].Parts.Base, days[3].Runs), (4, 0, 100L, 1000L, 2));
    }

    /// <summary>A Claude Code work run gets Link's helper subagent; a plan first, a judge and Link's own answers do not.</summary>
    private static void OneLoopHelpers()
    {
        // Codex as a tool is looked for in the install places Windows names; its server's words are checked in OneLoop.
        var settings = new AgentSettings { CodexHelper = false };
        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var work = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = TempFolder(), Prompt = "Rename the props" };
        string? Agents(AgentTask task) => claude.Arguments(task).ToList() is var arguments && arguments.IndexOf("--agents") is var at and >= 0 ? arguments[at + 1] : null;
        var file = Agents(work);
        Program.Equal("one loop: a work run gets the mechanic on the Light model, from a file",
            (file is not null && File.ReadAllText(file).Contains("\"mechanic\"") && File.ReadAllText(file).Contains("\"model\":\"claude-haiku-5-5\"")), true);
        Program.Equal("one loop: a plan first, a judge, Link's own answer and the setting off get no helper",
            (Agents(work with { PlanOnly = true }), Agents(work with { Judge = true }), Agents(work with { WithoutMcp = true }), Agents(work with { MaxTurns = 3 })),
            ((string?)null, (string?)null, (string?)null, (string?)null));
        settings.Helpers = false;
        Program.Equal("one loop: Helpers off, no subagents", Agents(work), (string?)null);
        DeleteFolder(work.Workspace);
    }

    /// <summary>One session that plans and delegates: Codex as its tool, what a helper's events show, and the final check.</summary>
    private static void OneLoop()
    {
        var work = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = TempFolder(), Prompt = "Rename the props" };
        var codex = Path.Combine(work.Workspace, "codex.exe");
        File.WriteAllText(codex, string.Empty);
        var claude = new ClaudeCodeProvider(() => new AgentSettings { CodexPath = codex }, _ => { });
        Program.Equal("one loop: Codex as a helper runs in its sandbox, asks nothing, without the desktop's add-ons",
            string.Join(" ", ClaudeCodeProvider.CodexConfig(work)),
            "-c sandbox_mode=workspace-write -c approval_policy=never " + string.Join(" ", CodexProvider.FeaturesOff.Select(feature => $"-c features.{feature}=false")));
        var variables = claude.EnvironmentVariables(work);
        var script = variables.TryGetValue("PATH", out var path) ? Path.Combine(path.Split(Path.PathSeparator)[0], ClaudeCodeProvider.CodexCommand) : string.Empty;
        Program.Equal("one loop: a work run may call link-codex, a script first on its PATH that runs the found Codex; a plan first may not hand it work",
            (claude.Arguments(work)[^1], File.Exists(script) && !File.ReadAllText(script).Contains('\r'), variables["LINK_CODEX"],
                claude.Arguments(work with { PlanOnly = true }).Contains(ClaudeCodeProvider.CodexRule)),
            (ClaudeCodeProvider.CodexRule, true, codex.Replace('\\', '/'), false));

        var judge = work with { Judge = true };
        Program.Equal("Codex reads: a plan first may only scout and a final check only review, in the read-only sandbox whatever the run's access",
            (claude.Arguments(work with { PlanOnly = true })[^1], claude.Arguments(judge)[^1],
                variables["LINK_CODEX_SCOUT"].StartsWith("-c sandbox_mode=read-only", StringComparison.Ordinal),
                claude.EnvironmentVariables(work with { NoStop = true })["LINK_CODEX_REVIEW"].StartsWith("-c sandbox_mode=read-only", StringComparison.Ordinal),
                File.ReadAllText(script).Split("scout)")[1].Split(";;")[0].Contains("--sandbox read-only")),
            (ClaudeCodeProvider.ScoutRule, ClaudeCodeProvider.ReviewRule, true, true, true));
        string? Agents(AgentTask task) => claude.Arguments(task).ToList() is var arguments && arguments.IndexOf("--agents") is var at and >= 0 ? File.ReadAllText(arguments[at + 1]) : null;
        Program.Equal("Codex reads: a plan first gets the scout alone, a work run the mechanic and the scout, a judge no subagent",
            (Agents(work with { PlanOnly = true }) is { } scout && scout.Contains("\"scout\"") && !scout.Contains("\"mechanic\""),
                Agents(work) is { } both && both.Contains("\"scout\"") && both.Contains("\"mechanic\""), Agents(judge)),
            (true, true, (string?)null));
        Program.Equal("Codex reads: the final check is told of its second reader, a work run of the scout",
            (claude.StandardInput(judge).Contains("link-codex review"), claude.StandardInput(work).Contains("`scout`")), (true, true));

        var parser = claude.CreateParser(work);
        var events = new[]
            {
                """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Agent","input":{"subagent_type":"mechanic","description":"Rename the props","prompt":"Rename SM_* files"}}]}}""",
                """{"type":"assistant","parent_tool_use_id":"t1","message":{"id":"s1","content":[{"type":"text","text":"Renaming."},{"type":"tool_use","id":"s2","name":"TodoWrite","input":{"todos":[{"content":"Rename","status":"in_progress"}]}},{"type":"tool_use","id":"s3","name":"Bash","input":{"command":"git mv a b"}}]}}""",
                """{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"link-codex <<'EOF'\nAdd the dash\nEOF"}}]}}"""
            }
            .SelectMany(parser.Read)
            .ToArray();
        Program.Equal("one loop: a hand-over names the helper and its part; the helper's steps show, its words and its own list do not",
            string.Join(" | ", events.Take(2).Select(each => $"{each.Kind}:{each.Text}")), "Tool:Agent: mechanic · Rename the props | Tool:Bash: git mv a b");
        Program.Equal("one loop: the run counts its hand-overs, to the mechanic and to Codex",
            (parser.Stats.HelperCalls, CliAgentProvider.LogStats(parser.Stats).Contains("helpers 2,")), (2, true));

        AgentPlanItem[] plan = [new("Model", true, false), new("Rig", true, false), new("Import", true, false)];
        AgentTaskRecord Record(IEnumerable<AgentPlanItem> steps) => new() { Goal = "A dash prop", Workspace = work.Workspace, Plan = [.. steps] };
        var record = Record(plan);
        var done = new AgentRun(work, new WorkspaceBefore(work.Workspace, true, new Dictionary<string, string>(), DateTime.UtcNow),
            new AgentResult { Succeeded = true, SessionId = "s" }, [new AgentChange("a.cs", "M", true)]);
        Program.Equal("final check: due when the task's own plan of three steps is done with changes; not before, not without changes, not for a judge",
            (FinalCheck.Due(record, done), FinalCheck.Due(Record([plan[0], plan[1], plan[2] with { Done = false }]), done),
                FinalCheck.Due(record, done with { Changes = [] }), FinalCheck.Due(Record([plan[0], plan[1]]), done),
                FinalCheck.Due(record, done with { Task = work with { Judge = true } })),
            (true, false, false, false, false));
        Program.Equal("final check: the last verdict line decides; none is null; the findings go on without it",
            (FinalCheck.NeedsFix("1. a.cs:4 null\nVerdict: fix"), FinalCheck.NeedsFix("All good.\nVerdict: clean"), FinalCheck.NeedsFix("Fine."),
                FinalCheck.Fix("1. a.cs:4 null\nVerdict: fix").Contains("1. a.cs:4 null\n\nFix the failed steps"), FinalCheck.Fix("x\nVerdict: fix").Contains("Verdict")),
            ((bool?)true, (bool?)false, (bool?)null, true, false));
        var prompt = FinalCheck.Prompt("<!-- note -->Check it.", "A dash prop", plan, "passed", ["a.cs"], "+x");
        Program.Equal("final check: the request without its notes, then the task, its plan, the check, the files and the diff",
            prompt, "Check it.\n\n## The task\n\nA dash prop\n\n## Its plan\n\n- Model\n- Rig\n- Import\n\n## The project's check\n\npassed\n\n## Changed files\n\na.cs\n\n## Diff\n\n```diff\n+x\n```");

        Directory.CreateDirectory(Path.Combine(work.Workspace, "Game", ".git"));
        Program.Equal("git: a workspace above its repository is told where git lives; one inside a repository is not",
            (AgentRules.Before("t", work.Workspace, false).Contains("run git in `Game/`."), AgentChanges.RepositoriesInside(Path.Combine(work.Workspace, "Game")).Count), (true, 0));

        Directory.CreateDirectory(Path.Combine(work.Workspace, "docs"));
        File.WriteAllText(ProjectMap.PathIn(work.Workspace), "# Link's map");
        var ownOnly = ProjectMap.Relative(work.Workspace, ProjectMap.Find(work.Workspace)!);
        Directory.CreateDirectory(Path.Combine(work.Workspace, "Game", "docs"));
        File.WriteAllText(Path.Combine(work.Workspace, "Game", "docs", "README.md"), "# The team's docs");
        var rules = AgentRules.Before("t", work.Workspace, false);
        Program.Equal("map: one map; the repository's own wins over Link's, and the rules name it for the skills' docs/map.md",
            (ownOnly, ProjectMap.Relative(work.Workspace, ProjectMap.Find(work.Workspace)!), rules.Contains("The project's map: `Game/docs/README.md`"),
                rules.Contains("names `docs/map.md`, it means this file")),
            ("docs/map.md", "Game/docs/README.md", true, true));
        DeleteFolder(work.Workspace);
    }

    private static void SpendSplit()
    {
        var claude = new ClaudeEventParser();
        claude.Meter.Start(3500, resumed: false);
        foreach (var line in new[]
                 {
                     """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Reading."}],"usage":{"input_tokens":1000,"cache_creation_input_tokens":20000,"cache_read_input_tokens":0,"output_tokens":5}}}""",
                     """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a.cs"}}],"usage":{"input_tokens":1000,"cache_creation_input_tokens":20000,"cache_read_input_tokens":0,"output_tokens":5}}}""",
                     """{"type":"assistant","parent_tool_use_id":"t9","message":{"id":"s1","content":[{"type":"text","text":"A subagent."}],"usage":{"input_tokens":50000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5}}}""",
                     $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"{{{new string('x', 7000)}}}"}]}}""",
                     """{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"Done."}],"usage":{"input_tokens":100,"cache_creation_input_tokens":2000,"cache_read_input_tokens":21000,"output_tokens":20}}}""",
                     """{"type":"assistant","message":{"id":"m3","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":100,"cache_creation_input_tokens":23100,"cache_read_input_tokens":0,"output_tokens":35}}}""",
                     """{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"s1","num_turns":3,"duration_api_ms":41000,"usage":{"input_tokens":1200,"cache_creation_input_tokens":45100,"cache_read_input_tokens":21000,"output_tokens":60,"cache_creation":{"ephemeral_1h_input_tokens":45100,"ephemeral_5m_input_tokens":0}}}"""
                 })
        {
            claude.Read(line);
        }

        var stats = claude.Stats;
        var spend = SpendMeter.Split(stats.SpendInput, new TokenCount(46_300, 21_000, 60));
        Program.Equal("Spend: the first request's growth beyond the brief is the CLI's own; a file read is files; a cold request reads all again",
            (spend.Brief, spend.Base, spend.Files, spend.Writing, spend.Total), (2100L, 42_099L, 4183L, 318L, 48_700L));
        Program.Equal("Spend: the curve and the cold input are measured; a subagent's request is not the session's",
            (string.Join(",", stats.ContextCurve), stats.ColdTokens, claude.Meter.Requests), ("21,23,23", (long?)23_100, 3));
        Program.Equal("Spend: the result tells the cache writes, how long they last, and the requests' time",
            (stats.CacheWriteTokens, stats.CacheMinutes, stats.ApiTime), ((long?)45_100, (int?)60, (TimeSpan?)TimeSpan.FromSeconds(41)));

        var compacted = new ClaudeEventParser();
        foreach (var line in new[]
                 {
                     """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}],"usage":{"input_tokens":0,"cache_creation_input_tokens":30000,"cache_read_input_tokens":0,"output_tokens":5}}}""",
                     $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"{{{new string('x', 6993)}}}"}]}}""",
                     """{"type":"assistant","message":{"id":"m2","content":[],"usage":{"input_tokens":0,"cache_creation_input_tokens":2000,"cache_read_input_tokens":30000,"output_tokens":5}}}""",
                     """{"type":"system","subtype":"compact_boundary","compact_metadata":{"pre_tokens":32000}}""",
                     """{"type":"assistant","message":{"id":"m3","content":[],"usage":{"input_tokens":0,"cache_creation_input_tokens":1000,"cache_read_input_tokens":30000,"output_tokens":5}}}"""
                 })
        {
            compacted.Read(line);
        }

        var input = compacted.Stats.SpendInput!;
        Program.Equal("Spend: after a compaction the CLI's prompt stays and the summary is the model's writing; the files went",
            (input.Base, input.Files, input.Writing, input.Total), (36_000L, 1999L, 1001L, 39_000L));

        var codex = new CodexEventParser();
        codex.Meter.Start(3500, resumed: false);
        foreach (var line in new[]
                 {
                     """{"type":"turn.started"}""",
                     """{"type":"item.completed","item":{"id":"i0","type":"reasoning","text":"Plan it."}}""",
                     """{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"dotnet build","status":"in_progress"}}""",
                     $$$"""{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"dotnet build","aggregated_output":"{{{new string('x', 7000)}}}","exit_code":0,"status":"completed"}}""",
                     """{"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"Built."}}""",
                     """{"type":"turn.completed","usage":{"input_tokens":5000,"cached_input_tokens":1000,"output_tokens":100,"reasoning_output_tokens":40}}"""
                 })
        {
            codex.Read(line);
        }

        var estimated = codex.Stats;
        Program.Equal("Spend: Codex counts a request where the model speaks after a tool's output; sizes are text's, so nothing is measured",
            (codex.Meter.Requests, estimated.SpendInput!.Brief, estimated.SpendInput.Commands, estimated.SpendInput.Writing, estimated.ContextCurve.Count, estimated.ColdTokens),
            (2, 1100L, 2000L, 6L, 0, (long?)null));
        Program.Equal("Spend: Codex tells its thinking apart", estimated.ReasoningTokens, (long?)40);
        codex.Meter.AddBase(12_000 - codex.Meter.First);
        Program.Equal("Spend: the prompt a later curve tells is read fresh once and from the cache after", codex.Meter.Input.Base, 12_100L);

        Program.Equal("Spend: a run with tokens and no split puts its input in the base, its output in the writing",
            (SpendMeter.Split(null, new TokenCount(900, 1000, 10)).Base, SpendMeter.Split(null, new TokenCount(900, 1000, 10)).Writing), (1000L, 50L));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0x06, 0x20, 0, 0, 0x03, 0xE8, 8, 6, 0, 0, 0];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, .. new byte[14], 0xFF, 0xC0, 0, 0x11, 8, 0x03, 0xE8, 0x05, 0xDC, 3, .. new byte[12]];
        Program.Equal("Spend: a picture's size from the start of its PNG or JPEG; text that is neither tells none",
            (SpendMeter.ImageSize(Convert.ToBase64String(png)), SpendMeter.ImageSize(Convert.ToBase64String(jpeg)), SpendMeter.ImageSize("not base64!")),
            ((1568, 1000), (1500, 1000), (0, 0)));
        Program.Equal("Spend: a picture costs its pixels over 750 after the scale to 1568 px and 1.15 megapixels; an unknown one a full one",
            (Math.Round(SpendMeter.ImageTokens(1000, 1000)), Math.Round(SpendMeter.ImageTokens(3000, 2000)), SpendMeter.ImageTokens(0, 0)),
            (1333.0, 1533.0, 1600.0));

        var thin = SpendMeter.Thin([.. Enumerable.Range(0, 300)]);
        Program.Equal("Spend: a long curve keeps 120 points, its first and last", (thin.Count, thin[0], thin[^1]), (120, 0, 299));
        var joined = new AgentStats { SpendInput = new SpendParts { Files = 10 }, ContextCurve = [1, 2] }
            .Add(new AgentStats { SpendInput = new SpendParts { Files = 5, Images = 3 }, ContextCurve = [3], ColdTokens = 7 });
        Program.Equal("Spend: a run that goes on adds its split, its curve and its cold input",
            (joined.SpendInput!.Files, joined.SpendInput.Images, string.Join(",", joined.ContextCurve), joined.ColdTokens), (15L, 3L, "1,2,3", (long?)7));
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

            // Codex images: exec's events leave the image tool out; its log has the file and the prompt it was made from.
            var now = DateTimeOffset.Now;
            string Image(DateTimeOffset at, string status, string path, string failure) =>
                $$$"""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"Extension","kind":"image_gen.generation","id":"call_1","status":"{{{status}}}","revisedPrompt":"A red circle\non white","result":"iVBOR","failure":{{{failure}}},"savedPath":"{{{path}}}"},"completed_at_ms":{{{at.ToUnixTimeMilliseconds()}}}}}""";
            File.WriteAllLines(file,
            [
                Image(now.AddHours(-1), "completed", "C:\\\\old.png", "null"),
                Image(now, "completed", "C:\\\\new.png", "null"),
                Image(now, "failed", "", "{\"message\":\"blocked\"}"),
                """{"type":"response_item","payload":{"type":"message","content":[{"type":"output_text","text":"image_gen.generation item_completed"}]}}"""
            ]);
            Program.Equal("Codex images: only those finished since the run began, with their prompt or why they failed",
                string.Join(" | ", CodexProvider.SessionReport(folder, "abc", now.AddMinutes(-1)).Images),
                "CodexImage { Path = C:\\new.png, Prompt = A red circle\non white, Failure =  } | " +
                "CodexImage { Path = , Prompt = A red circle\non white, Failure = {\"message\":\"blocked\"} }");
            Program.Equal("Codex images: none are read when the run asks for none", CodexProvider.SessionReport(folder, "abc").Images.Count, 0);

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
        Program.Equal("budget: 250k by default, in older settings too", (settings.ContextBudget, JsonSerializer.Deserialize<AgentSettings>("{}")!.ContextBudget), (250_000, 250_000));
        var normalized = new[] { 0, -1, 49_999, 2_000_001, 50_000, 550_000, 2_000_000 }.Select(budget =>
        {
            var each = new AgentSettings { ContextBudget = budget };
            each.Normalize();
            return each.ContextBudget;
        });
        Program.Equal("budget: Auto and 50k to 2M are kept, anything else is the default",
            string.Join(", ", normalized), "0, 250000, 250000, 250000, 50000, 550000, 2000000");
        Program.Equal("budget: on Auto the limit is the model's window, a set budget is capped by it",
            (AgentContext.Limit(0, 1_000_000), AgentContext.Limit(0, null), AgentContext.Limit(400_000, 200_000), AgentContext.Line(150_000, null, 0)),
            ((long?)1_000_000, (long?)null, (long?)200_000, "Context 150k"));
        Program.Equal("budget: a copy keeps it", new AgentSettings { ContextBudget = 800_000 }.Clone().ContextBudget, 800_000);

        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var codex = new CodexProvider(() => settings, _ => { });
        var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Explain" };
        Program.Equal("budget: Claude Code compacts at it, through its auto-compact window",
            (claude.EnvironmentVariables(task with { ContextBudget = 400_000 }).GetValueOrDefault("CLAUDE_CODE_AUTO_COMPACT_WINDOW"),
                claude.EnvironmentVariables(task).ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW"),
                claude.EnvironmentVariables(task with { ContextBudget = 0 }).ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW")),
            ("400000", false, false));
        Program.Equal("budget: Claude Code takes no argument for it",
            claude.Arguments(task with { ContextBudget = 400_000 }).SequenceEqual(claude.Arguments(task)), true);

        var codexTask = task with { Provider = AgentProviderKind.Codex };
        Program.Equal("budget: Codex compacts at nine tenths of it, on resume too, and without a budget leaves it alone",
            (string.Join(' ', codex.Arguments(codexTask with { ContextBudget = 400_000 })).Contains("-c model_auto_compact_token_limit=360000"),
                string.Join(' ', codex.Arguments(codexTask with { ContextBudget = 400_000, ResumeSessionId = "t1" })).Contains("-c model_auto_compact_token_limit=360000"),
                string.Join(' ', codex.Arguments(codexTask)).Contains("compact") || string.Join(' ', codex.Arguments(codexTask with { ContextBudget = 0 })).Contains("compact")),
            (true, true, false));
    }

    /// <summary>The agent's word for its next step, as its rules ask it, and what Link does with the block.</summary>
    private static void NoStopRules()
    {
        var question = AgentQuestion.Split("""
            Which jump?
            ```link-ask
            {"question": "Which jump should stay?", "options": [{"label": "Keep the double jump"}, {"label": "Only one jump"}]}
            ```
            """).Question!;
        Program.Equal("No-stop picks the option the agent recommends, the first", NoStop.Pick(question), "Keep the double jump");
        Program.Equal("No-stop goes on with the step the agent named",
            NoStop.GoOn(AgentNext.Split("""
                ```link-next
                {"next": "wire the jump", "effort": "medium"}
                ```
                """).Next!), "Go on: wire the jump");

        AgentRunRecord Run(int files) => new() { Provider = AgentProviderKind.ClaudeCode, StartedAt = DateTimeOffset.Now, Files = [.. Enumerable.Repeat("a.cs", files)] };
        var record = new AgentTaskRecord { Goal = "g", Workspace = @"C:\Work", NoStop = true, Runs = [Run(1), Run(0)] };
        Program.Equal("No-stop takes a new choice", NoStop.WhyWait(record, 20, "b", "a"), null);
        Program.Equal("No-stop waits on the same choice twice", NoStop.WhyWait(record, 20, "a", "a") is not null, true);
        Program.Equal("No-stop waits after its turns in a row", NoStop.WhyWait(new AgentTaskRecord { Goal = "g", Workspace = "w", NoStopTaken = 20 }, 20, "b", null) is not null, true);
        record.Runs.AddRange([Run(0), Run(0)]);
        Program.Equal("No-stop waits after runs that changed no file", NoStop.WhyWait(record, 20, "b", "a") is not null, true);

        var settings = new AgentSettings { NoStopTurns = 0 };
        settings.Normalize();
        Program.Equal("No-stop settings: turns fall back to their default", settings.NoStopTurns, AgentSettings.DefaultNoStopTurns);
        var older = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>("""{"NoStopTurns":5,"NoStopDeny":["Bash(git push:*)"]}""")!;
        Program.Equal("Settings with the old deny list of Night mode still load", older.NoStopTurns, 5);
    }

    private static async Task LocalPagesAsync()
    {
        var packs = Path.Combine(Path.GetTempPath(), "link-pages-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(packs, "blender", "skills", "blender-review", "review.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "print('http://127.0.0.1:7331/')");
        var json = JsonSerializer.Serialize(script);
        var answer = "The wheel is built: mark the spokes.\n\n```link-page\n{\"label\": \"Mark changes on the model\", \"url\": \"http://127.0.0.1:7331/\", " +
            $"\"reopen\": [\"C:/Python312/python.exe\", {json}, \"open\", \"C:/t/wheel\", \"--no-browser\"], " +
            $"\"reply\": {{\"label\": \"Apply marks\", \"text\": \"Apply my open marks.\", \"attach\": [\"python\", {json}, \"notes\"]}}}}\n```\n" +
            "```link-page\n{\"label\": \"Elsewhere\", \"url\": \"https://example.com/\"}\n```\n```link-page\nnot json\n```";
        var (text, pages) = LocalPages.Split(answer);
        Program.Equal("pages: a link-page block becomes a button with its words and goes from the text; another site's page and a block that does not read do not",
            (text, string.Join(" | ", pages.Select(page => $"{page.Label} {page.Address.AbsoluteUri} {page.Reopen.Count} {page.Reply?.Label}: {page.Reply?.Text} +{page.Reply?.Attach?.Count}"))),
            ("The wheel is built: mark the spokes.\n\n```link-page\nnot json\n```",
                "Mark changes on the model http://127.0.0.1:7331/ 5 Apply marks: Apply my open marks. +3"));
        Program.Equal("pages: an answer without a block stays as it is, a bare address is no button",
            LocalPages.Split("Open at http://127.0.0.1:7331/\n").Pages.Count, 0);
        Program.Equal("pages: a reply without its words is no second button",
            LocalPages.Split("```link-page\n{\"label\": \"A\", \"url\": \"http://127.0.0.1:7331/\", \"reply\": {\"label\": \"Apply marks\"}}\n```").Pages[0].Reply, null);

        var frame = Path.Combine(Path.GetTempPath(), "link-frame-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(frame, []);
        try
        {
            Program.Equal("answers: an existing file named by its full path (or %TEMP%) is a link, a picture shows; anything else stays code",
                (MarkdownRenderer.LocalFile(frame), MarkdownRenderer.LocalFile("%TEMP%\\" + Path.GetFileName(frame)) is not null,
                    MarkdownRenderer.IsPicture(frame), MarkdownRenderer.LocalFile(Path.GetFileName(frame)), MarkdownRenderer.LocalFile(frame + ".gone"),
                    MarkdownRenderer.LocalFile("dotnet build")),
                (frame, true, true, (string?)null, (string?)null, (string?)null));
        }
        finally
        {
            File.Delete(frame);
        }

        var page = pages[0];
        var previous = ToolPacks.Folder;
        ToolPacks.Folder = packs;
        try
        {
            Program.Equal("pages: Link runs Python (or uv run) on a skill's script only, never another program or a script elsewhere",
                (LocalPages.ScriptOf(page.Reopen, null) is not null,
                    LocalPages.ScriptOf(["uv", "run", "--no-project", "python", script, "notes"], null) is not null,
                    LocalPages.ScriptOf(["cmd.exe", script, "open"], null),
                    LocalPages.ScriptOf(["uv", "run", "--with", "evil", "python", script], null),
                    LocalPages.ScriptOf(["python", Path.Combine(packs, "..", "evil.py")], null),
                    LocalPages.ScriptOf([], null)),
                (true, true, (string?)null, (string?)null, (string?)null, (string?)null));
        }
        finally
        {
            ToolPacks.Folder = previous;
            Directory.Delete(packs, true);
        }

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var up = await LocalPages.IsUpAsync(new Uri($"http://127.0.0.1:{port}/"));
        listener.Stop();
        Program.Equal("pages: a page that still listens is up, a stopped one is not",
            (up, await LocalPages.IsUpAsync(new Uri($"http://127.0.0.1:{port}/"))), (true, false));
    }

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
            ("medium", "medium", "medium", "the brief reads as a mechanical change (\"import\")"));
        Program.Equal("auto: the raise never goes above xhigh, and starts from high when the last effort is not known",
            (Choose(EffortPhase.Reply, last: "xhigh", failures: 1).Effort, Choose(EffortPhase.Reply, last: "max", failures: 1).Effort, Choose(EffortPhase.Steer, failures: 1).Effort),
            ("xhigh", "xhigh", "high"));
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
            (("high", "the brief reads as an unclear failure (\"crash\")"), "the brief reads as a design task (\"system\")", "high"));
        Program.Equal("auto: a mechanical task is low", Choose(EffortPhase.New, brief: "Rename the Player class"), ("low", "the brief reads as a mechanical change (\"rename\")"));
        Program.Equal("auto: the brief is read in any case", Choose(EffortPhase.New, brief: "REFACTOR the input").Effort, "high");
        Program.Equal("auto: high wins over low", Choose(EffortPhase.New, brief: "Rewrite the docs"), ("high", "the brief reads as a design task (\"rewrite\")"));
        Program.Equal("auto: Russian stems",
            (Choose(EffortPhase.New, brief: "Переименуй поле"), Choose(EffortPhase.New, brief: "Почему падает игра"), Choose(EffortPhase.New, brief: "Спроектируй систему").Effort,
                Choose(EffortPhase.New, brief: "Опечатка в описании").Effort, Choose(EffortPhase.New, brief: "Переименуй систему").Effort),
            (("low", "the brief reads as a mechanical change (\"переимен\")"), ("high", "the brief reads as an unclear failure (\"почему\")"), "high", "low", "high"));
        Program.Equal("auto: an indefinite word is no question", Choose(EffortPhase.New, brief: "Скиллы почему-то не видны, добавь двери").Effort, "medium");
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
            Choose(EffortPhase.New, brief: "Rename it", efforts: []), ((string?)null, "the brief reads as a mechanical change (\"rename\")"));
        Program.Equal("auto: a long brief is no mechanical change for one of its words",
            Choose(EffortPhase.New, brief: "Rework the SUV body in Blender so the windows are real openings with glass and the normals are right. " +
                "Before rebuilding, rename the current collection to SUV_prev and hide it, then export the FBX again."), ("medium", "a usual task"));
        Program.Equal("auto: a short brief names the first mechanical word of the list",
            AutoEffort.Choose(EffortPhase.New, "Bump version and add a comment", ["low", "medium", "high"], null, 0, null),
            ((string?)"low", "the brief reads as a mechanical change (\"comment\")"));
    }

    /// <summary>The runs that count as failed for Auto: failed, or the check failed; a good or stopped run clears them.</summary>
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
        Program.Equal("auto failures: a loop label counts for nothing: a steer or Stop in a loop does not raise",
            (AutoEffort.Failures([Run("Stopped after 4:00", looped: true)]), AutoEffort.Failures([Run("Done in 5:00", looped: true)])), (0, 0));
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
            "3D Artist|Build / Performance|Gameplay Developer|Level Artist|QA / Playtest|Rigger / Animator|Technical Designer|UI/UX Designer");
        Program.Equal("roles: each has a description, keywords, an effort and a body without the editor's note",
            builtIn.All(role => role.Description.Length > 0 && role.Keywords.Count > 0 && role.Effort is not null &&
                                role.Body.StartsWith("## Duties", StringComparison.Ordinal) && !role.Body.Contains("<!--")), true);
        var rigger = builtIn.First(role => role.Id == "rigger-animator");
        Program.Equal("roles: a file's front matter is read, a list by its commas",
            (rigger.Name, rigger.Effort, string.Join("+", rigger.Keywords.Take(2)), rigger.Keywords.Contains("animat"), rigger.FilePath.EndsWith("rigger-animator.md", StringComparison.Ordinal)),
            ("Rigger / Animator", "medium", "rigging+rigged", true, true));

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
                "3d-artist|audio|build-engineer|gameplay-developer|level-artist|qa-playtest|rigger-animator|technical-designer|ui-ux-designer");
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

    /// <summary>
    /// The specialists Link gives a task from the brief, by word starts, in English or Russian: the lead is the one whose
    /// words come first, at most three; none is General.
    /// </summary>
    private static void RolePick()
    {
        var roles = ProjectRoles.In(null);
        string? Id(string brief) => ProjectRoles.Pick(brief, roles).FirstOrDefault()?.Id;
        Program.Equal("suggest: a Russian brief about an animation is the rigger",
            (Id("Переделай анимацию прыжка Нори"), Id("Fix the foot sliding in the run cycle")), ("rigger-animator", "rigger-animator"));
        Program.Equal("suggest: a build problem is the build engineer",
            (Id("Почему падает билд при упаковке"), Id("Cook errors when packaging for Windows")), ("build-engineer", "build-engineer"));
        Program.Equal("suggest: each of the others by its own words",
            (Id("Make a low-poly mesh in Blender and unwrap it"), Id("Блокаут локации и освещение"), Id("Нужен плейтест и баг-репорт"), Id("Write a spec with acceptance criteria"),
                Id("Draw the HUD in Figma"), Id("Add an Enhanced Input action in a Blueprint")),
            ("3d-artist", "level-artist", "qa-playtest", "technical-designer", "ui-ux-designer", "gameplay-developer"));
        Program.Equal("suggest: case does not matter", Id("PACKAGING FAILS"), "build-engineer");
        Program.Equal("suggest: a keyword counts only at the start of a word, and no keyword is General",
            (Id("The inanimate object"), Id("Rename the variable"), Id(string.Empty), ProjectRoles.Pick("Fix the animation", []).Count), ((string?)null, (string?)null, (string?)null, 0));
        Program.Equal("pick: at most three, in the order the work flows, the lead first",
            string.Join(", ", ProjectRoles.Pick("Make a low-poly mesh in Blender, retarget the animation, add an Enhanced Input action in a Blueprint, then a playtest and a bug report", roles)
                .Select(role => role.Id)),
            "3d-artist, rigger-animator, gameplay-developer");
    }

    /// <summary>What a new session is told of its specialists and skills, and where.</summary>
    private static void RoleSession()
    {
        var rigger = ProjectRoles.Find("rigger-animator", null)!;
        var artist = ProjectRoles.Find("3d-artist", null)!;
        var workspace = TempFolder();
        try
        {
            WriteSkill(workspace, "blender-anim", "Animate in Blender.");
            WriteSkill(workspace, "other", "Something else.");
            var text = ProjectRoles.SessionText([rigger], ["blender-anim", "unreal-animbp"], workspace);
            Program.Equal("role text: the name and the body, then the skills the workspace has, as relative files",
                (text.StartsWith("## Your role: Rigger / Animator\n## Duties", StringComparison.Ordinal), text.EndsWith("\n\nRead first: .agents/skills/blender-anim/SKILL.md", StringComparison.Ordinal),
                    text.Contains("unreal-animbp"), text.Contains("<!--")),
                (true, true, false, false));
            Program.Equal("role text: a role without skills has no line, no role and no skills nothing, skills alone only the line",
                (ProjectRoles.SessionText([rigger], [], workspace).Contains("Read first"), ProjectRoles.SessionText([], [], workspace), ProjectRoles.SessionText([], ["blender-anim", "other"], workspace)),
                (false, string.Empty, "Read first: .agents/skills/blender-anim/SKILL.md, .agents/skills/other/SKILL.md"));
            var both = ProjectRoles.SessionText([artist, rigger], ["blender-anim"], workspace);
            Program.Equal("role text: several roles are named with the lead, each body under its own name, then the skills",
                (both.StartsWith("## Your roles\nYou work on this task as 3D Artist, Rigger / Animator: each part of the work by the role it belongs to; where they differ, 3D Artist leads.\n\n### 3D Artist\n", StringComparison.Ordinal),
                    both.Contains("\n\n### Rigger / Animator\n## Duties"), both.Contains("## Your role:"), both.EndsWith("\n\nRead first: .agents/skills/blender-anim/SKILL.md", StringComparison.Ordinal)),
                (true, true, false, true));

            var withRole = AgentRules.Before("Fix it", workspace, listOwnSkills: true, [rigger], ["blender-anim"]);
            var order = new[] { "# Agent rules", "## Skills of this workspace", "## Your role: Rigger / Animator", "Read first: .agents/skills/blender-anim/SKILL.md", "\n\n# Task\n\nFix it" }
                .Select(part => withRole.IndexOf(part, StringComparison.Ordinal)).ToArray();
            Program.Equal("role text: in a new session after the rules and the skills list, before the task",
                (order.All(at => at >= 0), order.SequenceEqual(order.Order()), withRole.EndsWith("\n\n# Task\n\nFix it", StringComparison.Ordinal)), (true, true, true));
            var plain = AgentRules.Before("Fix it", workspace, listOwnSkills: false);
            Program.Equal("role text: General with no skills adds nothing, nor do skills the workspace lacks",
                (plain.Contains("## Your role"), plain == AgentRules.Before("Fix it", workspace, listOwnSkills: false, null, ["gone"])), (false, true));

            var claude = new ClaudeCodeProvider(() => new AgentSettings(), _ => { });
            var task = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = workspace, Prompt = "Fix it", RoleIds = ["rigger-animator"], Skills = ["blender-anim"] };
            Program.Equal("role text: the provider gives a new session its roles and skills, and a resumed one has them already",
                (claude.StandardInput(task).Contains("## Your role: Rigger / Animator"), claude.StandardInput(task).Contains("Read first: .agents/skills/blender-anim/SKILL.md"),
                    claude.StandardInput(task with { ResumeSessionId = "s1" }), claude.StandardInput(task with { RoleIds = ["gone"], Skills = [] }).Contains("Your role"),
                    claude.StandardInput(task with { RoleIds = ["3d-artist", "rigger-animator"] }).Contains("### Rigger / Animator")),
                (true, true, "Fix it", false, true));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>What a tool call opens of a task's kit, and the kit as the user sees it, lit by what was opened.</summary>
    private static void RoleKit()
    {
        string Opened(string call) => string.Join(",", ProjectRoles.Opened(call));
        Program.Equal("kit opened: a skill's or a role's file, whatever the slashes and the case; a pattern or another file is none",
            (Opened(@"Read C:\Work\.agents\skills\Blender-Anim\SKILL.md"), Opened("Read /c/work/.agents/skills/blender-anim/SKILL.md"),
                Opened(@"Read {""file_path"":""C:\\Link\\harness\\roles\\rigger-animator.md""}"), Opened("Glob .agents/skills/*/SKILL.md"), Opened("Read src/roles.cs")),
            ("skills/blender-anim", "skills/blender-anim", "roles/rigger-animator", string.Empty, string.Empty));

        ProjectSkill Skill(string name, string? pack = null) => new(name, name, $@"C:\W\.agents\skills\{name}\SKILL.md", pack);
        ProjectSkill[] skills = [Skill("blender-anim"), Skill("blender-model", "Blender")];
        AgentRole[] roles =
        [
            new("a", "A", "Does a.", [], ["blender-anim"], null, string.Empty, @"C:\W\.agents\roles\a.md"),
            new("b", "B", "Does b.", [], [], null, string.Empty, @"C:\W\.agents\roles\b.md")
        ];
        ToolPack[] packs = [new("blender", "Blender", "Blender's skills.", [], @"C:\Packs\blender")];
        string Kit(IReadOnlyList<string> ids, params string[] opened) =>
            string.Join(", ", ProjectRoles.Kit(ids, opened, roles, skills, packs).Select(part => part.Name + (part.Lit ? "*" : string.Empty)));
        Program.Equal("kit: the roles, the lead first, then the packs, pale until opened; no role or a gone one is General, lit",
            (Kit(["b", "a"]), Kit([]), Kit(["gone"])), ("B, A, Blender", "General*, Blender", "General*, Blender"));
        Program.Equal("kit: a role lights by its file or one of its own skills, a pack by one of its skills",
            (Kit(["a", "b"], "roles/b"), Kit(["a", "b"], "skills/blender-anim"), Kit(["a"], "skills/blender-model")), ("A, B*, Blender", "A*, B, Blender", "A, Blender*"));
        Program.Equal("kit: a pack's part names its skills and opens its folder",
            ProjectRoles.Kit([], [], roles, skills, packs)[1] is { Skills: ["blender-model"], FilePath: @"C:\Packs\blender" }, true);
    }

    /// <summary>
    /// The skills picked for a task: the role's own first, then those that two words or more tie to the brief or the role,
    /// at most four; a team's step and the critic get the role's own alone, with the game's add-on of their tool.
    /// </summary>
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
        string Picked(string brief, AgentRole? role, params ProjectSkill[] among) =>
            string.Join(",", ProjectRoles.SkillsFor(brief, role is null ? [] : [role], among.Length > 0 ? among : skills));

        Program.Equal("skills: two words of the role and the brief tie a skill to the task, the more the earlier",
            Picked("Animate the jump height in Blender", rigger), "blender-anim,unreal-animbp,jump-tuning");
        Program.Equal("skills: one word in common is chance; without a role only the brief's words, none when none match",
            (Picked("Fix the lighting", null), Picked("Fix the lighting of the level", null), Picked("Rename the field", null)), (string.Empty, "level-lighting", string.Empty));
        Program.Equal("skills: the role's skills the workspace lacks are skipped",
            Picked("Fix the lighting of the level", Role("r", "blender-anim"), skills[2], skills[5]), "level-lighting");
        Program.Equal("skills: at most four, in the workspace's order on a tie",
            Picked("animate export lighting level jump height save games mix sound", null), "blender-anim,level-lighting,jump-tuning,save-system");
        Program.Equal("skills: the more words of a skill the brief has, the earlier",
            Picked("Mix the sound of the jump height and tune the sound", null), "jump-tuning,sound-mix");
        Program.Equal("skills: a word counts at its start, in any case, and only a name's whole parts or a description's long words",
            (Picked("JUMPING HEIGHTS", null), Picked("a jumper's height", null), Picked("unjump unheight", null), Picked("an anim for Blender", null)),
            ("jump-tuning", "jump-tuning", string.Empty, "blender-anim"));
        ProjectSkill[] pack = [new("blender-model", "Model props in Blender.", string.Empty, "blender"), new("blender-anim", "Animate rigs in Blender.", string.Empty, "blender")];
        Program.Equal("skills: a pack's name ties none of its skills to a task, its other words do",
            (Picked("Model the props in Blender", null, pack), Picked("Model it in Blender", null, pack), Picked("Model it in Blender", null, [new("blender-model", "Model props in Blender.", string.Empty)])),
            ("blender-model", string.Empty, "blender-model"));

        Program.Equal("skills: words any text has tie no skill to a task",
            Picked("Fix the menu in the project, using the docs", null, new ProjectSkill("ue-blueprint", "Edit Blueprint graphs of the project using ue.mjs.", string.Empty)),
            string.Empty);

        ProjectSkill[] measuring = [new("pie-check", "Measure foot sliding in PIE.", string.Empty), new("other", "Something else.", string.Empty)];
        Program.Equal("skills: a skill whose description has the role's keywords goes with the role",
            (Picked("Anything", Role("r", string.Empty, "foot slid", "measur"), measuring), Picked("Anything", Role("r", string.Empty, "foot slid"), measuring),
                Picked("Anything", Role("r", string.Empty, "lighting"), measuring)),
            ("pie-check", string.Empty, string.Empty));
        Program.Equal("skills: a role skill is named as the workspace has it, once", Picked("Anything", Role("r", "BLENDER-ANIM"), skills[0], skills[0]), "blender-anim");
        AgentRole lead = new("a", "a", string.Empty, [], ["blender-anim", "unreal-animbp", "sound-mix"], null, string.Empty, string.Empty);
        AgentRole second = new("b", "b", string.Empty, [], ["level-lighting", "jump-tuning"], null, string.Empty, string.Empty);
        Program.Equal("skills: several roles take turns, the lead first, so that it does not take all four",
            string.Join(",", ProjectRoles.SkillsFor("Anything", [lead, second], skills)), "blender-anim,level-lighting,unreal-animbp,jump-tuning");

        ProjectSkill[] game =
        [
            new("art-style", "The game's art profile.", string.Empty, "blender"), new("blender-model", "Model props in Blender.", string.Empty, "blender"),
            new("blender-project", "This game's Blender lessons, on top of the pack's skills.", string.Empty), new("unreal-project", "This game's Unreal lessons.", string.Empty),
            new("jump-tuning", "Tune the jump height of the character.", string.Empty)
        ];
        AgentRole artist = new("3d-artist", "3D Artist", string.Empty, ["model"], ["blender-model", "art-style", "blender-live"], null, string.Empty, string.Empty);
        Program.Equal("skills: a role reads its own and the game's add-on of its tool, whatever the words; General none",
            (string.Join(",", ProjectRoles.OwnSkills(artist, game)), string.Join(",", ProjectRoles.OwnSkills(Role("ui-ux-designer", "art-style"), game)),
                ProjectRoles.OwnSkills(null, game).Count),
            ("blender-model,art-style,blender-project", "art-style", 0));

        AgentRole Role(string id, string skill, params string[] keywords) => new(id, id, string.Empty, keywords, string.IsNullOrEmpty(skill) ? [] : [skill], null, string.Empty, string.Empty);
    }

    /// <summary>
    /// The specialists and skills in what is saved: older tasks load as General with no skills, an older Link's one
    /// specialist as the list of one.
    /// </summary>
    private static void RolesSaved()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var older = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Fix it","Workspace":"C:\\Work","Runs":[{"Provider":0,"Outcome":"Done in 1:00"}]}""", options)!;
        Program.Equal("roles saved: a task of an older Link is General with no skills",
            (older.RoleIds.Count, older.Skills.Count, older.Runs[0].RoleIds.Count, older.Runs[0].Opened.Count), (0, 0, 0, 0));
        var single = JsonSerializer.Deserialize<AgentTaskRecord>(
            """{"Goal":"Fix it","Workspace":"C:\\Work","RoleId":"rigger-animator","Runs":[{"Provider":0,"RoleId":"qa-playtest"}]}""", options)!;
        Program.Equal("roles saved: an older Link's one specialist loads as a list of one and is not written back",
            (string.Join("+", single.RoleIds), string.Join("+", single.Runs[0].RoleIds), JsonSerializer.Serialize(single, options).Contains("\"RoleId\"")),
            ("rigger-animator", "qa-playtest", false));
        var saved = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(new AgentTaskRecord
        {
            Goal = "Fix it",
            Workspace = @"C:\Work",
            RoleIds = ["3d-artist", "rigger-animator"],
            Skills = ["blender-anim", "unreal-animbp"],
            Runs = [new AgentRunRecord { RoleIds = ["qa-playtest"], Opened = ["skills/blender-anim"], Outcome = "Done in 1:00" }]
        }, options), options)!;
        Program.Equal("roles saved: the task's specialists and skills, the run's specialists and what it opened are kept",
            (string.Join("+", saved.RoleIds), string.Join("+", saved.Skills), string.Join("+", saved.Runs[0].RoleIds), string.Join("+", saved.Runs[0].Opened)),
            ("3d-artist+rigger-animator", "blender-anim+unreal-animbp", "qa-playtest", "skills/blender-anim"));
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
                RoleIds = ["rigger-animator"],
                Skills = ["blender-anim"]
            };
            var brief = TaskState.CompactBrief(record, null, workspace);
            Program.Equal("compact: the state and then the words to go on with",
                (brief.StartsWith("# Task state\nGoal: Make the landing animation\n", StringComparison.Ordinal), brief.EndsWith($"\n\n{TaskState.GoOn}", StringComparison.Ordinal),
                    TaskState.GoOn), (true, true, "Go on with the task from this state; look at the files touched before changing them; do not redo what is done."));

            var last = new AgentTask
            {
                Provider = AgentProviderKind.Codex, Workspace = workspace, Prompt = "Continue", AutoApprove = true, ResumeSessionId = "s1",
                Model = "gpt-6-sol", Effort = "high", CheckCommand = "dotnet test", PlanOnly = true, AnswerFile = "docs/map.md",
                RoleIds = ["qa-playtest"], Skills = ["old"], Images = [@"C:\old.png"]
            };
            var task = AgentCoordinator.CompactTask(last, record, "  # Task state, edited \n", [@"C:\shot.png"]);
            Program.Equal("compact: a new session of the same agent with its model, effort, Auto and check, the edited state as the prompt",
                (task.Provider, task.ResumeSessionId, task.Prompt, task.Model, task.Effort, task.AutoApprove, task.CheckCommand, task.PlanOnly, task.AnswerFile,
                    string.Join(",", task.Images)),
                (AgentProviderKind.Codex, (string?)null, "# Task state, edited", "gpt-6-sol", "high", true, "dotnet test", false, (string?)null, @"C:\shot.png"));
            Program.Equal("compact: the task's specialist and skills, not the last run's",
                (string.Join("+", task.RoleIds), string.Join(",", task.Skills)), ("rigger-animator", "blender-anim"));

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

    /// <summary>Settings of a Link that had the custom model (its number was 2) load without it.</summary>
    private static void RemovedCustomModel()
    {
        Program.Equal("removed custom model: the numbers of the agents stay",
            ((int)AgentProviderKind.ClaudeCode, (int)AgentProviderKind.Codex, Enum.IsDefined((AgentProviderKind)2)), (0, 1, false));
        var older = JsonSerializer.Deserialize<AgentSettings>(
            """{"DefaultProvider":2,"OwnAnswersOnCustom":true,"Custom":{"Preset":0,"Model":"glm-4.7-flash"},"ClaudeModel":"opus"}""")!;
        older.Normalize();
        Program.Equal("removed custom model: settings that made it the default fall back to Claude Code, the rest is kept",
            (older.DefaultProvider, older.ClaudeModel, JsonSerializer.Serialize(older).Contains("glm", StringComparison.Ordinal)),
            (AgentProviderKind.ClaudeCode, "opus", false));
    }

    /// <summary>Link's own answers (a map, a note) run Light on the agent asked, or Standard when Settings says so.</summary>
    private static void OwnAnswersLevel()
    {
        var light = new AgentSettings();
        var standard = new AgentSettings { OwnAnswersOnLight = false };
        Program.Equal("own answers: Light by default, on the agent asked",
            (light.OwnAnswers(AgentProviderKind.ClaudeCode), light.OwnAnswers(AgentProviderKind.Codex)),
            (light.Level(AgentProviderKind.ClaudeCode, AgentLevel.Light), light.Level(AgentProviderKind.Codex, AgentLevel.Light)));
        Program.Equal("own answers: Standard when Settings turns Light off",
            standard.OwnAnswers(AgentProviderKind.ClaudeCode), standard.Level(AgentProviderKind.ClaudeCode, AgentLevel.Standard));
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
            .RunAsync(Task(), new SyncProgress(events.Add), CancellationToken.None);
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
        // Codex as a tool depends on this machine's install; OneLoop checks it.
        var settings = new AgentSettings { CodexHelper = false };
        var claude = new ClaudeCodeProvider(() => settings, _ => { });
        var codex = new CodexProvider(() => settings, _ => { });
        var analyze = new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Explain" };
        Program.Equal("Claude never runs in dontAsk: no run is read only",
            (string.Join(' ', claude.Arguments(analyze)).Contains("dontAsk"), claude.Arguments(analyze).Contains("acceptEdits")), (false, true));
        Program.Equal("Claude never bypasses permissions but on No-stop",
            new[] { analyze, analyze with { AutoApprove = true }, analyze with { PlanOnly = true } }
                .Any(task => string.Join(' ', claude.Arguments(task)).Contains("bypass", StringComparison.OrdinalIgnoreCase)),
            false);
        var doTask = analyze with { ResumeSessionId = "s1", AllowedTools = ["Bash(dotnet test)"] };
        Program.Equal("Claude accepts edits, resumes and ends with the allowed tools",
            string.Join(' ', claude.Arguments(doTask)).EndsWith("--resume s1 --allowedTools Bash(dotnet test)") &&
            claude.Arguments(doTask).Contains("acceptEdits"),
            true);
        Program.Equal("Claude plan-first keeps its edits, asked for the plan in words",
            (claude.Arguments(doTask with { PlanOnly = true }).Contains("acceptEdits"), claude.Arguments(doTask with { PlanOnly = true }).Contains("plan"),
                claude.StandardInput(doTask with { PlanOnly = true, ResumeSessionId = "s1" }).StartsWith(CliAgentProvider.PlanRequest)), (true, false, true));

        Program.Equal("Codex writes only the workspace",
            string.Join(' ', codex.Arguments(analyze)).Contains("--sandbox workspace-write"), true);
        var featuresOff = string.Join(' ', CodexProvider.FeaturesOff.Select(feature => $"-c features.{feature}=false"));
        Program.Equal("Codex resume sets the sandbox through config and keeps the desktop app's extras off",
            string.Join(' ', codex.Arguments(analyze with { ResumeSessionId = "t1" })),
            $"exec resume t1 --json --skip-git-repo-check -c sandbox_mode=workspace-write {featuresOff} -");
        Program.Equal("Codex: a new session runs without the desktop app's plugins and computer use, with its subagents",
            string.Join(' ', codex.Arguments(analyze)).Contains(featuresOff) && featuresOff.Contains("features.plugins=false") &&
            featuresOff.Contains("features.computer_use=false") && !featuresOff.Contains("multi_agent"), true);
        Program.Equal("Codex plan-first writes the workspace too",
            string.Join(' ', codex.Arguments(analyze with { PlanOnly = true })).Contains("--sandbox workspace-write"), true);

        // Model, effort and auto.
        var chosen = analyze with { Model = "claude-opus-5-5", Effort = "high" };
        Program.Equal("Claude takes the model and effort, and keeps its subagents",
            string.Join(' ', claude.Arguments(chosen)).Contains("--model claude-opus-5-5 --effort high --disallowedTools AskUserQuestion"), true);
        Program.Equal("Claude without a model or effort leaves them to its own settings",
            claude.Arguments(analyze).Any(argument => argument is "--model" or "--effort"), false);
        Program.Equal("Claude runs load the workspace's settings, never the user's own setup",
            string.Join(' ', claude.Arguments(analyze)).Contains("--setting-sources project,local"), true);
        Program.Equal("Claude plan-first denies no file tool",
            string.Join(' ', claude.Arguments(analyze with { PlanOnly = true })).Contains("Write"), false);
        Program.Equal("Claude auto runs in its auto mode, in a plan too",
            (claude.Arguments(analyze with { AutoApprove = true }).Contains("auto"),
                claude.Arguments(analyze with { AutoApprove = true, PlanOnly = true }).Contains("auto")), (true, true));
        var codexChosen = string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex, Model = "gpt-6-sol", Effort = "xhigh", ResumeSessionId = "t1" }));
        Program.Equal("Codex takes the model and effort through config, on resume too",
            codexChosen.Contains("-c model=gpt-6-sol -c model_reasoning_effort=xhigh"), true);
        Program.Equal("Codex auto sends what would leave the sandbox to its automatic review, in a plan too",
            (string.Join(' ', codex.Arguments(analyze with { AutoApprove = true })).Contains("-c approval_policy=on-request -c approvals_reviewer=auto_review"),
                string.Join(' ', codex.Arguments(analyze with { AutoApprove = true, PlanOnly = true })).Contains("approval")), (true, true));

        // No-stop (Night mode): nothing is asked or refused.
        var noStop = analyze with { AutoApprove = true, NoStop = true };
        Program.Equal("Claude No-stop bypasses permissions and refuses only AskUserQuestion",
            (claude.Arguments(noStop).Contains("bypassPermissions"),
                string.Join(' ', claude.Arguments(noStop)).Contains("--disallowedTools AskUserQuestion --strict-mcp-config")), (true, true));
        var codexNoStop = string.Join(' ', codex.Arguments(noStop));
        Program.Equal("Codex No-stop never asks and runs without its sandbox, on resume too",
            (codexNoStop.Contains("-c approval_policy=never"), codexNoStop.Contains("auto_review"), codexNoStop.Contains("--sandbox danger-full-access"),
                string.Join(' ', codex.Arguments(noStop with { ResumeSessionId = "t1" })).Contains("-c sandbox_mode=danger-full-access")), (true, false, true, true));
        Program.Equal("Codex gets the network for a run that reads the web, and not for others",
            (string.Join(' ', codex.Arguments(analyze with { ReadsWeb = true })).Contains("network_access=true"),
                string.Join(' ', codex.Arguments(analyze)).Contains("network_access=true")), (true, false));

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

            // Link's own answers (the map, the note) need no editor tools, whose schemas would be sent again on every call.
            var ownCodex = string.Join(' ', codex.Arguments(analyze with { Provider = AgentProviderKind.Codex, Workspace = game, WithoutMcp = true }));
            Program.Equal("Codex: Link's own answers get no server: the user's all disabled, the workspace's not defined at all",
                ownCodex.Contains("-c mcp_servers.node_repl.enabled=false -c mcp_servers.blender.enabled=false -c mcp_servers.ue-mcp.enabled=false") &&
                !ownCodex.Contains("mcp_servers.unreal-mcp") && !ownCodex.Contains("command='npx'"), true);
            var ownClaude = claude.Arguments(analyze with { Workspace = game, WithoutMcp = true });
            Program.Equal("Claude: Link's own answers get --strict-mcp-config and no server",
                (ownClaude.Contains("--strict-mcp-config"), ownClaude.Contains("--mcp-config")), (true, false));
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
            Program.Equal("Claude runs as the user and needs no git settings, only its task list on",
                string.Join(",", claude.EnvironmentVariables(new AgentTask { Provider = AgentProviderKind.ClaudeCode, Workspace = repository, Prompt = "x" })),
                "[CLAUDE_CODE_ENABLE_TODO_TOOLS, 1]");
        }
        finally
        {
            DeleteFolder(Path.GetDirectoryName(repository)!);
        }
    }

    private static async Task StreamAsync()
    {
        var log = new List<string>();
        var events = new List<AgentEvent>();
        var result = await new FakeProvider("stream", new AgentSettings { ClaudePath = Environment.ProcessPath }, log.Add)
            .RunAsync(Task(), new SyncProgress(events.Add), CancellationToken.None);
        Program.Equal("fake CLI: events arrive in order, junk skipped, the session named as soon as it is known",
            string.Join(",", events.Select(e => e.Kind)), "Status,Session,Tool,Output,Completed");
        Program.Equal("fake CLI: a clean exit succeeds", result.Succeeded, true);
        Program.Equal("fake CLI: the session id is kept", result.SessionId, "fake-session");
        Program.Equal("fake CLI: the run's numbers are in the result", (result.Stats.Turns, result.Stats.ToolCalls, result.Stats.Searches), (1, 1, 1));
        Program.Equal("the [Agent] line has the numbers and not the task",
            log.Count == 1 && log[0].Contains("turns 1, tools 1, helpers 0, searches 1") && !log[0].Contains("fake task"), true);
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
        Program.Equal("hand-over: a new session of the other agent, in the same folder with the same Auto and check",
            (task.Provider, task.ResumeSessionId, task.Workspace, task.AutoApprove, task.CheckCommand, task.Prompt),
            (AgentProviderKind.Codex, (string?)null, @"C:\Work", true, "dotnet test", brief));
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

        // Asked while it works: Claude Code's can_use_tool request and Link's answer, as claude 2.1.278 sends and takes them.
        var asking = new ClaudeCodeProvider(() => new AgentSettings { CodexHelper = false }, _ => { });
        var watched = new AgentTask
        {
            Provider = AgentProviderKind.ClaudeCode, Workspace = @"C:\Work", Prompt = "Fix", AllowedTools = ["Read"],
            AskPermission = (_, _) => System.Threading.Tasks.Task.FromResult(new AgentPermissionAnswer(true))
        };
        var talkArguments = asking.Arguments(watched);
        Program.Equal("ask: a watched run takes stream-json in and asks on stdio, --allowedTools still last",
            (string.Join(' ', talkArguments).Contains("--input-format stream-json --replay-user-messages --permission-prompt-tool stdio"), talkArguments[^2], talkArguments[^1],
                string.Join(' ', asking.Arguments(watched with { AskPermission = null })).Contains("--permission-prompt-tool")),
            (true, "--allowedTools", "Read", false));
        var hearing = string.Join(' ', asking.Arguments(watched with { AskPermission = null, Inbox = new AgentInbox() }));
        Program.Equal("mid-run words: a No-stop run with an inbox takes stream-json in and shows the words back, asking nothing",
            (hearing.Contains("--input-format stream-json --replay-user-messages"), hearing.Contains("--permission-prompt-tool")), (true, false));
        Program.Equal("mid-run words: a message shown back gives its words without the images' note; a tool result gives none",
            (ClaudeCodeProvider.Replayed("""{"type":"user","message":{"role":"user","content":"Make it red\n\nImages attached to this task (open them with Read):\nC:\\a.png"}}"""),
                ClaudeCodeProvider.Replayed("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Stop there"}]}}"""),
                ClaudeCodeProvider.Replayed("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}}""")),
            ((string?)"Make it red", (string?)"Stop there", (string?)null));
        var toolRequest = ClaudeCodeProvider.Request(
            """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"python -c \"print(42)\"","description":"Print 42"},"description":"Print 42","permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"python -c \"print(42)\""}],"behavior":"allow","destination":"localSettings"}],"tool_use_id":"toolu_1"}}""");
        Program.Equal("ask: a tool request names the tool, the command and the rule Claude Code suggests",
            (toolRequest?.Id, toolRequest?.Ask?.Tool, toolRequest?.Ask?.What, string.Join(", ", toolRequest?.Ask?.Rules ?? [])),
            ((string?)"r1", (string?)"Bash", (string?)"python -c \"print(42)\"", "Bash(python -c \"print(42)\")"));
        Program.Equal("ask: without a suggestion the rule is the denials' own; a result ends the talk, another request is no tool's",
            (string.Join(", ", ClaudeCodeProvider.Request("""{"type":"control_request","request_id":"r2","request":{"subtype":"can_use_tool","tool_name":"WebFetch","input":{"url":"https://example.com"}}}""")?.Ask?.Rules ?? []),
                ClaudeCodeProvider.Request("""{"type":"result","subtype":"success"}""")?.Ended,
                ClaudeCodeProvider.Request("""{"type":"control_request","request_id":"r3","request":{"subtype":"hook_callback"}}""") is { Ask: null, Id: "r3" },
                ClaudeCodeProvider.Request("""{"type":"assistant","message":{"content":[{"type":"text","text":"\"type\":\"result\""}]}}""")),
            ("WebFetch", (bool?)true, true, (ClaudeCodeProvider.TalkRequest?)null));
        Program.Equal("ask: Allow sends the input back as it was; Always adds the rules for the session only",
            ClaudeCodeProvider.Answer("r1", new AgentPermissionAnswer(true, Always: true), toolRequest!.ToolInput, toolRequest.Rules),
            """{"type":"control_response","response":{"subtype":"success","request_id":"r1","response":{"behavior":"allow","updatedInput":{"command":"python -c \"print(42)\"","description":"Print 42"},"updatedPermissions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"python -c \"print(42)\""}],"behavior":"allow","destination":"session"}]}}}""");
        Program.Equal("ask: Deny tells the agent to go on without it",
            ClaudeCodeProvider.Answer("r1", new AgentPermissionAnswer(false), toolRequest.ToolInput, toolRequest.Rules),
            """{"type":"control_response","response":{"subtype":"success","request_id":"r1","response":{"behavior":"deny","message":"The user did not allow this. Do the task another way, or stop and say what you need."}}}""");

        var store = new AgentLimits(Path.Combine(Path.GetTempPath(), "link-limits-" + Guid.NewGuid().ToString("N") + ".json"));
        store.Update(windows);
        Program.Equal("limits: a full window means the agent is near its limit and waits for the reset",
            (store.IsNear(AgentProviderKind.Codex), store.ReachedUntil(AgentProviderKind.Codex) is not null, store.IsNear(AgentProviderKind.ClaudeCode)),
            (true, true, false));
        Program.Equal("an earlier Link's session becomes a task of one run",
            AgentTaskRecord.FromSession(new AgentSessionRecord { SessionId = "s9", Workspace = @"C:\Work", Goal = "Fix it", Summary = "Done." }) is
                { Goal: "Fix it", LastWords: "Done.", Runs: [{ SessionId: "s9" }] }, true);
    }

    // The words a run's stdin takes while it works: the turn's end leaves it open until they are taken in.
    private static async Task MidRunWordsAsync()
    {
        var input = new AgentInput();
        var taken = new List<string>();
        input.Taken += taken.Add;
        await input.OpenAsync(new StreamWriter(new MemoryStream()), "task");
        var said = await input.SayAsync("{\"type\":\"user\"}", "Make it red");
        var keptOpen = !await input.CloseAsync(unlessUnread: true);
        input.Took("task");
        var stillUnread = input.HasUnread;
        input.Took("Make it red");
        Program.Equal("mid-run words: said words keep stdin open past a turn's end until taken in; then it closes, and later words are refused",
            (said, keptOpen, stillUnread, await input.CloseAsync(unlessUnread: true), await input.SayAsync("{}", "late")), (true, true, true, true, false));
        input.Took("Make it red");
        Program.Equal("mid-run words: the composer hears once of the user's words taken in, never of the task's own",
            string.Join("|", taken), "Make it red");

        var inbox = new AgentInbox();
        var closed = await inbox.SayAsync("x", []);
        inbox.Open((words, _) => System.Threading.Tasks.Task.FromResult(words == "y"));
        Program.Equal("mid-run words: an inbox takes words only while a run holds it open", (closed, await inbox.SayAsync("y", []), inbox.IsOpen), (false, true, true));
    }

    private static async Task FailureAsync()
    {
        var (result, events) = await RunFakeAsync("fail");
        Program.Equal("fake CLI: a non-zero exit fails", (result.Succeeded, result.ExitCode), (false, 3));
        Program.Equal("fake CLI: a broken-off line is skipped", events.Count(e => e.Kind != AgentEventKind.Session), 1);
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
    private sealed class FakeProvider(string scenario, AgentSettings settings, Action<string> log)
        : CliAgentProvider(() => settings, log)
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
        Program.Equal("tasks: Claude Code's runs start from the plan of the session",
            PlanLine(new ClaudeCodeProvider(() => new AgentSettings(), _ => { }).CreateParser(task).Read(complete).Single().Plan), "✓1, ✓4");
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
        const string Workspace = @"C:\Users\me\projects\Game\";
        const string Temp = @"C:\Users\me\AppData\Local\Temp\";
        string Tidy(string row) => StepGroups.Tidy(row, Workspace, Temp);
        Program.Equal("steps: a cd into the workspace goes, its files show relative, a cd elsewhere stays, temp files show as %TEMP%",
            (Tidy("$ Bash: cd /c/Users/me/projects/Game; cat docs/map.md"), Tidy("$ cd \"C:\\Users\\me\\projects\\Game\" && dotnet build"),
                Tidy(@"≡ Read: C:\Users\me\projects\Game\Art\kid.jpg"), Tidy("$ Bash: cd /c/Users/me/projects/Game/Art/refs; ls"),
                Tidy("$ Bash: cd /c/Users/me/LinkPacks; cat a.md"), Tidy(@"≡ Read: C:\Users\me\AppData\Local\Temp\masks.png"),
                Tidy("✎ Write: C:/Users/me/projects/Game/tools/kit.py"), Tidy("Said C:\\Users\\me\\projects\\Game\\x.cs")),
            ("$ Bash: cat docs/map.md", "$ dotnet build", @"≡ Read: Art\kid.jpg", "$ Bash: cd Art/refs; ls",
                "$ Bash: cd /c/Users/me/LinkPacks; cat a.md", @"≡ Read: %TEMP%\masks.png", "✎ Write: tools/kit.py", "Said x.cs"));
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
            new() { RoleIds = role is null ? [] : [role], Provider = provider, Model = model, Outcome = outcome, Duration = TimeSpan.FromSeconds(seconds), InputTokens = input, OutputTokens = output };
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
            TaskOf(AgentTaskStatus.Done, 0, RunOf(null, Codex, "gpt-5", "an earlier session", 0)),
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
                "General|Codex|gpt-5|1|1|1|0|0:00||0",
                "Rigger|Codex||1|1|1|0|1:00|30000|0"));
        Program.Equal("stats: no tasks, no rows", AgentStatsReport.For([], kind => kind.DefaultName(), Role).Count, 0);
    }

    /// <summary>
    /// The look back on two weeks: the cost, the dearest tasks, loops, a failure twice over (the agent's words or a start
    /// that did not happen), and the skills no recorded step opened; older runs are left out.
    /// </summary>
    private static void ReflectionLines()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        AgentRunRecord RunOf(int daysAgo, decimal? cost, string outcome = "Done in 1:00", string words = "", bool looped = false, params string[] steps) =>
            new() { StartedAt = now.AddDays(-daysAgo), CostUsd = cost, Outcome = outcome, Words = words, Looped = looped, Steps = [.. steps], InputTokens = 1_000 };
        static AgentTaskRecord TaskOf(string goal, params AgentRunRecord[] runs) => new() { Goal = goal, Workspace = "w", Runs = [.. runs] };

        var noStart = TaskOf("Never started");
        noStart.Entries.Add(new AgentTaskEntry(now.AddDays(-1), AgentEntryKind.NoStart, "Codex could not start: Codex is not found"));
        var tasks = new[]
        {
            TaskOf("Model the wheel", RunOf(1, 1.50m, steps: @"⌕ Bash: cat C:\packs\blender\skills\blender-review\SKILL.md"), RunOf(2, 0.50m, looped: true)),
            TaskOf("Fix the login", RunOf(3, 0.20m, "Failed after 0:02", "Not logged in · Please run /login\nmore"), RunOf(3, 0.10m, "Failed after 0:01", "Not logged in · Please run /login")),
            TaskOf("Old work", RunOf(30, 9m)),
            TaskOf("Rename a file", RunOf(5, null)),
            noStart,
            TaskOf("Codex again", RunOf(4, null, "Failed after 0:00", "Codex could not start: Codex is not found\nat PATH"))
        };
        Program.Equal("reflect: totals, the dearest three, loops, the same failure twice, unopened skills",
            string.Join("\n", Reflection.Lines(tasks, ["blender-review", "blender-model"], now)),
            string.Join("\n",
                "5 tasks, 6 runs in 14 days · ≈ $2.30 at API prices",
                "Dearest:",
                "  $2.00 · 2 runs · Model the wheel",
                "  $0.30 · 2 runs · Fix the login",
                "  1k tokens · 1 run · Rename a file",
                "Looped in 1 task: Model the wheel",
                "Failed the same way:",
                "  × 2 Not logged in · Please run /login",
                "  × 2 Codex could not start: Codex is not found",
                "No recorded step opened: blender-model (a run keeps only its last steps)"));
        Program.Equal("reflect: nothing in two weeks, no lines", Reflection.Lines([TaskOf("Old work", RunOf(30, 9m))], [], now).Count, 0);
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
                "Setup|Context|Rules|Role|Project memory|Task state|Skills|Helpers|Injected context|Tools and MCP|Verification|Stats|Reflect");
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
                (Section(bare, "Task state").Value, Section(bare, "Task state").Detail, Section(bare, "Injected context").Value,
                 Section(bare, "Role").Value),
                ("No task yet", (string?)null, "Nothing yet", "General — no role"));

            // Project memory and skills, MCP, check.
            Program.Equal("harness memory: a map that is not there is missing, and not opened",
                (Section(bare, "Project memory").Value, Section(bare, "Project memory").FilePath), ("docs/map.md · missing", (string?)null));
            Directory.CreateDirectory(Path.Combine(workspace, "docs"));
            File.WriteAllText(ProjectMap.PathIn(workspace), "# Map");
            Program.Equal("harness memory: a map that is there opens",
                (Section(HarnessView.Build(Input()), "Project memory").Value, Section(HarnessView.Build(Input()), "Project memory").FilePath),
                ("docs/map.md · 1 line", ProjectMap.PathIn(workspace)));
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
                RoleIds = ["rigger-animator"],
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
                Section(HarnessView.Build(Input(new AgentTaskRecord { Goal = "g", Workspace = workspace, RoleIds = ["gone"] })), "Role").Value, "gone (its file is gone)");
            Program.Equal("harness context: the compactions in the task's history are counted",
                Section(open, "Context").Note!.EndsWith("\nCompacted 2 times", StringComparison.Ordinal), true);
            Program.Equal("harness task state: the state Compact starts from",
                Section(open, "Task state").Detail!.StartsWith("# Task state\nGoal: Fix the jump\n", StringComparison.Ordinal), true);
            Program.Equal("harness injected context: the brief Link gave, to read",
                (Section(open, "Injected context").Value, Section(open, "Injected context").Detail),
                ("The brief Link gave: 13 characters", "Fix the jump."));

            Program.Equal("harness tools: no .mcp.json, no servers, nothing to open; the access and the project's rules",
                (Section(open, "Tools and MCP").Value, Section(open, "Tools and MCP").FilePath, Section(open, "Tools and MCP").Note, Section(open, "Tools and MCP").Detail),
                ("No MCP servers", (string?)null, "Access: edits: other commands ask you\nTool rules the project allows: 2", "Read\nBash(git log:*)"));
            File.WriteAllText(Path.Combine(workspace, ".mcp.json"), "{\"mcpServers\": {\"unreal\": {\"command\": \"node\"}, \"blender\": {\"url\": \"http://localhost:1\"}}}");
            var tools = Section(HarnessView.Build(Input(task) with { AutoApprove = true }), "Tools and MCP");
            Program.Equal("harness tools: the MCP servers by the workspace's .mcp.json, auto access",
                (tools.Value, tools.FilePath, tools.Note!.Split('\n')[0]),
                ("MCP: unreal, blender", Path.Combine(workspace, ".mcp.json"), "Access: auto: edits and commands without asking"));
            Program.Equal("harness tools: a plan",
                Section(HarnessView.Build(Input() with { PlanOnly = true }), "Tools and MCP").Note!.Split('\n')[0], "Access: plan first, then the plan runs");
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
                "Rigger · Claude Code · opus — 3 tasks, 1 done, 3 failed, 2:30 a run, 127k tokens a run, 3 steers\nGeneral · Codex · default model — 1 task, 1 done"));
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

    /// <summary>A new task's card left unsent comes back after a restart, its words and pictures as they were.</summary>
    private static void CardDraftKept()
    {
        var file = Path.Combine(TempFolder(), "card-draft.json");
        AtomicJsonFile.Write(file, new CardDraft("Fix the seats\nand the legs", [@"C:\pictures\a.png", @"C:\pictures\b.png"]), new JsonSerializerOptions());
        var back = AtomicJsonFile.Read<CardDraft>(file);
        Program.Equal("card draft: the words and pictures of an unsent card are read back after a restart",
            (back?.Goal, back?.Pictures.Length, back?.Pictures[1]), ("Fix the seats\nand the legs", (int?)2, @"C:\pictures\b.png"));
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }

    /// <summary>
    /// The Link window's place and inspector are saved with the settings; an older settings.json, with the removed overlay
    /// card's glass and place in it, has the defaults.
    /// </summary>
    private static void StudioSettingsKept()
    {
        static (double?, double?, double, double, bool, bool) Of(StudioSettings studio) =>
            (studio.Left, studio.Top, studio.Width, studio.Height, studio.Maximized, studio.InspectorOpen);

        var older = JsonSerializer.Deserialize<AppSettings>(
            """{"Opacity":0.7,"AlwaysOnTop":false,"Window":{"Width":400},"Studio":{"ProjectId":"4b0b5b0e-6c3e-4b7a-9d3a-0f0e8a1c2d3e"}}""")!;
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

    /// <summary>The first-run window says whether each CLI is found and signed in, and where to install the missing one.</summary>
    private static void FirstRunWords()
    {
        const AgentProviderKind Claude = AgentProviderKind.ClaudeCode;
        Program.Equal("first run: found and signed in",
            FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", "2.1", true)), "Found, signed in");
        Program.Equal("first run: found, not signed in says where the agent signs in",
            (FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", null, false)),
                FirstRun.Status(AgentProviderKind.Codex, new AgentProviderStatus("codex.exe", null, false))),
            ("Found, not signed in: Settings → Sign in", "Found, not signed in: run 'codex login' once"));
        Program.Equal("first run: found, sign-in unknown",
            FirstRun.Status(Claude, new AgentProviderStatus("claude.exe", null, null)), "Found, sign-in unknown");
        Program.Equal("first run: not found", FirstRun.Status(Claude, new AgentProviderStatus(null, null, null)), "Not found");
        Program.Equal("first run: each agent has its own https install page",
            (FirstRun.InstallAddress(Claude).StartsWith("https://", StringComparison.Ordinal),
                FirstRun.InstallAddress(AgentProviderKind.Codex).StartsWith("https://", StringComparison.Ordinal),
                FirstRun.InstallAddress(Claude) != FirstRun.InstallAddress(AgentProviderKind.Codex)),
            (true, true, true));
        Program.Equal("claude sign-in: the page to open again is the address in Claude Code's line",
            (ClaudeSignIn.PageIn("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&state=x"),
                ClaudeSignIn.PageIn("Paste code here if prompted > ")),
            ("https://claude.com/cai/oauth/authorize?code=true&state=x", (string?)null));
        Program.Equal("system proxy: Windows' setting as HTTPS_PROXY takes it, the https part of a per-protocol one, none when empty",
            (SystemProxy.Parse("127.0.0.1:12334"), SystemProxy.Parse("http://127.0.0.1:12334"),
                SystemProxy.Parse("http=10.0.0.1:80;https=10.0.0.2:443;socks=10.0.0.3:1080"), SystemProxy.Parse("http=10.0.0.1:80"),
                SystemProxy.Parse("socks=10.0.0.3:1080"), SystemProxy.Parse("")),
            ("http://127.0.0.1:12334", "http://127.0.0.1:12334", "http://10.0.0.2:443", "http://10.0.0.1:80", (string?)null, (string?)null));
        var ownProxy = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["https_proxy"] = "http://own:1" };
        SystemProxy.AddTo(ownProxy);
        Program.Equal("system proxy: a proxy the environment names already is kept as it is",
            (ownProxy.Count, ownProxy["HTTPS_PROXY"]), (1, (string?)"http://own:1"));
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

    /// <summary>
    /// Link's own Blender pack (a skill to model, one to rig, a QA role) and a packs folder where the agents changed the
    /// model skill and the QA role, added a bake skill with a script and an artist role, and left the rig as it shipped.
    /// </summary>
    private static (string BuiltIn, string Packs) LearnedPacks()
    {
        var builtIn = TempFolder();
        var packs = TempFolder();
        foreach (var root in new[] { builtIn, packs })
        {
            PackFile(root, "blender/pack.md", "---\nname: Blender\ndescription: Blender\ndetect: *.blend\n---\n");
            PackFile(root, "blender/skills/blender-model/SKILL.md", "---\nname: blender-model\ndescription: Model a prop.\n---\nBevel last.\n");
            PackFile(root, "blender/skills/blender-rig/SKILL.md", "---\nname: blender-rig\ndescription: Rig it.\n---\n");
            PackFile(root, "blender/roles/qa.md", "---\nname: QA\n---\nCheck the model.\n");
        }

        PackFile(packs, "blender/skills/blender-model/SKILL.md", "---\nname: blender-model\ndescription: Model a prop.\n---\nBevel first: a lesson.\n");
        PackFile(packs, "blender/roles/qa.md", "---\nname: QA\n---\nCheck the model, then its bake.\n");
        PackFile(packs, "blender/skills/blender-rig/__pycache__/rig.cpython-312.pyc", "cache");
        PackFile(packs, "blender/skills/blender-bake/SKILL.md", "---\nname: blender-bake\ndescription: Bake the textures.\n---\nSave to C:\\Users\\vlad\\Desktop\\bake.\n");
        PackFile(packs, "blender/skills/blender-bake/scripts/run.py", "token = 'abcdef1234567890xyz'\n");
        PackFile(packs, "blender/roles/artist.md", "---\nname: Artist\n---\nKeep the DriveSlop palette.\n");
        return (builtIn, packs);
    }

    private static void PackFile(string root, string name, string text)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>What the agents learned is what differs from Link's own, each item with the lines of it that look personal.</summary>
    private static void KnowledgePackLearned()
    {
        Program.Equal("knowledge pack: why a line looks personal",
            string.Join("|", new[]
                {
                    @"Save to C:\Users\vlad\Desktop", "cd /c/Users/vlad/game", "Ask vlad@example.com", "sk-" + new string('a', 24), "password: hunter2hunter2",
                    "The DriveSlop menu", "DriveSlopper", "Bevel before you bake."
                }
                .Select(line => KnowledgePack.Why(line, ["DriveSlop"]) ?? "-")),
            "a user's folder|a user's folder|an email|a key or token|a key or token|\"DriveSlop\"|-|-");

        var (builtIn, packs) = LearnedPacks();
        try
        {
            var learned = KnowledgePack.Learned(packs, builtIn, ["DriveSlop"]);
            Program.Equal("knowledge pack: added and changed items with their personal lines; Link's own as shipped and Python's cache left out",
                string.Join("|", learned.Select(item => $"{item.Item}={item.State}:{string.Join(",", item.Personal)}")),
                "blender/skills/blender-bake=Added:scripts/run.py:1 a key or token,SKILL.md:5 a user's folder|blender/skills/blender-model=Changed:|" +
                "blender/roles/artist.md=Added:artist.md:4 \"DriveSlop\"|blender/roles/qa.md=Changed:");
            Program.Equal("knowledge pack: a skill is described by its SKILL.md, a role without a description by its name",
                string.Join("|", learned.Select(item => item.Description)), "Bake the textures.|Model a prop.|Artist|QA");
        }
        finally
        {
            DeleteFolder(builtIn);
            DeleteFolder(packs);
        }
    }

    /// <summary>
    /// What the agents learned goes to a file and into a friend's packs folder: new items and Link's untouched files are
    /// taken, a file the friend changed too goes beside theirs, and the friend's agent gets a brief to fit it to a project.
    /// </summary>
    private static void KnowledgePackRoundTrip()
    {
        var (builtIn, packs) = LearnedPacks();
        var friend = TempFolder();
        var folder = TempFolder();
        var file = Path.Combine(folder, "skills-2026" + TeamPack.Extension);
        try
        {
            KnowledgePack.Export(packs, KnowledgePack.Learned(packs, builtIn, []), file);
            Program.Equal("knowledge pack: the items with their pack.md, a README and pack.json, nothing else",
                string.Join("|", PackEntries(file)),
                "README.md|pack.json|packs/blender/pack.md|packs/blender/roles/artist.md|packs/blender/roles/qa.md|packs/blender/skills/blender-bake/SKILL.md|" +
                "packs/blender/skills/blender-bake/scripts/run.py|packs/blender/skills/blender-model/SKILL.md");
            using (var zip = ZipFile.OpenRead(file))
            using (var readme = new StreamReader(zip.GetEntry("README.md")!.Open()))
            {
                var text = readme.ReadToEnd();
                Program.Equal("knowledge pack: its README says where the skills go without Link, and what is inside",
                    (text.Contains("~/.claude/skills/"), text.Contains("<project>/.agents/skills/"), text.Contains("- `blender/skills/blender-bake` — Bake the textures.")),
                    (true, true, true));
            }

            var team = Path.Combine(folder, "team" + TeamPack.Extension);
            MakeZip(team, ("pack.json", "{\"format\":1}"), ("roles/qa.md", "x"));
            var plainText = Path.Combine(folder, "text" + TeamPack.Extension);
            File.WriteAllText(plainText, "plain text");
            Program.Equal("knowledge pack: told apart from a team pack and from a file that is no zip",
                (KnowledgePack.IsOne(file), KnowledgePack.IsOne(team), KnowledgePack.IsOne(plainText)), (true, false, false));

            // The friend's Link seeded its own Blender pack, and the friend changed its QA role since; the bake skill has its
            // SKILL.md but not its script yet, and the pack's pack.md is gone.
            ToolPacks.Seed(builtIn, friend);
            File.Delete(Path.Combine(friend, "blender", "pack.md"));
            PackFile(friend, "blender/roles/qa.md", "---\nname: QA\n---\nCheck it my way.\n");
            PackFile(friend, "blender/skills/blender-bake/SKILL.md", File.ReadAllText(Path.Combine(packs, "blender", "skills", "blender-bake", "SKILL.md")));
            var preview = KnowledgePack.Preview(file, friend);
            Program.Equal("knowledge pack: a missing file or Link's untouched one updates, a missing item is new, one the friend changed is both",
                string.Join("|", preview.Select(item => $"{item.Item}={item.State}")),
                "blender/skills/blender-bake=Updates|blender/skills/blender-model=Updates|blender/roles/artist.md=Added|blender/roles/qa.md=BothChanged");
            Program.Equal("knowledge pack: an opened pack describes its items", string.Join("|", preview.Select(item => item.Description)),
                "Bake the textures.|Model a prop.|Artist|QA");

            var nothing = KnowledgePack.Import(file, friend, []);
            Program.Equal("knowledge pack: nothing ticked, nothing taken",
                (nothing.Taken.Count, nothing.PutBeside.Count, File.Exists(Path.Combine(friend, "blender", "roles", "artist.md"))), (0, 0, false));
            var import = KnowledgePack.Import(file, friend, [.. preview.Select(item => item.Item)]);
            var beside = Path.Combine(friend, KnowledgePack.IncomingFolder, "skills-2026");
            Program.Equal("knowledge pack: taken into the packs, the friend's changed one put beside",
                (string.Join("|", import.Taken), string.Join("|", import.PutBeside), import.Beside),
                ("blender/skills/blender-bake|blender/skills/blender-model|blender/roles/artist.md", "blender/roles/qa.md", beside));
            Program.Equal("knowledge pack: the pack's files land, the friend's QA stays with the pack's beside it, the pack comes with its pack.md",
                (File.ReadAllText(Path.Combine(friend, "blender", "skills", "blender-model", "SKILL.md")).Contains("Bevel first"),
                    File.Exists(Path.Combine(friend, "blender", "skills", "blender-bake", "scripts", "run.py")),
                    File.ReadAllText(Path.Combine(friend, "blender", "roles", "qa.md")).Contains("my way"),
                    File.ReadAllText(Path.Combine(beside, "blender", "roles", "qa.md")).Contains("then its bake"),
                    string.Join("|", ToolPacks.All(friend).Select(pack => pack.Id))),
                (true, true, true, true, "blender"));
            Program.Equal("knowledge pack: opened again, all is the same but the friend's own QA",
                string.Join("|", KnowledgePack.Preview(file, friend).Select(item => $"{item.Item}={item.State}")),
                "blender/skills/blender-bake=Same|blender/skills/blender-model=Same|blender/roles/artist.md=Same|blender/roles/qa.md=BothChanged");
            Program.Equal("knowledge pack: taken again, what is the same is left", KnowledgePack.Import(file, friend, [.. import.Taken]).Taken.Count, 0);

            var brief = KnowledgePack.SetUpBrief("skills-2026", friend, import);
            var plain = KnowledgePack.SetUpBrief("skills-2026", friend, new KnowledgeImport(["blender/skills/blender-bake"], [], beside));
            Program.Equal("knowledge pack: the set-up brief names what was taken and what is beside, and asks a merge only then",
                (brief.Contains("- blender/roles/artist.md\n"), brief.Contains($"in {beside}:\n- blender/roles/qa.md\n"), brief.Contains("\n4. For each skill with two versions"),
                    plain.Contains("- blender/skills/blender-bake\n"), plain.Contains("beside"), plain.Contains("\n4. "), plain.EndsWith("one line per skill.", StringComparison.Ordinal)),
                (true, true, true, true, false, false, true));
        }
        finally
        {
            DeleteFolder(builtIn);
            DeleteFolder(packs);
            DeleteFolder(friend);
            DeleteFolder(folder);
        }
    }

    /// <summary>A file of another kind or version, and every entry that could leave the packs or hide in them, are refused before anything is written.</summary>
    private static void KnowledgePackRefusals()
    {
        var friend = TempFolder();
        var folder = TempFolder();
        try
        {
            string Open(params (string Name, string Text)[] entries)
            {
                var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + TeamPack.Extension);
                MakeZip(file, entries);
                try
                {
                    var items = KnowledgePack.Preview(file, friend);
                    KnowledgePack.Import(file, friend, [.. items.Select(item => item.Item)]);
                    return string.Join("|", items.Select(item => item.Item));
                }
                catch (InvalidDataException)
                {
                    return "refused";
                }
            }

            const string Manifest = "{\"kind\":\"learned\",\"format\":1,\"created\":\"2026-10-30\",\"items\":[]}";
            Program.Equal("knowledge pack: a team pack is refused", Open(("pack.json", "{\"format\":1}"), ("packs/blender/roles/qa.md", "x")), "refused");
            Program.Equal("knowledge pack: a pack of another format is refused", Open(("pack.json", "{\"kind\":\"learned\",\"format\":2}"), ("packs/blender/roles/qa.md", "x")), "refused");
            foreach (var bad in new[]
                     {
                         "packs/../evil.md", "packs/blender/../../evil.md", "packs/blender/skills/x/..\\..\\evil.md", "/packs/evil.md", "C:/evil.md",
                         "packs//blender/roles/qa.md", "packs/.incoming/roles/qa.md", "packs/.git/roles/qa.md"
                     })
            {
                Program.Equal($"knowledge pack: the entry {bad} is refused", Open(("pack.json", Manifest), ("packs/blender/roles/qa.md", "fine"), (bad, "evil")), "refused");
            }

            Program.Equal("knowledge pack: nothing was written by any refused pack",
                (Directory.GetFileSystemEntries(friend).Length, File.Exists(Path.Combine(folder, "evil.md"))), (0, false));
            Program.Equal("knowledge pack: the README, a folder entry and files outside skills and roles are not taken",
                Open(("pack.json", Manifest), ("README.md", "x"), ("packs/blender/", string.Empty), ("packs/blender/notes.txt", "x"), ("packs/blender/skills/stray.md", "x"),
                    ("packs/blender/roles/qa.txt", "x"), ("packs/blender/skills/x/SKILL.md", "---\nname: x\n---\n")),
                "blender/skills/x");
        }
        finally
        {
            DeleteFolder(friend);
            DeleteFolder(folder);
        }
    }

    /// <summary>A plan step's headline in the task's plan card: its first sentence, the rest apart.</summary>
    private static void PlanHeadline()
    {
        Program.Equal("plan: a step's headline is its first sentence, the rest apart; a file name's dot ends nothing",
            (PlanRow.Headline(" Rework the body in Art/SUV.blend. Cut the windows through.\nThen export. "), PlanRow.Headline("Rename the files"),
                PlanRow.Headline("Wire the doors\n(1) Copy the FBX")),
            (("Rework the body in Art/SUV.blend.", "Cut the windows through.\nThen export."), ("Rename the files", ""), ("Wire the doors", "(1) Copy the FBX")));
        var endless = string.Join(' ', Enumerable.Repeat("word", 60));
        Program.Equal("plan: a first sentence too long for a line is cut at a word, and then the rest is the whole step",
            PlanRow.Headline(endless) is var (title, whole) && title.EndsWith("word…", StringComparison.Ordinal) && title.Length <= 160 && whole == endless, true);
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

    /// <summary>A task measured: tokens per agent without the cached part counted twice, the strong agent's apart, and steers.</summary>
    private static void TaskMetricsCounted()
    {
        var at = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var minute = TimeSpan.FromMinutes(1);
        var record = new AgentTaskRecord
        {
            Goal = "Add the dash", Workspace = "C:\\Work", Status = AgentTaskStatus.Done, LastCheck = "dotnet test: passed",
            Runs =
            [
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Done in 1:00", StartedAt = at, Duration = minute, InputTokens = 1000, CachedTokens = 800, OutputTokens = 100 },
                new AgentRunRecord { Provider = AgentProviderKind.Codex, Outcome = "Done in 2:00", StartedAt = at + minute, Duration = 2 * minute, InputTokens = 500, CachedTokens = 0, OutputTokens = 10 },
                // A cached part beyond the input is a slip of the CLI: it is cut to the input.
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, Outcome = "Done in 1:00", StartedAt = at + 3 * minute, Duration = minute, InputTokens = 100, CachedTokens = 300 }
            ]
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
        Program.Equal("metrics: time, runs, the words given while a run went, the end and the check",
            (metric.Time, metric.Runs, metric.Steers, metric.Done, metric.CheckPassed), (4 * minute, 3, 2, true, (bool?)true));
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

    /// <summary>A task exported for a chat in the browser: its numbers, brief and thread, in order and readable as Markdown.</summary>
    private static void TaskExported()
    {
        var at = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var minute = TimeSpan.FromMinutes(1);
        var record = new AgentTaskRecord
        {
            Goal = "Add the dash", Workspace = "C:\\Work\\Game", CreatedAt = at, UpdatedAt = at + 4 * minute, Status = AgentTaskStatus.Done,
            RoleIds = ["programmer"], Brief = "Add the dash.\n```cs\nvar speed = 2;\n```", Handovers = ["Codex takes it over"], LastCheck = "dotnet test: passed\nall green",
            ChangedFiles = ["Src/Dash.cs"], Images = ["C:\\Shots\\ref.png"],
            Runs =
            [
                new AgentRunRecord
                {
                    Provider = AgentProviderKind.ClaudeCode, Model = "claude-opus-5-5", Effort = "xhigh", EffortReason = "a plan", AutoApprove = true,
                    SessionId = "s1", StartedAt = at, Duration = minute, Outcome = "Done in 1:00", InputTokens = 20_000, CachedTokens = 18_000, OutputTokens = 1_000,
                    Steps = ["Read Dash.cs"], Words = "## Plan\nStep one"
                },
                new AgentRunRecord
                {
                    Provider = AgentProviderKind.Codex, Effort = "high", RoleIds = ["programmer"],
                    SessionId = "s2", StartedAt = at + 2 * minute, Duration = 2 * minute, Outcome = "Done in 2:00", InputTokens = 5_000, CachedTokens = 0, OutputTokens = 200,
                    Files = ["Src/Dash.cs"], Words = "Done."
                }
            ]
        };
        record.Entries.Add(new AgentTaskEntry(at, AgentEntryKind.Brief, "Add the dash"));
        record.Entries.Add(new AgentTaskEntry(at + minute, AgentEntryKind.Run, "Claude Code: done"));
        record.Entries.Add(new AgentTaskEntry(at + minute, AgentEntryKind.Said, "The plan is ready"));
        record.Entries.Add(new AgentTaskEntry(at + minute, AgentEntryKind.HandOff, "Step 1 → Codex"));
        record.Entries.Add(new AgentTaskEntry(at + 4 * minute, AgentEntryKind.Run, "Codex: done"));
        record.Entries.Add(new AgentTaskEntry(at + 5 * minute, AgentEntryKind.You, "thanks"));
        var text = TaskExport.Markdown(record, kind => kind.DefaultName(), id => id is null ? "General" : "Programmer", AgentProviderKind.ClaudeCode, at.AddDays(1));
        var lines = text.Split('\n');
        string Line(string start) => lines.FirstOrDefault(line => line.StartsWith(start, StringComparison.Ordinal)) ?? $"(no line {start})";

        Program.Equal("export: the summary tells how the task stands, where, how long and what each agent spent",
            string.Join("\n", Line("# LINK"), Line("- Status"), Line("- Folder"), Line("- Started"), Line("- Runs"), Line("- Claude Code:"), Line("- Codex:"), Line("- In all"),
                Line("- Check"), Line("- Images"), Line("- Files")),
            "# LINK task: Add the dash\n- Status: Done · specialist: Programmer\n- Folder: Game\n" +
            "- Started 2026-10-08 10:00, last change 2026-10-08 10:04; the agents worked 3:00\n" +
            "- Runs: 2 (Claude Code 1, Codex 1) · the user's words: 1, 0 of them while a run went\n" +
            "- Claude Code: 20k in (18k cached), 1k out, ≈ 8.8k\n- Codex: 5k in (0 cached), 200 out, ≈ 6k\n" +
            "- In all ≈ 14.8k, ≈ 8.8k of them on Claude Code, the default agent\n- Check: dotnet test: passed\n- Images: ref.png\n- Files changed (1): Src/Dash.cs");
        Program.Equal("export: a brief with a fence of its own is fenced longer, the hand-over after it",
            (text.Contains("## Brief\n````text\nAdd the dash.\n```cs\nvar speed = 2;\n```\n````\n", StringComparison.Ordinal),
                text.Contains("## Hand-over 1\n```text\nCodex takes it over\n```\n", StringComparison.Ordinal)),
            (true, true));
        Program.Equal("export: each run with its setup and numbers",
            string.Join("\n", Line("### Run 1"), Line("- 2026-10-08 10:00"), Line("- Effort"), Line("### Run 2")),
            "### Run 1 · Claude Code · claude-opus-5-5 · xhigh · as General · auto\n" +
            "- 2026-10-08 10:00 · Done in 1:00 · 20k in (18k cached), 1k out\n- Effort chosen by Link: a plan\n" +
            "### Run 2 · Codex · default model · high · as Programmer · edits");
        Program.Equal("export: the thread in order, answers quoted so that their headings stay theirs, the answer's own lines once",
            (text.IndexOf("**The task**", StringComparison.Ordinal) < text.IndexOf("### Run 1", StringComparison.Ordinal),
                text.Contains("> ## Plan\n> Step one\n", StringComparison.Ordinal), text.Contains("The plan is ready", StringComparison.Ordinal),
                text.IndexOf("HandOff: Step 1 → Codex", StringComparison.Ordinal) < text.IndexOf("### Run 2", StringComparison.Ordinal),
                text.EndsWith("**You** · 2026-10-08 10:05\n> thanks\n", StringComparison.Ordinal)),
            (true, true, false, true, true));
        Program.Equal("export: the file is named by the task's start and goal, without what a file name cannot hold",
            (TaskExport.FileName(record), TaskExport.FileName(new AgentTaskRecord { Goal = "Fix: \"a/b\" ?", Workspace = "w", CreatedAt = at })),
            ("2026-10-08 1000 Add-the-dash.md", "2026-10-08 1000 Fix-a-b.md"));
    }

    /// <summary>A reached window the CLI gave no reset for does not hold for ever: only for its own length after it was seen.</summary>
    private static void LimitsHold()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(Path.GetTempPath(), "link-limits-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string Held(params AgentLimit[] limits)
            {
                File.Delete(file);
                var store = new AgentLimits(file);
                store.Update(limits);
                return string.Join(",", store.For(AgentProviderKind.Codex, now).Select(limit => limit.Window));
            }

            AgentLimit Window(string window, TimeSpan seenAgo, DateTimeOffset? resets = null) =>
                new(AgentProviderKind.Codex, window, 100, resets, true, now - seenAgo);
            Program.Equal("limit holds: a 5 h window without a reset time holds for five hours",
                (Held(Window("5 h", TimeSpan.FromHours(4))), Held(Window("5 h", TimeSpan.FromHours(6)))), ("5 h", string.Empty));
            Program.Equal("limit holds: a week window, whatever it is called after the week, holds for seven days",
                (Held(Window("week", TimeSpan.FromDays(6))), Held(Window("week", TimeSpan.FromDays(8))),
                    Held(Window("week, Opus", TimeSpan.FromDays(6))), Held(Window("week, Opus", TimeSpan.FromDays(8)))),
                ("week", string.Empty, "week, Opus", string.Empty));
            Program.Equal("limit holds: any other window is taken as five hours",
                (Held(Window("month", TimeSpan.FromHours(4))), Held(Window("month", TimeSpan.FromHours(6)))), ("month", string.Empty));
            Program.Equal("limit holds: a window with a reset time holds until it, however long ago it was seen",
                (Held(Window("5 h", TimeSpan.FromHours(10), now.AddHours(1))), Held(Window("5 h", TimeSpan.FromMinutes(1), now.AddMinutes(-1)))),
                ("5 h", string.Empty));
            Program.Equal("limit holds: the windows come 5 h first",
                Held(Window("week", TimeSpan.Zero), Window("5 h", TimeSpan.Zero)), "5 h,week");
        }
        finally
        {
            File.Delete(file);
        }

        // The agent is near its limit, or not, by the same rule.
        var live = new AgentLimits(file);
        live.Update([new AgentLimit(AgentProviderKind.Codex, "5 h", 100, null, true, DateTimeOffset.Now - TimeSpan.FromHours(1)),
            new AgentLimit(AgentProviderKind.ClaudeCode, "5 h", 100, null, true, DateTimeOffset.Now - TimeSpan.FromHours(6))]);
        Program.Equal("limit holds: a reached window without a reset is near for five hours, then the agent is free again; it has no time to wait for",
            (live.IsNear(AgentProviderKind.Codex), live.IsNear(AgentProviderKind.ClaudeCode), live.ReachedUntil(AgentProviderKind.Codex)),
            (true, false, (DateTimeOffset?)null));
        File.Delete(file);
    }

    /// <summary>A new card, the map and a note start on Settings' agent; the other only while that one's limit is reached now.</summary>
    private static void StartAgentRule()
    {
        var file = Path.Combine(Path.GetTempPath(), "link-limits-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new AgentLimits(file);
            var (free, none) = store.StartsOn(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex);
            Program.Equal("start agent: nothing reached, the default agent", (free, none), (AgentProviderKind.ClaudeCode, (DateTimeOffset?)null));

            var reset = DateTimeOffset.Now.AddHours(2);
            store.Update([new AgentLimit(AgentProviderKind.ClaudeCode, "5 h", 100, reset, true, DateTimeOffset.Now)]);
            var (taken, until) = store.StartsOn(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex);
            Program.Equal("start agent: while the default's limit is reached the other starts, and the reset is told",
                (taken, until), (AgentProviderKind.Codex, (DateTimeOffset?)reset));
            Program.Equal("start agent: the limit of the other agent is no reason to leave the default",
                store.StartsOn(AgentProviderKind.Codex, AgentProviderKind.ClaudeCode).Provider, AgentProviderKind.Codex);

            store.Update([new AgentLimit(AgentProviderKind.ClaudeCode, "5 h", 100, DateTimeOffset.Now.AddMinutes(-1), true, DateTimeOffset.Now.AddHours(-6))]);
            Program.Equal("start agent: once the reset has passed the default starts again",
                store.StartsOn(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex).Provider, AgentProviderKind.ClaudeCode);
            store.Update([new AgentLimit(AgentProviderKind.ClaudeCode, "5 h", 92, reset, false, DateTimeOffset.Now)]);
            Program.Equal("start agent: near the limit is not reaching it",
                store.StartsOn(AgentProviderKind.ClaudeCode, AgentProviderKind.Codex).Provider, AgentProviderKind.ClaudeCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>What a run took of each usage window: the end less the start, with every case that must not be counted left out.</summary>
    private static void UsageSpentMeasured()
    {
        var start = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var reset = start.AddHours(3);
        static AgentLimit Window(string name, double? used, DateTimeOffset seen, DateTimeOffset? resets) =>
            new(AgentProviderKind.Codex, name, used, resets, false, seen);
        static string Show(IEnumerable<UsageSpent> spent) =>
            string.Join(", ", spent.Select(each => each.Window + " " + each.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string Spent(AgentLimit[] before, AgentLimit[] after, AgentLimit[]? first = null) => Show(AgentLimits.Spent(before, after, start, first));

        var earlier = start.AddMinutes(-1);
        var during = start.AddMinutes(10);
        Program.Equal("spent: the end less the start of the window",
            Spent([Window("5 h", 10, earlier, reset)], [Window("5 h", 14, during, reset)]), "5 h 4");
        Program.Equal("spent: each window apart, in the order the end reported them, rounded to a tenth",
            Spent([Window("5 h", 10.04, earlier, reset), Window("week", 30, earlier, reset)], [Window("5 h", 12.34, during, reset), Window("week", 31.25, during, reset)]),
            "5 h 2.3, week 1.3");
        Program.Equal("spent: never below nothing, as when another app lowered it",
            Spent([Window("5 h", 20, earlier, reset)], [Window("5 h", 18, during, reset)]), "5 h 0");
        Program.Equal("spent: a window that reset during the run is left out, one that reset by a few minutes' difference of computing is the same",
            (Spent([Window("5 h", 90, earlier, reset)], [Window("5 h", 3, during, reset.AddHours(5))]),
                Spent([Window("5 h", 10, earlier, reset)], [Window("5 h", 12, during, reset.AddSeconds(90))]),
                Spent([Window("5 h", 10, earlier, reset)], [Window("5 h", 12, during, reset.AddMinutes(3))])),
            (string.Empty, "5 h 2", string.Empty));
        Program.Equal("spent: no reset time on either side is the same window, one on only one side is not",
            (Spent([Window("5 h", 10, earlier, null)], [Window("5 h", 12, during, null)]),
                Spent([Window("5 h", 10, earlier, null)], [Window("5 h", 12, during, reset)])),
            ("5 h 2", string.Empty));
        Program.Equal("spent: a window not reported during the run is left out, and so is one without a number",
            (Spent([Window("5 h", 10, earlier, reset)], [Window("5 h", 14, earlier, reset)]),
                Spent([Window("5 h", 10, earlier, reset)], [Window("5 h", null, during, reset)]),
                Spent([Window("5 h", null, earlier, reset)], [Window("5 h", 14, during, reset)])),
            (string.Empty, string.Empty, string.Empty));

        var stale = Window("5 h", 10, start.AddMinutes(-16), reset);
        var firstReport = Window("5 h", 11, start.AddMinutes(1), reset);
        Program.Equal("spent: a start older than 15 minutes is not trusted: the first report of the run is the start, or there is none",
            (Spent([stale], [Window("5 h", 14, during, reset)]), Spent([stale], [Window("5 h", 14, during, reset)], [firstReport]),
                Spent([Window("5 h", 10, start.AddMinutes(-15), reset)], [Window("5 h", 14, during, reset)], [firstReport])),
            (string.Empty, "5 h 3", "5 h 4"));
        Program.Equal("spent: a window the start did not know has the first report of the run as its start; one reported before the run does not",
            (Spent([], [Window("week", 5, during, reset)], [Window("week", 4, start.AddMinutes(1), reset)]),
                Spent([], [Window("week", 5, during, reset)]),
                Spent([], [Window("week", 5, during, reset)], [Window("week", 4, earlier, reset)])),
            ("week 1", string.Empty, string.Empty));

        Program.Equal("spent: the runs of a task add up per window, the 5-hour one first",
            Show(AgentLimits.Total([new UsageSpent("week", 1), new UsageSpent("5 h", 2), new UsageSpent("5 h", 2.5), new UsageSpent("week, Opus", 0.5)]).Select(each => new UsageSpent(each.Key, each.Value))),
            "5 h 4.5, week 1, week, Opus 0.5");
        Program.Equal("spent: said as the run's line, the agent named once, only what took a tenth of a point or more",
            (AgentLimits.SpentWords("Codex", AgentLimits.Total([new UsageSpent("5 h", 4), new UsageSpent("week", 1)])),
                AgentLimits.SpentWords("Codex", AgentLimits.Total([new UsageSpent("5 h", 0.04), new UsageSpent("week", 1.25)])),
                AgentLimits.SpentWords("Claude", AgentLimits.Total([new UsageSpent("5 h", 0.4)])),
                AgentLimits.SpentWords("Codex", AgentLimits.Total([new UsageSpent("5 h", 0.04)])),
                AgentLimits.SpentWords("Codex", AgentLimits.Total([]))),
            ("used 4% of Codex 5 h, 1% of week", "used 1.3% of Codex week", "used 0.4% of Claude 5 h", string.Empty, string.Empty));

        var at = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var task = new AgentTaskRecord
        {
            Goal = "Add the dash", Workspace = "C:\\Work",
            Runs =
            [
                new AgentRunRecord { Provider = AgentProviderKind.Codex, StartedAt = at, Spent = [new UsageSpent("5 h", 3), new UsageSpent("week", 1)] },
                new AgentRunRecord { Provider = AgentProviderKind.ClaudeCode, StartedAt = at },
                new AgentRunRecord { Provider = AgentProviderKind.Codex, StartedAt = at, Spent = [new UsageSpent("5 h", 1.5)] }
            ]
        };
        Program.Equal("metrics: what each agent took of each window, summed over its runs",
            string.Join("; ", TaskMetrics.Of(task, AgentProviderKind.ClaudeCode).Agents.Select(agent => $"{agent.Provider}: {Show(agent.Spent.Select(each => new UsageSpent(each.Key, each.Value)))}")),
            "ClaudeCode: ; Codex: 5 h 4.5, week 1");

        var saved = JsonSerializer.Deserialize<AgentRunRecord>(JsonSerializer.Serialize(task.Runs[0]))!;
        var old = JsonSerializer.Deserialize<AgentRunRecord>("""{"Provider":1,"Outcome":"Done in 1:00"}""")!;
        Program.Equal("spent: kept with the run; a run of an older Link has none",
            (Show(saved.Spent), old.Spent.Count), ("5 h 3, week 1", 0));
    }

    /// <summary>A hand-off's brief starts from the task's original brief; the hand-over texts are kept apart, so that none is repeated in the next.</summary>
    private static void HandoversKeepTheBrief()
    {
        const string Original = "Make the landing animation";
        static int Count(string text) => (text.Length - text.Replace(Original, string.Empty, StringComparison.Ordinal).Length) / Original.Length;
        var record = new AgentTaskRecord { Goal = Original, Brief = Original, Workspace = @"C:\Work", ChangedFiles = ["tools/anim.py"] };
        record.Add(AgentEntryKind.You, "keep the pose");

        var first = AgentCoordinator.HandOffBrief(record, "Claude Code", numstat: null);
        record.Handovers.Add($"── Handed over to Codex ──\n{first}");
        var second = AgentCoordinator.HandOffBrief(record, "Codex", numstat: null);
        record.Handovers.Add($"── Back to Claude Code ──\n{AgentCoordinator.ReturnBrief(record, "Codex", numstat: null)}");
        record.Handovers.Add($"── Handed over to Codex ──\n{second}");
        var third = AgentCoordinator.HandOffBrief(record, "Codex", numstat: null);
        Program.Equal("hand-overs: the brief of every hand-off holds the original once, however many hand-overs there were",
            (Count(first), Count(second), Count(third), record.Brief), (1, 1, 1, Original));
        Program.Equal("hand-overs: what Link gave is the brief and then each hand-over under its mark",
            (record.BriefText.StartsWith(Original + "\n\n── Handed over to Codex ──\n" + first, StringComparison.Ordinal),
                record.BriefText.Contains("\n\n── Back to Claude Code ──\nWhile you were away, Codex", StringComparison.Ordinal),
                new AgentTaskRecord { Goal = "g", Brief = "just the brief", Workspace = "w" }.BriefText),
            (true, true, "just the brief"));

        var saved = JsonSerializer.Serialize(record);
        var again = JsonSerializer.Deserialize<AgentTaskRecord>(saved)!;
        var old = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"g","Brief":"b","Workspace":"w"}""")!;
        Program.Equal("hand-overs: kept with the task, derived text is not saved, a task of an older Link has none",
            (again.Handovers.Count, again.BriefText == record.BriefText, saved.Contains("BriefText", StringComparison.Ordinal), old.Handovers.Count, old.BriefText),
            (3, true, false, 0, "b"));
    }

    /// <summary>After a limit's wait, or a long idle, a big or cold session starts afresh from the task's state when Settings allow it.</summary>
    private static void CompactAfterWait()
    {
        var now = new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
        // The run ended `idle` ago, after ten minutes of work.
        static AgentRunRecord Run(DateTimeOffset now, long? held, TimeSpan idle, long? window = null, AgentProviderKind provider = AgentProviderKind.ClaudeCode) => new()
        {
            Provider = provider, StartedAt = now - idle - TimeSpan.FromMinutes(10), Duration = TimeSpan.FromMinutes(10),
            ContextTokens = held, ContextWindow = window
        };
        string? Why(AgentRunRecord? last, bool afterLimit, bool auto = true) => CompactRule.Why(last, auto, 450_000, now, afterLimit);
        static TimeSpan minutes(int count) => TimeSpan.FromMinutes(count);

        Program.Equal("compact after a wait: Settings' word comes first, and a task without a run has nothing to compact",
            (Why(Run(now, 400_000, minutes(5)), afterLimit: true, auto: false), Why(null, afterLimit: true), Why(null, afterLimit: false)),
            ((string?)null, (string?)null, (string?)null));
        Program.Equal("compact after a wait: a reply to a run just ended is never taken from its session, big or not",
            (Why(Run(now, 400_000, minutes(5)), afterLimit: false), Why(Run(now, 150_000, minutes(59)), afterLimit: false)),
            ((string?)null, (string?)null));
        AgentPlanItem[] plan = [new("Model", true, false), new("Rig", true, false), new("Animate", false, false)];
        string? Phase(long held, IReadOnlyList<AgentPlanItem> steps) => CompactRule.Why(Run(now, held, minutes(5)), true, 450_000, now, afterLimit: false, steps);
        Program.Equal("compact at a phase's end: a reply after a done part of the plan starts the next part afresh from half the budget, not below, not mid-step, not when all is done",
            (Phase(225_000, plan)?.StartsWith("2 of 3 steps of the plan are done and the session holds 225k of 450k", StringComparison.Ordinal), Phase(224_000, plan),
                Phase(300_000, [plan[0], plan[1], plan[2] with { Active = true }]), Phase(300_000, [plan[0], plan[1], plan[2] with { Done = true }]),
                Phase(300_000, [])),
            ((bool?)true, (string?)null, (string?)null, (string?)null, (string?)null));
        Program.Equal("compact after a wait: a reply after an hour idle starts afresh where the session is big enough, the idle counted from the run's end",
            (Why(Run(now, 150_000, minutes(60)), afterLimit: false)?.StartsWith("the session's cache has expired (60 min idle)", StringComparison.Ordinal),
                Why(Run(now, 50_000, TimeSpan.FromHours(5)), afterLimit: false)),
            ((bool?)true, (string?)null));
        Program.Equal("compact after a wait: Codex's cache lasts half an hour, Claude's an hour",
            (Why(Run(now, 150_000, minutes(30), provider: AgentProviderKind.Codex), afterLimit: false)?.StartsWith("the session's cache has expired (30 min idle)", StringComparison.Ordinal),
                Why(Run(now, 150_000, minutes(29), provider: AgentProviderKind.Codex), afterLimit: false), Why(Run(now, 150_000, minutes(45)), afterLimit: false)),
            ((bool?)true, (string?)null, (string?)null));
        Program.Equal("compact after a wait: after a limit's wait the rule applies at once: a big session starts afresh, a small one resumes",
            (Why(Run(now, 400_000, minutes(10)), afterLimit: true)?.StartsWith("the session holds 400k of 450k", StringComparison.Ordinal),
                Why(Run(now, 50_000, TimeSpan.FromHours(3)), afterLimit: true), Why(Run(now, null, TimeSpan.FromHours(3)), afterLimit: true)),
            ((bool?)true, (string?)null, (string?)null));
        Program.Equal("compact for a step of Link's own: a big session reviews afresh at once, a small one resumes, Settings' word first",
            (CompactRule.ForStep(Run(now, 400_000, minutes(2)), true, 450_000, now)?.StartsWith("the session holds 400k of 450k", StringComparison.Ordinal),
                CompactRule.ForStep(Run(now, 50_000, minutes(2)), true, 450_000, now), CompactRule.ForStep(Run(now, 400_000, minutes(2)), false, 450_000, now)),
            ((bool?)true, (string?)null, (string?)null));
        Program.Equal("compact after a wait: a model's smaller window caps the budget",
            Why(Run(now, 170_000, minutes(10), window: 200_000), afterLimit: true),
            "the session holds 170k of 200k: each step of the next run would re-send it; the task's state is far shorter");

        var workspace = TempFolder();
        try
        {
            var record = new AgentTaskRecord { Goal = "Add the dash", Brief = "Add the dash to the movement.", Workspace = workspace };
            record.Add(AgentEntryKind.You, "keep it short");
            var brief = TaskState.CompactContinue(record, null, workspace, "  Your usage limit has reset. Go on.  ");
            Program.Equal("compact after a wait: the fresh session gets the task's state, how to go on from it, then the words the old session was to get",
                (brief.StartsWith("# Task state\nGoal: Add the dash", StringComparison.Ordinal), brief.Contains("- keep it short", StringComparison.Ordinal),
                    brief.Contains(TaskState.GoOn + "\n\nYour usage limit has reset. Go on.", StringComparison.Ordinal), brief.EndsWith("Go on.", StringComparison.Ordinal),
                    brief.Split("Add the dash to the movement.").Length - 1),
                (true, true, true, true, 1));
        }
        finally
        {
            DeleteFolder(workspace);
        }

        // The wait is kept with the task, so that a restart of Link does not lose it.
        var waiting = new AgentTaskRecord { Goal = "g", Workspace = "w", ContinueAt = new DateTimeOffset(2026, 10, 8, 18, 1, 0, TimeSpan.FromHours(3)) };
        var saved = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(waiting))!;
        waiting.ContinueAt = null;
        Program.Equal("continue at: kept with the task and cleared by null; a task of an older Link waits for nothing",
            (saved.ContinueAt, JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(waiting))!.ContinueAt,
                JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"g","Workspace":"w"}""")!.ContinueAt),
            ((DateTimeOffset?)new DateTimeOffset(2026, 10, 8, 18, 1, 0, TimeSpan.FromHours(3)), (DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    /// <summary>Every specialist a brief names, in the order the work flows: where their words first occur.</summary>
    private static void RoleSuggestAll()
    {
        var roles = ProjectRoles.In(null);
        string Ids(string brief) => string.Join(", ", ProjectRoles.SuggestAll(brief, roles).Select(role => role.Id));
        Program.Equal("suggest all: a brief that goes from a model to its animation to the game names the specialists in that order",
            (Ids("Сделай 3д модель кабана, анимация бега и блюпринт для него"), Ids("Add the Enhanced Input action in a Blueprint, then retarget the animation, then make a low-poly mesh in Blender")),
            ("3d-artist, rigger-animator, gameplay-developer", "gameplay-developer, rigger-animator, 3d-artist"));
        Program.Equal("suggest all: the user's own words for a model, its animation and the combat system",
            Ids("сделать модель + анимировать и добавить в боевую систему"), "3d-artist, rigger-animator, gameplay-developer");
        Program.Equal("suggest all: a role appears once, by its earliest word, and one with no word in the brief is not there",
            (Ids("Retarget the animation, rig the skeleton, then retarget again"), Ids("Rename the variable"), Ids(string.Empty), ProjectRoles.SuggestAll("Fix the animation", []).Count),
            ("rigger-animator", string.Empty, string.Empty, 0));
        AgentRole Role(string id, params string[] keywords) => new(id, id, string.Empty, keywords, [], null, string.Empty, string.Empty);
        Program.Equal("suggest all: roles whose words start at the same place keep their order, and a word counts only at the start of a word",
            (string.Join(", ", ProjectRoles.SuggestAll("jump and run", [Role("b", "jump"), Role("a", "jump", "run")]).Select(role => role.Id)),
                string.Join(", ", ProjectRoles.SuggestAll("the inanimate run", [Role("one", "animat"), Role("two", "run")]).Select(role => role.Id))),
            ("b, a", "two"));
        Program.Equal("stems: where a stem starts, or -1", (Stems.IndexOf("a Jump", "jump"), Stems.IndexOf("inanimate", "animat"), Stems.IndexOf("x", string.Empty)), (2, -1, -1));
    }

    /// <summary>
    /// The colour of a specialist's jellyfish: Link's orange for General, a file's own colour, else a colour of the palette that
    /// no file names, handed out in the order of the ids, again from the first when they run out; the same every time.
    /// </summary>
    private static void RoleColors()
    {
        static AgentRole Role(string id, string? color = null) => new(id, id, string.Empty, [], [], null, string.Empty, string.Empty, color);
        static string Of(IReadOnlyList<AgentRole> roles) => string.Join(",", roles.Select(role => ProjectRoles.ColorOf(role, roles)));

        Program.Equal("role colors: General is Link's orange, whatever roles there are",
            (ProjectRoles.ColorOf(null, []), ProjectRoles.ColorOf(null, [Role("a")])), ("#F5843F", "#F5843F"));
        Program.Equal("role colors: the palette has Link's orange in no shade, and no color twice",
            (ProjectRoles.Colors.Contains(ProjectRoles.LinkColor), ProjectRoles.Colors.Contains("#FB923C"), ProjectRoles.Colors.Distinct().Count() == ProjectRoles.Colors.Count),
            (false, false, true));
        Program.Equal("role colors: a file's own color wins, in capitals",
            Of([Role("a", "#112233"), Role("b", "#abcdef")]), "#112233,#ABCDEF");

        // The roles without a color take the palette without the colors the files name, by id, wherever they stand in the list.
        var roles = new[] { Role("a", "#4ACFDF"), Role("c"), Role("b"), Role("d") };
        Program.Equal("role colors: the others take the free colors of the palette, in the order of their ids",
            Of(roles), "#4ACFDF,#A78BFA,#6C9EF8,#F472B6");
        Program.Equal("role colors: the same roles in another order give the same colors",
            Of([.. roles.Reverse()]), "#F472B6,#6C9EF8,#A78BFA,#4ACFDF");
        Program.Equal("role colors: ids are put in order by their letters as they are, capitals first",
            Of([Role("a"), Role("B")]), ProjectRoles.Colors[1] + "," + ProjectRoles.Colors[0]);

        var many = Enumerable.Range(1, 12).Select(number => Role($"r{number:00}")).ToArray();
        Program.Equal("role colors: more roles than colors go round the palette again",
            Of(many), string.Join(",", ProjectRoles.Colors.Concat(ProjectRoles.Colors.Take(2))));
        var taken = ProjectRoles.Colors.Select((color, index) => Role($"n{index}", color)).Append(Role("z")).ToArray();
        Program.Equal("role colors: when the files name every color, a role without one takes the palette from its start",
            ProjectRoles.ColorOf(taken[^1], taken), ProjectRoles.Colors[0]);

        var odd = new[] { Role("a", "orange"), Role("b", "#12345"), Role("c", "#GGGGGG"), Role("d", "123456"), Role("e", "#1234567") };
        Program.Equal("role colors: a color that is not #RRGGBB is none",
            Of(odd), string.Join(",", ProjectRoles.Colors.Take(5)));
    }

    /// <summary>A role file's <c>color:</c> line is read ("#RRGGBB" or none); the eight roles that ship keep theirs; a workspace role is given a free one.</summary>
    private static void RoleColorFiles()
    {
        var builtIn = ProjectRoles.In(null);
        Program.Equal("role color files: each built-in role names its own color in its file",
            string.Join(",", builtIn.Select(role => role.Id + "=" + role.Color)),
            "3d-artist=#A78BFA,build-engineer=#A3E635,gameplay-developer=#6C9EF8,level-artist=#34D399,qa-playtest=#F87171,rigger-animator=#F472B6,technical-designer=#FBBF24,ui-ux-designer=#38BDF8");
        var colors = builtIn.Select(role => ProjectRoles.ColorOf(role, builtIn)).ToArray();
        Program.Equal("role color files: the eight colors differ, and none is Link's orange",
            (colors.Distinct().Count(), colors.Contains(ProjectRoles.LinkColor)), (8, false));

        var workspace = TempFolder();
        try
        {
            var folder = ProjectRoles.FolderIn(workspace);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "audio.md"), "---\nname: Audio\ndescription: Sound.\nkeywords: sound\n---\nMix the sound.");
            File.WriteAllText(Path.Combine(folder, "zeta.md"), "---\nname: Zeta\ndescription: Last.\nkeywords: zeta\ncolor: orange\n---\nBody");
            File.WriteAllText(Path.Combine(folder, "loud.md"), "---\nname: Loud\ndescription: Own color.\nkeywords: loud\ncolor: #ff8800\n---\nBody");
            var roles = ProjectRoles.In(workspace);
            string Color(string id) => ProjectRoles.ColorOf(roles.First(role => role.Id == id), roles);
            Program.Equal("role color files: a color is read in capitals, one that is not a color is none",
                (roles.First(role => role.Id == "loud").Color, roles.First(role => role.Id == "zeta").Color, roles.First(role => role.Id == "audio").Color),
                ("#FF8800", (string?)null, (string?)null));
            Program.Equal("role color files: workspace roles without a color take the two the built-in ones leave, and the built-in ones do not move",
                (Color("loud"), Color("audio"), Color("zeta"), Color("3d-artist"), Color("build-engineer")),
                ("#FF8800", "#4ACFDF", "#C084FC", "#A78BFA", "#A3E635"));
        }
        finally
        {
            DeleteFolder(workspace);
        }
    }

    /// <summary>The inspector's Context meter is what the session holds over the limit its line names: the budget, or the model's window if that is smaller; none when unknown.</summary>
    private static void HarnessContextMeter()
    {
        var caps = new AgentCapabilities(true, true, true, "Anthropic");
        HarnessSection Context(long? used, long? window, int budget = 400_000) =>
            HarnessView.Build(new HarnessInput(null, "Claude Code", "Opus 5.5 · Medium", "a usual task", caps, budget) { ContextUsed = used, ContextWindow = window })
                .Sections.Single(section => section.Title == "Context");

        Program.Equal("harness meter: used over the budget while the window is wider",
            Context(182_000, 1_000_000).Meter, (double?)(182_000 / 400_000.0));
        Program.Equal("harness meter: used over the window when it is smaller than the budget",
            Context(150_000, 200_000).Meter, (double?)0.75);
        Program.Equal("harness meter: no window told, the budget is the limit",
            Context(300_000, null).Meter, (double?)0.75);
        Program.Equal("harness meter: nothing told yet, no meter; nothing held, an empty one",
            (Context(null, 1_000_000).Meter, Context(0, 1_000_000).Meter), ((double?)null, (double?)0.0));
        Program.Equal("harness meter: over the limit it is full, not more",
            Context(500_000, 1_000_000).Meter, (double?)1.0);
        Program.Equal("harness meter: the other sections have none",
            HarnessView.Build(new HarnessInput(null, "Claude Code", "Opus 5.5", "x", caps, 400_000) { ContextUsed = 1000 }).Sections.Count(section => section.Meter is not null), 1);

        HarnessSection Role(string? roleId) =>
            HarnessView.Build(new HarnessInput(null, "Claude Code", "Opus 5.5", "x", caps, 400_000)
            {
                Task = roleId is null ? null : new AgentTaskRecord { Goal = "Goal", Workspace = "w", RoleIds = [roleId] }
            }).Sections.Single(section => section.Title == "Role");
        Program.Equal("harness role: its jellyfish is the role's color, Link's orange for General, none for a role whose file is gone",
            (Role("rigger-animator").Color, Role(null).Color, Role("gone").Color), ("#F472B6", "#F5843F", (string?)null));
    }

    /// <summary>The Studio's tasks sidebar is open unless it was folded; an older settings.json, and one with a Studio section from before, open it.</summary>
    private static void StudioSidebarKept()
    {
        var older = JsonSerializer.Deserialize<AppSettings>("""{"Opacity":0.7}""")!;
        older.Normalize();
        var before = JsonSerializer.Deserialize<AppSettings>("""{"Studio":{"Width":900,"Height":600,"InspectorOpen":false}}""")!;
        before.Normalize();
        Program.Equal("studio sidebar: open by default, and in a section written before it was there",
            (new StudioSettings().SidebarOpen, older.Studio.SidebarOpen, before.Studio.SidebarOpen, before.Studio.InspectorOpen, before.Studio.Width), (true, true, true, false, 900.0));

        var folded = new AppSettings();
        folded.Studio.SidebarOpen = false;
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(folded))!;
        back.Normalize();
        Program.Equal("studio sidebar: a folded sidebar is saved, and so is the inspector's choice apart from it",
            (back.Studio.SidebarOpen, back.Studio.InspectorOpen), (false, true));
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

        public Task<AgentProviderStatus> CheckAsync(AgentSettings setup, CancellationToken cancellationToken) =>
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

    private static void OldTrainingLoads()
    {
        var training = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Train","Workspace":"w","Mode":3,"Training":{"Stage":2,"Exams":[{}]}}""")!;
        var team = JsonSerializer.Deserialize<AgentTaskRecord>("""{"Goal":"Team","Workspace":"w","Mode":1,"Team":[{"Text":"Model it","Worker":"Codex"}]}""")!;
        Program.Equal("old records: a training or a team task loads as an ordinary one, its course or steps dropped",
            string.Join("|", ProjectService.Readable([training, team]).Select(task => task.Goal)), "Train|Team");
    }

    /// <summary>The pictures a run's turn shows: the frames its answer names and the images it made, by path.</summary>
    private static void RunImagesFound()
    {
        Program.Equal("turn pictures: image paths in backticks with spaces, bare, in a link and at a sentence's end, in order",
            string.Join("|", RunImages.Named(@"Frames: `C:\My Shots\crate game.png`, C:\shots\thumb_1.png and [lineup](Art/Review/lineup.JPG).
See shots/flat.png. Not crate.blend or a.pngx.")),
            @"C:\My Shots\crate game.png|C:\shots\thumb_1.png|Art/Review/lineup.JPG|shots/flat.png");

        var workspace = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "Art"));
            var game = Path.Combine(workspace, "Art", "game.png");
            File.WriteAllText(game, "png");
            File.WriteAllText(Path.Combine(workspace, "Art", "thumb.jpg"), "jpg");
            File.WriteAllText(Path.Combine(workspace, "Art", "crate.blend"), "blend");
            var frames = RunImages.Frames([game, "Art/thumb.jpg", "Art/game.png", "Art/crate.blend", "Art/gone.png", "bad\0path.png"], workspace, File.Exists);
            Program.Equal("turn pictures: frames are the images that exist, full paths, a relative one from the workspace, each once",
                string.Join("|", frames.Select(frame => Path.GetRelativePath(workspace, frame).Replace('\\', '/'))), "Art/game.png|Art/thumb.jpg");
            // A real PNG of one pixel, read off the UI thread as the thread reads its pictures.
            var render = Path.Combine(workspace, "Art", "render.png");
            File.WriteAllBytes(render, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
            Program.Equal("turn pictures: a run's picture is read for its turn, and a file that is no picture shows nothing",
                (System.Threading.Tasks.Task.Run(() => ThreadImage.Read(render) is not null).GetAwaiter().GetResult(), ThreadImage.Read(game) is null),
                (true, true));
        }
        finally
        {
            DeleteFolder(workspace);
        }

        var since = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? Written(string path) => Path.GetFileName(path) switch
        {
            "ref.png" => since.AddHours(-1),
            "gone.png" => null,
            "edge.png" => since.AddSeconds(-1),
            _ => since.AddMinutes(1)
        };
        Program.Equal("turn pictures: the answer's first, then the newest looked at, each once; none from before the run or gone",
            string.Join("|", RunImages.Of(["Art/final.png", "notes.txt"],
                [@"C:\Temp\shots\a.png", @"C:\Game\refs\ref.png", @"C:\Temp\shots\b.png", "edge.png", @"C:\Temp\gone.png", @"C:\Temp\shots\a.png"],
                @"C:\Game", since, Written)),
            @"C:\Game\Art\final.png|C:\Temp\shots\a.png|C:\Game\edge.png|C:\Temp\shots\b.png");
        var temp = Environment.GetEnvironmentVariable("TEMP")!;
        Program.Equal("turn pictures: a path as the agent's shell wrote it is found where Windows has it",
            string.Join("|", RunImages.Of([@"%TEMP%\shots\a.png", "$TEMP/shots/b.png", @"$env:TEMP\shots\c.png", "/tmp/shots/d.png", "/c/Game/e.png",
                "~/shots/f.png"], [], @"C:\Game", since, Written)),
            string.Join("|", Path.GetFullPath(Path.Combine(temp, "shots", "a.png")), Path.GetFullPath(Path.Combine(temp, "shots", "b.png")),
                Path.GetFullPath(Path.Combine(temp, "shots", "c.png")), Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shots", "d.png")),
                @"C:\Game\e.png", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "shots", "f.png")));
        Program.Equal("turn pictures: a run that rendered many keeps its newest",
            string.Join("|", RunImages.Of([], Enumerable.Range(1, 8).Select(n => $@"C:\Temp\{n}.png"), @"C:\Game", since, Written)),
            @"C:\Temp\8.png|C:\Temp\7.png|C:\Temp\6.png|C:\Temp\5.png|C:\Temp\4.png|C:\Temp\3.png");

    }

    private static void ToolPacksUsed()
    {
        var builtIn = ToolPacks.All(ToolPacks.BuiltInFolder);
        Program.Equal("tool packs: Link's own, each with a name and what shows it",
            string.Join("|", builtIn.Select(pack => $"{pack.Id}={pack.Name}:{string.Join(",", pack.Detect)}")),
            "blender=Blender:*.blend|cascadeur=Cascadeur:*.casc|godot=Godot:project.godot|perforce=Perforce:.p4config,p4config.txt,P4CONFIG|" +
            "unity=Unity:ProjectSettings/ProjectVersion.txt|unreal=Unreal:*.uproject");
        var roleIds = ProjectRoles.In(null).Select(role => role.Id).ToHashSet();
        Program.Equal("tool packs: each role a pack adds to is a built-in role",
            builtIn.Where(pack => Directory.Exists(Path.Combine(pack.Folder, "roles")))
                .SelectMany(pack => Directory.GetFiles(Path.Combine(pack.Folder, "roles"), "*.md").Select(Path.GetFileNameWithoutExtension))
                .Where(id => !roleIds.Contains(id!)).ToArray().Length, 0);

        var folder = ToolPacks.Folder;
        var packs = TempFolder();
        var workspace = TempFolder();
        try
        {
            ToolPacks.Folder = packs;
            Program.Equal("tool packs: Seed copies Link's own into an empty folder", ToolPacks.Seed(ToolPacks.BuiltInFolder, packs) > 0, true);
            var mine = Path.Combine(packs, "unreal", "roles", "gameplay-developer.md");
            File.WriteAllText(mine, "---\nskills: unreal-blueprint, ue-own\nkeywords: niagara\n---\n- My Unreal way.");
            File.Delete(Path.Combine(packs, "godot", "pack.md"));
            Program.Equal("tool packs: Seed again copies only what is missing: the user's changes stay",
                (ToolPacks.Seed(ToolPacks.BuiltInFolder, packs), File.ReadAllText(mine).Contains("My Unreal way")), (1, true));

            // A newer Link's pack: a file still as Link put it takes the new text, one the user changed stays theirs.
            var newer = TempFolder();
            var seededTo = TempFolder();
            try
            {
                var skill = Path.Combine(newer, "blender", "skills", "blender-model", "SKILL.md");
                Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
                File.WriteAllText(skill, "v1");
                var seededCopy = Path.Combine(seededTo, "blender", "skills", "blender-model", "SKILL.md");
                var first = ToolPacks.Seed(newer, seededTo);
                File.WriteAllText(skill, "v2");
                Program.Equal("tool packs: a newer Link's version replaces a file the user did not change",
                    (first, ToolPacks.Seed(newer, seededTo), File.ReadAllText(seededCopy)), (1, 1, "v2"));
                File.WriteAllText(seededCopy, "v2 and my lesson");
                File.WriteAllText(skill, "v3");
                Program.Equal("tool packs: a file the user changed stays theirs when Link's changes too",
                    (ToolPacks.Seed(newer, seededTo), File.ReadAllText(seededCopy)), (0, "v2 and my lesson"));
            }
            finally
            {
                DeleteFolder(newer);
                DeleteFolder(seededTo);
            }

            var shared = Path.Combine(packs, "unreal", "skills", "ue-own");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "SKILL.md"), "---\nname: ue-own\ndescription: Build the Unreal project\n---\nRun the build.");
            var other = Path.Combine(packs, "unreal", "skills", "ue-other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "SKILL.md"), "---\nname: ue-other\ndescription: Other\n---\nOther.");
            var empty = TempFolder();
            Program.Equal("tool packs: a folder without its engine's files uses no pack", ToolPacks.Names(empty), string.Empty);
            DeleteFolder(empty);

            File.WriteAllText(Path.Combine(workspace, "Game.uproject"), "{}");
            Directory.CreateDirectory(Path.Combine(workspace, "Art", "Hero"));
            File.WriteAllText(Path.Combine(workspace, "Art", "Hero", "hero.blend"), "x");
            var copy = Path.Combine(ProjectSkills.FolderIn(workspace), "ue-own");
            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "SKILL.md"), "---\nname: ue-own\ndescription: Build the Unreal project\n---\nRun the build with -NoHotReload.");
            Program.Equal("tool packs: a .uproject at the top and a .blend two levels down show their packs, with the tools every game brings",
                ToolPacks.Names(workspace), "Blender + Unreal");
            var unity = TempFolder();
            Directory.CreateDirectory(Path.Combine(unity, "ProjectSettings"));
            File.WriteAllText(Path.Combine(unity, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1");
            Program.Equal("tool packs: a Unity project with no .blend yet still has Blender, which comes with the engine",
                ToolPacks.Names(unity), "Blender + Unity");
            Program.Equal("tool packs: the engine's rules go with a developer, not with an artist who works beside the engine",
                (AgentRules.Before("Fix it", unity, false, [ProjectRoles.Find("gameplay-developer", unity)!]).Contains("## In Unity"),
                    AgentRules.Before("Model it", unity, false, [ProjectRoles.Find("3d-artist", unity)!]).Contains("## In Unity")),
                (true, false));
            DeleteFolder(unity);
            var repository = TempFolder();
            Directory.CreateDirectory(Path.Combine(repository, "Art"));
            Directory.CreateDirectory(Path.Combine(repository, "Game", "ProjectSettings"));
            File.WriteAllText(Path.Combine(repository, "Game", "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1");
            Program.Equal("tool packs: a Unity project in a folder of the repository shows its pack",
                ToolPacks.Names(repository), "Blender + Unity");
            DeleteFolder(repository);

            var skills = ProjectSkills.In(workspace);
            var ueOther = skills.Single(skill => skill.Name == "ue-other");
            Program.Equal("tool packs: the workspace's own skill overrides the pack's of its name, the pack's others join with their pack",
                (skills[0].Name, skills[0].Pack, skills.Count(skill => skill.Name == "ue-own"), ueOther.Pack, skills.Any(skill => skill is { Name: "blender-model", Pack: "Blender" })),
                ("ue-own", (string?)null, 1, "Unreal", true));
            Program.Equal("tool packs: a pack's skill is told in full, the workspace's from its root",
                (ProjectSkills.PathFor(workspace, skills[0]), ProjectSkills.PathFor(workspace, ueOther)),
                (".agents/skills/ue-own/SKILL.md", Path.Combine(other, "SKILL.md").Replace('\\', '/')));

            var codexRules = AgentRules.Before("Fix it", workspace, listOwnSkills: false);
            Program.Equal("tool packs: an agent that finds the workspace's skills itself is still told the packs', which lie outside it",
                (codexRules.Contains($"- `{ProjectSkills.PathFor(workspace, ueOther)}`: Other (Unreal pack, shared)"), codexRules.Contains(".agents/skills/ue-own")),
                (true, false));

            var developer = ProjectRoles.Find("unreal-developer", workspace)!;
            Program.Equal("tool packs: the old role's name finds the gameplay developer, with what the pack adds",
                (developer.Id, developer.Body.Contains("\n## In Unreal\n- My Unreal way."), string.Join(",", developer.Skills), developer.Keywords.Contains("niagara")),
                ("gameplay-developer", true, "unreal-blueprint,ue-own", true));
            var artist = ProjectRoles.Find("3d-artist", workspace)!;
            Program.Equal("tool packs: a role takes each of the workspace's packs in turn",
                (artist.Body.Contains("## In Blender"), artist.Body.Contains("## In Unreal"), artist.Skills.Contains("blender-model")), (true, true, true));
            Program.Equal("tool packs: without the packs the role is the engine's-free one",
                ProjectRoles.Find("3d-artist", null)!.Body.Contains("## In "), false);

            var lessons = ToolPacks.Lessons(workspace, DateTimeOffset.Now.AddMinutes(-1));
            Program.Equal("tool packs: a copy written during the task that differs from the pack's is a lesson; an older one is not",
                (string.Join("|", lessons.Select(lesson => lesson.Copy.Name)), ToolPacks.Lessons(workspace, DateTimeOffset.Now.AddMinutes(1)).Count), ("ue-own", 0));
            ToolPacks.Share(lessons[0].Copy, lessons[0].Shared);
            Program.Equal("tool packs: sharing puts the copy into the pack and takes it out of the project",
                (File.ReadAllText(Path.Combine(shared, "SKILL.md")).Contains("-NoHotReload"), Directory.Exists(copy)), (true, false));
        }
        finally
        {
            ToolPacks.Folder = folder;
            DeleteFolder(packs);
            DeleteFolder(workspace);
        }
    }
}
