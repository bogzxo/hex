using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// Where chats for linked models go (docs/hex-link-plan.md, 5.7): the scene says which linked device a fingerprint is, and
/// every chat streamer and agent chat for a linked model sends through one HTTP client whose handler asks that device's
/// model over Link (<see cref="LinkChatHandler"/>), so the OpenAI parsing, the thinking split and the tool-call joining are
/// the same code a local llama-server is read with. The loopback endpoint (<see cref="LinkAiLoopback"/>) goes through the same handler.
/// </summary>
internal static class LinkAiRouter
{
    /// <summary>The address every linked chat is "sent" to, never connected to: the handler answers it.</summary>
    public static readonly Uri Api = new("http://link.ai/v1/chat/completions");

    /// <summary>The linked device a fingerprint names, null when it isn't linked now. Set by the scene.</summary>
    public static Func<string, HexLinkPeer?>? Find { get; set; }

    /// <summary>A chat is waiting behind others of this device on that device's machine: its name and its place (2 is next). Set by the scene.</summary>
    public static Action<string, int>? Queued { get; set; }

    /// <summary>Told when a device is asked for a chat and when it ended, for the status line: the device's name, the chat's model, and whether it began.</summary>
    public static Action<string, string, bool>? Used { get; set; }

    /// <summary>The client linked chats go through, no timeout of its own: each chat has its deadline.</summary>
    public static HttpClient Http { get; } = new(new LinkChatHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The sentence for why a request to a linked device failed, naming the device and what to do next.</summary>
    public static PredictionException Failure(HexLinkException error, string device)
    {
        string status = error.Code == HexLinkErrors.AI_FAILED ? HexLinkAiRequest.StatusOf(error) : string.Empty;
        if (error.Code == HexLinkErrors.NOT_ALLOWED)
            return new PredictionException(PredictionStatus.Refused, $"{device} hasn't let this device use its AI. Ask {device} to tick Use my AI models for it in Devices.");
        if (error.Code is HexLinkErrors.CLOSED or HexLinkErrors.TIMED_OUT)
            return new PredictionException(PredictionStatus.Unreachable, $"Lost the link to {device}. Reconnect it in Devices and try again, or pick a model on this machine.");
        return status switch
        {
            HexLinkAiRequest.REFUSED => new PredictionException(PredictionStatus.Refused, $"{device} stopped serving AI. Ask {device} to tick Use my AI models for this device in Devices (and keep its own AI on), or pick another model."),
            HexLinkAiRequest.BUSY => new PredictionException(PredictionStatus.Refused, $"{device} is busy ({error.Message}) Try again when one has finished."),
            HexLinkAiRequest.NO_MODEL => new PredictionException(PredictionStatus.NoSuchModel, $"{device} no longer has that model. Pick another in the model picker."),
            _ => new PredictionException(PredictionStatus.BadAnswer, $"{device} couldn't answer: {error.Message}"),
        };
    }
}

/// <summary>
/// An HTTP handler that doesn't touch the network: a chat completions request whose "model" is a linked model's id
/// (<see cref="LinkAiModelId"/>) is sent over Link to that device's model, and the chunks it streams back are returned as the
/// server-sent events a llama-server would have written. Failures are thrown as the <see cref="PredictionException"/> the
/// callers already show, with a sentence that says which device and what to do.
/// </summary>
internal sealed class LinkChatHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        string id;
        bool streaming;
        try
        {
            JsonObject? root = JsonNode.Parse(body) as JsonObject;
            id = root?["model"] is JsonValue value && value.TryGetValue(out string? said) ? said : string.Empty;
            streaming = root?["stream"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
        }
        catch (JsonException)
        {
            throw new PredictionException(PredictionStatus.BadAnswer, "The chat request wasn't JSON.");
        }

        if (!LinkAiModelId.TryParse(id, out string fingerprint, out string model))
            throw new PredictionException(PredictionStatus.NoSuchModel, $"{id} isn't a model on a linked device.");
        HexLinkPeer? peer = LinkAiRouter.Find?.Invoke(fingerprint);
        if (peer is null)
            throw new PredictionException(PredictionStatus.Unreachable, "That device isn't linked now. Link it in Devices, or pick a model on this machine.");
        string device = peer.Device.Name;
        if (!peer.ServesAi)
            throw new PredictionException(PredictionStatus.Refused, $"{device} stopped serving AI. Ask {device} to tick Use my AI models for this device in Devices, or pick another model.");

        HexLinkAiRequest asked;
        try
        {
            asked = new HexLinkAiRequest(model, body);
        }
        catch (HexLinkException e)
        {
            throw LinkAiRouter.Failure(e, device);
        }

        var stream = new LinkChunkStream(peer, asked, device, model);
        if (!streaming)
        {
            // A caller that wants the answer whole (Link Chat's @ai): the chunks joined into the one answer a server would have given
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = new List<string>();
                while (await reader.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
                    lines.Add(line);
                var whole = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LinkAiAnswer.Join(lines), Encoding.UTF8, "application/json"), RequestMessage = request };
                return whole;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream), RequestMessage = request };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }
}

