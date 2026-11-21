using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    // A chat of the other machine's mirrored here: the chat once it's made, the last of what was sent of it that was
    // taken (seq), and what came since, waiting for the editor's thread
    private sealed class Mirror(int id)
    {
        public int Id { get; } = id;
        public HexAgentChat? Chat { get; set; }
        public int Seq { get; set; } = -1;
        public Queue<JsonObject> Queued { get; } = new();
        public bool Resyncing { get; set; }
        public string Plan { get; set; } = string.Empty;
        public bool Gone { get; set; }
    }

    private readonly Dictionary<int, Mirror> mirrors = [];

    // The most patches a chat holds before the rest are dropped and the whole chat asked for again
    private const int MOST_QUEUED = 256;

    /// <summary>
    /// A chat of the other machine opened: read whole into a <see cref="HexAgentChat"/> of this machine's and patched as
    /// it changes there, by <see cref="Pump"/> on the editor's thread. Never added to a HexAgentHub, so this machine's
    /// ids, runs and limits never see it. Its waiting approvals and questions answer over the link. Opening one that's
    /// open gives the same chat.
    /// </summary>
    public async Task<HexAgentChat> OpenChat(int id, CancellationToken cancel = default)
    {
        Mirror mirror;
        lock (mirrors)
        {
            if (mirrors.TryGetValue(id, out Mirror? open) && open.Chat is { } chat)
                return chat;
            mirror = open ?? (mirrors[id] = new Mirror(id));
        }

        try
        {
            JsonObject whole = await AskObject("chat/watch", new JsonObject { ["id"] = id }, cancel);
            HexAgentChat made = Made(whole, mirror) ?? throw new HexLinkException(HexLinkErrors.INVALID_REQUEST, $"The other Hex sent chat #{id} in a shape this one can't read.");
            lock (mirrors)
            {
                if (mirror.Chat is { } already)
                    return already;
                mirror.Chat = made;
                mirror.Seq = HexLinkData.Int(whole, "seq") ?? 0;
            }

            return made;
        }
        catch (Exception)
        {
            lock (mirrors)
            {
                if (mirror.Chat is null && mirrors.GetValueOrDefault(id) == mirror)
                    mirrors.Remove(id);
            }

            throw;
        }
    }

    /// <summary>A mirrored chat closed, the other machine told to stop sending what changes of it.</summary>
    public void CloseChat(int id)
    {
        lock (mirrors)
        {
            if (!mirrors.Remove(id))
                return;
        }

        _ = session.Ask("chat/unwatch", new JsonObject { ["id"] = id }).ContinueWith(static done => _ = done.Exception, TaskScheduler.Default);
    }

    /// <summary>The chats mirrored now, by their id on the other machine.</summary>
    public IReadOnlyDictionary<int, HexAgentChat> Chats
    {
        get
        {
            lock (mirrors)
                return mirrors.Values.Where(mirror => mirror.Chat is not null).ToDictionary(mirror => mirror.Id, mirror => mirror.Chat!);
        }
    }

    /// <summary>Whether a mirrored chat's chat went on the other machine (closed there), so it's only a record now.</summary>
    public bool IsGone(int id)
    {
        lock (mirrors)
            return mirrors.TryGetValue(id, out Mirror? mirror) && mirror.Gone;
    }

    // What changed of a chat, queued for the editor's thread, or dropped for a chat not open
    private void ChatChanged(JsonObject said)
    {
        if (HexLinkData.Int(said, "id") is not { } id)
            return;
        lock (mirrors)
        {
            if (!mirrors.TryGetValue(id, out Mirror? mirror))
                return;
            if (mirror.Queued.Count >= MOST_QUEUED)
            {
                mirror.Queued.Clear();
                mirror.Seq = int.MaxValue - 1;
                Resync(mirror);
                return;
            }

            mirror.Queued.Enqueue(said);
        }
    }

    /// <summary>
    /// What changed of the mirrored chats patched into them, on the editor's thread, every tick. True when one moved. A
    /// patch that doesn't follow on from the last (one went missing) or doesn't fit asks for the whole chat again.
    /// </summary>
    public bool Pump()
    {
        bool moved = false;
        Mirror[] all;
        lock (mirrors)
            all = [.. mirrors.Values];
        foreach (Mirror mirror in all)
        {
            while (true)
            {
                JsonObject said;
                HexAgentChat chat;
                lock (mirrors)
                {
                    if (mirror.Chat is null || !mirror.Queued.TryDequeue(out said!))
                        break;
                    chat = mirror.Chat;
                }

                moved |= Take(mirror, chat, said);
            }
        }

        if (moved)
            Changed?.Invoke();
        return moved;
    }

    // One thing said of a chat taken into its mirror, true when the chat moved
    private bool Take(Mirror mirror, HexAgentChat chat, JsonObject said)
    {
        if (HexLinkData.Bool(said, "gone") == true)
        {
            mirror.Gone = true;
            chat.MirrorHead(chat.Title, false, chat.TurnSeconds);
            return true;
        }

        if (HexLinkData.Int(said, "seq") is not { } seq || seq <= mirror.Seq)
            return false;

        bool whole = HexLinkData.Bool(said, "whole") == true;
        if (!whole && seq != mirror.Seq + 1)
        {
            Resync(mirror);
            return false;
        }

        bool fitted = whole ? Whole(chat, said, mirror) : Patched(chat, said, mirror);
        if (!fitted)
        {
            Resync(mirror);
            return false;
        }

        mirror.Seq = seq;
        return true;
    }

    // The whole chat asked for again, once at a time, what comes back queued like any other change
    private void Resync(Mirror mirror)
    {
        lock (mirrors)
        {
            if (mirror.Resyncing)
                return;
            mirror.Resyncing = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await session.Ask("chat/watch", new JsonObject { ["id"] = mirror.Id }) is JsonObject whole)
                {
                    lock (mirrors)
                    {
                        // Taken whatever seq it has, it's the newest there is
                        if (mirror.Seq == int.MaxValue - 1)
                            mirror.Seq = -1;
                        mirror.Queued.Enqueue(whole);
                    }
                }
            }
            catch (Exception)
            {
                // The link went, or the chat; the mirror stays as it was
            }
            finally
            {
                lock (mirrors)
                    mirror.Resyncing = false;
            }
        });
    }

    // A chat made from the whole of it as it was sent, null when it can't be read
    private HexAgentChat? Made(JsonObject whole, Mirror mirror)
    {
        // The head read the way a chat written down is, its rows apart, where a mirror's are read with limits
        var head = new JsonObject();
        foreach (var (key, value) in whole)
        {
            if (key != "rows")
                head[key] = value?.DeepClone();
        }

        HexAgentChat? chat;
        try
        {
            chat = HexAgentChatStore.Read(head);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException or KeyNotFoundException)
        {
            return null;
        }

        return chat is not null && Whole(chat, whole, mirror) ? chat : null;
    }

    // A whole chat taken into a mirror, every row replaced
    private bool Whole(HexAgentChat chat, JsonObject whole, Mirror mirror)
    {
        if (HexLinkChats.CountOf(whole) is not { } count || !RowsOf(mirror, whole, out var rows))
            return false;
        if (!chat.MirrorRows(Math.Max(0, HexLinkData.Int(whole, "dropped") ?? 0), count, rows, whole: true))
            return false;
        Head(chat, whole, mirror);
        return true;
    }

    // A patch taken into a mirror, the rows that moved replaced and the head's fields set
    private bool Patched(HexAgentChat chat, JsonObject patch, Mirror mirror)
    {
        if (HexLinkChats.CountOf(patch) is not { } count || !RowsOf(mirror, patch, out var rows))
            return false;
        if (!chat.MirrorRows(Math.Max(0, HexLinkData.Int(patch, "dropped") ?? 0), count, rows))
            return false;
        if (HexLinkData.Object(patch, "head") is { } head)
            Head(chat, head, mirror);
        else
            chat.Moved();
        return true;
    }

    // Rows read, a waiting card answering over the link
    private bool RowsOf(Mirror mirror, JsonObject said, out List<(int Index, HexAgentEntry Entry)> rows) =>
        HexLinkChats.RowsOf(said,
            (request, allow) => _ = Approve(mirror.Id, request, allow).ContinueWith(static done => _ = done.Exception, TaskScheduler.Default),
            entry => answer =>
            {
                if (answer is not null)
                    _ = Answer(mirror.Id, answer).ContinueWith(static done => _ = done.Exception, TaskScheduler.Default);
            },
            out rows);

    // What a chat is and what it used, as the other machine says, set on the mirror
    private static void Head(HexAgentChat chat, JsonObject head, Mirror mirror)
    {
        chat.Name = HexLinkData.String(head, "name", MOST_NAME);
        chat.Session = HexLinkData.String(head, "session", MOST_NAME);
        chat.Model = HexLinkData.String(head, "model", MOST_NAME);
        chat.InputTokens = Math.Max(0, HexLinkData.Long(head, "input_tokens") ?? 0);
        chat.OutputTokens = Math.Max(0, HexLinkData.Long(head, "output_tokens") ?? 0);
        chat.Cost = Math.Max(0, HexLinkData.Double(head, "cost") ?? 0);
        chat.ContextUsed = Math.Max(0, HexLinkData.Long(head, "context") ?? 0);
        chat.ContextLimit = Math.Max(0, HexLinkData.Long(head, "context_limit") ?? 0);
        chat.CacheRead = Math.Max(0, HexLinkData.Long(head, "cache_read") ?? 0);
        chat.Stopped = HexLinkData.Bool(head, "stopped") == true;
        chat.Settle(HexLinkData.Bool(head, "settled") == true);

        if (HexLinkData.Object(head, "options") is { } options)
        {
            chat.Options.Clear();
            foreach (var (key, _) in options.Take(64))
            {
                if (key.Length <= MOST_NAME && HexLinkData.String(options, key, 4000) is { } value)
                    chat.Options[key] = value;
            }
        }

        // The plan taken again only when it changed, a plan read is a few hundred objects
        if (HexLinkData.Object(head, "plan") is { } plan)
        {
            string key = plan.ToJsonString();
            if (key.Length <= 1024 * 1024 && key != mirror.Plan)
            {
                mirror.Plan = key;
                try
                {
                    chat.SetPlan(HexAgentPlan.FromJson(plan));
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
                {
                }
            }
        }

        chat.MirrorHead(HexLinkData.String(head, "title", 1000) ?? chat.Title, HexLinkData.Bool(head, "running") == true,
            Math.Max(0, HexLinkData.Double(head, "turn_seconds") ?? 0));
        chat.Moved();
    }
}
