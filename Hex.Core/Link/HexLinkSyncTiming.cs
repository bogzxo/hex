using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hex.Core;

// Enabled only by the opt-in working-tree benchmark. Timings overlap across endpoints and workers.
internal static class HexLinkSyncTiming
{
    private static ConcurrentDictionary<string, Counter>? active;

    internal sealed class Counter
    {
        public long Calls, Units, Ticks;
    }

    internal readonly struct Span(Counter? counter, long started, int units) : IDisposable
    {
        public void Dispose()
        {
            if (counter is null)
                return;
            Interlocked.Increment(ref counter.Calls);
            Interlocked.Add(ref counter.Units, units);
            Interlocked.Add(ref counter.Ticks, Stopwatch.GetTimestamp() - started);
        }
    }

    public static void Start() => Volatile.Write(ref active, new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal));

    public static Span Measure(string phase, int units = 1)
    {
        var counters = Volatile.Read(ref active);
        return counters is null ? default : new Span(counters.GetOrAdd(phase, static _ => new Counter()), Stopwatch.GetTimestamp(), units);
    }

    public static string[] Finish()
    {
        var counters = Interlocked.Exchange(ref active, null);
        return counters is null ? [] : [.. counters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => FormattableString.Invariant(
                $"{pair.Key,-27} {pair.Value.Calls,8} ops {pair.Value.Units,12} units {pair.Value.Ticks / (double)Stopwatch.Frequency,9:F3} aggregate s"))];
    }
}
