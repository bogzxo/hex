using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkSession
{
    private readonly ConcurrentDictionary<(string Purpose, string Id), BulkResponse> bulkResponses = new();

    // A response is the only upload allowed without a reverse grant. Its caller already accepted this exact offer.
    internal IDisposable ExpectBulkResponse(string purpose, string id, long length, string hash, TimeSpan lease,
        Func<bool> canReceive, Func<IHexLinkBulkSink> open)
    {
        if (purpose.Length is 0 or > 80 || !Guid.TryParseExact(id, "N", out _) || length < 0
            || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(1))
            throw new ArgumentException("A Link bulk response needs an exact request, length, hash and deadline.");
        var response = new BulkResponse(this, (purpose, id), length, hash, lease, canReceive, open);
        if (bulkReceivers.ContainsKey(purpose) || !bulkResponses.TryAdd((purpose, id), response))
            throw new InvalidOperationException("That Link bulk response is already expected.");
        return response;
    }

    private BulkResponse FindBulkResponse(string purpose, long length, JsonObject? metadata)
    {
        string? id = HexLinkData.String(metadata, "request", 33);
        string? hash = HexLinkData.String(metadata, "sha256", 65);
        if (id is null || !bulkResponses.TryGetValue((purpose, id), out BulkResponse? response)
            || length != response.Length || !string.Equals(hash, response.Hash, StringComparison.OrdinalIgnoreCase)
            || !response.AvailableForOpen)
            throw RefuseBulkResponse("No matching live Link bulk response was requested.");
        return response;
    }

    private HexLinkException RefuseBulkResponse(string reason)
    {
        var failure = new HexLinkException(HexLinkErrors.NOT_ALLOWED, reason);
        AuditFault("bulk response refused", failure);
        return failure;
    }

    private sealed class BulkResponse(HexLinkSession session, (string Purpose, string Id) key, long length, string hash,
        TimeSpan lease, Func<bool> canReceive, Func<IHexLinkBulkSink> open) : IDisposable
    {
        private readonly long expires = Environment.TickCount64 + (long)lease.TotalMilliseconds;
        private int used, disposed;
        public long Length => length;
        public string Hash => hash;
        public bool Allowed => Volatile.Read(ref disposed) == 0 && Environment.TickCount64 < expires && canReceive();
        public bool AvailableForOpen => Volatile.Read(ref used) == 0 && Allowed;
        public bool Claim() => Interlocked.CompareExchange(ref used, 1, 0) == 0;
        public IHexLinkBulkSink Open() => open();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                session.bulkResponses.TryRemove(key, out _);
        }
    }
}
