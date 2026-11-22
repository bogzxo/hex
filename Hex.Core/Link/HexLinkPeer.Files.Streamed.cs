using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    private readonly ConcurrentDictionary<string, StreamedRead> streamedReads = new(StringComparer.Ordinal);
    private bool streamedReadsRegistered;

    private sealed class StreamedRead(HexLinkFileTransfer transfer, string partial)
    {
        public HexLinkFileTransfer Transfer { get; } = transfer;
        public string Partial { get; } = partial;
        public bool Received { get; set; }
    }

    internal async Task ReadStreamedFile(HexLinkFileTransfer transfer, string partial, CancellationToken cancel)
    {
        lock (filePacks)
        {
            if (!streamedReadsRegistered)
            {
                session.RegisterBulkReceiver("sync/file/read", HexLinkRights.View, OpenStreamedRead);
                streamedReadsRegistered = true;
            }
        }

        string id = Guid.NewGuid().ToString("N");
        var read = new StreamedRead(transfer, partial);
        streamedReads[id] = read;
        try
        {
            var checkpoint = await transfer.Inspect(partial, cancel);
            JsonObject parameters = transfer.Json();
            parameters["id"] = id;
            checkpoint.PutIn(parameters);
            await AskObject("sync/file/read", parameters, cancel, TimeSpan.FromMinutes(15));
            if (!read.Received)
                throw HexLinkException.Invalid("The peer did not return the streamed file.");
        }
        finally
        {
            streamedReads.TryRemove(id, out _);
        }
    }

    private ValueTask<IHexLinkBulkSink> OpenStreamedRead(long length, JsonObject metadata, CancellationToken cancel)
    {
        string id = HexLinkData.Need(metadata, "id", 33);
        if (!streamedReads.TryGetValue(id, out StreamedRead? read) || read.Received)
            throw HexLinkException.NotFound("There is no pending request for this streamed file.");

        var checkpoint = HexLinkFileTransfer.Checkpoint.Of(metadata, read.Transfer.Length);
        return ValueTask.FromResult<IHexLinkBulkSink>(read.Transfer.Receive(read.Partial, length, checkpoint, (_, _) =>
        {
            read.Received = true;
            return Task.FromResult<JsonNode?>(null);
        }, discardOnSuccess: false));
    }

    internal async Task WriteStreamedFile(HexLinkFileTransfer transfer, string full, CancellationToken cancel)
    {
        JsonObject answer = await AskObject("sync/file/resume", transfer.Json(), cancel, TimeSpan.FromMinutes(5));
        var checkpoint = HexLinkFileTransfer.Checkpoint.Of(answer, transfer.Length);
        FileStream opened;
        await using (await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel))
            opened = HexLinkFileTransfer.Read(full);
        // A checked replacement can rename this path after the open. The handle keeps the opened contents.
        await using FileStream file = opened;
        JsonNode? result = await transfer.Send(session, "sync/file/write", transfer.Json(), file, checkpoint, cancel);
        if (result is not JsonObject committed || HexLinkData.String(committed, "hash", 64) != transfer.Hash)
            throw HexLinkException.Invalid("The peer did not confirm the streamed file hash.");
    }

    internal Task DeleteStreamedFile(string path, string baseHash, CancellationToken cancel) =>
        session.Ask("sync/file/delete", new JsonObject { ["path"] = path, ["base"] = baseHash }, cancel);
}
