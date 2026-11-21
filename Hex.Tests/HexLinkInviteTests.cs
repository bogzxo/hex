using System.Net;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkInviteTests : IDisposable
{
    private readonly HexLinkTestMachine host = new("Host");
    private readonly HexLinkTestMachine joiner = new("Joiner");

    [Fact]
    public void Tokens_preserve_the_pin_port_and_private_routes()
    {
        HexLinkInvite invite = HexLinkInvite.Create(host.Device.Id, 47833, DateTimeOffset.UtcNow.AddMinutes(5),
            [IPAddress.Parse("100.101.2.3"), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fe80::1%1")])!;
        Assert.True(HexLinkInvite.TryParse(invite.Encode(), out HexLinkInvite? read));
        Assert.NotNull(read);
        Assert.Equal(host.Device.Id, read.Pin);
        Assert.Equal(47833, read.Port);
        Assert.Equal(["192.168.1.5", "100.101.2.3"], read.Addresses.Select(address => address.ToString()));
        Assert.False(HexLinkInvite.TryParse(invite.Encode()[..^4], out _));
    }

    [Fact]
    public async Task An_invitation_races_routes_and_can_only_be_confirmed_once()
    {
        host.Listen();
        Assert.True(host.Transport.OpenInvite([IPAddress.Parse("127.0.0.2"), IPAddress.Loopback], HexLinkTestMachine.WAIT, shown =>
        {
            Assert.True(shown.Invited);
            Assert.Empty(shown.Code);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.View);
        }));
        Assert.True(HexLinkInvite.TryParse(host.Transport.ActiveInvite!.Encode(), out HexLinkInvite? read));
        HexLinkPairResult paired = await Pair(read!, shown =>
        {
            Assert.True(shown.Invited);
            Assert.Equal(host.Device.Id, shown.Other.Id);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.Predict);
        });
        Assert.True(paired.Paired, paired.Reason);
        Assert.Equal(HexLinkRights.Predict, joiner.Trust.Find(host.Device.Id)!.Rights);
        Assert.Equal(HexLinkRights.View, host.Trust.Find(joiner.Device.Id)!.Rights);
        Assert.NotNull(paired.Connection);
        Assert.Null(host.Transport.ActiveInvite);
        Assert.Equal("invite_expired", (await Pair(read!)).Reason);
    }

    [Fact]
    public async Task Five_bad_proofs_invalidate_the_invitation_without_asking()
    {
        int port = host.Listen();
        int asked = 0;
        Assert.True(host.Transport.OpenInvite([IPAddress.Loopback], HexLinkTestMachine.WAIT, _ =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.View);
        }));
        HexLinkInvite stale = host.Transport.ActiveInvite!;
        HexLinkInvite wrong = HexLinkInvite.Create(host.Device.Id, port, stale.Until, [IPAddress.Loopback])!;
        for (int attempt = 0; attempt < 5; attempt++)
            Assert.Equal("invite_invalid", (await Pair(wrong)).Reason);
        Assert.Equal("invite_expired", (await Pair(stale)).Reason);
        Assert.Equal(0, asked);
        Assert.Empty(host.Trust.Known);
        Assert.Empty(joiner.Trust.Known);
    }

    [Fact]
    public async Task Expired_and_wrong_pin_tokens_never_reach_approval()
    {
        int port = host.Listen();
        HexLinkInvite expired = HexLinkInvite.Create(host.Device.Id, port, DateTimeOffset.UtcNow.AddSeconds(-1), [IPAddress.Loopback])!;
        Assert.Equal("invite_expired", (await Pair(expired)).Reason);
        HexLinkInvite wrongPin = HexLinkInvite.Create(joiner.Device.Id, port, DateTimeOffset.UtcNow.AddMinutes(5), [IPAddress.Loopback])!;
        Assert.Equal("pin", (await Pair(wrongPin)).Reason);
        Assert.Empty(joiner.Trust.Known);
    }

    [Fact]
    public async Task Overasking_terms_are_masked_and_never_grant_rights_before_approval()
    {
        Assert.True(HexLinkInvite.TryTerms("terms 255 255 255", out HexLinkPairingTerms? decoded));
        Assert.NotNull(decoded);
        HexLinkRights supported = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;
        Assert.Equal(new HexLinkPairingTerms(HexLinkPreset.Custom, supported, supported), decoded);
        Assert.False(HexLinkInvite.TryTerms("terms 0 1 " + new string('0', 40), out _));
        Assert.False(HexLinkInvite.TryTerms("terms 0 seven 2", out _));

        host.Listen();
        var proposals = new HexLinkPairingTerms((HexLinkPreset)255, (HexLinkRights)255, (HexLinkRights)255);
        Assert.True(host.Transport.OpenInvite([IPAddress.Loopback], HexLinkTestMachine.WAIT,
            _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), () => proposals));
        var shown = new TaskCompletionSource<HexLinkPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<HexLinkRights?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HexLinkPairResult> pairing = Pair(host.Transport.ActiveInvite!, asking =>
        {
            shown.TrySetResult(asking);
            return decision.Task;
        });

        HexLinkPairing offer = await shown.Task.WaitAsync(HexLinkTestMachine.WAIT);
        Assert.Equal(decoded, offer.Terms);
        Assert.Empty(joiner.Trust.Known);
        Assert.Empty(host.Trust.Known);
        decision.SetResult(HexLinkRights.View);
        HexLinkPairResult result = await pairing;
        Assert.True(result.Paired, result.Reason);
        Assert.Equal(HexLinkRights.View, result.Granted);
        Assert.Equal(HexLinkRights.View, joiner.Trust.Find(host.Device.Id)!.Rights);
    }

    [Fact]
    public async Task A_viewer_can_confirm_without_granting_the_host_rights()
    {
        host.Listen();
        Assert.True(host.Transport.OpenInvite([IPAddress.Loopback], HexLinkTestMachine.WAIT,
            _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View),
            () => new HexLinkPairingTerms(HexLinkPreset.Viewer, HexLinkRights.View, HexLinkRights.None)));
        HexLinkPairResult result = await Pair(host.Transport.ActiveInvite!, asking =>
        {
            Assert.Equal(HexLinkPreset.Viewer, asking.Terms?.Preset);
            Assert.Equal(HexLinkRights.None, asking.Terms?.HostRequests);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.None);
        });
        Assert.True(result.Paired, result.Reason);
        Assert.Equal(HexLinkRights.None, joiner.Trust.Find(host.Device.Id)!.Rights);
        Assert.Equal(HexLinkRights.View, host.Trust.Find(joiner.Device.Id)!.Rights);
    }

    private async Task<HexLinkPairResult> Pair(HexLinkInvite invite, Func<HexLinkPairing, Task<HexLinkRights?>>? ask = null)
    {
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        return await joiner.Transport.Pair(string.Empty, invite.Port, ask ?? (_ => Task.FromResult<HexLinkRights?>(null)), cancel.Token, invite);
    }

    public void Dispose()
    {
        joiner.Dispose();
        host.Dispose();
    }
}
