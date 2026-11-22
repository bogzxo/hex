using System.Text.Json.Nodes;

namespace Hex.Core;

internal static class HexLinkDocumentProtocol
{
    internal static string Field(JsonObject message, string key, int limit)
    {
        string value = HexLinkData.Need(message, key, limit + 1);
        if (value.Length > limit)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"Shared document field {key} exceeds its limit.");
        return value;
    }

    internal static long Revision(JsonObject message) => message["revision"] is JsonValue value
        && value.TryGetValue<long>(out long revision) && revision >= 0 ? revision
        : throw HexLinkException.Invalid("Expected a nonnegative document revision.");

    internal static JsonObject Encode(HexLinkDocSnapshot snapshot) => new()
    {
        ["path"] = snapshot.Path, ["session"] = snapshot.Session, ["revision"] = snapshot.Revision,
        ["text"] = snapshot.Text, ["author"] = snapshot.Author, ["conflict"] = snapshot.Conflict
    };

    internal static HexLinkDocSnapshot Decode(JsonObject message, string path)
    {
        if (Field(message, "path", HexLinkFiles.MOST_PATH) != path)
            throw HexLinkException.Invalid("Unexpected document path.");
        string session = Field(message, "session", 32);
        if (!Guid.TryParseExact(session, "N", out _))
            throw HexLinkException.Invalid("Invalid document session.");
        return new(path, session, Revision(message), Field(message, "text", HexLinkDocuments.MOST_TEXT),
            Field(message, "author", 80), HexLinkData.Bool(message, "conflict") ?? throw HexLinkException.Invalid("Missing conflict flag."));
    }
}

internal sealed partial class HexLinkHost
{
    private async Task<JsonNode?> Document2Request(string method, JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, method);
        string path = HexLinkDocumentProtocol.Field(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = ResolveFile(path);
        var rooms = services.Documents?.Rooms ?? throw HexLinkException.NotFound("No documents are shared.");
        bool canEdit = HexLinkAllowed.Has(Rights, HexLinkRights.Edit);
        HexLinkDocumentRoom room = method == "doc2/open"
            ? await services.OpenSharedDocument(path, full, Device, HexLinkData.Bool(parameters, "automatic") == true, cancel)
            : rooms.Find(path);
        Need(HexLinkRights.View, method);
        string scope = Device.Id;
        var author = Device;
        if (HexLinkData.Object(parameters, "relay") is { } envelope && HexLinkData.String(envelope, "origin", 64) is { } origin)
        {
            scope = Device.Id + "/" + origin;
            author = new HexLinkDevice(origin, HexLinkNames.Clean(HexLinkData.String(envelope, "name", 80)) ?? origin[..8]);
        }

        try
        {
            if (method == "doc2/leave")
            {
                room.Leave(scope, this);
                return new JsonObject();
            }

            Guid? epoch = HexLinkDocumentWire.Epoch(parameters);
            long revision = HexLinkDocumentWire.Revision(parameters);
            if (method == "doc2/open")
                return HexLinkDocumentWire.Encode(room.Join(scope, this, author, epoch, revision) with { CanEdit = canEdit });

            HexDocOperation[] operations = HexLinkDocumentWire.DecodeOperations(parameters);
            if (operations.Length > 0)
                Need(HexLinkRights.Edit, method);
            if (epoch is null)
                throw HexLinkException.Invalid("The shared document operation needs an epoch.");
            return HexLinkDocumentWire.Encode(room.Exchange(scope, this, epoch.Value, revision, operations,
                HexLinkDocumentWire.DecodePoint(parameters["caret"]), HexLinkDocumentWire.DecodePoint(parameters["anchor"]),
                HexLinkDocumentWire.DecodePoint(parameters["pointer"]), HexLinkData.Bool(parameters, "ready") == true) with { CanEdit = canEdit });
        }
        catch (ArgumentException e)
        {
            throw HexLinkException.Invalid(e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new HexLinkException(HexLinkErrors.CONFLICT, e.Message);
        }
    }

    private async Task<JsonNode?> Document2Relay(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Steer, "doc2/relay");
        if (parameters.ContainsKey("relay"))
            throw HexLinkException.NotAllowed("one document relay hop");
        string target = HexLinkDocumentProtocol.Field(parameters, "target", 64);
        string method = HexLinkDocumentProtocol.Field(parameters, "method", 16);
        if (method is not ("doc2/open" or "doc2/exchange" or "doc2/leave") || target == Device.Id)
            throw HexLinkException.Invalid("Invalid document relay destination or method.");
        if (services.SharedDocumentPeer(target) is not { IsClosed: false } peer)
            throw HexLinkException.NotFound("The shared document's owner is offline.");
        var request = HexLinkData.Object(parameters, "document") ?? throw HexLinkException.Invalid("Missing document request.");
        if (request.ContainsKey("relay"))
            throw HexLinkException.NotAllowed("one document relay hop");

        bool canEdit = HexLinkAllowed.Has(Rights, HexLinkRights.Edit);
        if (method == "doc2/exchange" && request["ops"] is JsonArray { Count: > 0 })
            Need(HexLinkRights.Edit, "relay document edits");
        if (method != "doc2/exchange" || request["ops"] is JsonArray { Count: > 0 })
            services.Audit("relay", Device, $"{method} from {Device.Name} ({Device.Id}) to {peer.Device.Id}");

        var origin = HexLinkAllowed.RelayOrigin;
        HexLinkDevice? previous = origin.Value;
        try
        {
            origin.Value = Device;
            JsonObject reply = await peer.ForwardDocument(method, request, cancel);
            if (!canEdit && method != "doc2/leave")
                reply["editable"] = false;
            return reply;
        }
        finally { origin.Value = previous; }
    }

