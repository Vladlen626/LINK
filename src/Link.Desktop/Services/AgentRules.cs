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
    /// A new session's input: the rules, the workspace's skills when the agent does not find them itself, then the task.
    /// </summary>
    public static string Before(string task, string workspace, bool listSkills)
    {
        string rules;
        try
        {
            rules = Comment().Replace(System.IO.File.ReadAllText(File), string.Empty).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The agent's rules are missing: {File}", exception);
        }

        var input = new StringBuilder(rules);
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

    // The note for whoever edits the rules; the agent needs none of it.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
