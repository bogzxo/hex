using System.Net;
using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class FixtureRouter : IHexRouterPortMapper, IHexRouterPortLease
    {
        public int Requests, Releases;
        public IPEndPoint PublicEndPoint => new(IPAddress.Parse("203.0.113.10"), 49123);
        public DateTimeOffset ExpiresAt => DateTimeOffset.UtcNow.AddMinutes(10);
        public Task<IHexRouterPortLease?> TryMapAsync(int localTcpPort, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult<IHexRouterPortLease?>(this);
        }
        public Task<bool> RenewAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Releases);
            return ValueTask.CompletedTask;
        }
    }

    private void TestLinkPublic(HexPointerScript test)
    {
        var router = new FixtureRouter();
        string home = LinkTestFolder("hex-link-public-", test);
        void Click(string key)
        {
            test.Settle(0.2f);
            test.Click(() =>
            {
                var dialog = chrome.Root.Children.OfType<UIDialog>().LastOrDefault(item => item.IsOpen);
                UIComponent? control = key switch
                {
                    "link.public_switch" => linkPublicSwitch,
                    "link.public_map" => linkMapSwitch,
                    _ => dialog is null ? null : Descendants(dialog).OfType<Button>()
                        .FirstOrDefault(item => item.Label == Said(key) && item.IsShownInHierarchy)
                };
                return control?.Module is { } module ? module.ToWorld(control.Bounds.Center) : Vector2.Zero;
            });
        }
        test.Run(() =>
        {
            StopLink();
            settings.LinkAllowPublic = settings.LinkMapPublicPort = false;
            linkPortMapping = new HexLinkPortMapping(router, LinkMoved);
            StartLink(port: 0, home: home, loopbackOnly: true);
            linkByTest = true;
            ShowLinkWindowForTest();
            ShowLinkIdentityDetails();
        });
        Click("link.public_switch");
        test.Check("link-public: enabling public access asks before contacting the router", () =>
            linkTransport?.AllowPublic == true && !settings.LinkMapPublicPort && router.Requests == 0 &&
            chrome.Root.Children.OfType<UIDialog>().Any(dialog => dialog.IsOpen && dialog.Buttons.Last().Label == Said("link.public_map_manual")));
        void Capture(string file) => test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, file));
        });
        test.Settle(0.3f);
        Capture("public-consent.png");
        Click("link.public_map_manual");
        test.Check("link-public: manual forwarding leaves the public listener active", () =>
            linkTransport is { Listening: true, AllowPublic: true } && !settings.LinkMapPublicPort && router.Requests == 0);
        Click("link.public_map");
        Click("link.public_map_allow");
        test.WaitFor(() => linkPublicStatus?.Text.Contains("49123", StringComparison.Ordinal) == true, 5);
        test.Check("link-public: consent displays the assigned external port", () =>
            settings.LinkMapPublicPort && LinkMappedPort == 49123 && router.Requests == 1);
        test.Settle(0.4f);
        Capture("public-mapped.png");
        Click("link.public_switch");
        test.WaitFor(() => router.Releases == 1, 5);
        test.Check("link-public: disabling public connections releases the lease and clears consent", () =>
            linkTransport?.AllowPublic == false && !settings.LinkMapPublicPort && LinkMappedPort is null && router.Releases == 1);
        test.Run(() =>
        {
            linkThisDeviceDialog?.Close();
            linkWindow?.Close();
            StopLink();
        });
    }
}
