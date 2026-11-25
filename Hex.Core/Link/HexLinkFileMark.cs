namespace Hex.Core;

internal enum HexLinkMarkKind { Editing, Planned, Publishing, Held }

/// <summary>
/// Who holds or edits a file, one device's claim or hold of it: what the Explorer row marks in the device's colour and what its
/// tooltip and menu say. <see cref="Person"/> is who is at it on that device (the person's name, or the agent's chat) and
/// <see cref="Age"/> how long, null when it isn't known.
/// </summary>
internal sealed record HexLinkFileMark(HexLinkMarkKind Kind, string DeviceId, string DeviceName, string Person, bool Own, TimeSpan? Age, int? ChatId = null)
{
    /// <summary>The marks of the claims on a file and the holds of it, this device's own first. Claims of the same device and kind are one mark.</summary>
    public static IReadOnlyList<HexLinkFileMark> Of(IEnumerable<HexLinkClaim> claims, long nowTick, string localDeviceId,
        IEnumerable<(string DeviceId, string DeviceName, TimeSpan? Age)> holds)
    {
        var marks = new List<HexLinkFileMark>();
        foreach (HexLinkClaim claim in claims)
        {
            HexLinkMarkKind kind = claim.Kind switch
            {
                HexLinkClaimKind.Planned => HexLinkMarkKind.Planned,
                HexLinkClaimKind.Private => HexLinkMarkKind.Publishing,
                _ => HexLinkMarkKind.Editing
            };
            TimeSpan age = TimeSpan.FromMilliseconds(Math.Max(0, nowTick - claim.SinceTick));
            AddOnce(new HexLinkFileMark(kind, claim.DeviceId, claim.DeviceName, claim.Holder, claim.DeviceId.Equals(localDeviceId, StringComparison.OrdinalIgnoreCase), age, claim.ChatId));
        }

        foreach (var (deviceId, deviceName, age) in holds)
            AddOnce(new HexLinkFileMark(HexLinkMarkKind.Held, deviceId, deviceName, string.Empty, deviceId.Equals(localDeviceId, StringComparison.OrdinalIgnoreCase), age));
        return [.. marks.OrderByDescending(mark => mark.Own).ThenBy(mark => mark.Kind)];

        void AddOnce(HexLinkFileMark mark)
        {
            if (!marks.Any(each => each.Kind == mark.Kind && each.DeviceId.Equals(mark.DeviceId, StringComparison.OrdinalIgnoreCase)))
                marks.Add(mark);
        }
    }

    /// <summary>How long ago, as the tooltip says it: "just now", "5m ago", "2h ago", "3d ago".</summary>
    public static string AgeText(TimeSpan age) => age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago" :
        age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago" : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m ago" : "just now";
}
