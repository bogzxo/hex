using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexDocParticipant(int Author, string Device, string Name, HexDocPoint? Caret = null,
    HexDocPoint? Anchor = null, HexDocPoint? Pointer = null);
internal sealed record HexDocExchange(int Author, HexDocReplay Replay, IReadOnlyList<HexDocParticipant> Participants,
    bool CanEdit = true, HexDocSaved? Saved = null);

/// <summary>One document and the authenticated members who may exchange operations with its authority.</summary>
internal sealed partial class HexLinkDocumentRoom(string path, string text, HexDocFileBasis? initialFile = null)
{
    private const int MaxParticipants = 65;
    private readonly Lock gate = new();
    private readonly Dictionary<string, HexDocParticipant> members = new(StringComparer.Ordinal);
    private readonly HexLinkDocumentAuthority authority = new(text);
    private readonly Dictionary<string, object> online = new(StringComparer.Ordinal);
    private IReadOnlyList<HexDocParticipant>? roster;
    private int lastAuthor;
    private HexDocFileBasis? fileBasis = initialFile;
    public string Path { get; } = path;

    public HexDocView View() => authority.View();

    public HexDocExchange Join(string scope, object connection, HexLinkDevice device, Guid? epoch = null, long revision = 0)
    {
        lock (gate)
        {
            if (!members.TryGetValue(scope, out var participant))
            {
                if (members.Count - agentUntil.Count >= MaxParticipants)
                    throw new InvalidOperationException("The shared document has reached its participant limit.");
                participant = new HexDocParticipant(++lastAuthor, device.Id, device.Name);
                members.Add(scope, participant);
            }

            bool joined = !online.TryGetValue(scope, out var previous) || previous != connection;
            online[scope] = connection;
            authority.Reconnect(participant.Author, resetAcknowledgement: joined);
            if (joined)
                roster = null;
            return State(participant.Author, epoch, revision);
        }
    }

    public HexDocExchange Exchange(string scope, object connection, Guid epoch, long revision, IReadOnlyList<HexDocOperation> operations,
        HexDocPoint? caret, HexDocPoint? anchor, HexDocPoint? pointer, bool checkpointReady)
    {
        lock (gate)
        {
            if (online.GetValueOrDefault(scope) != connection || !members.TryGetValue(scope, out var participant))
                throw HexLinkException.NotFound("Join the shared document first.");

            if (epoch == authority.Checkpoint.Epoch)
            {
                if (operations.Any(operation => operation.Id.Author != participant.Author))
                    throw new InvalidOperationException("A device may submit only its own edits.");
                authority.Acknowledge(epoch, participant.Author, revision, checkpointReady: false);
                foreach (HexDocOperation operation in operations)
                {
                    HexDocReceipt receipt = authority.Submit(epoch, participant.Author, operation);
                    if (receipt.Outcome == HexDocApply.MissingDependency)
                        throw new InvalidOperationException("An earlier document operation is missing. Retry from the acknowledged revision.");
                }

                authority.Acknowledge(epoch, participant.Author, revision, checkpointReady && operations.Count == 0);
                if ((participant.Caret, participant.Anchor, participant.Pointer) != (caret, anchor, pointer))
                {
                    members[scope] = participant with { Caret = caret, Anchor = anchor, Pointer = pointer };
                    roster = null;
                }
                if (checkpointReady && revision - authority.Checkpoint.Revision >= 2048
                    && authority.View().Text.Length <= HexLinkDocumentWire.MaxText
                    && authority.Compact() is { } checkpoint)
                {
                    if (saved is { } receipt && receipt.Revision == checkpoint.Revision)
                        saved = receipt with { Epoch = checkpoint.Epoch };
                    foreach (string member in members.Keys.ToArray())
                        members[member] = members[member] with { Caret = null, Anchor = null, Pointer = null };
                    roster = null;
                }
            }

            return State(participant.Author, epoch, revision);
        }
    }

    public void Disconnect(string scope, object connection)
    {
        lock (gate)
        {
            if (online.GetValueOrDefault(scope) != connection)
                return;
            online.Remove(scope);
            roster = null;
            if (members.TryGetValue(scope, out var participant))
            {
                authority.Disconnect(participant.Author);
                members[scope] = participant with { Caret = null, Anchor = null, Pointer = null };
            }
        }
    }

    public void Disconnect(object connection)
    {
        lock (gate)
        {
            foreach (string scope in online.Where(each => each.Value == connection).Select(each => each.Key).ToArray())
                Disconnect(scope, connection);
        }
    }

    public void Leave(string scope, object connection)
    {
        lock (gate)
        {
            if (online.GetValueOrDefault(scope) != connection)
                return;
            online.Remove(scope);
            roster = null;
            agentUntil.Remove(scope);
            if (members.Remove(scope, out var participant))
                authority.Leave(participant.Author);
        }
    }

