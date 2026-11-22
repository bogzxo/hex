using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Where each person is (docs/hex-link-plan.md, 5.3). This machine's caret, the file as folder-name/relative/path with
// the line, column and selection, is told to every linked device that lets this one see there, when it moves and at
// most twice a second (HexLinkPeer coalesces it). Nothing is sent while it stays put and nothing at all while no device
// is linked. The others' (the wiring keeps the latest of each in LinkPresence) are drawn in a file this machine has open
// too, mapped by the workspace-relative path: their caret a mark in the device's colour, their selection lit faintly,
// and their name beside the caret. Follow opens what a device's person has open and keeps the caret on theirs
// until the person here types or moves it.
internal sealed partial class DawnScene
{
    // What was last sent, so a tick that moved nothing sends nothing
    private string linkPresenceSent = string.Empty;
    private long linkPresenceSampled;

    // What was last drawn, so marks are only made again when something of them moved
    private string linkPresenceDrawnKey = string.Empty;

    // The marks drawn into each tab, so they're taken out again without touching anybody else's
    private readonly HashSet<HexDocument> linkPresenceDrawn = [];

    // The device followed, where the caret was put for it and the version of the text then, to tell the person moving off
    private string? linkFollowing;
    private HexDocument? linkFollowDocument;
    private UITextPosition linkFollowCaret;
    private int linkFollowVersion;
    private string linkFollowSeen = string.Empty;

    // The colours devices are drawn in, one picked by a device's id
    private static readonly Vector4[] LINK_PRESENCE_COLOURS =
    [
        new(0.93f, 0.55f, 0.20f, 1.0f), new(0.35f, 0.70f, 0.95f, 1.0f), new(0.75f, 0.45f, 0.95f, 1.0f),
        new(0.35f, 0.85f, 0.55f, 1.0f), new(0.95f, 0.40f, 0.55f, 1.0f), new(0.95f, 0.85f, 0.30f, 1.0f)
    ];

    /// <summary>The colour a device is drawn in, the same every time for the same device.</summary>
    internal static Vector4 LinkDeviceColour(string deviceId)
    {
        uint hash = 2166136261;
        foreach (char c in deviceId)
            hash = (hash ^ c) * 16777619;
        return LINK_PRESENCE_COLOURS[hash % (uint)LINK_PRESENCE_COLOURS.Length];
    }

    /// <summary>The device whose person is followed, null when none is.</summary>
    internal string? LinkFollowing => linkFollowing;

    /// <summary>
    /// Follows a device's person: what they have open opened here (the local file when the folder is synced or the same
    /// folder is open here, otherwise their file over the link) and the caret kept on theirs until the person here types
    /// or moves it. Following the one followed already stops.
    /// </summary>
    internal void LinkFollow(string deviceId, string? shownName = null)
    {
        if (linkFollowing == deviceId)
        {
            LinkUnfollow(said: true);
            return;
        }

        linkFollowing = deviceId;
        linkFollowDocument = null;
        linkFollowSeen = string.Empty;
        string name = shownName ?? LinkPeers.FirstOrDefault(peer => peer.Device.Id == deviceId)?.Device.Name ?? deviceId;
        string status = shownName is not null ? "link.devices_session_following"
            : linkPresence.TryGetValue(deviceId, out LinkPresenceSeen? seen) && seen.Presence is not null ? "link.following" : "link.following_nowhere";
        Say(Said(status, ("device", name)));
        linkDirty = true;
    }

    /// <summary>Stops following.</summary>
    internal void LinkUnfollow(bool said = false)
    {
        if (linkFollowing is null)
            return;
        if (said)
            Say(Said("link.unfollowed"));
        linkFollowing = null;
        linkFollowDocument = null;
        linkDirty = true;
    }

