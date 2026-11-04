namespace Hex.Core;

/// <summary>What a row of a chat is, what was asked, what the agent said or thought, a tool it called, a note or something wrong.</summary>
internal enum HexAgentEntryKind { User, Text, Thinking, Tool, Note, Error }

/// <summary>Where a tool call has got to.</summary>
internal enum HexAgentToolState { Running, Done, Failed }

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

    /// <summary>Whether its row is opened to say all of it.</summary>
    public bool Open { get; set; }

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

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double Cost { get; set; }
    public string? Model { get; set; }

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
        turnStarted = Environment.TickCount64;
        TurnSeconds = 0;
        return Add(new HexAgentEntry(HexAgentEntryKind.User, prompt) { AskedText = askedText, ContextLocation = contextLocation });
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
