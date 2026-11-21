using System.Security.Cryptography;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Pairing over loopback: the same code on both sides and each stored by the other with what it was granted, a decline
/// on either side storing nothing on both, a joiner that breaks its commitment refused, one try a window, and the
/// code's arithmetic on its own.
/// </summary>
public sealed class HexLinkPairingTests : IDisposable
{
    private readonly HexLinkTestMachine host = new("Host");
    private readonly HexLinkTestMachine joiner = new("Joiner");

    public void Dispose()
    {
        host.Dispose();
        joiner.Dispose();
    }

    private static Func<HexLinkPairing, Task<HexLinkRights?>> Answer(HexLinkRights? granted, List<HexLinkPairing> shown) => pairing =>
    {
        lock (shown)
            shown.Add(pairing);
        return Task.FromResult(granted);
    };

    private async Task<(HexLinkPairResult Joined, HexLinkPairResult Hosted)> Pair(HexLinkRights? hostGrants, HexLinkRights? joinerGrants, List<HexLinkPairing> hostShown, List<HexLinkPairing> joinerShown)
    {
        var hosted = new TaskCompletionSource<HexLinkPairResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Transport.Paired = result => hosted.TrySetResult(result);
        int port = host.Listen();
        host.Transport.OpenPairing(TimeSpan.FromMinutes(5), Answer(hostGrants, hostShown));
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkPairResult joined = await joiner.Transport.Pair("127.0.0.1", port, Answer(joinerGrants, joinerShown), cancel.Token);
        return (joined, await hosted.Task.WaitAsync(HexLinkTestMachine.WAIT));
    }

    [Fact]
    public async Task Pairing_shows_both_the_same_code_and_each_stores_the_other_with_what_it_granted()
    {
        List<HexLinkPairing> hostShown = [], joinerShown = [];
        (HexLinkPairResult joined, HexLinkPairResult hosted) = await Pair(HexLinkRights.View, HexLinkRights.View | HexLinkRights.Steer, hostShown, joinerShown);

        Assert.True(joined.Paired, joined.Reason);
        Assert.True(hosted.Paired, hosted.Reason);
        HexLinkPairing hostSaw = Assert.Single(hostShown), joinerSaw = Assert.Single(joinerShown);
        Assert.Matches("^[0-9]{6}$", hostSaw.Code);
        Assert.Equal(hostSaw.Code, joinerSaw.Code);
        Assert.Equal(joiner.Device.Id, hostSaw.Other.Id);
        Assert.Equal("Joiner", hostSaw.Other.Name);
        Assert.Equal(host.Device.Id, joinerSaw.Other.Id);
        Assert.Equal(host.Device.Fingerprint, joinerSaw.Other.Fingerprint);

        Assert.Equal(HexLinkRights.View, host.Trust.Find(joiner.Device.Id)!.Rights);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, joiner.Trust.Find(host.Device.Id)!.Rights);
        Assert.Equal(host.Transport.Port, joiner.Trust.Find(host.Device.Id)!.Port);

        // The pairing carries on as a link, and the window is closed
        Assert.NotNull(joined.Connection);
        HexLinkConnection accepted = await host.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        await HexLinkTestMachine.WriteLine(joined.Connection.Stream, "after pairing");
        Assert.Equal("after pairing", await HexLinkTestMachine.ReadLine(accepted.Stream));
        Assert.Equal(HexLinkRights.View, accepted.Rights);
        Assert.False(host.Transport.PairingOpen);
        joined.Connection.Dispose();

