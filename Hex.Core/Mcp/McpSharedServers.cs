using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The shared MCP servers of this Hex, one <see cref="McpSharedServer"/> for each key (name, worktree root, command
/// line), made for the first agent that asks and forgotten once it's been idle long enough. Agents reach it through
/// <c>Hex --mcp-proxy</c> (McpProxy), which comes in on Hex's MCP pipe tagged <see cref="TAG"/> and says what it wants
/// in its first line after the door.
/// </summary>
internal sealed class McpSharedServers
{
    /// <summary>What a proxy is tagged with at the pipe's door (McpGate), so the pipe host hands it here.</summary>
    public const string TAG = "shared";

    // How many requests each server has up at once, and how long one with no agents is kept
    public const int MOST = 4;
    public static readonly TimeSpan IDLE = TimeSpan.FromMinutes(10);

    private readonly McpUpstreamStart start;
    private readonly int most;
    private readonly TimeSpan idle;
    private readonly Action<string> log;
    private readonly Dictionary<string, McpSharedServer> servers = [];
    private readonly Lock gate = new();

    public McpSharedServers(McpUpstreamStart start, int most, TimeSpan idle, Action<string> log)
    {
        this.start = start;
        this.most = most;
        this.idle = idle;
        this.log = log;
    }

    private static McpSharedServers? shared;

    /// <summary>The process's own, real processes, its lines in Hex's log, all of them stopped as Hex goes.</summary>
    public static McpSharedServers Shared
    {
        get
        {
            if (Volatile.Read(ref shared) is { } made)
                return made;
            var fresh = new McpSharedServers(McpProcessUpstream.Start, MOST, IDLE, line => Horizon.Logging.Log.Info(line));
            if (Interlocked.CompareExchange(ref shared, fresh, null) is null)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => fresh.ShutdownAll();
            return shared!;
        }
    }

    /// <summary>The servers there are now.</summary>
    public IReadOnlyList<McpSharedServer> Servers
    {
        get { lock (gate) return [.. servers.Values]; }
    }

    /// <summary>An agent joined to the server for its key, made when there's none (or the one there was retired).</summary>
    public McpSharedServer Join(McpSharedKey key, McpSharedClient client)
    {
        lock (gate)
        {
            if (!servers.TryGetValue(key.Id, out McpSharedServer? server) || !server.Attach(client))
            {
                server = new McpSharedServer(key, start, most, idle, log, Retire);
                servers[key.Id] = server;
                server.Attach(client);
            }

            return server;
        }
    }

    private bool Retire(McpSharedServer server)
    {
        lock (gate)
        {
            if (!server.TryRetire())
                return false;
            if (servers.TryGetValue(server.Key.Id, out McpSharedServer? there) && there == server)
                servers.Remove(server.Key.Id);
            return true;
        }
    }

    /// <summary>Every server stopped, Hex going.</summary>
    public void ShutdownAll()
    {
        McpSharedServer[] all;
        lock (gate)
        {
            all = [.. servers.Values];
            servers.Clear();
        }

        foreach (McpSharedServer server in all)
            server.Shutdown();
    }

    /// <summary>
    /// A proxy let in at the pipe's door, on a thread of its own until it goes. Its first line says what it wants,
    /// <c>{"name", "folder", "command", "args", "chat"}</c>, and it's told <c>OK</c> or <c>NO why</c> in a line;
    /// the rest is MCP to and from the shared server.
    /// </summary>
    public void Serve(TextReader reader, Stream pipe, Action done) =>
        new Thread(() =>
        {
            try
            {
                Talk(reader, pipe);
            }
            finally
            {
                try { pipe.Dispose(); } catch (Exception) { }
                done();
            }
        })
        { IsBackground = true, Name = "mcp shared client" }.Start();

    private void Talk(TextReader reader, Stream pipe)
    {
        string? first;
        try
        {
            first = reader.ReadLine();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return;
        }

        if (KeyOf(first, out string who) is not { } key)
        {
            Write(pipe, McpProxy.NO + " that wasn't a shared server's name, folder and command\n");
            return;
        }

        Write(pipe, McpProxy.YES + "\n");

        // What goes to the proxy is written by a thread of its own, so the server never waits on a pipe
        var outgoing = new BlockingCollection<string>();
        var client = new McpSharedClient(line =>
        {
            try { outgoing.Add(line); } catch (InvalidOperationException) { }
        }, who);
        var writer = new Thread(() =>
        {
            try
            {
                foreach (string line in outgoing.GetConsumingEnumerable())
                    pipe.Write(Encoding.UTF8.GetBytes(line + "\n"));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        })
        { IsBackground = true, Name = "mcp shared client out" };
        writer.Start();

        McpSharedServer server = Join(key, client);
        log($"[Hex] {who} joined the shared MCP server {key}, {server.Clients} client(s)");
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                    server.FromClient(client, line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        server.Detach(client);
        outgoing.CompleteAdding();
        writer.Join(2000);
        log($"[Hex] {who} left the shared MCP server {key}, {server.Clients} client(s)");
    }

    /// <summary>The key a proxy's first line says, null when it doesn't say one.</summary>
    internal static McpSharedKey? KeyOf(string? line, out string who)
    {
        who = "a proxy";
        if (line is null)
            return null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject said
                || Text(said["name"]) is not { Length: > 0 } name
                || Text(said["folder"]) is not { Length: > 0 } folder
                || Text(said["command"]) is not { Length: > 0 } command)
                return null;
            string[] arguments = said["args"] is JsonArray listed ? [.. listed.Select(Text).OfType<string>()] : [];
            who = Text(said["chat"]) is { Length: > 0 } chat ? $"chat {chat}" : "a proxy";
            return new McpSharedKey(name, McpSharedKey.RootOf(folder), command, arguments);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static void Write(Stream pipe, string line)
    {
        try
        {
            pipe.Write(Encoding.UTF8.GetBytes(line));
            pipe.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }
}
