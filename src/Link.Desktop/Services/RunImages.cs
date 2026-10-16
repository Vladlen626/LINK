using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The pictures a run's turn shows: the images it made while it ran (renders, screenshots, sheets) that it looked at or
/// named in its answer. An image older than the run, such as a reference or an approved frame, is not its work.
/// </summary>
public static partial class RunImages
{
    /// <summary>So many pictures a turn keeps: those the answer names first, then the newest the run looked at.</summary>
    public const int MostShown = 6;

    // A file written just before the run's clock started is still its own: the two clocks are not one.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>Whether a file is a picture an agent can be given and a turn can show.</summary>
    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>
    /// The run's pictures: those its answer names (<paramref name="named"/>), then those it looked at or wrote
    /// (<paramref name="seen"/>, oldest first), newest first; each once, at most <see cref="MostShown"/>, and only those
    /// written (<paramref name="written"/>, null for a missing file) since <paramref name="since"/>.
    /// </summary>
    public static IReadOnlyList<string> Of(IEnumerable<string> named, IEnumerable<string> seen, string workspace, DateTimeOffset since,
        Func<string, DateTimeOffset?> written) =>
        [.. ArtCritic.Frames([.. named, .. seen.Reverse()], workspace, path => written(path) >= since - Slack).Take(MostShown)];

    /// <summary>
    /// A path in full: a relative one from the workspace, one an agent wrote as its shell does (<c>%TEMP%\…</c>,
    /// <c>$TEMP/…</c>, <c>$env:TEMP\…</c>, Git Bash's <c>/tmp/…</c> and <c>/c/…</c>, <c>~/…</c>) where Windows has it;
    /// null for one that cannot be a file's.
    /// </summary>
    public static string? Full(string path, string workspace)
    {
        path = ShellVariable().Replace(Environment.ExpandEnvironmentVariables(path),
            variable => Environment.GetEnvironmentVariable(variable.Groups["name"].Value) ?? variable.Value);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }
        else if (path.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            // Git for Windows mounts /tmp on the user's temp folder.
            path = Path.Combine(Path.GetTempPath(), path[5..]);
        }
        else if (GitBashDrive().IsMatch(path))
        {
            path = $"{char.ToUpperInvariant(path[1])}:{path[2..]}";
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>When a file was last written; null when it is not there.</summary>
    public static DateTimeOffset? Written(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    // $TEMP, ${TEMP} as Bash writes a variable, $env:TEMP as PowerShell does.
    [GeneratedRegex(@"\$(?:env:)?\{?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}?")]
    private static partial Regex ShellVariable();

    [GeneratedRegex("^/[A-Za-z]/")]
    private static partial Regex GitBashDrive();
}
