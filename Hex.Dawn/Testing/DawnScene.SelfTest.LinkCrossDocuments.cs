namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_CROSS_DOCUMENT_FILE = "shared-note.txt";
    private const string LINK_CROSS_DOCUMENT_PATH = LINK_CROSS_FOLDER + "/" + LINK_CROSS_DOCUMENT_FILE;

    private void LinkCrossPublishDocument(string work, string text)
    {
        File.WriteAllText(Path.Combine(work, LINK_CROSS_DOCUMENT_FILE), "disk");
        linkServices!.Documents.Share(LINK_CROSS_DOCUMENT_PATH, text);
    }

    // The listener owns the buffer. Each next action waits on an observation through the real session
    // or the listener's own document and audit state. No rendezvous files or additional connections.
    private void TestLinkCrossDocuments(HexPointerScript test, Func<HexLinkPeer?> peer, bool guest, string work)
    {
        const string path = LINK_CROSS_DOCUMENT_PATH;
        var guestCaret = new HexLinkPresence(path, 0, 2, 0, 1, 0, 3);
        var hostCaret = new HexLinkPresence(path, 0, 1, 0, 0, 0, 4);
        bool SawPresence(HexLinkPresence? expected) => peer() is { } other
            && LinkPresence.TryGetValue(other.Device.Id, out var seen) && seen.Presence == expected;

        void Await(string label, Func<bool> condition)
        {
            test.WaitFor(condition, 25);
            test.Check("link-cross-documents: " + label, condition);
        }

        void Check(string label, Func<bool> condition) =>
            test.Check("link-cross-documents: " + label, condition);

        // Let Dawn send its initial active-tab presence before injecting the protocol sample.
        // Neither side opens an editor in this fixture. The loopback link-documents suite covers drawing.
        test.Settle(1);
        if (!guest)
        {
            HexLinkDocSnapshot? Local() => linkServices?.Documents.Snapshot(path);
            void Commit(long revision, string text)
            {
                if (Local() is { Revision: var observed } current && observed == revision)
                    linkServices!.Documents.Apply(path, current.Session, revision, text, linkServices.Device);
            }

            Await("host received the guest caret, selection and distinct pointer", () => SawPresence(guestCaret));
            test.Run(() =>
            {
                peer()?.SetPresence(hostCaret);
                Commit(0, "Aseed");
            });
            Await("host and stale guest disjoint edits converged at revision 2", () => Local() is { Revision: 2, Text: "AseedB" });
            test.Run(() => Commit(2, "AseedBx"));
            Await("same-offset insertions converged in acceptance order at revision 4", () => Local() is { Revision: 4, Text: "AseedBxy" });
            test.Run(() => Commit(4, "AhostBxy"));
            Await("overlapping guest edit returned conflict without changing the host", () =>
                Local() is { Revision: 5, Text: "AhostBxy" } && peer() is { } other &&
                LinkAuditLatest(80).Any(entry => entry.Device == other.Device.Id &&
                    entry.Detail == $"doc/edit {path}, revision 5, conflict True"));
            test.Run(() =>
            {
                if (peer() is { } other)
                    LinkSetRights(other.Device.Id, HexLinkRights.View);
            });
            Await("guest cleared presence after observing the rights downgrade", () => SawPresence(null));
            Check("host logged Edit denial and kept authoritative text and disk unchanged", () =>
                Local() is { Revision: 5, Text: "AhostBxy" } && peer() is { } other &&
                LinkAuditLatest(80).Any(entry => entry.Event == "refused" && entry.Device == other.Device.Id &&
                    entry.Detail == "doc/edit, it needs edit") &&
                File.ReadAllText(Path.Combine(work, LINK_CROSS_DOCUMENT_FILE)) == "disk");
            test.Run(() => linkServices?.Documents.Close(path));
            test.WaitFor(() => LinkPeers.Count == 0, 35);
            return;
        }

        HexLinkDocSnapshot? basis = null, disjoint = null, sameOffset = null;
        Task<HexLinkDocSnapshot>? joined = null, edited = null;
        Task<HexLinkDocSnapshot?>? poll = null;
        Task? request = null;
        bool HadError(int code) => request?.Exception?.GetBaseException() is HexLinkException error && error.Code == code;

        void PollHost(HexLinkDocSnapshot? from)
        {
            if (peer() is { } host && from is not null)
                poll = WaitForLinkCrossDocumentChange(host, from);
        }

        void Propose(HexLinkDocSnapshot? from, string text)
        {
            edited = peer() is { } host && from is not null
                ? host.EditDocument(path, from.Session, from.Revision, text) : null;
        }

        test.Run(() => joined = peer()?.JoinDocument(path));
        Await("guest joined the host's unsaved text at revision 0", () =>
            joined is { IsCompletedSuccessfully: true, Result: { Text: "seed", Revision: 0, Conflict: false } });
        test.Run(() =>
        {
            basis = joined is { IsCompletedSuccessfully: true } ? joined.Result : null;
            if (peer() is { } host && basis is not null)
            {
                poll = host.PollDocument(path, basis.Session, basis.Revision);
            }
        });
        Await("guest poll of current revision returned unchanged", () => poll is { IsCompletedSuccessfully: true, Result: null });
        test.Run(() =>
        {
            peer()?.SetPresence(guestCaret);
            PollHost(basis);
        });
        Await("guest received the host caret, selection and distinct pointer", () => SawPresence(hostCaret));
        Await("guest polled the host prepend while retaining its original base", () =>
            poll is { IsCompletedSuccessfully: true, Result: { Revision: 1, Text: "Aseed", Conflict: false } });
        test.Run(() => Propose(basis, "seedB"));
        Await("guest stale append merged with the host prepend", () =>
            edited is { IsCompletedSuccessfully: true, Result: { Revision: 2, Text: "AseedB", Conflict: false } });
        test.Run(() =>
        {
            disjoint = edited is { IsCompletedSuccessfully: true } ? edited.Result : null;
            PollHost(disjoint);
        });
        Await("guest observed the host insertion at their shared offset", () =>
            poll is { IsCompletedSuccessfully: true, Result: { Revision: 3, Text: "AseedBx", Conflict: false } });
        test.Run(() => Propose(disjoint, "AseedBy"));
        Await("guest observed both insertions in acceptance order", () =>
            edited is { IsCompletedSuccessfully: true, Result: { Revision: 4, Text: "AseedBxy", Conflict: false } });
        test.Run(() =>
        {
            sameOffset = edited is { IsCompletedSuccessfully: true } ? edited.Result : null;
            PollHost(sameOffset);
        });
        Await("guest observed the host replacement", () =>
            poll is { IsCompletedSuccessfully: true, Result: { Revision: 5, Text: "AhostBxy", Conflict: false } });
        test.Run(() => Propose(sameOffset, "AguestBxy"));
        Await("overlapping guest replacement returned the authoritative conflict snapshot", () =>
            edited is { IsCompletedSuccessfully: true, Result: { Revision: 5, Text: "AhostBxy", Conflict: true } });
        Await("guest grant changed to View", () => peer()?.Granted == HexLinkRights.View);
        test.Run(() =>
        {
            if (peer() is { } host && basis is not null)
                request = host.EditDocument(path, basis.Session, 5, "denied");
        });
        test.WaitFor(() => request?.IsCompleted == true, 20);
        Check("guest edit was rejected after Edit revocation", () => HadError(HexLinkErrors.NOT_ALLOWED));
        test.Run(() =>
        {
            peer()?.SetPresence(null);
            if (peer() is { } host && basis is not null)
                request = WaitForLinkCrossDocumentChange(host, basis with { Revision = 5 });
        });
        test.WaitFor(() => request?.IsCompleted == true, 30);
        Check("guest detected that the host closed the document", () => HadError(HexLinkErrors.NOT_FOUND));
    }

    private static async Task<HexLinkDocSnapshot?> WaitForLinkCrossDocumentChange(HexLinkPeer host, HexLinkDocSnapshot from)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (await host.PollDocument(from.Path, from.Session, from.Revision, timeout.Token) is { } changed)
                return changed;
            await Task.Delay(150, timeout.Token);
        }
    }
}
