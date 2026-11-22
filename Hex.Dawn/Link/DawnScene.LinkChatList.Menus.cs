using System.Globalization;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private string? linkChatLastRefusal;

    private Menu LinkRemoteMenu(LinkRemoteCard remote)
    {
        var menu = new Menu("remote chat");
        bool Online() => LinkChatOnline(remote.DeviceId);
        if (!Online())
            menu.Add(new MenuItem { Label = LinkChatOfflineReason(remote.DeviceName), Icon = "info", IsEnabled = () => false });
        menu.Add(Said("agents_panel.chat_open"), () => OpenRemoteChat(remote), icon: "comment").IsEnabled = Online;
        menu.AddSeparator();
        menu.Add(Said("link.chat_rename"), () => LinkAskRemoteRename(remote), icon: "edit").IsEnabled = Online;
        menu.Add(Said("link.chat_stop"), () => LinkRemoteManage(remote, "stop"), icon: "debug-stop").IsEnabled = Online;
        menu.Add(Said("link.chat_restart"), () => LinkRemoteManage(remote, "restart"), icon: "debug-restart").IsEnabled = Online;
        menu.AddSeparator();
        menu.Add(Said("link.chat_close"), () => LinkAskRemoteEnd(remote, delete: false), icon: "close").IsEnabled = Online;
        menu.Add(Said("link.chat_delete"), () => LinkAskRemoteEnd(remote, delete: true), icon: "trash", destructive: true).IsEnabled = Online;
        return menu;
    }

    private void LinkAskRemoteRename(LinkRemoteCard remote)
    {
        string name = LinkChatRows(remote.DeviceId)?.FirstOrDefault(row => row.Id == remote.Id)?.Name ?? string.Empty;
        ((ITerminalHostUi)this).AskText(Said("link.chat_rename_title"), string.Empty, name, typed =>
        {
            string next = typed.Trim();
            if (next.Length > 0 && next != name)
                LinkRemoteManage(remote, "rename", next);
        });
    }

    private void LinkAskRemoteEnd(LinkRemoteCard remote, bool delete)
    {
        string title = Said(delete ? "link.chat_delete_title" : "link.chat_close_title");
        string text = Said(delete ? "link.chat_delete_text" : "link.chat_close_text",
            ("chat", remote.Id.ToString(CultureInfo.InvariantCulture)), ("device", remote.DeviceName));
        string yes = Said(delete ? "link.chat_delete_go" : "link.chat_close_go");
        Context.Windows.Ask(title, text, [Said("git.discard_no"), yes], answer =>
        {
            if (answer == yes)
                LinkRemoteManage(remote, delete ? "delete" : "close");
        }, risky: yes);
    }

    private void LinkRemoteManage(LinkRemoteCard remote, string action, string? text = null) =>
        LinkRemoteManage(remote, [remote.Id], action, text);

    private void LinkRemoteManage(LinkRemoteCard remote, IReadOnlyList<int> ids, string action, string? text = null)
    {
        LinkChatDevice? fake = LinkChatFake(remote.DeviceId);
        HexLinkPeer? peer = fake is null ? LinkPeerOf(remote.DeviceId) : null;
        if (fake is { Online: false } || (fake is null && peer is null))
        {
            Say(LinkChatOfflineReason(remote.DeviceName), error: true);
            return;
        }

        Func<int, string, string?, Task<(bool Ok, string Message)>>? act = fake?.Act;
        if (peer is not null)
            act = (id, operation, value) => peer.AgentAction(id, operation, value);
        if (act is null)
            return;

        _ = Task.Run(async () =>
        {
            foreach (int id in ids)
            {
                (bool ok, string message) result;
                try
                {
                    result = await act(id, action, text);
                }
                catch (Exception error)
                {
                    result = (false, error.Message);
                }

                if (!result.ok)
                    PostLink(() =>
                    {
                        linkChatLastRefusal = result.message;
                        Say(Said("link.chat_refused", ("device", remote.DeviceName), ("why", result.message)), error: true);
                    });
            }
        });
    }

    private IReadOnlyList<HexLinkRow>? LinkChatRows(string deviceId) =>
        LinkChatFake(deviceId)?.Rows ?? LinkPeerOf(deviceId)?.Fleet;

    private Menu LinkDeviceMenu(LinkChatDevice device)
    {
        var menu = new Menu("device chats");
        menu.Add(Said("link.device_collapse_all"), () => SetLinkDeviceOpen(device.Id, false), icon: "chevron-right");
        menu.Add(Said("link.device_expand_all"), () => SetLinkDeviceOpen(device.Id, true), icon: "chevron-down");
        menu.AddSeparator();
        menu.Add(Said("link.device_open_window"), ShowLinkWindow, icon: "vm");
        int closable = HexLinkChatGroups.ClosableFinished(device.Rows).Count;
        menu.Add(Said("link.device_close_finished"), () => LinkAskCloseFinished(device), icon: "close", destructive: true)
            .IsEnabled = () => LinkChatOnline(device.Id) && closable > 0;
        return menu;
    }

    private void SetLinkDeviceOpen(string id, bool open)
    {
        LinkChatFolds.Set(HexLinkChatGroups.DeviceKey(id), open);
        foreach (HexLinkChatSection section in linkChatSections)
            LinkChatFolds.Set(HexLinkChatGroups.SectionKey(id, section), open);
        SaveLinkChatFolds();
    }

    private void LinkAskCloseFinished(LinkChatDevice device)
    {
        IReadOnlyList<HexLinkRow>? rows = LinkChatRows(device.Id);
        if (!LinkChatOnline(device.Id) || rows is null)
            return;
        List<HexLinkRow> closable = HexLinkChatGroups.ClosableFinished(rows);
        if (closable.Count == 0)
            return;
        string yes = Said("link.chat_delete_go");
        string text = Said(closable.Count == 1 ? "link.device_close_finished_one" : "link.device_close_finished_text",
            ("device", device.Name), ("count", closable.Count.ToString(CultureInfo.InvariantCulture)));
        Context.Windows.Ask(Said("link.device_close_finished_title"), text, [Said("git.discard_no"), yes], answer =>
        {
            if (answer != yes)
                return;
            if (!LinkChatOnline(device.Id) || LinkChatRows(device.Id) is not { } current)
            {
                Say(LinkChatOfflineReason(device.Name), error: true);
                return;
            }
            List<HexLinkRow> stillClosable = HexLinkChatGroups.ClosableFinished(current);
            if (!stillClosable.Select(row => (row.Id, row.Parent)).ToHashSet().SetEquals(closable.Select(row => (row.Id, row.Parent))))
            {
                Say(Said("link.device_close_finished_changed"), error: true);
                return;
            }
            List<HexLinkRow> tops = HexLinkChatGroups.ClosableTops(stillClosable);
            if (tops.Count > 0)
                LinkRemoteManage(new LinkRemoteCard(device.Id, device.Name, tops[0].Id), tops.Select(row => row.Id).ToArray(), "delete");
        }, risky: yes);
    }
}
