using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// One agent's end of a shared server: what goes back to it, which must never wait (it's said under the server's lock,
/// a pipe's client writes from a thread of its own), and who it is for the log.
/// </summary>
internal sealed class McpSharedClient(Action<string> send, string who)
{
    public string Who { get; } = who;

    /// <summary>Its initialize answered, from then on it's told what the server says to everybody.</summary>
    internal bool Ready;

    /// <summary>It went, nothing more goes to it.</summary>
    internal bool Gone;

    internal void Send(string line)
    {
        if (!Gone)
            send(line);
    }
}

/// <summary>
/// One process of a heavy stdio MCP server (the dotnet kit's Roslyn navigator, a gigabyte and half a minute of CPU to
/// load a solution) shared by every agent in a worktree, which each think they have it to themselves.
/// <list type="bullet">
/// <item>It's started for the first agent and shaken hands with once, with that agent's initialize (told it has roots,
/// which Hex answers); the answer is kept and is every later agent's answer at once. Their notifications/initialized
/// are theirs, the server hears its own once.</item>
/// <item>Each agent's request ids are its own, so every request goes up under an id of the server's (a number counted up)
/// and its answer goes back under the agent's. The same for progress tokens, and for notifications/cancelled, which
/// names an id the server knows. A request cancelled before it went up never goes.</item>
/// <item>What the server says to nobody in particular (tools/list_changed, a log message) goes to every agent. What it
/// asks (roots/list, ping) Hex answers, with the worktree's root; it's asked of nobody else.</item>
/// <item>A few requests up at once at most, the rest wait their turn in the order they came.</item>
/// <item>When it ends of itself, the calls it had are failed for whichever agents made them, and it's started again
/// (and shaken hands with again) for the next call. Three times in a row ending before it's shaken hands is three too
/// many, the calls waiting are failed then.</item>
/// <item>With no agent left it's stopped after a while, the registry forgets it then (<see cref="McpSharedServers"/>).</item>
/// </list>
/// </summary>
internal sealed class McpSharedServer
{
    // A request that failed because of the sharing rather than the server, JSON-RPC's range for the implementation's own
    internal const int SHARED_ERROR = -32000;

    // The id the handshake goes up under, the generation after it, never a number so it never meets a call's
    private const string INIT_ID = "hex-shared-init-";
    private const string PROGRESS = "hex-shared-progress-";
    private const int MOST_FAILED_STARTS = 3;

    private readonly McpUpstreamStart start;
    private readonly int most;
    private readonly TimeSpan idle;
    private readonly Action<string> log;
    private readonly Func<McpSharedServer, bool>? retire;
    private readonly Lock gate = new();
    private readonly List<McpSharedClient> clients = [];

    // What there is of the server now, and which of its runs it is, so what an older run says late is known
    private IMcpUpstream? upstream;
    private int generation;
    private bool shaken;
    private int failedStarts;

    // The handshake said for it, the first agent's, and its answer, which every agent after gets
    private JsonObject? initParams;
    private JsonNode? initResult;
    private readonly List<(McpSharedClient Client, JsonNode? Id)> waitingInit = [];

    private readonly LinkedList<Pending> queued = new();
    private readonly Dictionary<long, Flight> flights = [];
    private readonly Dictionary<string, (McpSharedClient Client, JsonNode Token)> progress = [];
    private long nextId;
    private Timer? idleTimer;

    private sealed record Pending(McpSharedClient Client, JsonNode? Id, JsonObject Message);
    private sealed record Flight(McpSharedClient Client, JsonNode? Id, string Method, string? ProgressToken);

    /// <param name="key">What it's for and how it's started.</param>
    /// <param name="start">Starts its process, the real one (McpProcessUpstream) or a test's.</param>
    /// <param name="most">How many requests are up at once at most.</param>
    /// <param name="idle">How long it's kept with no agent before it's stopped.</param>
    /// <param name="log">A line for Hex's log.</param>
    /// <param name="retire">Asked once it's been idle long enough, true when it was let go (the registry's say).</param>
    public McpSharedServer(McpSharedKey key, McpUpstreamStart start, int most, TimeSpan idle, Action<string> log, Func<McpSharedServer, bool>? retire = null)
    {
        Key = key;
        this.start = start;
        this.most = Math.Max(1, most);
        this.idle = idle;
        this.log = log;
        this.retire = retire;
    }

