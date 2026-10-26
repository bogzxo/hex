using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

/// <summary>
/// An MCP client for the self test, on Hex's own pipe the way the bridge is, the token said at the door and JSON-RPC a
/// line at a time after it. Requests come back as tasks, the test waits for them by the tick while the editor runs the calls.
/// </summary>
internal sealed class HexMcpTestClient : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> waiting = new();
    private readonly Lock writing = new();
    private int nextId;

    private HexMcpTestClient(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        new Thread(Read) { IsBackground = true, Name = "mcp self test client" }.Start();
    }

    /// <summary>A client let in at a Hex's door, null with the reason when it wasn't.</summary>
    public static HexMcpTestClient? Connect(McpListing listing, string token, out string? why)
    {
        var pipe = new NamedPipeClientStream(".", listing.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        if (McpGate.Knock(pipe, token, out why))
            return new HexMcpTestClient(pipe);

        pipe.Dispose();
        return null;
    }

    public Task<JsonObject> Request(string method, JsonObject? parameters = null)
    {
        int id = Interlocked.Increment(ref nextId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = answer;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
        return answer.Task;
    }

    public void Notify(string method) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    /// <summary>A tool called, its result's object back (content and isError).</summary>
    public Task<JsonObject> Call(string tool, JsonObject? arguments = null) =>
        Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });

    /// <summary>The text items of a tool's answer as one.</summary>
    public static string TextOf(Task<JsonObject>? answer) =>
        answer is { IsCompletedSuccessfully: true } && answer.Result["result"]?["content"] is JsonArray content
            ? string.Join("\n", content.Select(item => item?["type"]?.GetValue<string>() == "text" ? item["text"]?.GetValue<string>() : null).OfType<string>())
            : string.Empty;

    /// <summary>Whether a tool's answer came back as the tool failing.</summary>
    public static bool Failed(Task<JsonObject>? answer) =>
        answer is not { IsCompletedSuccessfully: true } || answer.Result["result"]?["isError"]?.GetValue<bool>() == true;

    private void Send(JsonObject message)
    {
        byte[] line = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        lock (writing)
        {
            pipe.Write(line);
            pipe.Flush();
        }
    }

    private void Read()
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (JsonNode.Parse(line) is JsonObject answer && answer["id"] is JsonValue id && id.TryGetValue(out int number) && waiting.TryRemove(number, out var asked))
                    asked.TrySetResult(answer);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or System.Text.Json.JsonException)
        {
        }

        foreach (var (_, asked) in waiting)
            asked.TrySetException(new IOException("Hex hung up"));
    }

    public void Dispose() => pipe.Dispose();
}
