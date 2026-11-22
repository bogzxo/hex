using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkDraftRoute(
    Func<CancellationToken, Task<HexLinkManifest>> Snapshot,
    Func<Guid, CancellationToken, Task<HexLinkPublishReceipt?>> Status,
    Func<Guid, IReadOnlyList<HexLinkPublishEdit>, bool, CancellationToken, Task<HexLinkPublishReceipt>> Send);

internal sealed record HexLinkDraftPublication(HexLinkPublishReceipt? Receipt, HexLinkWorktreeGateResult? Gate,
    IReadOnlyList<HexLinkDraftConflict> Conflicts);

internal sealed partial class HexLinkPrivateWorktree
{
    private sealed record PendingPublish(Guid Id, HexLinkDraftPlan Plan, bool Bypass);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Publications = new(PlatformPaths.Comparer);
    private string PendingFile => Path.Combine(data, "pending.json");

    public Task<HexLinkDraftPublication> Publish(HexLinkDraftRoute route, HexLinkWorktreeGateOptions options,
        bool bypass, CancellationToken cancel) =>
        Publish(route, (_, _) => Task.FromResult<IReadOnlyList<HexLinkWorktreeGateOptions>>([options]), bypass, cancel);

    public async Task<HexLinkDraftPublication> Publish(HexLinkDraftRoute route,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HexLinkWorktreeGateOptions>>> gateFor,
        bool bypass, CancellationToken cancel)
    {
        SemaphoreSlim serial = Publications.GetOrAdd(data, static _ => new SemaphoreSlim(1));
        await serial.WaitAsync(cancel);
        try
        {
            PendingPublish? pending = await Pending(cancel);
            HexLinkWorktreeGateResult? gate = null;
            if (pending is null)
            {
                HexLinkManifest snapshot = await route.Snapshot(cancel);
                HexLinkDraftPlan plan = await Prepare(snapshot, cancel);
                if (plan.Conflicts.Count > 0 || plan.Edits.Count == 0)
                    return new(null, null, plan.Conflicts);
                if (!bypass)
                {
                    gate = await Gate(snapshot, plan, gateFor, cancel);
                    if (!gate.Passed)
                        return new(null, gate, []);
                }
                pending = new PendingPublish(Guid.NewGuid(), plan, bypass);
                await KeepPending(pending, cancel);
            }

            HexLinkPublishReceipt? receipt = await route.Status(pending.Id, cancel);
            if (receipt is null)
            {
                try
                {
                    receipt = await route.Send(pending.Id, pending.Plan.Edits, pending.Bypass, cancel);
                }
                catch (HexLinkException error) when (error.Code == HexLinkErrors.CONFLICT)
                {
                    // Only the authority can say whether the CAS already committed this ID.
                    receipt = await route.Status(pending.Id, cancel);
                    if (receipt is null)
                    {
                        ClearPending(pending);
                        throw;
                    }
                }
            }
            if (receipt.Id != pending.Id || receipt.Files != pending.Plan.Edits.Count || !HexLinkGit.IsCommitId(receipt.Commit))
                throw new IOException("The authority returned a different publication receipt.");
            await Accept(pending.Plan, cancel);
            ClearPending(pending);
            return new(receipt, gate, []);
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task KeepPending(PendingPublish pending, CancellationToken cancel)
    {
        string stage = PendingDirectory(pending.Id);
        Directory.CreateDirectory(stage);
        var files = new JsonArray();
        for (int at = 0; at < pending.Plan.Edits.Count; at++)
        {
            HexLinkPublishEdit edit = pending.Plan.Edits[at];
            string? hash = null;
            if (edit.Bytes is { } bytes)
            {
                hash = HexLinkFiles.Hash(bytes);
                await File.WriteAllBytesAsync(PendingBlob(pending.Id, at), bytes, cancel);
            }
            files.Add((JsonNode)new JsonObject
            {
                ["path"] = edit.Path,
                ["base"] = edit.Base,
                ["draft"] = pending.Plan.DraftHashes.GetValueOrDefault(edit.Path),
                ["hash"] = hash
            });
        }
        var saved = new JsonObject { ["id"] = pending.Id.ToString("N"), ["files"] = files, ["bypass"] = pending.Bypass };
        HexLinkFiles.WriteAtomic(PendingFile, Encoding.UTF8.GetBytes(saved.ToJsonString()));
    }

    private async Task<PendingPublish?> Pending(CancellationToken cancel)
    {
        if (!File.Exists(PendingFile))
            return null;
        if (JsonNode.Parse(await File.ReadAllBytesAsync(PendingFile, cancel)) is not JsonObject data ||
            !Guid.TryParseExact(HexLinkData.String(data, "id", 32), "N", out Guid id) || id == Guid.Empty ||
            data["files"] is not JsonArray rows || rows.Count is 0 or > HexLinkTree.MOST_FILES)
            throw new IOException("This worktree's pending publication is invalid.");

        var edits = new HexLinkPublishEdit[rows.Count];
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int at = 0; at < rows.Count; at++)
        {
            if (rows[at] is not JsonObject row || HexLinkData.String(row, "path", HexLinkFiles.MOST_PATH) is not { } wire)
                throw new IOException("The pending publication contains an invalid path.");
            string? hash = HexLinkData.String(row, "hash", 64);
            byte[]? content = hash is not null ? await HexLinkFiles.ReadSharedAsync(PendingBlob(id, at), cancel) : null;
            if (content is not null && HexLinkFiles.Hash(content) != hash)
                throw new IOException($"The pending publication was changed on disk for {wire}.");
            edits[at] = new HexLinkPublishEdit(wire, HexLinkData.String(row, "base", 64), content);
            hashes.Add(wire, HexLinkData.String(row, "draft", 64));
        }
        return new PendingPublish(id, new HexLinkDraftPlan(edits, [], hashes), HexLinkData.Bool(data, "bypass") == true);
    }

    private void ClearPending(PendingPublish pending)
    {
        File.Delete(PendingFile);
        for (int i = 0; i < pending.Plan.Edits.Count; i++)
            File.Delete(PendingBlob(pending.Id, i));
        Directory.Delete(PendingDirectory(pending.Id));
    }

    private string PendingDirectory(Guid id) => Path.Combine(data, "pending-" + id.ToString("N"));
    private string PendingBlob(Guid id, int index) => Path.Combine(PendingDirectory(id), $"blob-{index}");
}
