using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>A tool the local agent may call: its name, what it does, and its parameters as a JSON schema object (JSON text).</summary>
internal sealed record ToolSpec(string Name, string Description, string ParametersJson);

/// <summary>A tool call the model made: its id (the server's, or made up when it has none), the tool's name, its arguments as JSON text.</summary>
internal sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// A message of the agent's conversation: system, user, assistant (with the tool calls it made, if any) or tool (the
/// output of the call with CallId, and the tool's Name).
/// </summary>
internal sealed record AgentMessage(string Role, string Content, IReadOnlyList<ToolCall>? Calls = null, string? CallId = null, string? Name = null);

/// <summary>A piece of the model's streamed answer: text, thinking, a whole tool call, how many tokens it took, or its end.</summary>
internal abstract record AgentDelta
{
    public sealed record Text(string Value) : AgentDelta;
    public sealed record Thinking(string Value) : AgentDelta;
    public sealed record Call(ToolCall Value) : AgentDelta;
    public sealed record Usage(int PromptTokens, int OutputTokens, double Seconds) : AgentDelta;
    public sealed record Done(string Reason) : AgentDelta;
}

/// <summary>A chat model asked with a conversation and tools, its answer streamed back.</summary>
internal interface IAgentChat
{
    /// <summary>
    /// The answer as it comes. Tool calls come whole (a server streaming a call's arguments in pieces has them joined).
    /// When the server can't take tools natively, the tools are described in the system prompt and calls are read out of
    /// the text by <see cref="ToolCallText"/>. Throws <see cref="PredictionException"/>; a cancel stays a cancel.
    /// </summary>
    IAsyncEnumerable<AgentDelta> Chat(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel);
}

/// <summary>The agent chat for a kind of server.</summary>
internal static class AgentChats
{
    /// <summary>
    /// Ollama's /api/chat with tools (provider ollama) or OpenAI's /v1/chat/completions with tools (provider openai or
    /// llamacpp, any URL OpenAiChat.Url takes), the model named, with the tuning, the most tokens an answer may have and
    /// the context window (Ollama's num_ctx; 0 for the server's). Null for another provider or a URL that isn't one.
    /// </summary>
    public static IAgentChat? Make(string provider, string url, string model, string? key, PredictionTuning tuning, int maxTokens, int contextTokens) =>
        provider switch
        {
            PredictionProviders.LINKAI => LinkAiModelId.Is(model) ? (IAgentChat)new LinkAgentChat(model, tuning, maxTokens) : null,
            PredictionProviders.OLLAMA => OllamaChat.Url(url) is { } chat
                ? new OllamaAgentChat(PredictionProviders.Shared, chat, model, tuning, maxTokens, contextTokens)
                : null,
            PredictionProviders.OPENAI or PredictionProviders.LLAMACPP => OpenAiChat.Url(url) is { } completions
                ? new OpenAiAgentChat(PredictionProviders.Shared, completions, model, string.IsNullOrWhiteSpace(key) ? null : key, tuning, maxTokens)
                : null,
            _ => null,
        };
}

/// <summary>
/// What the two servers' agent chats share: a request streamed with the tools, and the way out when the server refuses
/// tools: asked again without them, the tools described in the system prompt and the calls read out of the answer's text.
/// Which servers and models refused is remembered, so later calls go straight to that way.
/// </summary>
internal abstract class ServerAgentChat(HttpClient http, Uri api, string model) : IAgentChat
{
    // The servers and models that refused tools, by address and name
    private static readonly ConcurrentDictionary<string, bool> refused = new();

    // The numbers calls without an id are given, one sequence for the whole run so that ids of different turns differ
    private static int made;

    /// <summary>The client requests are sent with.</summary>
    protected HttpClient Http => http;

    /// <summary>The address requests go to.</summary>
    protected Uri Api => api;

    /// <summary>The model's name.</summary>
    protected string Model => model;

    /// <summary>An id for a call the server gave none.</summary>
    protected static string NewId() => "call_" + Interlocked.Increment(ref made);

