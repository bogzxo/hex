namespace Hex.Core;

/// <summary>What a row of a chat is, what was asked, what the agent said or thought, a tool it called, a note or something wrong.</summary>
internal enum HexAgentEntryKind { User, Text, Thinking, Tool, Note, Error, Approval, Question }

/// <summary>Where a tool call has got to.</summary>
internal enum HexAgentToolState { Running, Done, Failed }

/// <summary>
/// A message one chat's agent sent another's, a finding, a question, an answer or an instruction. Numbered across all
/// chats so what's been read is one number a chat keeps. <paramref name="Directive"/> is an instruction, which starts a
/// finished agent working on it where anything else waits to be read.
/// </summary>
internal sealed record HexAgentMail(long Seq, int? From, string FromName, string Kind, string Text, bool Directive)
{
    /// <summary>The chat it's for, or one of the endpoints of HexMailLog (the person watching, Hex).</summary>
    public int To { get; init; }

    /// <summary>The message it answers, when it's an answer to one.</summary>
    public long? Re { get; init; }
}

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
    public long Started { get; init; } = Environment.TickCount64;
    public long? Ended { get; set; }

    /// <summary>
    /// For what somebody asked, the checkpoint of each repository taken as the turn began (its top folder and the git tree),
    /// kept with the chat so a turn's changes can still be reviewed and put back after Hex starts again. Null while none.
    /// </summary>
    public IReadOnlyList<(string Top, string Tree)>? Checkpoints { get; set; }

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

    /// <summary>
    /// For an approval, what's done with the person's answer, true to let the agent go ahead. Null once it's been answered
    /// (or for any other row), so a second press does nothing. Its state says the answer, Done allowed and Failed denied.
    /// </summary>
    public Action<bool>? Answer { get; set; }

    /// <summary>For a question, the answers the agent offers, the person picking one or writing their own.</summary>
    public string[]? Choices { get; init; }

    /// <summary>For a question, the choice the agent would take itself, what it goes on with when nobody answers in time.</summary>
    public int? Recommended { get; init; }

    /// <summary>For a question, whether several of its answers can be picked together, the card's buttons toggles then.</summary>
    public bool Multiple { get; init; }

    /// <summary>For a question of several answers, which are picked so far, kept here so the card made again keeps them.</summary>
    public HashSet<int> Picked { get; } = [];

    /// <summary>For a question, when the agent stops waiting and goes on without an answer (Environment.TickCount64).</summary>
    public long? Deadline { get; init; }

    /// <summary>
    /// For a question, what's done with the answer, null once it's been given or the time ran out, so it's given once. The
    /// answer is in <see cref="Output"/>, the state Done when the person gave it and Failed when nobody did.
    /// </summary>
    public Action<string?>? Respond { get; set; }

    /// <summary>How long a tool took, or has been going, in seconds.</summary>
    public double Seconds => ((Ended ?? Environment.TickCount64) - Started) / 1000.0;
}

/// <summary>
/// A chat with an agent system, the rows of it, the session the agent said it's in (handed back to it so the next prompt
/// goes on from there), and what the turns used. Everything here happens on the editor's thread, the agent's lines are
/// queued for it by <see cref="HexAgentProcess"/>, so none of it locks.
/// </summary>
internal sealed partial class HexAgentChat(int id, string agent)
{
    // The rows a chat keeps in memory, and how many more it lets come before the oldest go to its file a batch at a time.
    // A batch rather than a row each, the panel draws the chat again from the start when rows leave its top
    public const int MOST_ENTRIES = 200, SPILL_BATCH = 50;

    private readonly List<HexAgentEntry> entries = [];

