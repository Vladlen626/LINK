using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>Word stems found in a brief, English or Russian, as Auto effort and the specialists read it.</summary>
public static class Stems
{
    /// <summary>
    /// Whether <paramref name="text"/> has a word that begins with <paramref name="stem"/>, ignoring case: "анимац" finds
    /// "анимацию", while "format" is not in "information" nor "race" in "trace". A Russian indefinite word is none of
    /// them: "почему-то" asks nothing.
    /// </summary>
    public static bool Found(string text, string stem) => IndexOf(text, stem) >= 0;

    /// <summary>Where the first word that begins with <paramref name="stem"/> starts in <paramref name="text"/>; -1 when there is none.</summary>
    public static int IndexOf(string text, string stem)
    {
        if (stem.Length == 0) return -1;
        var match = Regex.Match(text, @"(?<!\p{L})" + Regex.Escape(stem) + @"(?!\p{L}*-(?:то|нибудь|либо)(?!\p{L}))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
    }
}
