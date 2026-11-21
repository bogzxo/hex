using System.IO.Pipes;

namespace Hex.Core;

/// <summary>
/// <c>Hex --mcp</c> started by an MCP client (Claude Code's <c>claude mcp add hex -- Hex.exe --mcp</c>), its stdin and stdout
/// the client's. It finds the Hex that has the folder open (the one named with --folder, or the one it was started in),
/// starts one on that folder with the server on when there's none and waits for it, says the token at its pipe's door, and
/// from then on passes the bytes along both ways untouched until either side hangs up. It has no window and no console of
/// its own, and what it has to say about itself goes to stderr, stdout is the protocol's.
/// </summary>
internal static partial class McpBridge
{
    // How long a Hex that was started is given to come up and open its pipe. A Debug build's first run compiles its shaders
    private const int START_WAIT_MS = 90_000;

    // How long the pipe of a Hex that's running is given to answer
    private const int CONNECT_MS = 5_000;

    internal static bool IsBridgeMode(string[] args) => args.Contains("--mcp-connect")
        || (args.Contains("--mcp") && !args.Contains("--window") && !args.Contains("--selftest"));

    /// <summary>The bridge, until the client or Hex goes. 0 when it went cleanly, 1 when it never got to Hex.</summary>
    public static int Run(string[] args, string program)
    {
        // A console made for this alone (a client with none of its own started a Debug Hex) is let go of at once, nobody
        // reads it and it was a window in somebody's way
        DropOwnConsole();

        string folder = FolderOf(args);
        McpListing? listing = Resolve(folder, args);
        if (listing is null)
        {
            if (args.Contains("--mcp-connect"))
            {
                Say($"no matching Hex MCP server for {folder}; open Hex with --window --mcp first. Attach-only mode never opens a window.");
                return 1;
            }
            listing = FindOrStart(folder, program, args);
            if (listing is null)
                return 1;
        }

        // A bridge started for a chat of the Agents panel says which, so what the agent does is known to be that chat's
        int chatAt = Array.IndexOf(args, "--agent-chat");
        string? chat = chatAt >= 0 && chatAt + 1 < args.Length ? args[chatAt + 1] : null;
        NamedPipeClientStream? pipe = Open(listing, chat);
        if (pipe is null)
            return 1;

        Say($"talking to Hex {listing.Pid} for {folder}");
        Pump(pipe, listing.Pid, () => Again(folder, args, chat));
        return 0;
    }

    // How long a bridge waits for a Hex to come back on its folder once the one it talked to went, a restart into a
    // build of itself takes a while, and how often it looks
    private const int AGAIN_WAIT_MS = 180_000, AGAIN_EVERY_MS = 500;

    /// <summary>The pipe of a Hex opened and the door knocked on, null having said why it couldn't be.</summary>
    private static NamedPipeClientStream? Open(McpListing listing, string? chat)
    {
        var pipe = new NamedPipeClientStream(".", listing.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(CONNECT_MS);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Say($"Hex {listing.Pid} didn't answer on its pipe, {e.Message}");
            pipe.Dispose();
            return null;
        }

        if (!McpGate.Knock(pipe, listing.Token, chat, out string? why))
        {
            Say(why ?? "Hex didn't let this in");
            pipe.Dispose();
            return null;
        }

        return pipe;
    }

    /// <summary>
    /// The Hex on the folder after the one talked to went, waited for while it restarts. Null when none came back in
    /// time, the bridge ends then as it always did.
    /// </summary>
    private static (NamedPipeClientStream Pipe, int Pid)? Again(string folder, string[] args, string? chat)
    {
        long giveUp = Environment.TickCount64 + AGAIN_WAIT_MS;
        while (Environment.TickCount64 < giveUp)
        {
            if (Resolve(folder, args) is { } listing && McpDiscovery.Running(listing.Pid) && Open(listing, chat) is { } pipe)
            {
                Say($"talking to Hex {listing.Pid} again for {folder}");
                return (pipe, listing.Pid);
            }

            Thread.Sleep(AGAIN_EVERY_MS);
        }

        Say($"no Hex came back for {folder} in {AGAIN_WAIT_MS / 1000} seconds");
        return null;
    }

    private static McpListing? Resolve(string folder, string[] args) =>
        Select(folder, McpDiscovery.All(), ExplicitFolder(args));

    internal static bool ExplicitFolder(string[] args) => args.Contains("--folder")
        || args.Any(arg => !arg.StartsWith('-') && Directory.Exists(arg));

    /// <summary>An explicit workspace takes priority; otherwise a single editor is unambiguous.</summary>
    internal static McpListing? Select(string folder, IEnumerable<McpListing> listings, bool explicitFolder)
    {
        var all = listings.OrderBy(listing => listing.Pid).ToList();
        return McpDiscovery.For(folder, all) ?? (!explicitFolder && all.Count == 1 ? all[0] : null);
    }