        // And the two link again later by their pins alone
        accepted.Dispose();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkConnection again = await RetryDial(joiner.Transport, host.Transport.Port, host.Device.Id, cancel.Token);
        Assert.Equal(host.Device.Id, again.Device.Id);
        again.Dispose();
    }

    private static async Task<HexLinkConnection> RetryDial(HexLinkTransport transport, int port, string id, CancellationToken cancel)
    {
        while (true)
        {
            try
            {
                return await transport.Dial("127.0.0.1", port, id, cancel);
            }
            catch (HexLinkRefused e) when (e.Reason == "busy" && !cancel.IsCancellationRequested)
            {
                await Task.Delay(50, cancel);
            }
        }
    }

    [Fact]
    public async Task The_host_declining_stores_nothing_on_either_side()
    {
        (HexLinkPairResult joined, HexLinkPairResult hosted) = await Pair(null, HexLinkRights.View, [], []);
        Assert.False(joined.Paired);
        Assert.Equal("declined-there", joined.Reason);
        Assert.False(hosted.Paired);
        Assert.Equal("declined-here", hosted.Reason);
        Assert.Empty(host.Trust.Known);
        Assert.Empty(joiner.Trust.Known);
        Assert.False(host.Accepted.Task.IsCompleted);
    }

    [Fact]
    public async Task The_joiner_declining_stores_nothing_on_either_side()
    {
        (HexLinkPairResult joined, HexLinkPairResult hosted) = await Pair(HexLinkRights.View, null, [], []);
        Assert.False(joined.Paired);
        Assert.Equal("declined-here", joined.Reason);
        Assert.False(hosted.Paired);
        Assert.Equal("declined-there", hosted.Reason);
        Assert.Empty(host.Trust.Known);
        Assert.Empty(joiner.Trust.Known);
    }

    [Fact]
    public async Task A_decline_over_there_ends_the_question_here()
    {
        // The host's person never answers, the joiner's declines, the host's question is told it's over
        var hosted = new TaskCompletionSource<HexLinkPairResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Transport.Paired = result => hosted.TrySetResult(result);
        int port = host.Listen();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Transport.OpenPairing(TimeSpan.FromMinutes(5), async pairing =>
        {
            pairing.Ended.Register(() => ended.TrySetResult());
            await Task.Delay(Timeout.Infinite, pairing.Ended);
            return HexLinkRights.View;
        });

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkPairResult joined = await joiner.Transport.Pair("127.0.0.1", port, _ => Task.FromResult<HexLinkRights?>(null), cancel.Token);
        Assert.False(joined.Paired);
        await ended.Task.WaitAsync(HexLinkTestMachine.WAIT);
        Assert.Equal("declined-there", (await hosted.Task.WaitAsync(HexLinkTestMachine.WAIT)).Reason);
        Assert.Empty(host.Trust.Known);
    }

    [Fact]
    public async Task One_try_a_window_a_failed_pairing_closes_it()
    {
        (HexLinkPairResult joined, _) = await Pair(null, HexLinkRights.View, [], []);
        Assert.False(joined.Paired);
        Assert.False(host.Transport.PairingOpen);

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkPairResult again = await joiner.Transport.Pair("127.0.0.1", host.Transport.Port, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), cancel.Token);
        Assert.False(again.Paired);
        Assert.Equal("closed", again.Reason);
    }

    [Fact]
    public async Task Pairing_without_the_window_open_is_refused()
    {
        int port = host.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkPairResult joined = await joiner.Transport.Pair("127.0.0.1", port, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), cancel.Token);
        Assert.False(joined.Paired);
        Assert.Equal("closed", joined.Reason);
        Assert.Null(joined.Device);
    }

    [Fact]
    public async Task A_window_runs_out()
    {
        host.Listen();
        host.Transport.OpenPairing(TimeSpan.FromMilliseconds(200), _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View));
        Assert.True(host.Transport.PairingOpen);
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        while (host.Transport.PairingOpen)
            await Task.Delay(50, cancel.Token);
        Assert.Null(host.Transport.PairingUntil);
    }

    [Fact]
    public async Task A_joiner_whose_nonce_isnt_the_one_it_committed_to_is_refused()
    {
        var asked = false;
        var hosted = new TaskCompletionSource<HexLinkPairResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Transport.Paired = result => hosted.TrySetResult(result);
        int port = host.Listen();
        host.Transport.OpenPairing(TimeSpan.FromMinutes(5), _ =>
        {
            asked = true;
            return Task.FromResult<HexLinkRights?>(HexLinkRights.View);
        });

        using var ssl = await HexLinkTestMachine.RawClient(port, joiner.Identity.Certificate);
        Assert.NotNull(ssl);
        await HexLinkTestMachine.WriteLine(ssl, "HEX-LINK 1 pair {\"name\":\"Cheat\"}");
        Assert.StartsWith("HEX-LINK 1 pair ", await HexLinkTestMachine.ReadLine(ssl));
        byte[] committed = RandomNumberGenerator.GetBytes(32), revealed = RandomNumberGenerator.GetBytes(32);
        await HexLinkTestMachine.WriteLine(ssl, "commit " + HexLinkSas.Hex(HexLinkSas.CommitmentOf(committed)));
        Assert.StartsWith("nonce ", await HexLinkTestMachine.ReadLine(ssl));
        await HexLinkTestMachine.WriteLine(ssl, "reveal " + HexLinkSas.Hex(revealed));
        Assert.Equal("NO mismatch\n", await HexLinkTestMachine.ReadToEnd(ssl));

        HexLinkPairResult result = await hosted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        Assert.False(result.Paired);
        Assert.Equal("mismatch", result.Reason);
        Assert.False(asked);
        Assert.Empty(host.Trust.Known);
        Assert.False(host.Transport.PairingOpen);
    }

    [Fact]
    public void The_code_is_six_digits_of_both_pins_and_both_nonces()
    {
        string hostPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), joinerPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] hostNonce = RandomNumberGenerator.GetBytes(32), joinerNonce = RandomNumberGenerator.GetBytes(32);
        string code = HexLinkSas.CodeOf(hostPin, joinerPin, hostNonce, joinerNonce);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(code, HexLinkSas.CodeOf(hostPin, joinerPin, hostNonce, joinerNonce));

        // Anything changed changes it; with a million codes a chance match is one in a million, so a few tries are enough to tell
        byte[] tampered = (byte[])joinerNonce.Clone();
        tampered[0] ^= 1;
        Assert.NotEqual(code, HexLinkSas.CodeOf(hostPin, joinerPin, hostNonce, tampered));
        Assert.NotEqual(code, HexLinkSas.CodeOf(hostPin, joinerPin, RandomNumberGenerator.GetBytes(32), joinerNonce));
        string otherKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Assert.NotEqual(code, HexLinkSas.CodeOf(otherKey, joinerPin, hostNonce, joinerNonce));
        Assert.NotEqual(code, HexLinkSas.CodeOf(hostPin, otherKey, hostNonce, joinerNonce));
        Assert.NotEqual(code, HexLinkSas.CodeOf(joinerPin, hostPin, hostNonce, joinerNonce));
    }

    [Fact]
    public void A_commitment_keeps_its_nonce_only()
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] commitment = HexLinkSas.CommitmentOf(nonce);
        Assert.True(HexLinkSas.Keeps(commitment, nonce));
        byte[] other = (byte[])nonce.Clone();
        other[31] ^= 0x80;
        Assert.False(HexLinkSas.Keeps(commitment, other));
        Assert.False(HexLinkSas.Keeps([1, 2, 3], nonce));
        Assert.Null(HexLinkSas.BytesAfter("commit 00", "commit"));
        Assert.Null(HexLinkSas.BytesAfter("reveal " + HexLinkSas.Hex(nonce), "commit"));
        Assert.Equal(nonce, HexLinkSas.BytesAfter("reveal " + HexLinkSas.Hex(nonce), "reveal"));
    }
}