    public McpSharedKey Key { get; }

    /// <summary>Stopped for good, idle or Hex going, an agent that comes now gets another.</summary>
    public bool Retired { get; private set; }

    /// <summary>Its process now, 0 while it hasn't one.</summary>
    public int Pid
    {
        get { lock (gate) return upstream?.Pid ?? 0; }
    }

    /// <summary>How many times its process has been started.</summary>
    public int Starts { get; private set; }

    /// <summary>The agents it has.</summary>
    public int Clients
    {
        get { lock (gate) return clients.Count; }
    }

    /// <summary>An agent come, the idle stop called off. False when it's retired, the agent needs another then.</summary>
    public bool Attach(McpSharedClient client)
    {
        lock (gate)
        {
            if (Retired)
                return false;
            clients.Add(client);
            idleTimer?.Dispose();
            idleTimer = null;
            return true;
        }
    }

    /// <summary>An agent gone: what it had waiting dropped, what it had up cancelled, and the idle stop set going when it was the last.</summary>
    public void Detach(McpSharedClient client)
    {
        lock (gate)
        {
            if (!clients.Remove(client))
                return;
            client.Gone = true;
            for (LinkedListNode<Pending>? at = queued.First; at is not null;)
            {
                LinkedListNode<Pending>? next = at.Next;
                if (at.Value.Client == client)
                    queued.Remove(at);
                at = next;
            }

            waitingInit.RemoveAll(waiting => waiting.Client == client);
            foreach (var (id, flight) in flights.Where(pair => pair.Value.Client == client).ToList())
                Cancel(id, flight, "the client went");
            Pump();

            if (clients.Count == 0 && !Retired)
                idleTimer = new Timer(_ => Idle(), null, idle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A line from an agent.</summary>
    public void FromClient(McpSharedClient client, string line)
    {
        JsonObject? message = Parse(line);
        lock (gate)
        {
            if (message is null)
            {
                client.Send(Error(null, -32700, "That wasn't a JSON-RPC message."));
                return;
            }

            string? method = Text(message["method"]);
            bool hasId = message.ContainsKey("id");
            JsonNode? id = message["id"]?.DeepClone();
            if (method is null)
                return; // An answer to something the server asked, Hex answered those already

            if (!hasId)
            {
                Notification(client, method, message);
                return;
            }

            switch (method)
            {
                case "initialize":
                    Initialize(client, id, message);
                    return;
                case "ping":
                    client.Send(Result(id, new JsonObject()));
                    return;
            }

            if (Retired)
            {
                client.Send(Error(id, SHARED_ERROR, $"The shared {Key.Name} server was stopped, start the client again."));
                return;
            }

            queued.AddLast(new Pending(client, id, message));
            Up();
            Pump();
        }
    }

    private void Initialize(McpSharedClient client, JsonNode? id, JsonObject message)
    {
        if (initResult is not null)
        {
            client.Send(Result(id, initResult.DeepClone()));
            client.Ready = true;
            return;
        }

        initParams ??= Handshake(message["params"] as JsonObject);
        waitingInit.Add((client, id));
        Up();
    }

    private void Notification(McpSharedClient client, string method, JsonObject message)
    {
        switch (method)
        {
            case "notifications/initialized":
            case "notifications/roots/list_changed":
            case "notifications/progress":
                // Its own handshake's end is its own, the server heard the first one. Its roots aren't the server's,
                // which has the worktree's
                return;
            case "notifications/cancelled":
                JsonNode? cancelled = (message["params"] as JsonObject)?["requestId"];
                string wanted = IdOf(cancelled);
                for (LinkedListNode<Pending>? at = queued.First; at is not null; at = at.Next)
                {
                    if (at.Value.Client == client && IdOf(at.Value.Id) == wanted)
                    {
                        queued.Remove(at);
                        return;
                    }
                }

                foreach (var (up, flight) in flights)
                {
                    if (flight.Client == client && IdOf(flight.Id) == wanted)
                    {
                        Cancel(up, flight, Text((message["params"] as JsonObject)?["reason"]) ?? "cancelled");
                        Pump();
                        return;
                    }
                }

                return;
            default:
                if (shaken)
                    upstream?.Send(Line(message));
                return;
        }
    }

    // A request the server has up, told it's cancelled and let go of here, its place free. A server needn't answer
    // a request that was cancelled, and what it says late is dropped
    private void Cancel(long up, Flight flight, string reason)
    {
        flights.Remove(up);
        if (flight.ProgressToken is not null)
            progress.Remove(flight.ProgressToken);
        upstream?.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = up, ["reason"] = reason }
        }.ToJsonString(relaxed));
    }

