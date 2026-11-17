using System.Collections;
using System.Collections.Concurrent;

namespace Hex.Core;

/// <summary>
/// The agents' sessions, owned without a window so the side panel, Noon and the tools all read the one model and it can
/// be tested on its own. Changed on the editor's thread only. Other threads read <see cref="Snapshot"/>, made again by
/// <see cref="Pump"/> whenever a session moved, and <see cref="Waiting"/>, which the waiting tools keep themselves.
/// </summary>
internal sealed class HexAgentHub
{
    private readonly List<HexAgentSession> sessions = [];
    private long pumpedFrom = -1;

    public HexAgentHub() => Chats = new HexHubChats(this);

    /// <summary>The sessions in the order they were added.</summary>
    public IReadOnlyList<HexAgentSession> Sessions => sessions;

    /// <summary>The sessions' chats, in the same order, for code that speaks of chats.</summary>
    public HexHubChats Chats { get; }

    /// <summary>
    /// The sessions whose agent is in hex_agent_wait now, how many waits each. Written off the editor's thread by the
    /// waits themselves, so a message to one of them knows to wake it.
    /// </summary>
    public ConcurrentDictionary<int, int> Waiting { get; } = new();

    // The families' logs of what was said between them, by the id of the chat at the top of each, and where messages
    // are numbered from, across every chat
    private readonly Dictionary<int, HexMailLog> mail = [];
    private long mailMade;

    /// <summary>The number of the next message, one count for every chat so they sort across families.</summary>
    public long NextMailSeq() => ++mailMade;

    /// <summary>The log of what was said in a chat's family, made the first time it's asked for.</summary>
    public HexMailLog MailOf(HexAgentChat chat)
    {
        int root = RootOf(chat).Id;
        if (!mail.TryGetValue(root, out HexMailLog? log))
            mail[root] = log = new HexMailLog();
        return log;
    }

    /// <summary>Goes up whenever a session comes, goes or moves, as of the last pump.</summary>
    public long Version { get; private set; }

    /// <summary>Every session as of the last pump, for the threads that aren't the editor's.</summary>
    public HexHubSnapshot Snapshot => snapshot;

    private volatile HexHubSnapshot snapshot = HexHubSnapshot.Empty;

    /// <summary>Where the rows chats no longer keep in memory go, null for nowhere (they're let go of).</summary>
    public HexAgentHistory? History { get; set; }

    /// <summary>A chat taken in as a session of its own, the rows it lets go of sent to its file.</summary>
    public HexAgentSession Add(HexAgentChat chat, HexSessionKind? kind = null)
    {
        chat.Spill ??= rows => History?.Append(chat.Key, rows);
        var session = new HexAgentSession(chat, kind);
        sessions.Add(session);
        return session;
    }

    /// <summary>A chat's session let go of, false when it had none.</summary>
    public bool Remove(HexAgentChat chat)
    {
        int at = sessions.FindIndex(session => session.Chat == chat);
        if (at < 0)
            return false;
        sessions.RemoveAt(at);
        Waiting.TryRemove(chat.Id, out _);
        // Its file of older rows goes with it
        History?.Delete(chat.Key);
        return true;
    }

    public HexAgentSession? ById(int id) => sessions.Find(session => session.Id == id);

    public HexAgentChat? ChatById(int id) => ById(id)?.Chat;

    /// <summary>The chat at the top of a chat's family, the one somebody started.</summary>
    public HexAgentChat RootOf(HexAgentChat chat)
    {
        HexAgentChat at = chat;
        for (int guard = 0; at.Parent is { } up && ChatById(up) is { } parent && guard < 16; guard++)
            at = parent;
        return at;
    }

    /// <summary>The chats a chat started, in the order they were.</summary>
    public IEnumerable<HexAgentChat> Children(HexAgentChat chat) => sessions.Where(session => session.Parent == chat.Id).Select(session => session.Chat);

