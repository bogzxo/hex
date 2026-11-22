using Hex.Core.Git;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class SharedEditor(HexDocument tab, HexLinkPeer? peer, HexLinkDocSnapshot snapshot)
    {
        public HexDocument Tab { get; } = tab;
        public HexLinkPeer? Peer { get; } = peer;
        public HexLinkDocSnapshot Base = snapshot;
        public bool Busy, Conflict;
        public int Version = tab.Editor.Document.Version;
        public float Wait;
    }

    private SharedEditor? linkDocument;
    private int linkJoinGeneration;

    private void LinkDocumentCommands(List<(string Text, Action Picked)> commands)
    {
        if (linkDocument is { } shared)
        {
            commands.Add((Said("link.doc_leave", ("path", shared.Base.Path)), LinkDocumentStop));
            if (shared.Conflict)
            {
                commands.Add((Said("link.doc_compare"), () =>
                    OpenDiff(shared.Tab.Path!, "link-document", Said("link.doc_compare"),
                        GitDiffText.Of(shared.Base.Text, shared.Tab.Editor.Text), -1)));
                commands.Add((Said("link.doc_resume"), () => shared.Conflict = false));
            }
            return;
        }
        if (document is { IsText: true, Path: { } path, DiffOf: null } && !linkRemote.ContainsKey(path) && LinkWireOf(path) is { } wire)
            commands.Add((Said("link.doc_host", ("path", wire)), () => LinkDocumentHost(document, wire)));
        foreach (HexLinkPeer peer in LinkPeers.Where(p => HexLinkAllowed.Has(p.Granted, HexLinkRights.Edit)))
            commands.Add((Said("link.doc_join", ("device", peer.Device.Name)), () =>
                AskText(Said("link.doc_title"), Said("link.doc_prompt"), string.Empty, Said("link.doc_button"),
                    path => LinkDocumentJoin(peer, path.Trim()))));
    }

    private void LinkDocumentHost(HexDocument tab, string wire)
    {
        if (linkDocument != null || linkServices is null || tab.Path is null || LinkWireOf(tab.Path) != wire)
            return;
        try
        {
            var snapshot = linkServices.Documents.Share(wire, tab.Editor.Text);
            ++linkJoinGeneration;
            linkDocument = new(tab, null, snapshot);
            Say(Said("link.doc_hosted", ("path", wire)));
        }
        catch (Exception error) { Say(error.Message, error: true); }
    }

    private async void LinkLegacyDocumentJoin(HexLinkPeer peer, string wire)
    {
        int generation = ++linkJoinGeneration;
        try
        {
            var snapshot = await peer.JoinDocument(wire, linkCancel?.Token ?? default).ConfigureAwait(false);
            PostLink(() =>
            {
                if (generation != linkJoinGeneration || link?.PeerOf(peer.Device.Id) != peer || linkDocument != null)
                    return;
                try
                {
                    string folder = Path.Combine(linkData, "documents", Guid.NewGuid().ToString("N"));
                    string copy = LinkRemoteCopyIn(folder, wire);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.WriteAllText(copy, snapshot.Text);
                    if (!OpenText(copy, snapshot.Text))
                        throw new IOException(Said("link.doc_open_failed"));
                    linkDocument = new(document, peer, snapshot);
                    linkPresenceDrawnKey = string.Empty;
                    Say(Said("link.doc_joined", ("path", wire), ("device", peer.Device.Name)));
                }
                catch (Exception error) { Say(error.Message, error: true); }
            });
        }
        catch (Exception error) { PostLink(() => { if (generation == linkJoinGeneration) Say(error.Message, error: true); }); }
    }

    private void LinkDocumentStop()
    {
        ++linkJoinGeneration;
        if (linkDocument is not { } shared)
            return;
        if (shared.Peer is null)
            linkServices?.Documents.Close(shared.Base.Path);
        linkDocument = null;
        linkPresenceDrawnKey = string.Empty;
        linkPresenceSent = string.Empty;
    }

    private void UpdateLinkDocument(float dt)
    {
        if (linkDocument is not { } shared || linkServices is null)
            return;
        if (!documents.Contains(shared.Tab) || shared.Peer is { } peer &&
            (link?.PeerOf(peer.Device.Id) != peer || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit)))
        {
            LinkDocumentStop();
            return;
        }
        if (shared.Busy || shared.Conflict || (shared.Wait -= dt) > 0)
            return;
        shared.Wait = 0.12f;
        var basis = shared.Base;
        string submitted = shared.Tab.Editor.Document.Version == shared.Version ? basis.Text : shared.Tab.Editor.Text;
        bool changed = submitted != basis.Text;
        try
        {
            if (shared.Peer is { } host)
            {
                shared.Busy = true;
                var token = linkCancel?.Token ?? default;
                LinkDocumentResponse(shared, submitted, host, changed, token);
            }
            else
            {
                var snapshot = changed
                    ? linkServices.Documents.Apply(basis.Path, basis.Session, basis.Revision, submitted, linkServices.Device)
                    : linkServices.Documents.Snapshot(basis.Path);
                LinkDocumentReceived(shared, submitted, snapshot);
            }
        }
        catch (Exception error) { LinkDocumentFailed(shared, error); }
    }

    private async void LinkDocumentResponse(SharedEditor shared, string submitted, HexLinkPeer host, bool changed, CancellationToken token)
    {
        try
        {
            var basis = shared.Base;
            var snapshot = changed
                ? await host.EditDocument(basis.Path, basis.Session, basis.Revision, submitted, token).ConfigureAwait(false)
                : await host.PollDocument(basis.Path, basis.Session, basis.Revision, token).ConfigureAwait(false);
            PostLink(() =>
            {
                if (snapshot != null)
                    LinkDocumentReceived(shared, submitted, snapshot);
                else
                    shared.Busy = false;
            });
        }
        catch (Exception error) { PostLink(() => LinkDocumentFailed(shared, error)); }
    }

    private void LinkDocumentFailed(SharedEditor shared, Exception error)
    {
        if (linkDocument != shared)
            return;
        LinkDocumentStop();
        Say(Said("link.doc_ended", ("reason", error.Message)), error: true);
    }

    private void LinkDocumentReceived(SharedEditor shared, string submitted, HexLinkDocSnapshot snapshot)
    {
        if (linkDocument != shared)
            return;
        shared.Busy = false;
        if (snapshot.Session != shared.Base.Session)
        {
            LinkDocumentFailed(shared, new IOException(Said("link.doc_replaced")));
            return;
        }
        var buffer = shared.Tab.Editor.Document;
        if (shared.Version == buffer.Version && snapshot.Revision == shared.Base.Revision && !snapshot.Conflict)
            return;
        shared.Base = snapshot;
        string current = shared.Tab.Editor.Text;
        if (snapshot.Conflict || !HexLinkDocuments.TryMerge(submitted, current, snapshot.Text, out string merged))
        {
            shared.Conflict = true;
            Say(Said("link.doc_conflict"), error: true);
            return;
        }
        if (merged != current)
        {
            buffer.ApplyExternal(merged, static (before, local, remote) =>
                HexLinkDocuments.TryMerge(before, local, remote, out string revised) ? revised : null);
            if (shared.Tab != document)
            {
                shared.Tab.Modified = shared.Tab.Editor.Text != shared.Tab.SavedCode;
                tabsDirty = true;
            }
        }
        if (merged == snapshot.Text)
            shared.Version = buffer.Version;
    }
}
