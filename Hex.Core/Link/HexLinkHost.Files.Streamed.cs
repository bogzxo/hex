using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private void InstallStreamedFiles()
    {
        session.RegisterBulkReceiver("sync/file/write", HexLinkRights.Edit, OpenStreamedWrite);
    }

    private string ResolveStreamedFile(string path, out HexLinkTree tree)
    {
        int slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
            throw HexLinkException.Invalid("A streamed Link path must name a file in its workspace.");
        tree = TreeOf(new JsonObject { ["folder"] = path[..slash] });
        string full = ResolveFile(path);
        if (tree.Ignored([path[(slash + 1)..]]).Count != 0)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Link cannot transfer an ignored file.");
        return full;
    }

    private string TransferPath(HexLinkTree tree, HexLinkFileTransfer transfer)
    {
        string root = services.TreeCacheDirectory ??
            Path.Combine(Path.GetTempPath(), "hexlink-large", HexLinkFiles.Hash(System.Text.Encoding.UTF8.GetBytes(tree.Root)));
        return transfer.PartialPath(Path.Combine(root, "transfers"), tree.Root + "\n" + Device.Id);
    }

    private async Task<JsonNode?> ResumeStreamedWrite(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "sync/file/resume");
        RequireStreamedFiles();
        HexLinkFileTransfer transfer = HexLinkFileTransfer.Of(parameters);
        ResolveStreamedFile(transfer.Path, out HexLinkTree tree);
        var result = new JsonObject();
        HexLinkFileTransfer.Checkpoint checkpoint = await transfer.Inspect(TransferPath(tree, transfer), cancel);
        checkpoint.PutIn(result);
        return result;
    }

    private ValueTask<IHexLinkBulkSink> OpenStreamedWrite(long length, JsonObject metadata, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "sync/file/write");
        HexLinkFileTransfer transfer = HexLinkFileTransfer.Of(metadata);
        ResolveStreamedFile(transfer.Path, out HexLinkTree tree);
        var checkpoint = HexLinkFileTransfer.Checkpoint.Of(metadata, transfer.Length);
        return ValueTask.FromResult<IHexLinkBulkSink>(transfer.Receive(TransferPath(tree, transfer), length, checkpoint,
            async (partial, stop) =>
            {
                Need(HexLinkRights.Edit, "sync/file/write");
                string full = ResolveFile(transfer.Path);
                await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(stop);
                Need(HexLinkRights.Edit, "sync/file/write");
                if (!PlatformPaths.Comparer.Equals(full, ResolveStreamedFile(transfer.Path, out HexLinkTree current)))
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The streamed file path changed before it was written.");
                services.Audit("edit", Device, $"sync/file/write {transfer.Path}, {transfer.Length} bytes");
                await using FileStream file = HexLinkFileTransfer.Read(partial);
                string? hash = await services.CompareAndReplace(full, transfer.Path, transfer.Base, file, transfer.Length,
                    transfer.Hash, stop, transfer.Executable);
                current.RememberMode(full, transfer.Executable);
                return new JsonObject { ["hash"] = hash };
            }, discardOnSuccess: true));
    }

    private async Task<JsonNode?> ReadStreamedFile(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/file/read");
        RequireStreamedFiles();
        string id = HexLinkData.Need(parameters, "id", 33);
        if (!Guid.TryParseExact(id, "N", out _))
            throw HexLinkException.Invalid("Invalid Link streamed read request id.");
        var transfer = HexLinkFileTransfer.Of(parameters);
        string full = ResolveFile(transfer.Path);
        var checkpoint = HexLinkFileTransfer.Checkpoint.Of(parameters, transfer.Length);
        FileStream opened;
        await using (await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel))
        {
            Need(HexLinkRights.View, "sync/file/read");
            if (!PlatformPaths.Comparer.Equals(full, ResolveStreamedFile(transfer.Path, out _)))
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The streamed file path changed before it was read.");
            opened = HexLinkFileTransfer.Read(full);
        }
        await using FileStream file = opened;
        await transfer.Send(session, "sync/file/read", new JsonObject { ["id"] = id }, file, checkpoint, cancel);
        return new JsonObject { ["hash"] = transfer.Hash };
    }

    private async Task<JsonNode?> DeleteStreamedFile(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "sync/file/delete");
        RequireStreamedFiles();
        string path = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string expected = HexLinkData.Need(parameters, "base", 65);
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
            throw HexLinkException.Invalid("A streamed deletion needs a SHA-256 base.");
        string full = ResolveFile(path);
        await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel);
        Need(HexLinkRights.Edit, "sync/file/delete");
        if (!PlatformPaths.Comparer.Equals(full, ResolveStreamedFile(path, out _)))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The streamed file path changed before it was deleted.");
        services.Audit("edit", Device, $"sync/file/delete {path}");
        await services.CompareAndReplace(full, path, expected, null, 0, null, cancel, null);
        return new JsonObject { ["path"] = path };
    }

    private void RequireStreamedFiles()
    {
        if (!session.SupportsBulk)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "The link did not negotiate streamed files.");
    }
}
