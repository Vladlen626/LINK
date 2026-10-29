using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Follows what a run's context is made of, request by request, so that its tokens can be split by what they went to
/// (<see cref="SpendParts"/>). Each request reads the whole context: what the cache kept at a tenth of the price, what
/// came in since the request before at full price. Where the provider tells each request's size (Claude Code) the
/// context's growth is what came in, split between the parts by the size of their text; where it does not (Codex) the
/// text's size stands in.
/// </summary>
public sealed class SpendMeter
{
    /// <summary>Characters per token of code and prose, near enough to split what came in between its parts.</summary>
    public const double CharsPerToken = 3.5;

    /// <summary>Points of the context curve a run keeps.</summary>
    public const int CurvePoints = 120;

    private static readonly int Parts = SpendParts.All.Length;
    private readonly double[] _held = new double[Parts];
    private readonly double[] _came = new double[Parts];
    private readonly double[] _cost = new double[Parts];
    private readonly List<long> _sizes = [];
    private bool _resumed;
    private bool _compacted;
    private double _base;

    /// <summary>Input the cache had lost and the model read again at full price, such as after a long pause.</summary>
    public long ColdTokens { get; private set; }

    public int Requests => _sizes.Count;

    /// <summary>The context of the first request, as told or as its text's size made it; zero before it.</summary>
    public long First => _sizes.Count > 0 ? _sizes[0] : 0;

    /// <summary>The provider told the requests' sizes: the curve and the cold input are measured, not estimated.</summary>
    public bool Measured { get; private set; }

    /// <summary>The run begins: Link's brief of <paramref name="characters"/>, in a session that goes on or a new one.</summary>
    public void Start(int characters, bool resumed)
    {
        _came[(int)SpendPart.Brief] += characters / CharsPerToken;
        _resumed = resumed;
    }

    /// <summary>Text that joins the context before the next request.</summary>
    public void Text(SpendPart part, int characters) => _came[(int)part] += characters / CharsPerToken;

    /// <summary>Tokens that join the context before the next request, such as a picture's.</summary>
    public void Tokens(SpendPart part, double tokens) => _came[(int)part] += tokens;

    /// <summary>The CLI compacted the session: its summary, the model's writing, takes the place of all but its own prompt.</summary>
    public void Compacted() => _compacted = true;

    /// <summary>
    /// The model made a request: of a context of <paramref name="context"/> tokens of which the cache kept
    /// <paramref name="cached"/>, where the provider tells them.
    /// </summary>
    public void Request(long? context = null, long? cached = null)
    {
        if (_compacted && context is { } after)
        {
            Array.Clear(_held);
            _held[(int)SpendPart.Base] = Math.Min(_base, after);
            _came[(int)SpendPart.Writing] += Math.Max(0, after - _held[(int)SpendPart.Base] - _came.Sum());
            _compacted = false;
        }

        Measured |= context is not null;
        var before = (double[])_held.Clone();
        var held = before.Sum();
        var came = _came.Sum();
        var added = (double[])_came.Clone();
        if (context is { } size)
        {
            var growth = size - held;
            if (growth < 0)
            {
                // Something left the context with no compaction told: what is held shrinks to the size told.
                for (var part = 0; part < Parts; part++) before[part] *= held > 0 ? size / held : 0;
                Array.Clear(added);
            }
            else if (came > 0 && growth <= came * 1.5)
            {
                for (var part = 0; part < Parts; part++) added[part] *= growth / came;
            }
            else
            {
                // Far more came than the text told: the CLI's own prompt on a first request, a session's earlier turns.
                added[(int)SpendPart.Base] += growth - came;
            }
        }

        var now = new double[Parts];
        for (var part = 0; part < Parts; part++) now[part] = before[part] + added[part];
        var total = now.Sum();
        if (_sizes.Count == 0) _base = now[(int)SpendPart.Base];

        // The cache keeps what the request before read; what is cached beyond it (the CLI's prompt, cached across
        // sessions, or a session that goes on) is the new part's.
        var heldBefore = before.Sum();
        var cachedTotal = Math.Min(cached ?? heldBefore, total);
        var fromBefore = Math.Min(cachedTotal, heldBefore);
        var addedTotal = added.Sum();
        var beforeShare = heldBefore > 0 ? fromBefore / heldBefore : 0;
        var addedShare = addedTotal > 0 ? (cachedTotal - fromBefore) / addedTotal : 0;
        for (var part = 0; part < Parts; part++)
        {
            var fromCache = before[part] * beforeShare + added[part] * addedShare;
            _cost[part] += now[part] - fromCache + 0.1 * fromCache;
        }

        if (cached is not null)
        {
            // The first request of a session that goes on finds its earlier turns in the cache, unless it went cold.
            var expected = _resumed && _sizes.Count == 0 ? added[(int)SpendPart.Base] : heldBefore;
            var missed = expected - (_resumed && _sizes.Count == 0 ? cachedTotal : fromBefore);
            // A few hundred tokens off is the CLI's own notes changing, not a cold cache.
            if (missed > Math.Max(1000, expected * 0.05)) ColdTokens += (long)missed;
        }

        Array.Copy(now, _held, Parts);
        Array.Clear(_came);
        _sizes.Add((long)Math.Round(total));
    }

