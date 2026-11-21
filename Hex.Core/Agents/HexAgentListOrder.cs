namespace Hex.Core;

/// <summary>
/// Where a chat stands in the lists of chats, top to bottom: waiting on the person, working, finished and not settled yet
/// (or new and not asked anything yet), and settled, the Finished group, which folds away.
/// </summary>
internal enum HexChatBand { NeedsYou, Working, Recent, Finished }

/// <summary>
/// What the order of a chat goes by, taken off it once so the order is worked out with times the caller says (tests and
/// the lists alike). <paramref name="TurnStarted"/>, <paramref name="Ended"/> and <paramref name="UnsettledAt"/> are
/// Environment.TickCount64's milliseconds, 0 for none.
/// </summary>
internal readonly record struct HexChatFacts(int Id, int? Parent, bool Running, bool NeedsYou, bool Asked, bool Unread, long TurnStarted, long Ended, bool Settled = false, long UnsettledAt = 0)
{
    /// <summary>The facts of a chat as it is now, what waits on the person being said by the caller (it reads the chat's mail and rows).</summary>
    public static HexChatFacts Of(HexAgentChat chat, bool needsYou) =>
        new(chat.Id, chat.Parent, chat.Running || chat.RunGoing, needsYou, chat.Entries.Count > 0, chat.Unread, chat.TurnStarted, chat.Ended, chat.Settled, chat.UnsettledAt);
}

/// <summary>A chat's place in the lists, its index in the chats it was worked out from, how far it's stepped in under the one that started it and its family's band.</summary>
internal readonly record struct HexChatPlace(int Index, int Step, HexChatBand Band);

/// <summary>
/// The order the Agents view's list and Agentic's fleet show the chats in. Families stay together (a chat nobody started,
/// then the ones it started under it), each family in the band of its most pressing chat: those waiting on the person
/// first, then those working, then those finished and not settled, then the settled (the Finished group), most recently
/// active first in each. One rule says what's in the Finished group: a chat is there while it's settled, and only then.
/// The person settles one from its menu; a finished one settles by itself once it's been done a while with nothing unread
/// or waiting on the person (SettlesNow); working again or waiting on the person unsettles it (Unsettles). A settled
/// subagent of a family still going is let go of it into the group on its own.
/// </summary>
internal static class HexAgentListOrder
{
    /// <summary>A chat's own band, its family's being the most pressing of its members'.</summary>
    public static HexChatBand BandOf(in HexChatFacts chat) =>
        chat.NeedsYou ? HexChatBand.NeedsYou
        : chat.Running ? HexChatBand.Working
        : !chat.Asked ? HexChatBand.Recent
        : chat.Settled ? HexChatBand.Finished
        : HexChatBand.Recent;

    /// <summary>
    /// Whether a finished chat settles by itself at <paramref name="now"/>: asked something, done, nothing unread or
    /// waiting on the person, and <paramref name="afterMs"/> gone by since it ended (or since the person last unsettled
    /// it). Never with <paramref name="afterMs"/> 0.
    /// </summary>
    public static bool SettlesNow(in HexChatFacts chat, long now, long afterMs) =>
        afterMs > 0 && !chat.Settled && chat.Asked && !chat.Running && !chat.NeedsYou && !chat.Unread
        && now - Math.Max(chat.Ended, chat.UnsettledAt) >= afterMs;

    /// <summary>Whether a settled chat comes out of the Finished group: it's working again or something waits on the person.</summary>
    public static bool Unsettles(in HexChatFacts chat) => chat.Settled && (chat.Running || chat.NeedsYou);

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
    public static void Order(IReadOnlyList<HexChatFacts> chats, int open, bool liftOpen, List<HexChatPlace> into)
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
            band[i] = BandOf(chats[i]);
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

        // The children of each chat sorted in a stretch at the end of one list, emitted from there and let go of after.
        // A chat with none (nearly all of them) costs nothing, where each made a comparer and two copies
        var siblings = new List<int>();
        Comparer<int>? byOrder = null;
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
            if (many == 0)
                return;
            siblings.Sort(from, many, byOrder ??= Comparer<int>.Create(Compare));
            for (int at = from; at < from + many; at++)
                Emit(siblings[at], step + 1, family);
            siblings.RemoveRange(from, many);
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
