using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkSessionNotice(string Wire, string People, bool ReadOnly, bool Offline, string? Draft, bool CanFollow,
        string OwnerId, string Owner);
    private sealed record LinkSessionPerson(string Id, string Name);

    private void DrawLinkSessionNotice(StackPanel notices, HexDocument shown, LinkSessionNotice session, LinkClaimMark? claim, float width)
    {
        string prefix = claim is null ? string.Empty : LinkClaimLabel(claim) + ". ";
        if (session.Draft is { } draft)
        {
            DrawEditorLinkNotice(notices, prefix + Said("link.doc_draft") + " " + OneLine(draft, 180),
                "warning", warning: true, width);
            return;
        }

        if (session.Offline)
        {
            var reconnect = LinkButton(Said("link.devices_session_reconnect"), "plug", () => LinkRetrySession(session.OwnerId));
            DrawEditorLinkNotice(notices, prefix + Said("link.devices_session_offline", ("device", session.Owner)),
                "debug-disconnect", warning: true, width, reconnect);
            return;
        }

        string roster = session.People.Length == 0 ? Said("link.devices_session_solo")
            : Said("link.devices_session_people", ("people", session.People));
        string title = claim is null ? roster : LinkClaimLabel(claim);
        if (session.ReadOnly)
            title += ". " + Said("link.devices_session_reader");

        Button? follow = null;
        if (session.CanFollow || linkFollowing is not null)
        {
            follow = LinkButton(Said("link.devices_session_follow"), "eye", () => { });
            follow.OnPressed = () => ShowLinkSessionPeople(shown, follow);
        }

        StackPanel card = DrawEditorLinkNotice(notices, title, claim is null ? "link" : "lock", warning: claim is not null, width, follow);
        card.Tooltip = roster + "\n" + session.Wire;
    }

    private void LinkRetrySession(string owner)
    {
        if (LinkPeerOf(owner) is { } peer)
        {
            if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                Say(Said("link.devices_session_rejoining", ("device", peer.Device.Name)));
            else
                Say(Said("link.devices_session_need_view", ("device", peer.Device.Name)), error: true);
            return;
        }

        if (LinkOn && LinkKnown.FirstOrDefault(device => device.Device.Id.Equals(owner, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            if (LinkReconnectAddresses(known).Length > 0)
                LinkConnectKnown(known);
            else
                ShowLinkDevicePage(LinkDevicePage.Join);
        }
        else
            ShowLinkWindow();
    }

    private void ShowLinkSessionPeople(HexDocument shown, Button opener)
    {
        IReadOnlyList<LinkSessionPerson> people = [];
        LinkReadSessionPeople(shown, ref people);
        if (people.Count == 0 && linkFollowing is null)
        {
            Say(Said("link.devices_session_nobody"));
            return;
        }

        var menu = new Menu("shared session participants");
        foreach (LinkSessionPerson person in people)
            menu.Add(Said("link.devices_session_follow_person", ("device", person.Name)), () => LinkFollow(person.Id), icon: "eye")
                .IsChecked = () => linkFollowing == person.Id;
        if (linkFollowing is not null)
            menu.Add(Said("link.devices_session_unfollow"), () => LinkUnfollow(said: true), icon: "close");
        ContextMenu.Show(chrome, opener.Bounds.PointAt(Origin.TopLeft), menu);
    }

    partial void LinkReadSessionNotice(HexDocument tab, ref LinkSessionNotice? session);
    partial void LinkReadSessionPeople(HexDocument tab, ref IReadOnlyList<LinkSessionPerson> people);
}
