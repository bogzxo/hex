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

    /// <summary>A chat taken in as a session of its own.</summary>
    public HexAgentSession Add(HexAgentChat chat, HexSessionKind? kind = null)
    {
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
            from = from * 31 + session.Chat.Version * 2 + (session.Chat.Running ? 1 : 0) + session.Id * 7919L;
        if (from == pumpedFrom)
            return false;

        pumpedFrom = from;
        Version++;
        snapshot = new HexHubSnapshot(Version, sessions.ToDictionary(session => session.Id, session => RowOf(session.Chat, reply)));
        return true;
    }

    // A chat as a row of the snapshot
    private static HexHubRow RowOf(HexAgentChat chat, int reply) => new(chat.Id, chat.Shown, chat.Agent,
        chat.Options.GetValueOrDefault("model") ?? chat.Model, chat.Parent, chat.Depth, StateWord(chat),
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
