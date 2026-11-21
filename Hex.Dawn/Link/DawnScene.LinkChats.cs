using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// A chat of another device on screen (docs/hex-link-plan.md, 5.1). Picking its card puts its stand-in up at once and
// asks the device for the chat, which comes back as a mirror the peer keeps patched (HexLinkPeer.OpenChat, pumped every
// tick by the wiring): its rows, tools, plan and what it used, drawn by the same view local chats are. The mirror is
// never added to the hub, so this machine's ids, runs, limits and saved chats never see it; picking another chat lets it
// go (CloseChat). Its approvals and questions answer over the link as the peer made them.
//
// A banner over the box says which machine it runs on, with Stop and Restart when that machine lets this one steer.
// The box sends with hex_agent_send there, and what was sent is said in the banner as waiting until the mirror shows
// it. Without Steer the box is shut and says the device lets this machine watch, not steer. The link dropping greys the
// chat and says so; it comes back by itself when the device links again.
internal sealed partial class DawnScene
{
    // The remote chat on screen, its mirror once it's come and the peer it came over
    private LinkRemoteCard? linkRemoteOpen;
    private HexAgentChat? linkRemoteMirror;
    private HexLinkPeer? linkRemotePeer;
    private bool linkRemoteOpening;

    // What was sent to it and not seen in the mirror yet, and until when it's said as waiting
    private string? linkRemoteQueued;
    private long linkRemoteQueuedUntil;

    // How long something sent is said as waiting at the most
    private const long LINK_QUEUED_MS = 30_000;

    // The banner over the box, how tall it is, and what it was last drawn from
    private Panel? linkBanner;
    private const float LINK_BANNER_TALL = 32.0f;
    private (LinkRemoteCard? Chat, int State, string? Queued, int Width, bool Running) linkBannerDrawn;

    // What the box's placeholder said before a remote chat said why it's shut
    private string? linkPlaceholderWas;

    /// <summary>The room the banner takes over the box while it's up.</summary>
    private float LinkBannerTall => linkBanner is { Visible: true } ? LINK_BANNER_TALL + 6.0f : 0.0f;

    /// <summary>A remote chat put on screen: its stand-in at once, its mirror as soon as the device sends it.</summary>
    private void OpenRemoteChat(LinkRemoteCard remote)
    {
        FleetPicked();
        if (linkRemoteOpen == remote && LinkRemoteOf(agentChat) == remote)
            return;

        CloseRemoteMirror();
        linkRemoteOpen = remote;
        linkRemoteQueued = null;
        HexAgentChat? standIn = linkStandIns.GetValueOrDefault((remote.DeviceId, remote.Id));
        if (standIn is null)
        {
            // Not drawn yet (one just started there), a stand-in of its own until the mirror comes
            standIn = new HexAgentChat(remote.Id, "agent");
            linkRemoteOf[standIn] = remote;
        }

        ShowDraftOf(standIn);
        LinkLoadMirror(remote);
    }

    // The chat asked of the device, put on screen when it comes if it's still the one wanted
    private void LinkLoadMirror(LinkRemoteCard remote)
    {
        if (LinkPeerOf(remote.DeviceId) is not { } peer || linkRemoteOpening)
            return;
        linkRemoteOpening = true;
        _ = Task.Run(async () =>
        {
            try
            {
                HexAgentChat mirror = await peer.OpenChat(remote.Id);
                PostLink(() =>
                {
                    linkRemoteOpening = false;
                    if (linkRemoteOpen != remote || LinkRemoteOf(agentChat) != remote)
                    {
                        peer.CloseChat(remote.Id);
                        return;
                    }

                    linkRemoteMirror = mirror;
                    linkRemotePeer = peer;
                    linkRemoteOf[mirror] = remote;
                    mirror.Seen = mirror.Version;
                    // In the stand-in's place, what's typed in the box kept
                    agentChat = mirror;
                    linkBannerDrawn = default;
                });
            }
            catch (Exception e)
            {
                PostLink(() =>
                {
                    linkRemoteOpening = false;
                    if (linkRemoteOpen == remote)
                        Say(Said("link.chat_open_failed", ("device", remote.DeviceName), ("why", e.Message)), error: true);
                });
            }
        });
    }

