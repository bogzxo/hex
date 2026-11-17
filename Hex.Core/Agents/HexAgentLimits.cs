using System.Globalization;

namespace Hex.Core;

/// <summary>How close an agent is to a limit, all clear, close to it (the system warned), or over it and refused.</summary>
internal enum HexAgentLimitState { Ok, Warning, Hit }

/// <summary>
/// Where an agent stands against one of its system's limits, as its extension said it. <paramref name="Kind"/> is the
/// limit in the system's own words (five_hour, seven_day, usage, rate), <paramref name="Used"/> how much of it is gone
/// as a share from 0 to 1 (negative when it wasn't said), <paramref name="Resets"/> when it starts again, and
/// <paramref name="Message"/> what the system said about it, if anything.
/// </summary>
internal sealed record HexAgentLimit(string Kind, HexAgentLimitState State, double Used, DateTimeOffset? Resets, string? Message)
{
    /// <summary>The state from the words the systems use for it, allowed, allowed_warning, warning, rejected, hit.</summary>
    public static HexAgentLimitState StateOf(string? said) => said?.ToLowerInvariant() switch
    {
        "rejected" or "hit" or "exceeded" or "blocked" => HexAgentLimitState.Hit,
        "allowed_warning" or "warning" or "near" => HexAgentLimitState.Warning,
        _ => HexAgentLimitState.Ok
    };
}

/// <summary>
/// The words for limits, compactions and retries, the same in a chat's quiet rows, its details and the systems panel,
/// so the person watching reads one way of saying each.
/// </summary>
internal static class HexAgentLimits
{
    /// <summary>A limit's kind said for people, the five hour window, the weekly one, or the system's own word.</summary>
    public static string KindName(string kind) => kind switch
    {
        "five_hour" => "session limit",
        "seven_day" => "weekly limit",
        "seven_day_opus" => "weekly Opus limit",
        "seven_day_sonnet" => "weekly Sonnet limit",
        "overage" => "extra usage",
        "usage" => "usage limit",
        "rate" => "rate limit",
        "" => "limit",
        _ => kind.Replace('_', ' ') + " limit"
    };

    /// <summary>Where a kind of limit comes in a list, the session first, then the week, then the rest.</summary>
    public static int Order(string kind) => kind switch
    {
        "five_hour" => 0,
        "seven_day" => 1,
        "seven_day_opus" => 2,
        "seven_day_sonnet" => 3,
        _ => 9
    };

    /// <summary>
    /// The latest of each kind of limit any of some chats was told, the one most used when several were, since a plan's
    /// limits are the account's and every chat on it sees the same. Session first, then the week.
    /// </summary>
    public static HexAgentLimit[] Across(IEnumerable<HexAgentChat> chats) =>
        [.. chats.SelectMany(chat => chat.Limits.Values).GroupBy(limit => limit.Kind)
            .Select(kind => kind.OrderByDescending(limit => limit.State).ThenByDescending(limit => limit.Used).First())
            .OrderBy(limit => Order(limit.Kind)).ThenBy(limit => limit.Kind, StringComparer.Ordinal)];

    /// <summary>When a limit resets said against now, in 40m, in 3h 10m, or the day and time when it's further off.</summary>
    public static string ResetsIn(DateTimeOffset resets, DateTimeOffset now)
    {
        TimeSpan left = resets - now;
        if (left <= TimeSpan.Zero)
            return "now";
        if (left.TotalMinutes < 60)
            return $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        if (left.TotalHours < 24)
            return $"in {(int)left.TotalHours}h {left.Minutes}m";
        return "on " + resets.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A limit in a line, its kind, how much is gone and when it resets, what the system said when it's over.</summary>
    public static string LimitLine(HexAgentLimit limit, DateTimeOffset now)
    {
        var parts = new List<string> { KindName(limit.Kind) };
        if (limit.Used >= 0)
            parts.Add($"{Math.Round(limit.Used * 100):0}% used");
        if (limit.Resets is { } resets)
            parts.Add("resets " + ResetsIn(resets, now));
        return string.Join(" · ", parts);
    }

    /// <summary>The quiet row for a limit coming close, hit, or clear again.</summary>
    public static string LimitNote(HexAgentLimit limit, DateTimeOffset now)
    {
        string line = LimitLine(limit, now);
        string said = limit.State switch
        {
            HexAgentLimitState.Hit => "Limit reached, " + line,
            HexAgentLimitState.Warning => "Close to the limit, " + line,
            _ => "Under the limit again, " + line
        };
        return limit.Message is { Length: > 0 } message && limit.State == HexAgentLimitState.Hit ? said + "\n" + message : said;
    }

    /// <summary>The quiet row for a compaction, by itself or asked for, and the tokens it went from and to when said.</summary>
    public static string CompactionNote(string trigger, long before, long after)
    {
        string how = trigger == "manual" ? "Context compacted on request" : "Context compacted automatically";
        if (before > 0 && after > 0)
            return $"{how}, {Tokens(before)} to {Tokens(after)} tokens";
        if (before > 0)
            return $"{how}, from {Tokens(before)} tokens";
        return how;
    }

    /// <summary>The quiet row for a retry, which try of how many, why and how long it waits first.</summary>
    public static string RetryNote(int attempt, int most, double delaySeconds, string why)
    {
        string said = most > 0 ? $"Retrying, attempt {attempt} of {most}" : $"Retrying, attempt {attempt}";
        if (why.Length > 0)
            said += ", " + why;
        if (delaySeconds >= 1)
            said += $", next in {delaySeconds:0}s";
        return said;
    }

    /// <summary>Tokens said short, 1.2k, 180k, 1.1M.</summary>
    public static string Tokens(long tokens) =>
        tokens >= 1_000_000 ? (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : tokens >= 10_000 ? (tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : tokens >= 1000 ? (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);
}
