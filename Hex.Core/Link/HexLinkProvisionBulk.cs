using System.Text.Json.Nodes;

namespace Hex.Core;

internal static class HexLinkProvisionBulk
{
    internal const string FILE_PURPOSE = "provision/file";
    internal const string BUNDLE_PURPOSE = "provision/bundle";
    internal static readonly TimeSpan TransferLease = TimeSpan.FromMinutes(30);

    internal sealed class Staging(string path, long length, Action<long, long>? progress) : IHexLinkBulkSink
    {
        private readonly FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        private long written;

        public bool Committed { get; private set; }
        public IOException? WriteFailure { get; private set; }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            try { await file.WriteAsync(data, cancel); }
            catch (IOException error)
            {
                WriteFailure = error;
                throw;
            }
            written += data.Length;
            progress?.Invoke(written, length);
        }

        public async ValueTask CommitAsync(CancellationToken cancel)
        {
            try { await file.FlushAsync(cancel); }
            catch (IOException error)
            {
                WriteFailure = error;
                throw;
            }
            Committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try { await file.DisposeAsync(); }
            finally
            {
                if (!Committed)
                    HexLinkGit.TryDelete(path);
            }
        }
    }
}

internal sealed partial class HexLinkPeer
{
    // Negotiated framing alone is insufficient: previous peers support bulk for sync but not a one-way Get.
    private int provisionBulkCapability;
    internal async Task<bool> SupportsProvisionBulk(CancellationToken cancel)
    {
        int known = Volatile.Read(ref provisionBulkCapability);
        if (!session.SupportsBulk || known < 0)
            return false;
        if (known > 0)
            return true;
        try
        {
            JsonObject answer = await AskObject("provision/bulk", new JsonObject { ["action"] = "probe" }, cancel);
            bool supported = HexLinkData.Int(answer, "version") is >= 1;
            Volatile.Write(ref provisionBulkCapability, supported ? 1 : -1);
            return supported;
        }
        catch (HexLinkException error) when (error.Code == HexLinkErrors.METHOD_NOT_FOUND)
        {
            Volatile.Write(ref provisionBulkCapability, -1);
            return false;
        }
    }

    internal Task<string> ReadProvisionBulk(HexLinkTreeFile file, CancellationToken cancel)
    {
        if (file.Hash is not { } hash)
            throw new IOException("The provisioning manifest contains no hash for this file.");
        var transfer = new HexLinkFileTransfer(file.Path, file.Size, hash);
        JsonObject request = transfer.Json();
        request["action"] = "file";
        string id = Guid.NewGuid().ToString("N");
        return DownloadProvisionBulk(id, HexLinkProvisionBulk.FILE_PURPOSE, file.Size, hash, request, null, cancel);
    }

    internal async Task<string?> ProvisionBundle(string repository, IReadOnlyList<string> have, Action<long, long> progress,
        string? verifyIn, CancellationToken cancel)
    {
        if (!await SupportsProvisionBulk(cancel))
            return await Bundle(repository, have, progress, verifyIn, cancel);
        string id = Guid.NewGuid().ToString("N");
        string? received = null;
        bool verified = false;
        try
        {
            JsonObject offer = await AskObject("provision/bulk", new JsonObject
            {
                ["action"] = "bundle", ["request"] = id, ["repository"] = repository,
                ["have"] = new JsonArray([.. have.Take(HexLinkGit.MOST_HAVE).Select(sha => (JsonNode)sha)])
            }, cancel, TimeSpan.FromMinutes(15));
            if (HexLinkData.Bool(offer, "empty") == true)
            {
                progress(0, 0);
                verified = true;
                return null;
            }
            long length = HexLinkData.Long(offer, "length") ?? -1;
            if (HexLinkData.String(offer, "request", 33) != id || length <= 0
                || HexLinkData.String(offer, "sha256", 65) is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
                throw HexLinkException.Invalid("The host offered an invalid provisioning bundle.");
            progress(0, length);
            received = await DownloadProvisionBulk(id, HexLinkProvisionBulk.BUNDLE_PURPOSE, length, hash,
                new JsonObject { ["action"] = "accept" }, progress, cancel);
            if (await Task.Run(() => HexLinkGit.Verify(received, verifyIn, cancel), cancel) is { } error)
                throw new IOException($"The bundle didn't verify, {error}");
            verified = true;
            return received;
        }
        finally
        {
            if (!verified)
            {
                if (!session.IsClosed)
                    session.Tell("git/cancel", new JsonObject { ["id"] = id });
                if (received is not null)
                    HexLinkGit.TryDelete(received);
            }
        }
    }

    internal async Task<T> WhileProvisionView<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        int revision = Volatile.Read(ref viewRevision);
        bool StillAllowed() => revision == Volatile.Read(ref viewRevision) && HexLinkAllowed.Has(Granted, HexLinkRights.View);
        void Changed()
        {
            if (!StillAllowed())
            {
                try { _ = stop.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }

        RightsChanged += Changed;
        try
        {
            if (!StillAllowed())
                throw HexLinkException.NotAllowed("view");
            return await work(stop.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && !StillAllowed())
        {
            throw HexLinkException.NotAllowed("view");
        }
        finally { RightsChanged -= Changed; }
    }

    private async Task<string> DownloadProvisionBulk(string id, string purpose, long length, string hash, JsonObject request,
        Action<long, long>? progress, CancellationToken cancel)
    {
        string path = Path.Combine(Path.GetTempPath(), "hexlink-provision-" + id + ".partial");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        lifetime.CancelAfter(HexLinkProvisionBulk.TransferLease);
        CancellationToken stop = lifetime.Token;
        int revision = Volatile.Read(ref viewRevision);
        bool Allowed() => !stop.IsCancellationRequested && revision == Volatile.Read(ref viewRevision)
            && HexLinkAllowed.Has(Granted, HexLinkRights.View);
        HexLinkProvisionBulk.Staging? staging = null;
        bool keep = false;
        request["request"] = id;
        using var expected = session.ExpectBulkResponse(purpose, id, length, hash, HexLinkProvisionBulk.TransferLease,
            Allowed, () => staging = new HexLinkProvisionBulk.Staging(path, length, progress));
        try
        {
            JsonObject complete = await AskObject("provision/bulk", request, stop, HexLinkProvisionBulk.TransferLease);
            if (!Allowed())
                throw HexLinkException.NotAllowed("view");
            if (staging?.Committed != true || !string.Equals(HexLinkData.String(complete, "sha256", 65), hash, StringComparison.OrdinalIgnoreCase))
                throw HexLinkException.Invalid("The host did not complete the expected provisioning stream.");
            keep = true;
            return path;
        }
        catch (HexLinkException) when (staging?.WriteFailure is { } error)
        {
            throw error;
        }
        finally
        {
            if (!keep)
            {
                if (!session.IsClosed)
                    session.Tell("git/cancel", new JsonObject { ["id"] = id });
                HexLinkGit.TryDelete(path);
            }
        }
    }
}
