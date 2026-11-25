using System.Text;

namespace Hex.Core;

/// <summary>
/// What a device is called, made fit to show. A name is another machine's data as much as anything it sends: it's
/// shown in lists, badges and notices, so what can't be seen or would turn the text around it (control characters,
/// the marks that reverse a line's direction, zero-width ones) is taken out, the ends trimmed and it's cut to
/// <see cref="MOST"/> characters.
/// </summary>
internal static class HexLinkNames
{
    /// <summary>The longest a device's name is shown.</summary>
    public const int MOST = 40;

    /// <summary>The name as it's shown, null when nothing of it is left.</summary>
    public static string? Clean(string? said)
    {
        if (said is null)
            return null;
        var kept = new StringBuilder(Math.Min(said.Length, MOST * 2));
        foreach (char c in said)
        {
            if (char.IsControl(c) || Hidden(c))
                kept.Append(c is '\t' or '\n' or '\r' ? ' ' : '\0');
            else
                kept.Append(c);
        }

        string name = kept.Replace("\0", string.Empty).ToString().Trim();
        while (name.Contains("  ", StringComparison.Ordinal))
            name = name.Replace("  ", " ", StringComparison.Ordinal);
        if (name.Length > MOST)
        {
            // Not cut through the middle of a pair that makes one character
            int end = char.IsHighSurrogate(name[MOST - 1]) ? MOST - 1 : MOST;
            name = name[..end].TrimEnd();
        }

        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The name with a short id tail, <c>BOGZPC · 3F9A2C</c>, when another device in <paramref name="others"/> goes by the same
    /// name (two Hex instances on one machine both default to the machine's name). The ids may be only a prefix (Nearby
    /// shows eight characters). A device alone with its name keeps the plain name.
    /// </summary>
    public static string Distinct(string? name, string id, IEnumerable<(string? Name, string Id)> others)
    {
        string shown = Clean(name) ?? (id.Length > 8 ? id[..8] : id);
        if (id.Length == 0)
            return shown;
        foreach (var (other, otherId) in others)
        {
            if (otherId.Length == 0 || !string.Equals(Clean(other), shown, StringComparison.OrdinalIgnoreCase))
                continue;
            int common = Math.Min(id.Length, otherId.Length);
            if (!string.Equals(id[..common], otherId[..common], StringComparison.OrdinalIgnoreCase))
                return $"{shown} · {id[..Math.Min(6, id.Length)].ToUpperInvariant()}";
        }
        return shown;
    }

    // The format characters that change how text around them reads or can't be seen: the direction marks and
    // overrides, the zero-width ones and the byte order mark
    private static bool Hidden(char c) =>
        c is '​' or '‌' or '‍' or '‎' or '‏' or '⁠' or '﻿'
        || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
}
