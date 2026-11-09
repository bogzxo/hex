using System.Runtime.CompilerServices;
using Horizon.UI.Components;

namespace Hex.Dawn;

// A displayed line can stand for several breakpoints after deletion. Keep their
// identities separate until undo restores their distinct source lines.
internal sealed class HexBreakpointTracking
{
    private sealed record Positions(int Before, int After);
    private sealed class Marker(int line, HexBreakpoint settings)
    {
        internal int Line = line;
        internal HexBreakpoint Settings = settings;
        internal readonly ConditionalWeakTable<object, Positions> History = new();
    }
    private List<Marker> markers = [];

    internal void Synchronize(IReadOnlyDictionary<int, HexBreakpoint> visible)
    {
        markers.RemoveAll(marker => !visible.ContainsKey(marker.Line));
        foreach (var (line, settings) in visible)
        {
            if (markers.FirstOrDefault(marker => marker.Line == line) is { } existing)
                existing.Settings = settings;
            else
                markers.Add(new(line, settings));
        }
    }

    internal Dictionary<int, HexBreakpoint> Apply(IReadOnlyDictionary<int, HexBreakpoint> visible, UITextLineChange change)
    {
        Synchronize(visible);
        // Stable ordering determines which breakpoint is displayed when lines merge.
        markers = markers.OrderBy(marker => marker.Line).ToList();
        Dictionary<int, HexBreakpoint> moved = [];
        foreach (var marker in markers)
        {
            if (marker.History.TryGetValue(change.Identity, out var remembered))
                marker.Line = change.Undo ? remembered.Before : remembered.After;
            else
            {
                int before = marker.Line;
                marker.Line = change.MapLine(before);
                marker.History.Add(change.Identity, change.Undo ? new(marker.Line, before) : new(before, marker.Line));
            }
            moved.TryAdd(marker.Line, marker.Settings);
        }
        return moved;
    }
}
