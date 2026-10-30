using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The harness every task goes with: the agent's rules (a file beside Link.exe, to be changed without a new build) at
/// the start of each new session, whatever the workspace holds. A resumed session has them already.
/// </summary>
public static partial class AgentRules
{
    public static string File => Path.Combine(AppContext.BaseDirectory, "harness", "agent-rules.md");

    /// <summary>
    /// A new session's input: the rules, the skills of the workspace's tool packs and its own skills when the agent does
    /// not find them itself (<paramref name="listOwnSkills"/>), the specialist's role and the skills picked for the task,
    /// then the task. The rules Link's guard holds go as words only when it does not watch the agent (<paramref name="guarded"/>);
    /// <paramref name="helpers"/> says when to hand work to the run's helpers.
    /// </summary>
    public static string Before(string task, string workspace, bool listOwnSkills, AgentRole? role = null, IReadOnlyList<string>? picked = null,
        bool guarded = false, string? helpers = null)
    {
        string rules;
        try
        {
            rules = WithoutNotes(System.IO.File.ReadAllText(File));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The agent's rules are missing: {File}", exception);
        }

        var input = new StringBuilder(rules);
        // What Link's guard refuses by code goes as words only to an agent the guard does not watch.
        if (!guarded && ReadHarness(GuardedFile) is { Length: > 0 } held) input.Append("\n\n").Append(held);
        // A Perforce workspace's files are read-only until opened: the agent is told how to open them.
        if (AgentChanges.IsPerforce(workspace) && ReadHarness(PerforceFile) is { Length: > 0 } perforce) input.Append("\n\n").Append(perforce);
        // A workspace above its repository: git fails in it, and an agent learns that only from its first git command.
        if (AgentChanges.RepositoriesInside(workspace) is { Count: > 0 } repositories)
        {
            input.Append("\n\nGit: the workspace itself is not a repository; run git in ")
                .Append(string.Join(", ", repositories.Select(repository => $"`{repository}/`"))).Append('.');
        }

        // One map: the repository's own where it keeps one, else Link's. Skills written for Link's name docs/map.md.
        if (ProjectMap.Find(workspace) is { } found)
        {
            var map = ProjectMap.Relative(workspace, found);
            input.Append("\n\nThe project's map: `").Append(map).Append("`, what is where and how to build and check. Read it first.");
            if (map != ProjectMap.RelativePath) input.Append(" Where a skill or a role names `docs/map.md`, it means this file.");
        }
        // The engine's or tool's own rules, for every task in a project of it, but a role's that works beside the engine.
        foreach (var pack in role is { PackRules: false } ? [] : ToolPacks.For(workspace))
        {
            if (ReadHarness(Path.Combine(pack.Folder, "rules.md")) is { Length: > 0 } packRules) input.Append("\n\n").Append(packRules);
        }
        if (helpers is { Length: > 0 }) input.Append("\n\n").Append(helpers);
        // A pack's skills lie outside the workspace, where no CLI looks for skills: every agent is told of them.
        var skills = ProjectSkills.In(workspace).Where(skill => listOwnSkills || skill.Pack is not null).ToArray();
        if (skills.Length > 0)
        {
            input.Append("\n\n## Skills of this workspace\n");
            foreach (var skill in skills)
            {
                input.Append("\n- `").Append(ProjectSkills.PathFor(workspace, skill)).Append("`: ").Append(skill.Description);
                // A pack's skill serves every project: a general lesson goes into a copy of it, a project's into its add-on (see the rules).
                if (skill.Pack is { } pack) input.Append(" (").Append(pack).Append(" pack, shared)");
            }
        }

        // The role changes from step to step and the workspace's skills do not: the stable part goes first, for the cache.
        if (ProjectRoles.SessionText(role, picked ?? [], workspace) is { Length: > 0 } specialist) input.Append("\n\n").Append(specialist);
        return input.Append("\n\n# Task\n\n").Append(task).ToString();
    }

    public static string GuardedFile => Path.Combine(AppContext.BaseDirectory, "harness", "guarded-rules.md");

    public static string HelpersFile => Path.Combine(AppContext.BaseDirectory, "harness", "helpers.md");

    public static string CodexHelperFile => Path.Combine(AppContext.BaseDirectory, "harness", "codex-helper.md");

    public static string PerforceFile => Path.Combine(AppContext.BaseDirectory, "harness", "perforce.md");

    /// <summary>A harness file's text without its notes for whoever edits it.</summary>
    public static string WithoutNotes(string text) => Comment().Replace(text, string.Empty).Trim();

    /// <summary>A harness file without its notes for whoever edits it; empty when it is missing.</summary>
    public static string ReadHarness(string file)
    {
        try
        {
            return WithoutNotes(System.IO.File.ReadAllText(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // The note for whoever edits the rules; the agent needs none of it.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
