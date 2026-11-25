namespace Hex.Core;

/// <summary>What the Simple mode's status sentence says (docs/local-ai-window-plan.md, 3).</summary>
internal enum LocalAiStatusKind { NotSetUp, Downloading, Running, Starting, Stopped, Problem, Outside }

/// <summary>One row the Advanced pages can show: its help key and the page it lives on, for the search.</summary>
internal sealed record LocalAiRowDef(string Key, LocalAiPage Page);

/// <summary>
/// The pure parts of the Local AI window's second form (docs/local-ai-window-plan.md): which mode and page a name is, what the
/// status sentence is, the search over the explained rows, the pages Alt+1..9 reach and the key the window redraws on.
/// </summary>
internal static class LocalAiWindowState
{
    public const string SIMPLE = "simple", ADVANCED = "advanced";

    /// <summary>The mode a setting says: anything but "advanced" is simple.</summary>
    public static string ParseMode(string? value) => string.Equals(value?.Trim(), ADVANCED, StringComparison.OrdinalIgnoreCase) ? ADVANCED : SIMPLE;

    /// <summary>The other mode.</summary>
    public static string Toggle(string mode) => ParseMode(mode) == ADVANCED ? SIMPLE : ADVANCED;

    /// <summary>
    /// The mode a person starts in when they never chose one: simple, unless they changed advanced-only settings before
    /// (a llama.cpp setting off its default or edited presets) and have a model set up.
    /// </summary>
    public static string DefaultMode(bool setUp, bool changedAdvanced) => setUp && changedAdvanced ? ADVANCED : SIMPLE;

    /// <summary>Whether a window we hold is still open: a window that is hidden or has no parent was closed, whatever ran.</summary>
    public static bool IsOpen(bool hasWindow, bool visible, bool hasParent) => hasWindow && visible && hasParent;

    /// <summary>The page Alt+<paramref name="digit"/> (1 to 9) reaches, null for another digit.</summary>
    public static LocalAiPage? PageOfDigit(int digit) =>
        digit >= 1 && digit <= Enum.GetValues<LocalAiPage>().Length ? Enum.GetValues<LocalAiPage>()[digit - 1] : null;

    /// <summary>The page a name says, the legacy "advanced" being Predictions, an unknown name the overview.</summary>
    public static LocalAiPage ParsePage(string? name)
    {
        if (string.Equals(name?.Trim(), "advanced", StringComparison.OrdinalIgnoreCase))
            return LocalAiPage.Predictions;
        return LocalAiSetupFlow.ParsePage(name);
    }

    /// <summary>Whether a mode shows the left nav (Advanced) rather than the single page.</summary>
    public static bool HasNav(string mode) => ParseMode(mode) == ADVANCED;

    /// <summary>Whole percents of a download's progress (0 to 1), -1 when none goes; the redraw key moves a percent at a time.</summary>
    public static int PercentBucket(float? progress) =>
        progress is { } p ? (int)Math.Clamp(Math.Floor(p * 100.0f), 0, 100) : -1;

    /// <summary>The sentence the status says, from what is known.</summary>
    public static LocalAiStatusKind StatusOf(bool setUp, bool downloading, bool running, bool starting, bool problem, bool outside)
    {
        if (downloading)
            return LocalAiStatusKind.Downloading;
        if (!setUp)
            return LocalAiStatusKind.NotSetUp;
        if (problem)
            return LocalAiStatusKind.Problem;
        if (starting)
            return LocalAiStatusKind.Starting;
        if (running)
            return outside ? LocalAiStatusKind.Outside : LocalAiStatusKind.Running;
        return LocalAiStatusKind.Stopped;
    }

    /// <summary>The string key of a status sentence.</summary>
    public static string StatusKey(LocalAiStatusKind kind) => "local_ai.status_" + kind.ToString().ToLowerInvariant();

    /// <summary>Whether text matches what was typed into the search: every word typed is in it, any case.</summary>
    public static bool Matches(string query, params string?[] texts)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return true;
        string all = string.Join("\n", texts.Where(text => !string.IsNullOrEmpty(text)));
        return words.All(word => all.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Text cut to at most <paramref name="most"/> characters with a "…" at the cut, as it is when it already fits.</summary>
    public static string Ellipsize(string text, int most)
    {
        if (text.Length <= most)
            return text;
        return most <= 1 ? "…"[..Math.Max(0, most)] : text[..(most - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The name a router alias is shown as: the display name its /v1/models entry or preset gives, else the alias's own friendly
    /// form (a name from a library model the alias is the id of). Never the raw alias when something better is known.
    /// </summary>
    public static string DisplayNameOf(string alias, IReadOnlyDictionary<string, string> names)
    {
        if (names.TryGetValue(alias, out string? known) && known.Length > 0)
            return known;
        foreach ((string key, string value) in names)
        {
            if (string.Equals(key, alias, StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                return value;
        }
        return alias;
    }

    /// <summary>The rows whose title, short line or long text match, in the order they are given.</summary>
    public static IReadOnlyList<LocalAiRowDef> Filter(string query, IEnumerable<LocalAiRowDef> rows, Func<string, (string Title, string Short, string Long)> texts) =>
        [.. rows.Where(row =>
        {
            var (title, shortLine, longText) = texts(row.Key);
            return Matches(query, title, shortLine, longText);
        })];

    /// <summary>The page the search jumps to: the current one when it still has a match, else the first page that has.</summary>
    public static LocalAiPage? JumpTo(IReadOnlyList<LocalAiRowDef> matches, LocalAiPage current)
    {
        if (matches.Count == 0)
            return null;
        return matches.Any(row => row.Page == current) ? current : matches.OrderBy(row => row.Page).First().Page;
    }

    /// <summary>
    /// The key the window redraws on: the mode, page, setup id, search, open explanation and the download percent are part of it
    /// beside whatever else the window shows.
    /// </summary>
    public static string Signature(string mode, LocalAiPage page, string setupId, string query, string? openHelp, int percent, params object?[] more) =>
        LocalAiSetupFlow.Signature(ParseMode(mode), page, setupId, query, openHelp, percent, more);
}
