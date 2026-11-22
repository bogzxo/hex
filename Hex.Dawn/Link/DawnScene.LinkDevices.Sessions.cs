using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkSessionNotice(string Wire, string People, bool ReadOnly, bool Offline, string? Draft, bool CanFollow);
    private sealed record LinkSessionPerson(string Id, string Name);

    private void DrawLinkSessionNotice(StackPanel notices, HexDocument shown, LinkSessionNotice session, float width)
    {
        if (session.Draft is { } draft)
        {
            DrawEditorLinkNotice(notices, Said("link.doc_draft") + " " + OneLine(draft, 180), "warning", warning: true, width);
            return;
        }
        if (session.Offline)
        {
            DrawEditorLinkNotice(notices, Said("link.devices_session_offline"), "debug-disconnect", warning: true, width);
            return;
        }

        string title = session.People.Length == 0 ? Said("link.devices_session_solo")
            : Said("link.devices_session_people", ("people", session.People));
        Button? follow = null;
        if (session.CanFollow)
        {
            follow = LinkButton(Said("link.devices_session_follow"), "eye", () => { });
            follow.OnPressed = () => ShowLinkSessionPeople(shown, follow);
        }

        StackPanel card = DrawEditorLinkNotice(notices, title, "link", warning: false, width, follow);
        card.Tooltip = session.Wire;
        if (session.ReadOnly)
            card.Add(LinkLine(Said("link.devices_session_reader")));
    }

    private void ShowLinkSessionPeople(HexDocument shown, Button opener)
    {
        IReadOnlyList<LinkSessionPerson> people = [];
        LinkReadSessionPeople(shown, ref people);
        if (people.Count == 0)
        {
            Say(Said("link.devices_session_nobody"));
            return;
        }

        var menu = new Menu("shared session participants");
        foreach (LinkSessionPerson person in people)
            menu.Add(Said("link.command_follow", ("device", person.Name)), () => LinkFollow(person.Id), icon: "eye")
                .IsChecked = () => linkFollowing == person.Id;
        ContextMenu.Show(chrome, opener.Bounds.PointAt(Origin.TopLeft), menu);
    }

    partial void LinkReadSessionNotice(HexDocument tab, ref LinkSessionNotice? session);
    partial void LinkReadSessionPeople(HexDocument tab, ref IReadOnlyList<LinkSessionPerson> people);
}
