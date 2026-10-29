using System.Text.Json.Serialization;

namespace Link.Desktop.Models;

/// <summary>What a share of a run's tokens went to; the order is the order the Spend view stacks them in.</summary>
public enum SpendPart
{
    /// <summary>Link's brief: its rules, the role, the skills, the map, the task's state and the user's words.</summary>
    Brief,

    /// <summary>The model's own writing: its output, and the reading of it again on later requests.</summary>
    Writing,

    /// <summary>What reading files brought back.</summary>
    Files,

    /// <summary>What commands, builds, tests, MCP calls and other tools brought back.</summary>
    Commands,

    /// <summary>What searching (Grep, Glob, a search command, the web) brought back.</summary>
    Search,

    /// <summary>The pictures the agent looked at: renders, screenshots, viewport shots.</summary>
    Images,

    /// <summary>The CLI's own prompt and tools, MCP tool lists, and the earlier turns of a session that goes on.</summary>
    Base
}

/// <summary>
/// Where a run's tokens went, in ≈ tokens (<see cref="Services.TokenCount.Equivalent"/>'s measure, so the parts add up
/// to it). An estimate: the cache's share and the context's growth are what the provider told, the split of each
/// request's new tokens between the parts follows the size of their text.
/// </summary>
public sealed record SpendParts
{
    public static readonly SpendPart[] All = Enum.GetValues<SpendPart>();

    public long Brief { get; init; }
    public long Writing { get; init; }
    public long Files { get; init; }
    public long Commands { get; init; }
    public long Search { get; init; }
    public long Images { get; init; }
    public long Base { get; init; }

    [JsonIgnore]
    public long Total => All.Sum(part => this[part]);

    public long this[SpendPart part] => part switch
    {
        SpendPart.Brief => Brief,
        SpendPart.Writing => Writing,
        SpendPart.Files => Files,
        SpendPart.Commands => Commands,
        SpendPart.Search => Search,
        SpendPart.Images => Images,
        _ => Base
    };

    public static SpendParts From(Func<SpendPart, double> value)
    {
        long Of(SpendPart part) => (long)Math.Round(Math.Max(0, value(part)));
        return new SpendParts
        {
            Brief = Of(SpendPart.Brief),
            Writing = Of(SpendPart.Writing),
            Files = Of(SpendPart.Files),
            Commands = Of(SpendPart.Commands),
            Search = Of(SpendPart.Search),
            Images = Of(SpendPart.Images),
            Base = Of(SpendPart.Base)
        };
    }

    public static SpendParts operator +(SpendParts first, SpendParts second) => From(part => first[part] + second[part]);
}

/// <summary>A file a run read more than once: its path as the agent named it, the times, and the ≈ tokens of all its reads.</summary>
public sealed record FileReads(string Path, int Times, long Tokens)
{
    /// <summary>The files read again of two lists, each once, the most costly first, at most <paramref name="kept"/>.</summary>
    public static List<FileReads> Join(IEnumerable<FileReads> reads, int kept = 5) =>
    [
        .. reads.GroupBy(read => read.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FileReads(group.First().Path, group.Sum(read => read.Times), group.Sum(read => read.Tokens)))
            .OrderByDescending(read => read.Tokens)
            .Take(kept)
    ];
}
