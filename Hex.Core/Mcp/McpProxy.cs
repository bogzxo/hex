using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.Core.Platform;

namespace Hex.Core;

/// <summary>
/// <c>Hex --mcp-proxy --name roslyn --folder worktree [--agent-chat id] [--hex pid] -- command args...</c>, what an agent's
/// MCP config starts in place of a heavy stdio server, so every agent in a worktree shares the one process the Hex they
/// run in keeps of it (McpSharedServers) rather than each loading the solution again. It finds that Hex (the one named
/// with --hex, else the one with the folder open, else the only one), comes in at its MCP pipe's door tagged as a proxy,
/// says the server it wants, and from then on passes lines both ways like the bridge, keeping the agent's handshake to
/// say again to a Hex that restarted. With no Hex to be had it starts the server itself and passes lines to that, so the
/// agent always has its server; only the sharing is lost.
/// </summary>
internal static class McpProxy
{
    public const string ARGUMENT = "--mcp-proxy";
    public const string YES = "OK", NO = "NO";

    private const int CONNECT_MS = 5_000;
    private const int ANSWER_MS = 10_000;

    // How long a proxy waits for a Hex to come back once the one it talked to went, as the bridge does, before it
    // starts the server itself, and how often it looks
    private const int AGAIN_WAIT_MS = 180_000, AGAIN_EVERY_MS = 500;

    internal static bool IsProxyMode(string[] args)
    {
        int dash = Array.IndexOf(args, "--");
        return Array.IndexOf(args, ARGUMENT) is >= 0 and var at && (dash < 0 || at < dash);
    }

    /// <summary>What a proxy was started with.</summary>
    internal sealed record Request(string Name, string Folder, string Command, string[] Arguments, string? Chat, int Hex)
    {
        /// <summary>The first line said to Hex after the door.</summary>
        public string Hello() => new JsonObject
        {
            ["name"] = Name,
            ["folder"] = Folder,
            ["command"] = Command,
            ["args"] = new JsonArray([.. Arguments.Select(argument => (JsonNode?)argument)]),
            ["chat"] = Chat
        }.ToJsonString();
    }

    /// <summary>The arguments read, null when there's no command after --.</summary>
    internal static Request? Parse(string[] args)
    {
        int dash = Array.IndexOf(args, "--");
        if (dash < 0 || dash + 1 >= args.Length)
            return null;
        string[] own = args[..dash];
        string command = args[dash + 1];
        string? Value(string name) => Array.IndexOf(own, name) is >= 0 and var at && at + 1 < own.Length ? own[at + 1] : null;

        string folder = Value("--folder") is { } named && Directory.Exists(named) ? Path.GetFullPath(named) : Directory.GetCurrentDirectory();
        string name = Value("--name") is { Length: > 0 } given ? given : Path.GetFileNameWithoutExtension(command);
        int hex = int.TryParse(Value("--hex"), out int pid) ? pid : 0;
        return new Request(name, folder, command, args[(dash + 2)..], Value("--agent-chat"), hex);
    }

    public static int Run(string[] args)
    {
        McpBridge.DropOwnConsole();
        if (Parse(args) is not { } request)
        {
            Say("usage: Hex --mcp-proxy --name <server> --folder <worktree> [--agent-chat <id>] -- <command> <args...>");
            return 2;
        }

        Link? link = ToHex(request);
        if (link is not null)
            Say($"sharing {request.Name} through Hex for {request.Folder}");
        else
        {
            link = Local(request);
            if (link is null)
                return 1;
        }

        Pump(link, () => Again(request));
        return 0;
    }

    /// <summary>One end of the line to the server: Hex's pipe, or the server's own stdio when it was started here.</summary>
    internal sealed class Link(Stream read, Stream write, Action close, bool shared) : IDisposable
    {
        public Stream Read { get; } = read;
        public bool Shared { get; } = shared;
        private int closed;

        public bool Write(string line)
        {
            try
            {
                write.Write(Encoding.UTF8.GetBytes(line + "\n"));
                write.Flush();
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                try { close(); } catch (Exception) { }
            }
        }
    }