    // Codex chats can each start a bridge before the first editor has published discovery. Serialize launches,
    // then recheck discovery while holding the same installation-wide lock.
    internal static McpListing? UnderLaunchLock(string scope, Func<McpListing?> find, Func<McpListing?> start)
    {
        string name = "hex-mcp-start-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope)))[..24];
        using var gate = new Mutex(false, name);
        bool owned = false;
        try
        {
            try { owned = gate.WaitOne(START_WAIT_MS + CONNECT_MS); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return null;
            return find() ?? start();
        }
        finally { if (owned) gate.ReleaseMutex(); }
    }

    private static McpListing? FindOrStart(string folder, string program, string[] args) =>
        UnderLaunchLock(Path.GetFullPath(McpDiscovery.Folder).ToUpperInvariant(), () => Resolve(folder, args), () =>
        {
            Say($"no Hex has {folder} open, starting one");
            return StartHex(program, folder);
        });

    /// <summary>The folder it's for, --folder's, a folder named on its own, or where it was started.</summary>
    private static string FolderOf(string[] args)
    {
        int at = Array.IndexOf(args, "--folder");
        if (at >= 0 && at + 1 < args.Length && Directory.Exists(args[at + 1]))
            return Path.GetFullPath(args[at + 1]);

        foreach (string arg in args)
        {
            if (!arg.StartsWith('-') && Directory.Exists(arg))
                return Path.GetFullPath(arg);
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>A Hex started on the folder with its server on, waited for until it's written its file. Null if it never did.</summary>
    private static McpListing? StartHex(string program, string folder)
    {
        int pid;
        try
        {
            pid = Launch(program, ["--window", "--folder", folder, "--mcp"]);
        }
        catch (Exception e)
        {
            Say($"Hex couldn't be started, {e.Message}");
            return null;
        }

        long giveUp = Environment.TickCount64 + START_WAIT_MS;
        while (Environment.TickCount64 < giveUp)
        {
            if (McpDiscovery.Of(pid) is { } listing)
                return listing;

            if (!McpDiscovery.Running(pid))
            {
                Say($"the Hex that was started ({pid}) stopped before its server was up");
                return null;
            }

            Thread.Sleep(100);
        }

        Say($"the Hex that was started ({pid}) didn't put its server up in {START_WAIT_MS / 1000} seconds");
        return null;
    }

    /// <summary>
    /// Messages both ways, a line each, until the client is done. Hex going (restarted into a build of itself) isn't the
    /// end. The calls it was in the middle of are failed so the client makes them again, what the client says meanwhile
    /// is held, and once a Hex is back on the folder it's shaken hands with as the client did and told the rest. Without
    /// one back in time the bridge ends, as it did before.
    /// </summary>
    private static void Pump(NamedPipeClientStream first, int pid, Func<(NamedPipeClientStream Pipe, int Pid)?> again)
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        var session = new McpBridgeSession();
        var writing = new Lock();
        NamedPipeClientStream? pipe = first;
        // Said by the stdin thread, read by this one
        bool clientGone = false;

        void ToClient(string line) => McpProtocol.WriteLine(stdout, line, writing);

        static bool ToHex(Stream to, string line)
        {
            try
            {
                McpProtocol.WriteLine(to, line);
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }

        var fromClient = new Thread(() =>
        {
            using var reader = new StreamReader(stdin, System.Text.Encoding.UTF8);
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    NamedPipeClientStream? now = Volatile.Read(ref pipe);
                    session.FromClient(line, now is not null);
                    if (now is not null && !ToHex(now, line))
                        session.FromClient(line, connected: false);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }

            // The client's done, Hex hears the end of the stream and lets the session go
            Volatile.Write(ref clientGone, true);
            try { Volatile.Read(ref pipe)?.Dispose(); } catch (Exception) { }
        }) { IsBackground = true, Name = "mcp stdin" };
        fromClient.Start();

        while (true)
        {
            NamedPipeClientStream talking = Volatile.Read(ref pipe)!;
            try
            {
                using var reader = new StreamReader(talking, System.Text.Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
                while (reader.ReadLine() is { } line)
                {
                    if (session.FromHex(line) is { } said)
                        ToClient(said);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }

            Volatile.Write(ref pipe, null);
            try { talking.Dispose(); } catch (Exception) { }
            if (Volatile.Read(ref clientGone))
                return;

            Say($"Hex {pid} went, waiting for it to come back");
            try
            {
                foreach (string failed in session.Dropped())
                    ToClient(failed);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return;
            }

            if (again() is not { } back || Volatile.Read(ref clientGone))
                return;
            pid = back.Pid;
            foreach (string line in session.Replay())
                ToHex(back.Pipe, line);
            Volatile.Write(ref pipe, back.Pipe);
            // Said what the client held while there was no Hex, since it may have said more as the first lines went
            foreach (string line in session.Held())
                ToHex(back.Pipe, line);
            try
            {
                ToClient(McpBridgeSession.ToolsChanged());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static void Say(string line)
    {
        try
        {
            Console.Error.WriteLine($"[Hex --mcp] {line}");
        }
        catch (Exception)
        {
        }
    }
}
