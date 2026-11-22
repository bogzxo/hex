using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Hex.Dawn;

// Hex told to open a file by another program, Dusk's open_settings, a path clicked in Dusk's output, its hex plugin, the
// terminal's dawn command. A pipe of Hex's own (horizon-hex-<user>) that takes a line at a time, "open|path|line|column" or
// "folder|path", on a thread of its own, the file or the folder opened on the editor's next tick. The first Hex to start has the pipe, one started after it opens what it was told
// to on its own command line and leaves the pipe to the first.
internal sealed partial class DawnScene
{
    // HEX_REMOTE_PIPE names another, for trying a Hex that sleeps between uses beside the one that's open
    public static string RemotePipe => System.Environment.GetEnvironmentVariable("HEX_REMOTE_PIPE") is { Length: > 0 } named ? named : "horizon-hex-" + System.Environment.UserName.ToLowerInvariant();

    private readonly ConcurrentQueue<(string Path, int Line, int Column)> remoteOpens = new();
    private readonly ConcurrentQueue<string> remoteFolders = new();

    /// <summary>
    /// The pipe, the process's rather than an editor's, so a Hex asleep between uses (DawnResident) hears the next start
    /// on it with no editor made. One thread listens for the whole run, and hands each line to whoever hears it now.
    /// </summary>
    internal static class Remote
    {
        private static Action<string>? heard;
        private static int started;

        /// <summary>Whether this Hex has the pipe, so another start finds it, which sleeping between uses needs.</summary>
        public static bool Owned { get; private set; }

        /// <summary>Has the lines go to <paramref name="hearer"/> from now on, listening first if nobody was.</summary>
        public static void Hear(Action<string> hearer)
        {
            heard = hearer;
            if (Interlocked.Exchange(ref started, 1) == 0)
                new Thread(Listen) { IsBackground = true, Name = "Hex remote" }.Start();
        }

        /// <summary>Nobody hears the lines, unless somebody else took them over already.</summary>
        public static void Leave(Action<string> hearer)
        {
            if (Equals(heard, hearer))
                heard = null;
        }

        private static void Listen()
        {
            HexInstance.ClearStale(RemotePipe);
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(RemotePipe, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    Owned = true;
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (reader.ReadLine() is { } line)
                        heard?.Invoke(line);
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
    }

    private void StartRemote()
    {
        // The self test leaves the pipe alone, a Hex somebody has open is the one Dusk means
        // and a tablet has no other Hex or terminal to hear from
        if (options.SelfTest || !HexHost.Pipes)
            return;

        Remote.Hear(Take);

        // A Hex in a macOS .app is told what to open by an Apple Event, which comes to the same place a line on the pipe does
        MacOpenDocuments.Install(Take);
    }

    /// <summary>A line another program said, queued for the editor's thread.</summary>
    private void Take(string line)
    {
        string[] parts = line.Split('|');
        if (parts is ["open", var path, ..] && path.Length > 0)
        {
            int at = parts.Length > 2 && int.TryParse(parts[2], out int number) ? number : 0;
            int column = parts.Length > 3 && int.TryParse(parts[3], out int across) ? across : 0;
            remoteOpens.Enqueue((path, at, column));
            Show();
        }
        else if (parts is ["folder", var folder, ..] && folder.Length > 0)
        {
            remoteFolders.Enqueue(folder);
            Show();
        }
        else if (parts is ["show", ..])
        {
            Show();
        }

        // Asked for by another start of Hex, its window in front
        void Show()
        {
            Engine.WindowManager.BringToFront();
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
                // A card that goes by itself, not a red line in the status bar that stays
                Notify(Said("remote.missing", ("path", asked.Path)));
                continue;
            }

            OpenAt(asked.Path, Math.Max(0, asked.Line - 1), Math.Max(0, asked.Column - 1));
            Say(Said("remote.opened", ("name", Path.GetFileName(asked.Path))));
        }
    }

    private void StopRemote()
    {
        Remote.Leave(Take);
    }
}
