using Hex.Core;

namespace Hex.Tests;

/// <summary>A link cut by hand says so before it closes, so the other end holds off dialling it again.</summary>
public sealed class HexLinkByeTests
{
    [Fact]
    public async Task A_link_removed_with_bye_tells_the_other_end_and_a_plain_removal_does_not()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View);
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.LinkB.ByeHeard = device => heard.TrySetResult(device.Id);

        Assert.True(pair.LinkA.Remove(HexLinkTestKit.DeviceB.Id, "cut by hand", bye: true));
        Assert.Equal(HexLinkTestKit.DeviceA.Id, await heard.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_link_removed_without_bye_is_not_a_hand_cut_to_the_other_end()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View);
        bool heard = false;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.LinkB.ByeHeard = _ => heard = true;
        pair.LinkB.Closed = (_, _) => closed.TrySetResult();

        Assert.True(pair.LinkA.Remove(HexLinkTestKit.DeviceB.Id, "dropped"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(heard);
    }
}
