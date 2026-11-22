using System.Text.Json.Nodes;

namespace Hex.Core;

internal readonly record struct HexLinkPublicationRequest(Guid Id, string Folder, long Epoch, bool Replica, bool Bypass)
{
    public static HexLinkPublicationRequest Read(JsonObject parameters)
    {
        if (!Guid.TryParseExact(HexLinkData.String(parameters, "id", 33), "N", out Guid id) || id == Guid.Empty ||
            HexLinkData.String(parameters, "folder", 201) is not { Length: > 0 and <= 200 } folder ||
            HexLinkData.Long(parameters, "epoch") is not long epoch || epoch < 1 ||
            HexLinkData.Bool(parameters, "replica") is not bool replica || HexLinkData.Bool(parameters, "bypass") is not bool bypass ||
            (replica && bypass))
            throw HexLinkException.Invalid("The publication request is invalid.");
        return new(id, folder, epoch, replica, bypass);
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id.ToString("N"), ["folder"] = Folder, ["epoch"] = Epoch, ["replica"] = Replica, ["bypass"] = Bypass
    };

    public bool Matches(HexLinkCommittedSet set) => set.Id == Id && set.Folder == Folder && set.Epoch == Epoch;
}

internal interface IHexLinkPublicationService
{
    string PublicationSpool { get; }
    void CheckPublication(HexLinkDevice device, HexLinkPublicationRequest request);
    Task ReceivePublication(HexLinkDevice device, HexLinkPublicationRequest request, HexLinkCommittedSet set,
        IReadOnlyList<HexLinkPublishEdit> edits, CancellationToken cancel);
    Task<JsonObject?> PublicationReceipt(HexLinkDevice device, HexLinkPublicationRequest request, CancellationToken cancel);
}

internal sealed partial class HexLinkHost
{
    private void RegisterPublicationReceiver() =>
        session.RegisterBulkReceiver(HexLinkPublicationPack.Purpose, HexLinkRights.Edit, OpenPublication);

    private ValueTask<IHexLinkBulkSink> OpenPublication(long length, JsonObject metadata, CancellationToken cancel)
    {
        if (length is < sizeof(int) or > HexLinkPublicationPack.MaximumBytes)
            throw HexLinkException.Invalid("The publication pack is outside the supported size.");
        IHexLinkPublicationService service = RequirePublication();
        var request = HexLinkPublicationRequest.Read(metadata);
        service.CheckPublication(Device, request);
        cancel.ThrowIfCancellationRequested();

        Directory.CreateDirectory(service.PublicationSpool);
        string temp = Path.Combine(service.PublicationSpool, Guid.NewGuid().ToString("N") + ".pack");
        var spool = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return ValueTask.FromResult<IHexLinkBulkSink>(new PublicationSink(spool, length, service, Device, request));
    }

    private async Task<JsonNode?> PublicationStatus(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "publish/status");
        if (parameters.ContainsKey("relay"))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Publication status requires the direct authenticated connection.");
        IHexLinkPublicationService service = RequirePublication();
        var request = HexLinkPublicationRequest.Read(parameters);
        service.CheckPublication(Device, request);
        return await service.PublicationReceipt(Device, request, cancel) ?? new JsonObject { ["present"] = false };
    }

    private IHexLinkPublicationService RequirePublication() => services.Publication ??
        throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "This device has not enabled checked publication.");

    private sealed class PublicationSink(FileStream spool, long length, IHexLinkPublicationService service,
        HexLinkDevice device, HexLinkPublicationRequest request) : IHexLinkBulkSink
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel) => spool.WriteAsync(data, cancel);

        public async ValueTask CommitAsync(CancellationToken cancel)
        {
            spool.Position = 0;
            var (set, edits) = await HexLinkPublicationPack.Read(spool, length, cancel);
            if (!request.Matches(set) || set.Kind != HexLinkFenceKind.Publication)
                throw HexLinkException.Invalid("The publication pack does not match the requested transaction.");
            service.CheckPublication(device, request);
            await service.ReceivePublication(device, request, set, edits, cancel);
        }

        public ValueTask DisposeAsync() => spool.DisposeAsync();
    }
}

internal sealed partial class HexLinkPeer
{
    public Task SendPublication(HexLinkCommittedSet set, IReadOnlyList<HexLinkPublishEdit> edits,
        bool replica, bool bypass, CancellationToken cancel) =>
        HexLinkPublicationPack.Send(session, set, edits, replica, bypass, cancel);

    public async Task<JsonObject?> PublicationStatus(HexLinkPublicationRequest request, CancellationToken cancel)
    {
        var answer = await session.Ask("publish/status", request.ToJson(), cancel) as JsonObject
            ?? throw HexLinkException.Invalid("The device returned no publication status.");
        return HexLinkData.Bool(answer, "present") == false ? null : answer;
    }
}
