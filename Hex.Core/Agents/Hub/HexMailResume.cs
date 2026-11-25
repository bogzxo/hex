namespace Hex.Core;

/// <summary>
/// When a message to a chat that isn't working starts a turn for it. A message is first kept in the chat's mail and the
/// turn starts a moment later (<see cref="DEBOUNCE_MS"/>), so messages that arrive together start one turn with all of
/// them. A message from a chat the receiver itself messaged within <see cref="ECHO_MS"/> doesn't start one, which keeps
/// two agents answering each other from waking one another for good (ping-pong). Editor thread only.
/// </summary>
internal sealed class HexMailResume(Func<long>? clock = null)
{
    /// <summary>How long after the first message a turn is started, to batch what comes with it.</summary>
    public const long DEBOUNCE_MS = 1000;

    /// <summary>A message from a chat the receiver messaged this recently is an echo, which doesn't start a turn.</summary>
    public const long ECHO_MS = 1000;

    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<(int From, int To), long> sent = [];
    private readonly Dictionary<int, long> due = [];

    /// <summary>Writes down that a chat messaged another, for <see cref="Echo"/> to look at.</summary>
    public void Sent(int from, int to)
    {
        long at = now();
        sent[(from, to)] = at;
        if (sent.Count > 256)
        {
            foreach ((int, int) stale in sent.Where(pair => at - pair.Value > ECHO_MS).Select(pair => pair.Key).ToList())
                sent.Remove(stale);
        }
    }

    /// <summary>Whether a message from a chat to another answers one the other sent it a moment ago.</summary>
    public bool Echo(int from, int to) => sent.TryGetValue((to, from), out long at) && now() - at <= ECHO_MS;

    /// <summary>Has a chat's turn start once the debounce is over. A second message doesn't push it later.</summary>
    public void Schedule(int chat) => due.TryAdd(chat, now() + DEBOUNCE_MS);

    /// <summary>Whether a chat has a turn waiting to start.</summary>
    public bool Pending(int chat) => due.ContainsKey(chat);

    /// <summary>Forgets a chat's waiting turn.</summary>
    public void Cancel(int chat) => due.Remove(chat);

    /// <summary>The chats whose debounce is over, still waiting until <see cref="Cancel"/> or <see cref="Take"/> is called for each.</summary>
    public int[] Ready()
    {
        if (due.Count == 0)
            return [];
        long at = now();
        List<int>? ready = null;
        foreach (var (chat, when) in due)
        {
            if (when <= at)
                (ready ??= []).Add(chat);
        }
        return ready is null ? [] : [.. ready];
    }

    /// <summary>Takes a ready chat off the waiting ones.</summary>
    public void Take(int chat) => due.Remove(chat);
}