    // The mirror on screen let go of, the device told to stop sending it
    private void CloseRemoteMirror()
    {
        if (linkRemoteMirror is { } mirror)
        {
            linkRemoteOf.Remove(mirror);
            if (linkRemotePeer is { IsClosed: false } peer && linkRemoteOpen is { } open)
                peer.CloseChat(open.Id);
        }

        linkRemoteMirror = null;
        linkRemotePeer = null;
        linkRemoteOpen = null;
        linkRemoteQueued = null;
    }

    /// <summary>
    /// Every tick: a remote chat that's no longer on screen let go of, one whose link dropped asked for again once its
    /// device is back, and what was sent to it taken off the banner once the mirror shows it. Nothing when none is open.
    /// </summary>
    private void LinkRemoteTick()
    {
        if (linkRemoteOpen is not { } open)
            return;

        if (LinkRemoteOf(agentChat) != open)
        {
            CloseRemoteMirror();
            linkBannerDrawn = default;
            return;
        }

        // Its device forgotten (or Link turned off): the chat put away, there's no coming back to it
        if (link is null || !LinkKnown.Any(known => known.Device.Id.Equals(open.DeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            CloseRemoteMirror();
            linkRemoteOf.Remove(agentChat!);
            ShowDraftOf(agentChats.Count > 0 ? agentChats[^1] : null);
            linkBannerDrawn = default;
            return;
        }

        // Back after the link dropped: the device linked again, the chat asked for anew over the new link
        if ((linkRemotePeer is { IsClosed: true } || linkRemoteMirror is null) && LinkPeerOf(open.DeviceId) is { } back && back != linkRemotePeer
            && HexLinkAllowed.Has(back.Granted, HexLinkRights.View) && !linkRemoteOpening)
        {
            linkRemotePeer = null;
            LinkLoadMirror(open);
        }

        if (linkRemoteQueued is { } queued && (System.Environment.TickCount64 > linkRemoteQueuedUntil
            || (linkRemoteMirror is { } mirror && mirror.Entries.Any(entry => entry.Kind == HexAgentEntryKind.User && entry.Text.Contains(queued, StringComparison.Ordinal)))))
            linkRemoteQueued = null;
    }

    /// <summary>Whether a chat can be steered from here: every one of this machine's, another device's while it grants Steer.</summary>
    private bool LinkMaySteer(HexAgentChat? chat) =>
        LinkRemoteOf(chat) is not { } remote || (LinkPeerOf(remote.DeviceId) is { } peer && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer));

    /// <summary>
    /// Whether the box can send for what's on screen, null when it's no business of Link's: a remote chat can be told
    /// something while its device is linked and lets this one steer, a new chat while a device it runs on is picked.
    /// </summary>
    private bool? LinkCanCompose()
    {
        if (LinkRemoteOf(agentChat) is { } remote)
            return LinkPeerOf(remote.DeviceId) is { } peer && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer);
        if (agentChat is null && LinkRunsOnPeer() is not null)
            return true;
        return null;
    }

    /// <summary>
    /// What's in the box sent where Link says, true when it was Link's to send: to the remote chat on screen, or as a new
    /// chat on the device picked under Runs on. False leaves it to the usual way.
    /// </summary>
    private bool LinkSendFromBox(string asked)
    {
        if (LinkRemoteOf(agentChat) is { } remote)
        {
            if (LinkPeerOf(remote.DeviceId) is not { } peer || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))
            {
                Say(Said("link.chat_watch_only", ("device", remote.DeviceName)), error: true);
                return true;
            }

            agentsInput.Text = string.Empty;
            RememberAsked(asked);
            linkRemoteQueued = asked;
            linkRemoteQueuedUntil = System.Environment.TickCount64 + LINK_QUEUED_MS;
            linkBannerDrawn = default;
            _ = Task.Run(async () =>
            {
                try
                {
                    McpToolResult result = await peer.Send(remote.Id, asked);
                    if (result.IsError)
                        PostLink(() => LinkRemoteFailed(remote, result.AllText()));
                }
                catch (Exception e)
                {
                    PostLink(() => LinkRemoteFailed(remote, e.Message));
                }
            });
            return true;
        }

