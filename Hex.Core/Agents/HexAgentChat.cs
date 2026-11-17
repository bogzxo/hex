namespace Hex.Core;

/// <summary>What a row of a chat is, what was asked, what the agent said or thought, a tool it called, a note or something wrong.</summary>
internal enum HexAgentEntryKind { User, Text, Thinking, Tool, Note, Error }

/// <summary>Where a tool call has got to.</summary>
internal enum HexAgentToolState { Running, Done, Failed }

/// <summary>
/// A message one chat's agent sent another's, a finding, a question, an answer or an instruction. Numbered across all
/// chats so what's been read is one number a chat keeps. <paramref name="Directive"/> is an instruction, which starts a
/// finished agent working on it where anything else waits to be read.
/// </summary>
internal sealed record HexAgentMail(long Seq, int? From, string FromName, string Kind, string Text, bool Directive);

/// <summary>
/// A row of a chat. A tool's row keeps its id (to be told it's done), its name, what it was called with and what it said
/// back, and when it started and ended. <see cref="Version"/> goes up with every change, what the panel redraws a row by.
/// </summary>
internal sealed class HexAgentEntry(HexAgentEntryKind kind, string text)
{
    public HexAgentEntryKind Kind { get; } = kind;
    public string Text { get; set; } = text;
    public string? AskedText { get; init; }
    public string? ContextLocation { get; init; }
    public string? ToolId { get; init; }
    public string? ToolName { get; init; }
    public string? Input { get; init; }
    public string? Output { get; set; }
    public HexAgentToolState State { get; set; } = HexAgentToolState.Running;
    public long Started { get; } = Environment.TickCount64;
    public long? Ended { get; set; }

    /// <summary>What a subagent this tool started is doing now, its latest step, null for a tool that started none.</summary>
    public string? Progress { get; set; }

    /// <summary>How many steps the subagent this tool started has taken, said on its row rather than a row each.</summary>
    public int Steps { get; set; }

    /// <summary>Whether its row is opened to say all of it.</summary>
    public bool Open { get; set; }

    /// <summary>Something asked while the agent was working, sent to it and waiting for it to take it in.</summary>
    public bool Queued { get; set; }

    /// <summary>Something asked while the agent was working that it never took in, the turn ending first.</summary>
    public bool Unheard { get; set; }

    public int Version { get; set; }

    /// <summary>How long a tool took, or has been going, in seconds.</summary>
    public double Seconds => ((Ended ?? Environment.TickCount64) - Started) / 1000.0;
}

/// <summary>
/// A chat with an agent system, the rows of it, the session the agent said it's in (handed back to it so the next prompt
/// goes on from there), and what the turns used. Everything here happens on the editor's thread, the agent's lines are
/// queued for it by <see cref="HexAgentProcess"/>, so none of it locks.
/// </summary>
internal sealed class HexAgentChat(int id, string agent)
{
    // The most rows a chat keeps, the oldest go after that
    public const int MOST_ENTRIES = 2000;

    private readonly List<HexAgentEntry> entries = [];

    public int Id { get; } = id;

    /// <summary>The id of the agent profile it's with.</summary>
    public string Agent { get; set; } = agent;

    public IReadOnlyList<HexAgentEntry> Entries => entries;

    /// <summary>What it's called in the list, the start of the first thing asked.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The agent's session, null until it says one.</summary>
    public string? Session { get; set; }

    public bool Running { get; private set; }

    /// <summary>The chat whose agent started this one (hex_agent_spawn), null for one somebody started in the panel.</summary>
    public int? Parent { get; init; }

    /// <summary>How many agents up it was started from, 0 for one somebody started.</summary>
    public int Depth { get; init; }

    /// <summary>What it was called by the agent that started it, shown in place of what it was asked when there is one.</summary>
    public string? Name { get; init; }

    // The most messages a chat keeps, the oldest go after that
    private const int MOST_MAIL = 100;

    private readonly List<HexAgentMail> mail = [];
    private long mailSeen;

    /// <summary>Something about the chat that isn't a row moved (what it used), so what shows it is drawn again.</summary>
    public void Moved() => Version++;

    /// <summary>The messages other chats' agents sent this one, read or not, on the editor's thread.</summary>
    public IReadOnlyList<HexAgentMail> Mail => mail;