/// <summary>The chunks of a streamed chat joined into the one chat completion a server gives when asked not to stream: text, reasoning, tool calls, why it ended.</summary>
internal static class LinkAiAnswer
{
    /// <summary>The server-sent event lines of a chat as its whole answer as JSON text. A chunk that is an error throws what it said.</summary>
    public static string Join(IEnumerable<string> lines)
    {
        var content = new StringBuilder();
        var thought = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        string finish = "stop", model = string.Empty;
        JsonNode? usage = null;
        foreach (string line in lines)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            if (data.Length == 0 || JsonNode.Parse(data) is not JsonObject chunk)
                continue;
            if (chunk["error"] is { } error)
                throw new PredictionException(PredictionStatus.BadAnswer, error is JsonObject own && own["message"] is JsonValue said && said.TryGetValue(out string? text) ? text : error.ToJsonString());

            model = chunk["model"] is JsonValue named && named.TryGetValue(out string? modelName) ? modelName : model;
            usage = chunk["usage"]?.DeepClone() ?? usage;
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                continue;
            if (choice["finish_reason"] is JsonValue reason && reason.TryGetValue(out string? why))
                finish = why;
            if (choice["delta"] is not JsonObject delta)
                continue;
            content.Append(delta["content"] is JsonValue part && part.TryGetValue(out string? piece) ? piece : string.Empty);
            thought.Append(delta["reasoning_content"] is JsonValue reasoning && reasoning.TryGetValue(out string? thinking) ? thinking : string.Empty);
            if (delta["tool_calls"] is not JsonArray pieces)
                continue;
            int position = 0;
            foreach (JsonNode? one in pieces)
            {
                int index = one is JsonObject given && given["index"] is JsonValue at && at.TryGetValue(out int number) ? number : position;
                position++;
                if (one is not JsonObject own)
                    continue;
                (string callId, string name, StringBuilder arguments) = calls.TryGetValue(index, out var have) ? have : (string.Empty, string.Empty, new StringBuilder());
                if (own["id"] is JsonValue idValue && idValue.TryGetValue(out string? idText) && callId.Length == 0)
                    callId = idText;
                if (own["function"] is JsonObject function)
                {
                    if (function["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? called) && name.Length == 0)
                        name = called;
                    if (function["arguments"] is JsonValue argumentsValue && argumentsValue.TryGetValue(out string? joined))
                        arguments.Append(joined);
                }

                calls[index] = (callId, name, arguments);
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
        if (thought.Length > 0)
            message["reasoning_content"] = thought.ToString();
        if (calls.Count > 0)
            message["tool_calls"] = new JsonArray([.. calls.Values.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id.Length > 0 ? call.Id : "call_" + Guid.NewGuid().ToString("N")[..8],
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() }
            })]);
        return new JsonObject
        {
            ["object"] = "chat.completion",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
            ["usage"] = usage
        }.ToJsonString();
    }
}

/// <summary>
/// The server-sent events of a chat over Link as a stream to read: each chunk the other machine's model streams is a
/// "data:" line, a comment line every few seconds while nothing comes (so a chat queued behind another isn't taken for a
/// dead one), "data: [DONE]" at the end. A chat that failed throws the sentence for it when read. Disposing it cancels the chat there.
/// </summary>
internal sealed class LinkChunkStream : Stream
{
    private static readonly TimeSpan KEEPALIVE = TimeSpan.FromSeconds(5);