    /// <summary>The shared server through a Hex, null when there's no Hex to be had or it wouldn't.</summary>
    private static Link? ToHex(Request request)
    {
        List<McpListing> all = McpDiscovery.All();
        McpListing? listing = (request.Hex > 0 ? all.FirstOrDefault(each => each.Pid == request.Hex) : null)
            ?? McpBridge.Select(request.Folder, all, explicitFolder: false);
        if (listing is null)
            return null;

        var pipe = new NamedPipeClientStream(".", listing.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(CONNECT_MS);
            if (!McpGate.Knock(pipe, listing.Token, McpSharedServers.TAG, out string? why))
            {
                Say(why ?? "Hex didn't let the proxy in");
                pipe.Dispose();
                return null;
            }

            pipe.Write(Encoding.UTF8.GetBytes(request.Hello() + "\n"));
            pipe.Flush();
            string? answer = ReadLine(pipe, ANSWER_MS);
            if (answer?.Trim() != YES)
            {
                // A Hex from before shared servers takes the proxy for an MCP client, its first line JSON
                Say($"Hex {listing.Pid} doesn't share servers ({answer ?? "no answer"})");
                pipe.Dispose();
                return null;
            }

            return new Link(pipe, pipe, pipe.Dispose, shared: true);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Say($"Hex {listing.Pid} didn't answer on its pipe, {e.Message}");
            pipe.Dispose();
            return null;
        }
    }

    // A line a byte at a time, so nothing after it is read into a buffer that goes nowhere, given up on after a while
    private static string? ReadLine(Stream stream, int milliseconds)
    {
        Task<string?> reading = Task.Run(() =>
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    return line.Length > 0 ? line.ToString() : null;
                if (b == '\n')
                    return line.ToString();
                line.Append((char)b);
            }
        });
        return reading.Wait(milliseconds) ? reading.Result : null;
    }

    /// <summary>The server started here, its stdio the line, tied to this proxy's life. Null when it couldn't be.</summary>
    private static Link? Local(Request request)
    {
        try
        {
            string found = HexAgentProcess.Find(request.Command) ?? throw new FileNotFoundException($"{request.Command} isn't on the PATH");
            ProcessStartInfo info = HexAgentProcess.StartInfo(found, request.Arguments, request.Folder);
            info.RedirectStandardError = false;
            info.StandardErrorEncoding = null;
            Process process = Process.Start(info) ?? throw new InvalidOperationException("nothing was started");
            ChildProcesses.Adopt(process);
            Say($"no Hex to share {request.Name} through, started it for this agent alone (process {process.Id})");
            return new Link(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, () =>
            {
                try { process.StandardInput.Close(); } catch (Exception) { }
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (!process.WaitForExit(3000))
                            process.Kill(entireProcessTree: true);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                });
            }, shared: false);
        }
        catch (Exception e)
        {
            Say($"{request.Command} couldn't be started, {e.Message}");
            return null;
        }
    }

    /// <summary>After the Hex talked to went: a Hex back in time, else the server started here.</summary>
    private static Link? Again(Request request)
    {
        long giveUp = Environment.TickCount64 + AGAIN_WAIT_MS;
        while (Environment.TickCount64 < giveUp)
        {
            if (ToHex(request with { Hex = 0 }) is { } link)
            {
                Say($"sharing {request.Name} through Hex again");
                return link;
            }

            Thread.Sleep(AGAIN_EVERY_MS);
        }

        return Local(request);
    }

    /// <summary>
    /// Lines both ways until the agent is done, as the bridge's pump. Hex going isn't the end: the calls it had are
    /// failed so the agent makes them again, what the agent says meanwhile is held, and the next line (a Hex back, or the
    /// server started here) is shaken hands with as the agent did and told the rest. The server started here ending is
    /// the end, as it would be without the proxy.
    /// </summary>
    private static void Pump(Link first, Func<Link?> again)
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        var session = new McpBridgeSession();
        var writing = new Lock();
        Link? link = first;
        bool clientGone = false;

        void ToClient(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (writing)
            {
                stdout.Write(bytes);
                stdout.Flush();
            }
        }

        var fromClient = new Thread(() =>
        {
            using var reader = new StreamReader(stdin, Encoding.UTF8);
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    Link? now = Volatile.Read(ref link);
                    session.FromClient(line, now is not null);
                    if (now is not null && !now.Write(line))
                        session.FromClient(line, connected: false);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }

            Volatile.Write(ref clientGone, true);
            Volatile.Read(ref link)?.Dispose();
        })
        { IsBackground = true, Name = "mcp proxy stdin" };
        fromClient.Start();

        while (true)
        {
            Link talking = Volatile.Read(ref link)!;
            try
            {
                using var reader = new StreamReader(talking.Read, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length > 0 && session.FromHex(line) is { } said)
                        ToClient(said);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }

            Volatile.Write(ref link, null);
            talking.Dispose();
            if (Volatile.Read(ref clientGone) || !talking.Shared)
                return;

            Say("Hex went, waiting for it to come back");
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
            {
                // Started too late to be of use, gone with the proxy
                return;
            }

            foreach (string line in session.Replay())
                back.Write(line);
            Volatile.Write(ref link, back);
            foreach (string line in session.Held())
                back.Write(line);
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
            Console.Error.WriteLine($"[Hex --mcp-proxy] {line}");
        }
        catch (Exception)
        {
        }
    }
}
