namespace Hex.Core;

/// <summary>
/// Where a chat stands in the lists of chats, top to bottom: waiting on the person, working, finished a moment ago (or
/// new and not asked anything yet), and finished, which folds away under a group of its own.
/// </summary>
internal enum HexChatBand { NeedsYou, Working, Recent, Finished }

/// <summary>
/// What the order of a chat goes by, taken off it once so the order is worked out with times the caller says (tests and
/// the lists alike). <paramref name="TurnStarted"/> and <paramref name="Ended"/> are Environment.TickCount64's
/// milliseconds, 0 for none.
/// </summary>
internal readonly record struct HexChatFacts(int Id, int? Parent, bool Running, bool NeedsYou, bool Asked, bool Unread, long TurnStarted, long Ended)
{
    /// <summary>The facts of a chat as it is now, what waits on the person being said by the caller (it reads the chat's mail and rows).</summary>
    public static HexChatFacts Of(HexAgentChat chat, bool needsYou) =>
        new(chat.Id, chat.Parent, chat.Running, needsYou, chat.Entries.Count > 0, chat.Unread, chat.TurnStarted, chat.Ended);
}

/// <summary>A chat's place in the lists, its index in the chats it was worked out from, how far it's stepped in under the one that started it and its family's band.</summary>
internal readonly record struct HexChatPlace(int Index, int Step, HexChatBand Band);

/// <summary>
/// The order the Agents view's list and Agentic's fleet show the chats in. Families stay together (a chat nobody started,
/// then the ones it started under it), each family in the band of its most pressing chat: those waiting on the person
/// first, then those working, then those finished a moment ago, then the finished, most recently active first in each.
/// A finished chat stays with the working ones for a few minutes after it ended (longer while nobody read it) so the
/// person sees it end, then folds into the finished group, a subagent too, let go of its family still going.
/// </summary>
internal static class HexAgentListOrder
{
    /// <summary>How long a finished chat stays in the main list after it ended, and how long while nobody has read it.</summary>
    public const long RECENT_MS = 3 * 60_000, UNREAD_RECENT_MS = 15 * 60_000;

    /// <summary>A chat's own band at <paramref name="now"/>, its family's being the most pressing of its members'.</summary>
    public static HexChatBand BandOf(in HexChatFacts chat, long now) =>
        chat.NeedsYou ? HexChatBand.NeedsYou
        : chat.Running ? HexChatBand.Working
        : !chat.Asked ? HexChatBand.Recent
        : chat.Ended > 0 && now - chat.Ended < (chat.Unread ? UNREAD_RECENT_MS : RECENT_MS) ? HexChatBand.Recent
        : HexChatBand.Finished;

    /// <summary>
    /// When a chat was last active for its order within its band: its turn's start while it works, when it ended once it
    /// has, a new chat nobody asked anything yet the newest of all.
    /// </summary>
    public static long ActiveAt(in HexChatFacts chat) =>
        !chat.Asked ? long.MaxValue : chat.Running ? chat.TurnStarted : chat.Ended > 0 ? chat.Ended : chat.TurnStarted;

    /// <summary>
    /// The chats in the order they're shown into <paramref name="into"/>, emptied first. With <paramref name="liftOpen"/>
    /// the family of the chat open (<paramref name="open"/>, its id) is never in the finished band, the finished group
    /// being folded and the chat on screen never hidden.
    /// </summary>
    public static void Order(IReadOnlyList<HexChatFacts> chats, long now, int open, bool liftOpen, List<HexChatPlace> into)
    {
        into.Clear();
        int count = chats.Count;
        if (count == 0)
            return;

        var index = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++)
            index.TryAdd(chats[i].Id, i);

        // Who started whom, by index: the first child of each and the next of its siblings, in the order they came
        var first = new int[count];
        var next = new int[count];
        var parentOf = new int[count];
        Array.Fill(first, -1);
        Array.Fill(next, -1);
        Array.Fill(parentOf, -1);
        var last = new int[count];
        Array.Fill(last, -1);
        for (int i = 0; i < count; i++)
        {
            parentOf[i] = chats[i].Parent is { } parent && index.TryGetValue(parent, out int at) && at != i && !Under(parentOf, at, i) ? at : -1;
            if (parentOf[i] < 0)
                continue;
            int p = parentOf[i];
            if (last[p] < 0)
                first[p] = i;
            else
                next[last[p]] = i;
            last[p] = i;
        }

        // Each chat's band and time with those under it, the most pressing band and the latest time of those in it
        var band = new HexChatBand[count];
        var active = new long[count];
        var done = new bool[count];
        for (int i = 0; i < count; i++)
            Tree(i);

        void Tree(int i)
        {
            if (done[i])
                return;
            done[i] = true;
            band[i] = BandOf(chats[i], now);
            active[i] = ActiveAt(chats[i]);
            for (int child = first[i]; child >= 0; child = next[child])
            {
                Tree(child);
                if (band[child] < band[i])
                    (band[i], active[i]) = (band[child], active[child]);
                else if (band[child] == band[i])
                    active[i] = Math.Max(active[i], active[child]);
            }
        }

        // The chat open and those over it lifted out of the finished band while that's folded
        if (liftOpen && index.TryGetValue(open, out int opened))
        {
            for (int at = opened, guard = 0; at >= 0 && guard < count; at = parentOf[at], guard++)
            {
                if (band[at] == HexChatBand.Finished)
                    band[at] = HexChatBand.Recent;
            }
        }

        // A finished subagent (and what it started, all finished too) under a family still going is let go of it, folded
        // into the finished group on its own rather than kept on screen for as long as the one that started it works
        for (int i = 0; i < count; i++)
        {
            if (parentOf[i] >= 0 && band[i] == HexChatBand.Finished && band[parentOf[i]] != HexChatBand.Finished)
                parentOf[i] = -1;
        }

        int Compare(int a, int b)
        {
            int by = band[a].CompareTo(band[b]);
            if (by == 0)
                by = active[b].CompareTo(active[a]);
            return by != 0 ? by : chats[b].Id.CompareTo(chats[a].Id);
        }

        var roots = new List<int>();
        for (int i = 0; i < count; i++)
            if (parentOf[i] < 0)
                roots.Add(i);
        roots.Sort(Compare);

        var siblings = new List<int>();
        void Emit(int i, int step, HexChatBand family)
        {
            into.Add(new HexChatPlace(i, step, family));
            int from = siblings.Count;
            for (int child = first[i]; child >= 0; child = next[child])
            {
                if (parentOf[child] == i)
                    siblings.Add(child);
            }
            int many = siblings.Count - from;
            siblings.Sort(from, many, Comparer<int>.Create(Compare));
            int[] children = [.. siblings.GetRange(from, many)];
            siblings.RemoveRange(from, many);
            foreach (int child in children)
                Emit(child, step + 1, family);
        }

        foreach (int root in roots)
            Emit(root, 0, band[root]);
    }

    // Whether the chat at index a is under the one at b already, the way a parent said twice round would loop
    private static bool Under(int[] parentOf, int a, int b)
    {
        for (int at = a, guard = 0; at >= 0 && guard < parentOf.Length; at = parentOf[at], guard++)
            if (at == b)
                return true;
        return false;
    }
}
