using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkPublishEdit(string Path, string? Base, byte[]? Bytes);

internal sealed record HexLinkPublishReceipt(Guid Id, string Commit, int Files, string? Author = null);

internal sealed record HexLinkPublishTarget(
    Func<Guid, CancellationToken, ValueTask<IAsyncDisposable>> Fence,
    Func<string, CancellationToken, Task<string?>> Hash,
    Func<HexLinkPublishEdit, CancellationToken, Task> Apply,
    // Persist the complete notice for replay, deduplicating by receipt ID.
    Func<HexLinkPublishReceipt, IReadOnlyList<HexLinkPublishEdit>, CancellationToken, Task> Announce,
    Action<Guid, bool>? BlockLiveForRecovery = null);

/// <summary>
/// The owner stages a complete set in Link's object store. One ref transaction records the commit decision
/// and receipt. Projection can resume from the receipt even when the publisher lost the connection.
/// </summary>
internal sealed class HexLinkPublishJournal(string directory, HexLinkWorktreeGit git, HexLinkPublishTarget target)
{
    private readonly SemaphoreSlim requests = new(1);
    private const string ManifestName = "manifest.json";

    public async Task<HexLinkPublishReceipt> Publish(Guid id, IReadOnlyList<HexLinkPublishEdit> edits, CancellationToken cancel, string? author = null)
    {
        author = HexLinkNames.Clean(author);
        HexLinkPublishEdit[] ordered = [.. edits.OrderBy(edit => edit.Path, StringComparer.Ordinal)];
        JsonObject manifest = Manifest(id, ordered, author);
        await requests.WaitAsync(cancel);
        try
        {
            if (await git.Receipt(id, cancel) is { } existing)
            {
                JsonObject retained = await ReadManifest(existing, cancel);
                if (!JsonNode.DeepEquals(retained, manifest))
                    throw HexLinkException.Invalid("A publication ID cannot be reused for a different set.");
                return await Finish(id, existing, cancel);
            }

            string? tip = await git.Tip(cancel);
            if (tip is not null)
            {
                Guid previous = ManifestId(await ReadManifest(tip, cancel));
                if (!Completed(previous, tip))
                    throw new HexLinkException(HexLinkErrors.BUSY, "The preceding publication is still being recovered.");
            }

            string commit = await Stage(manifest, ordered, cancel);
            await using IAsyncDisposable fence = await target.Fence(id, cancel);
            CancellationToken stop = fence is HexLinkPublicationFence.Lease lease ? lease.Stop : cancel;
            if (await git.Tip(stop) != tip)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "A publication advanced while this set was being prepared.");
            foreach (HexLinkPublishEdit edit in ordered)
                await CheckBase(edit, stop);

            target.BlockLiveForRecovery?.Invoke(id, true);
            try
            {
                await git.CommitPublish(id, commit, tip, stop);
            }
            catch
            {
                if (await git.Receipt(id, CancellationToken.None) is null)
                    target.BlockLiveForRecovery?.Invoke(id, false);
                throw;
            }
            return await Project(id, commit, ordered, author, stop);
        }
        finally
        {
            requests.Release();
        }
    }

    public async Task<HexLinkPublishReceipt?> Recover(Guid id, CancellationToken cancel)
    {
        await requests.WaitAsync(cancel);
        try
        {
            return await git.Receipt(id, cancel) is { } commit ? await Finish(id, commit, cancel) : null;
        }
        finally
        {
            requests.Release();
        }
    }

    public async Task<HexLinkPublishReceipt?> RecoverCurrent(CancellationToken cancel)
    {
        await requests.WaitAsync(cancel);
        try
        {
            if (await git.Tip(cancel) is not { } commit)
                return null;
            return await Finish(ManifestId(await ReadManifest(commit, cancel)), commit, cancel);
        }
        finally
        {
            requests.Release();
        }
    }

    private async Task<HexLinkPublishReceipt> Finish(Guid id, string commit, CancellationToken cancel)
    {
        JsonObject said = await ReadManifest(commit, cancel);
        if (ManifestId(said) != id || said["files"] is not JsonArray list || list.Count > HexLinkTree.MOST_FILES)
            throw new IOException("The saved publication manifest is invalid.");
        if (Completed(id, commit))
            return new HexLinkPublishReceipt(id, commit, list.Count, HexLinkData.String(said, "author", 80));

        HexLinkPublishEdit[] edits = await ReadEdits(commit, list, cancel);

        await using IAsyncDisposable fence = await target.Fence(id, cancel);
        if (Completed(id, commit))
            return new HexLinkPublishReceipt(id, commit, edits.Length);
        target.BlockLiveForRecovery?.Invoke(id, true);
        return await Project(id, commit, edits, HexLinkData.String(said, "author", 80), fence is HexLinkPublicationFence.Lease lease ? lease.Stop : cancel);
    }

    public async Task<IReadOnlyList<HexLinkPublishEdit>> CommittedEdits(Guid id, CancellationToken cancel)
    {
        string commit = await git.Receipt(id, cancel)
            ?? throw HexLinkException.NotFound("The publication has no committed payload.");
        JsonObject said = await ReadManifest(commit, cancel);
        if (ManifestId(said) != id || said["files"] is not JsonArray list || list.Count > HexLinkTree.MOST_FILES)
            throw new IOException("The saved publication manifest is invalid.");
        return await ReadEdits(commit, list, cancel);
    }

    public async Task<Guid?> PendingProjection(CancellationToken cancel)
    {
        if (await git.Tip(cancel) is not { } tip)
            return null;
        Guid id = ManifestId(await ReadManifest(tip, cancel));
        return Completed(id, tip) ? null : id;
    }

    private async Task<HexLinkPublishEdit[]> ReadEdits(string commit, JsonArray list, CancellationToken cancel)
    {
        var edits = new HexLinkPublishEdit[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject row || HexLinkData.String(row, "path", HexLinkFiles.MOST_PATH) is not { } path)
                throw new IOException("The saved publication has an invalid path.");
            string? basis = HexLinkData.String(row, "base", 64), hash = HexLinkData.String(row, "hash", 64);
            byte[]? bytes = hash is null ? null : await git.ReadFile(commit, BlobName(i), cancel);
            if ((bytes is not null && HexLinkFiles.Hash(bytes) != hash) || (basis is not null && !IsHash(basis)))
                throw new IOException($"The saved publication is invalid for {path}.");
            edits[i] = new HexLinkPublishEdit(path, basis, bytes);
        }

        return edits;
    }

    private async Task<HexLinkPublishReceipt> Project(Guid id, string commit, IReadOnlyList<HexLinkPublishEdit> edits, string? author, CancellationToken cancel)
    {
        foreach (HexLinkPublishEdit edit in edits)
        {
            string? current = await target.Hash(edit.Path, cancel);
            string? intended = edit.Bytes is { } bytes ? HexLinkFiles.Hash(bytes) : null;
            if (current == intended)
                continue;
            if (current != edit.Base)
                throw Stale(edit.Path);
            await target.Apply(edit, cancel);
        }

        var receipt = new HexLinkPublishReceipt(id, commit, edits.Count, author);
        await target.Announce(receipt, edits, cancel);
        HexLinkFiles.WriteAtomic(CompletePath(id), Encoding.UTF8.GetBytes(commit));
        target.BlockLiveForRecovery?.Invoke(id, false);
        return receipt;
    }

    private async Task CheckBase(HexLinkPublishEdit edit, CancellationToken cancel)
    {
        if (await target.Hash(edit.Path, cancel) != edit.Base)
            throw Stale(edit.Path);
    }

    private static HexLinkException Stale(string path) =>
        new(HexLinkErrors.CONFLICT, $"{path} changed since this private version was prepared.");

    private async Task<string> Stage(JsonObject manifest, HexLinkPublishEdit[] edits, CancellationToken cancel)
    {
        string stage = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var staged = new List<HexLinkWorktreeFile> { new(ManifestName) };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stage, ManifestName), manifest.ToJsonString(), cancel);
            for (int i = 0; i < edits.Length; i++)
            {
                if (edits[i].Bytes is not { } data)
                    continue;
                string name = BlobName(i);
                staged.Add(new HexLinkWorktreeFile(name));
                await File.WriteAllBytesAsync(Path.Combine(stage, name), data, cancel);
            }
            return await git.Capture(stage, staged, cancel);
        }
        finally
        {
            foreach (HexLinkWorktreeFile file in staged)
                File.Delete(Path.Combine(stage, file.Path));
            Directory.Delete(stage);
        }
    }

    private async Task<JsonObject> ReadManifest(string commit, CancellationToken cancel) =>
        JsonNode.Parse(await git.ReadFile(commit, ManifestName, cancel)) as JsonObject
        ?? throw new IOException("The saved publication manifest could not be read.");

    private static Guid ManifestId(JsonObject said) =>
        Guid.TryParseExact(HexLinkData.String(said, "id", 32), "N", out Guid id) && id != Guid.Empty
            ? id : throw new IOException("The saved publication ID is invalid.");

    private static JsonObject Manifest(Guid id, HexLinkPublishEdit[] edits, string? author)
    {
        if (id == Guid.Empty || edits.Length is 0 or > HexLinkTree.MOST_FILES)
            throw HexLinkException.Invalid("A publication needs an ID and a bounded set of changes.");

        var seen = new HashSet<string>(PlatformPaths.Comparer);
        var entries = new JsonArray();
        foreach (HexLinkPublishEdit edit in edits)
        {
            if (edit.Path.Length is 0 or > HexLinkFiles.MOST_PATH || !seen.Add(edit.Path) ||
                (edit.Base is not null && !IsHash(edit.Base)) || edit.Bytes?.Length > HexLinkFiles.MOST_READ)
                throw HexLinkException.Invalid($"Invalid or duplicated publish path: {edit.Path}");
            entries.Add((JsonNode)new JsonObject
            {
                ["path"] = edit.Path,
                ["base"] = edit.Base,
                ["hash"] = edit.Bytes is { } bytes ? HexLinkFiles.Hash(bytes) : null
            });
        }
        var result = new JsonObject { ["id"] = id.ToString("N"), ["files"] = entries };
        if (author is not null)
            result["author"] = author;
        return result;
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string BlobName(int index) => $"blob-{index}";
    private string CompletePath(Guid id) => Path.Combine(directory, $"{id:N}.done");
    private bool Completed(Guid id, string commit) => File.Exists(CompletePath(id)) && File.ReadAllText(CompletePath(id)) == commit;
}
