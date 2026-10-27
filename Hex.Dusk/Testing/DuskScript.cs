using System.Numerics;

using Horizon.UI;

namespace Hex.Dusk;

/// <summary>
/// A pointer that follows a script rather than the mouse, for Dusk to test itself with, Hex's pointer script in
/// short. Moves, presses, things to do and things to check are queued up front and played back one after the other,
/// by updates and not by the clock, except what really takes time (a tween, a shell coming up), which is waited for
/// with <see cref="Settle"/> and <see cref="WaitFor"/>. The checks are printed as they run, with a tally at the end.
/// </summary>
internal sealed class DuskScript
{
    private const int MOVE_UPDATES = 2, PRESS_UPDATES = 1, WAIT_UPDATES = 3;

    /// <summary>How many times slower than flat out it goes, 1 for as fast as Dusk keeps up with, 8 or so to watch.</summary>
    public int Pace { get; init; } = 1;

    private readonly record struct Step(
        Func<Vector2>? Target = null,
        bool Down = false,
        float Duration = 0.0f,
        string? Name = null,
        Func<bool>? Check = null,
        Action? Action = null,
        Func<bool>? Until = null,
        bool Secondary = false,
        bool Seconds = false);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private float elapsed;
    private Vector2 from, position;
    private bool down, secondary;

    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public bool IsFinished { get; private set; }

    public UIPointer Pointer => new(position, down, secondary);

    public void Click(Func<Vector2> target)
    {
        Glide(target, false, MOVE_UPDATES * Pace);
        Glide(target, true, PRESS_UPDATES * Pace);
        Glide(target, false, PRESS_UPDATES * Pace);
    }

    public void RightClick(Func<Vector2> target)
    {
        Glide(target, false, MOVE_UPDATES * Pace);
        steps.Enqueue(new Step(target, false, PRESS_UPDATES * Pace, Secondary: true));
        Glide(target, false, PRESS_UPDATES * Pace);
    }

    /// <summary>Presses at one place, moves to another with the button held and lets go there.</summary>
    public void Drag(Func<Vector2> start, Func<Vector2> end)
    {
        Glide(start, false, MOVE_UPDATES * Pace);
        Glide(start, true, PRESS_UPDATES * Pace);
        Glide(end, true, MOVE_UPDATES * 4.0f * Pace);
        Glide(end, false, PRESS_UPDATES * Pace);
    }

    /// <summary>Moves the pointer somewhere without pressing, out of the way of a shot say.</summary>
    public void Move(Func<Vector2> target) => Glide(target, false, MOVE_UPDATES * Pace);

    /// <summary>Lets a few updates pass with the pointer where it is, for the UI to catch up with what was done to it.</summary>
    public void Wait() => steps.Enqueue(new Step(Duration: WAIT_UPDATES * Pace));

    /// <summary>Lets time pass by the clock, for what takes time whatever the pace is, a tween, a shell.</summary>
    public void Settle(float seconds) => steps.Enqueue(new Step(Duration: seconds, Seconds: true));

    /// <summary>Waits for something to be the case, for no longer than a while (in seconds).</summary>
    public void WaitFor(Func<bool> until, float timeout) => steps.Enqueue(new Step(Duration: timeout, Until: until, Seconds: true));

    public void Check(string name, Func<bool> check) => steps.Enqueue(new Step(Name: name, Check: check));

    public void Run(Action action) => steps.Enqueue(new Step(Action: action));

    private void Glide(Func<Vector2> target, bool held, float duration) => steps.Enqueue(new Step(target, held, duration));

    public void Update(float dt)
    {
        if (current is null)
        {
            if (!steps.TryDequeue(out var next))
            {
                if (!IsFinished)
                    Console.WriteLine($"[Dusk self-test] {Passed} of {Passed + Failed} checks passed.");

                IsFinished = true;
                return;
            }

            current = next;
            elapsed = 0.0f;
            from = position;
        }

        Step step = current.Value;

        if (step.Action is not null)
        {
            try
            {
                step.Action();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Dusk self-test] step threw: {e.GetType().Name}: {e.Message}");
                Console.WriteLine("[Dusk self-test] FAIL: a step threw");
                Failed++;
            }

            current = null;
            return;
        }

        if (step.Check is not null)
        {
            bool ok;
            try
            {
                ok = step.Check();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Dusk self-test] {step.Name} threw: {e.Message}");
                ok = false;
            }

            if (ok) Passed++; else Failed++;
            Console.WriteLine($"[Dusk self-test] {(ok ? "pass" : "FAIL")}: {step.Name}");
            current = null;
            return;
        }

        elapsed += step.Seconds ? dt : 1.0f;

        if (step.Target is null)
        {
            // A wait whose question blows up is over, with a failure counted for it once, and the test goes on
            bool done;
            try
            {
                done = elapsed >= step.Duration || step.Until?.Invoke() == true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Dusk self-test] step threw: {e.GetType().Name}: {e.Message}");
                Console.WriteLine("[Dusk self-test] FAIL: a wait threw");
                Failed++;
                done = true;
            }

            if (done)
                current = null;
            return;
        }

        float progress = step.Duration <= 0.0f ? 1.0f : MathF.Min(1.0f, elapsed / step.Duration);
        position = Vector2.Lerp(from, step.Target(), progress);
        down = step.Down;
        secondary = step.Secondary;

        if (progress >= 1.0f)
            current = null;
    }
}