    /// <summary>Whether a chat's agent is waiting in hex_agent_wait, which a message to it wakes.</summary>
    public bool IsWaiting(HexAgentChat chat) => Waiting.TryGetValue(chat.Id, out int count) && count > 0;

    /// <summary>
    /// Every tick, the snapshot made again when a session came, went or moved, true when it was. Nothing is made while
    /// they're as they were, a sum of their versions is all it costs. <paramref name="reply"/> is the most of each
    /// reply a row keeps.
    /// </summary>
    public bool Pump(int reply)
    {
        long from = sessions.Count;
        foreach (HexAgentSession session in sessions)
            // What was read of it too, so what watches for chats wanting looking at hears of one being looked at
            from = from * 31 + session.Chat.Version * 2 + (session.Chat.Running ? 1 : 0) + session.Id * 7919L + session.Chat.Seen * 104729L + session.Chat.MailSeen * 15485863L;
        if (from == pumpedFrom)
            return false;

        pumpedFrom = from;
        Version++;
        // A chat that didn't move keeps the row it had, only the one that did (the agent streaming) is made again, its
        // reply and its mail gone through. Made again whole, every chat's were for every line one of them said
        var rows = new Dictionary<int, HexHubRow>(sessions.Count);
        foreach (HexAgentSession session in sessions)
        {
            HexAgentChat chat = session.Chat;
            var stamp = (chat.Version, chat.Running, chat.Seen, chat.MailSeen);
            rows[session.Id] = rowsMade.TryGetValue(session.Id, out var was) && was.Stamp == stamp
                ? was.Row
                : (rowsMade[session.Id] = (stamp, RowOf(chat, reply))).Row;
        }

        if (rowsMade.Count > rows.Count)
        {
            foreach (int gone in rowsMade.Keys.Where(id => !rows.ContainsKey(id)).ToArray())
                rowsMade.Remove(gone);
        }

        snapshot = new HexHubSnapshot(Version, rows);
        return true;
    }

    // Each chat's row and what it was made at, kept for the next snapshot
    private readonly Dictionary<int, ((int Version, bool Running, int Seen, long MailSeen) Stamp, HexHubRow Row)> rowsMade = [];

    // A chat as a row of the snapshot
    private static HexHubRow RowOf(HexAgentChat chat, int reply) => new(chat.Id, chat.Shown, chat.Agent,
        // The model's id as the agent runs it, which an agent can start another with, the alias it was asked for until it says
        chat.Model is { Length: > 0 } running ? running : chat.Options.GetValueOrDefault("model"), chat.Parent, chat.Depth, StateWord(chat),
        chat.Running ? string.Empty : chat.LastReply(reply), chat.TurnSeconds, chat.Cost, chat.Version, chat, chat.UnreadMail());

    /// <summary>A chat's state in a word, as the tools say it.</summary>
    public static string StateWord(HexAgentChat chat) =>
        chat.Running ? "running" : chat.Stopped ? "stopped" : chat.Failed ? "failed" : chat.Entries.Count == 0 ? "new" : "done";
}

/// <summary>
/// The hub's sessions seen as their chats, for the code that adds, finds and counts chats. Adding a chat here makes it
/// a session of the hub, removing it lets the session go.
/// </summary>
internal sealed class HexHubChats(HexAgentHub hub) : IReadOnlyList<HexAgentChat>
{
    public HexAgentChat this[int index] => hub.Sessions[index].Chat;

    public int Count => hub.Sessions.Count;

    public void Add(HexAgentChat chat) => hub.Add(chat);

    public bool Remove(HexAgentChat chat) => hub.Remove(chat);

    public IEnumerator<HexAgentChat> GetEnumerator()
    {
        // By index, a chat added while the list is gone through (an agent starting another) is gone through as well
        for (int i = 0; i < hub.Sessions.Count; i++)
            yield return hub.Sessions[i].Chat;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