    private HexDocExchange State(int author, Guid? epoch, long revision)
    {
        ExpireAgents();
        return new(author, authority.Replay(epoch, revision, budget: HexLinkDocumentWire.Budget),
            roster ??= [.. online.Keys.Select(scope => members[scope]).OrderBy(each => each.Author)], Saved: saved);
    }
}

internal sealed class HexLinkDocumentRooms
{
    private const int MaxRooms = 8;
    private readonly Lock gate = new();
    private readonly Dictionary<string, HexLinkDocumentRoom> rooms = new(PlatformPaths.Comparer);

    public HexLinkDocumentRoom Open(string path, string seed, HexDocFileBasis? file = null)
    {
        if (seed.Length > HexLinkDocumentWire.MaxText)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, "The shared document exceeds its size limit.");
        lock (gate)
        {
            if (rooms.TryGetValue(path, out var existing))
                return existing;
            if (rooms.Count >= MaxRooms)
                throw new InvalidOperationException("Too many shared documents are open.");
            var room = new HexLinkDocumentRoom(path, seed, file);
            rooms.Add(path, room);
            return room;
        }
    }

    public HexLinkDocumentRoom? TryFind(string path)
    {
        lock (gate)
            return rooms.GetValueOrDefault(path);
    }

    public HexLinkDocumentRoom Find(string path) =>
        TryFind(path) ?? throw HexLinkException.NotFound("This document is not shared.");

    public bool Close(string path)
    {
        lock (gate)
            return rooms.Remove(path);
    }

    public void Disconnect(object connection)
    {
        lock (gate)
            foreach (HexLinkDocumentRoom room in rooms.Values)
                room.Disconnect(connection);
    }
}

internal static class HexLinkDocumentWire
{
    internal const int MaxBatch = 128;
    internal const int MaxText = 2 * 1024 * 1024;
    internal const int Budget = 14 * 1024 * 1024;
    private const int MaxPeople = 97;

    internal static JsonObject Open(string path, Guid? epoch, long revision) =>
        new() { ["path"] = path, ["epoch"] = epoch?.ToString("N"), ["revision"] = revision };

    internal static JsonObject Exchange(string path, Guid epoch, long revision, IReadOnlyList<HexDocOperation> operations,
        HexDocPoint? caret, HexDocPoint? anchor, HexDocPoint? pointer, bool checkpointReady)
    {
        JsonObject request = Open(path, epoch, revision);
        request["ops"] = new JsonArray([.. operations.Select(EncodeOperation)]);
        request["caret"] = EncodePoint(caret);
        request["anchor"] = EncodePoint(anchor);
        request["pointer"] = EncodePoint(pointer);
        request["ready"] = checkpointReady;
        return request;
    }

    internal static JsonObject Encode(HexDocExchange reply)
    {
        HexDocReplay replay = reply.Replay;
        return new JsonObject
        {
            ["author"] = reply.Author, ["epoch"] = replay.Epoch.ToString("N"), ["revision"] = replay.Since,
            ["head"] = replay.Head, ["checkpoint"] = replay.Checkpoint?.Text,
            ["ops"] = new JsonArray([.. replay.Operations.Select(EncodeOperation)]),
            ["participants"] = new JsonArray([.. reply.Participants.Select(EncodeParticipant)]), ["editable"] = reply.CanEdit,
            ["saved"] = reply.Saved?.Encode()
        };
    }

    internal static HexDocExchange Decode(JsonObject message)
    {
        Guid epoch = Epoch(message) ?? throw HexLinkException.Invalid("Missing document epoch.");
        long since = Revision(message);
        long head = HexLinkData.Long(message, "head") ?? -1;
        int author = HexLinkData.Int(message, "author") ?? 0;
        HexDocOperation[] ops = DecodeOperations(message, 256);
        if (author < 1 || head < since || head - since < ops.Length)
            throw HexLinkException.Invalid("Invalid document replay.");
        HexDocCheckpoint? checkpoint = message["checkpoint"] is not null
            ? new HexDocCheckpoint(epoch, since, HexLinkDocumentProtocol.Field(message, "checkpoint", MaxText))
            : null;
        HexDocParticipant[] participants = [.. BoundedArray(message, "participants", MaxPeople)
            .Select(node => DecodeParticipant(node as JsonObject ?? throw HexLinkException.Invalid("Invalid document participant.")))];
        return new HexDocExchange(author, new HexDocReplay(epoch, since, head, checkpoint, ops), participants,
            HexLinkData.Bool(message, "editable") == true,
            HexLinkData.Object(message, "saved") is { } saved ? HexDocSaved.Decode(saved) : null);
    }

