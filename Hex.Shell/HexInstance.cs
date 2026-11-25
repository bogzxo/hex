using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Hex.Shell;

/// <summary>
/// One of a mode at a time. The first Dawn or Dusk to start listens on a pipe of its mode's (<see cref="PipeOf"/>), and
/// one started after it says what it was started for down the pipe (a line, "tab|folder|profile" say) and leaves, so a
/// second Dusk is a tab in the first. The line is taken on the program's own thread with <see cref="TryTake"/>.
/// </summary>
public sealed partial class HexInstance : IDisposable
{
    private readonly string pipe;
    private readonly ConcurrentQueue<string> heard = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Action? woken;

    /// <summary>The pipe a mode listens on, the user's name in it so two people on one machine don't meet.</summary>
    public static string PipeOf(HexMode mode) => $"hex-{mode.ToString().ToLowerInvariant()}-{Environment.UserName.ToLowerInvariant()}";

    /// <summary>Says a line to the one of a mode that is running, false when none is listening. Lets it come to the front.</summary>
    public static bool Tell(HexMode mode, string line) => Tell(PipeOf(mode), line);

    /// <summary>Says a line down a pipe by its name, for a test that mustn't reach the Dusk somebody has open.</summary>
    public static bool Tell(string pipeName, string line)
    {
        // No pipe of the name at all, nobody's running. Connect waits its whole time out for a pipe that isn't there,
        // which every start with nothing running paid before it could make its window
        if (OperatingSystem.IsWindows() && !WaitNamedPipeW(@"\\.\pipe\" + pipeName, 1) && Marshal.GetLastPInvokeError() == ERROR_FILE_NOT_FOUND)
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(150);
            if (OperatingSystem.IsWindows())
                AllowSetForegroundWindow(-1);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// On Linux and macOS a pipe is a socket file in the temp folder (CoreFxPipe_name), and a program that crashed leaves
    /// it there, after which nobody can listen on the name and every start took it for one already running. One that
    /// nothing answers on is taken away, before listening. Nothing on Windows, whose pipes go with their program.
    /// </summary>
    public static void ClearStale(string pipeName)
    {
        if (OperatingSystem.IsWindows())
            return;

        string socket = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        if (!File.Exists(socket))
            return;

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(150);
            return;
        }
        catch (Exception)
        {
            // Nobody there
        }

        try
        {
            File.Delete(socket);
        }
        catch (Exception)
        {
        }
    }

    /// <param name="woken">Called on the listening thread when a line came, to wake a program that sleeps between frames.</param>
    public HexInstance(HexMode mode, Action? woken = null) : this(PipeOf(mode), woken)
    {
    }

    /// <summary>One listening on a pipe by its name, for a test.</summary>
    public HexInstance(string pipeName, Action? woken = null)
    {
        pipe = pipeName;
        this.woken = woken;
        new Thread(Listen) { IsBackground = true, Name = $"{pipeName} instance" }.Start();
    }

    /// <summary>The next line another start of the mode said, false when there is none.</summary>
    public bool TryTake(out string line) => heard.TryDequeue(out line!);

    private void Listen() => Serve(pipe, line =>
    {
        heard.Enqueue(line);
        woken?.Invoke();
    }, stop.Token);

    /// <summary>Whether somebody listens on the pipe now.</summary>
    public static bool IsAnswered(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(150);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens on a pipe and hands each line to <paramref name="onLine"/>, until <paramref name="token"/> is cancelled. Runs
    /// on a thread of its own, and nothing it meets leaves it as a throw, which on such a thread takes the whole program down
    /// (a macOS crash of Hex was the bind of the next server failing with "address already in use"). A bind that fails
    /// is tried again after a wait, the stale socket file of a program that died removed; a pipe somebody else answers on
    /// is theirs, and this one stops serving. <paramref name="onBound"/> is called when the pipe is bound.
    /// </summary>
    public static void Serve(string pipeName, Action<string> onLine, CancellationToken token, Action? onBound = null, PipeOptions options = PipeOptions.Asynchronous)
    {
        int failures = 0;
        int wait = 100;
        bool told = false;
        try
        {
            ClearStale(pipeName);
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, options);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Address in use (a SocketException on Linux and macOS, an IOException on Windows) or no right to the pipe.
                    // Somebody who answers has it and keeps it, otherwise a stale file is taken away and the bind tried again
                    if (IsAnswered(pipeName) || e is UnauthorizedAccessException || ++failures > 8)
                    {
                        if (!told)
                            Console.WriteLine($"[Hex] the pipe {pipeName} is not this program's to serve, {e.Message}");
                        return;
                    }

                    if (!told)
                        Console.WriteLine($"[Hex] the pipe {pipeName} could not be bound, trying again, {e.Message}");
                    told = true;
                    ClearStale(pipeName);
                    if (token.WaitHandle.WaitOne(wait))
                        return;
                    wait = Math.Min(wait * 2, 2000);
                    continue;
                }

                failures = 0;
                wait = 100;
                try
                {
                    using (server)
                    {
                        onBound?.Invoke();
                        if (options.HasFlag(PipeOptions.Asynchronous))
                            server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                        else
                            server.WaitForConnection();
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        while (reader.ReadLine() is { } line)
                            onLine(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // A client that went away mid line, a handler that threw. The next client is still waited for
                    Console.WriteLine($"[Hex] the pipe {pipeName} dropped a client, {e.Message}");
                    if (token.WaitHandle.WaitOne(50))
                        return;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] the pipe {pipeName} stopped, {e.Message}");
        }
    }

    public void Dispose() => stop.Cancel();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int process);

    private const int ERROR_FILE_NOT_FOUND = 2;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipeW(string name, uint milliseconds);
}
