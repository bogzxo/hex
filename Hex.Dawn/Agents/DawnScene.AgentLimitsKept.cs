using System.Globalization;

namespace Hex.Dawn;

// Each agent system's limits as last told, kept in a file beside Hex's settings, so its block shows where it stands
// against its plan from the start, before any chat of this run has said, and after its chats are closed. A line a limit,
// the system, the kind, how much is used, when it resets and its state, between tabs.
internal sealed partial class DawnScene
{
    private Dictionary<string, Dictionary<string, HexAgentLimit>>? limitsKept;
    private string limitsKeptSaid = string.Empty;

    private string LimitsFile => Path.Combine(TerminalFiles.UserFolder, "agent_limits.tsv");

    /// <summary>
    /// A system's limits, what its chats say now over what was kept, session first then the week. What's new is kept in
    /// the file, written only when it changed.
    /// </summary>
    private HexAgentLimit[] KnownLimits(string system, IEnumerable<HexAgentChat> chats)
    {
        limitsKept ??= ReadLimits();
        if (!limitsKept.TryGetValue(system, out var kept))
            limitsKept[system] = kept = new Dictionary<string, HexAgentLimit>(StringComparer.Ordinal);
        foreach (HexAgentLimit limit in HexAgentLimits.Across(chats))
            kept[limit.Kind] = limit;

        // A limit that has reset since it was said is all clear again, its use unknown until it's next said
        DateTimeOffset now = DateTimeOffset.Now;
        HexAgentLimit[] known = [.. kept.Values.Select(limit => limit.Resets is { } resets && resets < now ? limit with { Used = 0, State = HexAgentLimitState.Ok, Resets = null } : limit)
            .OrderBy(limit => HexAgentLimits.Order(limit.Kind)).ThenBy(limit => limit.Kind, StringComparer.Ordinal)];
        SaveLimits();
        return known;
    }

    private Dictionary<string, Dictionary<string, HexAgentLimit>> ReadLimits()
    {
        var read = new Dictionary<string, Dictionary<string, HexAgentLimit>>(StringComparer.Ordinal);
        if (options.SelfTest)
            return read;
        try
        {
            if (!File.Exists(LimitsFile))
                return read;
            foreach (string line in File.ReadAllLines(LimitsFile))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 5 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double used)
                    || !Enum.TryParse(parts[4], out HexAgentLimitState state))
                    continue;
                DateTimeOffset? resets = long.TryParse(parts[3], out long seconds) && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                if (!read.TryGetValue(parts[0], out var kinds))
                    read[parts[0]] = kinds = new Dictionary<string, HexAgentLimit>(StringComparer.Ordinal);
                kinds[parts[1]] = new HexAgentLimit(parts[1], state, used, resets, null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        limitsKeptSaid = Said(read);
        return read;
    }

    private static string Said(Dictionary<string, Dictionary<string, HexAgentLimit>> limits) =>
        string.Join('\n', limits.SelectMany(system => system.Value.Values.Select(limit =>
            $"{system.Key}\t{limit.Kind}\t{limit.Used.ToString(CultureInfo.InvariantCulture)}\t{limit.Resets?.ToUnixTimeSeconds() ?? 0}\t{limit.State}")));

    private void SaveLimits()
    {
        if (limitsKept is null || options.SelfTest)
            return;
        string said = Said(limitsKept);
        if (said == limitsKeptSaid)
            return;
        limitsKeptSaid = said;
        try
        {
            File.WriteAllText(LimitsFile, said);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