    /// <summary>
    /// Each tick while a device is linked: this machine's caret told when it moved, the others' drawn when theirs did,
    /// and the one followed kept up with. Next to nothing when nothing moved.
    /// </summary>
    private void UpdateLinkPresence()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        if (peers.Count == 0)
        {
            if (linkPresenceDrawn.Count > 0)
                LinkPresenceClear();
            linkPresenceSent = string.Empty;
            return;
        }

        LinkPresenceSend(peers);
        LinkPresenceDraw();
        LinkFollowUp();
    }

    // This machine's caret, when it moved, to the devices that may see here
    private void LinkPresenceSend(IReadOnlyList<HexLinkPeer> peers)
    {
        long now = Environment.TickCount64;
        if (linkPresenceSent.Length != 0 && now - linkPresenceSampled < 250)
            return;
        linkPresenceSampled = now;
        HexLinkPresence? mine = LinkPresenceHere();
        if (mine is not null && document.Editor.PointerTextPosition is { } pointer)
            mine = mine with { PointerLine = pointer.Line, PointerColumn = pointer.Column };
        string key = mine is null ? "-" : $"{mine.File}:{mine.Line}:{mine.Column}:{mine.ToLine}:{mine.ToColumn}:{mine.PointerLine}:{mine.PointerColumn}";
        if (key == linkPresenceSent)
            return;
        linkPresenceSent = key;
        foreach (HexLinkPeer peer in peers)
        {
            if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                peer.SetPresence(mine);
        }
    }

    /// <summary>Where the caret is here as the link says it, null when the tab on screen isn't a file of the workspace (or another machine's).</summary>
    internal HexLinkPresence? LinkPresenceHere()
    {
        if (document is not { IsText: true, Path: { } path, DiffOf: null } || LinkWireOf(path) is not { } wire)
            return null;
        UITextPosition caret = document.Editor.Document.Caret, anchor = document.Editor.Document.Anchor;
        return caret == anchor
            ? new HexLinkPresence(wire, caret.Line, caret.Column, null, null)
            : new HexLinkPresence(wire, caret.Line, caret.Column, anchor.Line, anchor.Column);
    }

    /// <summary>A file here as a wire path, folder-name/relative/path: of a workspace folder, or the file of a remote tab; null otherwise.</summary>
    internal string? LinkWireOf(string path)
    {
        if (linkDocument is { } shared && shared.Tab.Path is { } sharedPath && PlatformPaths.Same(sharedPath, path))
            return shared.Base.Path;
        if (linkRemote.TryGetValue(path, out LinkRemoteTab? remote))
            return remote.Wire;
        foreach (HexLinkFolder folder in linkServices?.Workspace.Folders ?? [])
        {
            if (PlatformPaths.IsUnder(path, folder.Path))
                return folder.Name + "/" + Path.GetRelativePath(folder.Path, path).Replace(Path.DirectorySeparatorChar, '/');
        }

        return null;
    }

    /// <summary>Where a device's wire path is open here, the local file of a workspace folder of that name or the copy of a remote tab; null when it isn't.</summary>
    private HexDocument? LinkDocumentOf(string deviceId, string wire)
    {
        if (linkDocument is { } shared && shared.Base.Path == wire && (shared.Peer is null || shared.Peer.Device.Id == deviceId))
            return shared.Tab;
        foreach (LinkRemoteTab tab in linkRemote.Values)
        {
            if (tab.DeviceId == deviceId && tab.Wire == wire && Context.Documents.Find(tab.Copy) is { } copy)
                return copy;
        }

        return LinkLocalOf(wire) is { } local ? Context.Documents.Find(local) : null;
    }

    // A wire path as a file of this workspace, by the folder's name; null when there's no such folder or it isn't a path
    private string? LinkLocalOf(string wire)
    {
        try
        {
            return linkServices?.Workspace.Folders is { Count: > 0 } folders ? HexLinkFiles.Resolve(folders, wire) : null;
        }
        catch (HexLinkException)
        {
            return null;
        }
    }

    // The others' carets drawn into the tabs that have their files, when something of them moved
    private void LinkPresenceDraw()
    {
        string key = string.Join("|", linkPresence.Values.Select(seen => $"{seen.Device.Id}:{seen.Presence?.File}:{seen.Presence?.Line}:{seen.Presence?.Column}:{seen.Presence?.ToLine}:{seen.Presence?.ToColumn}:{seen.Presence?.PointerLine}:{seen.Presence?.PointerColumn}:{seen.Device.Name}"))
            + "#" + documents.Count;
        if (key == linkPresenceDrawnKey && linkPresenceDrawn.All(documents.Contains))
            return;
        linkPresenceDrawnKey = key;
        LinkPresenceClear();

        var carets = new Dictionary<HexDocument, List<UICodeRemoteCaret>>();
        foreach (LinkPresenceSeen seen in linkPresence.Values)
        {
            if (seen.Presence is not { File: not null } where || LinkDocumentOf(seen.Device.Id, where.File) is not { IsText: true } open)
                continue;
            if (!carets.TryGetValue(open, out var participants))
                carets[open] = participants = [];
            participants.Add(new(seen.Device.Id, seen.Device.Name, where.Line, where.Column, where.ToLine ?? where.Line,
                where.ToColumn ?? where.Column, LinkDeviceColour(seen.Device.Id), where.PointerLine, where.PointerColumn));
        }

        foreach (var (open, participants) in carets)
        {
            open.Editor.RemoteCarets = participants;
            linkPresenceDrawn.Add(open);
        }
    }

    private void LinkPresenceClear()
    {
        foreach (HexDocument open in linkPresenceDrawn)
            open.Editor.RemoteCarets = [];
        linkPresenceDrawn.Clear();
    }

    // The person followed kept up with: their file opened, the caret put on theirs; let go of when the person here
    // types or moves the caret themselves
    private void LinkFollowUp()
    {
        if (linkFollowing is not { } id)
            return;
        if (linkFollowDocument is { } followed && documents.Contains(followed) && document == followed
            && (followed.Editor.Document.Version != linkFollowVersion || followed.Editor.Document.Caret != linkFollowCaret))
        {
            LinkUnfollow(said: true);
            return;
        }

        if (!linkPresence.TryGetValue(id, out LinkPresenceSeen? seen) || seen.Presence is not { File: not null } where)
            return;
        string key = $"{where.File}:{where.Line}:{where.Column}";
        if (key == linkFollowSeen)
            return;
        linkFollowSeen = key;

        HexDocument? open = LinkDocumentOf(id, where.File);
        if (open is null && LinkLocalOf(where.File) is { } local && File.Exists(local) && Open(local))
            open = document;
        if (open is null)
        {
            // Not here, their file over the link; the caret's put on theirs the next time it moves, or now when it's open by then
            linkFollowSeen = string.Empty;
            if (link?.PeerOf(id) is not null)
            {
                string wire = where.File;
                linkFollowSeen = "opening:" + key;
                _ = Task.Run(async () =>
                {
                    if (await LinkOpenRemote(id, wire) is { } why)
                        PostLink(() => Say(why, error: true));
                    PostLink(() => linkFollowSeen = string.Empty);
                });
            }

            return;
        }

        if (document != open)
            Show(open);
        open.Editor.GoTo(where.Line, where.Column);
        linkFollowDocument = open;
        linkFollowCaret = open.Editor.Document.Caret;
        linkFollowVersion = open.Editor.Document.Version;
    }

    // A device linked: it's told where this machine's person is at once, the next tick
    private void LinkPresenceJoined(HexLinkPeer peer)
    {
        linkPresenceSent = string.Empty;
        linkPresenceDrawnKey = string.Empty;
    }

    // Link going down: the marks taken out, following stopped
    private void LinkPresenceStopAll()
    {
        LinkPresenceClear();
        linkPresenceDrawnKey = linkPresenceSent = string.Empty;
        linkFollowing = null;
        linkFollowDocument = null;
    }
}
