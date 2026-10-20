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

    private void Listen()
    {
        ClearStale(pipe);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    heard.Enqueue(line);
                    woken?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // another of the mode has the pipe already
                return;
            }
        }
    }

    public void Dispose() => stop.Cancel();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int process);
}