    public async IAsyncEnumerable<AgentDelta> Chat(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, [EnumeratorCancellation] CancellationToken cancel)
    {
        string known = api + "|" + model;
        if (tools.Count == 0)
        {
            await foreach (AgentDelta delta in Stream(messages, tools, cancel).ConfigureAwait(false))
                yield return delta;
            yield break;
        }

        bool fallback = refused.ContainsKey(known);
        if (!fallback)
        {
            await using IAsyncEnumerator<AgentDelta> deltas = Stream(messages, tools, cancel).GetAsyncEnumerator(cancel);
            bool started = false;
            while (true)
            {
                bool more;
                try
                {
                    more = await deltas.MoveNextAsync().ConfigureAwait(false);
                }
                catch (PredictionException e) when (!started && e.Message.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    refused[known] = true;
                    fallback = true;
                    break;
                }

                if (!more)
                    yield break;
                started = true;
                yield return deltas.Current;
            }
        }

        // The server takes no tools: they are in the prompt and the calls are in the text
        var text = new StringBuilder();
        AgentDelta? done = null;
        await foreach (AgentDelta delta in Stream(Plain(messages, tools), [], cancel).ConfigureAwait(false))
        {
            switch (delta)
            {
                case AgentDelta.Text said:
                    text.Append(said.Value);
                    break;
                case AgentDelta.Done:
                    done = delta;
                    break;
                default:
                    yield return delta;
                    break;
            }
        }

        List<ToolCall> calls = ToolCallText.Find(text.ToString(), out string rest);
        if (rest.Length > 0)
            yield return new AgentDelta.Text(rest);
        foreach (ToolCall call in calls)
            yield return new AgentDelta.Call(call);
        yield return done ?? new AgentDelta.Done("stop");
    }

    /// <summary>One request and its answer as it comes, with the tools natively when there are any.</summary>
    protected abstract IAsyncEnumerable<AgentDelta> Stream(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel);

    // The conversation for a server without tools: the tools described in the system message, earlier calls and their
    // results written as text
    private static List<AgentMessage> Plain(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools)
    {
        string described = ToolCallText.Describe(tools);
        var plain = new List<AgentMessage>();
        bool system = false;
        foreach (AgentMessage message in messages)
        {
            if (message.Role == "system" && !system)
            {
                system = true;
                plain.Add(message with { Content = message.Content.Length > 0 ? message.Content + "\n\n" + described : described });
            }
            else if (message.Role == "assistant" && message.Calls is { Count: > 0 } calls)
            {
                var written = new StringBuilder(message.Content);
                foreach (ToolCall call in calls)
                {
                    if (written.Length > 0)
                        written.Append('\n');
                    written.Append("<tool_call>\n{\"name\": ").Append(PlainJson.Quote(call.Name))
                        .Append(", \"arguments\": ").Append(call.ArgumentsJson).Append("}\n</tool_call>");
                }
                plain.Add(new AgentMessage("assistant", written.ToString()));
            }
            else if (message.Role == "tool")
                plain.Add(new AgentMessage("user", $"Result of {message.Name ?? "the tool"}:\n{message.Content}"));
            else
                plain.Add(message with { Calls = null });
        }

        if (!system)
            plain.Insert(0, new AgentMessage("system", described));
        return plain;
    }

    /// <summary>The pieces of the stream as deltas, the thinking ones marked.</summary>
    protected static AgentDelta Delta(ChatPiece piece) =>
        piece.Thinking ? new AgentDelta.Thinking(piece.Text) : new AgentDelta.Text(piece.Text);

    /// <summary>Arguments as JSON text written into the writer as the value of a property, an object when they are one.</summary>
    protected static void WriteArguments(Utf8JsonWriter writer, string arguments)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(arguments);
            json.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}

// A string as JSON text without reflection, for the one place a name is written into text
internal static class PlainJson
{
    internal static string Quote(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>A tool call whose pieces are still coming: its id and name, its arguments so far.</summary>
internal sealed class PartialCall
{
    public string Id = "";
    public string Name = "";
    public readonly StringBuilder Arguments = new();
}

/// <summary>An OpenAI-compatible server's chat completions with tools: llama-server with --jinja, LM Studio, vLLM.</summary>
internal sealed class OpenAiAgentChat(HttpClient server, Uri address, string name, string? key, PredictionTuning tuning, int maxTokens)
    : ServerAgentChat(server, address, name)
{
    protected override async IAsyncEnumerable<AgentDelta> Stream(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, [EnumeratorCancellation] CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, messages, tools)),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan wait = PredictionHttp.Loading;
        deadline.CancelAfter(wait);
        using HttpResponseMessage response = await PredictionHttp.Send(Http, message, deadline, cancel, Api, key).ConfigureAwait(false);
        using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, Api).ConfigureAwait(false);