    /// <summary>The number of the last message read, read and moved on from any thread.</summary>
    public long MailSeen => Interlocked.Read(ref mailSeen);

    /// <summary>A message for this chat's agent kept until it's read.</summary>
    public void Post(HexAgentMail message)
    {
        mail.Add(message);
        if (mail.Count > MOST_MAIL)
            mail.RemoveAt(0);
        Version++;
    }

    /// <summary>Every message up to <paramref name="seq"/> read, from whichever thread read them. It only goes forward.</summary>
    public void MailRead(long seq)
    {
        long was;
        do
        {
            was = Interlocked.Read(ref mailSeen);
            if (seq <= was)
                return;
        }
        while (Interlocked.CompareExchange(ref mailSeen, seq, was) != was);
    }

    /// <summary>The messages not read yet, oldest first.</summary>
    public HexAgentMail[] UnreadMail() => [.. mail.Where(each => each.Seq > MailSeen)];

    /// <summary>The agent's options for this chat alone (the model it was started with), over its extension's settings.</summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether its last turn was stopped rather than finishing.</summary>
    public bool Stopped { get; set; }

    /// <summary>Whether something went wrong in the last turn, an error row since the last thing asked.</summary>
    public bool Failed
    {
        get
        {
            for (int i = entries.Count - 1; i >= 0 && entries[i].Kind != HexAgentEntryKind.User; i--)
            {
                if (entries[i].Kind == HexAgentEntryKind.Error)
                    return true;
            }

            return false;
        }
    }

    /// <summary>The version of it somebody last had on screen, it's unread while it's moved on since and isn't running.</summary>
    public int Seen { get; set; }

    /// <summary>Whether it finished a turn since it was last on screen.</summary>
    public bool Unread => !Running && Version != Seen && entries.Count > 0;

    /// <summary>What it's called in a list, its name, or what it was asked.</summary>
    public string Shown => Name is { Length: > 0 } name ? name : Title;

    /// <summary>
    /// What the agent said in its last turn, its text rows since the last thing asked joined, the most recent kept when
    /// it's longer than <paramref name="most"/>. Empty when it hasn't said anything yet.
    /// </summary>
    public string LastReply(int most = 8000)
    {
        int from = entries.Count;
        while (from > 0 && entries[from - 1].Kind != HexAgentEntryKind.User)
            from--;

        string said = string.Join("\n\n", entries.Skip(from).Where(entry => entry.Kind is HexAgentEntryKind.Text or HexAgentEntryKind.Error).Select(entry => entry.Text));
        return said.Length > most ? "…" + said[^most..] : said;
    }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double Cost { get; set; }
    public string? Model { get; set; }

    /// <summary>How much of its context the agent's last call took, what it was reading when it last spoke, 0 when it didn't say.</summary>
    public long ContextUsed { get; set; }

    /// <summary>How much context its model has, 0 when nobody said.</summary>
    public long ContextLimit { get; set; }

    /// <summary>The input tokens read back out of the cache rather than paid for in full, over the whole chat.</summary>
    public long CacheRead { get; set; }

    private long turnStarted;

    /// <summary>How long the last turn took (or the one going on has), in seconds.</summary>
    public double TurnSeconds { get; private set; }

    /// <summary>Goes up with every change, the panel redraws when it's moved.</summary>
    public int Version { get; private set; }

    /// <summary>How many rows have been dropped off the top, for the panel to know its rows from the start.</summary>
    public int Dropped { get; private set; }

    private HexAgentEntry Add(HexAgentEntry entry)
    {
        entries.Add(entry);
        if (entries.Count > MOST_ENTRIES)
        {
            entries.RemoveAt(0);
            Dropped++;
        }

        Version++;
        return entry;
    }

    /// <summary>A turn started with what somebody asked, the chat named by <paramref name="title"/> (what was asked) the first time.</summary>
    public HexAgentEntry Ask(string prompt, string? title = null, string? askedText = null, string? contextLocation = null)
    {
        if (Title.Length == 0)
        {
            string line = (title ?? prompt).Trim().Split('\n')[0];
            Title = line.Length > 48 ? line[..45] + "..." : line;
        }

        Running = true;
        Stopped = false;
        turnStarted = Environment.TickCount64;
        TurnSeconds = 0;
        return Add(new HexAgentEntry(HexAgentEntryKind.User, prompt) { AskedText = askedText, ContextLocation = contextLocation });
    }

