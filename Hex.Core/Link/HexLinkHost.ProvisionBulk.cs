using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private readonly ConcurrentDictionary<string, PreparedProvisionBundle> preparedProvisionBundles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim provisionBundleSlots = new(4);

    private async Task<JsonNode?> ProvisionBulk(JsonObject parameters, CancellationToken cancel)
    {
        if (!session.SupportsBulk)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "This Link does not support bulk provisioning.");
        Need(HexLinkRights.View, "provision/bulk");
        string action = HexLinkData.Need(parameters, "action", 30);
        if (action == "probe")
            return new JsonObject { ["version"] = 1 };
        string id = HexLinkData.Need(parameters, "request", 33);
        if (!Guid.TryParseExact(id, "N", out _))
            throw HexLinkException.Invalid("A provisioning stream needs a request id.");

        CancellationTokenSource transfer = BeginBundle(id, cancel);
        try
        {
            return action switch
            {
                "file" => await SendProvisionFile(id, parameters, transfer.Token),
                "bundle" => await PrepareProvisionBundle(id, parameters, transfer.Token),
                "accept" => await SendProvisionBundle(id, transfer.Token),
                _ => throw HexLinkException.Invalid("Unknown bulk provisioning action.")
            };
        }
        finally { EndBundle(id, transfer); }
    }

    private async Task<JsonObject> SendProvisionFile(string id, JsonObject parameters, CancellationToken cancel)
    {
        HexLinkFileTransfer file = HexLinkFileTransfer.Of(parameters);
        string full = ResolveStreamedFile(file.Path, out _);
        await using FileStream input = HexLinkFileTransfer.Read(full);
        if (input.Length != file.Length)
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The file changed after its provisioning manifest.");
        await SendProvisionBytes(id, HexLinkProvisionBulk.FILE_PURPOSE, input, file.Length, file.Hash, cancel);
        return new JsonObject { ["sha256"] = file.Hash };
    }

    private async Task<JsonObject> PrepareProvisionBundle(string id, JsonObject parameters, CancellationToken cancel)
    {
        if (!provisionBundleSlots.Wait(0))
            throw new HexLinkException(HexLinkErrors.BUSY, "Too many provisioning bundles are prepared.");
        HexLinkGit.Made? made = null;
        bool retained = false;
        try
        {
            string name = HexLinkData.Need(parameters, "repository", 200);
            HexLinkHostedProvision plan = hostedProvision ?? throw HexLinkException.NotFound("Ask for a workspace plan before its bundles.");
            if (!plan.WirePaths.TryGetValue(name, out string? wire))
                throw HexLinkException.NotFound("The workspace plan changed. Request it again.");
            HexLinkProvisionRepository planned = plan.Plan.Repositories.First(repo => repo.Id == name);
            string repository = HexLinkFiles.Resolve(Folders, wire, services.Excludes);
            List<string> have = HexLinkData.Strings(parameters, "have", HexLinkGit.MOST_HAVE, 64);
            services.Audit("git", Device, $"provision/bulk {planned.Path}");
            made = await Task.Run(() =>
            {
                if (HexLinkProvision.ReadHead(repository, cancel) != planned.Head)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The repository changed on the host. Request a new workspace plan.");
                return HexLinkGit.Bundle(repository, have, cancel, includeHead: true);
            }, cancel);
            Need(HexLinkRights.View, "provision/bulk");
            if (made is null)
                return new JsonObject { ["empty"] = true };

            await using var file = HexLinkFileTransfer.Read(made.File);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel)).ToLowerInvariant();
            Need(HexLinkRights.View, "provision/bulk");
            var offer = new PreparedProvisionBundle(made.File, made.Size, hash);
            if (!preparedProvisionBundles.TryAdd(id, offer))
                throw HexLinkException.Invalid("A provisioning bundle already uses that request id.");
            retained = true;
            _ = ExpireProvisionBundle(id, offer);
            return new JsonObject { ["request"] = id, ["length"] = made.Size, ["sha256"] = hash };
        }
        finally
        {
            if (!retained)
            {
                if (made is not null)
                    HexLinkGit.TryDelete(made.File);
                provisionBundleSlots.Release();
            }
        }
    }

    private async Task<JsonObject> SendProvisionBundle(string id, CancellationToken cancel)
    {
        if (!preparedProvisionBundles.TryRemove(id, out PreparedProvisionBundle? offer))
            throw HexLinkException.NotFound("That provisioning bundle expired.");
        try
        {
            await using var file = HexLinkFileTransfer.Read(offer.File);
            await SendProvisionBytes(id, HexLinkProvisionBulk.BUNDLE_PURPOSE, file, offer.Length, offer.Hash, cancel);
            return new JsonObject { ["sha256"] = offer.Hash };
        }
        finally { DiscardProvisionBundle(offer); }
    }

    private async Task ExpireProvisionBundle(string id, PreparedProvisionBundle offer)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), session.Closing); }
        catch (OperationCanceledException) { }
        if (preparedProvisionBundles.TryRemove(new KeyValuePair<string, PreparedProvisionBundle>(id, offer)))
            DiscardProvisionBundle(offer);
    }

    private void CancelPreparedProvisionBundle(string id)
    {
        if (preparedProvisionBundles.TryRemove(id, out PreparedProvisionBundle? offer))
            DiscardProvisionBundle(offer);
    }

    private void DiscardProvisionBundle(PreparedProvisionBundle offer)
    {
        HexLinkGit.TryDelete(offer.File);
        provisionBundleSlots.Release();
    }

    private async Task SendProvisionBytes(string id, string purpose, Stream input, long size, string hash, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "provision/bulk");
        await using HexLinkSession.BulkUpload upload = await session.OpenBulkAsync(purpose, size,
            new JsonObject { ["request"] = id, ["sha256"] = hash }, cancel);
        int block = HexLinkFrameHeader.MaximumPayload;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(block);
        try
        {
            long remaining = size;
            while (remaining > 0)
            {
                Need(HexLinkRights.View, "provision/bulk");
                int count = (int)Math.Min(remaining, block);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancel);
                await upload.WriteAsync(buffer.AsMemory(0, count), cancel);
                remaining -= count;
            }
            Need(HexLinkRights.View, "provision/bulk");
            if (input.ReadByte() != -1)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The file grew during provisioning.");
            await upload.CompleteAsync(cancel);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed record PreparedProvisionBundle(string File, long Length, string Hash);
}
