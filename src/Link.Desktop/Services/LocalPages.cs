using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// The pages an agent opened on this machine (a model's review window), named at the end of its answer in a fenced
/// <c>link-page</c> block of JSON: what the button says, the page's address and the command that brings the page back.
/// The turn shows them as buttons, and a click runs that command itself: reopening a window is not worth an agent run.
/// </summary>
public static partial class LocalPages
{
    /// <summary>
    /// A page: what its button says, where it is, the skill's command that serves it again (empty: none), and the words
    /// a second button sends the agent once the user has worked on the page (null: no such button).
    /// </summary>
    public sealed record Page(string Label, Uri Address, IReadOnlyList<string> Reopen, PageReply? Reply = null);

    /// <summary>
    /// The second button: what it says ("Apply marks"), the reply it sends, and the skill's command whose output goes with
    /// the reply (the open marks as text; empty: none), so the agent spends no turn of its own fetching them.
    /// </summary>
    public sealed record PageReply(string Label, string Text, IReadOnlyList<string>? Attach = null);

    // A list of a server's pages would bury the answer under buttons.
    private const int Kept = 3;

    /// <summary>The answer without its link-page blocks, and the pages they name; a block that does not read stays; one that leads off this machine goes without a button.</summary>
    public static (string Text, IReadOnlyList<Page> Pages) Split(string answer)
    {
        var pages = new List<Page>();
        var text = Block().Replace(answer, match =>
        {
            if (Read(match.Groups[1].Value) is not { } page) return match.Value;
            if (IsLocal(page.Address) && pages.Count < Kept && pages.All(kept => kept.Address != page.Address)) pages.Add(page);
            return string.Empty;
        });
        return text == answer ? (answer, pages) : (text.Trim(), pages);
    }

    private static Page? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || AgentQuestion.Text(root, "label")?.Trim() is not { Length: > 0 } label
                || AgentQuestion.Text(root, "url")?.Trim() is not { } url
                || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return null;
            var reopen = Command(root, "reopen");
            var reply = root.TryGetProperty("reply", out var said) && said.ValueKind == JsonValueKind.Object
                && AgentQuestion.Text(said, "label")?.Trim() is { Length: > 0 } replyLabel
                && AgentQuestion.Text(said, "text")?.Trim() is { Length: > 0 } replyText
                ? new PageReply(replyLabel, replyText, Command(said, "attach"))
                : null;
            return new Page(label, address, reopen, reply);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] Command(JsonElement element, string name) =>
        element.TryGetProperty(name, out var command) && command.ValueKind == JsonValueKind.Array
        && command.EnumerateArray().All(part => part.ValueKind == JsonValueKind.String)
            ? command.EnumerateArray().Select(part => part.GetString()!).ToArray()
            : [];

    // Only this machine's pages: a button with the agent's words on it never leads elsewhere.
    private static bool IsLocal(Uri address) =>
        address.Scheme is "http" or "https" && (address.IsLoopback || address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    // What its text goes with, at most: the open marks of a review, not a log.
    private const int AttachedChars = 12_000;

    /// <summary>
    /// The script of a command Link may run: Python (directly or through <c>uv run</c>) on a script of a skill (the tool
    /// packs' or the workspace's own), never any command a model wrote; null for any other.
    /// </summary>
    public static string? ScriptOf(IReadOnlyList<string> command, string? workspace)
    {
        var at = command.ToList().FindIndex(part => part.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (at < 1 || !command.Take(at).All(part => Python().IsMatch(Path.GetFileName(part)) || part is "run" or "--no-project")) return null;
        if (!Path.IsPathFullyQualified(command[at])) return null;
        var script = Path.GetFullPath(command[at]);
        string?[] roots = [ToolPacks.Folder, workspace is null ? null : ProjectSkills.FolderIn(workspace)];
        return File.Exists(script) && roots.OfType<string>().Any(root =>
            script.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ? script
            : null;
    }

    /// <summary>
    /// Serves the page again by its command, hidden and with no agent: the last address of this machine the command
    /// prints, else null (no command Link may run, it failed or it took too long).
    /// </summary>
    public static async Task<Uri?> ReopenAsync(Page page, string? workspace) =>
        await RunAsync(page.Reopen, workspace) is { } printed && Address().Matches(printed).LastOrDefault() is { } found
        && Uri.TryCreate(found.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var address) ? address : null;

    /// <summary>The reply's words with what its command prints (the open marks), so the agent need not fetch them.</summary>
    public static async Task<string> ReplyTextAsync(PageReply reply, string? workspace) =>
        reply.Attach is { Count: > 0 } attach && await RunAsync(attach, workspace) is { Length: > 0 } printed
            ? $"{reply.Text}\n\nLink ran it for you:\n{printed.Trim()[..Math.Min(printed.Trim().Length, AttachedChars)]}"
            : reply.Text;

    /// <summary>A skill's command, hidden: what it printed, else null (no command Link may run, it failed or took too long).</summary>
    private static async Task<string?> RunAsync(IReadOnlyList<string> command, string? workspace)
    {
        if (ScriptOf(command, workspace) is not { } script) return null;
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            // Read, or a full pipe would hold the command.
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var printed = await output;
            await errors;
            return process.ExitCode == 0 ? printed : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            // Only the command itself: a server it started runs apart from it.
            if (process is { HasExited: false }) process.Kill();
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Whether something still listens at the page's port: a review window's server stops a while after its tab closes.</summary>
    public static async Task<bool> IsUpAsync(Uri address)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(address.Host, address.Port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"```link-page[^\n]*\n(.*?)```[ \t]*(?:\r?\n)?", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"^(python3?|pythonw|py|uv)(\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Python();

    [GeneratedRegex(@"https?://(?:127\.0\.0\.1|localhost):\d{2,5}(?:/[^\s)""'`]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
