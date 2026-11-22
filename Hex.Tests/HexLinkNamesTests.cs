using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// What devices are called: names cleaned as the other machine's data they are, a name the person gives a device here
/// winning over what it calls itself and kept across a restart and its own renames, and a device renaming itself
/// while linked heard by the other at once.
/// </summary>
public sealed class HexLinkNamesTests
{
    [Theory]
    [InlineData("  Studio Mac  ", "Studio Mac")]
    [InlineData("Bad‮evil‬ name", "Badevil name")]
    [InlineData("Line\nbreak\tand\u0007bell", "Line break andbell")]
    [InlineData("zero​width", "zerowidth")]
    public void A_name_is_trimmed_and_loses_what_cant_be_seen(string said, string shown) =>
        Assert.Equal(shown, HexLinkNames.Clean(said));

    [Fact]
    public void A_name_is_forty_characters_at_most_and_nothing_left_is_no_name()
    {
        Assert.Equal(HexLinkNames.MOST, HexLinkNames.Clean(new string('x', 200))!.Length);
        Assert.Null(HexLinkNames.Clean(" ​\u0001 "));
        Assert.Null(HexLinkNames.Clean(null));
    }

    [Fact]
    public void A_name_given_here_wins_over_the_devices_own_and_outlives_its_renames_and_a_restart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-link-names-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var trust = new HexLinkTrust(folder);
            trust.Add(DeviceB, HexLinkRights.View);
            Assert.True(trust.Rename(DeviceB.Id, "  The build box "));
            trust.Seen(DeviceB.Id, DeviceB with { Name = "Renamed itself" }, "10.0.0.2");
            HexLinkKnown known = trust.Find(DeviceB.Id)!;
            Assert.Equal(("The build box", "Renamed itself"), (known.Shown, known.Device.Name));

            // Read back from its file, then let go of
            known = new HexLinkTrust(folder).Find(DeviceB.Id)!;
            Assert.Equal("The build box", known.Shown);
            Assert.True(trust.Rename(DeviceB.Id, null));
            Assert.Equal("Renamed itself", new HexLinkTrust(folder).Find(DeviceB.Id)!.Shown);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_device_renaming_itself_is_heard_by_the_other_at_once_cleaned()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        HexLinkDevice? renamed = null;
        int changed = 0;
        pair.PeerB.Renamed = device => renamed = device;
        pair.PeerB.Changed += () => Interlocked.Increment(ref changed);

        // B (whose view of A is PeerA) tells A it's called something else now
        pair.PeerA.SayName(" B's new‮ name ");
        await WaitUntil(() => renamed is not null);
        Assert.Equal("B's new name", pair.PeerB.Device.Name);
        Assert.Equal(DeviceB.Id, renamed!.Id);
        Assert.True(changed > 0);
    }

    private static async Task WaitUntil(Func<bool> until)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && clock.ElapsedMilliseconds < WAIT_MS)
            await Task.Delay(10);
        Assert.True(until());
    }
}
