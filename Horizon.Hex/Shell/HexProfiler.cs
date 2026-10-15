using System.Diagnostics;

namespace Horizon.Hex;

/// <summary>
/// Where the time of a tick of the editor goes, and the garbage, step by step, for when Hex stutters and nobody knows why.
/// Off unless HEX_PROFILE is set (to how many milliseconds a tick has to take to be written out, 20 if it's just on). A
/// slow tick is logged with its steps that took longest, and on the way out every step's total, worst and bytes.
/// The steps are marked as they finish (<see cref="Mark"/>), the names are literals, so with it on or off it makes no garbage.
/// </summary>
internal sealed class HexProfiler
{
    // A step's numbers, Busy being how many times it took long enough to have done something (a tenth of a millisecond)
    // and Bytes what the editor's thread allocated in it
    private readonly struct Step(string name, long total, long worst, int busy, long bytes)
    {
        public readonly string Name = name;
        public readonly long Total = total, Worst = worst, Bytes = bytes;
        public readonly int Busy = busy;
    }

    private readonly bool on;
    private readonly long slowTicks;
    private readonly Dictionary<string, Step> steps = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Ticks)> tick = [];
    private long tickStart, last, lastBytes, ticks;
    private static readonly long busyTicks = Stopwatch.Frequency / 10_000;
    private const long BIG_BYTES = 16 * 1024;

    public HexProfiler()
    {
        string? asked = System.Environment.GetEnvironmentVariable("HEX_PROFILE");
        on = !string.IsNullOrEmpty(asked) && asked != "0";
        slowTicks = (int.TryParse(asked, out int ms) && ms > 1 ? ms : 20) * Stopwatch.Frequency / 1000;
    }

    public bool On => on;

    public void Begin()
    {
        if (!on) return;
        tick.Clear();
        tickStart = last = Stopwatch.GetTimestamp();
        lastBytes = GC.GetAllocatedBytesForCurrentThread();
        ticks++;
    }

    /// <summary>The step that just finished, everything since the last mark is its.</summary>
    public void Mark(string name)
    {
        if (!on) return;
        long now = Stopwatch.GetTimestamp();
        long took = now - last;
        last = now;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long made = bytes - lastBytes;
        tick.Add((name, took));

        // A step that made a lot at once is said there and then, it's the thing to look for when a sweep makes garbage
        if (made > BIG_BYTES)
            Console.WriteLine($"[Hex profile] {name} made {made / 1024} KB in one tick");

        steps.TryGetValue(name, out Step was);
        steps[name] = new Step(name, was.Total + took, Math.Max(was.Worst, took), was.Busy + (took > busyTicks ? 1 : 0), was.Bytes + made);

        // What writing this down allocated (the dictionary growing, the list) isn't the step's
        lastBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    public void End()
    {
        if (!on) return;
        long took = Stopwatch.GetTimestamp() - tickStart;
        if (took < slowTicks) return;

        // The slow tick and the three steps that were most of it
        var worst = tick.OrderByDescending(step => step.Ticks).Take(3).Select(step => $"{step.Name} {Ms(step.Ticks):0.0}");
        Console.WriteLine($"[Hex profile] a tick of {Ms(took):0.0} ms, {string.Join(", ", worst)}");
    }

    /// <summary>Every step, the ones that took longest altogether first.</summary>
    public void Report()
    {
        if (!on) return;
        Console.WriteLine($"[Hex profile] {ticks} ticks. Step, ms altogether, times it did something, ms a time, ms at worst, bytes a tick");
        foreach (Step step in steps.Values.OrderByDescending(step => step.Total))
            Console.WriteLine($"[Hex profile]   {step.Name,-22} {Ms(step.Total),9:0.0} {step.Busy,7} {Ms(step.Total) / Math.Max(1, step.Busy),8:0.00} {Ms(step.Worst),8:0.0} {step.Bytes / Math.Max(1, ticks),9}");
    }

    /// <summary>Starts counting again, for measuring one stretch of a run (the self test's sweeps).</summary>
    public void Clear()
    {
        steps.Clear();
        ticks = 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
