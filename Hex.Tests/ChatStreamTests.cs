using System.Net;
using Hex.Core;

namespace Hex.Tests;

/// <summary>Chat answers streamed from Ollama and from OpenAI-compatible servers, thinking split from the answer.</summary>
public sealed class ChatStreamTests
{
    private static readonly IReadOnlyList<ChatTurn> conversation = [new ChatTurn("user", "hello")];

    // Thinking left to the model, so that no /api/show is asked
    private static readonly PredictionTuning model = PredictionTuning.Usual with { Thinking = PredictionTuning.THINK_MODEL };

    private static OllamaChat Ollama(FakeServer server) =>
        new(new HttpClient(server), OllamaChat.Url("http://localhost:11434")!, "qwen3:" + Guid.NewGuid()) { Tuning = model };

    private static OpenAiChat OpenAi(FakeServer server, string? key = null) =>
        new(new HttpClient(server), OpenAiChat.Url("http://localhost:8080/v1")!, "gpt", key) { Tuning = model, MaxTokens = 321 };

    private static async Task<List<ChatPiece>> All(IChatStreamer chat, CancellationToken cancel = default)
    {
        var pieces = new List<ChatPiece>();
        await foreach (ChatPiece piece in chat.Stream(conversation, cancel))
            pieces.Add(piece);
        return pieces;
    }

    private static string Text(IEnumerable<ChatPiece> pieces, bool thinking) =>
        string.Concat(pieces.Where(piece => piece.Thinking == thinking).Select(piece => piece.Text));

    [Fact]
    public async Task OllamaStreamsThinkingFieldAndContentAcrossReads()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "{\"message\":{\"role\":\"assistant\",\"thinking\":\"Let me\"},\"done\":false}\n{\"mess",
            "age\":{\"thinking\":\" see\"},\"done\":false}\n{\"message\":{\"content\":\"Hel\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"lo\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true}\n",
        ]));

        List<ChatPiece> pieces = await All(Ollama(server));

        Assert.Equal("Let me see", Text(pieces, true));
        Assert.Equal("Hello", Text(pieces, false));
        Assert.Contains("\"stream\":true", server.Sent[0].Body);
        Assert.EndsWith("/api/chat", server.Sent[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"num_predict\":", server.Sent[0].Body);
    }

    [Fact]
    public async Task OllamaSplitsThinkBlockInContentAcrossPieces()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "{\"message\":{\"content\":\"<thi\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"nk>plan it</th\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"ink>\\n\\nAnswer\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"\"},\"done\":true}\n",
        ]));

        List<ChatPiece> pieces = await All(Ollama(server));

        Assert.Equal("plan it", Text(pieces, true));
        Assert.Equal("Answer", Text(pieces, false));
    }

    [Fact]
    public async Task OllamaErrorLineThrows()
    {
        var server = FakeServer.Streaming(new PieceStream(["{\"message\":{\"content\":\"Hi\"},\"done\":false}\n{\"error\":\"model exploded\"}\n"]));

        await Assert.ThrowsAsync<PredictionException>(() => All(Ollama(server)));
    }

    [Fact]
    public async Task OpenAiStreamsReasoningThenContentUntilDone()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hmm\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" ok\"}}]}\n\n: keep-alive\n\ndata: {\"choices\":[{\"delta\":{\"con",
            "tent\":\"Hi\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n",
            "data: [DONE]\n\n",
        ]));

        List<ChatPiece> pieces = await All(OpenAi(server));

        Assert.Equal("hmm ok", Text(pieces, true));
        Assert.Equal("Hi there", Text(pieces, false));
    }

    [Fact]
    public async Task OpenAiStopsAtFinishReasonAndSplitsThinkBlocks()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>why</think>Because\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"NEVER\"}}]}\n\n",
        ]));

        List<ChatPiece> pieces = await All(OpenAi(server));

        Assert.Equal("why", Text(pieces, true));
        Assert.Equal("Because", Text(pieces, false));
    }

    [Fact]
    public async Task OpenAiRequestStreamsWithModelMaxTokensAndBearer()
    {
        var server = FakeServer.Streaming(new PieceStream(["data: [DONE]\n\n"]));

        await All(OpenAi(server, "sekrit"));

        (HttpRequestMessage request, string body) = server.Sent[0];
        Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Contains("\"stream\":true", body);
        Assert.Contains("\"model\":\"gpt\"", body);
        Assert.Contains("\"max_tokens\":321", body);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sekrit", request.Headers.Authorization?.Parameter);
        Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task OpenAiErrorInDataLineThrowsWithoutTheKey()
    {
        var server = FakeServer.Streaming(new PieceStream(["data: {\"error\":{\"message\":\"context too long\"}}\n\n"]));

        var failure = await Assert.ThrowsAsync<PredictionException>(() => All(OpenAi(server, "sekrit")));

        Assert.DoesNotContain("sekrit", failure.Message);
    }

    [Fact]
    public async Task OpenAiErrorStatusThrows()
    {
        var server = FakeServer.Answering(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");

        await Assert.ThrowsAsync<PredictionException>(() => All(OpenAi(server, "sekrit")));
    }

    [Fact]
    public async Task CancellingStopsTheStream()
    {
        var server = FakeServer.Streaming(new PieceStream(["data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n"], stall: true));
        using var cancel = new CancellationTokenSource();
        var got = new List<ChatPiece>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatPiece piece in OpenAi(server).Stream(conversation, cancel.Token))
            {
                got.Add(piece);
                await cancel.CancelAsync();
            }
        });

        Assert.Equal("Hi", Text(got, false));
    }

    [Fact]
    public void MakeChoosesTheStreamerForTheProvider()
    {
        Assert.IsType<OllamaChat>(ChatStreams.Make(PredictionProviders.OLLAMA, "http://localhost:11434", "m", null, model, 100));
        Assert.IsType<OpenAiChat>(ChatStreams.Make(PredictionProviders.OPENAI, "http://localhost:8080/v1", "m", "k", model, 100));
        Assert.IsType<OpenAiChat>(ChatStreams.Make(PredictionProviders.LLAMACPP, "http://localhost:8080", "m", " ", model, 100));
        Assert.Null(ChatStreams.Make("link", "http://localhost:11434", "m", null, model, 100));
        Assert.Null(ChatStreams.Make(PredictionProviders.OLLAMA, "not a url", "m", null, model, 100));
        Assert.Null(ChatStreams.Make(PredictionProviders.OPENAI, "ftp://host", "m", null, model, 100));
    }
}
