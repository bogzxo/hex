using Hex.Core;

namespace Hex.Tests;

/// <summary>The pure parts of the Link UI features: claim and hold marks, device pause, rights-change notices.</summary>
public sealed class HexLinkDeviceUiModelTests
{
    private static HexLinkClaim Claim(string deviceId, string name, string holder, HexLinkClaimKind kind, long since, int? chat = null) =>
        new(new HexLinkClaimScope("work", "scope"), new HexLinkDevice(deviceId, name),
            new HexLinkClaimDraft(new HexLinkClaimKey("a.txt", false, chat, kind), holder), since, since + 35_000, deviceId);

    [Fact]
    public void Marks_name_the_device_and_person_and_put_this_devices_own_first()
    {
        long now = 1_000_000;
        var marks = HexLinkFileMark.Of(
            [Claim("beta", "Beta", "bogdan", HexLinkClaimKind.Editing, now - 125_000), Claim("alpha", "Alpha", "me", HexLinkClaimKind.Editing, now - 5_000)],
            now, "alpha", [("beta", "Beta", null)]);

        Assert.Equal(3, marks.Count);
        Assert.True(marks[0].Own);
        Assert.Equal("alpha", marks[0].DeviceId);
        HexLinkFileMark edited = marks.Single(mark => mark.DeviceId == "beta" && mark.Kind == HexLinkMarkKind.Editing);
        Assert.Equal("bogdan", edited.Person);
        Assert.Equal(TimeSpan.FromSeconds(125), edited.Age);
        Assert.Equal("2m ago", HexLinkFileMark.AgeText(edited.Age!.Value));
        HexLinkFileMark held = marks.Single(mark => mark.Kind == HexLinkMarkKind.Held);
        Assert.Equal("Beta", held.DeviceName);
        Assert.Null(held.Age);
    }

    [Fact]
    public void A_device_with_two_claims_of_one_kind_is_one_mark_and_planned_and_private_claims_have_their_kinds()
    {
        long now = 50_000;
        var marks = HexLinkFileMark.Of(
            [Claim("beta", "Beta", "agent", HexLinkClaimKind.Planned, 10_000, 3), Claim("beta", "Beta", "agent 2", HexLinkClaimKind.Planned, 20_000, 4),
             Claim("beta", "Beta", "agent", HexLinkClaimKind.Private, 10_000, 3)], now, "alpha", []);
        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, mark => mark.Kind == HexLinkMarkKind.Planned && mark.ChatId == 3);
        Assert.Contains(marks, mark => mark.Kind == HexLinkMarkKind.Publishing);
        Assert.All(marks, mark => Assert.False(mark.Own));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(3_600, "1h ago")]
    [InlineData(90_000, "1d ago")]
    public void Ages_read_as_the_tooltip_says_them(int seconds, string said) =>
        Assert.Equal(said, HexLinkFileMark.AgeText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Pause_offers_pause_while_something_syncs_and_resume_when_everything_was_paused()
    {
        HexLinkDevicePause running = HexLinkDevicePause.Of(["work", "docs"], ["old"]);
        Assert.True(running.CanPause);
        Assert.False(running.CanResume);
        Assert.False(running.IsPaused);
        Assert.Equal(["work", "docs"], running.Targets);

        HexLinkDevicePause paused = HexLinkDevicePause.Of([], ["work", "docs", "work"]);
        Assert.False(paused.CanPause);
        Assert.True(paused.CanResume);
        Assert.True(paused.IsPaused);
        Assert.Equal(["work", "docs"], paused.Targets);

        HexLinkDevicePause nothing = HexLinkDevicePause.Of([], []);
        Assert.False(nothing.CanPause || nothing.CanResume || nothing.IsPaused);
        Assert.Empty(nothing.Targets);

        // A folder paused but already running again is no longer paused
        Assert.Empty(HexLinkDevicePause.Of(["work"], ["work"]).Paused);
    }

    [Fact]
    public void A_rights_change_says_what_was_lost_and_what_came()
    {
        HexLinkRightsChange lost = HexLinkRightsChange.Between(HexLinkRights.View | HexLinkRights.Edit, HexLinkRights.View);
        Assert.Equal(HexLinkRights.Edit, lost.Lost);
        Assert.Equal(HexLinkRights.None, lost.Gained);
        Assert.Equal([HexLinkRights.Edit], lost.Revoked);

        HexLinkRightsChange both = HexLinkRightsChange.Between(HexLinkRights.View | HexLinkRights.Edit, HexLinkRights.View | HexLinkRights.Steer);
        Assert.Equal([HexLinkRights.Edit], both.Revoked);
        Assert.Equal([HexLinkRights.Steer], both.Granted);

        Assert.False(HexLinkRightsChange.Between(HexLinkRights.View, HexLinkRights.View).Any);
    }
}
