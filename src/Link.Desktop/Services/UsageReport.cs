using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>What the agents spent at API prices in a period (a day, a week, a month), all together and by agent.</summary>
public sealed record UsagePeriod(string Period, double Cost, long Tokens, IReadOnlyDictionary<string, double> ByAgent);

/// <summary>Claude's 5-hour block under way: what it cost so far, the rate, and what it comes to at that rate by its end.</summary>
public sealed record UsageBlock(DateTimeOffset Ends, double Cost, double CostPerHour, double Projected);

/// <summary>
/// The agents' spending as ccusage reads it from their own logs on this computer: every Claude Code and Codex session,
/// Link's or not. Costs are at API prices: on a subscription they say how much work it was, not a bill.
/// </summary>
public sealed record UsageSummary(
    UsagePeriod? Today, UsagePeriod? Week, UsagePeriod? Month, UsageBlock? Block, IReadOnlyDictionary<string, double> Sessions, DateTimeOffset At)
{
    /// <summary>"Today $98.92 · month $625.84": the line under the usage bars.</summary>
    public string Line => $"Spent today {Money(Today?.Cost)} · month {Money(Month?.Cost)}";

    /// <summary>The rest on hover: the week, each agent this month, and the 5-hour block.</summary>
    public string Hint
    {
        get
        {
            var lines = new List<string> { $"At API prices, as ccusage reads the agents' logs (all sessions, Link's or not). This week {Money(Week?.Cost)}." };
            if (Month is { ByAgent.Count: > 0 } month)
            {
                lines.Add("This month: " + string.Join(", ", month.ByAgent.OrderByDescending(agent => agent.Value).Select(agent => $"{agent.Key} {Money(agent.Value)}")));
            }

            if (Block is { } block)
            {
                lines.Add($"Claude's 5-hour block: {Money(block.Cost)} so far, {Money(block.CostPerHour)}/h, ≈ {Money(block.Projected)} by {block.Ends.ToLocalTime():HH:mm}.");
            }

            lines.Add("On a subscription it is no bill: the bars are what runs out. Click to count again.");
            return string.Join('\n', lines);
        }
    }

    /// <summary>What the sessions with these ids cost; null when ccusage knows none of them.</summary>
    public double? CostOf(IEnumerable<string> sessionIds)
    {
        var costs = sessionIds.Distinct().Select(id => Sessions.TryGetValue(id, out var cost) ? cost : (double?)null).OfType<double>().ToArray();
        return costs.Length == 0 ? null : costs.Sum();
    }

    public static string Money(double? cost) => cost is { } value ? value.ToString("$0.00", CultureInfo.InvariantCulture) : "—";
}

