using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkPublicInvites(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-public-invites-", test);
        bool publicWasEnabled = false;
        string? clipboard = null;
        HexLinkIdentity? identity = null;
        HexLinkTransport? host = null;
        HexLinkTrust? hostTrust = null;
        HexLinkSession? session = null;

        void Click(Func<Button?> target)
        {
            test.Settle(0.2f);
            test.Click(() =>
            {
                Button? button = target();
                return button?.Module is { } module ? module.ToWorld(button.Bounds.Center) : Vector2.Zero;
            });
        }

        void ClickOnboarding(string key) => Click(() => linkOnboardingDialog is { IsOpen: true } dialog
            ? Descendants(dialog).OfType<Button>().FirstOrDefault(button => button.Label == Said(key) && button.IsShownInHierarchy)
            : null);

        test.Run(() =>
        {
            StopLink();
            publicWasEnabled = settings.LinkAllowPublic;
            settings.LinkAllowPublic = true;
            StartLink(port: 0, home: Path.Combine(home, "local"), loopbackOnly: true);
            linkByTest = true;
            linkInviteCopyForTest = value => clipboard = value;
            ShowLinkDevicePage(LinkDevicePage.Invite);
        });
        test.WaitFor(() => LinkActiveInvite is not null && linkCopyInvite is not null, 5);
        test.Check("public invites: the ordinary invite stays private with public connections enabled", () =>
            LinkActiveInvite is { PublicRoute: null });
        ClickOnboarding("link.devices_public_invite");
        test.WaitFor(() => linkInviteRouteBox?.IsShownInHierarchy == true, 5);
        test.Run(() =>
        {
            if (linkInviteRouteBox is not null)
                linkInviteRouteBox.Text = "office-fixture.duckdns.org:49123";
        });
        ClickOnboarding("link.devices_public_invite_create");
        test.WaitFor(() => LinkActiveInvite?.PublicRoute is not null, 5);
        ClickOnboarding("link.devices_copy_invite");
        test.Check("public invites: the copied token keeps the public host and port alongside the private route", () =>
            HexLinkInvite.TryParse(clipboard ?? string.Empty, out var copied) &&
            copied?.PublicRoute is { Host: "office-fixture.duckdns.org", Port: 49123 } &&
            copied.Addresses.SequenceEqual([IPAddress.Loopback]));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "link_public_invite.png"));
        });
        ClickOnboarding("link.devices_private_invite");
        test.Check("public invites: creating a private invite removes the public route", () =>
            LinkActiveInvite is { PublicRoute: null });
        ClickOnboarding("link.devices_cancel_invite");
        test.WaitFor(() => linkOnboardingDialog is null && LinkActiveInvite is null, 5);

        test.Run(() =>
        {
            settings.LinkAllowPublic = false;
            if (linkTransport is not null)
                linkTransport.AllowPublic = false;
            string peer = Path.Combine(home, "peer");
            identity = HexLinkIdentity.LoadOrCreate(peer, "Invite fixture", fileOnly: true);
            hostTrust = new HexLinkTrust(peer);
            host = new HexLinkTransport(identity, hostTrust);
            host.Accepted = connection => session = new HexLinkSession(connection)
            {
                Requested = (method, _, _) => Task.FromResult<JsonNode?>(method == "link/hello"
                    ? new JsonObject
                    {
                        ["protocol"] = HexLinkHost.PROTOCOL,
                        ["device"] = HexLinkData.DeviceJson(identity.Device),
                        ["granted"] = HexLinkData.Words(HexLinkRights.View)
                    }
                    : new JsonObject())
            }.Start();
            host.Start(0, loopbackOnly: true);
            host.OpenInvite([IPAddress.Parse("127.0.0.2")], TimeSpan.FromMinutes(1),
                _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View));
            ShowLinkDevicePage(LinkDevicePage.Join);
            if (linkAddressBox is not null)
                linkAddressBox.Text = host.ActiveInvite?.Encode() ?? string.Empty;
            if (linkJoinRouteBox is not null)
                linkJoinRouteBox.Text = "https://invalid";
            LinkSetJoinMessage(null);
        });
        ClickOnboarding("link.devices_join_submit");
        test.Check("public invites: an invalid route is explained before dialing", () =>
            linkJoinRouteBox?.IsShownInHierarchy == true &&
            linkJoinStatus?.Text.Contains(Said("link.devices_invite_bad_route"), StringComparison.Ordinal) == true);
        test.Run(() =>
        {
            if (linkJoinRouteBox is not null)
                linkJoinRouteBox.Text = $"localhost:{host?.Port}";
            LinkSetJoinMessage(null);
        });
        ClickOnboarding("link.devices_join_submit");
        test.WaitFor(() => linkPairingDialog is { IsOpen: true }, 8);
        test.Check("public invites: a private override still uses the verified invitation and no comparison code", () =>
            LinkPairingPending is { Invited: true, Code.Length: 0 } pending && pending.Other.Id == identity?.Id);
        Click(() => linkPairingDialog is { IsOpen: true } dialog
            ? Descendants(dialog).OfType<Button>().FirstOrDefault(button => button.Label == Said("link.devices_preset_viewer"))
            : null);
        Click(() => linkPairingDialog?.Buttons[0]);
        test.WaitFor(() => identity is not null && LinkPeerOf(identity.Id) is not null, 8);
        test.Check("public invites: pairing consumes the invite and remembers the explicitly dialed route and grants", () =>
            host?.ActiveInvite is null && identity is not null && LinkPeerOf(identity.Id) is not null &&
            LinkKnown.Any(known => known.Device.Id == identity.Id && known.Address == "localhost" &&
                known.Port == host?.Port && known.Rights == HexLinkRights.View) &&
            LinkDevice is { } local && hostTrust?.Find(local.Id)?.Rights == HexLinkRights.View);
        test.Run(() =>
        {
            linkOnboardingDialog?.Close();
            CloseLinkPairingDialog();
            linkWindow?.Close();
            linkInviteCopyForTest = null;
            settings.LinkAllowPublic = publicWasEnabled;
            session?.Dispose();
            host?.Dispose();
            StopLink();
        });
    }
}
