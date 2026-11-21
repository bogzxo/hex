using System.Globalization;

namespace Hex.Dawn;

// Link's commands in the palette, all of them starting "Link:", what the status bar's link item opens the palette on.
// Enough to turn it on, pair, connect and forget with what Hex has already (its prompt, its menus and its question
// dialog), until the Link window does it all in one place.
internal sealed partial class DawnScene
{
    /// <summary>Link's commands for the palette, those that make sense right now.</summary>
    private List<(string Text, Action Picked)> LinkPaletteCommands()
    {
        var commands = new List<(string Text, Action Picked)>();
        if (!LinkOn)
        {
            commands.Add((Said("link.command_on"), () => LinkSwitch(true)));
            return commands;
        }

        commands.Add((Said("link.command_off"), () => LinkSwitch(false)));
        if (HexHost.Listens)
            commands.Add((Said("link.command_pair"), LinkPairCommand));
        commands.Add((Said("link.command_connect"), LinkConnectCommand));
        if (LinkKnown.Count > 0)
            commands.Add((Said("link.command_forget"), LinkForgetCommand));
        return commands;
    }

    // The link setting switched, as its row would, kept in the user's settings
    private void LinkSwitch(bool on)
    {
        settings.Link = on;
        SaveSettings();
        SettingsPage?.Refresh("link");
        if (on && options.SelfTest && !LinkOn)
            StartLink();
        else
            ApplyLink();
    }

    // The pairing window opened, the notice saying what to type on the other machine
    private void LinkPairCommand()
    {
        if (!LinkOpenPairing())
            return;
        string port = LinkPort.ToString(CultureInfo.InvariantCulture);
        string[] addresses = [.. HexLinkAddresses.Preferred(LinkLocalAddresses()).Select(address => address.ToString()).Take(3)];
        Say(Said("link.pairing_open", ("addresses", addresses.Length > 0 ? string.Join(", ", addresses) : "127.0.0.1"), ("port", port),
            ("minutes", ((int)LINK_PAIRING_WINDOW.TotalMinutes).ToString(CultureInfo.InvariantCulture))));
    }

    // This machine's private addresses, what another machine dials
    private static IEnumerable<System.Net.IPAddress> LinkLocalAddresses()
    {
        var found = new List<System.Net.IPAddress>();
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(unicast.Address))
                        found.Add(unicast.Address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        return found;
    }

    // An address asked for: a device this machine knows is linked to, one it doesn't is paired with
    private void LinkConnectCommand()
    {
        AskText(Said("link.connect_title"), Said("link.connect_text"), string.Empty, Said("link.connect_button"), typed =>
        {
            if (!TryLinkAddress(typed, HexLinkTransport.DEFAULT_PORT, out string host, out int port))
            {
                Say(Said("link.connect_bad", ("address", typed)), error: true);
                return;
            }

            HexLinkKnown? known = LinkKnown.FirstOrDefault(each => each.Address == host && (each.Port == port || each.Port == 0));
            _ = Task.Run(async () =>
            {
                try
                {
                    await LinkConnect(host, port, known?.Device.Id);
                }
                catch (HexLinkRefused refused) when (refused.Reason is "unknown" or "pin")
                {
                    // Not a device this one knows (or not the one that was there before), so a pairing, the person asked on both sides
                    PostLink(() => Say(Said("link.connect_pairing", ("address", $"{host}:{port}"))));
                    await LinkPair(host, port);
                }
                catch (Exception e)
                {
                    PostLink(() => Say(Said("link.connect_failed", ("address", $"{host}:{port}"), ("why", e.Message)), error: true));
                }
            });
        });
    }

    /// <summary>
    /// An address as somebody types it, host, host:port, [v6]:port or a bare IPv6 address, the port
    /// <paramref name="usual"/> when it's left out. False when it isn't one.
    /// </summary>
    internal static bool TryLinkAddress(string typed, int usual, out string host, out int port)
    {
        string said = typed.Trim();
        host = string.Empty;
        port = usual;
        if (said.Length == 0 || said.Any(char.IsWhiteSpace))
            return false;

        if (said.StartsWith('['))
        {
            int close = said.IndexOf(']');
            if (close < 0)
                return false;
            host = said[1..close];
            string rest = said[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port)))
                return false;
        }
        else if (said.Count(c => c == ':') == 1)
        {
            int colon = said.IndexOf(':');
            host = said[..colon];
            if (!int.TryParse(said[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                return false;
        }
        else
        {
            host = said;
        }

        return host.Length > 0 && port is > 0 and <= 65535;
    }

    // A menu of the known devices, the one picked forgotten
    private void LinkForgetCommand()
    {
        List<(string Text, Action Picked)> devices = [.. LinkKnown.Select(known => (
            $"{known.Device.Name}  ({known.Device.Fingerprint})",
            (Action)(() => LinkForget(known.Device.Id))))];
        ChooseFrom(Said("link.forget_title"), Said("link.forget_text"), devices);
    }
}
