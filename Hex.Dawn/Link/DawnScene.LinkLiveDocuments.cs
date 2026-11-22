using Hex.Core.Git;
using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class LinkLiveDocument(HexDocument tab, string wire, HexLinkAuthority authority,
        HexLinkDocumentReplica replica, HexLinkDocumentRoom? room)
    {
        public HexDocument Tab { get; } = tab;
        public string Wire { get; } = wire;
        public HexLinkAuthority Authority { get; } = authority;
        public HexLinkDocumentReplica Replica { get; } = replica;
        public HexLinkDocumentRoom? Room { get; } = room;
        public ScopedDocumentDraft? Draft;
        public HexDocView Shown = replica.View;
        public IReadOnlyList<HexDocParticipant> Participants = replica.Participants;
        public HexLinkPeer? Connection;
        public int Version = tab.Editor.Document.Version;
        public bool Busy, Offline;
        public bool SaveRequested;
        public HexDocSaved? Saved;
        public readonly Queue<HexDocSaved> ConfirmedWrites = new();
        public readonly Queue<LinkLiveAgentWork> AgentEdits = new();
        public bool OriginalReadOnly = tab.Editor.ReadOnly;
        public float Wait;
    }

    private sealed record ScopedDocumentDraft(string SharedText, string Reason);
    private sealed record LinkLiveAgentWork(string Agent, string Name, string Before, string After, TaskCompletionSource<string?> Done);

    private readonly Dictionary<HexDocument, LinkLiveDocument> linkLiveDocuments = [];
    private readonly HashSet<string> linkLiveJoining = new(PlatformPaths.Comparer);
    private readonly HashSet<string> linkLivePaused = new(PlatformPaths.Comparer);
    private float linkLiveScan;

    private void LinkLiveDocumentCommands(List<(string Text, Action Picked)> commands)
    {
        if (linkLiveDocuments.TryGetValue(document, out var shared))
        {
            commands.Add((Said("link.doc_leave", ("path", shared.Wire)), () => LinkLiveDocumentClose(shared, true)));
            if (shared.Draft is { } draft)
                commands.Add((Said("link.doc_compare"), () => OpenDiff(shared.Tab.Path!, "link-document", Said("link.doc_compare"),
                    GitDiffText.Of(draft.SharedText, shared.Tab.Editor.Text), -1)));
            foreach (var member in shared.Participants.Where(p => p.Author != shared.Replica.Author && p.Caret is not null))
                commands.Add((Said("link.command_follow", ("device", member.Name)), () => LinkFollow(member.Device)));
            return;
        }

        if (document is { IsText: true, Path: { } path, DiffOf: null } && !linkRemote.ContainsKey(path) && LinkWireOf(path) is { } wire)
            commands.Add((Said("link.doc_host", ("path", wire)), () => LinkLiveDocumentHost(document, wire, explicitly: true)));
        foreach (HexLinkPeer peer in LinkPeers.Where(p => HexLinkAllowed.Has(p.Granted, HexLinkRights.View)))
            commands.Add((Said("link.doc_join", ("device", peer.Device.Name)), () =>
                AskText(Said("link.doc_title"), Said("link.doc_prompt"), string.Empty, Said("link.doc_button"),
                    path => LinkDocumentJoin(peer, path.Trim()))));
    }

    private void LinkLiveDocumentHost(HexDocument tab, string wire, bool explicitly)
    {
        if (linkServices is not { } services || tab.Path is null || linkLiveDocuments.ContainsKey(tab))
            return;
        try
        {
            HexLinkFolder folder = LiveFolder(wire) ?? throw HexLinkException.NotFound("The shared folder is no longer open.");
            if (!PlatformPaths.Same(HexLinkFiles.Resolve([folder], wire, services.Excludes), tab.Path))
                return;
            HexLinkAuthority pin = services.FolderAuthorities.Of(folder) ?? new HexLinkAuthority(services.Device.Id, 1);
            if (pin.DeviceId != services.Device.Id)
            {
                if (explicitly)
                    Say("This folder's owner must host the shared document.", error: true);
                return;
            }

            if (explicitly)
                pin = services.FolderAuthorities.Bind(folder, pin);
            linkLivePaused.Remove(wire);
            var room = services.Documents.Rooms.Open(wire, tab.Editor.Text,
                new HexDocFileBasis(tab.DiskText, tab.DiskEncoding, tab.SavedCrLf));
            var replica = new HexLinkDocumentReplica(room.Join(services.Device.Id, tab, services.Device));
            LinkLiveAttach(tab, wire, pin, replica, room, null);
            if (explicitly)
                Say(Said("link.doc_hosted", ("path", wire)));
        }
        catch (Exception error)
        {
            if (explicitly)
                Say(error.Message, error: true);
        }
    }

    private HexLinkFolder? LiveFolder(string wire)
    {
        int slash = wire.IndexOf('/');
        return slash > 0 ? linkServices?.Workspace.Folders.FirstOrDefault(folder =>
            PlatformPaths.Comparer.Equals(folder.Name, wire[..slash])) : null;
    }

    private async Task<HexLinkDocumentRoom> LinkLiveOpenRoom(LinkServices services, string wire, string full, HexLinkDevice caller,
        bool automatic, CancellationToken cancel)
    {
        if (services.Documents.Rooms.TryFind(wire) is { } existing)
            return existing;
        if (!automatic)
            throw HexLinkException.NotFound("This document is not shared.");

        string? openText = await OnEditorForLink(() =>
        {
            cancel.ThrowIfCancellationRequested();
            if (services != linkServices || linkLivePaused.Contains(wire) || LiveFolder(wire) is not { } folder
                || link?.PeerOf(caller.Id) is not { } peer || !LinkLiveSynced(peer, folder.Name))
                throw HexLinkException.NotFound("The caller has no linked sync for this document.");
            var pin = services.FolderAuthorities.Of(folder);
            if (pin is not null && pin.DeviceId != services.Device.Id || pin is null &&
                !LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted))
                throw HexLinkException.NotFound("This device does not own the shared folder.");

            if (Context.Documents.Find(full) is { IsText: true, DiffOf: null } tab)
            {
                LinkLiveDocumentHost(tab, wire, explicitly: false);
                return tab.Editor.Text;
            }
            return null;
        }).WaitAsync(cancel).ConfigureAwait(false);

        if (openText is null)
        {
            JsonObject read = await Task.Run(() => HexLinkFiles.Read(full, wire), cancel).ConfigureAwait(false);
            if (read["text"] is not JsonValue value || !value.TryGetValue(out openText))
                throw HexLinkException.NotFound("Only text documents can be shared.");
            return services.Documents.Rooms.Open(wire, openText.ReplaceLineEndings("\n"), HexDocFileBasis.FromRead(read, openText));
        }

        return services.Documents.Rooms.Find(wire);
    }

    private void LinkDocumentJoin(HexLinkPeer peer, string wire)
    {
        var folder = LiveFolder(wire);
        var remoteFolder = peer.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, wire.Split('/')[0]));
        var pinned = folder is null ? null : linkServices?.FolderAuthorities.Of(folder);
        var pin = pinned ?? remoteFolder?.Authority ?? new HexLinkAuthority(peer.Device.Id, 1);
        var route = LinkLiveRoute(pin, peer);
        if (route is null)
        {
            Say(Said("link.doc_offline", ("path", wire)), error: true);
            return;
        }
        linkLivePaused.Remove(wire);
        var tab = LinkLocalOf(wire) is { } local ? Context.Documents.Find(local) : null;
        LinkLiveJoin(route, wire, pin, tab, explicitly: true);
    }

    private HexLinkPeer? LinkLiveRoute(HexLinkAuthority pin, HexLinkPeer? preferred = null)
    {
        if (link?.PeerOf(pin.DeviceId) is { IsClosed: false } direct)
            return direct;
        if (preferred is { IsClosed: false } && HexLinkAllowed.Has(preferred.Granted, HexLinkRights.Steer))
            return preferred;
        return LinkPeers.FirstOrDefault(peer => !peer.IsClosed && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer));
    }

    private static string? LinkLiveRelay(HexLinkAuthority pin, HexLinkPeer peer) =>
        pin.DeviceId == peer.Device.Id ? null : pin.DeviceId;

    private async void LinkLiveJoin(HexLinkPeer peer, string wire, HexLinkAuthority pin, HexDocument? tab, bool explicitly)
    {
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) || tab is not null && linkLiveDocuments.ContainsKey(tab)
            || !linkLiveJoining.Add(wire))
            return;

        try
        {
            var cancel = linkCancel?.Token ?? default;
            string data = linkData;
            string? relay = LinkLiveRelay(pin, peer);
            HexDocExchange opened = await peer.OpenDocument(wire, cancel: cancel, automatic: !explicitly, relayTo: relay).ConfigureAwait(false);
            var replica = new HexLinkDocumentReplica(opened);
            while (replica.Revision < replica.Head)
            {
                var next = await peer.ExchangeDocument(wire, replica.Epoch, replica.Revision, [], cancel: cancel, relayTo: relay).ConfigureAwait(false);
                replica.Receive(next);
            }
            string? copy = null;
            if (tab is null)
            {
                string folder = Path.Combine(data, "documents", Guid.NewGuid().ToString("N"));
                copy = LinkRemoteCopyIn(folder, wire);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await File.WriteAllTextAsync(copy, replica.View.Text, cancel).ConfigureAwait(false);
            }

            PostLink(() =>
            {
                linkLiveJoining.Remove(wire);
                if (link?.PeerOf(peer.Device.Id) != peer || linkLivePaused.Contains(wire))
                    return;
                if (tab is null)
                {
                    if (!OpenText(copy!, replica.View.Text))
                        return;
                    tab = document;
                }
                if (!documents.Contains(tab) || linkLiveDocuments.ContainsKey(tab))
                    return;
                LinkLiveAttach(tab, wire, pin, replica, null, peer);
                if (explicitly)
                    Say(Said("link.doc_joined", ("path", wire), ("device", peer.Device.Name)));
            });
        }
        catch (Exception error)
        {
            PostLink(() =>
            {
                linkLiveJoining.Remove(wire);
                if (explicitly)
                    Say(error.Message, error: true);
            });
        }
    }

    private void LinkLiveAttach(HexDocument tab, string wire, HexLinkAuthority pin, HexLinkDocumentReplica replica,
        HexLinkDocumentRoom? room, HexLinkPeer? peer)
    {
        var shared = new LinkLiveDocument(tab, wire, pin, replica, room) { Connection = peer };
        linkLiveDocuments.Add(tab, shared);
        if (tab.Editor.Text != shared.Shown.Text)
        {
            if (tab.Modified || tab.Editor.Text != tab.SavedCode)
            {
                LinkLiveDraft(shared, "The local tab has unsaved text when the shared document started.");
                return;
            }
            LinkLiveApply(shared, shared.Shown, null);
        }
        LinkLiveReadRights(shared, peer);
        linkPresenceDrawnKey = string.Empty;
        shared.Tab.Editor.RemoteCarets = LiveCarets(shared.Shown, shared.Participants, shared.Replica.Author);
        tabsDirty = true;
    }

    private void LinkLiveReadRights(LinkLiveDocument shared, HexLinkPeer? peer)
    {
        if (peer is not null)
            shared.Tab.Editor.ReadOnly = shared.OriginalReadOnly || !shared.Replica.CanEdit || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
    }

    private void LinkLiveDraft(LinkLiveDocument shared, string reason)
    {
        shared.Draft = new(shared.Replica.View.Text, reason);
        shared.Tab.Editor.ReadOnly = shared.OriginalReadOnly;
        shared.Tab.Editor.RemoteCarets = [];
        tabsDirty = true;
        Say(Said("link.doc_draft") + " " + reason, error: true);
    }

    private void LinkLiveDocumentClose(LinkLiveDocument shared, bool explicitly)
    {
        if (!linkLiveDocuments.Remove(shared.Tab))
            return;
        while (shared.AgentEdits.TryDequeue(out var request))
            request.Done.TrySetResult("The shared document closed before the edit.");

        if (explicitly)
            linkLivePaused.Add(shared.Wire);
        if (shared.Room is { } room)
        {
            room.Leave(shared.Authority.DeviceId, shared.Tab);
            linkServices?.Documents.Rooms.Close(shared.Wire);
        }
        else if (shared.Connection is { IsClosed: false } peer)
            _ = LinkLiveLeave(peer, shared.Wire, LinkLiveRelay(shared.Authority, peer));
        shared.Tab.Editor.ReadOnly = shared.OriginalReadOnly;
        shared.Tab.Editor.RemoteCarets = [];
        linkPresenceDrawnKey = string.Empty;
        tabsDirty = true;
    }

    private static async Task LinkLiveLeave(HexLinkPeer peer, string wire, string? relay)
    {
        try { await peer.LeaveDocument(wire, relayTo: relay).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or HexLinkException) { }
    }

    private void LinkLiveStopAll()
    {
        foreach (var shared in linkLiveDocuments.Values.ToArray())
            LinkLiveDocumentClose(shared, false);
        linkLivePaused.Clear();
        linkLiveJoining.Clear();
    }

    private void UpdateLinkLiveDocuments(float dt)
    {
        if (linkServices is not { } services)
            return;
        if ((linkLiveScan -= dt) <= 0)
        {
            linkLiveScan = 0.25f;
            LinkLiveAutoOpen(services);
        }
        foreach (var shared in linkLiveDocuments.Values.ToArray())
        {
            if (!documents.Contains(shared.Tab))
            {
                LinkLiveDocumentClose(shared, false);
                continue;
            }

            if (shared.Busy || shared.Draft is not null || (shared.Wait -= dt) > 0)
                continue;
            if (LiveFolder(shared.Wire) is { } folder && services.FolderAuthorities.Of(folder) is { } pin && pin != shared.Authority)
            {
                if (shared.Room is not null)
                    services.Documents.Rooms.Close(shared.Wire);
                LinkLiveDraft(shared, "The folder's authority changed.");
                continue;
            }
            shared.Wait = 0.05f;
            var peer = shared.Room is null ? LinkLiveRoute(shared.Authority, shared.Connection) : null;
            if (shared.Room is null && (peer is null || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
            {
                if (!shared.Offline)
                {
                    shared.Offline = true;
                    shared.Tab.Editor.RemoteCarets = [];
                    tabsDirty = true;
                    Say(Said("link.doc_offline", ("path", shared.Wire)));
                }
                if (peer is not null)
                    shared.Tab.Editor.ReadOnly = true;
                shared.Connection = null;
                peer = null;
                if (shared.Version == shared.Tab.Editor.Document.Version)
                    continue;
            }

            LinkLiveReadRights(shared, peer);
            var buffer = shared.Tab.Editor.Document;
            string local = shared.Version == buffer.Version ? shared.Shown.Text : buffer.Text;
            var cursor = document == shared.Tab ? (buffer.Caret, buffer.Anchor, shared.Tab.Editor.PointerTextPosition) :
                ((UITextPosition, UITextPosition, UITextPosition?)?)null;
            bool editable = !shared.Tab.Editor.ReadOnly;
            shared.Busy = true;
            LinkLiveStep(shared, peer, local, buffer.Version, cursor, editable, linkCancel?.Token ?? default);
        }
    }

    private void LinkLiveAutoOpen(LinkServices services)
    {
        if (document is not { IsText: true, Path: { } path, DiffOf: null } tab || linkRemote.ContainsKey(path)
            || linkLiveDocuments.ContainsKey(tab) || linkDocument?.Tab == tab || LinkWireOf(path) is not { } wire
            || linkLivePaused.Contains(wire) || LiveFolder(wire) is not { } folder)
            return;

        var atFile = linkPresence.Values.Where(each => each.Presence?.File == wire)
            .Select(each => link?.PeerOf(each.Device.Id)).OfType<HexLinkPeer>()
            .Where(peer => LinkLiveSynced(peer, folder.Name)).ToArray();
        var pin = services.FolderAuthorities.Of(folder);
        if (atFile.Length == 0 && (pin is null || !linkPresence.Values.Any(each => each.Presence?.File == wire)
            || !LinkPeers.Any(peer => LinkLiveSynced(peer, folder.Name))))
            return;
        if (pin is null)
        {
            if (atFile.Length != 1)
                return;
            var peer = atFile[0];
            pin = peer.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name))?.Authority;
            pin ??= new HexLinkAuthority(LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted)
                ? services.Device.Id : peer.Device.Id, 1);
        }
        if (pin.DeviceId == services.Device.Id)
            LinkLiveDocumentHost(tab, wire, explicitly: false);
        else if (LinkLiveRoute(pin) is { } owner)
            LinkLiveJoin(owner, wire, pin, tab, explicitly: false);
    }

    private bool LinkLiveSynced(HexLinkPeer peer, string name)
    {
        lock (linkSyncGate)
            return linkSyncs.TryGetValue(LinkSyncKey(peer.Device.Id, name), out var entry) && entry.Sync is { State: not HexLinkSyncState.Off }
                || peer.ChosenSyncFolders.Contains(name, PlatformPaths.Comparer);
    }

    private async void LinkLiveStep(LinkLiveDocument shared, HexLinkPeer? peer, string local, int version,
        (UITextPosition Caret, UITextPosition Anchor, UITextPosition? Pointer)? cursor, bool editable, CancellationToken cancel)
    {
        HexDocView basis = shared.Shown;
        HexDocView captured = basis;
        Guid epoch = shared.Replica.Epoch;
        Exception? error = null;
        IReadOnlyList<UICodeRemoteCaret>? carets = null;
        HexDocSaved? save = null;
        bool saveRequested = shared.SaveRequested;
        shared.AgentEdits.TryPeek(out var agent);
        bool agentAttempted = false;
        string? agentError = null;
        string? localPath = shared.Tab.Path;
        try
        {
            await Task.Run(async () =>
            {
                var replica = shared.Replica;
                if (editable)
                    captured = replica.Capture(basis, local);
                else if (basis.Text != local)
                    throw new InvalidOperationException("Edit permission changed while the tab had local edits.");
                if (!editable && replica.HasPending)
                    throw new InvalidOperationException("Edit permission changed while edits were awaiting acknowledgement.");

                HexDocPoint? caret = null, anchor = null, pointer = null;
                if (cursor is { } here)
                {
                    caret = LivePoint(captured, here.Caret);
                    anchor = LivePoint(captured, here.Anchor);
                    pointer = here.Pointer is { } mouse ? LivePoint(captured, mouse) : null;
                }

                if (shared.Room is null && peer is null)
                    return;

                HexDocExchange response;
                if (shared.Room is { } room)
                    response = room.Exchange(shared.Authority.DeviceId, shared.Tab, replica.Epoch, replica.Revision,
                        replica.Batch(), caret, anchor, pointer, false);
                else if (peer != shared.Connection)
                    response = await peer!.OpenDocument(shared.Wire, replica.Epoch, replica.Revision, cancel,
                        relayTo: LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
                else
                    response = await peer!.ExchangeDocument(shared.Wire, replica.Epoch, replica.Revision,
                        replica.Batch(), caret, anchor, pointer, cancel: cancel,
                        relayTo: LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);

                replica.Receive(response);
                if (agent is not null && editable && replica.CanEdit && !replica.HasPending && replica.Revision == replica.Head)
                {
                    agentAttempted = true;
                    try
                    {
                        var change = new HexDocAgentChange(agent.Agent, agent.Name, HexDocAgentChange.HashText(agent.Before),
                            HexDocSplice.Between(agent.Before, agent.After));
                        var revised = shared.Room is { } authority
                            ? authority.AgentEdit(shared.Authority.DeviceId, shared.Tab,
                                new HexLinkDevice(shared.Authority.DeviceId, shared.Authority.DeviceId), replica.Epoch, replica.Revision, change)
                            : await peer!.EditDocumentAsAgent(shared.Wire, replica.Epoch, replica.Revision, change, cancel,
                                LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
                        replica.Receive(revised);
                    }
                    catch (Exception e) { agentError = e.Message; }
                }
                if (saveRequested && editable && replica.CanEdit && !replica.HasPending && replica.Revision == replica.Head)
                    save = shared.Room is { } owned
                        ? await owned.Save(shared.Authority.DeviceId, shared.Tab, localPath!, replica.Epoch, replica.Revision, cancel).ConfigureAwait(false)
                        : await peer!.SaveDocument(shared.Wire, replica.Epoch, replica.Revision, cancel,
                            relayTo: LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
                carets = LiveCarets(replica.View, replica.Participants, replica.Author);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) { error = e; }

        PostLink(() =>
        {
            if (!linkLiveDocuments.TryGetValue(shared.Tab, out var still) || still != shared)
                return;
            shared.Busy = false;
            shared.Shown = captured;
            shared.Version = version;
            if (agentAttempted && agent is not null && shared.AgentEdits.TryPeek(out var pending) && pending == agent)
            {
                shared.AgentEdits.Dequeue();
                agent.Done.TrySetResult(agentError);
            }
            if (error is InvalidOperationException or ArgumentException
                || error is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED or HexLinkErrors.CONFLICT or HexLinkErrors.INVALID_REQUEST })
            {
                LinkLiveDraft(shared, error.Message);
                return;
            }
            if (error is not null)
            {
                shared.Wait = 1f;
                shared.Connection = null;
                if (!shared.Offline)
                {
                    shared.Offline = true;
                    Say(Said("link.doc_offline", ("path", shared.Wire)));
                }
                return;
            }

            if (shared.Room is null && peer is null)
                return;
            if (save is not null)
            {
                shared.SaveRequested = false;
                Say(Said("documents.saved", ("path", shared.Wire)));
            }
            LinkLiveRememberSaved(shared, save ?? shared.Replica.Saved);
            if (shared.Offline || !LiveSamePeople(shared.Participants, shared.Replica.Participants))
                tabsDirty = true;
            shared.Offline = false;
            shared.Connection = peer;
            LinkLiveReadRights(shared, peer);
            shared.Participants = shared.Replica.Participants;
            if (shared.Tab.Editor.Document.Version != version)
            {
                if (epoch != shared.Replica.Epoch)
                    LinkLiveDraft(shared, "The shared document restarted while this tab was changing.");
                return;
            }
            LinkLiveApply(shared, shared.Replica.View, epoch == shared.Replica.Epoch ? captured : null);
            LinkLiveMarkSaved(shared);
            if (carets is not null && !shared.Tab.Editor.RemoteCarets.SequenceEqual(carets))
                shared.Tab.Editor.RemoteCarets = carets;
        });
    }

    private void LinkLiveApply(LinkLiveDocument shared, HexDocView view, HexDocView? previous)
    {
        var buffer = shared.Tab.Editor.Document;
        bool following = linkFollowDocument == shared.Tab && linkFollowVersion == buffer.Version && linkFollowCaret == buffer.Caret;
        shared.Tab.Editor.ApplySharedText(view.Text, (basis, draft, changed) => HexLinkDocuments.TryMerge(basis, draft, changed, out string merged) ? merged : null,
            previous is null ? null : point => view.OffsetOf(LivePoint(previous, point)) is { } offset ? LivePosition(view, offset) : point);
        shared.Shown = view;
        shared.Version = buffer.Version;
        if (following)
        {
            linkFollowCaret = buffer.Caret;
            linkFollowVersion = buffer.Version;
        }
    }

    private LinkLiveDocument? LinkLiveOf(HexDocument? tab) =>
        tab is not null ? linkLiveDocuments.GetValueOrDefault(tab) : null;

    private IEnumerable<McpToolResult?> LinkLiveAgentWrite(JsonObject args, LinkLiveDocument shared,
        string before, string after, string said)
    {
        if (shared.Offline || shared.Draft is not null || shared.Tab.Editor.ReadOnly)
        {
            yield return McpToolResult.Error("The shared session cannot accept this edit. Its buffer and disk were kept.");
            yield break;
        }
        if (shared.AgentEdits.Count >= 16)
        {
            yield return McpToolResult.Error("The shared session is busy. Read the latest text and retry.");
            yield break;
        }

        int? caller = McpServer.CallingChat.Value;
        string name = caller is { } id ? ChatById(id)?.Shown ?? $"#{id}" : "MCP";
        var outcome = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        shared.AgentEdits.Enqueue(new(caller?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "mcp",
            HexLinkNames.Clean(name) ?? "MCP", before, after, outcome));
        shared.Wait = 0;
        while (!outcome.Task.IsCompleted)
            yield return null;
        if (outcome.Task.Result is { } error)
        {
            yield return McpToolResult.Error(error);
            yield break;
        }
        foreach (var step in McpThenCheck(args, shared.Tab.Path!, said + " Changed the shared buffer. It will reach disk when saved."))
            yield return step;
    }

    private bool LinkLiveTrySave(HexDocument tab, string path)
    {
        if (!linkLiveDocuments.TryGetValue(tab, out var shared) || tab.Path is null || !PlatformPaths.Same(tab.Path, path))
            return false;
        if (shared.Draft is not null)
        {
            Say(Said("link.doc_draft"), error: true);
            return true;
        }
        shared.SaveRequested = true;
        shared.Wait = 0;
        return true;
    }

    private static void LinkLiveRememberSaved(LinkLiveDocument shared, HexDocSaved? saved)
    {
        if (saved is null || shared.Saved == saved)
            return;
        shared.Saved = saved;
        shared.ConfirmedWrites.Enqueue(saved);
        if (shared.ConfirmedWrites.Count > 8)
            shared.ConfirmedWrites.Dequeue();
    }

    private void LinkLiveMarkSaved(LinkLiveDocument shared)
    {
        var replica = shared.Replica;
        if (shared.Saved is not { } saved || saved.Epoch != replica.Epoch || saved.Revision != replica.Revision || replica.HasPending)
            return;
        var tab = shared.Tab;
        if (tab.SavedCode == shared.Shown.Text)
            return;
        if (shared.Room is not null)
            tab.DiskText = DocumentFile.WithLineEndings(shared.Shown.Text, tab.DiskText, tab.SavedCrLf, tab.SavedCrLf);
        tab.SavedCode = shared.Shown.Text;
        tab.Modified = false;
        tabsDirty = true;
    }

    private async Task<bool> LinkLiveKnownSyncSave(HexDocument? tab, string hash, CancellationToken cancel)
    {
        if (tab is null)
            return false;
        for (int wait = 0; wait < 16; wait++)
        {
            bool? matched = await OnEditorForLink<bool?>(() =>
                linkLiveDocuments.TryGetValue(tab, out var shared) && shared.Draft is null
                    ? shared.ConfirmedWrites.Any(save => string.Equals(save.Hash, hash, StringComparison.OrdinalIgnoreCase)) : null)
                .WaitAsync(cancel).ConfigureAwait(false);
            if (matched is null || matched.Value)
                return matched == true;
            await Task.Delay(50, cancel).ConfigureAwait(false);
        }
        return false;
    }

    private static bool LiveSamePeople(IReadOnlyList<HexDocParticipant> before, IReadOnlyList<HexDocParticipant> after)
    {
        if (before.Count != after.Count)
            return false;
        for (int i = 0; i < before.Count; i++)
            if ((before[i].Author, before[i].Name) != (after[i].Author, after[i].Name))
                return false;
        return true;
    }

    private IReadOnlyList<System.Numerics.Vector4> LinkLiveTabColors(HexDocument tab) =>
        linkLiveDocuments.TryGetValue(tab, out var shared) && !shared.Offline && shared.Draft is null
            ? [.. shared.Participants.Where(p => p.Author != shared.Replica.Author).Select(p => LinkDeviceTint(p.Device, Skin))]
            : [];

    private string? LinkLiveTooltip(HexDocument tab)
    {
        if (!linkLiveDocuments.TryGetValue(tab, out var shared))
            return null;
        if (shared.Draft is { } draft)
            return Said("link.doc_draft") + " " + draft.Reason;
        if (shared.Offline)
            return Said("link.doc_offline", ("path", shared.Wire));
        string people = string.Join(", ", shared.Participants.Where(p => p.Author != shared.Replica.Author).Select(p => p.Name));
        return people.Length == 0 ? null : Said("link.doc_people", ("people", people));
    }
    private static HexDocPoint LivePoint(HexDocView view, UITextPosition position) =>
        view.PointAt(view.OffsetAt(position.Line, position.Column));

    private static UITextPosition LivePosition(HexDocView view, int offset)
    {
        var (line, column) = view.PositionAt(offset);
        return new(line, column);
    }

    private static IReadOnlyList<UICodeRemoteCaret> LiveCarets(HexDocView view, IReadOnlyList<HexDocParticipant> participants, int ownAuthor)
    {
        var result = new List<UICodeRemoteCaret>();
        foreach (HexDocParticipant member in participants)
        {
            if (member.Author == ownAuthor || member.Caret is not { } caret || view.OffsetOf(caret) is not { } offset)
                continue;
            UITextPosition at = LivePosition(view, offset);
            UITextPosition anchor = member.Anchor is { } mark && view.OffsetOf(mark) is { } anchored
                ? LivePosition(view, anchored) : at;
            UITextPosition? pointer = member.Pointer is { } mouse && view.OffsetOf(mouse) is { } pointed
                ? LivePosition(view, pointed) : null;
            result.Add(new(member.Device, member.Name, at.Line, at.Column, anchor.Line, anchor.Column,
                LinkDeviceColour(member.Device), pointer?.Line, pointer?.Column));
        }
        return result;
    }
}