    private readonly HexLinkPeer peer;
    private readonly string device;
    private readonly CancellationTokenSource source = new();
    private readonly IAsyncEnumerator<string> chunks;
    private Task<bool>? pending;
    private byte[] buffer = [];
    private int at;
    private bool finished;
    private int disposed;

    public LinkChunkStream(HexLinkPeer peer, HexLinkAiRequest request, string device, string model)
    {
        this.peer = peer;
        this.device = device;
        chunks = peer.Ai(request, place => LinkAiRouter.Queued?.Invoke(device, place), source.Token).GetAsyncEnumerator(source.Token);
        LinkAiRouter.Used?.Invoke(device, model, true);
        this.model = model;
    }

    private readonly string model;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancel = default)
    {
        while (at >= buffer.Length)
        {
            if (finished)
                return 0;
            buffer = await Next(cancel).ConfigureAwait(false);
            at = 0;
        }

        int count = Math.Min(destination.Length, buffer.Length - at);
        buffer.AsMemory(at, count).CopyTo(destination);
        at += count;
        return count;
    }

    // The next lines of the answer: a chunk, a keepalive, or the end
    private async Task<byte[]> Next(CancellationToken cancel)
    {
        using CancellationTokenRegistration registration = cancel.Register(() => { try { source.Cancel(); } catch (ObjectDisposedException) { } });
        pending ??= chunks.MoveNextAsync().AsTask();
        try
        {
            Task finishedFirst = await Task.WhenAny(pending, Task.Delay(KEEPALIVE, cancel)).ConfigureAwait(false);
            if (finishedFirst != pending)
                return Encoding.UTF8.GetBytes(": waiting\n\n");

            Task<bool> moved = pending;
            pending = null;
            if (!await moved.ConfigureAwait(false))
            {
                finished = true;
                return Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            }

            return Encoding.UTF8.GetBytes("data: " + chunks.Current.ReplaceLineEndings(" ") + "\n\n");
        }
        catch (HexLinkException e)
        {
            finished = true;
            throw LinkAiRouter.Failure(e, device);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // The other machine cancelled it (it stopped serving, or the right went)
            finished = true;
            throw new PredictionException(PredictionStatus.Refused, $"{device} stopped serving AI. Ask {device} to tick Use my AI models for this device in Devices (and keep its own AI on), or pick another model.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try { source.Cancel(); } catch (ObjectDisposedException) { }
            _ = Finish();
        }

        base.Dispose(disposing);
    }

    // The enumerator let go of off the reader's task: its end tells the other machine ai/cancel
    private async Task Finish()
    {
        try
        {
            if (pending is not null)
                await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // It was cancelled or failed, either way it's over
        }

        try { await chunks.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
        source.Dispose();
        LinkAiRouter.Used?.Invoke(device, model, false);
    }
}

/// <summary>A chat model on a linked device asked with a conversation, the answer streamed back a piece at a time.</summary>
internal sealed class LinkChatStreamer(string linkedModel, PredictionTuning tuning, int maxTokens) : IChatStreamer
{
    private readonly OpenAiChat inner = new(LinkAiRouter.Http, LinkAiRouter.Api, linkedModel, null) { Tuning = tuning, MaxTokens = maxTokens };

    public IAsyncEnumerable<ChatPiece> Stream(IReadOnlyList<ChatTurn> conversation, CancellationToken cancel) => inner.Stream(conversation, cancel);
}

/// <summary>A chat model on a linked device asked with a conversation and tools (the Local agent's), calls and thinking as from a local server.</summary>
internal sealed class LinkAgentChat(string linkedModel, PredictionTuning tuning, int maxTokens) : IAgentChat
{
    private readonly OpenAiAgentChat inner = new(LinkAiRouter.Http, LinkAiRouter.Api, linkedModel, null, tuning, maxTokens);

    public IAsyncEnumerable<AgentDelta> Chat(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel) =>
        inner.Chat(messages, tools, cancel);
}
