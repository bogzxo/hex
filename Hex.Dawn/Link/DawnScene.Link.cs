using System.Collections.Concurrent;
using System.Net.NetworkInformation;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Hex Link in the running editor (docs/hex-link-plan.md, phase 2). Hex.Core's Link has the door, the devices this one
// knows, the protocol and both halves of a link; this is where they're made, put up and taken down with the link
// setting (or --link), given the editor to answer from (DawnScene.Link.Services), and pumped a tick at a time. Off,
// nothing is made and nothing listens, and all a tick costs is a null check and a look at an empty queue.
//
// The listener, each link's reader and the pairing all run on threads of their own. Whatever they have for the editor
// (a link up, a link gone, a pairing to ask the person about, a notice) is posted to a queue the tick drains, and the
// window loop is woken for it, so a sleeping Hex answers at once. The UI (the Link window, the pairing dialog and
// Agentic's sections, which another part draws) reads the scene's Link* members below, on the editor's thread, and
// redraws when LinkChanged says so.
internal sealed partial class DawnScene
{
    // The folder of the settings folder the device's key and the known devices are kept in, and of the data folder
    // the audit log, remote file copies and patches offered go in. HEX_HOME moves both, so two Hexes of one build
    // started with homes of their own (the link self test) are two devices
    private const string LINK_FOLDER = "link";

    // How long the pairing window stays open once it's opened
    internal static readonly TimeSpan LINK_PAIRING_WINDOW = TimeSpan.FromMinutes(5);

    // How often, in seconds, the known devices that aren't linked are looked at for a redial and the workspace's
    // folders for a change, and how often the repositories' branches and heads are asked again whatever happened
    private const float LINK_LOOK_EVERY = 1.0f, LINK_GIT_EVERY = 10.0f;

    // The one Hex scene in the process that has Link up, as with the MCP server
    private static DawnScene? linkOwner;

    private HexLinkIdentity? linkIdentity;
    private HexLinkTrust? linkTrust;
    private HexLinkTransport? linkTransport;
    private HexLinkDiscovery? linkDiscovery;
    private HexLink? link;
    private HexLinkAudit? linkAudit;
    private LinkServices? linkServices;
    private CancellationTokenSource? linkCancel;
    private string linkData = string.Empty;
    private StatusItem linkItem = null!;

    // Started by a self test with a port and folders of its own, which the settings changing mustn't take down
    private bool linkByTest;

    // What arrived off the editor's thread for it, run by the next tick
    private ConcurrentQueue<Action>? pendingLinkPosts;
    private ConcurrentQueue<Action> linkPosted => LazyInitializer.EnsureInitialized(ref pendingLinkPosts);
    private volatile bool linkDirty;

