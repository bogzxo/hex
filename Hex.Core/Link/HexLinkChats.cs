using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// What a link last sent of a chat to one device: which rows (by the row itself and its version), how many had been let
/// go of off the top, how many times rows were taken out of the middle, and a count of what was sent so the other end
/// can tell a patch went missing. Kept by the host per device and chat, only touched inside
/// <see cref="IHexLinkServices.Chat"/>, on the editor's thread, where the chat can't change under it.
/// </summary>
internal sealed class HexLinkChatMark
{
    public List<(HexAgentEntry Entry, int Version)> Rows { get; } = [];
    public int Dropped { get; set; }
    public int Reshaped { get; set; }
    public int Seq { get; set; } = -1;
    public string Head { get; set; } = string.Empty;
}

/// <summary>
/// A chat on the wire. It's a chat as <see cref="HexAgentChatStore"/> writes it down, so the watching side reads it back
/// into a <see cref="HexAgentChat"/> of its own and draws it as it draws its own. After the whole chat, only what
/// changed goes: the rows that are new or moved on (each with its index among the rows the chat keeps), how many it
/// keeps and has let go of, and its head (what it is and what it used) when that changed.
/// <para>
/// A row carries a little more than the store keeps, what's only of the moment: whether an approval or a question is
/// waiting, a question's choices, which request an approval is, and a message still queued.
/// </para>
/// </summary>
internal static class HexLinkChats
{
    // The most a mirrored row's text, input or output is let be, and how many rows a chat may say it has
    private const int MOST_TEXT = 256 * 1024, MOST_ROWS = HexAgentChat.MOST_ENTRIES + HexAgentChat.SPILL_BATCH + 50, MOST_CHOICES = 16;

    /// <summary>A chat's head as the link sends it: the store's, and whether it's running and how long its turn took.</summary>
    public static JsonObject Head(HexAgentChat chat)
    {
        JsonObject head = HexAgentChatStore.Head(chat);
        head["running"] = chat.Running;
        head["turn_seconds"] = chat.TurnSeconds;
        head["state"] = HexAgentHub.StateWord(chat);
        head["version"] = chat.Version;
        return head;
    }

    /// <summary>A row as the link sends it, the store's and what's of the moment.</summary>
    public static JsonObject Row(HexAgentChat chat, HexAgentEntry entry, int index)
    {
        JsonObject row = HexAgentChatStore.RowOf(entry);
        row["index"] = index;
        if (HexAgentChat.IsPending(entry))
            row["pending"] = true;
        if (entry.Queued)
            row["queued"] = true;
        if (entry.Unheard)
            row["unheard"] = true;
        if (entry.Kind == HexAgentEntryKind.Question)
        {
            if (entry.Choices is { } choices)
                row["choices"] = new JsonArray([.. choices.Select(choice => (JsonNode)choice)]);
            row["recommended"] = entry.Recommended;
            row["multiple"] = entry.Multiple;
            if (entry.Deadline is { } deadline)
                row["deadline_in"] = Math.Max(0, (deadline - Environment.TickCount64) / 1000.0);
        }

        if (entry.Kind == HexAgentEntryKind.Approval)
        {
            // Its number as hex_agent_approve counts it, the approvals among the rows kept up to it
            row["request"] = HexAgentHub.RequestNumber(chat, entry);
        }

        return row;
    }

    /// <summary>
    /// The whole chat, every row, and <paramref name="mark"/> made to say all of it was sent. On the editor's thread.
    /// </summary>
    public static JsonObject Whole(HexAgentChat chat, HexLinkChatMark mark)
    {
        JsonObject whole = Head(chat);
        var rows = new JsonArray();
        mark.Rows.Clear();
        for (int i = 0; i < chat.Entries.Count; i++)
        {
            HexAgentEntry entry = chat.Entries[i];
            rows.Add((JsonNode)Row(chat, entry, i));
            mark.Rows.Add((entry, entry.Version));
        }

        mark.Dropped = chat.Dropped;
        mark.Reshaped = chat.Reshaped;
        mark.Head = HeadKey(whole);
        // Counted on from what was sent before, so the other end never takes something older for something newer
        mark.Seq++;
        whole["rows"] = rows;
        whole["count"] = chat.Entries.Count;
        whole["seq"] = mark.Seq;
        whole["whole"] = true;
        return whole;
    }

    /// <summary>
    /// What changed in a chat since <paramref name="mark"/>, and the mark moved on: a patch, null when nothing did, or the
    /// whole chat when the rows can't be patched (one was taken out of the middle). On the editor's thread.
    /// </summary>
    public static JsonObject? Patch(HexAgentChat chat, HexLinkChatMark mark)
    {
        if (chat.Reshaped != mark.Reshaped || chat.Dropped < mark.Dropped)
            return Whole(chat, mark);

        int off = Math.Min(chat.Dropped - mark.Dropped, mark.Rows.Count);
        mark.Rows.RemoveRange(0, off);
        var rows = new JsonArray();
        for (int i = 0; i < chat.Entries.Count; i++)
        {
            HexAgentEntry entry = chat.Entries[i];
            if (i < mark.Rows.Count && ReferenceEquals(mark.Rows[i].Entry, entry) && mark.Rows[i].Version == entry.Version)
                continue;
            rows.Add((JsonNode)Row(chat, entry, i));
            if (i < mark.Rows.Count)
                mark.Rows[i] = (entry, entry.Version);
            else
                mark.Rows.Add((entry, entry.Version));
        }

        if (mark.Rows.Count > chat.Entries.Count)
            mark.Rows.RemoveRange(chat.Entries.Count, mark.Rows.Count - chat.Entries.Count);

        JsonObject head = Head(chat);
        string key = HeadKey(head);
        bool headMoved = key != mark.Head;
        if (rows.Count == 0 && !headMoved && chat.Dropped == mark.Dropped)
            return null;

        mark.Head = key;
        mark.Dropped = chat.Dropped;
        mark.Seq++;
        return new JsonObject
        {
            ["id"] = chat.Id,
            ["seq"] = mark.Seq,
            ["dropped"] = chat.Dropped,
            ["count"] = chat.Entries.Count,
            ["head"] = head,
            ["rows"] = rows
        };
    }

