using System.Net;
using System.Text;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>The local agent's chat with tools: native tool calls from both servers, the text fallback, and the calls found in text.</summary>
public sealed class LocalAgentChatTests
{
    private static readonly PredictionTuning model = PredictionTuning.Usual with { Thinking = PredictionTuning.THINK_MODEL };

    private static readonly IReadOnlyList<ToolSpec> tools =
    [
        new ToolSpec("read_file", "Reads a file. Returns its text.", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
    ];

    private static readonly IReadOnlyList<AgentMessage> history =
    [
        new AgentMessage("system", "You are an agent."),
        new AgentMessage("user", "read a.txt"),
        new AgentMessage("assistant", "", [new ToolCall("call_9", "read_file", "{\"path\":\"a.txt\"}")]),
        new AgentMessage("tool", "hello", CallId: "call_9", Name: "read_file"),
    ];

    private static IAgentChat OpenAi(FakeServer server, int maxTokens = 321) =>
        new OpenAiAgentChat(new HttpClient(server), OpenAiChat.Url("http://localhost:8080/v1")!, "gpt-" + Guid.NewGuid(), null, model, maxTokens);

    private static IAgentChat Ollama(FakeServer server, int context = 0) =>
        new OllamaAgentChat(new HttpClient(server), OllamaChat.Url("http://localhost:11434")!, "qwen3:" + Guid.NewGuid(), model, 500, context);

    private static async Task<List<AgentDelta>> All(IAgentChat chat, IReadOnlyList<AgentMessage>? messages = null, IReadOnlyList<ToolSpec>? with = null)
    {
        var deltas = new List<AgentDelta>();
        await foreach (AgentDelta delta in chat.Chat(messages ?? history, with ?? tools, CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private static string Sse(string json) => "data: " + json + "\n\n";

    [Fact]
    public async Task OpenAiJoinsToolCallPiecesByIndex()
    {
        var server = FakeServer.Streaming(new PieceStream([
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"c2\",\"function\":{\"name\":\"list\",\"arguments\":\"{}\"}}]}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"a\\\"}\"}}]}}]}"),
            Sse("{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}"),
            "data: [DONE]\n\n",
        ]));

        List<AgentDelta> deltas = await All(OpenAi(server));

        ToolCall[] calls = deltas.OfType<AgentDelta.Call>().Select(call => call.Value).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(new ToolCall("c1", "read_file", "{\"path\":\"a\"}"), calls[0]);
        Assert.Equal(new ToolCall("c2", "list", "{}"), calls[1]);
        Assert.Equal(new AgentDelta.Done("tool_calls"), deltas[^1]);
    }

    [Fact]
    public async Task OpenAiBodyHasToolsAndMapsToolMessages()
    {
        var server = FakeServer.Streaming(new PieceStream([Sse("{\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")]));

        await All(OpenAi(server));

        using JsonDocument body = JsonDocument.Parse(server.Sent[0].Body);
        JsonElement root = body.RootElement;
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(321, root.GetProperty("max_tokens").GetInt32());
        JsonElement function = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("read_file", function.GetProperty("name").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());

        JsonElement messages = root.GetProperty("messages");
        JsonElement assistant = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call_9", assistant.GetProperty("id").GetString());
        Assert.Equal("{\"path\":\"a.txt\"}", assistant.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_9", messages[3].GetProperty("tool_call_id").GetString());
        Assert.EndsWith("/v1/chat/completions", server.Sent[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task OpenAiStreamsReasoningThinkBlocksAndUsage()
    {
        var server = FakeServer.Streaming(new PieceStream([
            Sse("{\"choices\":[{\"delta\":{\"reasoning_content\":\"hmm\"}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"content\":\"<think>plan</think>Answer\"}}]}"),
            Sse("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"),
            Sse("{\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5},\"timings\":{\"predicted_ms\":500}}"),
            "data: [DONE]\n\n",
        ]));

        List<AgentDelta> deltas = await All(OpenAi(server));

        Assert.Equal("hmmplan", string.Concat(deltas.OfType<AgentDelta.Thinking>().Select(thought => thought.Value)));
        Assert.Equal("Answer", string.Concat(deltas.OfType<AgentDelta.Text>().Select(text => text.Value)));
        Assert.Contains(new AgentDelta.Usage(12, 5, 0.5), deltas);
        Assert.Equal(new AgentDelta.Done("stop"), deltas[^1]);
    }

    [Fact]
    public async Task OpenAiUsageFromLlamaServerTimings()
    {
        var server = FakeServer.Streaming(new PieceStream([
            Sse("{\"choices\":[{\"delta\":{\"content\":\"x\"},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":7,\"predicted_n\":3,\"predicted_ms\":1500}}"),
        ]));

        List<AgentDelta> deltas = await All(OpenAi(server));

        Assert.Contains(new AgentDelta.Usage(7, 3, 1.5), deltas);
    }

    [Fact]
    public async Task OllamaToolCallObjectArgumentsBecomeJsonText()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}}]},\"done\":false}\n",
            "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":20,\"eval_count\":4,\"eval_duration\":2000000000}\n",
        ]));

        List<AgentDelta> deltas = await All(Ollama(server));

