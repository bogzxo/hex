using System.Net;
using System.Text;
using Hex.Core;
using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>
/// What a streamed answer of 2,000 tokens allocates, per token: a fake server whose body completes every read at once
/// keeps the whole run on one thread, so the thread's allocated bytes are the answer's. The ceilings are what the
/// parsers cost after the pooled byte reader; a regression to a string per line shows as a failure.
/// </summary>
public sealed class StreamAllocationTests(ITestOutputHelper output)
{
    private const int TOKENS = 2000;

    /// <summary>A body that hands out one event per read, every read already complete.</summary>
    private sealed class EventStream(byte[][] events) : Stream
    {
        private int next;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (next >= events.Length)
                return new ValueTask<int>(0);
            byte[] bytes = events[next++];
            bytes.CopyTo(buffer);
            return new ValueTask<int>(bytes.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpClient Client(byte[][] events) =>
        new(new FakeServer(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EventStream(events)) })));

    private static byte[][] OpenAiText(int tokens)
    {
        var all = new List<byte[]>();
        for (int i = 0; i < tokens; i++)
            all.Add(Encoding.UTF8.GetBytes($"data: {{\"choices\":[{{\"index\":0,\"text\":\" tok{i % 100}\",\"finish_reason\":null}}]}}\n\n"));
        all.Add(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"index\":0,\"text\":\"\",\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        return [.. all];
    }

    private static byte[][] OpenAiChatDeltas(int tokens)
    {
        var all = new List<byte[]>();
        for (int i = 0; i < tokens; i++)
            all.Add(Encoding.UTF8.GetBytes($"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\" tok{i % 100}\"}},\"finish_reason\":null}}]}}\n\n"));
        all.Add(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        return [.. all];
    }

    private static byte[][] OllamaGenerate(int tokens)
    {
        var all = new List<byte[]>();
        for (int i = 0; i < tokens; i++)
            all.Add(Encoding.UTF8.GetBytes($"{{\"model\":\"m\",\"response\":\" tok{i % 100}\",\"done\":false}}\n"));
        all.Add(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"response\":\"\",\"done\":true,\"done_reason\":\"stop\"}\n"));
        return [.. all];
    }

    private static byte[][] OllamaChatLines(int tokens)
    {
        var all = new List<byte[]>();
        for (int i = 0; i < tokens; i++)
            all.Add(Encoding.UTF8.GetBytes($"{{\"model\":\"m\",\"message\":{{\"role\":\"assistant\",\"content\":\" tok{i % 100}\"}},\"done\":false}}\n"));
        all.Add(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n"));
        return [.. all];
    }

    private static async Task<(long Bytes, int Pieces)> Run(Func<IAsyncEnumerable<string>> make)
    {
        int pieces = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        await foreach (string _ in make())
            pieces++;
        return (GC.GetAllocatedBytesForCurrentThread() - before, pieces);
    }

    private async Task<double> PerToken(string name, Func<int, Func<IAsyncEnumerable<string>>> make)
    {
        await Run(make(50));
        var (small, _) = await Run(make(0));
        var (big, pieces) = await Run(make(TOKENS));
        double perToken = (big - small) / (double)TOKENS;
        output.WriteLine($"{name}: {small} B per request, {perToken:0.0} B per token, {pieces} pieces");
        Assert.True(pieces >= TOKENS);
        return perToken;
    }

    [Fact]
    public void Noting_an_edit_in_5000_lines_allocates_a_fraction_of_the_text()
    {
        string[] lines = [.. Enumerable.Range(0, 5000).Select(i => $"    public static int Value{i}(int a, int b) => a * {i} + b; // line {i}")];
        long text = lines.Sum(l => l.Length + 1) * sizeof(char);
        var history = new PredictionHistory();
        history.Seed("a.cs", lines);
        long total = 0;
        const int KEYS = 100;
        for (int i = 0; i < KEYS + 20; i++)
        {
            string[] next = (string[])lines.Clone();
            next[2500] += new string('x', i + 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            history.NoteSoon("a.cs", i + 2, next, 2500);
            history.Flush();
            if (i >= 20)
                total += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        double perNote = total / (double)KEYS;
        Assert.True(perNote < 4 * text);
    }

    private static readonly PredictionWireRequest request = new("PROMPT", null, 4096, ["\n"], "m");

    [Fact]
    public async Task Openai_completions_stream() =>
        Assert.True(await PerToken("openai completions", n => () => new OpenAiProvider(Client(OpenAiText(n)), PredictionProviders.OpenAiUrl("http://127.0.0.1:1/v1")!, "m", null).Complete(request, default)) < Ceiling.OpenAiText);

    [Fact]
    public async Task Ollama_generate_stream() =>
        Assert.True(await PerToken("ollama generate", n => () => new OllamaProvider(Client(OllamaGenerate(n)), PredictionProviders.OllamaUrl("http://127.0.0.1:1")!, "m").Complete(request, default)) < Ceiling.OllamaGenerate);

    [Fact]
    public async Task Openai_chat_stream() =>
        Assert.True(await PerToken("openai chat", n => () => Texts(new OpenAiChat(Client(OpenAiChatDeltas(n)), OpenAiChat.Url("http://127.0.0.1:1/v1")!, "m", null).Stream([new ChatTurn("user", "hi")], default))) < Ceiling.OpenAiChat);

    [Fact]
    public async Task Ollama_chat_stream() =>
        Assert.True(await PerToken("ollama chat", n => () => Texts(new OllamaChat(Client(OllamaChatLines(n)), OllamaChat.Url("http://127.0.0.1:1")!, "m").Stream([new ChatTurn("user", "hi")], default))) < Ceiling.OllamaChat);

    private static async IAsyncEnumerable<string> Texts(IAsyncEnumerable<ChatPiece> pieces)
    {
        await foreach (ChatPiece piece in pieces)
            yield return piece.Text;
    }

    /// <summary>
    /// Bytes per token the parsers may allocate; the pieces' own strings are most of it. Measured on OpenAI chat: about 368
    /// in Release and 664 in Debug, so 1,500 holds in both with room, and a string per line (several KB a token) still fails.
    /// </summary>
    private static class Ceiling
    {
        public const double OpenAiText = 1500, OllamaGenerate = 1500, OpenAiChat = 1500, OllamaChat = 1500;
    }
}
