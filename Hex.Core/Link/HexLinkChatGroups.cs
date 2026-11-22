using System.Globalization;
using System.Text;

namespace Hex.Core;

/// <summary>
/// The two sections a linked device's chats are split into in the chat lists: Running (working, or waiting on the
/// person) and Finished (done, stopped, failed, or new and idle). A device's fleet doesn't say whether a chat settled
/// there, so a remote chat is in Finished as soon as it's no longer going.
/// </summary>
internal enum HexLinkChatSection { Running, Finished }

/// <summary>How many of a device's chats are in each section, and how many of them wait on the person.</summary>
internal readonly record struct HexLinkChatCounts(int Running, int Finished, int NeedsYou)
{
    public int All => Running + Finished;
}

/// <summary>
/// How a linked device's chats are grouped in the Agents view's list and Agentic's fleet: which section each is in,
/// the counts its group's head says, what waits on the person there, and whether a group or a section is open by
/// default. Kept apart from the drawing so the rules are tested on their own.
/// </summary>
internal static class HexLinkChatGroups
{
    /// <summary>More finished chats than this and the Finished section starts folded.</summary>
    public const int FINISHED_OPEN_MOST = 5;

    /// <summary>Whether a fleet row's state says it's working, a turn or a program still going.</summary>
    public static bool IsRunning(string state) =>
        state == "running" || state.StartsWith("running (", StringComparison.Ordinal);

    /// <summary>Whether a row waits on the person there: an approval or a question.</summary>
    public static bool Waiting(HexLinkRow row) => row.Waiting || row.Approvals.Count > 0;

    /// <summary>The section a row goes in.</summary>
    public static HexLinkChatSection SectionOf(HexLinkRow row) =>
        IsRunning(row.State) || Waiting(row) ? HexLinkChatSection.Running : HexLinkChatSection.Finished;

    /// <summary>
    /// The finished rows that can be closed together: in Finished, with nothing they started still running or waiting,
    /// since closing a chat there closes what it started with it. <see cref="ClosableTops"/> says which of them to ask
    /// for, a chat whose starter is closed too going with it.
    /// </summary>
    public static List<HexLinkRow> ClosableFinished(IReadOnlyList<HexLinkRow> rows)
    {
        var byId = new Dictionary<int, HexLinkRow>(rows.Count);
        foreach (HexLinkRow row in rows)
            byId.TryAdd(row.Id, row);

        // Every chat with something running or waiting under it, found by going up from each running one
        var busy = new HashSet<int>();
        foreach (HexLinkRow row in rows)
        {
            if (SectionOf(row) != HexLinkChatSection.Running)
                continue;
            int? at = row.Parent;
            for (int guard = 0; at is { } up && guard < 64 && busy.Add(up); guard++)
                at = byId.TryGetValue(up, out HexLinkRow? parent) ? parent.Parent : null;
        }

        var closable = new List<HexLinkRow>();
        foreach (HexLinkRow row in rows)
        {
            if (SectionOf(row) == HexLinkChatSection.Finished && !busy.Contains(row.Id))
                closable.Add(row);
        }

        return closable;
    }

    /// <summary>Of the closable rows, those whose starter isn't closed with them: closing these closes the rest.</summary>
    public static List<HexLinkRow> ClosableTops(IReadOnlyList<HexLinkRow> closable)
    {
        var ids = new HashSet<int>();
        foreach (HexLinkRow row in closable)
            ids.Add(row.Id);
        var tops = new List<HexLinkRow>();
        foreach (HexLinkRow row in closable)
        {
            if (row.Parent is not { } parent || parent == row.Id || !ids.Contains(parent))
                tops.Add(row);
        }

        return tops;
    }

    /// <summary>
    /// Whether a row needs the person: it waits on them, or it failed while this machine was watching and nobody has
    /// opened it since (<paramref name="failedUnseen"/> says which those are).
    /// </summary>
    public static bool NeedsYou(HexLinkRow row, Func<HexLinkRow, bool>? failedUnseen) =>
        Waiting(row) || (row.State == "failed" && failedUnseen?.Invoke(row) == true);

    /// <summary>The counts of a device's rows.</summary>
    public static HexLinkChatCounts Count(IReadOnlyList<HexLinkRow> rows, Func<HexLinkRow, bool>? failedUnseen = null)
    {
        int running = 0, finished = 0, needs = 0;
        foreach (HexLinkRow row in rows)
        {
            if (SectionOf(row) == HexLinkChatSection.Running)
                running++;
            else
                finished++;
            if (NeedsYou(row, failedUnseen))
                needs++;
        }

        return new HexLinkChatCounts(running, finished, needs);
    }