    // The server started when there's none, and shaken hands with
    private void Up()
    {
        if (upstream is not null || Retired)
            return;

        int run = ++generation;
        shaken = false;
        try
        {
            upstream = start(Key, line => FromServer(run, line), how => Ended(run, how));
        }
        catch (Exception e)
        {
            log($"[Hex] the shared MCP server {Key} couldn't be started, {e.Message}");
            FailWaiting($"The shared {Key.Name} server couldn't be started: {e.Message}");
            return;
        }

        Starts++;
        log($"[Hex] the shared MCP server {Key} started (process {upstream.Pid}), {clients.Count} client(s)");
        upstream.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = INIT_ID + run,
            ["method"] = "initialize",
            ["params"] = (initParams ?? Handshake(null)).DeepClone()
        }.ToJsonString(relaxed));
    }

    // What's waiting up, a few at a time once the server has shaken hands
    private void Pump()
    {
        while (shaken && upstream is not null && flights.Count < most && queued.First is { } first)
        {
            queued.RemoveFirst();
            Pending pending = first.Value;
            if (pending.Client.Gone)
                continue;

            long up = ++nextId;
            JsonObject message = pending.Message;
            message["id"] = up;
            string? token = null;
            if ((message["params"] as JsonObject)?["_meta"] is JsonObject meta && meta["progressToken"] is { } theirs)
            {
                token = PROGRESS + up;
                progress[token] = (pending.Client, theirs.DeepClone());
                meta["progressToken"] = token;
            }

            flights[up] = new Flight(pending.Client, pending.Id, Text(message["method"]) ?? "a call", token);
            upstream.Send(Line(message));
        }
    }

    private void FromServer(int run, string line)
    {
        JsonObject? message = Parse(line);
        if (message is null)
            return;
        lock (gate)
        {
            if (run != generation || upstream is null)
                return;

            string? method = Text(message["method"]);
            JsonNode? id = message["id"];
            if (method is null && id is not null)
                Answer(run, id, message);
            else if (method is not null && message.ContainsKey("id"))
                Asked(id?.DeepClone(), method);
            else if (method is not null)
                Told(method, message);
        }
    }

    private void Answer(int run, JsonNode id, JsonObject message)
    {
        if (Text(id) == INIT_ID + run)
        {
            if (message["result"] is JsonNode result)
            {
                initResult = result.DeepClone();
                shaken = true;
                failedStarts = 0;
                upstream!.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
                foreach (var (client, waiting) in waitingInit)
                {
                    client.Send(Result(waiting, result.DeepClone()));
                    client.Ready = true;
                }

                waitingInit.Clear();
                Pump();
            }
            else
            {
                string why = Text((message["error"] as JsonObject)?["message"]) ?? "it said no";
                log($"[Hex] the shared MCP server {Key} turned the handshake down, {why}");
                FailWaiting($"The shared {Key.Name} server turned the handshake down: {why}");
                Stop();
            }

            return;
        }

        if (id is not JsonValue number || !number.TryGetValue(out long up) || !flights.Remove(up, out Flight? flight))
            return; // Cancelled, or from a run before

        if (flight.ProgressToken is not null)
            progress.Remove(flight.ProgressToken);
        message["id"] = flight.Id?.DeepClone();
        flight.Client.Send(Line(message));
        Pump();
    }

    // The server asks the client, which is every agent and none, so Hex answers
    private void Asked(JsonNode? id, string method)
    {
        JsonObject answer = method switch
        {
            "roots/list" => ResultOf(id, new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = Key.RootUri, ["name"] = Path.GetFileName(Key.Root) })
            }),
            "ping" => ResultOf(id, new JsonObject()),
            _ => ErrorOf(id, -32601, $"Hex shares this server among several clients and doesn't pass {method} on to them.")
        };
        upstream!.Send(Line(answer));
    }

    private void Told(string method, JsonObject message)
    {
        if (method == "notifications/progress")
        {
            if (message["params"] is JsonObject said && Text(said["progressToken"]) is { } token && progress.TryGetValue(token, out var whose))
            {
                said["progressToken"] = whose.Token.DeepClone();
                whose.Client.Send(Line(message));
            }

            return;
        }

        // A request of its own it gave up on, Hex answered it already
        if (method == "notifications/cancelled")
            return;

        string line = Line(message);
        foreach (McpSharedClient client in clients)
        {
            if (client.Ready)
                client.Send(line);
        }
    }

    private void Ended(int run, string how)
    {
        lock (gate)
        {
            if (run != generation || upstream is null)
                return;

            bool wasShaken = shaken;
            log($"[Hex] the shared MCP server {Key} stopped, {how}");
            upstream.Dispose();
            upstream = null;
            shaken = false;
            generation++;

            foreach (Flight flight in flights.Values)
                flight.Client.Send(Error(flight.Id, SHARED_ERROR,
                    $"The shared {Key.Name} server stopped while {flight.Method} was going on ({how}). Make the call again, it's started again for it."));
            flights.Clear();
            progress.Clear();

            if (!wasShaken && ++failedStarts >= MOST_FAILED_STARTS)
            {
                FailWaiting($"The shared {Key.Name} server stopped {failedStarts} times before it was ready ({how}).");
                failedStarts = 0;
                return;
            }

            // Somebody is waiting for it, started again now. Otherwise when the next call comes
            if (waitingInit.Count > 0 || queued.Count > 0)
                Up();
        }
    }

    // Everybody waiting for the server told it isn't coming
    private void FailWaiting(string why)
    {
        foreach (var (client, id) in waitingInit)
            client.Send(Error(id, SHARED_ERROR, why));
        waitingInit.Clear();
        foreach (Pending pending in queued)
            pending.Client.Send(Error(pending.Id, SHARED_ERROR, why));
        queued.Clear();
    }

    private void Stop()
    {
        upstream?.Dispose();
        upstream = null;
        shaken = false;
        generation++;
        foreach (Flight flight in flights.Values)
            flight.Client.Send(Error(flight.Id, SHARED_ERROR, $"The shared {Key.Name} server was stopped while {flight.Method} was going on."));
        flights.Clear();
        progress.Clear();
    }

    private void Idle()
    {
        if (retire is not null)
            retire(this);
        else
            TryRetire();
    }

    /// <summary>Stopped and retired when it still has no agent, true when it was. The registry asks, holding its own lock.</summary>
    public bool TryRetire()
    {
        lock (gate)
        {
            if (clients.Count > 0 || Retired)
                return false;
            Retired = true;
            idleTimer?.Dispose();
            idleTimer = null;
            if (upstream is not null)
                log($"[Hex] the shared MCP server {Key} had no clients for {idle.TotalMinutes:0.#} minutes, stopped");
            Stop();
            return true;
        }
    }

    /// <summary>Stopped for good whoever it has, Hex going.</summary>
    public void Shutdown()
    {
        lock (gate)
        {
            Retired = true;
            idleTimer?.Dispose();
            idleTimer = null;
            FailWaiting($"Hex stopped the shared {Key.Name} server.");
            Stop();
        }
    }

    // The handshake said for the server, the first agent's with roots added (Hex answers roots/list), or one of Hex's
    // own when the first word an agent said wasn't initialize (it came back to a Hex restarted)
    private static JsonObject Handshake(JsonObject? theirs)
    {
        JsonObject said = theirs?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["clientInfo"] = new JsonObject { ["name"] = "hex-shared", ["version"] = "1.0" }
        };
        if (said["capabilities"] is not JsonObject capabilities)
            said["capabilities"] = capabilities = new JsonObject();
        capabilities["roots"] = new JsonObject { ["listChanged"] = false };
        // Hex answers what the server asks, and it asks nobody for a model or for the person
        capabilities.Remove("sampling");
        capabilities.Remove("elicitation");
        return said;
    }

    internal static JsonObject ResultOf(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    internal static JsonObject ErrorOf(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static string Result(JsonNode? id, JsonNode result) => Line(ResultOf(id, result));

    private static string Error(JsonNode? id, int code, string message) => Line(ErrorOf(id, code, message));

    // Not indented, so a message is a line, and what a server says in its text (<, >, &, a name in Cyrillic) kept as
    // it is rather than escaped, as Hex's own server writes
    private static readonly JsonSerializerOptions relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Line(JsonNode message) => message.ToJsonString(relaxed);

    private static string IdOf(JsonNode? id) => id?.ToJsonString() ?? "null";

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
