using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// Things an extension wants done later, once or over and over.
//
//   hex.every(seconds, function)   function(hex) every so many seconds, for as long as the extension is on
//   hex.after(seconds, function)   function(hex) once, that many seconds from now
//
// Done on the editor's thread from its tick like everything else an extension does. Hex sleeps while nothing happens,
// so a timer of the thread pool wakes it when the next one is due, and an idle editor still gets its greeting on time.
// Starting an extension again (switched on, its file saved) forgets what it asked for before, its on_start asks again.
internal sealed partial class HexExtensions
{
    // The shortest a timer is allowed to be, anything quicker is an extension spinning in the editor's tick
    private const double SHORTEST_TIMER = 0.1;

    private readonly List<ExtensionTimer> timers = [];
    private Timer? waker;

    private (string, IRuntimeValue)[] Timers(HexExtension extension) =>
    [
        ("every", Natives.Action("every", args => AddTimer(extension, args.Number(0), args.Callable(1), repeats: true))),
        ("after", Natives.Action("after", args => AddTimer(extension, args.Number(0), args.Callable(1), repeats: false)))
    ];

    private void AddTimer(HexExtension extension, double seconds, IRuntimeValue function, bool repeats)
    {
        double every = Math.Max(SHORTEST_TIMER, double.IsFinite(seconds) ? seconds : SHORTEST_TIMER);
        timers.Add(new ExtensionTimer(extension, function, every, System.Environment.TickCount64 + (long)(every * 1000), repeats));
        WakeForTimers();
    }

    /// <summary>Every tick, the timers that are due called, and the ones of an extension that's off or broken let go of.</summary>
    private void UpdateTimers()
    {
        if (timers.Count == 0)
            return;

        // Gone through as they were at the start of the tick, one called can ask for more or have its extension started
        // again. The copy is a list kept for it, an array made every tick there was a timer at all was garbage a tick
        long now = System.Environment.TickCount64;
        timersLooked.AddRange(timers);
        try
        {
            foreach (ExtensionTimer timer in timersLooked)
            {
                if (!timer.Extension.Enabled || timer.Extension.Failed is not null)
                {
                    timers.Remove(timer);
                    continue;
                }

                if (now < timer.Due)
                    continue;

                if (timer.Repeats) timer.Due = now + (long)(timer.Every * 1000);
                else timers.Remove(timer);

                if (!timer.Extension.Invoke(timer.Function, HexOf(timer.Extension)))
                    Failed(timer.Extension);
            }
        }
        finally
        {
            timersLooked.Clear();
        }

        WakeForTimers();
    }

    /// <summary>What an extension asked to have done later, forgotten, as it starts again.</summary>
    private void ForgetTimers(HexExtension extension) => timers.RemoveAll(timer => timer.Extension == extension);

    // The thread pool's timer set for the soonest one, which wakes the editor so its tick gets round to it
    private void WakeForTimers()
    {
        if (timers.Count == 0)
        {
            waker?.Change(Timeout.Infinite, Timeout.Infinite);
            wakeFor = long.MinValue;
            return;
        }

        long soonest = long.MaxValue;
        foreach (ExtensionTimer timer in timers)
            soonest = Math.Min(soonest, timer.Due);

        // Set again only when the soonest moved, it was set again every tick, the same time over and over
        if (soonest == wakeFor && waker is not null)
            return;

        wakeFor = soonest;
        long wait = Math.Max(0, soonest - System.Environment.TickCount64);
        waker ??= new Timer(_ => context.Wake());
        waker.Change(wait, Timeout.Infinite);
    }

    // What the timers' waker was last set for, the soonest timer's time, nothing set while there are none
    private long wakeFor = long.MinValue;

    // The timers as they were at the start of a tick, gone through by UpdateTimers
    private readonly List<ExtensionTimer> timersLooked = [];

    private sealed class ExtensionTimer(HexExtension extension, IRuntimeValue function, double every, long due, bool repeats)
    {
        public HexExtension Extension { get; } = extension;
        public IRuntimeValue Function { get; } = function;
        public double Every { get; } = every;
        public bool Repeats { get; } = repeats;
        public long Due { get; set; } = due;
    }
}
