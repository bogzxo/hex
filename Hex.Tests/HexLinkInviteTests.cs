using System.Buffers.Binary;
using System.Buffers.Text;
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
        Assert.StartsWith("hex-link:v1.", invite.Encode());
        Assert.True(HexLinkInvite.TryParse(invite.Encode(), out HexLinkInvite? read));
        Assert.NotNull(read);
        Assert.Null(read.PublicRoute);
        Assert.Equal(invite.Encode(), read.Encode());
        Assert.Equal(host.Device.Id, read.Pin);
        Assert.Equal(47833, read.Port);
        Assert.Equal(["192.168.1.5", "100.101.2.3"], read.Addresses.Select(address => address.ToString()));
        Assert.False(HexLinkInvite.TryParse(invite.Encode()[..^4], out _));
    }

    [Theory]
    [InlineData("office-fixture.duckdns.org", "office-fixture.duckdns.org:443")]
    [InlineData("203.0.113.8", "203.0.113.8:443")]
    [InlineData("2001:db8::8", "[2001:db8::8]:443")]
    public void Public_tokens_round_trip_the_external_route_alongside_private_addresses(string hostName, string shown)
    {
        Assert.True(HexLinkInviteRoute.TryCreate(hostName, 443, out var route));
        HexLinkInvite token = HexLinkInvite.Create(host.Device.Id, 47833, DateTimeOffset.UtcNow.AddMinutes(5),
            [IPAddress.Parse("192.168.1.5")], route)!;

        Assert.StartsWith("hex-link:v2.", token.Encode());
        Assert.True(HexLinkInvite.TryParse(token.Encode(), out var read));
        Assert.NotNull(read);
        Assert.Equal(host.Device.Id, read.Pin);
        Assert.Equal(47833, read.Port);
        Assert.Equal("192.168.1.5", Assert.Single(read.Addresses).ToString());
        Assert.Equal(shown, read.PublicRoute?.ToString());
        Assert.Equal(token.Encode(), read.Encode());
    }

    [Theory]
    [InlineData("https://office-fixture.duckdns.org", 443)]
    [InlineData("office-fixture.duckdns.org/location", 443)]
    [InlineData("office-fixture.duckdns.org@elsewhere", 443)]
    [InlineData("office fixture.duckdns.org", 443)]
    [InlineData("double..dot.example", 443)]
    [InlineData("fe80::1%3", 443)]
    [InlineData("office-fixture.duckdns.org", 0)]
    [InlineData("office-fixture.duckdns.org", 65536)]
    public void A_route_requires_a_host_and_valid_port(string hostName, int port) =>
        Assert.False(HexLinkInviteRoute.TryCreate(hostName, port, out _));

    [Fact]
    public void A_public_token_bounds_its_route_and_rejects_invalid_payloads()
    {
        string maxHost = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        Assert.False(HexLinkInviteRoute.TryCreate(maxHost + "x", 443, out _));
        Assert.True(HexLinkInviteRoute.TryCreate(maxHost, 443, out var route));
        IPAddress[] lan = [.. Enumerable.Range(1, 8).Select(i => IPAddress.Parse($"fd00::{i}"))];
        HexLinkInvite token = HexLinkInvite.Create(host.Device.Id, 47833, DateTimeOffset.UtcNow.AddMinutes(5), lan, route)!;
        string encoded = token.Encode();
        Assert.Equal(HexLinkInvite.MaxTokenLength, encoded.Length);
        Assert.True(HexLinkInvite.TryParse(encoded, out _));
        Assert.False(HexLinkInvite.TryParse(encoded[..^4], out _));
        Assert.False(HexLinkInvite.TryParse(encoded + "AAAA", out _));

        byte[] payload = Base64Url.DecodeFromChars(encoded.AsSpan("hex-link:v2.".Length));
        payload[^1] = (byte)'/';
        Assert.False(HexLinkInvite.TryParse("hex-link:v2." + Base64Url.EncodeToString(payload), out _));
    }

    [Fact]
    public async Task A_receiver_with_public_disabled_uses_only_the_private_token_routes()
    {
        int port = host.Listen();
        Assert.True(HexLinkInviteRoute.TryCreate("localhost", port, out var route));
        Assert.False(host.Transport.OpenInvite([], HexLinkTestMachine.WAIT, _ => Task.FromResult<HexLinkRights?>(null), publicRoute: route));
        host.Transport.AllowPublic = true;
        Assert.True(host.Transport.OpenInvite([], HexLinkTestMachine.WAIT,
            _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), publicRoute: route));
        var publicOnly = host.Transport.ActiveInvite!;
        Assert.Equal("public_disabled", (await Pair(publicOnly)).Reason);
        Assert.Same(publicOnly, host.Transport.ActiveInvite);
        Assert.Empty(joiner.Trust.Known);

        Assert.True(HexLinkInviteRoute.TryCreate("203.0.113.8", 443, out var publicOverride));
        Assert.Equal("public_disabled", (await Pair(publicOnly, overrideRoute: publicOverride)).Reason);
        Assert.True(HexLinkInviteRoute.TryCreate("office-fixture.invalid", 443, out var unused));
        Assert.True(host.Transport.OpenInvite([IPAddress.Loopback], HexLinkTestMachine.WAIT,
            _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), publicRoute: unused));
        HexLinkPairResult paired = await Pair(host.Transport.ActiveInvite!, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.Predict));
        Assert.True(paired.Paired, paired.Reason);
        Assert.Equal(port, joiner.Trust.Find(host.Device.Id)?.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_route_uses_the_invite_pin_proof_and_grants_and_remembers_the_dial_address(bool tokenOverride)
    {
        int port = host.Listen();
        Assert.True(HexLinkInviteRoute.TryCreate("localhost", port, out var route));
        host.Transport.AllowPublic = true;
        joiner.Transport.AllowPublic = !tokenOverride;
        Assert.True(host.Transport.OpenInvite(tokenOverride ? [IPAddress.Loopback] : [], HexLinkTestMachine.WAIT, shown =>
        {
            Assert.True(shown.Invited);
            Assert.Empty(shown.Code);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.View);
        }, publicRoute: tokenOverride ? null : route));
        HexLinkInvite actual = host.Transport.ActiveInvite!;

        if (tokenOverride)
        {
            HexLinkInvite wrongPin = HexLinkInvite.Create(joiner.Device.Id, port, actual.Until, [IPAddress.Loopback])!;
            Assert.Equal("pin", (await Pair(wrongPin, overrideRoute: route)).Reason);
            HexLinkInvite wrongSecret = HexLinkInvite.Create(host.Device.Id, port, actual.Until, [IPAddress.Loopback])!;
            Assert.Equal("invite_invalid", (await Pair(wrongSecret, overrideRoute: route)).Reason);
            Assert.Empty(host.Trust.Known);
        }

        string encoded = actual.Encode();
        int prefixLength = "hex-link:v1.".Length;
        byte[] payload = Base64Url.DecodeFromChars(encoded.AsSpan(prefixLength));
        int otherPort = port == 65535 ? port - 1 : port + 1;
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)otherPort);
        Assert.True(HexLinkInvite.TryParse(encoded[..prefixLength] + Base64Url.EncodeToString(payload), out var read));
        Assert.NotNull(read);
        Assert.Equal(otherPort, read.Port);
        HexLinkPairResult paired = await Pair(read, shown =>
        {
            Assert.True(shown.Invited);
            Assert.Equal(host.Device.Id, shown.Other.Id);
            return Task.FromResult<HexLinkRights?>(HexLinkRights.Predict);
        }, tokenOverride ? route : null);
        Assert.True(paired.Paired, paired.Reason);
        Assert.NotNull(paired.Connection);
        Assert.Equal(IPAddress.Loopback.ToString(), paired.Connection.Address);
        Assert.Equal("localhost", joiner.Trust.Find(host.Device.Id)?.Address);
        Assert.Equal(port, joiner.Trust.Find(host.Device.Id)?.Port);
        Assert.Equal(HexLinkRights.Predict, joiner.Trust.Find(host.Device.Id)?.Rights);
        Assert.Equal(HexLinkRights.View, host.Trust.Find(joiner.Device.Id)?.Rights);
        Assert.Null(host.Transport.ActiveInvite);
    }

    [Fact]
    public async Task An_override_does_not_fall_back_to_a_working_invitation_route()
    {
        int port = host.Listen();
        int notTheInvite = joiner.Listen();
        Assert.True(HexLinkInviteRoute.TryCreate("127.0.0.1", notTheInvite, out var route));
        Assert.True(host.Transport.OpenInvite([IPAddress.Loopback], HexLinkTestMachine.WAIT,
            _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View)));
        var token = host.Transport.ActiveInvite!;
        Assert.Equal(port, token.Port);
        Assert.Equal("pin", (await Pair(token, overrideRoute: route)).Reason);
        Assert.Same(token, host.Transport.ActiveInvite);
    }

    [Fact]
    public async Task A_vpn_winner_gives_pending_lan_routes_a_chance_to_finish()
    {
        var lan = new TaskCompletionSource<string>();
        var grace = new TaskCompletionSource();
        Task<string> unreachable = Task.FromCanceled<string>(new CancellationToken(true));

        Task<Task<string>?> choice = HexLinkTransport.FindInviteLan([unreachable, lan.Task], grace.Task);
        Assert.False(choice.IsCompleted);
        lan.SetResult("LAN");

        Assert.Same(lan.Task, await choice);
    }

    [Fact]
    public async Task A_vpn_winner_can_proceed_when_lan_routes_fail_or_grace_expires()
    {
        var lan = new TaskCompletionSource<string>();
        var grace = new TaskCompletionSource();
        Task<string> unreachable = Task.FromCanceled<string>(new CancellationToken(true));

        Assert.Null(await HexLinkTransport.FindInviteLan([unreachable], grace.Task));
        Task<Task<string>?> choice = HexLinkTransport.FindInviteLan([lan.Task], grace.Task);
        grace.SetResult();

        Assert.Null(await choice);
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
        Assert.Equal(HexLinkBulkSupport.Zstandard, paired.Connection.Bulk);
        Assert.Equal(HexLinkBulkSupport.Zstandard, (await host.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT)).Bulk);
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

    private async Task<HexLinkPairResult> Pair(HexLinkInvite invite, Func<HexLinkPairing, Task<HexLinkRights?>>? ask = null,
        HexLinkInviteRoute? overrideRoute = null)
    {
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        return await joiner.Transport.Pair(string.Empty, invite.Port, ask ?? (_ => Task.FromResult<HexLinkRights?>(null)),
            cancel.Token, invite, overrideRoute);
    }

    public void Dispose()
    {
        joiner.Dispose();
        host.Dispose();
    }
}
