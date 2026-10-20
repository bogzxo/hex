using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Hex.Dawn;

// Hex told to open a file by another program, Dusk's open_settings, a path clicked in Dusk's output, its hex plugin, the
// terminal's dawn command. A pipe of Hex's own (horizon-hex-<user>) that takes a line at a time, "open|path|line" or
// "folder|path", on a thread of its own, the file or the folder opened on the editor's next tick. The first Hex to start has the pipe, one started after it opens what it was told
// to on its own command line and leaves the pipe to the first.
internal sealed partial class HexScene
{
    public static string RemotePipe => "horizon-hex-" + System.Environment.UserName.ToLowerInvariant();

    private readonly ConcurrentQueue<(string Path, int Line)> remoteOpens = new();
    private readonly ConcurrentQueue<string> remoteFolders = new();
    private CancellationTokenSource? remoteStop;

    private void StartRemote()
    {
        // The self test leaves the pipe alone, a Hex somebody has open is the one Dusk means
        if (options.SelfTest)
            return;

        remoteStop = new CancellationTokenSource();
        CancellationToken stop = remoteStop.Token;
        var thread = new Thread(() => Listen(stop)) { IsBackground = true, Name = "Hex remote" };
        thread.Start();
    }

    private void Listen(CancellationToken stop)
    {
        HexInstance.ClearStale(RemotePipe);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(RemotePipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                    Take(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Another Hex has the pipe, this one opens what it is told on its own command line and nothing else
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] the pipe for other programs stopped, {e.Message}");
                return;
            }
        }
    }

    /// <summary>A line another program said, queued for the editor's thread.</summary>
    private void Take(string line)
    {
        string[] parts = line.Split('|');
        if (parts is ["open", var path, ..] && path.Length > 0)
        {
            int at = parts.Length > 2 && int.TryParse(parts[2], out int number) ? number : 0;
            remoteOpens.Enqueue((path, at));
            Engine.WindowManager.Wake();
        }
        else if (parts is ["folder", var folder, ..] && folder.Length > 0)
        {
            remoteFolders.Enqueue(folder);
            Engine.WindowManager.Wake();
        }
    }

    /// <summary>Every tick, the files other programs asked for opened, at their lines.</summary>
    private void UpdateRemote()
    {
        while (remoteFolders.TryDequeue(out string? folder))
            explorer.OpenFolder(folder);

        while (remoteOpens.TryDequeue(out var asked))
        {
            if (!File.Exists(asked.Path))
            {
                Say($"asked to open {asked.Path}, which isn't there", error: true);
                continue;
            }

            OpenAt(asked.Path, Math.Max(0, asked.Line - 1), 0);
            Say($"opened {Path.GetFileName(asked.Path)}, asked for by another program");
        }
    }

    private void StopRemote()
    {
        remoteStop?.Cancel();
    }
}
