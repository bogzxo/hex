namespace Hex.Core;

internal static class FilePublication
{
    private sealed class PathWrite
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private static readonly Dictionary<string, PathWrite> writing = new(PlatformPaths.Comparer);

    private static PathWrite JoinPath(string full)
    {
        lock (writing)
        {
            if (!writing.TryGetValue(full, out PathWrite? gate))
                writing.Add(full, gate = new PathWrite());
            gate.Users++;
            return gate;
        }
    }

    private static void LeavePath(string full, PathWrite gate, bool entered)
    {
        if (entered)
            gate.Turn.Release();
        lock (writing)
        {
            if (--gate.Users == 0)
            {
                writing.Remove(full);
                gate.Turn.Dispose();
            }
        }
    }

    private sealed class LocalWrite(string full, PathWrite gate) : IDisposable
    {
        public void Dispose() => LeavePath(full, gate, entered: true);
    }

    /// <summary>Serializes writes to the same local destination.</summary>
    internal static IDisposable Enter(string full)
    {
        full = Path.GetFullPath(full);
        PathWrite gate = JoinPath(full);
        gate.Turn.Wait();
        return new LocalWrite(full, gate);
    }

    internal static async ValueTask<IDisposable> EnterAsync(string full, CancellationToken cancel)
    {
        PathWrite gate = JoinPath(full);
        try
        {
            await gate.Turn.WaitAsync(cancel).ConfigureAwait(false);
            return new LocalWrite(full, gate);
        }
        catch
        {
            LeavePath(full, gate, entered: false);
            throw;
        }
    }
}
