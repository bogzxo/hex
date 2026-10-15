using System.Diagnostics;

namespace Horizon.Hex;

/// <summary>
/// Where the time of a tick of the editor goes, step by step, for when Hex stutters and nobody knows why. Off unless
/// HEX_PROFILE is set (to how many milliseconds a tick has to take to be written out, 20 if it's just on). A slow tick is
/// logged with its steps that took longest, and on the way out every step's total and worst.
/// The steps are marked as they finish (<see cref="Mark"/>), the names are literals, so with it on or off it makes no garbage.
/// </summary>
internal sealed class HexProfiler
{
    // A step's numbers, Busy being how many times it took long enough to have done something (a tenth of a millisecond)
    private readonly struct Step(string name, long total, long worst, int busy)
    {
        public readonly string Name = name;
        public readonly long Total = total, Worst = worst;
        public readonly int Busy = busy;
    }

    private readonly bool on;
    private readonly long slowTicks;
    private readonly Dictionary<string, Step> steps = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Ticks)> tick = [];
    private long tickStart, last;
    private static readonly long busyTicks = Stopwatch.Frequency / 10_000;

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
    }

    /// <summary>The step that just finished, everything since the last mark is its.</summary>
    public void Mark(string name)
    {
        if (!on) return;
        long now = Stopwatch.GetTimestamp();
        long took = now - last;
        last = now;
        tick.Add((name, took));

        steps.TryGetValue(name, out Step was);
        steps[name] = new Step(name, was.Total + took, Math.Max(was.Worst, took), was.Busy + (took > busyTicks ? 1 : 0));
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
        Console.WriteLine("[Hex profile] step, ms altogether, times it did something, ms a time, ms at worst");
        foreach (Step step in steps.Values.OrderByDescending(step => step.Total))
            Console.WriteLine($"[Hex profile]   {step.Name,-22} {Ms(step.Total),9:0.0} {step.Busy,7} {Ms(step.Total) / Math.Max(1, step.Busy),8:0.00} {Ms(step.Worst),8:0.0}");
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
