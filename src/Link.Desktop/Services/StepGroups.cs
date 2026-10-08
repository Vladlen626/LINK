using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What a tool step did, as the mark its row starts with tells.</summary>
public enum ToolKind
{
    Command,
    Read,
    Search,
    Edit,

    /// <summary>Any other tool: an MCP call, a web search, a skill. Also every tool row of an earlier Link, which marked them all alike.</summary>
    Other
}

/// <summary>
/// The tool steps between two pieces of the agent's own words make one group in the thread, folded into a line as the
/// agents' own apps fold them: "Ran 3 commands, read 2 files, edited 1 file". A step is kept as its row of text, so a
/// tool row starts with a mark for its kind, and the group is read back from the rows.
/// </summary>
public static class StepGroups
{
    /// <summary>The row of a tool call: the mark of what it does, then its words.</summary>
    public static string Step(AgentEvent call) => $"{Mark(call)} {call.Text}";

    private static char Mark(AgentEvent call) => call.Detail switch
    {
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "file_change" => '✎',
        "Read" => '≡',
        _ when call.IsSearch => '⌕',
        "Bash" or "PowerShell" or "command" => '$',
        _ => '▸'
    };

    /// <summary>The kind of a step row; null for the agent's words and every row that is no tool call.</summary>
    public static ToolKind? KindOf(string step) => step.Length > 1 && step[1] == ' '
        ? step[0] switch
        {
            '$' => ToolKind.Command,
            '≡' => ToolKind.Read,
            '⌕' => ToolKind.Search,
            '✎' => ToolKind.Edit,
            '▸' => ToolKind.Other,
            _ => null
        }
        : null;

    /// <summary>
    /// A group's tool steps in a line. A file read or edited again is the same file, as its row says; the rows that are
    /// no tool call are left out. Empty when there is no tool call.
    /// </summary>
    public static string Summarize(IEnumerable<string> steps)
    {
        var tools = steps.Select(step => (Kind: KindOf(step), Step: step)).Where(tool => tool.Kind is not null).ToArray();
        int Calls(ToolKind kind) => tools.Count(tool => tool.Kind == kind);
        int Files(ToolKind kind) => tools.Where(tool => tool.Kind == kind).Select(tool => tool.Step).Distinct().Count();
        var parts = new[]
        {
            Part(Calls(ToolKind.Command), "ran {0} command{1}"),
            Part(Files(ToolKind.Read), "read {0} file{1}"),
            Part(Files(ToolKind.Edit), "edited {0} file{1}"),
            Part(Calls(ToolKind.Search), "searched {0}"),
            Part(Calls(ToolKind.Other), "used {0} tool{1}")
        }.Where(part => part.Length > 0);
        var line = string.Join(", ", parts);
        return line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];

        static string Part(int count, string format) =>
            count == 0 ? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, count, count == 1 ? string.Empty : "s");
    }
}
