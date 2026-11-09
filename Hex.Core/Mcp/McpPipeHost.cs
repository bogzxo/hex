using System.IO.Pipes;
using System.Text;

namespace Hex.Core;

/// <summary>
/// Hex's MCP server on a pipe of its own, for <c>Hex --mcp</c> to bridge a client's stdin and stdout to. Only this machine
/// and only this user can open it (the pipe is made for the current user, Windows turns away remote clients of such a
/// pipe and a Unix socket is a file in the user's temp folder), and only with the token (McpGate). A thread waits for
/// clients, each one let in gets a session and a server of its own, a few at once at most.
/// </summary>
internal sealed class McpPipeHost : IDisposable
{
    // How many clients at once, more wait for one to go
    private const int MOST = 32;

    // How long a client has to say the token
    private const int ADMIT_MS = 5000;

    private readonly Func<McpServer> makeServer;
    private readonly string token;
    private readonly CancellationTokenSource stop = new();
    private readonly List<McpSession> sessions = [];
    private readonly SemaphoreSlim room = new(MOST, MOST);

    /// <summary>The pipe it listens on.</summary>
    public string Pipe { get; }

    /// <summary>Called when a client comes, goes or says who it is, on whichever thread that happened on.</summary>
    public Action? Changed { get; set; }

    /// <param name="makeServer">A server for each client, whose tools are the editor's.</param>
    public McpPipeHost(string pipe, string token, Func<McpServer> makeServer)
    {
        Pipe = pipe;
        this.token = token;
        this.makeServer = makeServer;
        new Thread(Listen) { IsBackground = true, Name = "mcp pipe" }.Start();
    }

    /// <summary>The clients connected and past the door, by who they said they are (or "connecting" before they've said).</summary>
    public IReadOnlyList<string> Clients
    {
        get
        {
            lock (sessions)
                return [.. sessions.Select(session => session.Server.Client ?? "connecting")];
        }
    }

    private void Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                room.Wait(stop.Token);
                pipe = new NamedPipeServerStream(Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult();
                NamedPipeServerStream taken = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(taken));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception e)
            {
                // A pipe that couldn't be made, a moment and another go
                pipe?.Dispose();
                room.Release();
                Console.WriteLine($"[Hex] the MCP pipe had a problem, {e.Message}");
                if (stop.Token.WaitHandle.WaitOne(1000))
                    return;
            }
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        // A client that opens the pipe and says nothing is shown the door after a while, it was holding a place
        var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        Task<bool> admitted = Task.Run(() => McpGate.Admit(reader, pipe, token));
        if (!admitted.Wait(ADMIT_MS) || !admitted.Result)
        {
            pipe.Dispose();
            room.Release();
            return;
        }

        McpServer server = makeServer();
        server.Initialized = _ => Changed?.Invoke();
        var session = new McpSession(reader, pipe, server);
        session.Closed = _ =>
        {
            lock (sessions)
                sessions.Remove(session);
            try { pipe.Dispose(); } catch (Exception) { }
            room.Release();
            Changed?.Invoke();
        };

        lock (sessions)
            sessions.Add(session);
        session.Start();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        stop.Cancel();
        McpSession[] open;
        lock (sessions)
            open = [.. sessions];
        foreach (McpSession session in open)
            session.Dispose();
    }
}