        if (agentChat is null && LinkRunsOnPeer() is { } there)
        {
            agentsInput.Text = string.Empty;
            RememberAsked(asked);
            LinkSpawnThere(there, asked);
            return true;
        }

        return false;
    }

    // Something asked of a remote chat that it refused, said, and the banner's waiting line taken off
    private void LinkRemoteFailed(LinkRemoteCard remote, string why)
    {
        linkRemoteQueued = null;
        linkBannerDrawn = default;
        Say(Said("link.chat_send_failed", ("device", remote.DeviceName), ("why", OneLine(why, 200))), error: true);
    }

    /// <summary>Stop pressed for a chat that's another device's, true when it was one (the device asked, nothing here stopped).</summary>
    private bool LinkStopRemote(HexAgentChat? chat)
    {
        if (LinkRemoteOf(chat) is not { } remote)
            return false;
        LinkRemoteStop(remote);
        return true;
    }

    private void LinkRemoteStop(LinkRemoteCard remote) => LinkRemoteAsk(remote, peer => peer.Stop(remote.Id));

    private void LinkRemoteRestart(LinkRemoteCard remote) => LinkRemoteAsk(remote, peer => peer.Restart(remote.Id));

    // Something asked of a remote chat over its link, a refusal said
    private void LinkRemoteAsk(LinkRemoteCard remote, Func<HexLinkPeer, Task<McpToolResult>> ask)
    {
        if (LinkPeerOf(remote.DeviceId) is not { } peer)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                McpToolResult result = await ask(peer);
                if (result.IsError)
                    PostLink(() => LinkRemoteFailed(remote, result.AllText()));
            }
            catch (Exception e)
            {
                PostLink(() => LinkRemoteFailed(remote, e.Message));
            }
        });
    }

    /* The banner and the box */

    /// <summary>
    /// The banner over the box for a remote chat: where it runs, whether it's linked, Stop and Restart when it may be
    /// steered, what was sent and is waiting; and the box shut with the reason when it can't be. Made again only when one
    /// of those moved, ten times a second at the most with the rest of the panel.
    /// </summary>
    private void DrawLinkBanner()
    {
        LinkRemoteCard? remote = LinkRemoteOf(agentChat);
        HexLinkPeer? peer = remote is null ? null : LinkPeerOf(remote.DeviceId);
        int state = remote is null ? 0 : peer is null ? 1 : HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer) ? 3 : 2;
        var now = (remote, state, linkRemoteQueued, (int)agentsView.Size.X, agentChat?.Running == true);
        if (now == linkBannerDrawn)
            return;
        linkBannerDrawn = now;

        // The box shut, saying why, for a chat this machine can only watch or that isn't linked now
        bool shut = remote is not null && state != 3;
        agentsInput.ReadOnly = shut;
        agentsStop.Enabled = !shut;
        linkPlaceholderWas ??= agentsPlaceholder.Text;
        agentsPlaceholder.Text = remote is null ? linkPlaceholderWas
            : state == 1 ? Said("link.chat_not_connected", ("device", remote.DeviceName))
            : state == 2 ? Said("link.chat_watch_only", ("device", remote.DeviceName))
            : Said("link.chat_placeholder", ("device", remote.DeviceName));

        bool was = linkBanner is { Visible: true };
        if (remote is null)
        {
            if (linkBanner is not null)
                linkBanner.Visible = false;
            if (was)
                PlaceAgentsList();
            return;
        }

        Vector4 tint = LinkDeviceTint(remote.DeviceId, Skin);
        linkBanner ??= agentsView.Add(new Panel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_BANNER_TALL), Padding = new UIEdges(10, 3, 6, 3), Radius = 8
        });
        Panel banner = linkBanner;
        foreach (UIComponent child in banner.Children.ToArray())
            banner.Remove(child);
        Vector4 tone = state == 1 ? Skin?.Palette.Error ?? new Vector4(0.9f, 0.3f, 0.3f, 1) : tint;
        banner.Color = tone with { W = 0.12f };
        banner.Outline = tone with { W = 0.45f };
        banner.Visible = true;
        banner.Position = new Vector2(0, agentsComposeTall + 6.0f);

        var row = banner.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Fill = UIFill.Both });
        var buttons = new List<Button>();
        if (state == 3 && peer is not null)
        {
            buttons.Add(new Button { Label = Said("link.chat_stop"), Icon = "debug-stop", LabelScale = 0.125f, Animated = false, Enabled = agentChat?.Running == true, OnPressed = () => LinkRemoteStop(remote) });
            buttons.Add(new Button { Label = Said("link.chat_restart"), Icon = "debug-restart", Style = "button_flat", LabelScale = 0.125f, Animated = false, OnPressed = () => LinkRemoteRestart(remote) });
        }

        float buttonsWide = 0;
        foreach (Button button in buttons)
        {
            float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine($"$({button.Icon}) {button.Label}", 0.125f, UITextStyle.None, default) + skin.ButtonPadding.Total.X + 10.0f) : 90.0f;
            button.Size = new Vector2(wide, 24);
            button.Anchor = Origin.Left;
            button.Pivot = Origin.Left;
            buttonsWide += wide + row.Spacing;
        }

        string said = Said("link.chat_runs_on", ("device", remote.DeviceName));
        if (state == 1)
            said += " · " + Said("link.chat_not_connected_short");
        else if (state == 2)
            said += " · " + Said("link.chat_watch_only", ("device", remote.DeviceName));
        else if (linkRemoteQueued is { } queued)
            said += " · " + Said("link.chat_queued", ("text", OneLine(queued, 60)));
        float room = MathF.Max(40, agentsView.Size.X - banner.Padding.Total.X - buttonsWide - 8);
        row.Add(new Label
        {
            Text = $"[color:{HexColour(tone)}]$(vm)[/color] {UIRichText.Literal(FitText(said, 0.13f, room - 20))}", TextScale = 0.13f, Align = Origin.Left,
            Size = new Vector2(room, 24), Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = said
        });
        foreach (Button button in buttons)
            row.Add(button);

        // The chat itself greyed while it isn't linked
        agentsScroll.Opacity = state == 1 ? 0.5f : 1.0f;
        if (!was)
            PlaceAgentsList();
    }

    /* Rights changed on the other side */

    // What each linked device granted this one as last seen, to say when it changes there
    private readonly Dictionary<string, HexLinkRights> linkGrantsSeen = new(StringComparer.OrdinalIgnoreCase);
    private int linkGrantsSeenAt = -1;

    /// <summary>A notice when a linked device changed what it lets this machine do, looked at only when Link moved.</summary>
    private void LinkSayGrantsMoved()
    {
        if (linkGrantsSeenAt == LinkVersion)
            return;
        linkGrantsSeenAt = LinkVersion;
        foreach (HexLinkPeer peer in LinkPeers)
        {
            if (peer.IsClosed || peer.Protocol == 0)
                continue;
            if (linkGrantsSeen.TryGetValue(peer.Device.Id, out HexLinkRights was) && was != peer.Granted)
                Say(Said("link.rights_changed_there", ("device", peer.Device.Name), ("there", LinkRightsSaid(peer.Granted))));
            linkGrantsSeen[peer.Device.Id] = peer.Granted;
        }
    }
}
