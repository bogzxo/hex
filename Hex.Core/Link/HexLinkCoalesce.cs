namespace Hex.Core;

/// <summary>
/// Something done at most once a while however often it's asked for: asked again while it's waiting or running it runs
/// once more after, never twice at once, and never sooner than <c>gap</c> after it last ran. What the fleet and a
/// watched chat send through, so an agent streaming a hundred lines a second is five notifications a second.
/// </summary>
internal sealed class HexLinkCoalesce(TimeSpan gap, Func<Task> run) : IDisposable
{
    private readonly Lock gate = new();
    private bool waiting, running, again, disposed;
    private long last = long.MinValue / 2;
    private Timer? timer;

    /// <summary>Asks for it to run, soon.</summary>
    public void Poke()
    {
        lock (gate)
        {
            if (disposed)
                return;
            if (running)
            {
                again = true;
                return;
            }

            if (waiting)
                return;
            waiting = true;
            long wait = Math.Max(0, last + (long)gap.TotalMilliseconds - Environment.TickCount64);
            timer ??= new Timer(_ => _ = Run(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(wait, Timeout.Infinite);
        }
    }

    private async Task Run()
    {
        lock (gate)
        {
            if (disposed)
                return;
            waiting = false;
            running = true;
            last = Environment.TickCount64;
        }

        try
        {
            await run();
        }
        catch (Exception)
        {
            // Whatever it sends went wrong this time, the next poke tries again
        }
        finally
        {
            bool more;
            lock (gate)
            {
                running = false;
                more = again;
                again = false;
            }

            if (more)
                Poke();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
        }
    }
}
