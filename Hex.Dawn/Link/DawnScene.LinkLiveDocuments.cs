using Hex.Core.Git;
using System.Diagnostics;
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
        public HexDocSaved? Saved, DiskSave;
        public long DiskSaveRetryAt;
        public int DiskSaveInFlight;
        public bool CheckingObservedSave;
        public readonly Queue<LinkLiveAgentWork> AgentEdits = new();
        public readonly Queue<bool> UndoRequests = new();
        public bool OriginalReadOnly = tab.Editor.ReadOnly;
        public float Wait;
        public long LastLocalChange = Environment.TickCount64;
        public long PresenceAt, MaintenanceAt, RetryAt;
        public (UITextPosition Caret, UITextPosition Anchor, UITextPosition? Pointer)? LastPresence;
        public LinkLiveRevision? RevisionReply;
        public bool HoldEditsForTest, ApplyingRemote;
        public Action? TextChanged;
        public CancellationTokenSource? RevisionWait;
        public HexLinkPeer? RevisionWaitUnsupported;
        public Action<LinkLiveTiming, string, long>? TestTiming;
        public Action? TestBeforeDiskSave;
    }

    private enum LinkLiveTiming { LocalChange, Dispatch, WorkerStart, WireReplay, Prepared }

    private sealed record LinkLiveRevision(Guid Epoch, long Revision, HexDocExchange Reply, long Arrived);
    private sealed record ScopedDocumentDraft(string SharedText, string Reason);
    private sealed record LinkLiveAgentWork(string Agent, string Name, string Before, string After, TaskCompletionSource<string?> Done);

    private readonly Dictionary<HexDocument, LinkLiveDocument> linkLiveDocuments = [];
    private readonly HashSet<string> linkLiveJoining = new(PlatformPaths.Comparer);
    private readonly HashSet<string> linkLivePaused = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, long> linkLiveRetry = new(PlatformPaths.Comparer);
    private float linkLiveScan;
    private uint linkLiveRelayCursor;

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
        HexLinkPeer[] relays = [.. LinkPeers.Where(peer => !peer.IsClosed && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))];
        return relays.Length == 0 ? null : relays[(int)(linkLiveRelayCursor++ % (uint)relays.Length)];
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
                if (LiveFolder(wire) is { } localFolder &&
                    linkServices?.FolderAuthorities.Of(localFolder) is { } current && current != pin)
                {
                    if (explicitly)
                        Say(Said("link.sync_authority_required", ("folder", localFolder.Name)), error: true);
                    _ = LinkLiveLeave(peer, wire, relay);
                    return;
                }
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
        tab.Editor.Document.SharedUndo = redo =>
        {
            if (tab.Editor.ReadOnly || shared.Draft is not null || shared.UndoRequests.Count >= 32)
                return;
            shared.UndoRequests.Enqueue(redo);
            LinkLiveFlush(shared, urgent: true);
        };
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
        var buffer = tab.Editor.Document;
        shared.TextChanged = () =>
        {
            if (!shared.ApplyingRemote)
            {
                shared.TestTiming?.Invoke(LinkLiveTiming.LocalChange, buffer.Text, Stopwatch.GetTimestamp());
                LinkLiveFlush(shared, urgent: true);
            }
        };
        buffer.Changed += shared.TextChanged;
        LinkLiveStartRevisionWait(shared);
        linkPresenceDrawnKey = string.Empty;
        shared.Tab.Editor.RemoteCarets = LinkCaretsForShared(shared, LiveCarets(shared.Shown, shared.Participants, shared.Replica.Author));
        tabsDirty = true;
    }

    private void LinkLiveReadRights(LinkLiveDocument shared, HexLinkPeer? peer)
    {
        if (peer is null)
            return;
        bool wasReadOnly = shared.Tab.Editor.ReadOnly;
        shared.Tab.Editor.ReadOnly = shared.OriginalReadOnly || !shared.Replica.CanEdit || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
        if (wasReadOnly != shared.Tab.Editor.ReadOnly)
            MarkLinkEditorNotices();
    }

    private void LinkLiveDraft(LinkLiveDocument shared, string reason, string? authorityText = null)
    {
        shared.Draft = new(authorityText ?? shared.Replica.View.Text, reason);
        linkPresenceDrawnKey = string.Empty;
        LinkLiveStopRevisionWait(shared);
        if (shared.TextChanged is { } changed)
        {
            shared.Tab.Editor.Document.Changed -= changed;
            shared.TextChanged = null;
        }
        while (shared.AgentEdits.TryDequeue(out var request))
            request.Done.TrySetResult(reason);
        shared.Tab.Editor.Document.SharedUndo = null;
        shared.Tab.Editor.ReadOnly = shared.OriginalReadOnly;
        shared.Tab.Editor.RemoteCarets = [];
        tabsDirty = true;
        Say(Said("link.doc_draft") + " " + reason, error: true);
    }

    private void LinkLiveDocumentClose(LinkLiveDocument shared, bool explicitly)
    {
        if (!linkLiveDocuments.Remove(shared.Tab))
            return;
        LinkLiveStopRevisionWait(shared);
        if (shared.TextChanged is { } changed)
            shared.Tab.Editor.Document.Changed -= changed;
        while (shared.AgentEdits.TryDequeue(out var request))
            request.Done.TrySetResult("The shared document closed before the edit.");

        if (explicitly)
            linkLivePaused.Add(shared.Wire);
        if (shared.Room is { } room)
        {
            room.Leave(shared.Authority.DeviceId, shared.Tab);
            if (explicitly)
                linkServices?.Documents.Rooms.Close(shared.Wire);
        }
        else if (shared.Connection is { IsClosed: false } peer)
            _ = LinkLiveLeave(peer, shared.Wire, LinkLiveRelay(shared.Authority, peer));
        shared.Tab.Editor.Document.SharedUndo = null;
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
        linkLiveRetry.Clear();
    }

    private void UpdateLinkLiveDocuments(float dt)
    {
        if (linkServices is not { } services)
            return;
        if ((linkLiveScan -= dt) <= 0)
        {
            linkLiveScan = 0.25f;
            foreach (HexDocument tab in documents)
                LinkLiveAutoOpen(services, tab);
        }
        foreach (var shared in linkLiveDocuments.Values.ToArray())
        {
            if (!documents.Contains(shared.Tab))
            {
                LinkLiveDocumentClose(shared, false);
                continue;
            }

            if (!shared.Busy)
                shared.Wait -= dt;
            LinkLiveFlush(shared, urgent: shared.RevisionReply is not null || shared.Version != shared.Tab.Editor.Document.Version);
        }
    }

    private void LinkLiveFlush(LinkLiveDocument shared, bool urgent)
    {
        long now = Environment.TickCount64;
        if (linkServices is not { } services || shared.Busy || shared.Draft is not null || shared.HoldEditsForTest
            || shared.RetryAt > now || !urgent && shared.Wait > 0)
            return;
        if (LiveFolder(shared.Wire) is { } folder && services.FolderAuthorities.Of(folder) is { } pin && pin != shared.Authority)
        {
            if (shared.Room is not null)
                services.Documents.Rooms.Close(shared.Wire);
            LinkLiveDraft(shared, "The folder's authority changed.");
            return;
        }

        shared.Wait = 0.05f;
        var peer = shared.Room is null ? LinkLiveRoute(shared.Authority, shared.Connection) : null;
        if (peer != shared.Connection)
            LinkLiveStopRevisionWait(shared);
        if (shared.Room is null && (peer is null || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
        {
            LinkLiveStopRevisionWait(shared);
            if (!shared.Offline)
            {
                shared.Offline = true;
                linkPresenceDrawnKey = string.Empty;
                while (shared.AgentEdits.TryDequeue(out var request))
                    request.Done.TrySetResult("The shared document's owner is offline.");
                shared.Tab.Editor.RemoteCarets = [];
                tabsDirty = true;
                Say(Said("link.doc_offline", ("path", shared.Wire)));
            }
            if (peer is not null)
                shared.Tab.Editor.ReadOnly = true;
            shared.Connection = null;
            peer = null;
            if (shared.Version == shared.Tab.Editor.Document.Version && shared.UndoRequests.Count == 0)
                return;
        }

        LinkLiveReadRights(shared, peer);
        var buffer = shared.Tab.Editor.Document;
        bool changed = shared.Version != buffer.Version;
        if (changed)
            shared.LastLocalChange = now;
        var cursor = document == shared.Tab ? (buffer.Caret, buffer.Anchor, shared.Tab.Editor.PointerTextPosition) :
            ((UITextPosition, UITextPosition, UITextPosition?)?)null;
        var wake = shared.RevisionReply;
        shared.RevisionReply = null;
        var replica = shared.Replica;
        bool connecting = peer != shared.Connection;
        bool localWork = changed || replica.HasPending || replica.Revision < replica.Head || shared.SaveRequested
            || shared.AgentEdits.Count > 0 || shared.UndoRequests.Count > 0;
        bool useReplay = wake is not null && !connecting && !localWork && (wake.Epoch, wake.Revision) == (replica.Epoch, replica.Revision);
        bool updatePresence = !useReplay && now >= shared.PresenceAt && (connecting || cursor != shared.LastPresence);
        if (!useReplay && !localWork && !updatePresence && !connecting && shared.MaintenanceAt > now)
        {
            LinkLiveStartRevisionWait(shared);
            return;
        }

        if (!useReplay)
            shared.MaintenanceAt = now + (peer is not null && peer == shared.RevisionWaitUnsupported ? 50 : 250);
        shared.Busy = true;
        LinkLiveStep(shared, peer, changed ? buffer.Text : shared.Shown.Text, buffer.Version, cursor,
            !shared.Tab.Editor.ReadOnly, updatePresence, useReplay ? wake : null, linkCancel?.Token ?? default);
    }

    private void LinkLiveStartRevisionWait(LinkLiveDocument shared)
    {
        var peer = shared.Connection;
        if (shared.RevisionWait is not null || shared.Room is null &&
            (peer is null || peer == shared.RevisionWaitUnsupported || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
            return;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(linkCancel?.Token ?? default);
        shared.RevisionWait = lifetime;
        _ = LinkLiveWaitForRevision(shared, peer, shared.Replica.Epoch, shared.Replica.Revision, shared.Replica.RosterVersion,
            shared.Saved?.Revision ?? -1, lifetime);
    }

    private static void LinkLiveStopRevisionWait(LinkLiveDocument shared)
    {
        var previous = shared.RevisionWait;
        shared.RevisionWait = null;
        shared.RevisionReply = null;
        previous?.Cancel();
    }

    private async Task LinkLiveWaitForRevision(LinkLiveDocument shared, HexLinkPeer? peer, Guid epoch, long revision,
        long roster, long knownSaved, CancellationTokenSource lifetime)
    {
        HexDocExchange? reply = null;
        bool timedOut = false, unsupported = false;
        long arrived = 0;
        try
        {
            reply = shared.Room is { } room
                ? await Task.Run(() => room.WaitForRevision(shared.Authority.DeviceId, shared.Tab, epoch, revision,
                    lifetime.Token, roster, knownSaved), lifetime.Token).ConfigureAwait(false)
                : await peer!.WaitForDocumentRevision(shared.Wire, epoch, revision, lifetime.Token,
                    LinkLiveRelay(shared.Authority, peer), roster, knownSaved).ConfigureAwait(false);
            timedOut = reply is null;
            if (reply is not null && shared.TestTiming is not null)
                arrived = Stopwatch.GetTimestamp();
        }
        catch (HexLinkException error) when (error.Code == HexLinkErrors.METHOD_NOT_FOUND) { unsupported = true; }
        catch (Exception error) when (error is IOException or HexLinkException or OperationCanceledException) { }
        PostLink(() =>
        {
            lifetime.Dispose();
            if (shared.RevisionWait != lifetime)
                return;
            shared.RevisionWait = null;
            if (unsupported)
                shared.RevisionWaitUnsupported = peer;
            if (reply is not null)
            {
                shared.RevisionReply = new(epoch, revision, reply, arrived);
                LinkLiveFlush(shared, urgent: true);
            }
            else if (timedOut && !shared.Busy && (shared.Room is not null || peer is { IsClosed: false }))
                LinkLiveStartRevisionWait(shared);
        });
    }

    private void LinkLiveAutoOpen(LinkServices services, HexDocument tab)
    {
        if (tab is not { IsText: true, Path: { } path, DiffOf: null } || linkRemote.ContainsKey(path)
            || linkLiveDocuments.ContainsKey(tab) || linkDocument?.Tab == tab || LinkWireOf(path) is not { } wire
            || linkLivePaused.Contains(wire) || linkLiveJoining.Contains(wire) || LiveFolder(wire) is not { } folder)
            return;

        var pin = services.FolderAuthorities.Of(folder);
        if (pin is null)
        {
            HexLinkPeer[] common = [.. LinkPeers.Where(peer =>
                peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name)))];
            if (common.Length != 1)
                return;
            var peer = common[0];
            pin = peer.Workspace.Folders.First(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name)).Authority;
            pin ??= new HexLinkAuthority(LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted)
                ? services.Device.Id : peer.Device.Id, 1);
        }

        if (pin.DeviceId == services.Device.Id)
        {
            if (linkPresence.Values.Any(each => each.Presence?.File == wire) && LinkPeers.Any(peer => LinkLiveSynced(peer, folder.Name)))
                LinkLiveDocumentHost(tab, wire, explicitly: false);
        }
        else if (LinkLiveRoute(pin) is { } owner && Environment.TickCount64 >= linkLiveRetry.GetValueOrDefault(wire))
        {
            linkLiveRetry[wire] = Environment.TickCount64 + 1500;
            try
            {
                if (PlatformPaths.Same(HexLinkFiles.Resolve([folder], wire, services.Excludes), path))
                    LinkLiveJoin(owner, wire, pin, tab, explicitly: false);
            }
            catch (HexLinkException) { }
        }
    }

    private bool LinkLiveSynced(HexLinkPeer peer, string name)
    {
        lock (linkSyncGate)
            return linkSyncs.TryGetValue(LinkSyncKey(peer.Device.Id, name), out var entry) && entry.Sync is { State: HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly }
                || peer.ChosenSyncFolders.Contains(name, PlatformPaths.Comparer);
    }

    private async void LinkLiveStep(LinkLiveDocument shared, HexLinkPeer? peer, string local, int version,
        (UITextPosition Caret, UITextPosition Anchor, UITextPosition? Pointer)? cursor, bool editable, bool updatePresence,
        LinkLiveRevision? replay, CancellationToken cancel)
    {
        shared.TestTiming?.Invoke(LinkLiveTiming.Dispatch, local, Stopwatch.GetTimestamp());
        HexDocView basis = shared.Shown;
        HexDocView captured = basis;
        Guid epoch = shared.Replica.Epoch;
        Exception? error = null;
        IReadOnlyList<UICodeRemoteCaret>? carets = null;
        IReadOnlyList<UITextEdit>? patches = null;
        HexDocSaved? save = null;
        string? saveError = null;
        bool saveRequested = shared.SaveRequested;
        shared.AgentEdits.TryPeek(out var agent);
        bool idle = Environment.TickCount64 - shared.LastLocalChange > 2000;
        bool[] undo = [.. shared.UndoRequests];
        shared.UndoRequests.Clear();
        bool agentAttempted = false;
        string? agentError = null;
        string? localPath = shared.Tab.Path;
        string? diskText = shared.Tab.DiskText;
        HexDocSaved? diskSave = shared.DiskSave;
        bool retryRelayedSave = peer is not null && LinkLiveRelay(shared.Authority, peer) is not null
            && localPath is not null && shared.DiskSaveRetryAt <= Environment.TickCount64;
        var diskEncoding = shared.Tab.DiskEncoding;
        bool diskCrLf = shared.Tab.SavedCrLf;
        HexDocSavedSnapshot? written = null;
        bool retryDiskSave = false;
        try
        {
            await Task.Run(async () =>
            {
                long started = shared.TestTiming is not null ? Stopwatch.GetTimestamp() : 0;
                var replica = shared.Replica;
                if (editable)
                    captured = replica.Capture(basis, local);
                else if (basis.Text != local)
                    throw new InvalidOperationException("Edit permission changed while the tab had local edits.");
                if (!editable && replica.HasPending)
                    throw new InvalidOperationException("Edit permission changed while edits were awaiting acknowledgement.");
                if (editable)
                    foreach (bool redo in undo)
                        replica.ChangeUndo(redo);

                HexDocPoint? caret = null, anchor = null, pointer = null;
                if (updatePresence && cursor is { } here)
                {
                    caret = LivePoint(captured, here.Caret);
                    anchor = LivePoint(captured, here.Anchor);
                    pointer = here.Pointer is { } mouse ? LivePoint(captured, mouse) : null;
                }

                if (shared.Room is null && peer is null)
                    return;

                bool ready = idle && undo.Length == 0 && !saveRequested && agent is null &&
                    replica.CheckpointSuggested && !replica.HasPending && replica.Revision == replica.Head;
                HexDocExchange response;
                if (replay is not null)
                    response = replay.Reply;
                else if (shared.Room is { } room)
                    response = room.Exchange(shared.Authority.DeviceId, shared.Tab, replica.Epoch, replica.Revision,
                        replica.Batch(), caret, anchor, pointer, ready, replica.RosterVersion, updatePresence);
                else if (peer != shared.Connection)
                    response = await peer!.OpenDocument(shared.Wire, replica.Epoch, replica.Revision, cancel,
                        relayTo: LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
                else
                    response = await peer!.ExchangeDocument(shared.Wire, replica.Epoch, replica.Revision,
                        replica.Batch(), caret, anchor, pointer, checkpointReady: ready, cancel: cancel,
                        relayTo: LinkLiveRelay(shared.Authority, peer), knownRoster: replica.RosterVersion, updatePresence: updatePresence).ConfigureAwait(false);

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
                {
                    try
                    {
                        save = shared.Room is { } owned
                            ? await owned.Save(shared.Authority.DeviceId, shared.Tab, localPath!, replica.Epoch, replica.Revision, cancel).ConfigureAwait(false)
                            : await peer!.SaveDocument(shared.Wire, replica.Epoch, replica.Revision, cancel,
                                relayTo: LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or HexLinkException or UnauthorizedAccessException)
                    {
                        saveError = e.Message;
                    }
                }
                HexDocSaved? receipt = save ?? replica.Saved;
                if (receipt is not null && receipt != diskSave)
                {
                    if (shared.Room is { } authority)
                        written = authority.SavedOnDisk();
                    else if (retryRelayedSave)
                    {
                        try
                        {
                            var relayBasis = new HexDocFileBasis(diskText, diskEncoding, diskCrLf);
                            retryDiskSave = !await LinkLiveSaveRelayed(shared, peer!, localPath!, relayBasis, receipt, cancel).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is IOException or HexLinkException or UnauthorizedAccessException or TimeoutException)
                        {
                            retryDiskSave = true;
                            if (save is not null)
                                saveError ??= e.Message;
                        }
                    }
                }
                var updated = replica.View;
                if (shared.TestTiming is { } trace)
                {
                    if (replay is { Arrived: > 0 })
                        trace(LinkLiveTiming.WireReplay, updated.Text, replay.Arrived);
                    trace(LinkLiveTiming.WorkerStart, updated.Text, started);
                    trace(LinkLiveTiming.Prepared, updated.Text, Stopwatch.GetTimestamp());
                }
                if (epoch == replica.Epoch)
                    patches = [.. captured.PatchesTo(updated).Select(splice => new UITextEdit(
                        LivePosition(captured, splice.Start), LivePosition(captured, splice.Start + splice.Length), splice.Inserted))];
                carets = LiveCarets(updated, replica.Participants, replica.Author);
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
                LinkLiveDraft(shared, error.Message, (error as HexDocEpochConflict)?.SharedText);
                return;
            }
            if (error is not null)
            {
                shared.RetryAt = Environment.TickCount64 + 1000;
                LinkLiveStopRevisionWait(shared);
                shared.Connection = null;
                if (!shared.Offline)
                {
                    shared.Offline = true;
                    tabsDirty = true;
                    Say(Said("link.doc_offline", ("path", shared.Wire)));
                }
                return;
            }

            if (shared.Room is null && peer is null)
            {
                if (undo.Length > 0 && shared.Tab.Editor.Document.Version == version)
                    LinkLiveApply(shared, shared.Replica.View, captured);
                return;
            }
            if (save is not null || saveError is not null)
            {
                shared.SaveRequested = false;
                Say(saveError is null ? Said("documents.saved", ("path", shared.Wire)) :
                    Said("documents.save_failed", ("path", shared.Wire), ("error", saveError)), error: saveError is not null);
            }
            LinkLiveRememberSaved(shared, save ?? shared.Replica.Saved);
            if (written is { Text: { } raw })
                LinkLiveSetDiskSave(shared, written.Receipt, raw, diskText);
            if (retryDiskSave)
                shared.DiskSaveRetryAt = Environment.TickCount64 + 2000;
            if (shared.Offline || !LiveSamePeople(shared.Participants, shared.Replica.Participants))
                tabsDirty = true;
            shared.Offline = false;
            if (shared.Connection != peer || epoch != shared.Replica.Epoch)
                shared.LastPresence = null;
            else if (updatePresence)
            {
                shared.LastPresence = cursor;
                shared.PresenceAt = Environment.TickCount64 + 50;
            }
            shared.Connection = peer;
            LinkLiveReadRights(shared, peer);
            shared.Participants = shared.Replica.Participants;
            if (shared.Tab.Editor.Document.Version != version)
            {
                if (epoch != shared.Replica.Epoch)
                    LinkLiveDraft(shared, "The shared document restarted while this tab was changing.");
                else
                    LinkLiveFlush(shared, urgent: true);
                return;
            }
            LinkLiveApply(shared, shared.Replica.View, epoch == shared.Replica.Epoch ? captured : null, patches);
            if (shared.DiskSave is not null)
                shared.Tab.Modified = HasChanges(shared.Tab);
            if (carets is not null)
            {
                var visible = LinkCaretsForShared(shared, carets);
                if (!shared.Tab.Editor.RemoteCarets.SequenceEqual(visible))
                    shared.Tab.Editor.RemoteCarets = visible;
            }
            if (shared.RevisionReply is not null || shared.Replica.Revision < shared.Replica.Head)
                LinkLiveFlush(shared, urgent: true);
            else
                LinkLiveStartRevisionWait(shared);
        });
    }

    private void LinkLiveApply(LinkLiveDocument shared, HexDocView view, HexDocView? previous, IReadOnlyList<UITextEdit>? patches = null)
    {
        var buffer = shared.Tab.Editor.Document;
        bool following = linkFollowDocument == shared.Tab && linkFollowVersion == buffer.Version && linkFollowCaret == buffer.Caret;
        shared.ApplyingRemote = true;
        try
        {
            shared.Tab.Editor.ApplySharedText(view.Text, (basis, draft, changed) => HexLinkDocuments.TryMerge(basis, draft, changed, out string merged) ? merged : null,
                previous is null ? null : point => view.OffsetOf(LivePoint(previous, point)) is { } offset ? LivePosition(view, offset) : point, patches);
        }
        finally { shared.ApplyingRemote = false; }
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
        LinkLiveFlush(shared, urgent: true);
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
        LinkLiveFlush(shared, urgent: true);
        return true;
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