        ToolCall call = Assert.Single(deltas.OfType<AgentDelta.Call>()).Value;
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", call.ArgumentsJson);
        Assert.False(string.IsNullOrEmpty(call.Id));
        Assert.Contains(new AgentDelta.Usage(20, 4, 2), deltas);
        Assert.Equal(new AgentDelta.Done("stop"), deltas[^1]);
    }

    [Fact]
    public async Task OllamaBodyHasToolsToolNameAndContext()
    {
        var server = FakeServer.Streaming(new PieceStream(["{\"message\":{\"content\":\"hi\"},\"done\":true}\n"]));

        await All(Ollama(server, context: 8192));

        using JsonDocument body = JsonDocument.Parse(server.Sent[0].Body);
        JsonElement root = body.RootElement;
        Assert.Equal("read_file", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(8192, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(500, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("30m", root.GetProperty("keep_alive").GetString());
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal("a.txt", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal("read_file", messages[3].GetProperty("tool_name").GetString());
        Assert.EndsWith("/api/chat", server.Sent[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RefusedToolsAreRetriedWithoutThemAndCallsReadFromText()
    {
        int asked = 0;
        var server = new FakeServer(_ =>
        {
            asked++;
            return Task.FromResult(asked == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"registry.ollama.ai/x does not support tools\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PieceStream([
                        "{\"message\":{\"content\":\"Looking.\\n<tool_call>\\n{\\\"name\\\":\\\"read_file\\\",\\\"arguments\\\":{\\\"path\\\":\\\"a.txt\\\"}}\\n</tool_call>\"},\"done\":false}\n",
                        "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n",
                    ])),
                });
        });
        IAgentChat chat = Ollama(server);

        List<AgentDelta> deltas = await All(chat);

        Assert.Equal(2, server.Sent.Count);
        Assert.Contains("\"tools\"", server.Sent[0].Body);
        Assert.DoesNotContain("\"tools\"", server.Sent[1].Body);
        // The JSON writer escapes '<' in the body; what the model reads is the decoded text
        string said = string.Join("\n", System.Text.Json.JsonDocument.Parse(server.Sent[1].Body).RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("content").GetString()));
        Assert.Contains("<tool_call>", said);
        Assert.Contains("read_file", said);
        Assert.Equal("Looking.", string.Concat(deltas.OfType<AgentDelta.Text>().Select(text => text.Value)));
        ToolCall call = Assert.Single(deltas.OfType<AgentDelta.Call>()).Value;
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", call.ArgumentsJson);
        Assert.Equal(new AgentDelta.Done("stop"), deltas[^1]);

        // Remembered: the next ask goes straight to the text way
        server.Sent.Clear();
        asked = 1;
        await All(chat);
        Assert.Single(server.Sent);
        Assert.DoesNotContain("\"tools\"", server.Sent[0].Body);
    }

    [Fact]
    public void FindReadsTagForm()
    {
        List<ToolCall> calls = ToolCallText.Find("Sure.\n<tool_call>\n{\"name\":\"a\",\"arguments\":{\"x\":1}}\n</tool_call>\n<tool_call>{\"name\":\"b\",\"arguments\":{}}</tool_call>", out string rest);

        Assert.Equal(2, calls.Count);
        Assert.Equal(new ToolCall("call_1", "a", "{\"x\":1}"), calls[0]);
        Assert.Equal("call_2", calls[1].Id);
        Assert.Equal("Sure.", rest);
    }

    [Fact]
    public void FindReadsFencedJson()
    {
        List<ToolCall> calls = Find("Here:\n```json\n{\"name\":\"a\",\"arguments\":{\"x\":1}}\n```\nDone", out string rest);

        Assert.Equal("a", Assert.Single(calls).Name);
        Assert.Equal("Here:\n\nDone", rest);
    }

    [Fact]
    public void FindReadsFunctionWrapperAndStringArguments()
    {
        List<ToolCall> calls = Find("<tool_call>{\"function\":{\"name\":\"a\",\"arguments\":\"{\\\"x\\\":2}\"}}</tool_call>", out string rest);

        ToolCall call = Assert.Single(calls);
        Assert.Equal("a", call.Name);
        Assert.Equal("{\"x\":2}", call.ArgumentsJson);
        Assert.Equal("", rest);
    }

    [Fact]
    public void FindLeavesInvalidJsonInRest()
    {
        List<ToolCall> calls = Find("<tool_call>{not json}</tool_call>", out string rest);

        Assert.Empty(calls);
        Assert.Equal("<tool_call>{not json}</tool_call>", rest);
    }

    [Fact]
    public void DescribeNamesToolsAndTheCallForm()
    {
        string described = ToolCallText.Describe(tools);

        Assert.Contains("<tool_call>", described);
        Assert.Contains("read_file: Reads a file.", described);
        Assert.DoesNotContain("Returns its text", described);
        Assert.Contains("{\"type\":\"object\"", described);
    }

    [Fact]
    public void MakeChoosesByProvider()
    {
        Assert.IsType<OllamaAgentChat>(AgentChats.Make("ollama", "http://localhost:11434", "m", null, model, 100, 0));
        Assert.IsType<OpenAiAgentChat>(AgentChats.Make("openai", "http://localhost:8080/v1", "m", "k", model, 100, 0));
        Assert.IsType<OpenAiAgentChat>(AgentChats.Make("llamacpp", "http://localhost:8080", "m", null, model, 100, 0));
        Assert.Null(AgentChats.Make("zeta", "http://localhost:8080", "m", null, model, 100, 0));
        Assert.Null(AgentChats.Make("ollama", "not a url", "m", null, model, 100, 0));
    }

    private static List<ToolCall> Find(string text, out string rest) => ToolCallText.Find(text, out rest);
}
