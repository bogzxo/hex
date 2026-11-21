using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Hex.Core;

namespace Hex.Tests;

/// <summary>What the prediction tests share: the goldens, a clock moved by hand, a scripted provider and a fake server.</summary>
internal static class PredictionTestKit
{
    /// <summary>A golden file kept in the test assembly, its line ends as '\n' whatever the checkout did to them.</summary>
    public static string Golden(string name)
    {
        using Stream stream = typeof(PredictionTestKit).Assembly.GetManifestResourceStream("Prediction." + name)
            ?? throw new InvalidOperationException("No golden " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>
    /// The model card's example read back into what it was made from: the input (the file being only the excerpt the
    /// prompt shows), the editable region's start and end, and the prompt itself.
    /// </summary>
    public static (PredictionInput Input, int RegionStart, int RegionEnd, string Prompt) ZetaSample()
    {
        // The card's file ends its last line, the newline after <[fim-middle]> that the model is never sent
        string prompt = Golden("zeta2.prompt");
        Assert.EndsWith(Zeta2Format.MIDDLE + "\n", prompt);
        prompt = prompt[..^1];
        const string path = "tree-sitter/crates/loader/src/loader.rs";

        int prefixAt = prompt.IndexOf(Zeta2Format.PREFIX, StringComparison.Ordinal);
        string after = prompt[Zeta2Format.SUFFIX.Length..prefixAt];
        after = after[..^1];

        string historyHeader = Zeta2Format.FILE + "edit_history\n";
        string fileHeader = Zeta2Format.FILE + path + "\n";
        int historyAt = prompt.IndexOf(historyHeader, StringComparison.Ordinal);
        int fileAt = prompt.IndexOf(fileHeader, StringComparison.Ordinal);
        string history = prompt[(historyAt + historyHeader.Length)..(fileAt - 1)];

        var events = new List<PredictionEvent>();
        string header = "--- a/" + path + "\n+++ b/" + path + "\n";
        foreach (string written in history.Split(header, StringSplitOptions.RemoveEmptyEntries))
            events.Add(new PredictionEvent(path, written, false));

        string section = prompt[(fileAt + fileHeader.Length)..];
        int current = section.IndexOf(Zeta2Format.CURRENT + "\n", StringComparison.Ordinal);
        string before = section[..current];
        int regionAt = current + Zeta2Format.CURRENT.Length + 1;
        int separator = section.IndexOf("\n" + Zeta2Format.SEPARATOR + "\n", regionAt, StringComparison.Ordinal);
        string marked = section[regionAt..separator];
        int cursor = marked.IndexOf(Zeta2Format.CURSOR, StringComparison.Ordinal);
        string region = marked.Replace(Zeta2Format.CURSOR, "", StringComparison.Ordinal);

        string text = before + region + after;
        var input = new PredictionInput(path, "rust", text, before.Length + cursor, events, []);
        return (input, before.Length, before.Length + region.Length, prompt);
    }

    /// <summary>Options for a test, Qwen's format unless said otherwise.</summary>
    public static PredictionOptions Options(string format = "qwen", int debounce = 200, int maxTokens = 256) =>
        new(true, "ollama", "http://localhost:11434", null, "test-model", format, maxTokens, debounce);
}

/// <summary>A clock that only moves when the test moves it.</summary>
internal sealed class HandClock : TimeProvider
{
    private long ticks = TimeSpan.TicksPerDay;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public void Advance(double milliseconds) => Interlocked.Add(ref ticks, (long)(milliseconds * TimeSpan.TicksPerMillisecond));
}

/// <summary>A provider that answers from a script, counting what it's asked and seeing whether it was cut off.</summary>
internal sealed class ScriptedProvider(Func<PredictionWireRequest, CancellationToken, IAsyncEnumerable<string>> script) : IPredictionProvider
{
    private int asked, closed, cancelled;

    /// <summary>A provider whose answer is these pieces, then the end.</summary>
    public ScriptedProvider(params string[] pieces) : this((_, cancel) => Pieces(pieces, false, cancel))
    {
    }

    public int Asked => Volatile.Read(ref asked);

    /// <summary>Answers left before their end, the connection closed under them.</summary>
    public int Closed => Volatile.Read(ref closed);

    /// <summary>Answers whose cancellation reached the provider.</summary>
    public int Cancelled => Volatile.Read(ref cancelled);

    public PredictionWireRequest? Last { get; private set; }

    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        Interlocked.Increment(ref asked);
        Last = request;
        bool ended = false;
        try
        {
            await foreach (string piece in script(request, cancel).WithCancellation(cancel))
                yield return piece;
            ended = true;
        }
        finally
        {
            if (cancel.IsCancellationRequested)
                Interlocked.Increment(ref cancelled);
            else if (!ended)
                Interlocked.Increment(ref closed);
        }
    }

    /// <summary>Pieces one after another, then waiting until cancelled when <paramref name="stall"/>.</summary>
    public static async IAsyncEnumerable<string> Pieces(string[] pieces, bool stall, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        foreach (string piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
        if (stall)
            await Task.Delay(Timeout.Infinite, cancel);
    }
}

/// <summary>A server in the test: what was sent to it is kept, and it answers as the test says.</summary>
internal sealed class FakeServer(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    /// <summary>A server that answers 200 with a body that arrives in these pieces.</summary>
    public static FakeServer Streaming(PieceStream stream) =>
        new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));

    /// <summary>A server that answers with a status and a body.</summary>
    public static FakeServer Answering(HttpStatusCode status, string body) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request, body));
        return await answer(request);
    }
}

/// <summary>A response body that arrives a piece per read, and then ends or waits until the read is cancelled.</summary>
internal sealed class PieceStream(IEnumerable<string> pieces, bool stall = false) : Stream
{
    private readonly Queue<byte[]> queue = new(pieces.Select(Encoding.UTF8.GetBytes));
    private byte[]? partial;
    private int partialAt;

    public bool Disposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (partial is null)
        {
            if (queue.Count == 0)
            {
                if (stall)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            partial = queue.Dequeue();
            partialAt = 0;
        }

        int count = Math.Min(buffer.Length, partial.Length - partialAt);
        partial.AsMemory(partialAt, count).CopyTo(buffer);
        partialAt += count;
        if (partialAt == partial.Length)
            partial = null;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
