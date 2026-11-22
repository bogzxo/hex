using Horizon.Core.Threading;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkWorkspaceOffer(HexLinkDevice Device, string Name)
    {
        public bool Presented { get; set; }
    }

    private readonly Dictionary<string, LinkWorkspaceOffer> linkWorkspaceOffers = new(StringComparer.OrdinalIgnoreCase);

    private void QueueLinkWorkspaceOffer(HexLinkDevice device, string workspace)
    {
        string? name = HexLinkNames.Clean(workspace);
        if (string.IsNullOrWhiteSpace(name))
            return;

        PostLink(() =>
        {
            if (linkTrust?.Find(device.Id) is null || linkWorkspaceOffers.GetValueOrDefault(device.Id)?.Name == name)
                return;
            // The authenticated request can arrive before our hello reply has filled in the peer's grants.
            linkWorkspaceOffers[device.Id] = new LinkWorkspaceOffer(device, name);
            linkWindowStale = true;
        });
    }

    private static bool CanGetLinkOffer(HexLinkPeer? peer) =>
        peer is { IsClosed: false } && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View);

    private void TickLinkWorkspaceOffers()
    {
        if (linkWorkspaceOffers.Count == 0 || chrome.Root.Children.OfType<UIDialog>().Any(dialog => dialog.IsOpen))
            return;

        foreach (LinkWorkspaceOffer offer in linkWorkspaceOffers.Values)
        {
            if (offer.Presented || !CanGetLinkOffer(LinkPeerOf(offer.Device.Id)))
                continue;

            offer.Presented = true;
            UIDialog.Show(chrome, UIRichText.Literal(LinkWorkspaceOfferTitle(offer)), Said("link.devices_workspace_offer_details"),
                new DialogChoice(Said("link.devices_workspace_offer_get"), () => GetLinkOfferedWorkspace(offer)),
                new DialogChoice(Said("link.devices_workspace_offer_later")));
            break;
        }
    }

    private string LinkWorkspaceOfferTitle(LinkWorkspaceOffer offer) => Said("link.devices_workspace_offer",
        ("device", LinkNameOf(offer.Device.Id, offer.Device.Name)), ("workspace", offer.Name));

    private void LinkGetWorkspaceInto(string deviceId, string deviceName, string destination, Action? received = null)
    {
        if (destination.Length == 0)
            return;
        HexLinkProvisionResult? download = null;
        Context.Work.Start($"link-provision-{deviceId}", Said("link.provision.get_title", ("device", deviceName)),
            async work =>
            {
                if (await LinkGetWorkspace(deviceId, destination, new Progress<HexLinkProvisionProgress>(progress => ReportProvision(work, progress)),
                    work.Token, result => download = result) is { } why)
                    throw new InvalidOperationException(LinkProvisionWithSkips(why, download));
            },
            done =>
            {
                if (done.State == WorkState.Failed)
                    Say(Said("link.provision.failed", ("why", done.Error ?? string.Empty)), error: true);
                else if (done.State == WorkState.Succeeded)
                {
                    if (download is not null)
                        LinkProvisionCompleted(download);
                    received?.Invoke();
                }
            });
    }

    private void GetLinkOfferedWorkspace(LinkWorkspaceOffer offer)
    {
        string id = offer.Device.Id;
        if (!CanGetLinkOffer(LinkPeerOf(id)))
        {
            Say(Said("link.devices_workspace_offer_reconnect"), error: true);
            return;
        }

        Context.Windows.ChooseFolder(destination => LinkGetWorkspaceInto(id, offer.Device.Name, destination, () =>
        {
            if (ReferenceEquals(linkWorkspaceOffers.GetValueOrDefault(id), offer))
            {
                linkWorkspaceOffers.Remove(id);
                linkWindowStale = true;
            }
        }));
    }
}
