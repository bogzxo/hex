using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const int LINK_DOCUMENT_SOAK_ROUNDS = 120, LINK_DOCUMENT_SOAK_WRITERS = 3;
    private const string LINK_DOCUMENT_SOAK_START = "[0:][1:][2:]";

    private void TestLinkCrossDocumentSoak(HexPointerScript test, Func<HexLinkPeer?> peer, bool guest, string work)
    {
        const string path = LINK_CROSS_DOCUMENT_PATH;
        const int finalRevision = LINK_DOCUMENT_SOAK_ROUNDS * LINK_DOCUMENT_SOAK_WRITERS;
        string diskPath = Path.Combine(work, LINK_CROSS_DOCUMENT_FILE);
        string expected = string.Concat(Enumerable.Range(0, LINK_DOCUMENT_SOAK_WRITERS).Select(lane =>
            LinkCrossSoakLane(lane, string.Concat(Enumerable.Range(0, LINK_DOCUMENT_SOAK_ROUNDS)
                .Select(round => LinkCrossSoakMarker(round, lane))))));
        string expectedHash = LinkCrossSoakHash(expected);
        var elapsed = new Stopwatch();

        void Log(string detail) => Console.WriteLine($"[Hex self-test] link-cross-document-soak: {detail}");
        bool CheckFinal(HexLinkDocSnapshot final, string? author) =>
            !final.Conflict && final.Path == path && final.Author == author && final.Revision == finalRevision
            && string.Equals(final.Text, expected, StringComparison.Ordinal) && LinkCrossSoakHash(final.Text) == expectedHash;

        test.Check("link-cross-document-soak: one authenticated encrypted peer with Edit", () =>
            LinkPeers.Count == 1 && peer() is { } other && (other.Granted & HexLinkRights.Edit) != 0
            && LinkTransportOf(other.Device.Id)?.Stream is SslStream { IsAuthenticated: true, IsEncrypted: true });
        test.Run(elapsed.Start);
        if (!guest)
        {
            DateTime? diskTime = null;
            string? session = null;
            test.Run(() =>
            {
                diskTime = File.GetLastWriteTimeUtc(diskPath);
                session = linkServices?.Documents.Snapshot(path).Session;
            });
            test.WaitFor(() => peer()?.IsClosed == true, 65);
            test.Check("link-cross-document-soak: host verified final revision, SHA-256 and unchanged disk", () =>
            {
                var final = linkServices?.Documents.Snapshot(path);
                if (final is null)
                    return false;

                string hash = LinkCrossSoakHash(final.Text);
                bool diskUnchanged = File.Exists(diskPath) && File.GetLastWriteTimeUtc(diskPath) == diskTime
                    && File.ReadAllBytes(diskPath).AsSpan().SequenceEqual("disk"u8);
                Log($"host rounds={LINK_DOCUMENT_SOAK_ROUNDS} proposals={finalRevision} revision={final.Revision} "
                    + $"sha256={hash} elapsed_ms={elapsed.ElapsedMilliseconds} disk_unchanged={diskUnchanged}");
                return peer() is { IsClosed: true } other && session == final.Session
                    && CheckFinal(final, other.Device.Id) && diskUnchanged;
            });
            test.Run(() => linkServices?.Documents.Close(path));
            return;
        }

        Task<(HexLinkDocSnapshot Final, int CommitReorders, bool DiskUnchanged)>? run = null;
        test.Run(() =>
        {
            if (peer() is { } host && LinkDevice is { } local)
                run = RunLinkCrossDocumentSoak(host, local.Id);
        });
        test.WaitFor(() => run?.IsCompleted == true, 55);
        test.Check("link-cross-document-soak: guest verified final revision, SHA-256 and unchanged disk", () =>
        {
            if (run is not { IsCompletedSuccessfully: true })
            {
                Log($"guest stopped after {elapsed.ElapsedMilliseconds} ms: {run?.Exception?.GetBaseException().Message ?? "no result"}");
                return false;
            }

            var (final, reorders, diskUnchanged) = run.Result;
            diskUnchanged &= !File.Exists(diskPath);
            Log($"guest rounds={LINK_DOCUMENT_SOAK_ROUNDS} proposals={finalRevision} stale={finalRevision - LINK_DOCUMENT_SOAK_ROUNDS} "
                + $"revision={final.Revision} sha256={LinkCrossSoakHash(final.Text)} elapsed_ms={elapsed.ElapsedMilliseconds} "
                + $"commit_reorders={reorders} disk_unchanged={diskUnchanged}");
            return peer()?.IsClosed == false && CheckFinal(final, LinkDevice?.Id) && diskUnchanged;
        });
    }

    private static async Task<(HexLinkDocSnapshot Final, int CommitReorders, bool DiskUnchanged)> RunLinkCrossDocumentSoak(
        HexLinkPeer host, string author)
    {
        const string path = LINK_CROSS_DOCUMENT_PATH;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var cancel = timeout.Token;
        var basis = await host.JoinDocument(path, cancel).ConfigureAwait(false);
        string session = basis.Session;
        if (basis.Conflict || basis.Revision != 0 || basis.Text != LINK_DOCUMENT_SOAK_START)
            throw new InvalidOperationException("The document soak did not join its initial buffer.");

        string[] lanes = Enumerable.Range(0, LINK_DOCUMENT_SOAK_WRITERS).Select(lane => LinkCrossSoakLane(lane, "")).ToArray();
        int reorders = 0;
        for (int round = 0; round < LINK_DOCUMENT_SOAK_ROUNDS; round++)
        {
            // At most 3 asks in flight and 30 edits per second, half the session allowance.
            await Task.Delay(100, cancel).ConfigureAwait(false);
            var nextLanes = lanes.Select((lane, index) => lane[..^1] + LinkCrossSoakMarker(round, index) + "]").ToArray();
            var order = Enumerable.Range(0, LINK_DOCUMENT_SOAK_WRITERS)
                .Select(index => (index + round / 2) % LINK_DOCUMENT_SOAK_WRITERS).ToArray();
            if (round % 2 != 0)
                Array.Reverse(order);

            var requests = order.Select(lane => host.EditDocument(path, session, basis.Revision,
                string.Concat(lanes.Select((text, index) => index == lane ? nextLanes[lane] : text)), cancel)).ToArray();
            var replies = await Task.WhenAll(requests).ConfigureAwait(false);
            var commits = Enumerable.Range(0, replies.Length).OrderBy(index => replies[index].Revision).ToArray();
            if (!commits.SequenceEqual(Enumerable.Range(0, replies.Length)))
                reorders++;

            long revision = basis.Revision;
            foreach (int index in commits)
            {
                int lane = order[index];
                lanes[lane] = nextLanes[lane];
                var reply = replies[index];
                if (reply.Conflict || reply.Session != session || reply.Author != author || reply.Revision != ++revision
                    || !string.Equals(reply.Text, string.Concat(lanes), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Document soak round {round + 1}, lane {lane}: invalid commit at revision {reply.Revision}.");
                basis = reply;
            }
        }

        var final = await host.JoinDocument(path, cancel).ConfigureAwait(false);
        if (final != basis)
            throw new InvalidOperationException("The final join did not return the last committed snapshot.");

        var disk = await host.Read(path, cancel).ConfigureAwait(false);
        return (final, reorders, disk.Bytes.AsSpan().SequenceEqual("disk"u8));
    }

    private static string LinkCrossSoakLane(int lane, string markers) => $"[{lane}:{markers}]";

    private static string LinkCrossSoakMarker(int round, int lane) => $"<{round:X3}.{lane}🧪e\u0301>";

    private static string LinkCrossSoakHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