/// <summary>
/// Runs ccusage (<c>npx ccusage</c>, a pinned version, offline prices) for Link's usage line and each task's cost. It needs
/// Node, which Claude Code's npm install brings; without it there is no line. Two reports at once, a few seconds, off
/// the window's thread; asked again only after <see cref="Fresh"/> unless the user clicks.
/// </summary>
public sealed class UsageReport(Action<string> log)
{
    /// <summary>The ccusage release Link reads: a newer one is taken by changing it here, after checking its JSON.</summary>
    public const string Package = "ccusage@20.0.34";

    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);
    private Task<UsageSummary?>? _reading;
    private int _toldNoNode;

    public UsageSummary? Last { get; private set; }

    /// <summary>The summary, read again when it is older than <see cref="Fresh"/> or <paramref name="now"/> asks; one read at a time.</summary>
    public Task<UsageSummary?> ReadAsync(bool now = false)
    {
        if (_reading is { IsCompleted: false } reading) return reading;
        if (!now && Last is { } last && DateTimeOffset.Now - last.At < Fresh) return Task.FromResult<UsageSummary?>(last);
        return _reading = ReadNewAsync();
    }

    private async Task<UsageSummary?> ReadNewAsync()
    {
        var today = DateTime.Today;
        // From the month's first day or the week's, whichever is earlier: one load gives today, the week, the month and the sessions.
        var since = new[] { new DateTime(today.Year, today.Month, 1), today.AddDays(-6) }.Min().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var periods = RunAsync("daily", "--json", "--offline", "--since", since, "--sections", "daily,weekly,monthly,session", "--by-agent");
        var blocks = RunAsync("blocks", "--active", "--json", "--offline");
        var (report, active) = (await periods, await blocks);
        if (report is null) return Last;
        try
        {
            Last = Parse(report, active, DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            log($"[Usage] ccusage's report did not read: {exception.Message}");
        }

        return Last;
    }

    /// <summary>The summary of ccusage's JSON: the sections report, and the active block's (null without one).</summary>
    public static UsageSummary Parse(string report, string? active, DateTimeOffset at)
    {
        using var json = JsonDocument.Parse(report);
        var root = json.RootElement;
        UsagePeriod? Latest(string section) =>
            root.TryGetProperty(section, out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? Period(rows[rows.GetArrayLength() - 1]) : null;

        var today = Latest("daily") is { } day && day.Period == at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ? day : null;
        var sessions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("session", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var session in list.EnumerateArray())
            {
                // Claude's period is its session id; Codex's is its rollout file, which ends with the thread id.
                var period = session.GetProperty("period").GetString() ?? string.Empty;
                var id = period.Length > 36 ? period[^36..] : period;
                sessions[id] = sessions.GetValueOrDefault(id) + session.GetProperty("totalCost").GetDouble();
            }
        }

        return new UsageSummary(today, Latest("weekly"), Latest("monthly"), active is null ? null : Block(active), sessions, at);
    }

    private static UsagePeriod Period(JsonElement row) => new(
        row.GetProperty("period").GetString() ?? string.Empty,
        row.GetProperty("totalCost").GetDouble(),
        row.GetProperty("totalTokens").GetInt64(),
        row.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array
            ? agents.EnumerateArray().ToDictionary(agent => agent.GetProperty("agent").GetString() ?? "?", agent => agent.GetProperty("totalCost").GetDouble())
            : new Dictionary<string, double>());

    private static UsageBlock? Block(string active)
    {
        using var json = JsonDocument.Parse(active);
        if (!json.RootElement.TryGetProperty("blocks", out var blocks) ||
            blocks.EnumerateArray().FirstOrDefault(block => block.TryGetProperty("isActive", out var on) && on.GetBoolean()) is not { ValueKind: JsonValueKind.Object } block)
        {
            return null;
        }

        var cost = block.GetProperty("costUSD").GetDouble();
        return new UsageBlock(
            block.GetProperty("endTime").GetDateTimeOffset(),
            cost,
            block.TryGetProperty("burnRate", out var rate) && rate.ValueKind == JsonValueKind.Object ? rate.GetProperty("costPerHour").GetDouble() : 0,
            block.TryGetProperty("projection", out var projection) && projection.ValueKind == JsonValueKind.Object ? projection.GetProperty("totalCost").GetDouble() : cost);
    }

    /// <summary>ccusage's output for a report, or null when it cannot run (no Node, no network for its first download) or fails.</summary>
    private async Task<string?> RunAsync(params string[] arguments)
    {
        // By its full path: started by name, npx.cmd looks for npm beside the folder Link runs in (System32 when
        // Windows starts it) and fails every time.
        if (AgentExecutables.SearchPath("npx.cmd") is not { } npx)
        {
            // Once a run of Link: it is asked after every task, two reports at once, and Node does not appear in between.
            if (Interlocked.Exchange(ref _toldNoNode, 1) == 0) log("[Usage] ccusage could not run: npx is not on PATH (is Node installed?)");
            return null;
        }

        var start = new ProcessStartInfo(npx)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[] { "-y", Package }.Concat(arguments)) start.ArgumentList.Add(argument);
        start.Environment["NO_COLOR"] = "1";
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("npx did not start");
            using var timeout = new CancellationTokenSource(Wait);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0) return await output;
            log($"[Usage] ccusage {arguments[0]} exited with {process.ExitCode}: {Shorten(await errors)}");
            return null;
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            log($"[Usage] ccusage {arguments[0]} took over {Wait.TotalSeconds:0} s");
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log($"[Usage] ccusage could not run (is Node installed?): {exception.Message}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string Shorten(string text) => text.Trim() is var trimmed && trimmed.Length > 300 ? trimmed[..300] + "…" : text.Trim();
}
