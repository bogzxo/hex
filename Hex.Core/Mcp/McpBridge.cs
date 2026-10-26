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

    /// <summary>The bridge, until the client or Hex goes. 0 when it went cleanly, 1 when it never got to Hex.</summary>
    public static int Run(string[] args, string program)
    {
        // A console made for this alone (a client with none of its own started a Debug Hex) is let go of at once, nobody
        // reads it and it was a window in somebody's way
        DropOwnConsole();

        string folder = FolderOf(args);
        McpListing? listing = McpDiscovery.For(folder, McpDiscovery.All());
        if (listing is null)
        {
            Say($"no Hex has {folder} open, starting one");
            listing = StartHex(program, folder);
            if (listing is null)
                return 1;
        }

        using var pipe = new NamedPipeClientStream(".", listing.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(CONNECT_MS);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Say($"Hex {listing.Pid} didn't answer on its pipe, {e.Message}");
            return 1;
        }

        if (!McpGate.Knock(pipe, listing.Token, out string? why))
        {
            Say(why ?? "Hex didn't let this in");
            return 1;
        }

        Say($"talking to Hex {listing.Pid} for {folder}");
        Pump(pipe);
        return 0;
    }

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

    /// <summary>Bytes both ways until one side is done. Stdin ending hangs up on Hex, Hex hanging up ends stdout.</summary>
    private static void Pump(NamedPipeClientStream pipe)
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();

        var toHex = new Thread(() =>
        {
            Copy(stdin, pipe);

            // The client's done, Hex hears the end of the stream and lets the session go
            try { pipe.Dispose(); } catch (Exception) { }
        }) { IsBackground = true, Name = "mcp stdin" };
        toHex.Start();

        Copy(pipe, stdout);
    }

    private static void Copy(Stream from, Stream to)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = from.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return;
                to.Write(buffer, 0, read);
                to.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
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
