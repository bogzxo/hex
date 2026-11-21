namespace Hex.Core;

/// <summary>How a message reached the agent it was for, for the family's view to say.</summary>
internal enum HexMailRoute
{
    /// <summary>Put on the stdin of an agent that takes more as it works.</summary>
    Steered,

    /// <summary>Handed to an agent sitting in hex_agent_wait, which it woke.</summary>
    WokeWait,

    /// <summary>Kept in its inbox for it to read between steps, or at its next turn.</summary>
    Inbox,

    /// <summary>An instruction to an agent that had finished, which started it working on it.</summary>
    StartedTurn
}

/// <summary>A message of the family's log and how it reached the one it was for.</summary>
internal sealed record HexMailEntry(HexAgentMail Mail, HexMailRoute Route);

/// <summary>
/// Everything said between the agents of one family (the chat somebody started and every chat under it), the person
/// watching and Hex, in the order it was said. Each receiving chat keeps its own mail as well, this is the whole
/// conversation in one place for the family's view. Editor thread only.
/// </summary>
internal sealed class HexMailLog
{
    /// <summary>Endpoints that aren't chats, the person watching and Hex itself.</summary>
    public const int USER = 0, HEX = -2;

    /// <summary>The most messages kept, the oldest read ones going first when there are more.</summary>
    public const int MOST = 50;

    private readonly List<HexMailEntry> entries = [];
    private readonly Func<HexAgentMail, bool>? unread;

    /// <summary>A log, <paramref name="unread"/> saying whether the one a message is for hasn't read it yet (none, all read).</summary>
    public HexMailLog(Func<HexAgentMail, bool>? unread = null) => this.unread = unread;

    /// <summary>Every message, oldest first.</summary>
    public IReadOnlyList<HexMailEntry> All => entries;

    /// <summary>
    /// A message put in the log. Past the most it keeps, the oldest one its receiver has read goes first, then the oldest
    /// unread one that isn't an instruction. An instruction an agent still has to read and act on is never let go of, the
    /// log growing past its most for as long as nothing else can go.
    /// </summary>
    public void Add(HexAgentMail mail, HexMailRoute route)
    {
        entries.Add(new HexMailEntry(mail, route));
        while (entries.Count > MOST)
        {
            int drop = entries.FindIndex(entry => !Unread(entry.Mail));
            if (drop < 0)
                drop = entries.FindIndex(entry => !IsInstruction(entry.Mail));
            if (drop < 0)
                return;
            entries.RemoveAt(drop);
        }
    }

    // Whether the one it's for hasn't read it, or, for a message to an endpoint that isn't a chat, whether it asks the person something
    private bool Unread(HexAgentMail mail) => unread?.Invoke(mail) ?? false;

    /// <summary>Whether a message is an instruction, which an agent has to act on.</summary>
    public static bool IsInstruction(HexAgentMail mail) => mail.Directive || mail.Kind == "instruction";

    /// <summary>Where a message came from as an endpoint, the person for one with no chat.</summary>
    public static int FromOf(HexAgentMail mail) => mail.From ?? USER;

    /// <summary>The messages between two endpoints, either way, oldest first.</summary>
    public IEnumerable<HexAgentMail> Between(int a, int b) =>
        entries.Select(entry => entry.Mail).Where(mail => (FromOf(mail) == a && mail.To == b) || (FromOf(mail) == b && mail.To == a));

    /// <summary>How many messages went from each endpoint to each other since a message's number, and the latest of them.</summary>
    public (int From, int To, int Count, long Last)[] Flows(long since) =>
        [.. entries.Select(entry => entry.Mail).Where(mail => mail.Seq > since)
            .GroupBy(mail => (From: FromOf(mail), mail.To))
            .Select(group => (group.Key.From, group.Key.To, group.Count(), group.Max(mail => mail.Seq)))
            .OrderBy(flow => flow.Item4)];
}