    // The redials: when each known device is next dialled, those being dialled now, and those the person cut, which
    // aren't dialled again until they're connected by hand or Hex starts again
    private HexLinkRedial linkRedial { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }
    private HashSet<string> linkDialling { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private HashSet<string> linkHeldOff { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private Dictionary<string, string> linkNearbyAddresses { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private volatile IReadOnlyList<string> linkListenAddresses = [];
    private bool linkListeningForAddressChanges;

    public HexLinkRedialState? LinkRedialState(string deviceId) => linkHeldOff.Contains(deviceId) || link?.PeerOf(deviceId) is not null
        ? null : linkRedial.State(deviceId, linkDialling.Contains(deviceId));

    // The versions of the chats as the hub last had them, so only the chats that moved are said to have
    private Dictionary<int, int> linkChatVersions { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    private float linkLookWait, linkGitWait;
    private string[] linkFoldersSeen = [];
    private bool linkWorkspaceAsking;

    // Where each device's person is, the latest said, by device id, for the UI to draw
    private Dictionary<string, LinkPresenceSeen> linkPresence { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }

    // The pairing waiting on the person here, and the dialog asking them
    private LinkPairingAsk? linkPairingPending;
    private UIDialog? linkPairingDialog;

    /// <summary>The status bar's item and Link itself, when the settings or the command line say so.</summary>
    private void BuildLink()
    {
        BuildLinkUi();
        if (LinkWanted)
            StartLink();
    }

    /// <summary>
    /// Whether Link should be up, the setting or --link, never for a run of checks. A self test goes by --link alone,
    /// the link suites put it up themselves with folders and a port of their own.
    /// </summary>
    private bool LinkWanted => ((settings.Link && !options.SelfTest) || options.Link) && options.Check.Length == 0;

    /// <summary>
    /// Link put up or taken down to match the settings, as a row changes or the file is read again; the name and
    /// whether it's found on the LAN changed while it's up taken at once, a new port by starting again.
    /// </summary>
    internal void ApplyLink(bool restart = false)
    {
        if (linkByTest)
        {
            if (linkTransport is not null)
                linkTransport.AllowPublic = settings.LinkAllowPublic;
            ApplyLinkMapping();
            return;
        }

        if (LinkWanted && linkTransport is null)
        {
            if (StartLink())
                OfferLinkIgnoreFiles(explorer.workspace.Folders);
        }
        else if (!LinkWanted && linkTransport is not null)
        {
            StopLink();
            Say(Said("link.stopped"));
        }
        else if (linkTransport is not null && ((linkTransport.AllowPublic && !settings.LinkAllowPublic)
            || (restart && HexHost.Listens && linkTransport.Port != (options.LinkPort ?? settings.LinkPort))))
        {
            StopLink();
            StartLink();
        }
        else if (linkTransport is not null)
        {
            linkTransport.AllowPublic = settings.LinkAllowPublic;
            ApplyLinkMapping();
            LinkTakeName();
            LinkAnnounce(!options.SelfTest);
            linkDirty = true;
        }
    }

    /// <summary>
    /// The name link_name says taken while Link is up (a self test's Link too): told to the devices linked now at once
    /// rather than at the next link, the old one said goodbye to on the network and the new one announced.
    /// </summary>
    internal void LinkTakeName()
    {
        if (linkIdentity is null || linkIdentity.Name == LinkNameWanted)
            return;
        linkIdentity.Name = LinkNameWanted;
        foreach (HexLinkPeer peer in LinkPeers)
            peer.SayName(linkIdentity.Name);
        linkDiscovery?.StopAnnouncing();
        LinkAnnounce(!options.SelfTest && !linkByTest);
        linkDirty = true;
    }

    /// <summary>What this device is called, the link_name setting or the machine's name.</summary>
    private string LinkNameWanted => HexLinkNames.Clean(settings.LinkName) ?? HexLinkNames.Clean(System.Environment.MachineName) ?? "Hex";

    public HexLinkIdentityFailure? LinkIdentityFailure { get; private set; }

    /// <summary>Sets aside the old key so the next start can create a new identity.</summary>
    internal bool ResetLinkIdentity(out string? backup, out string? reason)
    {
        backup = null;
        reason = null;
        if (linkTransport is not null || linkKeys is not { } keys)
        {
            reason = "Turn Link off before resetting its identity.";
            return false;
        }

        try
        {
            backup = HexLinkIdentity.Reset(keys);
            LinkIdentityFailure = null;
            linkAudit?.Write("identity reset", string.Empty, $"Saved the previous identity as {Path.GetFileName(backup)}.");
            linkDirty = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reason = e.Message;
            linkAudit?.Write("identity reset failed", string.Empty, reason);
            return false;
        }
    }

    /// <summary>
    /// Puts Link up: the device's key read (made the first time), the known devices read, the listener on its port
    /// (none on a tablet, which only dials) and the LAN looked at. True when it's up.
    /// </summary>
    /// <param name="port">The port, the setting's (or --link-port's) when null, 0 for any free one.</param>
    /// <param name="home">A folder the key and the known devices go in, and data/ under it the audit log, for a self test; the settings and data folders when null.</param>
    /// <param name="discover">Whether to look for devices on the LAN. Defaults to off in self-tests.</param>
    /// <param name="loopbackOnly">Bind to IPv4 loopback and skip LAN discovery for local-only self-tests.</param>
    internal bool StartLink(int? port = null, string? home = null, bool? discover = null, bool loopbackOnly = false)
    {
        if (linkTransport is not null)
            return true;
        if (linkOwner is not null && linkOwner != this)
            return false;

        try
        {
            string keys = home ?? Path.Combine(TerminalFiles.UserFolder, LINK_FOLDER);
            linkKeys = keys;
            linkData = home is not null ? Path.Combine(home, "data") : Path.Combine(HexData.Root, LINK_FOLDER);
            Directory.CreateDirectory(linkData);
            linkAudit = new HexLinkAudit(Path.Combine(linkData, "audit.log"));
            // A self test's key is never put in the person's Keychain or secret service, the file alone keeps it
            linkIdentity = HexLinkIdentity.LoadOrCreate(keys, LinkNameWanted, fileOnly: options.SelfTest);
            LinkIdentityFailure = null;
            linkTrust = new HexLinkTrust(keys);
            linkRedial.Open(keys, linkTrust.Known.Select(known => known.Device.Id).ToArray());
            linkServices = new LinkServices(this);
            int unpinned = linkServices.FolderAuthorities.MigrateLegacy(
                LinkReadProvisionedFolders().Select(binding => (binding.Folder, binding.DeviceId)));
            if (unpinned > 0)
                linkAudit.Write("authority migration", string.Empty, $"Cleared {unpinned} unverified first-epoch folder binding(s).");
            linkCancel = new CancellationTokenSource();
            var started = new HexLink(linkServices)
            {
                Changed = LinkMoved,
                Closed = (device, why) => PostLink(() => LinkClosed(device, why))
            };
            started.TalkHeard += message => PostLink(() =>
            {
                if (link == started)
                    HearLinkTalk(message);
            });
            link = started;
            linkTransport = new HexLinkTransport(linkIdentity, linkTrust)
            {
                AllowPublic = settings.LinkAllowPublic,
                Audit = linkAudit,
                Accepted = connection => _ = LinkAdd(connection),
                Changed = LinkMoved,
                Paired = result => PostLink(() => LinkPairEnded(result))
            };
            linkTrust.Changed += LinkTrustChanged;
            if (HexHost.Listens)
            {
                linkListenAddresses = HexLinkRedial.LocalAddresses(loopbackOnly);
                linkTransport.Start(port ?? options.LinkPort ?? settings.LinkPort, loopbackOnly);
                if (!loopbackOnly)
                {
                    try
                    {
                        NetworkChange.NetworkAddressChanged += LinkNetworkAddressChanged;
                        linkListeningForAddressChanges = true;
                    }
                    catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
                    {
                        Console.WriteLine($"[Hex] Link can't monitor network address changes, {e.Message}");
                    }
                }
            }
            ApplyLinkMapping();
            linkOwner = this;
            LinkOn = true;
            if (linkItem is null)
            {
                linkItem = statusBar.Add("$(link)", right: true, pressed: ShowLinkWindow, tooltip: Said("link.status_tip_none"));
                linkItem.IsVisible = () => linkTransport is not null;
            }
            if (linkEditorNotices is null)
                BuildLinkEditorNotices();
            if (linkTalkPanel is null)
                BuildLinkTalk();
            ApplyLinkPredictions();
            mcpTools?.Refresh();
            AppDomain.CurrentDomain.ProcessExit += StopLinkAtExit;
            linkFoldersSeen = [];
            linkLookWait = 0.0f;
            LinkLookAtWorkspace(force: true);
            LinkAnnounce(!loopbackOnly && (discover ?? !options.SelfTest));

            Say(HexHost.Listens ? Said("link.started", ("port", linkTransport.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))) : Said("link.started_dialling"));
            Console.WriteLine($"[Hex] Link is up as {linkIdentity.Name} ({linkIdentity.Device.Fingerprint}){(HexHost.Listens ? $" on port {linkTransport.Port}" : ", dialling only")}");
            linkDirty = true;
            return true;
        }
        catch (Exception e)
        {
            if (e is HexLinkIdentityLoadException identity)
            {
                LinkIdentityFailure = identity.Failure;
                linkAudit?.Write("identity load failed", string.Empty, $"{identity.Failure.Problem}: {identity.Message}");
            }
            Say(Said("link.failed", ("why", e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? Said("link.port_taken", ("port", (port ?? options.LinkPort ?? settings.LinkPort).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : e.Message)), error: true);
            StopLink();
            return false;
        }
    }

    /// <summary>Takes Link down: the listener closed, every link cut, the LAN told goodbye and a pairing asked about answered no.</summary>
    internal void StopLink()
    {
        AppDomain.CurrentDomain.ProcessExit -= StopLinkAtExit;
        _ = linkPortMapping?.SetPort(0);
        linkCancel?.Cancel();
        if (linkListeningForAddressChanges)
        {
            NetworkChange.NetworkAddressChanged -= LinkNetworkAddressChanged;
            linkListeningForAddressChanges = false;
        }
        linkListenAddresses = [];
        if (linkDiscovery is not null)
        {
            linkDiscovery.StopAnnouncing();
            linkDiscovery.Dispose();
            linkDiscovery = null;
        }

        // What's shared over the links let go of before they go (DawnScene.LinkSync, LinkFiles, LinkPresence)
        LinkLiveStopAll();
        LinkDocumentStop();
        LinkShareStopAll();
        link?.Dispose();
        link = null;
        ResetLinkTalk();
        SetLinkClaimSession(null);
        if (linkTrust is not null)
            linkTrust.Changed -= LinkTrustChanged;
        linkTransport?.Dispose();
        linkTransport = null;
        LinkOn = false;
        ApplyLinkPredictions();
        mcpTools?.Refresh();
        linkIdentity?.Dispose();
        linkIdentity = null;
        linkTrust = null;
        linkServices = null;
        linkCancel?.Dispose();
        linkCancel = null;
        if (linkOwner == this)
            linkOwner = null;

        linkPairingPending?.Decide(null);
        CloseLinkPairingDialog();
        linkPairingPending = null;
        linkRedial.Clear();
        linkDialling.Clear();
        linkNearbyAddresses.Clear();
        linkPresence.Clear();
        linkChatVersions.Clear();
        linkByTest = false;
        linkDirty = true;
    }

    private void StopLinkAtExit(object? sender, EventArgs e)
    {
        try
        {
            linkTransport?.Stop();
            linkPortMapping?.SetPort(0).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Going anyway, the sockets close with the process
        }
    }

    /// <summary>Something of Link's moved on another thread, the UI redrawn on the next tick and the window woken for it.</summary>
    private void LinkMoved()
    {
        linkDirty = true;
        Engine.WindowManager.Wake();
    }

    /// <summary>Something for the editor's thread from one of Link's, run by the next tick, the window woken for it.</summary>
    private void PostLink(Action action)
    {
        linkPosted.Enqueue(action);
        Engine.WindowManager.Wake();
    }

    /// <summary>Something done on the editor's thread for one of Link's, what it made handed back.</summary>
    private Task<T> OnEditorForLink<T>(Func<T> run)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostLink(() =>
        {
            try
            {
                done.TrySetResult(run());
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Every tick: what arrived for the editor run, the mirrored chats patched, the redials and the workspace looked at
    /// once a second, and the status bar and whoever draws Link told when something moved. Nothing at all while Link is
    /// off and nothing is waiting.
    /// </summary>
    private void UpdateLink(float dt)
    {
        if (LinkOn || linkWindow is not null || linkDirty || pendingLinkPosts is { IsEmpty: false })
            PumpLink(dt);
    }

    private void PumpLink(float dt)
    {
        UpdateLinkUi();

        while (pendingLinkPosts?.TryDequeue(out Action? posted) == true)
        {
            try
            {
                posted();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] something of Link's failed on the editor's thread, {e.Message}");
            }
        }

        if (link is not null && link.Pump())
            linkDirty = true;

        // Where the person is here told, and where the others are drawn (DawnScene.LinkPresence)
        if (link is not null)
        {
            UpdateLinkClaims(dt);
            UpdateLinkDocument(dt);
            UpdateLinkLiveDocuments(dt);
            UpdateLinkPresence();
        }

        if (linkPairingPending is { IsDecided: true } decided)
        {
            // One that ended on the other side stays a moment to say so, and closes itself (DawnScene.LinkPairing)
            if (!decided.Ended.IsCancellationRequested || linkPairCloseAt == 0)
                CloseLinkPairingDialog();
            linkPairingPending = null;
            linkDirty = true;
        }

        if (linkTransport is not null && (linkLookWait -= dt) <= 0.0f)
        {
            linkLookWait = LINK_LOOK_EVERY;
            LinkRedialDue();
            linkGitWait -= LINK_LOOK_EVERY;
            LinkLookAtWorkspace(force: linkGitWait <= 0.0f);
            ApplyLinkPredictions();
            LinkPredictLookServing();
            CheckLinkHoldAge();
        }

        if (linkDirty)
        {
            linkDirty = false;
            CaptureLinkChatDevices(LinkPeers);
            LinkPredictLookSource();
            LinkRedraw();
        }
    }

    /// <summary>The status bar's item said again and whoever draws Link told.</summary>
    private void LinkRedraw()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        RouteLinkAgentApprovals(peers);
        if (linkItem is not null)
        {
            // Breathing while a device steers something here, so it's seen from across the room
            linkSteeringShown = LinkSteeringNow;
            string glyph = linkSteeringShown ? "$(link~breathe)" : "$(link)";
            // Who is linked (the name of the one, the count of several) and what needs the person, readable without opening anything
            int needing = LinkConflicts.Count;
            string who = peers.Count switch
            {
                0 => string.Empty,
                1 => " " + OneLine(LinkNameOf(peers[0].Device.Id, peers[0].Device.Name), 18),
                _ => " " + Said("link.status_count", ("count", peers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            };
            string need = needing > 0 ? " · " + Said("link.status_conflicts", ("count", needing.ToString(System.Globalization.CultureInfo.InvariantCulture))) : string.Empty;
            linkItem.Text = glyph + who + need;
            linkItem.Color = needing > 0 ? LinkWarning : null;
            linkItem.Tooltip = peers.Count == 0
                ? Said(linkTransport?.PairingOpen == true ? "link.status_tip_pairing" : "link.status_tip_none")
                : Said("link.status_tip", ("devices", string.Join("\n", peers.Select(peer =>
                    Said("link.status_tip_device", ("device", LinkNameOf(peer.Device.Id, peer.Device.Name)), ("there", LinkRightsSaid(peer.Granted)), ("here", LinkRightsSaid(linkTrust?.Find(peer.Device.Id)?.Rights ?? HexLinkRights.None)))))));
        }

        LinkVersion++;
        LinkChanged?.Invoke();
    }

    /// <summary>What a set of rights is, in words, "nothing" when there are none.</summary>
    internal static string LinkRightsSaid(HexLinkRights rights) => rights == HexLinkRights.None
        ? Said("link.rights_none")
        : string.Join(", ", HexLinkTrust.RightsNames(rights).Select(name => Said("link.right_" + name)));

    /* The links */

    /// <summary>
    /// A connection the transport handed over (taken in, dialled or paired) made a link, hello said both ways. Off the
    /// editor's thread; the editor told once it's up. The peer, or null when it couldn't be made one.
    /// </summary>
    private async Task<HexLinkPeer?> LinkAdd(HexLinkConnection connection)
    {
        HexLink? into = link;
        if (into is null)
        {
            connection.Dispose();
            return null;
        }

        try
        {
            HexLinkPeer peer = await into.Add(connection, linkCancel?.Token ?? CancellationToken.None);
            PostLink(() => LinkJoined(peer));
            return peer;
        }
        catch (Exception e)
        {
            linkAudit?.Write("link failed", connection.Device.Id, e.Message);
            PostLink(() => Say(Said("link.not_linked", ("device", connection.Device.Name), ("why", e.Message)), error: true));
            return null;
        }
    }

    // A link up, said, its device's redials started over, and its fleet and tools asked for when it lets this one see
    private void LinkJoined(HexLinkPeer peer)
    {
        if (peer.IsClosed)
            return;
        linkRedial.Soon(peer.Device.Id);
        linkHeldOff.Remove(peer.Device.Id);
        // Its name as it says it kept as its own (the person's name for it, if any, still wins), and kept as it changes
        linkTrust?.Seen(peer.Device.Id, peer.Device, string.Empty);
        RememberLinkAddresses(peer);
        peer.AddressesMoved += () => PostLink(() => RememberLinkAddresses(peer));
        peer.SyncPausesChanged = () => PostLink(() => LinkAcceptRemotePauses(peer));
        peer.WorkspaceChanged = () => PostLink(() =>
        {
            if (LinkPeerOf(peer.Device.Id) != peer)
                return;
            LinkLearnAuthority(peer);
            LinkPinSyncedFolders(peer);
        });
        LinkAcceptRemotePauses(peer);
        peer.RightsChanged += () =>
        {
            if (LinkPeerOf(peer.Device.Id) == peer)
                LinkSyncRightsChangedLocally(peer);
        };
        peer.ViewRestored += () => PostLink(() =>
        {
            if (link?.PeerOf(peer.Device.Id) == peer && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                LinkShareJoined(peer);
        });
        peer.Renamed = device => PostLink(() =>
        {
            linkTrust?.Seen(device.Id, device, string.Empty);
            linkDirty = true;
        });
        Say(Said("link.connected", ("device", LinkNameOf(peer.Device.Id, peer.Device.Name))));
        linkDirty = true;
        // What's shared with it taken up again: the folders synced with it, presence, its remote tabs (DawnScene.LinkSync, ...)
        LinkShareJoined(peer);
        if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await peer.WatchFleet();
                    await peer.ListTools();
                }
                catch (Exception)
                {
                    // What it shows is the UI's to ask again for, the link is up whatever this said
                }
            });
        }
    }

    private void RememberLinkAddresses(HexLinkPeer peer)
    {
        if (linkTrust?.Find(peer.Device.Id) is not { } known || peer.IsClosed)
            return;
        HexLinkConnection connection = peer.Session.Connection;
        HexLinkEndpoint? reached = connection.Dialled ? HexLinkRedial.Endpoint(connection.Address, known.Port) : null;
        linkRedial.Remember(peer.Device.Id, peer.RedialAddresses, reached);
        linkDirty = true;
    }

    private void LinkNetworkAddressChanged(object? sender, EventArgs args)
    {
        IReadOnlyList<string> current = HexLinkRedial.LocalAddresses(loopbackOnly: false);
        PostLink(() =>
        {
            if (!linkListeningForAddressChanges || linkServices is not { } services || linkListenAddresses.SequenceEqual(current))
                return;
            linkListenAddresses = current;
            foreach (HexLinkPeer peer in LinkPeers)
                peer.Session.TellLatest("link/addresses", services.RedialAdvertisement());
        });
    }

    // A link gone, said unless Link itself was turned off
    private void LinkClosed(HexLinkDevice device, string why)
    {
        linkPresence.Remove(device.Id);
        linkDirty = true;
        if (linkTransport is null)
            return;
        Say(Said("link.disconnected", ("device", LinkNameOf(device.Id, device.Name)), ("why", why)));
    }

    // A device forgotten here (or elsewhere through the file): what's shown of it goes
    private void LinkTrustChanged(string id) => PostLink(() =>
    {
        if (linkTrust?.Find(id) is null)
            linkRedial.Forget(id);
        linkDirty = true;
    });

    /* Redialling the known devices */

    /// <summary>
    /// The known devices that aren't linked and whose wait is over dialled, each at the addresses it was last at and
    /// those the LAN says it's at now, best first (HexLinkAddresses.Preferred). One that can't be reached waits longer
    /// each time (HexLinkRedial).
    /// </summary>
    private void LinkRedialDue()
    {
        if (linkTransport is not { } transport || linkTrust is null || link is null)
            return;

        DateTime now = DateTime.UtcNow;
        IReadOnlyList<HexLinkNearby> nearby = linkDiscovery?.Found ?? [];
        foreach (HexLinkKnown known in linkTrust.Known)
        {
            string id = known.Device.Id;
            if (linkDialling.Contains(id) || linkHeldOff.Contains(id) || linkManualDials.GetValueOrDefault(id)?.Connecting == true || link.PeerOf(id) is not null || transport.LinkTo(id) is not null || !linkRedial.Due(id, now))
                continue;

            HexLinkEndpoint[] tries = LinkDialRoutes(known, nearby, linkRedial.Addresses(id));
            if (tries.Length == 0)
            {
                linkRedial.Failed(id, now, "No listening address is known for this device.");
                linkDirty = true;
                continue;
            }

            linkDialling.Add(id);
            linkDirty = true;
            CancellationToken stop = linkCancel?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                string? why = null;
                string? detail = null;
                foreach (var (host, port) in tries)
                {
                    if (stop.IsCancellationRequested || linkTrust?.Find(id)?.PreferredRoute != known.PreferredRoute)
                        break;
                    try
                    {
                        HexLinkConnection connection = await transport.Dial(host, port, id, stop);
                        HexLinkPeer? peer = await LinkAdd(connection);
                        PostLink(() =>
                        {
                            if (linkTransport != transport)
                                return;
                            linkDialling.Remove(id);
                            if (peer is null)
                                linkRedial.Failed(id, DateTime.UtcNow, "The device connected, then Link could not say hello.");
                            linkDirty = true;
                        });
                        return;
                    }
                    catch (HexLinkRefused e) when (e.Reason == "busy")
                    {
                        // Linked meanwhile the other way, nothing to do
                        PostLink(() =>
                        {
                            if (linkTransport != transport)
                                return;
                            linkDialling.Remove(id);
                            if (link?.PeerOf(id) is null)
                                linkRedial.Failed(id, DateTime.UtcNow, e.Message);
                            linkDirty = true;
                        });
                        return;
                    }
                    catch (Exception e)
                    {
                        why = e is HexLinkRefused refused ? refused.Reason : e.Message;
                        detail = e.Message;
                    }
                }

                string said = why ?? "unreachable";
                PostLink(() =>
                {
                    if (linkTransport != transport)
                        return;
                    linkDialling.Remove(id);
                    if (linkTrust?.Find(id)?.PreferredRoute == known.PreferredRoute)
                        linkRedial.Failed(id, DateTime.UtcNow, detail ?? said);
                    linkDirty = true;
                    LinkRedialRefused?.Invoke(id, said);
                });
            }, stop);
        }
    }

    /// <summary>A redial that didn't link, the device and why in a word (unknown when the other end has forgotten this one), for a self test to see.</summary>
    internal Action<string, string>? LinkRedialRefused { get; set; }

    /// <summary>
    /// Where a known device can be dialled: the address and port it was last at, and every address the LAN says it's at
    /// now (matched by the start of its id), then what that device advertised over its authenticated connection.
    /// </summary>
    internal static List<(string Host, int Port)> LinkAddressesOf(HexLinkKnown known, IReadOnlyList<HexLinkNearby> nearby,
        IReadOnlyList<HexLinkEndpoint>? remembered = null)
    {
        var tries = new List<(string Host, int Port)>();
        foreach (HexLinkNearby heard in nearby)
        {
            if (heard.Port <= 0 || !known.Device.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase) || heard.Id8.Length < 8)
                continue;
            foreach (System.Net.IPAddress address in HexLinkAddresses.Preferred(heard.Addresses))
                tries.Add((address.ToString(), heard.Port));
        }

        if (known.Address.Length > 0 && known.Port > 0 && !tries.Contains((known.Address, known.Port)))
            tries.Add((known.Address, known.Port));
        if (remembered is not null)
        {
            foreach (HexLinkEndpoint endpoint in remembered)
                if (!tries.Contains((endpoint.Host, endpoint.Port)))
                    tries.Add((endpoint.Host, endpoint.Port));
        }
        return tries;
    }

    /* The LAN */

    // Looked at while Link is on, told about this device while link_discoverable is on as well
    private void LinkAnnounce(bool discover)
    {
        if (!discover || linkTransport is null || linkIdentity is null)
        {
            linkDiscovery?.StopAnnouncing();
            return;
        }

        if (linkDiscovery is null)
        {
            linkDiscovery = new HexLinkDiscovery { Changed = LinkNearbyMoved };
            if (!linkDiscovery.Browse() && linkDiscovery.Off is { } off)
                Console.WriteLine($"[Hex] Link can't look for Hexes on the LAN, {off}");
        }

        if (settings.LinkDiscoverable && HexHost.Listens && linkTransport.Port > 0)
            linkDiscovery.Announce(linkIdentity.Name, linkIdentity.Id, linkTransport.Port);
        else
            linkDiscovery.StopAnnouncing();
    }

    // Somebody heard on the LAN: a known device among them is dialled at once rather than when its wait is up
    private void LinkNearbyMoved() => PostLink(() =>
    {
        if (linkTrust is null || linkDiscovery is null)
            return;
        var stillNearby = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HexLinkNearby heard in linkDiscovery.Found)
        {
            if (heard.Id8.Length >= 8 && linkTrust.Known.FirstOrDefault(known => known.Device.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                string id = known.Device.Id;
                string address = $"{heard.Port}/{string.Join(",", HexLinkAddresses.Preferred(heard.Addresses))}";
                if (!linkNearbyAddresses.TryGetValue(id, out string? was) || was != address)
                    linkRedial.Soon(id);
                linkNearbyAddresses[id] = address;
                stillNearby.Add(id);
            }
        }
        foreach (string away in linkNearbyAddresses.Keys.Where(id => !stillNearby.Contains(id)).ToArray())
            linkNearbyAddresses.Remove(away);

        linkDirty = true;
    });

    /* The workspace as the links describe it */

    /// <summary>
    /// The workspace's folders looked at, and when they changed (or <paramref name="force"/>) the description the links
    /// give made again off the editor's thread, with git asked where each folder's repository is and what it's on.
    /// </summary>
    private void LinkLookAtWorkspace(bool force)
    {
        if (linkServices is not { } services)
            return;
        string[] folders = [.. explorer.workspace.Folders, .. linkTestExtraFolders];
        if (!force && folders.SequenceEqual(linkFoldersSeen, PlatformPaths.Comparer))
            return;
        if (linkWorkspaceAsking)
            return;

        // Linked index tools also run when this Hex's local MCP pipe is disabled.
        McpFolders();
        linkFoldersSeen = folders;
        linkGitWait = LINK_GIT_EVERY;
        linkWorkspaceAsking = true;
        string name = explorer.workspace.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : string.Empty;

        // The folders at once, so a hello said before git has answered says them; their repositories when it has
        if (!folders.SequenceEqual(services.Workspace.Folders.Select(folder => folder.Path), PlatformPaths.Comparer))
            services.Workspace = LinkDescribeWorkspace(name, folders, _ => null) with { Repositories = services.Workspace.Repositories };
        _ = Task.Run(() =>
        {
            HexLinkWorkspace described;
            try
            {
                described = LinkDescribeWorkspace(name, folders, HexLinkWorkspaces.RepositoryOf);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] Link couldn't describe the workspace, {e.Message}");
                described = LinkDescribeWorkspace(name, folders, _ => null);
            }

            services.Workspace = described;
            PostLink(() => linkWorkspaceAsking = false);
        });
    }

    /// <summary>
    /// The hub pumped and something moved (DawnScene.McpAgents): what the fleet's rows carry besides made again, the
    /// devices watching the fleet told, and each chat whose version moved said to have to the devices that have it open.
    /// </summary>
    private void LinkHubMoved()
    {
        if (link is null || linkServices is null)
            return;

        HexHubSnapshot snapshot = hub.Snapshot;
        var extras = new Dictionary<int, HexLinkChatExtra>();
        foreach (HexHubRow row in snapshot.Rows.Values)
        {
            HexAgentChat chat = row.Chat;
            bool waiting = chat.FirstPending(HexAgentEntryKind.Question) is not null || HexAgentHub.Approvals(chat).Any();
            string? step = chat.Plan is { Unfinished: true, Doing: { } doing } ? doing.Text : null;
            if (waiting || step is not null)
                extras[row.Id] = new HexLinkChatExtra(waiting, step);
        }

        linkServices.FleetExtras = extras;
        link.FleetMoved();
        foreach (HexHubRow row in snapshot.Rows.Values)
        {
            if (linkChatVersions.TryGetValue(row.Id, out int was) && was == row.Version)
                continue;
            linkChatVersions[row.Id] = row.Version;
            link.ChatMoved(row.Id);
        }
    }
}

/// <summary>Where a device's person is in a file, as they last said, and when that was (UTC).</summary>
internal sealed record LinkPresenceSeen(HexLinkDevice Device, HexLinkPresence? Presence, DateTime When);
