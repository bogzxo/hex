namespace Hex.Dawn;

// What the rest of the editor sees of Link and does with it, on the editor's thread: the Link window, the pairing
// dialog, Agentic's sections of other devices, the palette's commands and the self tests all go through these. Reading
// them is cheap (the lists are copies made as they're asked for), so a window may ask every time it draws, and
// LinkChanged says when it's worth drawing again.
internal sealed partial class DawnScene
{
    /// <summary>Something shown of Link moved (a link, a device, a pairing, presence, the LAN). On the editor's thread.</summary>
    internal event Action? LinkChanged;

    /// <summary>Goes up each time <see cref="LinkChanged"/> is said, for a window that would rather compare than listen.</summary>
    internal int LinkVersion { get; private set; }

    /// <summary>Whether Link is up.</summary>
    internal bool LinkOn { get; private set; }

    /// <summary>The port it listens on, 0 while it's off or doesn't listen (a tablet).</summary>
    internal int LinkPort => linkTransport?.Port ?? 0;

    /// <summary>This device as the others know it (its id, the name it goes by, its fingerprint), null while Link is off.</summary>
    internal HexLinkDevice? LinkDevice => linkIdentity?.Device;

    /// <summary>How this device's key is kept on the disk, for the window to say (the file alone is said as a warning), null while off.</summary>
    internal HexLinkProtection? LinkProtection => linkIdentity?.Protection;

    /// <summary>The devices this one has paired with, by name, linked or not.</summary>
    internal IReadOnlyList<HexLinkKnown> LinkKnown => linkTrust?.Known ?? [];

    /// <summary>The devices linked now, each the other machine as this one sees it (its fleet, chats, files and tools).</summary>
    internal IReadOnlyList<HexLinkPeer> LinkPeers => link?.Peers ?? [];

    /// <summary>The Hexes heard on the LAN, this one left out, and the ones found on a tailnet (DawnScene.LinkTailscale).</summary>
    internal IReadOnlyList<HexLinkNearby> LinkNearby => LinkNearbyAll();

    // Every device this one can currently name: itself, the paired ones, the linked ones and the Hexes heard nearby
    private IEnumerable<(string? Name, string Id)> LinkNamedDevices(bool nearby = true)
    {
        if (LinkDevice is { } me)
            yield return (me.Name, me.Id);
        foreach (HexLinkKnown known in LinkKnown)
            yield return (known.Device.Name, known.Device.Id);
        foreach (HexLinkPeer peer in LinkPeers)
            yield return (peer.Device.Name, peer.Device.Id);
        if (!nearby)
            yield break;
        foreach (HexLinkNearby heard in LinkNearby)
            yield return (heard.Name, heard.Id8);
    }

