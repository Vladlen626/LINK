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
    /// A new session's input: the rules, the specialist's role and the skills picked for the task, the workspace's skills
    /// when the agent does not find them itself, then the task.
    /// </summary>
    public static string Before(string task, string workspace, bool listSkills, AgentRole? role = null, IReadOnlyList<string>? picked = null)
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
        // A Perforce workspace's files are read-only until opened: the agent is told how to open them.
        if (AgentChanges.IsPerforce(workspace) && ReadHarness(PerforceFile) is { Length: > 0 } perforce) input.Append("\n\n").Append(perforce);
        if (ProjectRoles.SessionText(role, picked ?? [], workspace) is { Length: > 0 } specialist) input.Append("\n\n").Append(specialist);
        var skills = listSkills ? ProjectSkills.In(workspace) : [];
        if (skills.Count > 0)
        {
            input.Append("\n\n## Skills of this workspace\n");
            foreach (var skill in skills)
            {
                input.Append("\n- `").Append(Path.GetRelativePath(workspace, skill.FilePath).Replace('\\', '/')).Append("`: ").Append(skill.Description);
            }
        }

        return input.Append("\n\n# Task\n\n").Append(task).ToString();
    }

    public static string PerforceFile => Path.Combine(AppContext.BaseDirectory, "harness", "perforce.md");

    /// <summary>A harness file's text without its notes for whoever edits it.</summary>
    public static string WithoutNotes(string text) => Comment().Replace(text, string.Empty).Trim();

    /// <summary>A harness file without its notes for whoever edits it; empty when it is missing.</summary>
    private static string ReadHarness(string file)
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
