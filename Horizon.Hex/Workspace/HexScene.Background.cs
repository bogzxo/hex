using System.Collections.Concurrent;

namespace Horizon.Hex;

// Work that reads or goes through the whole workspace, done on the thread pool and taken in on the editor's thread when
// it is done. Checking every file (F8) and finding in files read and parsed every .hor on the editor's thread, a big
// workspace was Hex frozen for as long as that took.
internal sealed partial class HexScene
{
    // What finished, to be taken in on the next tick, and how many are still going
    private readonly ConcurrentQueue<Action> finishedWork = new();
    private int workGoing;

    /// <summary>Whether something is being done in the background right now.</summary>
    private bool Working => Volatile.Read(ref workGoing) > 0;

    /// <summary>
    /// Does something off the editor's thread and hands what it came to over on it. What the work reads of the editor
    /// it is given before it goes (a list of folders, a query), never reaches for while it runs.
    /// </summary>
    /// <param name="quiet">Whether it goes without saying so in the status bar, for what nobody asked for by name.</param>
    private void InBackground<T>(string doing, Func<T> work, Action<T> done, bool quiet = false)
    {
        Interlocked.Increment(ref workGoing);
        if (!quiet)
            Say(doing);

        Task.Run(() =>
        {
            try
            {
                T result = work();
                finishedWork.Enqueue(() => done(result));
            }
            catch (Exception e)
            {
                finishedWork.Enqueue(() => Notify($"{doing} went wrong, {e.Message}", error: true));
            }
            finally
            {
                Interlocked.Decrement(ref workGoing);
                Engine.WindowManager.Wake();
            }
        });
    }

    /// <summary>Every tick, takes in whatever finished.</summary>
    private void TakeFinishedWork()
    {
        while (finishedWork.TryDequeue(out Action? done))
            done();
    }
}
