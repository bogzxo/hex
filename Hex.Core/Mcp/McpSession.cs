using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A client talking to an <see cref="McpServer"/> over a pair of streams, the way MCP's stdio transport frames it, a
/// message a line of JSON with no new lines inside it. A thread reads the lines, each request is answered on a task of
/// its own (a ping gets its answer while a screenshot is still being taken) and the answers go out under a lock in
/// whatever order they're done. It's over when the client hangs up or <see cref="Dispose"/> is called.
/// </summary>
internal sealed class McpSession : IDisposable
{
    private readonly TextReader input;
    private readonly Stream output;
    private readonly Lock writing = new();
    private readonly ManualResetEventSlim done = new();
    private int pending;
    private volatile bool closed;

    public McpServer Server { get; }

    /// <summary>Called once when the client goes, on the reading thread.</summary>
    public Action<McpSession>? Closed { get; set; }

    /// <param name="input">What the client writes, read a line at a time (already past the gate's line, see McpGate).</param>
    /// <param name="output">What goes back to the client.</param>
    public McpSession(TextReader input, Stream output, McpServer server)
    {
        this.input = input;
        this.output = output;
        Server = server;
    }

    /// <summary>Starts reading, once <see cref="Closed"/> is set, so a client that's gone at once still says so.</summary>
    public McpSession Start()
    {
        new Thread(Read) { IsBackground = true, Name = "mcp session" }.Start();
        return this;
    }

    /// <summary>Waits for the client to go, for no longer than a while, true when it went.</summary>
    public bool Wait(int milliseconds = Timeout.Infinite) => done.Wait(milliseconds);

    private void Read()
    {
        try
        {
            while (!closed && input.ReadLine() is { } line)
            {
                if (line.Length == 0 || line.All(char.IsWhiteSpace))
                    continue;

                Interlocked.Increment(ref pending);
                _ = Answer(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went, or was cut off
        }

        // What's being answered still goes out before the end, a client that wrote its last request and closed its side
        // waits for the answer on the other
        SpinWait.SpinUntil(() => Volatile.Read(ref pending) == 0, 30_000);
        Close();
    }

    private async Task Answer(string line)
    {
        try
        {
            if (await Server.HandleLine(line) is { } answer)
                Send(answer);
        }
        catch (Exception)
        {
            // An answer that couldn't go is the client's loss, the session goes on
        }
        finally
        {
            Interlocked.Decrement(ref pending);
        }
    }

    /// <summary>Writes a message as a line. From any thread.</summary>
    public void Send(JsonNode message)
    {
        if (closed)
            return;

        // Node by node, the serializer proper wants reflection, which a native Hex hasn't got. Not indented, so there's
        // never a new line inside a message, a new line in a string is written as \n
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            message.WriteTo(writer);

        lock (writing)
        {
            try
            {
                output.Write(buffer.WrittenSpan);
                output.WriteByte((byte)'\n');
                output.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                closed = true;
            }
        }
    }

    private int ended;

    // Once, whoever's listening told before anybody waiting is let go
    private void Close()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
            return;

        closed = true;
        Closed?.Invoke(this);
        done.Set();
    }

    public void Dispose()
    {
        closed = true;
        try { output.Dispose(); } catch (Exception) { }
        try { input.Dispose(); } catch (Exception) { }
        Close();
    }
}

/// <summary>
/// The door of Hex's MCP pipe. The first line a client says is <c>HEX-MCP token</c>, the token Hex wrote into its file
/// in the settings folder (McpDiscovery), and only one that says the right one gets in. It's told OK or NO in a line,
/// and the rest of the stream is MCP. Compared in constant time, for what little that matters on one machine.
/// </summary>
internal static class McpGate
{
    public const string HELLO = "HEX-MCP";
    public const string YES = "OK", NO = "NO";

    /// <summary>Whether the first line of a client is the right token, saying so to it. Reads only that line.</summary>
    public static bool Admit(TextReader reader, Stream output, string token)
    {
        string? line;
        try
        {
            line = reader.ReadLine();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }

        bool right = line is not null && line.StartsWith(HELLO + " ", StringComparison.Ordinal)
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(line[(HELLO.Length + 1)..].Trim()), Encoding.UTF8.GetBytes(token));

        try
        {
            output.Write(Encoding.ASCII.GetBytes((right ? YES : NO) + "\n"));
            output.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }

        return right;
    }

    /// <summary>The client's side of it, the token said and the answer read. False with the reason when it wasn't let in.</summary>
    public static bool Knock(Stream stream, string token, out string? why)
    {
        why = null;
        byte[] hello = Encoding.UTF8.GetBytes($"{HELLO} {token}\n");
        stream.Write(hello);
        stream.Flush();

        // A byte at a time, so nothing after the answer's line is read into a buffer that goes nowhere
        var answer = new StringBuilder();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                why = "Hex hung up before it answered.";
                return false;
            }

            if (b == '\n')
                break;
            answer.Append((char)b);
        }

        if (answer.ToString().Trim() == YES)
            return true;

        why = "Hex didn't take the token, its file in the settings folder may be from another run.";
        return false;
    }
}
