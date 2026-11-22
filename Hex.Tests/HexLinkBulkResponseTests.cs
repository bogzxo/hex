using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hex.Core;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkBulkResponseTests
{
    private const string PURPOSE = "provision/test";
    private static readonly byte[] payload = [17, 37, 59, 73];

    [Fact]
    public async Task An_exact_requested_response_uses_only_its_connection_and_is_single_use()
    {
        var (sender, receiver) = BulkPair();
        using var a = sender;
        using var b = receiver;
        var (unrelatedSender, unrelatedReceiver) = BulkPair();
        using var otherA = unrelatedSender;
        using var otherB = unrelatedReceiver;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancel = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string hash = Hash(payload);
        var sink = new Staging();
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel)));
        using var expected = b.ExpectBulkResponse(PURPOSE, id, payload.Length, hash, TimeSpan.FromMinutes(1), static () => true, () => sink);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(otherA.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync("provision/other", payload.Length, Offer(id, hash), cancel)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync(PURPOSE, payload.Length - 1, Offer(id, hash), cancel)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, Hash([0])), cancel)));
        await using (HexLinkSession.BulkUpload upload = await a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel))
        {
            await upload.WriteAsync(payload, cancel);
            await upload.CompleteAsync(cancel);
        }
        Assert.Equal(payload, sink.Published);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel)));
        Assert.False(a.IsClosed);
    }

    [Fact]
    public async Task Current_view_is_checked_after_the_offer_and_loss_midstream_discards_staging()
    {
        var (sender, receiver) = BulkPair();
        using var a = sender;
        using var b = receiver;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancel = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string hash = Hash(payload);
        int view = 0;
        var sink = new Staging();
        using var expected = b.ExpectBulkResponse(PURPOSE, id, payload.Length, hash, TimeSpan.FromMinutes(1),
            () => Volatile.Read(ref view) == 1, () => sink);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel)));
        Volatile.Write(ref view, 1);
        await using HexLinkSession.BulkUpload upload = await a.OpenBulkAsync(PURPOSE, payload.Length, Offer(id, hash), cancel);
        await upload.WriteAsync(payload.AsMemory(0, 2), cancel);
        await sink.Staged.Task.WaitAsync(cancel);
        Volatile.Write(ref view, 0);
        await upload.WriteAsync(payload.AsMemory(2), cancel);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(upload.CompleteAsync(cancel)));
        Assert.Null(sink.Published);
        Assert.Equal(2, sink.BytesStaged);
        Assert.True(sink.Disposed);
        Assert.False(a.IsClosed);
    }

    private static (HexLinkSession Sender, HexLinkSession Receiver) BulkPair()
    {
        var (a, b) = Pair();
        return (new HexLinkSession(new HexLinkConnection(a, DeviceB, "memory", true, () => HexLinkRights.None, bulk: HexLinkBulkSupport.Raw)).Start(),
            new HexLinkSession(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.None, bulk: HexLinkBulkSupport.Raw)).Start());
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static JsonObject Offer(string id, string hash) => new() { ["request"] = id, ["sha256"] = hash };

    private sealed class Staging : IHexLinkBulkSink
    {
        private readonly MemoryStream data = new();
        public TaskCompletionSource Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Published { get; private set; }
        public int BytesStaged { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel)
        {
            data.Write(bytes.Span);
            BytesStaged += bytes.Length;
            Staged.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancel)
        {
            Published = data.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return data.DisposeAsync();
        }
    }
}