    /// <summary>What names its file of rows it no longer keeps, the same across Hex's restarts.</summary>
    public string Key { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Where the rows it no longer keeps go, oldest first, nowhere (they're let go of) when nobody said.</summary>
    public Action<IReadOnlyList<HexAgentEntry>>? Spill { get; set; }

    public int Id { get; } = id;

    /// <summary>The id of the agent profile it's with.</summary>
    public string Agent { get; set; } = agent;

    /// <summary>
    /// The pictures going with the message being written for the agent right now (pasted into the box), PNG files, for
    /// its extension to hand over in the agent's own way (chat.images). Empty between messages.
    /// </summary>
    public IReadOnlyList<string> Attached { get; set; } = [];

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

    /// <summary>
    /// What it was called by the agent that started it, or renamed to from its card, shown in place of what it was asked
    /// when there is one. Null goes back to what it was asked.
    /// </summary>
    public string? Name { get; set; }

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

    /// <summary>How many messages aren't read yet, counted with nothing made, for what's looked at every few ticks.</summary>
    public int UnreadMailCount()
    {
        long seen = MailSeen;
        int count = 0;
        for (int i = 0; i < mail.Count; i++)
            count += mail[i].Seq > seen ? 1 : 0;
        return count;
    }

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

    /// <summary>How many times its context was compacted, the agent summing up what went before to make room.</summary>
    public int Compactions { get; private set; }

    /// <summary>How many tokens the context held before the last compaction and after it, 0 when nobody said.</summary>
    public long CompactedFrom { get; private set; }
    public long CompactedTo { get; private set; }

    /// <summary>How many times a call to the model was tried again after it failed, over the whole chat.</summary>
    public int Retries { get; private set; }

    /// <summary>Where the agent stands against its rate or usage limit, as last said, null while nobody has said.</summary>
    public HexAgentLimit? Limit { get; private set; }

    /// <summary>
    /// Every limit the agent was told of, the latest of each kind (the 5-hour session, the week), since a plan has
    /// several at once and the latest said alone hid the others.
    /// </summary>
    public IReadOnlyDictionary<string, HexAgentLimit> Limits => limits;

    private readonly Dictionary<string, HexAgentLimit> limits = new(StringComparer.Ordinal);

    // The row saying the agent is trying a call again, put right in place while it keeps trying rather than a row a try
    private HexAgentEntry? retrying;

    private long turnStarted;

    /// <summary>How long the last turn took (or the one going on has), in seconds.</summary>
    public double TurnSeconds { get; private set; }

    /// <summary>Goes up with every change, the panel redraws when it's moved.</summary>
    public int Version { get; private set; }

    /// <summary>Called something else from its card, nothing going back to what it was asked. Kept with the chat.</summary>
    public void Rename(string? name)
    {
        string? named = string.IsNullOrWhiteSpace(name) ? null : name.Trim().ReplaceLineEndings(" ");
        if (named == Name)
            return;
        Name = named;
        Version++;
    }

    public HexAgentPlan? Plan { get; private set; }

    /// <summary>The chat's plan, when each step was done kept with it (HexAgentPlan.Stamped).</summary>
    public void SetPlan(HexAgentPlan plan)
    {
        Plan = HexAgentPlan.Stamped(Plan, plan, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Version++;
    }

    /// <summary>How many rows have been dropped off the top, for the panel to know its rows from the start.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// A chat read back as it was written (HexAgentChatStore), its title and its rows, a tool still going when it was
    /// written taken as having ended with it.
    /// </summary>
    public void Restore(string title, IEnumerable<HexAgentEntry> rows, int dropped = 0)
    {
        Title = title;
        Dropped = dropped;
        entries.Clear();
        foreach (HexAgentEntry row in rows)
        {
            if (row.Kind == HexAgentEntryKind.Tool && row.State == HexAgentToolState.Running)
                row.State = HexAgentToolState.Failed;
            entries.Add(row);
        }

        Version++;
        Seen = Version;
    }

    /// <summary>
    /// Everything of it let go of but who it is (its id, agent, the chat that started it, its name and options), for it to
    /// start afresh, its rows, its session, its mail and what it used. A new key, the file of its old rows the caller's
    /// to delete by the one it had.
    /// </summary>
    public void Clear()
    {
        Restore(string.Empty, [], 0);
        Plan = null;
        Session = null;
        Key = Guid.NewGuid().ToString("N");
        InputTokens = OutputTokens = CacheRead = ContextUsed = 0;
        Cost = 0;
        Stopped = false;
        mail.Clear();
        Version++;
    }

    private HexAgentEntry Add(HexAgentEntry entry)
    {
        entries.Add(entry);
        if (entries.Count > MOST_ENTRIES + SPILL_BATCH)
        {
            // The oldest to its file, never one still queued, a tool still going or a question still waiting for its answer,
            // which are still to change. A question spilled while the agent waited was never seen and timed out unanswered
            int going = entries.FindIndex(each => each.Queued || each.Answer is not null || each.Respond is not null
                || (each.Kind == HexAgentEntryKind.Tool && each.State == HexAgentToolState.Running));
            int spill = Math.Min(entries.Count - MOST_ENTRIES, going < 0 ? int.MaxValue : going);
            if (spill > 0)
            {
                HexAgentEntry[] old = [.. entries.GetRange(0, spill)];
                entries.RemoveRange(0, spill);
                Dropped += spill;
                Spill?.Invoke(old);
            }
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

    /// <summary>
    /// Something asked and still queued taken back out of the chat, to be written again, false when it's gone further. The
    /// rows are drawn again from the start after it, see <see cref="Reshaped"/>.
    /// </summary>
    public bool Withdraw(HexAgentEntry entry)
    {
        if (!entry.Queued || !entries.Remove(entry))
            return false;
        Reshaped++;
        Version++;
        return true;
    }

    /// <summary>How many times a row was taken out of the middle, for the panel to draw the chat again from the start.</summary>
    public int Reshaped { get; private set; }

    /// <summary>Whether something asked is still waiting for the agent to take it in.</summary>
    public bool HasQueued => entries.Any(entry => entry.Queued);

    /// <summary>Resolve the first queued message that matches the prompt, or the oldest when no prompt was given.</summary>
    public bool Taken(string? prompt = null, bool accepted = true)
    {
        if (entries.FirstOrDefault(entry => entry.Queued && (prompt is null || entry.Text == prompt)) is not { } taken)
            return false;

        taken.Queued = false;
        taken.Unheard = !accepted;
        Touch(taken);
        return true;
    }

    /// <summary>
    /// The agent asking leave to do something (run a command, change a file), a row with what it is that waits for the
    /// person's answer, handed to <paramref name="answer"/> once it's given.
    /// </summary>
    public HexAgentEntry Approval(string title, string detail, Action<bool> answer)
    {
        HexAgentEntry asked = Add(new HexAgentEntry(HexAgentEntryKind.Approval, title) { Input = detail, Answer = answer });
        Approving?.Invoke(asked);
        return asked;
    }

    /// <summary>
    /// Told of every approval the agent asks, for the agent that started this one to be asked as well as the person
    /// (HexAgentHub sets it as the chat is taken in).
    /// </summary>
    public Action<HexAgentEntry>? Approving { get; set; }

    /// <summary>An approval answered, the agent told, the row saying what was decided. False when it had been already.</summary>
    public bool Decide(HexAgentEntry approval, bool allowed)
    {
        if (approval.Kind != HexAgentEntryKind.Approval || approval.Answer is not { } answer)
            return false;
        approval.Answer = null;
        approval.State = allowed ? HexAgentToolState.Done : HexAgentToolState.Failed;
        approval.Ended = Environment.TickCount64;
        Touch(approval);
        answer(allowed);
        return true;
    }

    /// <summary>
    /// The agent asking the person something, a card with the answers it offers and a box for one of their own, which it
    /// waits on until <paramref name="seconds"/> are up and then goes on with the one it would pick itself.
    /// </summary>
    public HexAgentEntry Question(string text, string[] choices, int? recommended, double seconds, Action<string?> respond, bool multiple = false) =>
        Add(new HexAgentEntry(HexAgentEntryKind.Question, text)
        {
            Choices = choices,
            Multiple = multiple,
            Recommended = recommended is { } pick && pick >= 0 && pick < choices.Length ? pick : null,
            Deadline = Environment.TickCount64 + (long)(Math.Max(1, seconds) * 1000),
            Respond = respond
        });

    /// <summary>
    /// A question answered, by the person or, with null, by nobody in time. The row says which, and the agent is handed
    /// the answer. False when it had been answered already.
    /// </summary>
    public bool Reply(HexAgentEntry question, string? answer)
    {
        if (question.Kind != HexAgentEntryKind.Question || question.Respond is not { } respond)
            return false;
        question.Respond = null;
        question.Output = answer;
        question.State = answer is null ? HexAgentToolState.Failed : HexAgentToolState.Done;
        question.Ended = Environment.TickCount64;
        Touch(question);
        respond(answer);
        return true;
    }

    /// <summary>The approvals and questions still waiting for the person.</summary>
    public IEnumerable<HexAgentEntry> Pending => entries.Where(IsPending);

    /// <summary>Whether an approval or a question waits for the person, a plain look the panels take every tick.</summary>
    public bool HasPending
    {
        get
        {
            foreach (HexAgentEntry entry in entries)
                if (IsPending(entry))
                    return true;
            return false;
        }
    }

    /// <summary>The first of a kind of thing waiting for the person, an approval or a question, null for none.</summary>
    public HexAgentEntry? FirstPending(HexAgentEntryKind kind)
    {
        foreach (HexAgentEntry entry in entries)
            if (entry.Kind == kind && IsPending(entry))
                return entry;
        return null;
    }

    /// <summary>Whether an entry waits for the person, an approval not answered or a question not replied to.</summary>
    public static bool IsPending(HexAgentEntry entry) =>
        (entry.Kind == HexAgentEntryKind.Approval && entry.Answer is not null) || (entry.Kind == HexAgentEntryKind.Question && entry.Respond is not null);

    /// <summary>What the agent said, a row of its own.</summary>
    public HexAgentEntry Say(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Text, text));

    public HexAgentEntry Think(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Thinking, text));

    public HexAgentEntry Note(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Note, WithoutEscapes(text)));

    public HexAgentEntry Error(string text) => Add(new HexAgentEntry(HexAgentEntryKind.Error, WithoutEscapes(text)));

    /// <summary>
    /// A terminal's colour and cursor codes taken out of what a program printed, its words kept. A note or an error is
    /// often a line an agent's program wrote to its error output for a terminal, and the codes showed as [31m and the like.
    /// </summary>
    public static string WithoutEscapes(string text) =>
        text.Contains('\u001b') ? EscapeCodes().Replace(text, string.Empty) : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"\u001b(\[[0-?]*[ -/]*[@-~]|\][^\u0007\u001b]*(\u0007|\u001b\\)|[@-Z\\-_])")]
    private static partial System.Text.RegularExpressions.Regex EscapeCodes();

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
        // Looked for back past what was asked since, not only in this turn. A subagent sent off in the background goes on
        // after its call came back, and a message typed to the agent meanwhile started a new turn as far as the rows go,
        // so every step it took after that was let go of and it was nowhere to be seen
        if ((ToolOfTurn(id) ?? entries.FindLast(entry => entry.Kind == HexAgentEntryKind.Tool && entry.ToolId == id)) is not { } tool)
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

