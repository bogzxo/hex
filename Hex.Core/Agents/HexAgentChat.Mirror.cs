namespace Hex.Core;

/// <summary>
/// A chat that's a copy of one on another machine (HexLinkPeer), kept as it is there by patches rather than by an agent
/// of its own. Nothing here runs for a chat of this machine: what a local chat does goes through Ask, Say and the rest.
/// Still the editor's thread only, as every chat is.
/// </summary>
internal sealed partial class HexAgentChat
{
    /// <summary>
    /// The rows patched to how they are on the other machine: <paramref name="dropped"/> is how many it has let go of off
    /// the top (the rows here past that taken off), <paramref name="count"/> how many it keeps, and each of
    /// <paramref name="rows"/> a row that's new or changed at its index among those. False, changing nothing, when the
    /// patch doesn't fit what's here (a row it skips over), for the whole chat to be asked for again. With
    /// <paramref name="whole"/> every row is in it and what's here is replaced.
    /// </summary>
    public bool MirrorRows(int dropped, int count, IReadOnlyList<(int Index, HexAgentEntry Entry)> rows, bool whole = false)
    {
        if (dropped < 0 || count < 0)
            return false;

        int off = whole ? entries.Count : dropped - Dropped;
        int kept = whole ? 0 : Math.Max(0, entries.Count - Math.Max(0, off));
        if (off < 0 && entries.Count > 0)
            return false;

        // Every row past what's kept here has to come in the patch, in order, or there'd be a gap
        int next = Math.Min(kept, count);
        foreach (var (index, _) in rows.OrderBy(row => row.Index))
        {
            if (index < 0 || index >= count)
                return false;
            if (index > next)
                return false;
            if (index == next)
                next++;
        }

        if (next < count)
            return false;

        if (off > 0)
            entries.RemoveRange(0, Math.Min(off, entries.Count));
        if (entries.Count > count)
            entries.RemoveRange(count, entries.Count - count);
        foreach (var (index, entry) in rows.OrderBy(row => row.Index))
        {
            if (index < entries.Count)
                entries[index] = entry;
            else
                entries.Add(entry);
        }

        Dropped = dropped;
        IndexTurnTools();
        // Rows replaced in the middle and off the top, the panel draws the chat again from the start
        if (off != 0 || whole)
            Reshaped++;
        Version++;
        return true;
    }

    /// <summary>What a mirrored chat says of itself that only its own agent sets here: its title, whether it's running and how long its turn has taken.</summary>
    public void MirrorHead(string title, bool running, double turnSeconds)
    {
        if (Title == title && Running == running && TurnSeconds == turnSeconds)
            return;
        Title = title;
        Running = running;
        TurnSeconds = turnSeconds;
        Version++;
    }
}