    /// <summary>
    /// The CLI's own prompt where the provider tells no request's size: the first request's context less what the text
    /// told, read fresh once and from the cache on every request after.
    /// </summary>
    public void AddBase(long tokens)
    {
        if (tokens <= 0 || _sizes.Count == 0) return;
        _cost[(int)SpendPart.Base] += tokens * (1 + 0.1 * (_sizes.Count - 1));
    }

    /// <summary>The input each part cost so far, in ≈ tokens: what the run's real input is split by.</summary>
    public SpendParts Input => SpendParts.From(part => _cost[(int)part]);

    /// <summary>The context of each request so far, in thousands of tokens, at most <see cref="CurvePoints"/> of them.</summary>
    public IReadOnlyList<int> Curve => Thin(Thousands(_sizes));

    /// <summary>Token counts in thousands, as a curve keeps them.</summary>
    public static int[] Thousands(IEnumerable<long> tokens) => [.. tokens.Select(count => (int)Math.Round(count / 1000.0))];

    /// <summary>A curve of at most <see cref="CurvePoints"/> points: evenly picked, the first and the last kept.</summary>
    public static IReadOnlyList<int> Thin(IReadOnlyList<int> curve) => curve.Count <= CurvePoints
        ? curve
        : [.. Enumerable.Range(0, CurvePoints).Select(point => curve[(int)Math.Round(point * (curve.Count - 1) / (double)(CurvePoints - 1))])];

    /// <summary>
    /// A run's spend: its real input, fresh and cached, split as <paramref name="input"/> (a meter's) splits it, and its
    /// output, all writing, at five times a fresh token. A run with tokens and no split puts its input in the base.
    /// </summary>
    public static SpendParts Split(SpendParts? input, TokenCount tokens)
    {
        var real = tokens.Uncached + 0.1 * tokens.Cached;
        var told = input?.Total ?? 0;
        return SpendParts.From(part =>
        {
            var share = told > 0 ? real * input![part] / told : part == SpendPart.Base ? real : 0;
            return part == SpendPart.Writing ? share + 5.0 * tokens.Output : share;
        });
    }

    /// <summary>The tokens a picture costs a Claude model: its pixels over 750, after a scale to a long side of 1568 and 1.15 megapixels.</summary>
    public static double ImageTokens(int width, int height)
    {
        if (width <= 0 || height <= 0) return 1_600;
        var scale = Math.Min(1, Math.Min(1568.0 / Math.Max(width, height), Math.Sqrt(1_150_000.0 / ((double)width * height))));
        return width * scale * height * scale / 750;
    }

    /// <summary>
    /// The size of a PNG or JPEG from the start of its base64 text; zero when it is neither or the start does not tell.
    /// </summary>
    public static (int Width, int Height) ImageSize(string base64)
    {
        try
        {
            var head = base64.Length > 87_380 ? base64[..87_380] : base64;
            var bytes = Convert.FromBase64String(head[..(head.Length / 4 * 4)]);
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            {
                return ((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19], (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
            }

            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return (0, 0);
            // A JPEG's frame header (SOF0 to SOF3) tells height, then width; the segments before it are skipped by their length.
            for (var at = 2; at + 9 < bytes.Length;)
            {
                if (bytes[at] != 0xFF) return (0, 0);
                var marker = bytes[at + 1];
                var length = (bytes[at + 2] << 8) | bytes[at + 3];
                if (marker is >= 0xC0 and <= 0xC3) return ((bytes[at + 7] << 8) | bytes[at + 8], (bytes[at + 5] << 8) | bytes[at + 6]);
                at += 2 + length;
            }
        }
        catch (FormatException)
        {
            // Not base64 after all.
        }

        return (0, 0);
    }
}