    // A head as a string to see whether it moved, its version left out (that moves with every row)
    private static string HeadKey(JsonObject head)
    {
        var copy = (JsonObject)head.DeepClone();
        copy.Remove("version");
        copy.Remove("rows");
        copy.Remove("count");
        copy.Remove("seq");
        copy.Remove("whole");
        return copy.ToJsonString();
    }

    /// <summary>
    /// A row as another machine sent it, read field by field with limits, null for one that isn't a row. A waiting
    /// approval or question gets <paramref name="approve"/> or <paramref name="answer"/> as what's done with the answer
    /// given here, so the mirrored card's buttons work as a local one's do.
    /// </summary>
    public static HexAgentEntry? EntryOf(JsonObject row, Action<int, bool>? approve, Action<string?>? answer)
    {
        if (!Enum.TryParse(HexLinkData.String(row, "kind", 20), out HexAgentEntryKind kind) || !Enum.IsDefined(kind))
            return null;

        long now = Environment.TickCount64;
        double seconds = Math.Clamp(HexLinkData.Double(row, "seconds") ?? 0, 0, 1e7);
        bool pending = HexLinkData.Bool(row, "pending") == true;
        string[]? choices = kind == HexAgentEntryKind.Question ? [.. HexLinkData.Strings(row, "choices", MOST_CHOICES, 2000)] : null;
        int? recommended = HexLinkData.Int(row, "recommended") is { } pick && choices is not null && pick >= 0 && pick < choices.Length ? pick : null;
        double? deadlineIn = HexLinkData.Double(row, "deadline_in");
        var state = Enum.TryParse(HexLinkData.String(row, "state", 20), out HexAgentToolState said) && Enum.IsDefined(said) ? said : HexAgentToolState.Done;
        var entry = new HexAgentEntry(kind, HexLinkData.String(row, "text", MOST_TEXT) ?? string.Empty)
        {
            Started = now - (long)(seconds * 1000),
            Ended = state == HexAgentToolState.Running || pending ? null : now,
            AskedText = HexLinkData.String(row, "asked", MOST_TEXT),
            ContextLocation = HexLinkData.String(row, "at", 2000),
            ToolId = HexLinkData.String(row, "tool_id", 200),
            ToolName = HexLinkData.String(row, "tool", 200),
            Input = HexLinkData.String(row, "input", MOST_TEXT),
            Output = HexLinkData.String(row, "output", MOST_TEXT),
            State = state,
            Progress = HexLinkData.String(row, "progress", 2000),
            Steps = Math.Max(0, HexLinkData.Int(row, "steps") ?? 0),
            Queued = HexLinkData.Bool(row, "queued") == true,
            Unheard = HexLinkData.Bool(row, "unheard") == true,
            Choices = choices,
            Recommended = recommended,
            Multiple = HexLinkData.Bool(row, "multiple") == true,
            Deadline = deadlineIn is { } left ? now + (long)(Math.Clamp(left, 0, 1e7) * 1000) : null
        };

        if (pending && kind == HexAgentEntryKind.Approval && approve is not null)
        {
            int request = HexLinkData.Int(row, "request") ?? 0;
            entry.Answer = allowed => approve(request, allowed);
        }
        else if (pending && kind == HexAgentEntryKind.Question && answer is not null)
        {
            entry.Respond = answer;
        }

        return entry;
    }

    /// <summary>
    /// The rows of a whole chat or a patch read, each with its index, at most as many as a chat keeps; false when one
    /// isn't a row or the count is beyond reason.
    /// </summary>
    public static bool RowsOf(JsonObject said, Action<int, bool>? approve, Func<HexAgentEntry, Action<string?>>? answer, out List<(int Index, HexAgentEntry Entry)> rows)
    {
        rows = [];
        if (said["rows"] is not JsonArray array || array.Count > MOST_ROWS)
            return false;
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject row || HexLinkData.Int(row, "index") is not { } index || index < 0 || index >= MOST_ROWS)
                return false;
            HexAgentEntry? made = null;
            Action<string?>? respond = null;
            // What answers a question is made knowing its row, the row is made knowing what answers it
            made = EntryOf(row, approve, answer is null ? null : said => respond?.Invoke(said));
            if (made is null)
                return false;
            if (answer is not null)
                respond = answer(made);
            rows.Add((index, made));
        }

        return true;
    }

    /// <summary>How many rows a chat says it keeps, within reason, null when it's beyond.</summary>
    public static int? CountOf(JsonObject said) => HexLinkData.Int(said, "count") is { } count && count >= 0 && count <= MOST_ROWS ? count : null;
}