    internal static Guid? Epoch(JsonObject message)
    {
        if (message["epoch"] is null)
            return null;
        string raw = HexLinkDocumentProtocol.Field(message, "epoch", 32);
        return Guid.TryParseExact(raw, "N", out Guid epoch) && epoch != Guid.Empty ? epoch
            : throw HexLinkException.Invalid("Invalid document epoch.");
    }

    internal static long Revision(JsonObject message) => HexLinkDocumentProtocol.Revision(message);

    internal static HexDocOperation[] DecodeOperations(JsonObject message, int limit = MaxBatch) =>
        [.. BoundedArray(message, "ops", limit).Select(node => DecodeOperation(node as JsonObject
            ?? throw HexLinkException.Invalid("Invalid document operation.")))];

    private static JsonNode EncodeOperation(HexDocOperation operation) => operation switch
    {
        HexDocEdit edit => new JsonObject
        {
            ["id"] = EncodeId(edit.Id), ["after"] = EncodePoint(edit.After), ["text"] = edit.Inserted,
            ["remove"] = new JsonArray([.. edit.Removed.Select(span =>
                (JsonNode)new JsonArray(span.Run.Clock, span.Run.Author, span.Start, span.Length))])
        },
        HexDocVisibility visibility => new JsonObject
        {
            ["id"] = EncodeId(visibility.Id), ["edit"] = EncodeId(visibility.Edit), ["enabled"] = visibility.Enabled
        },
        _ => throw new ArgumentException("Unknown shared document operation.", nameof(operation))
    };

    private static HexDocOperation DecodeOperation(JsonObject node)
    {
        HexDocId id = DecodeId(node["id"], 2);
        if (node.ContainsKey("edit"))
            return new HexDocVisibility(id, DecodeId(node["edit"], 2),
                HexLinkData.Bool(node, "enabled") ?? throw HexLinkException.Invalid("Missing undo state."));
        HexDocPoint after = DecodePoint(node["after"]) ?? throw HexLinkException.Invalid("Missing insertion boundary.");
        string inserted = HexLinkDocumentProtocol.Field(node, "text", MaxText);
        HexDocSpan[] removed = [.. BoundedArray(node, "remove", 8192).Select(DecodeSpan)];
        return new HexDocEdit(id, after, inserted, removed);
    }

    private static HexDocSpan DecodeSpan(JsonNode? node)
    {
        HexDocId id = DecodeId(node, 4);
        var range = (JsonArray)node!;
        return new HexDocSpan(id, Number<int>(range[2]), Number<int>(range[3]));
    }

    private static JsonNode EncodeParticipant(HexDocParticipant participant) => new JsonObject
    {
        ["author"] = participant.Author, ["device"] = participant.Device, ["name"] = participant.Name,
        ["caret"] = EncodePoint(participant.Caret), ["anchor"] = EncodePoint(participant.Anchor), ["pointer"] = EncodePoint(participant.Pointer)
    };

    private static HexDocParticipant DecodeParticipant(JsonObject json) => new(
        HexLinkData.Int(json, "author") ?? throw HexLinkException.Invalid("Invalid document participant id."),
        HexLinkDocumentProtocol.Field(json, "device", 80), HexLinkDocumentProtocol.Field(json, "name", 80),
        DecodePoint(json["caret"]), DecodePoint(json["anchor"]), DecodePoint(json["pointer"]));

    private static JsonArray EncodeId(HexDocId id) => new(id.Clock, id.Author);

    private static HexDocId DecodeId(JsonNode? node, int fields)
    {
        if (node is not JsonArray array || array.Count != fields)
            throw HexLinkException.Invalid("Invalid document operation identity.");
        return new HexDocId(Number<long>(array[0]), Number<int>(array[1]));
    }

    private static JsonNode? EncodePoint(HexDocPoint? point) =>
        point is { } value ? new JsonArray(value.Run.Clock, value.Run.Author, value.Offset) : null;

    internal static HexDocPoint? DecodePoint(JsonNode? node)
    {
        if (node is null)
            return null;
        HexDocId id = DecodeId(node, 3);
        int offset = Number<int>(((JsonArray)node)[2]);
        if (offset < 0)
            throw HexLinkException.Invalid("Invalid document position.");
        return new HexDocPoint(id, offset);
    }

    private static T Number<T>(JsonNode? value) where T : struct =>
        value is JsonValue json && json.TryGetValue<T>(out T number) ? number
            : throw HexLinkException.Invalid("Invalid document position number.");

    private static JsonArray BoundedArray(JsonObject node, string key, int limit) =>
        node[key] is JsonArray array && array.Count <= limit ? array
            : throw HexLinkException.Invalid($"Invalid document {key} array.");
}