    /// <summary>
    /// The agent's context compacted, what it held before and after in tokens (0 for what it didn't say) and whether it
    /// did it by itself or was asked. A quiet row says so, since the agent forgets the detail of what went before.
    /// </summary>
    public HexAgentEntry Compacted(string trigger, long before, long after)
    {
        Compactions++;
        CompactedFrom = before;
        CompactedTo = after;
        if (after > 0)
            ContextUsed = after;
        return Note(HexAgentLimits.CompactionNote(trigger, before, after));
    }

    /// <summary>
    /// A call to the model tried again, the how manyth try of how many, how long it waits first and why. While it keeps
    /// trying the same row says the latest try, so a run of retries is one row and not a column of them.
    /// </summary>
    public HexAgentEntry Retry(int attempt, int most, double delaySeconds, string why)
    {
        Retries++;
        string said = HexAgentLimits.RetryNote(attempt, most, delaySeconds, why);
        if (retrying is not null && entries.Count > 0 && ReferenceEquals(entries[^1], retrying))
        {
            retrying.Text = said;
            Touch(retrying);
            return retrying;
        }

        return retrying = Note(said);
    }

    /// <summary>
    /// Where the agent stands against a limit as it said it. A row only when that comes to something the person watching
    /// needs to know, close to the limit or over it, or back under it after being over, never for every turn's all clear.
    /// </summary>
    public HexAgentEntry? Limited(HexAgentLimit limit)
    {
        HexAgentLimit? was = Limit;
        Limit = limit;
        limits[limit.Kind.Length > 0 ? limit.Kind : "usage"] = limit;
        Moved();
        bool worse = limit.State != HexAgentLimitState.Ok && (was is null || was.State < limit.State || was.Kind != limit.Kind);
        bool better = was is { State: HexAgentLimitState.Hit } && limit.State == HexAgentLimitState.Ok;
        if (!worse && !better)
            return null;
        string said = HexAgentLimits.LimitNote(limit, DateTimeOffset.Now);
        return limit.State == HexAgentLimitState.Hit ? Error(said) : Note(said);
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
