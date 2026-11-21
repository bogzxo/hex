namespace Hex.Core;

/// <summary>
/// When a known device that isn't linked is dialled again. The first try is at once, a failed one waits five seconds,
/// then twice that each time up to a minute, so a machine that's asleep or gone home isn't hammered and one that wakes is
/// found within the minute. A device heard on the LAN again (mDNS) is tried at once whatever it was waiting for, and a
/// link made puts it back to the start. Only the editor's thread uses it.
/// </summary>
internal sealed class HexLinkRedial
{
    /// <summary>The wait after the first failure, and the longest a wait gets.</summary>
    public static readonly TimeSpan FIRST = TimeSpan.FromSeconds(5), MOST = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (DateTime Due, TimeSpan Gap)> devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a device is to be dialled now, true for one never tried.</summary>
    public bool Due(string id, DateTime now) => !devices.TryGetValue(id, out var state) || now >= state.Due;

    /// <summary>When a device is next due, null for at once.</summary>
    public DateTime? Next(string id) => devices.TryGetValue(id, out var state) ? state.Due : null;

    /// <summary>A dial that failed, the next one put off by the wait, which doubles for the one after.</summary>
    public void Failed(string id, DateTime now)
    {
        TimeSpan gap = devices.TryGetValue(id, out var state) ? state.Gap : FIRST;
        devices[id] = (now + gap, gap * 2 > MOST ? MOST : gap * 2);
    }

    /// <summary>A device to be tried at once, heard on the LAN or linked, the waits started over.</summary>
    public void Soon(string id) => devices.Remove(id);

    /// <summary>Every device's waits forgotten, Link turned off.</summary>
    public void Clear() => devices.Clear();
}
