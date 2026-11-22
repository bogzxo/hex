using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private static readonly SemaphoreSlim packedWrites = new(16, 16);

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
        using var total = HexLinkSyncTiming.Measure("pack.receiver-apply", entries.Count);
        Need(HexLinkRights.Edit, "sync/pack/write");
        HexLinkTree tree = TreeOf(metadata);
        string prefix = tree.Folder.Name + "/";
        if (entries.Any(entry => !entry.Path.StartsWith(prefix, StringComparison.Ordinal) || entry.Path.Length == prefix.Length))
            throw HexLinkException.Invalid("A packed file path is outside its workspace folder.");

        HashSet<string> ignored;
        using (HexLinkSyncTiming.Measure("pack.receiver-ignore", entries.Count))
            ignored = tree.Ignored([.. entries.Select(entry => entry.Path[prefix.Length..])]);
        services.Audit("edit", Device, $"sync/pack/write {tree.Folder.Name}, {entries.Count} files");
        var paths = new string?[entries.Count];
        var results = new HexLinkFilePack.Outcome?[entries.Count];
        var targets = new HashSet<string>(PlatformPaths.Comparer);
        for (int i = 0; i < entries.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            HexLinkFilePack.Entry entry = entries[i];
            try
            {
                if (ignored.Contains(entry.Path[prefix.Length..]))
                    throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Link cannot write an ignored file.");
                using var resolve = HexLinkSyncTiming.Measure("pack.receiver-resolve");
                paths[i] = ResolveFile(entry.Path);
            }
            catch (HexLinkException error)
            {
                results[i] = new(entry.Path, null, error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results[i] = new(entry.Path, null, new HexLinkException(HexLinkErrors.INTERNAL_ERROR, error.Message));
            }
            if (paths[i] is { } path && !targets.Add(path))
                throw HexLinkException.Invalid("Packed paths resolve to the same file.");
        }

        foreach (string path in targets)
        {
            for (string? parent = Path.GetDirectoryName(path); parent is not null && PlatformPaths.IsUnder(parent, tree.Root);
                parent = Path.GetDirectoryName(parent))
            {
                if (targets.Contains(parent))
                    throw HexLinkException.Invalid("Packed paths overlap.");
            }
        }

        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 6) };
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), options, async (i, stop) =>
        {
            if (paths[i] is not { } full)
                return;

            HexLinkFilePack.Entry entry = entries[i];
            await packedWrites.WaitAsync(stop);
            try
            {
                Need(HexLinkRights.Edit, "sync/pack/write");
                string? hash = await services.CompareAndReplace(full, entry.Path, entry.Base, entry.Bytes, stop, entry.Executable);
                tree.RememberMode(full, entry.Executable);
                results[i] = new(entry.Path, hash);
            }
            catch (HexLinkException error)
            {
                results[i] = new(entry.Path, null, error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results[i] = new(entry.Path, null, new HexLinkException(HexLinkErrors.INTERNAL_ERROR, error.Message));
            }
            finally
            {
                packedWrites.Release();
            }
        });

        return new JsonObject { ["outcomes"] = new JsonArray([.. results.Select(each => (JsonNode?)each!.Json())]) };
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
        int maximum = paths.Count == 1 ? HexLinkFilePack.MaximumSingle : HexLinkFilePack.MaximumFile;
        int length = 0;
        foreach (string path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                string full = ResolveFile(path);
                var info = new FileInfo(full);
                if (info.Length > maximum)
                    throw new HexLinkException(HexLinkErrors.TOO_BIG, $"The file {path} has outgrown a Link file pack.");
                byte[] bytes = await HexLinkFiles.ReadSharedAsync(full, cancel);
                int cost = HexLinkFilePack.WireCost(path, bytes.Length);
                if (bytes.Length > maximum || length + cost > HexLinkFilePack.MaximumLength)
                    throw new HexLinkException(HexLinkErrors.TOO_BIG, $"The file {path} needs a separate Link file pack.");
                length += cost;
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