        var splitter = new ThinkSplitter();
        var pieces = new List<ChatPiece>(2);
        var calls = new SortedDictionary<int, PartialCall>();
        string? reason = null;
        int prompt = 0, output = 0;
        double seconds = 0;
        while (await PredictionHttp.Line(reader, deadline, wait, cancel, Api).ConfigureAwait(false))
        {
            wait = PredictionHttp.FirstPiece;
            if (!WireLines.Data(reader.Current, out ReadOnlyMemory<byte> data))
                continue;
            if (WireLines.IsDone(data))
                break;

            string? thought = null, content = null;
            using (JsonDocument document = PredictionHttp.Parse(data, Api))
            {
                JsonElement root = document.RootElement;
                if (PredictionHttp.Error(root) is { } error)
                    throw PredictionHttp.Failed(error, Api, key);
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object)
                {
                    JsonElement choice = choices[0];
                    if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        thought = delta.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String ? reasoning.GetString() : null;
                        content = delta.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                        if (delta.TryGetProperty("tool_calls", out JsonElement made) && made.ValueKind == JsonValueKind.Array)
                            Accumulate(calls, made);
                    }
                    if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                        reason = finish.GetString();
                }

                if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    prompt = Int(usage, "prompt_tokens", prompt);
                    output = Int(usage, "completion_tokens", output);
                }
                if (root.TryGetProperty("timings", out JsonElement timings) && timings.ValueKind == JsonValueKind.Object)
                {
                    prompt = Int(timings, "prompt_n", prompt);
                    output = Int(timings, "predicted_n", output);
                    if (timings.TryGetProperty("predicted_ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number)
                        seconds = ms.GetDouble() / 1000;
                }
            }

            if (!string.IsNullOrEmpty(thought))
                yield return new AgentDelta.Thinking(thought);
            if (content is not null)
            {
                pieces.Clear();
                splitter.Feed(content, pieces);
                foreach (ChatPiece piece in pieces)
                    yield return Delta(piece);
            }
        }

        pieces.Clear();
        splitter.End(pieces);
        foreach (ChatPiece piece in pieces)
            yield return Delta(piece);
        foreach (PartialCall call in calls.Values.Where(call => call.Name.Length > 0))
            yield return new AgentDelta.Call(new ToolCall(
                call.Id.Length > 0 ? call.Id : NewId(), call.Name, call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}"));
        if (prompt > 0 || output > 0)
            yield return new AgentDelta.Usage(prompt, output, seconds);
        yield return new AgentDelta.Done(reason ?? "stop");
    }

    // The pieces of tool calls a delta carries joined to the calls they belong to, by their index
    private static void Accumulate(SortedDictionary<int, PartialCall> calls, JsonElement pieces)
    {
        int position = 0;
        foreach (JsonElement piece in pieces.EnumerateArray())
        {
            int index = position++;
            if (piece.ValueKind != JsonValueKind.Object)
                continue;
            if (piece.TryGetProperty("index", out JsonElement at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt32(out int number))
                index = number;
            if (!calls.TryGetValue(index, out PartialCall? call))
                calls[index] = call = new PartialCall();

            if (piece.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && call.Id.Length == 0)
                call.Id = id.GetString() ?? "";
            if (piece.TryGetProperty("function", out JsonElement function) && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String && call.Name.Length == 0)
                    call.Name = name.GetString() ?? "";
                if (function.TryGetProperty("arguments", out JsonElement arguments))
                {
                    if (arguments.ValueKind == JsonValueKind.String)
                        call.Arguments.Append(arguments.GetString());
                    else if (arguments.ValueKind == JsonValueKind.Object)
                        call.Arguments.Append(arguments.GetRawText());
                }
            }
        }
    }

    private static int Int(JsonElement from, string name, int otherwise) =>
        from.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : otherwise;

    // {model, messages, tools?, tool_choice?, stream, max_tokens, temperature, top_p?, top_k?, repeat_penalty?, chat_template_kwargs?}
    private void Body(Utf8JsonWriter writer, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools)
    {
        writer.WriteStartObject();
        writer.WriteString("model", Model);
        writer.WriteStartArray("messages");
        foreach (AgentMessage turn in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", turn.Role);
            writer.WriteString("content", turn.Content);
            if (turn.Role == "tool" && turn.CallId is not null)
                writer.WriteString("tool_call_id", turn.CallId);
            if (turn.Role == "assistant" && turn.Calls is { Count: > 0 } calls)
            {
                writer.WriteStartArray("tool_calls");
                foreach (ToolCall call in calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", call.Id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (tools.Count > 0)
        {
            AgentTools.Write(writer, tools);
            writer.WriteString("tool_choice", "auto");
        }
        writer.WriteBoolean("stream", true);
        writer.WriteNumber("max_tokens", maxTokens);
        writer.WriteNumber("temperature", tuning.Temperature);
        if (tuning.TopP > 0)
            writer.WriteNumber("top_p", tuning.TopP);
        if (tuning.TopK > 0)
            writer.WriteNumber("top_k", tuning.TopK);
        if (tuning.RepeatPenalty > 0)
            writer.WriteNumber("repeat_penalty", tuning.RepeatPenalty);
        if (tuning.Thinking != PredictionTuning.THINK_MODEL)
        {
            writer.WriteStartObject("chat_template_kwargs");
            writer.WriteBoolean("enable_thinking", tuning.Thinking != PredictionTuning.THINK_OFF);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

/// <summary>Ollama's /api/chat with tools, the answer streamed a line at a time.</summary>
internal sealed class OllamaAgentChat(HttpClient server, Uri address, string name, PredictionTuning tuning, int maxTokens, int contextTokens)
    : ServerAgentChat(server, address, name)
{
    protected override async IAsyncEnumerable<AgentDelta> Stream(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, [EnumeratorCancellation] CancellationToken cancel)
    {
        object? think = await OllamaChat.Think(Http, Api, Model, tuning.Thinking, cancel).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, Api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, messages, tools, think)),
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan wait = PredictionHttp.Loading;
        deadline.CancelAfter(wait);
        using HttpResponseMessage response = await PredictionHttp.Send(Http, message, deadline, cancel, Api, null).ConfigureAwait(false);
        using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, Api).ConfigureAwait(false);

        var splitter = new ThinkSplitter();
        var pieces = new List<ChatPiece>(2);
        var calls = new List<ToolCall>();
        string reason = "stop";
        int prompt = 0, output = 0;
        double seconds = 0;
        while (await PredictionHttp.Line(reader, deadline, wait, cancel, Api).ConfigureAwait(false))
        {
            wait = PredictionHttp.FirstPiece;
            ReadOnlyMemory<byte> line = reader.Current;
            if (line.Length == 0)
                continue;

            string? thought = null, content = null;
            bool done = false;
            using (JsonDocument document = PredictionHttp.Parse(line, Api))
            {
                JsonElement root = document.RootElement;
                if (PredictionHttp.Error(root) is { } error)
                    throw PredictionHttp.Failed(error, Api, null);
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (root.TryGetProperty("message", out JsonElement said) && said.ValueKind == JsonValueKind.Object)
                {
                    thought = said.TryGetProperty("thinking", out JsonElement thinking) && thinking.ValueKind == JsonValueKind.String ? thinking.GetString() : null;
                    content = said.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                    if (said.TryGetProperty("tool_calls", out JsonElement made) && made.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement item in made.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("function", out JsonElement function) && function.ValueKind == JsonValueKind.Object
                                && function.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } called)
                            {
                                string arguments = function.TryGetProperty("arguments", out JsonElement given)
                                    ? given.ValueKind == JsonValueKind.String ? given.GetString() ?? "{}" : given.GetRawText()
                                    : "{}";
                                string id = item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String && idElement.GetString() is { Length: > 0 } given1
                                    ? given1
                                    : NewId();
                                calls.Add(new ToolCall(id, called, arguments));
                            }
                }

                done = root.TryGetProperty("done", out JsonElement doneElement) && doneElement.ValueKind == JsonValueKind.True;
                if (done)
                {
                    if (root.TryGetProperty("done_reason", out JsonElement why) && why.ValueKind == JsonValueKind.String)
                        reason = why.GetString() ?? "stop";
                    if (root.TryGetProperty("prompt_eval_count", out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int promptCount))
                        prompt = promptCount;
                    if (root.TryGetProperty("eval_count", out JsonElement o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out int outputCount))
                        output = outputCount;
                    if (root.TryGetProperty("eval_duration", out JsonElement ns) && ns.ValueKind == JsonValueKind.Number)
                        seconds = ns.GetDouble() / 1e9;
                }
            }

            if (!string.IsNullOrEmpty(thought))
                yield return new AgentDelta.Thinking(thought);
            if (content is not null)
            {
                pieces.Clear();
                splitter.Feed(content, pieces);
                foreach (ChatPiece piece in pieces)
                    yield return Delta(piece);
            }
            if (done)
                break;
        }

        pieces.Clear();
        splitter.End(pieces);
        foreach (ChatPiece piece in pieces)
            yield return Delta(piece);
        foreach (ToolCall call in calls)
            yield return new AgentDelta.Call(call);
        if (prompt > 0 || output > 0)
            yield return new AgentDelta.Usage(prompt, output, seconds);
        yield return new AgentDelta.Done(reason);
    }

    // {model, messages, tools?, stream, keep_alive, think?, options: {num_predict, temperature, top_p, top_k, num_ctx, repeat_penalty}}
    private void Body(Utf8JsonWriter writer, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, object? think)
    {
        writer.WriteStartObject();
        writer.WriteString("model", Model);
        writer.WriteStartArray("messages");
        foreach (AgentMessage turn in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", turn.Role);
            writer.WriteString("content", turn.Content);
            if (turn.Role == "tool" && turn.Name is not null)
                writer.WriteString("tool_name", turn.Name);
            if (turn.Role == "assistant" && turn.Calls is { Count: > 0 } calls)
            {
                writer.WriteStartArray("tool_calls");
                foreach (ToolCall call in calls)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("arguments");
                    WriteArguments(writer, call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (tools.Count > 0)
            AgentTools.Write(writer, tools);
        writer.WriteBoolean("stream", true);
        writer.WriteString("keep_alive", "30m");
        switch (think)
        {
            case bool on:
                writer.WriteBoolean("think", on);
                break;
            case string level:
                writer.WriteString("think", level);
                break;
        }
        writer.WriteStartObject("options");
        writer.WriteNumber("num_predict", maxTokens);
        (contextTokens > 0 ? tuning with { Context = contextTokens } : tuning).WriteOllamaOptions(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

// The tools as the "tools" property both APIs take: [{type: function, function: {name, description, parameters}}]
internal static class AgentTools
{
    public static void Write(Utf8JsonWriter writer, IReadOnlyList<ToolSpec> tools)
    {
        writer.WriteStartArray("tools");
        foreach (ToolSpec tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            try
            {
                using JsonDocument schema = JsonDocument.Parse(tool.ParametersJson);
                schema.WriteTo(writer);
            }
            catch (JsonException)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>Tool calls written in the text by a model that doesn't make them natively.</summary>
internal static partial class ToolCallText
{
    /// <summary>How the tools and the way to call them are described in the system prompt for such a model.</summary>
    public static string Describe(IReadOnlyList<ToolSpec> tools)
    {
        var text = new StringBuilder();
        text.Append("You can call tools. To call one, answer with exactly:\n<tool_call>\n{\"name\": \"<tool>\", \"arguments\": {...}}\n</tool_call>\n");
        text.Append("One call per answer, then wait for its result. Tools:");
        foreach (ToolSpec tool in tools)
            text.Append("\n- ").Append(tool.Name).Append(": ").Append(FirstSentence(tool.Description)).Append(" Parameters: ").Append(Compact(tool.ParametersJson));
        return text.ToString();
    }

    /// <summary>
    /// The calls in a finished answer's text (&lt;tool_call&gt;{"name":..,"arguments":{..}}&lt;/tool_call&gt;, Qwen's and
    /// Hermes' way, or a fenced json block holding name and arguments), and the text without them.
    /// </summary>
    public static List<ToolCall> Find(string text, out string rest)
    {
        var calls = new List<ToolCall>();
        string found = Tagged().Replace(text, block => Read(block.Groups[1].Value, calls) ? string.Empty : block.Value);
        if (calls.Count == 0)
            found = Fenced().Replace(found, block => Read(block.Groups[1].Value, calls) ? string.Empty : block.Value);
        rest = found.Trim();
        return calls;
    }

    // Adds the call the JSON text holds to the calls; false when it isn't one
    private static bool Read(string json, List<ToolCall> calls)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.Trim());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("function", out JsonElement wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;
            if (!root.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(name.GetString()))
                return false;
            if (!root.TryGetProperty("arguments", out JsonElement arguments) && !root.TryGetProperty("parameters", out arguments))
                return false;

            string written;
            if (arguments.ValueKind == JsonValueKind.Object)
                written = arguments.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.String)
            {
                using JsonDocument inner = JsonDocument.Parse(arguments.GetString() ?? "");
                if (inner.RootElement.ValueKind != JsonValueKind.Object)
                    return false;
                written = inner.RootElement.GetRawText();
            }
            else
                return false;

            calls.Add(new ToolCall("call_" + (calls.Count + 1), name.GetString()!, written));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FirstSentence(string description)
    {
        string one = description.ReplaceLineEndings(" ").Trim();
        int end = one.IndexOf(". ", StringComparison.Ordinal);
        return end >= 0 ? one[..(end + 1)] : one;
    }

    private static string Compact(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                document.WriteTo(writer);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    [GeneratedRegex(@"<tool_call>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Tagged();

    [GeneratedRegex(@"```(?:json)?[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fenced();
}