    /// <summary>
    /// Something asked while the turn is going on, for an agent that's steered (more said to it as it works). Queued
    /// until the agent says it took it in, see <see cref="Taken"/>, the turn going on as it was.
    /// </summary>
    public HexAgentEntry Steer(string prompt, string? askedText = null, string? contextLocation = null) =>
        Add(new HexAgentEntry(HexAgentEntryKind.User, prompt) { AskedText = askedText, ContextLocation = contextLocation, Queued = true });

    /// <summary>Whether something asked is still waiting for the agent to take it in.</summary>
    public bool HasQueued => entries.Any(entry => entry.Queued);

    /// <summary>The first thing still queued taken in by the agent, false when there's nothing queued.</summary>
    public bool Taken()
    {
        if (entries.FirstOrDefault(entry => entry.Queued) is not { } taken)
            return false;

        taken.Queued = false;
        Touch(taken);
        return true;
    }

    /// <summary>What the agent said, a row of its own.</summary>
    public HexAgentEntry Say(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Text, text));

    public HexAgentEntry Think(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Thinking, text));

    public HexAgentEntry Note(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Note, text));

    public HexAgentEntry Error(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Error, text));

    /// <summary>
    /// A tool called, running until it's said to be done. Said again by the same id in the same turn it's the row there
    /// is, so an agent that only says a tool once it's finished can say it and then that it's done. Codex counts its
    /// items from 0 again every turn, so an id of a turn before is another tool.
    /// </summary>
    public HexAgentEntry Tool(string id, string name, string input) =>
        ToolOfTurn(id) ?? Add(new HexAgentEntry(HexAgentEntryKind.Tool, name) { ToolId = id, ToolName = name, Input = input });

    // The tool of an id in the turn going on, after the last thing asked
    private HexAgentEntry? ToolOfTurn(string id)
    {
        for (int i = entries.Count - 1; i >= 0 && entries[i].Kind != HexAgentEntryKind.User; i--)
        {
            if (entries[i].Kind == HexAgentEntryKind.Tool && entries[i].ToolId == id)
                return entries[i];
        }

        return null;
    }

    /// <summary>
    /// A step of a subagent a tool started, said on that tool's row as its latest step and a count, where a row each made
    /// a subagent's every search and thought look like the chat's own. False when no tool of that id is in the turn.
    /// </summary>
    public bool Progress(string id, string step)
    {
        if (ToolOfTurn(id) is not { } tool)
            return false;

        tool.Steps++;
        if (!string.IsNullOrWhiteSpace(step))
            tool.Progress = step;
        Touch(tool);
        return true;
    }

    /// <summary>A tool done, by its id, false when there's no tool of that id going (it's added as done then).</summary>
    public bool ToolDone(string id, bool ok, string output)
    {
        HexAgentEntry? tool = ToolOfTurn(id);
        bool found = tool is not null;
        tool ??= Tool(id, "tool", string.Empty);
        tool.State = ok ? HexAgentToolState.Done : HexAgentToolState.Failed;
        tool.Output = output;
        tool.Ended = Environment.TickCount64;
        tool.Version++;
        Version++;
        return found;
    }

    /// <summary>The turn over, every tool still going taken as ended with it.</summary>
    public void Done()
    {
        if (!Running)
            return;

        Running = false;
        TurnSeconds = (Environment.TickCount64 - turnStarted) / 1000.0;
        foreach (HexAgentEntry tool in entries.Where(entry => entry.Kind == HexAgentEntryKind.Tool && entry.State == HexAgentToolState.Running))
        {
            tool.State = HexAgentToolState.Failed;
            tool.Output ??= "the agent stopped before it said how this went";
            tool.Ended = Environment.TickCount64;
            tool.Version++;
        }

        foreach (HexAgentEntry asked in entries.Where(entry => entry.Queued))
        {
            asked.Queued = false;
            asked.Unheard = true;
            asked.Version++;
        }

        Version++;
    }

    /// <summary>The time of the turn going on brought up to now, for the panel's footer.</summary>
    public void Tick()
    {
        if (Running)
            TurnSeconds = (Environment.TickCount64 - turnStarted) / 1000.0;
    }

    /// <summary>Something about a row changed (opened, closed), for the panel to redraw it.</summary>
    public void Touch(HexAgentEntry entry)
    {
        entry.Version++;
        Version++;
    }
}