    /// <summary>A device's name as it is now (a rename is followed), with a short id tail where another device goes by the same name.</summary>
    internal string LinkDeviceLabel(string id, string? lastName = null, bool nearby = true)
    {
        HexLinkDevice? now = LinkPeerOf(id)?.Device ?? LinkKnown.FirstOrDefault(each => each.Device.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Device;
        string? shown = linkTrust?.Find(id)?.Shown;
        return HexLinkNames.Distinct(shown is { Length: > 0 } ? shown : now?.Name ?? lastName, now?.Id ?? id, LinkNamedDevices(nearby));
    }

    internal string LinkDeviceLabel(HexLinkDevice device) => LinkDeviceLabel(device.Id, device.Name);

    /// <summary>A Nearby row's name, with its id tail when two heard (or paired) devices share it, the port when it has no id.</summary>
    internal string LinkNearbyLabel(HexLinkNearby heard)
    {
        string label = HexLinkNames.Distinct(heard.Name, heard.Id8, LinkNamedDevices());
        if (heard.Id8.Length == 0 && LinkNearby.Count(each => each != heard && string.Equals(each.Name, heard.Name, StringComparison.OrdinalIgnoreCase)) > 0)
            label += $" · :{heard.Port}";
        return label;
    }

    /// <summary>Why the LAN can't be looked at, null when it can or Link is off.</summary>
    internal string? LinkNearbyOff => linkDiscovery?.Off;

    /// <summary>Where each linked device's person is, the latest each said, by device id.</summary>
    internal IReadOnlyDictionary<string, LinkPresenceSeen> LinkPresence => linkPresence;

    /// <summary>The audit log's latest events, oldest first, at most <paramref name="count"/>.</summary>
    internal List<HexLinkAuditEntry> LinkAuditLatest(int count) => linkAudit?.Latest(count) ?? [];

    /// <summary>Where the audit log, the remote files' copies and the patches offered are kept, empty while Link is off.</summary>
    internal string LinkDataFolder => linkData;

    /// <summary>A pairing waiting on the person here, null when there's none. The UI shows it and calls Decide.</summary>
    internal LinkPairingAsk? LinkPairingPending => linkPairingPending;

    /// <summary>When the pairing window closes, null when it's closed.</summary>
    internal DateTime? LinkPairingUntil => linkTransport?.PairingUntil;
    internal HexLinkInvite? LinkActiveInvite => linkTransport?.ActiveInvite;

    /// <summary>The TLS version a device's link was made with (Tls13, Tls12), null when it isn't linked.</summary>
    internal System.Security.Authentication.SslProtocols? LinkTlsOf(string id) =>
        linkTransport?.LinkTo(id)?.Stream is System.Net.Security.SslStream ssl ? ssl.SslProtocol : null;

    /// <summary>Whether a known device is one the person cut, which isn't dialled again until they connect it by hand.</summary>
    internal bool LinkHeldOff(string id) => linkHeldOff.Contains(id);

    /// <summary>The name of the device that cut the link by hand when it was the other one, null when it was cut here or isn't cut.</summary>
    internal string? LinkHeldOffBy(string id) => linkHeldOffBy.GetValueOrDefault(id);

    /// <summary>
    /// Opens the pairing window: for the next five minutes (or <paramref name="window"/>) one unknown device may pair,
    /// and the person is asked about it (<see cref="LinkPairingPending"/>, and the pairing dialog, DawnScene.LinkPairing).
    /// </summary>
    internal bool LinkOpenPairing(TimeSpan? window = null)
    {
        if (linkTransport is not { Listening: true } transport)
            return false;
        transport.OpenPairing(window ?? LINK_PAIRING_WINDOW, pairing => LinkAskPerson(pairing, host: true));
        linkDirty = true;
        return true;
    }

    internal bool LinkOpenInvite(HexLinkInviteRoute? publicRoute = null)
    {
        if (linkTransport is not { Listening: true } transport || !transport.OpenInvite(InviteAddresses(), LINK_PAIRING_WINDOW,
            pairing => LinkAskPerson(pairing, host: true), LinkInvitationTerms, publicRoute))
            return false;
        linkDirty = true;
        return true;
    }

    private HexLinkPairingTerms LinkInvitationTerms()
    {
        LinkInviteOptions chosen = linkInviteOptions;
        return new HexLinkPairingTerms(chosen.Preset, chosen.Rights, LinkPresetRequest(chosen.Preset, chosen.Rights), linkAutoShareAvailable && chosen.Share);
    }

    /// <summary>Closes the pairing window, a pairing already under way going on to its end.</summary>
    internal void LinkClosePairing()
    {
        linkTransport?.ClosePairing();
        linkDirty = true;
    }

    /// <summary>
    /// Pairs with the device at an address, which has to have its pairing window open. The person here is asked about
    /// it as the person there is, unless <paramref name="ask"/> answers instead (a self test, never the program). Once
    /// paired the connection carries on as a link. How it ended, never throws but for Link being off.
    /// </summary>
    internal async Task<HexLinkPairResult> LinkPair(string host, int port, Func<HexLinkPairing, Task<HexLinkRights?>>? ask = null,
        HexLinkInvite? invite = null, HexLinkInviteRoute? overrideRoute = null)
    {
        HexLinkTransport transport = linkTransport ?? throw new InvalidOperationException(Said("link.off"));
        CancellationToken stop = linkCancel?.Token ?? CancellationToken.None;
        HexLinkPairResult result;
        try
        {
            result = await transport.Pair(host, port, ask ?? (pairing => LinkAskPerson(pairing, host: false)),
                stop, invite, overrideRoute);
        }
        catch (OperationCanceledException)
        {
            result = HexLinkPairResult.Failed("failed");
        }

        PostLink(() => LinkPairEnded(result));
        if (result.Connection is { } connection)
            await LinkAdd(connection);
        return result;
    }

    /// <summary>
    /// Links to a known device at an address (<paramref name="expectId"/> the one that has to answer there, when it's
    /// said). The peer once it's up; throws HexLinkRefused with why when it isn't let in (unknown when the other end
    /// doesn't know this one, busy when they're linked already).
    /// </summary>
    internal async Task<HexLinkPeer> LinkConnect(string host, int port, string? expectId = null, CancellationToken cancel = default)
    {
        HexLinkTransport transport = linkTransport ?? throw new InvalidOperationException(Said("link.off"));
        HexLinkConnection connection = await transport.Dial(host, port, expectId, cancel.CanBeCanceled ? cancel : linkCancel?.Token ?? CancellationToken.None);
        if (cancel.IsCancellationRequested)
        {
            connection.Dispose();
            cancel.ThrowIfCancellationRequested();
        }
        PostLink(() => linkHeldOff.Remove(connection.Device.Id));
        return await LinkAdd(connection) ?? throw new HexLinkRefused("refused", Said("link.not_linked", ("device", connection.Device.Name), ("why", "hello failed")));
    }

    /// <summary>A device's link cut by the person, not dialled again until they connect it. False when it wasn't linked.</summary>
    internal bool LinkDisconnect(string id)
    {
        if (linkManualDials.Remove(id, out LinkDeviceConnection? connecting))
            connecting.Attempt?.Cancel();
        linkHeldOff.Add(id);
        linkHeldOffBy.Remove(id);
        linkDirty = true;
        return link?.Remove(id, "it was disconnected here", bye: true) == true;
    }

    /// <summary>A device forgotten: its pin gone, its link cut the same moment, and it can't link again without pairing.</summary>
    internal bool LinkForget(string id)
    {
        if (linkTrust is null)
            return false;
        string name = LinkNameOf(id);
        bool forgot = linkTrust.Forget(id);
        linkWorkspaceOffers.Remove(id);
        link?.Remove(id, "it was forgotten here");
        linkRedial.Soon(id);
        linkHeldOff.Remove(id);
        if (linkManualDials.Remove(id, out LinkDeviceConnection? connecting))
            connecting.Attempt?.Cancel();
        // The transport writes it in the audit log as it cuts the link
        if (forgot)
            Say(Said("link.forgotten", ("device", name)));

        linkDirty = true;
        return forgot;
    }

    /// <summary>What a device may do here from now on, told to it at once and checked on its next request. False when it isn't known.</summary>
    internal bool LinkSetRights(string id, HexLinkRights rights)
    {
        if (linkTrust?.SetRights(id, rights) != true)
            return false;
        linkAudit?.Write("rights", id, string.Join(' ', HexLinkTrust.RightsNames(rights)));
        link?.RightsMoved(id);
        if (LinkPeerOf(id) is { } peer)
            LinkSyncRightsChangedLocally(peer);
        linkDirty = true;
        return true;
    }

    /// <summary>
    /// A known device called something of the person's own here, which wins over what it calls itself (cleaned, 1 to 40
    /// characters). False when it isn't known or nothing of the name is left.
    /// </summary>
    internal bool LinkRename(string id, string name)
    {
        if (HexLinkNames.Clean(name) is not { } named || linkTrust?.Rename(id, named) != true)
            return false;
        linkDirty = true;
        return true;
    }

    /// <summary>A known device shown by its own name again, what the person called it here let go of. False when it isn't known.</summary>
    internal bool LinkUseOwnName(string id)
    {
        if (linkTrust?.Rename(id, null) != true)
            return false;
        linkDirty = true;
        return true;
    }

    /// <summary>What a device is shown as here: the person's name for it, else its own (cleaned), else the start of its id.</summary>
    internal string LinkNameOf(string id, string? said = null) => LinkDeviceLabel(id, said, nearby: false);

    /* Pairing, asking the person */

    /// <summary>
    /// A pairing to ask the person about, from the transport's thread: shown on the editor's, answered when they pick
    /// (or the pairing ends without them, which its Ended token says, and the dialog closes).
    /// </summary>
    private Task<HexLinkRights?> LinkAskPerson(HexLinkPairing pairing, bool host)
    {
        HexLinkPairingTerms? terms = pairing.Terms;
        var ask = new LinkPairingAsk(pairing.Other, pairing.Code, pairing.Address, host, pairing.Ended)
        {
            Invited = pairing.Invited,
            Terms = terms,
            Preset = terms?.Preset ?? (!host || pairing.Invited ? HexLinkPreset.Collaborator : null),
            InitialRights = terms is not null ? host ? terms.HostGrants : terms.HostRequests
                : !host || pairing.Invited ? LINK_COLLABORATOR : HexLinkRights.None,
            ShareOnJoin = host && terms?.ShareWorkspace == true
        };
        pairing.Ended.Register(() =>
        {
            ask.Decide(null);
            LinkMoved();
        });
        PostLink(() => ShowLinkPairing(ask));
        return ask.Answer;
    }

    // The dialog that asks the person (ShowLinkPairing, CloseLinkPairingDialog) is DawnScene.LinkPairing

    // How a pairing ended, said, the device's redials started over when it paired
    private void LinkPairEnded(HexLinkPairResult result)
    {
        linkDirty = true;
        if (result.Device?.Id is { } id && linkShareAfterPairing == id)
        {
            linkShareAfterPairing = null;
            if (result.Paired)
                linkShareAfterConnected = (id, System.Environment.TickCount64 + 30000);
        }

        string device = result.Device?.Name ?? Said("link.another_device");
        if (result.Paired)
        {
            linkRedial.Soon(result.Device!.Id);
            Say(Said("link.paired", ("device", device), ("granted", LinkRightsSaid(result.Granted))));
            return;
        }

        if (result.Reason == "declined-here")
            return;
        string why = LinkPairReasonSaid(result.Reason);
        // The dialog still saying the pairing ended says why as well
        if (linkPairEnded is { Visible: true } ended)
            ended.Text = $"[color:{HexColour(LinkWarning)}]$(warning) {Horizon.UI.Drawing.UIRichText.Literal(Said("link.pair_ended_why", ("why", why)))}[/color]";
        Say(Said("link.pair_failed", ("device", device), ("why", why)), error: true);
    }
}

/// <summary>
/// A pairing waiting on the person, the model the pairing dialog shows: the other device (its id proved, its name only
/// what it says), the six digits both screens show, where it is, and whether this machine is the one that opened its
/// pairing window (the host) or the one that came to pair. <see cref="Decide"/> once the person has looked, with what
/// this machine grants the other or null to decline. <see cref="Ended"/> is cancelled when the pairing ends without
/// this side's answer (the other declined, two minutes passed, the connection broke); the dialog closes then.
/// </summary>
internal sealed class LinkPairingAsk(HexLinkDevice other, string code, string address, bool host, CancellationToken ended)
{
    private readonly TaskCompletionSource<HexLinkRights?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HexLinkDevice Other { get; } = other;

    public string Code { get; } = code;

    public string Address { get; } = address;

    public bool Host { get; } = host;

    public bool Invited { get; init; }
    public HexLinkPreset? Preset { get; init; }
    public HexLinkRights InitialRights { get; init; }
    public bool ShareOnJoin { get; init; }
    public HexLinkPairingTerms? Terms { get; init; }
    public bool CanApproveNone => !Host && Invited && Terms is { HostRequests: HexLinkRights.None, HostGrants: not HexLinkRights.None };
    public bool CanApprove(HexLinkRights rights) => CanApproveNone || DawnScene.LinkRightsAnything(rights);

    public CancellationToken Ended { get; } = ended;

    /// <summary>Whether it's been answered, or ended without an answer.</summary>
    public bool IsDecided => answer.Task.IsCompleted;

    /// <summary>What the person answered, the transport waiting on it.</summary>
    internal Task<HexLinkRights?> Answer => answer.Task;

    /// <summary>The person's answer, what this machine grants the other, null to decline. Only the first counts.</summary>
    public void Decide(HexLinkRights? granted) => answer.TrySetResult(granted);
}
