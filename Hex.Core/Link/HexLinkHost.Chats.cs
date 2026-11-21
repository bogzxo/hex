using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    // A chat the device has open: what was last sent of it, and what sends what changed at most so often
    private sealed class WatchedChat(int id, HexLinkCoalesce coalesce)
    {
        public int Id { get; } = id;
        public HexLinkChatMark Mark { get; } = new();
        public HexLinkCoalesce Coalesce { get; } = coalesce;
    }

    /// <summary>The most chats one device may have open at once.</summary>
    public const int MOST_WATCHED = 32;

    private const int MOST_GET_MARKS = 24;
    private readonly Dictionary<(int Id, int Version), HexLinkChatMark> fetchedChats = [];
    private readonly Queue<(int Id, int Version)> fetchedOrder = new();

    // A chat whole, or a patch from a version previously sent to this device.
    private async Task<JsonNode?> ChatGet(JsonObject parameters)
    {
        Need(HexLinkRights.View, "chat/get");
        int id = HexLinkData.NeedInt(parameters, "id");
        int? since = HexLinkData.Int(parameters, "since");
        JsonObject? said = await services.Chat(id, chat =>
        {
            lock (fetchedChats)
            {
                if (since == chat.Version)
                    return new JsonObject { ["id"] = chat.Id, ["version"] = chat.Version, ["unchanged"] = true };

                HexLinkChatMark mark = since is { } known && fetchedChats.TryGetValue((id, known), out HexLinkChatMark? prior)
                    ? CopyMark(prior) : new HexLinkChatMark();
                JsonObject answer = mark.Seq < 0
                    ? HexLinkChats.Whole(chat, mark)
                    : HexLinkChats.Patch(chat, mark) ?? new JsonObject
                    {
                        ["id"] = id, ["count"] = chat.Entries.Count, ["dropped"] = chat.Dropped, ["rows"] = new JsonArray()
                    };
                answer["version"] = chat.Version;
                if (mark.Seq >= 0 && HexLinkData.Bool(answer, "whole") != true)
                    answer["since"] = since;
                RememberFetchedLocked(id, chat.Version, mark);
                return answer;
            }
        });
        return said ?? throw HexLinkException.NotFound($"There's no chat #{id}.");
    }

    private static HexLinkChatMark CopyMark(HexLinkChatMark prior)
    {
        var copy = new HexLinkChatMark { Dropped = prior.Dropped, Reshaped = prior.Reshaped, Seq = prior.Seq, Head = prior.Head };
        copy.Rows.AddRange(prior.Rows);
        return copy;
    }

    private void RememberFetchedLocked(int id, int version, HexLinkChatMark mark)
    {
        var key = (id, version);
        if (fetchedChats.TryAdd(key, mark))
        {
            fetchedOrder.Enqueue(key);
            if (fetchedOrder.Count > MOST_GET_MARKS)
                fetchedChats.Remove(fetchedOrder.Dequeue());
        }
    }

    private void ForgetFetchedChats()
    {
        lock (fetchedChats)
        {
            fetchedChats.Clear();
            fetchedOrder.Clear();
        }
    }

    // A chat whole, and what changes of it from now on sent as chat/changed
    private async Task<JsonNode?> ChatWatch(JsonObject parameters)
    {
        Need(HexLinkRights.View, "chat/watch");
        int id = HexLinkData.NeedInt(parameters, "id");
        if (!chats.TryGetValue(id, out WatchedChat? watched))
        {
            if (chats.Count >= MOST_WATCHED)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A device may have {MOST_WATCHED} chats open at once.");
            var made = new WatchedChat(id, new HexLinkCoalesce(CHAT_GAP, () => SendChat(id)));
            watched = chats.GetOrAdd(id, made);
            if (watched != made)
                made.Coalesce.Dispose();
        }

        JsonObject? whole = await services.Chat(id, chat => HexLinkChats.Whole(chat, watched.Mark));
        if (whole is null)
        {
            if (chats.TryRemove(id, out WatchedChat? gone))
                gone.Coalesce.Dispose();
            throw HexLinkException.NotFound($"There's no chat #{id}.");
        }

        return whole;
    }

    private JsonObject ChatUnwatch(JsonObject parameters)
    {
        int id = HexLinkData.NeedInt(parameters, "id");
        if (chats.TryRemove(id, out WatchedChat? watched))
            watched.Coalesce.Dispose();
        return [];
    }

    /// <summary>A chat moved (Dawn calls it as the hub pumps), what changed sent on when the device has it open.</summary>
    public void ChatMoved(int id)
    {
        if (chats.TryGetValue(id, out WatchedChat? watched))
            watched.Coalesce.Poke();
    }

    // What changed of an open chat since it was last sent, as chat/changed; a chat that's gone said so and let go of
    private async Task SendChat(int id)
    {
        if (!chats.TryGetValue(id, out WatchedChat? watched) || !HexLinkAllowed.Has(Rights, HexLinkRights.View))
            return;

        // Nothing changed said as an empty object, so null is only ever no such chat
        JsonObject? patch = await services.Chat(id, chat => HexLinkChats.Patch(chat, watched.Mark) ?? []);
        if (patch is null)
        {
            if (chats.TryRemove(id, out WatchedChat? gone))
            {
                gone.Coalesce.Dispose();
                session.Tell("chat/changed", new JsonObject { ["id"] = id, ["gone"] = true });
            }

            return;
        }

        if (patch.Count > 0)
            session.Tell("chat/changed", patch);
    }

    // A question card answered as the person would
    private async Task<JsonNode?> ChatAnswer(JsonObject parameters)
    {
        Need(HexLinkRights.Steer, "chat/answer");
        int id = HexLinkData.NeedInt(parameters, "id");
        string answer = HexLinkData.Need(parameters, "answer", 8000);
        services.Audit("steer", Device, $"chat/answer #{id} {Cut(answer, 200)}");
        return new JsonObject { ["answered"] = await services.Answer(id, answer) };
    }
}
