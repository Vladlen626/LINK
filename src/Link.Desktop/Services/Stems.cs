using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>Word stems found in a brief, English or Russian, as Auto effort and the specialists read it.</summary>
public static class Stems
{
    /// <summary>
    /// Whether <paramref name="text"/> has a word that begins with <paramref name="stem"/>, ignoring case: "анимац" finds
    /// "анимацию", while "format" is not in "information" nor "race" in "trace".
    /// </summary>
    public static bool Found(string text, string stem) =>
        stem.Length > 0 && Regex.IsMatch(text, @"(?<!\p{L})" + Regex.Escape(stem), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