    /// <summary>The rows of one section, in the fleet's order.</summary>
    public static List<HexLinkRow> In(IReadOnlyList<HexLinkRow> rows, HexLinkChatSection section)
    {
        var shown = new List<HexLinkRow>();
        foreach (HexLinkRow row in rows)
        {
            if (SectionOf(row) == section)
                shown.Add(row);
        }

        return shown;
    }

    /// <summary>
    /// Whether a device's group is open when the person hasn't said: open while something runs or waits there, folded
    /// otherwise so a device of old chats takes one line.
    /// </summary>
    public static bool DeviceUsuallyOpen(HexLinkChatCounts counts) => counts.Running > 0 || counts.NeedsYou > 0;

    /// <summary>Whether a section is open when the person hasn't said: Running always, Finished while it has a few.</summary>
    public static bool SectionUsuallyOpen(HexLinkChatSection section, int count) =>
        section == HexLinkChatSection.Running || count <= FINISHED_OPEN_MOST;

    /// <summary>The key a device's group is folded under.</summary>
    public static string DeviceKey(string device) => device;

    /// <summary>The key a section of a device's group is folded under.</summary>
    public static string SectionKey(string device, HexLinkChatSection section) =>
        device + (section == HexLinkChatSection.Running ? "/running" : "/finished");

    /// <summary>What a device's group says it holds, "2 running · 5 finished", the parts with none left out.</summary>
    public static string CountsSaid(HexLinkChatCounts counts, string running, string finished, string none)
    {
        var said = new List<string>(2);
        if (counts.Running > 0)
            said.Add(running.Replace("{count}", counts.Running.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (counts.Finished > 0)
            said.Add(finished.Replace("{count}", counts.Finished.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        return said.Count == 0 ? none : string.Join(" · ", said);
    }
}

/// <summary>
/// The groups and sections of the chat lists the person folded or opened, by key (a device's id, or its id and a
/// section), kept in the settings as one line, "key=0;key=1". A key that isn't there goes by its default. At most
/// <see cref="MOST"/> are kept, the oldest choices let go of first.
/// </summary>
internal sealed class HexChatListFolds
{
    /// <summary>The most choices kept.</summary>
    public const int MOST = 200;

    // Each choice in the order it was last made, the newest last
    private readonly List<(string Key, bool Open)> chosen = [];

    public HexChatListFolds(string? kept = null)
    {
        if (string.IsNullOrWhiteSpace(kept))
            return;
        foreach (string part in kept.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.LastIndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
                continue;
            string key = part[..equals].Trim();
            string value = part[(equals + 1)..].Trim();
            if (value is "0" or "1")
                Set(key, value == "1");
        }
    }

    /// <summary>How many choices are kept.</summary>
    public int Count => chosen.Count;

    /// <summary>What the person chose for a key, null for nothing.</summary>
    public bool? Chosen(string key)
    {
        foreach (var (each, open) in chosen)
        {
            if (each == key)
                return open;
        }

        return null;
    }

    /// <summary>Whether a key is open: as the person chose, or <paramref name="usual"/>.</summary>
    public bool Open(string key, bool usual) => Chosen(key) ?? usual;

    /// <summary>A key opened or folded by the person. A key with ; or = in it is cleaned of them.</summary>
    public void Set(string key, bool open)
    {
        key = Clean(key);
        if (key.Length == 0)
            return;
        chosen.RemoveAll(each => each.Key == key);
        chosen.Add((key, open));
        if (chosen.Count > MOST)
            chosen.RemoveRange(0, chosen.Count - MOST);
    }

    /// <summary>A choice let go of, the key going by its default again.</summary>
    public void Forget(string key) => chosen.RemoveAll(each => each.Key == Clean(key));

    /// <summary>The line kept in the settings.</summary>
    public override string ToString()
    {
        var said = new StringBuilder();
        foreach (var (key, open) in chosen)
        {
            if (said.Length > 0)
                said.Append(';');
            said.Append(key).Append('=').Append(open ? '1' : '0');
        }

        return said.ToString();
    }

    private static string Clean(string key) => key.Replace(";", string.Empty, StringComparison.Ordinal).Replace("=", string.Empty, StringComparison.Ordinal).Trim();
}
