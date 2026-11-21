using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private void InstallFilePacks()
    {
        if (session.SupportsBulk)
        {
            InstallStreamedFiles();
            session.RegisterBulkReceiver("sync/pack/write", HexLinkRights.Edit, (length, metadata, _) =>
                ValueTask.FromResult<IHexLinkBulkSink>(new HexLinkFilePack.Sink(length,
                    (entries, cancel) => ApplyFilePack(metadata, entries, cancel))));
        }
    }

    private async Task<JsonNode?> ApplyFilePack(JsonObject metadata, IReadOnlyList<HexLinkFilePack.Entry> entries, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "sync/pack/write");
        HexLinkTree tree = TreeOf(metadata);
        string prefix = tree.Folder.Name + "/";
        if (entries.Any(entry => !entry.Path.StartsWith(prefix, StringComparison.Ordinal) || entry.Path.Length == prefix.Length))
            throw HexLinkException.Invalid("A packed file path is outside its workspace folder.");

        HashSet<string> ignored = tree.Ignored([.. entries.Select(entry => entry.Path[prefix.Length..])]);
        services.Audit("edit", Device, $"sync/pack/write {tree.Folder.Name}, {entries.Count} files");
        var results = new JsonArray();
        foreach (HexLinkFilePack.Entry entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            HexLinkFilePack.Outcome result;
            try
            {
                Need(HexLinkRights.Edit, "sync/pack/write");
                string full = ResolveFile(entry.Path);
                if (ignored.Contains(entry.Path[prefix.Length..]))
                    throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Link cannot write an ignored file.");
                string? hash = await services.CompareAndReplace(full, entry.Path, entry.Base, entry.Bytes, cancel, entry.Executable);
                tree.RememberMode(full, entry.Executable);
                result = new(entry.Path, hash);
            }
            catch (HexLinkException error)
            {
                result = new(entry.Path, null, error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result = new(entry.Path, null, new HexLinkException(HexLinkErrors.INTERNAL_ERROR, error.Message));
            }
            results.Add((JsonNode)result.Json());
        }

        return new JsonObject { ["outcomes"] = results };
    }

    private async Task<JsonNode?> ReadFilePack(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/pack/read");
        if (!session.SupportsBulk)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "The link did not negotiate file packs.");
        string id = HexLinkData.Need(parameters, "id", 33);
        if (!Guid.TryParseExact(id, "N", out _))
            throw HexLinkException.Invalid("Invalid Link pack request id.");
        List<string> paths = HexLinkData.Strings(parameters, "paths", HexLinkFilePack.MaximumFiles + 1, HexLinkFiles.MOST_PATH + 1);
        if (paths.Count > HexLinkFilePack.MaximumFiles || paths.Any(path => path.Length > HexLinkFiles.MOST_PATH) ||
            paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw HexLinkException.Invalid("Invalid Link pack path list.");

        var files = new List<HexLinkFilePack.Entry>(paths.Count);
        var errors = new JsonArray();
        foreach (string path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                string full = ResolveFile(path);
                var info = new FileInfo(full);
                if (info.Length > HexLinkFilePack.MaximumFile)
                    throw new HexLinkException(HexLinkErrors.TOO_BIG, $"The file {path} has outgrown a Link file pack.");
                byte[] bytes = await HexLinkFiles.ReadSharedAsync(full, cancel);
                if (bytes.Length > HexLinkFilePack.MaximumFile)
                    throw new HexLinkException(HexLinkErrors.TOO_BIG, $"The file {path} has outgrown a Link file pack.");
                int slash = path.IndexOf('/');
                HexLinkTree tree = TreeOf(new JsonObject { ["folder"] = path[..slash] });
                files.Add(new HexLinkFilePack.Entry(path, bytes, Executable: tree.Mode(full)));
            }
            catch (HexLinkException error)
            {
                errors.Add((JsonNode)new HexLinkFilePack.Outcome(path, null, error).Json());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                int code = error is FileNotFoundException or DirectoryNotFoundException ? HexLinkErrors.NOT_FOUND : HexLinkErrors.INTERNAL_ERROR;
                errors.Add((JsonNode)new HexLinkFilePack.Outcome(path, null, new HexLinkException(code, error.Message)).Json());
            }
        }

        if (files.Count > 0)
        {
            Need(HexLinkRights.View, "sync/pack/read");
            await HexLinkFilePack.Send(session, "sync/pack/read", new JsonObject { ["id"] = id }, files, cancel);
        }

        return new JsonObject { ["outcomes"] = errors };
    }
}
