using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// What a right click on the Link item of the status bar lists: the Devices window, an invite, pause and disconnect for
    /// each device linked now, Link Chat, a port to share and Link off. Made as it is pressed, so it says how things stand.
    /// </summary>
    private Menu LinkStatusMenu()
    {
        var menu = new Menu("link status");
        menu.Add(Said("link.status_menu_devices"), ShowLinkWindow, icon: "link");
        menu.Add(Said("link.status_menu_invite"), () => ShowLinkDevicePage(LinkDevicePage.Invite), icon: "add");

        foreach (HexLinkPeer peer in LinkPeers.ToArray())
        {
            string id = peer.Device.Id, name = OneLine(LinkNameOf(id, peer.Device.Name), 30);
            HexLinkDevicePause pause = LinkDevicePauseOf(peer);
            if (pause.CanPause)
                menu.Add(Said("link.status_menu_pause", ("device", name)), () => LinkPauseDevice(id, pause), icon: "debug-pause");
            else if (pause.CanResume && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                menu.Add(Said("link.status_menu_resume", ("device", name)), () => LinkPauseDevice(id, pause), icon: "sync");
        }

        foreach (HexLinkPeer peer in LinkPeers.ToArray())
        {
            string id = peer.Device.Id, name = OneLine(LinkNameOf(id, peer.Device.Name), 30);
            menu.Add(Said("link.status_menu_disconnect", ("device", name)), () =>
            {
                LinkDisconnect(id);
                Say(Said("link.disconnected", ("device", LinkNameOf(id, peer.Device.Name))));
            }, icon: "debug-disconnect");
        }

        menu.AddSeparator();
        if (LinkPeers.Count > 0)
            menu.Add(Said("link.status_menu_chat"), ToggleLinkTalk, icon: "comment-discussion");
        if (LinkPortsOf is not null)
            menu.Add(Said("link.ports_share"), () => LinkAskShare(null), icon: "add");
        menu.AddSeparator();
        menu.Add(Said("link.status_menu_off"), () => LinkSwitch(false), icon: "debug-stop");
        return menu;
    }
}