    private JsonObject DocumentRequest(string method, JsonObject parameters)
    {
        bool edit = method == "doc/edit";
        Need(edit ? HexLinkRights.Edit : HexLinkRights.View, method);
        var documents = services.Documents ?? throw new HexLinkException(HexLinkErrors.NOT_FOUND, "No documents are shared.");
        string path = HexLinkDocumentProtocol.Field(parameters, "path", HexLinkFiles.MOST_PATH);
        HexLinkFiles.Resolve(Folders, path);
        if (!edit)
        {
            var current = documents.Snapshot(path);
            if (method == "doc/poll" && HexLinkDocumentProtocol.Field(parameters, "session", 32) == current.Session
                && HexLinkDocumentProtocol.Revision(parameters) == current.Revision)
                return new JsonObject { ["unchanged"] = true };
            return HexLinkDocumentProtocol.Encode(current);
        }

        var snapshot = documents.Apply(path, HexLinkDocumentProtocol.Field(parameters, "session", 32),
            HexLinkDocumentProtocol.Revision(parameters), HexLinkDocumentProtocol.Field(parameters, "text", HexLinkDocuments.MOST_TEXT), Device);
        services.Audit("edit", Device, $"doc/edit {path}, revision {snapshot.Revision}, conflict {snapshot.Conflict}");
        return HexLinkDocumentProtocol.Encode(snapshot);
    }
}

internal sealed partial class HexLinkPeer
{
    public async Task<HexDocExchange> OpenDocument(string path, Guid? epoch = null, long revision = 0, CancellationToken cancel = default,
        bool automatic = false, string? relayTo = null)
    {
        var request = HexLinkDocumentWire.Open(path, epoch, revision);
        request["automatic"] = automatic;
        return HexLinkDocumentWire.Decode(await ForwardDocument("doc2/open", request, cancel, relayTo));
    }

    public async Task<HexDocExchange> ExchangeDocument(string path, Guid epoch, long revision, IReadOnlyList<HexDocOperation> operations,
        HexDocPoint? caret = null, HexDocPoint? anchor = null, HexDocPoint? pointer = null, bool checkpointReady = false,
        CancellationToken cancel = default, string? relayTo = null) =>
        HexLinkDocumentWire.Decode(await ForwardDocument("doc2/exchange",
            HexLinkDocumentWire.Exchange(path, epoch, revision, operations, caret, anchor, pointer, checkpointReady), cancel, relayTo));

    public Task LeaveDocument(string path, CancellationToken cancel = default, string? relayTo = null) =>
        ForwardDocument("doc2/leave", new JsonObject { ["path"] = path }, cancel, relayTo);

    internal Task<JsonObject> ForwardDocument(string method, JsonObject request, CancellationToken cancel, string? relayTo = null) =>
        relayTo is null ? AskObject(method, request, cancel) : AskObject("doc2/relay",
            new JsonObject { ["target"] = relayTo, ["method"] = method, ["document"] = request }, cancel);

    public async Task<HexLinkDocSnapshot> JoinDocument(string path, CancellationToken cancel = default) =>
        HexLinkDocumentProtocol.Decode(await AskObject("doc/join", new JsonObject { ["path"] = path }, cancel), path);

    public async Task<HexLinkDocSnapshot?> PollDocument(string path, string session, long revision, CancellationToken cancel = default)
    {
        var request = new JsonObject { ["path"] = path, ["session"] = session, ["revision"] = revision };
        var reply = await AskObject("doc/poll", request, cancel);
        return HexLinkData.Bool(reply, "unchanged") == true ? null : HexLinkDocumentProtocol.Decode(reply, path);
    }

    public async Task<HexLinkDocSnapshot> EditDocument(string path, string session, long revision, string text, CancellationToken cancel = default)
    {
        HexLinkDocuments.CheckText(text);
        var request = new JsonObject { ["path"] = path, ["session"] = session, ["revision"] = revision, ["text"] = text };
        return HexLinkDocumentProtocol.Decode(await AskObject("doc/edit", request, cancel), path);
    }
}
