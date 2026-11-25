namespace Hex.Core;

/// <summary>
/// What a device card's one Pause / Resume button does: pausing stops the syncs that run with the device, resuming starts
/// the folders that were paused. Kept apart from the window so what it picks can be tested.
/// </summary>
internal sealed record HexLinkDevicePause(IReadOnlyList<string> Running, IReadOnlyList<string> Paused)
{
    /// <summary>Pause is offered: something syncs with the device.</summary>
    public bool CanPause => Running.Count > 0;

    /// <summary>Resume is offered: nothing runs and something was paused.</summary>
    public bool CanResume => Running.Count == 0 && Paused.Count > 0;

    /// <summary>The card says Paused: every folder that was synced is stopped.</summary>
    public bool IsPaused => CanResume;

    /// <summary>The folders the button acts on.</summary>
    public IReadOnlyList<string> Targets => CanPause ? Running : CanResume ? Paused : [];

    public static HexLinkDevicePause Of(IEnumerable<string> running, IEnumerable<string> paused)
    {
        string[] now = [.. running.Distinct(PlatformPaths.Comparer)];
        string[] stopped = [.. paused.Distinct(PlatformPaths.Comparer).Where(folder => !now.Contains(folder, PlatformPaths.Comparer))];
        return new HexLinkDevicePause(now, stopped);
    }
}
