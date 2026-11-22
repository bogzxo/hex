using System.Globalization;
using System.Net;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HexLinkPortMapping? linkPortMapping;
    private ToggleButton? linkPublicSwitch, linkMapSwitch;
    private Label? linkPublicStatus;

    internal IPEndPoint? LinkMappedEndPoint => linkPortMapping?.Status.Endpoint;
    internal int? LinkMappedPort => LinkMappedEndPoint?.Port;

    private void ApplyLinkMapping()
    {
        if (settings.LinkAllowPublic && settings.LinkMapPublicPort && LinkPort > 0)
        {
            // Native fixtures install their own mapper. Never contact a router during a self-test.
            if (!options.SelfTest)
                linkPortMapping ??= new HexLinkPortMapping(new HexRouterPortMapper(), LinkMoved);
            _ = linkPortMapping?.SetPort(LinkPort);
        }
        else
            _ = linkPortMapping?.SetPort(0);
    }

    private void AddLinkPublicControls(StackPanel body)
    {
        linkPublicSwitch = body.Add(new ToggleButton(Said("link.public_switch"), settings.LinkAllowPublic)
        {
            Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("link.public_help")
        });
        linkPublicSwitch.OnPressed = () =>
        {
            bool enable = linkPublicSwitch.State;
            settings.LinkAllowPublic = enable;
            settings.LinkMapPublicPort = false;
            KeepLinkPublicSettings();
            if (enable && HexHost.Listens)
                AskLinkRouterConsent();
        };
        linkMapSwitch = body.Add(new ToggleButton(Said("link.public_map"), settings.LinkMapPublicPort)
        {
            Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT
        });
        linkMapSwitch.OnPressed = () =>
        {
            if (linkMapSwitch.State)
            {
                linkMapSwitch.State = false;
                AskLinkRouterConsent();
            }
            else
            {
                settings.LinkMapPublicPort = false;
                KeepLinkPublicSettings();
            }
        };
        linkPublicStatus = body.Add(LinkLine(string.Empty));
        RefreshLinkPublicControls();
    }

    private void AskLinkRouterConsent()
    {
        UIDialog.Show(chrome, Said("link.public_map_question"), Said("link.public_map_explain"),
            new DialogChoice(Said("link.public_map_allow"), () =>
            {
                if (settings.LinkAllowPublic)
                {
                    settings.LinkMapPublicPort = true;
                    KeepLinkPublicSettings();
                }
                ShowLinkIdentityDetails();
            }, Risky: true),
            new DialogChoice(Said("link.public_map_manual"), ShowLinkIdentityDetails));
    }

    private void KeepLinkPublicSettings()
    {
        SaveSettings();
        ApplyLink();
        RefreshLinkPublicControls();
    }

    private void RefreshLinkPublicControls()
    {
        if (linkPublicStatus is null || linkPublicSwitch is null || linkMapSwitch is null)
            return;
        linkPublicSwitch.State = settings.LinkAllowPublic;
        linkMapSwitch.Visible = HexHost.Listens && settings.LinkAllowPublic;
        linkMapSwitch.State = settings.LinkMapPublicPort;
        string key = !settings.LinkAllowPublic ? "link.public_private"
            : !HexHost.Listens ? "link.public_dial_only"
            : LinkPort == 0 ? "link.public_wait"
            : !settings.LinkMapPublicPort ? "link.public_manual"
            : LinkMappedEndPoint is not null ? "link.public_mapped"
            : linkPortMapping?.Status.Unavailable == true ? "link.public_unavailable"
            : "link.public_requesting";
        linkPublicStatus.Text = Said(key, ("port", LinkPort.ToString(CultureInfo.InvariantCulture)),
            ("endpoint", LinkMappedEndPoint?.ToString() ?? string.Empty));
    }
}
