using System.Text;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkCrossRecovery(HexPointerScript test, Func<HexLinkPeer?> paired, string? dial, string work)
    {
        const string wire = LINK_CROSS_FOLDER + "/recovery.txt";
        const string original = "original disk\r\n";
        const string seed = "Seed: \u00e9 / e\u0301 / \U0001f680\n";
        const string delivered = seed + "\u6f22\u5b57 \U0001f469\u200d\U0001f4bb\n";
        const string draft = delivered + "draft \U0001f9ea\n";
        const string refreshed = seed + "new session \u00df\n";
        const string reconciled = refreshed + "draft \U0001f9ea\n";
        const string echoSeen = "recovery-echo-seen.txt", resolutionSeen = "recovery-resolution-seen.txt";
        string path = Path.Combine(work, "recovery.txt");

        void Check(string label, Func<bool> ready)
        {
            test.WaitFor(ready, 30);
            test.Check("link-cross-recovery: " + label, ready);
        }

        if (dial is null)
        {
            HexLinkDocuments? docs = null;
            HexLinkDocSnapshot? first = null;
            HexLinkDocSnapshot? next = null;
            test.Run(() =>
            {
                File.WriteAllText(path, original);
                docs = linkServices?.Documents;
                first = docs?.Share(wire, seed);
            });
            Check("host receives the exact Unicode edit at revision one", () =>
                docs?.Snapshot(wire) is { Revision: 1, Text: delivered } && File.Exists(Path.Combine(work, echoSeen)));
            test.Run(() =>
            {
                if (docs is null || paired() is not { } peer) return;
                docs.Close(wire);
                next = docs.Share(wire, refreshed);
                LinkDisconnect(peer.Device.Id);
            });
            Check("host receives the explicitly reconciled draft in the new session", () =>
                next is not null && docs?.Snapshot(wire) is { Revision: 1, Text: reconciled } state
                && first is not null && state.Session == next.Session && state.Session != first.Session);
            Check("guest acknowledged the settled recovery", () => File.Exists(Path.Combine(work, resolutionSeen)));
            test.Check("link-cross-recovery: host disk keeps the original CRLF bytes", () =>
                File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(original)));
            test.Run(() => docs?.Close(wire));
            return;
        }

        HexLinkPeer? previous = null;
        HexDocument? draftTab = null;
        HexLinkDocSnapshot? basis = null;
        Task<HexLinkDocSnapshot>? send = null;
        Task<HexLinkPeer>? reconnect = null;
        Task<(HexLinkDocSnapshot Before, HexLinkDocSnapshot Refused, HexLinkDocSnapshot After)>? recover = null;

        test.WaitFor(() =>
        {
            if (linkDocument is not null) return true;
            if (paired() is { IsClosed: false } peer && peer.Workspace.Folders.Count > 0)
                previous = peer;
            return previous is not null;
        }, 30);
        // Give the listener time to create the document. Retry only until a join succeeds.
        double nextJoin = 0;
        test.WaitFor(() =>
        {
            if (linkDocument is not null) return true;
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (previous is { IsClosed: false } peer && now >= nextJoin)
            {
                nextJoin = now + 2;
                LinkLegacyDocumentJoin(peer, wire);
            }
            return false;
        }, 30);
        test.Check("link-cross-recovery: guest joined the exact Unicode seed", () =>
            linkDocument is { Base.Text: seed });
        test.Run(() =>
        {
            if (linkDocument is not { Peer: { } peer } shared) return;
            basis = shared.Base;
            draftTab = shared.Tab;
            shared.Wait = 3600;
            Rewrite(draftTab, draft);
            send = peer.EditDocument(wire, basis.Session, basis.Revision, delivered);
        });
        Check("guest received the exact Unicode echo", () =>
            send is { IsCompletedSuccessfully: true, Result: { Revision: 1, Text: delivered, Conflict: false } });
        test.Run(() =>
        {
            if (send is not { IsCompletedSuccessfully: true } || previous is null) return;
            // Keep automatic redial from racing the explicit reconnect.
            linkHeldOff.Add(previous.Device.Id);
            _ = previous.Write(LINK_CROSS_FOLDER + "/" + echoSeen, null, "seen\n"u8.ToArray());
        });
        Check("disconnect detaches the editor and preserves its unsent draft", () =>
            previous is { IsClosed: true } && linkDocument is null && draftTab?.Editor.Text == draft);
        test.Run(() =>
        {
            if (previous is not null && TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port))
                reconnect = LinkConnect(host, port, previous.Device.Id);
        });
        Check("known device reconnects without pairing", () =>
            reconnect is { IsCompletedSuccessfully: true } && reconnect.Result != previous);
        test.Run(() =>
        {
            if (reconnect is { IsCompletedSuccessfully: true } && basis is not null)
                recover = Reconcile(reconnect.Result, basis);
        });
        Check("stale session conflicts and explicit reconciliation advances only the new revision", () =>
            recover is { IsCompletedSuccessfully: true } &&
            recover.Result is ({ Revision: 0, Text: refreshed }, { Revision: 0, Text: refreshed, Conflict: true },
                { Revision: 1, Text: reconciled, Conflict: false }) &&
            recover.Result.Before.Session != basis?.Session &&
            recover.Result.Before.Session == recover.Result.Refused.Session &&
            recover.Result.Before.Session == recover.Result.After.Session &&
            draftTab?.Editor.Text == draft);
        test.Run(() =>
        {
            if (recover is { IsCompletedSuccessfully: true } && reconnect is { IsCompletedSuccessfully: true })
                _ = reconnect.Result.Write(LINK_CROSS_FOLDER + "/" + resolutionSeen, null, "seen\n"u8.ToArray());
        });

        async Task<(HexLinkDocSnapshot Before, HexLinkDocSnapshot Refused, HexLinkDocSnapshot After)> Reconcile(
            HexLinkPeer peer, HexLinkDocSnapshot old)
        {
            var before = await peer.JoinDocument(wire).ConfigureAwait(false);
            var refused = await peer.EditDocument(wire, old.Session, old.Revision, draft).ConfigureAwait(false);
            var after = await peer.EditDocument(wire, before.Session, before.Revision, reconciled).ConfigureAwait(false);
            return (before, refused, after);
        }
    }
}
