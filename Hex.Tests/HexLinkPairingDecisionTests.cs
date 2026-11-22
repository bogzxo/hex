using System.Text;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkPairingDecisionTests
{
    [Theory]
    [InlineData("deny", true)]
    [InlineData("confirm", false)]
    [InlineData("", false)]
    public async Task Only_a_read_decline_can_replace_a_failed_confirm(string reply, bool declined)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var link = new DecisionStream(reply);
        var shown = new HexLinkPairing(HexLinkTestKit.DeviceB, "123456", "loopback", cancel.Token);
        Task<(string Verdict, HexLinkRights Granted)> result =
            HexLinkTransport.Decide(link, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), shown, cancel);

        if (declined)
            Assert.Equal(("declined-there", HexLinkRights.None), await result);
        else
            await Assert.ThrowsAsync<IOException>(() => result);
    }

    private sealed class DecisionStream(string reply) : MemoryStream(Encoding.UTF8.GetBytes(reply.Length == 0 ? "" : reply + "\n"))
    {
        private readonly TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            await sending.Task.WaitAsync(cancel);
            return await base.ReadAsync(buffer, cancel);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
        {
            sending.TrySetResult();
            return ValueTask.FromException(new IOException("The socket closed during confirm."));
        }
    }
}
