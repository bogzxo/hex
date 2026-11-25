using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hex.Core;

// Chat completions asked of the other machine's models (docs/hex-link-plan.md, 5.7), the asking half of HexLinkHost.Ai.
// Whether it would answer for this machine is what its hello said in "serves" and link/serves has said since. A chat is an
// ai/start with an id picked here, so the chunks that come back as ai/chunk (which may come before the start's answer) are
// taken by the id from the first moment, ai/queued says its place while it waits behind this device's other chats, and
// ai/done ends it. Letting go of the stream early (the person cancelled, the token cancelled) tells the other machine ai/cancel at once.
internal sealed partial class HexLinkPeer
{
    private static readonly TimeSpan AI_PATIENCE = TimeSpan.FromSeconds(30);

    // A chat being received, its chunks in order, its place while it waits, and how it ended when it didn't end well
    private sealed class AiStream
    {
        public Channel<string> Chunks { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { SingleReader = true });

        public Exception? Failed { get; set; }

        public Action<int>? Queued { get; init; }
    }

    private readonly ConcurrentDictionary<string, AiStream> aiStreams = new(StringComparer.Ordinal);
    private long aiIds;
    private volatile bool servesAi;

    /// <summary>Whether the other machine would answer chats for this one now: it serves AI and lets this one ask.</summary>
    public bool ServesAi => servesAi && !IsClosed;

    /// <summary>
    /// The chat models the other machine serves. NOT_ALLOWED (data.needs "ai") without the right, AI_FAILED with data.status
    /// "refused" when it doesn't serve AI.
    /// </summary>
    public async Task<IReadOnlyList<HexLinkAiModel>> AiModels(CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("ai/models", new JsonObject(), cancel).ConfigureAwait(false);
        var models = new List<HexLinkAiModel>();
        foreach (JsonObject each in HexLinkData.Objects(said, "models", HexLinkAiModel.MOST_MODELS))
        {
            if (HexLinkAiModel.Of(each) is { } model)
                models.Add(model);
        }

        return models;
    }

    /// <summary>
    /// A chat completion made by the other machine's model, the server's streamed chunks (chat.completion.chunk JSON texts) as
    /// they come. <paramref name="queued"/> is told the place of the chat while it waits (2 is next after the one running).
    /// The link closing, or the other machine refusing or failing, throws the <see cref="HexLinkException"/> it was: NOT_ALLOWED
    /// (data.needs "ai") without the right, AI_FAILED with data.status otherwise. Cancelling the token, or letting go of the
    /// stream before it ended, cancels it there at once.
    /// </summary>
    public async IAsyncEnumerable<string> Ai(HexLinkAiRequest request, Action<int>? queued = null, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        string id = "a" + Interlocked.Increment(ref aiIds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stream = new AiStream { Queued = queued };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        aiStreams[id] = stream;
        bool ended = false;
        try
        {
            using CancellationTokenRegistration abandoned = lifetime.Token.Register(() => stream.Chunks.Writer.TryComplete());
            using CancellationTokenRegistration closing = session.Closing.Register(() =>
            {
                stream.Failed ??= new HexLinkException(HexLinkErrors.CLOSED, $"The link closed, {session.Why}");
                stream.Chunks.Writer.TryComplete();
            });

            Task<JsonNode?> accepted = session.Ask("ai/start", request.ToJson(id), lifetime.Token, AI_PATIENCE);
            _ = FinishOnAiStartFailure(accepted, stream);
            while (await stream.Chunks.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (stream.Chunks.Reader.TryRead(out string? chunk))
                {
                    cancel.ThrowIfCancellationRequested();
                    yield return chunk;
                }
            }

            await accepted.ConfigureAwait(false);
            ended = true;
            if (stream.Failed is { } failed)
                throw failed;
        }
        finally
        {
            stream.Chunks.Writer.TryComplete();
            aiStreams.TryRemove(id, out _);
            lifetime.Cancel();
            if (!ended && !session.IsClosed)
                await session.TellAsync("ai/cancel", new JsonObject { ["id"] = id });
        }
    }

    private static async Task FinishOnAiStartFailure(Task accepted, AiStream stream)
    {
        try { await accepted.ConfigureAwait(false); }
        catch (Exception) { stream.Chunks.Writer.TryComplete(); }
    }

    // The dispatcher awaits the stream's receive window before it takes the next notice.
    private async ValueTask<bool> AiTold(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "ai/chunk":
                if (HexLinkData.String(parameters, "id", HexLinkAiRequest.LONGEST_ID) is { } chunkOf && aiStreams.TryGetValue(chunkOf, out AiStream? into)
                    && HexLinkData.String(parameters, "data", HexLinkAiRequest.MOST_CHUNK) is { Length: > 0 } data)
                {
                    if (await into.Chunks.Writer.WaitToWriteAsync().ConfigureAwait(false))
                        into.Chunks.Writer.TryWrite(data);
                }

                return true;
            case "ai/queued":
                if (HexLinkData.String(parameters, "id", HexLinkAiRequest.LONGEST_ID) is { } queuedOf && aiStreams.TryGetValue(queuedOf, out AiStream? waiting)
                    && HexLinkData.Int(parameters, "place") is { } place)
                {
                    try { waiting.Queued?.Invoke(place); }
                    catch (Exception) { /* a caller's callback never breaks the link */ }
                }

                return true;
            case "ai/done":
                if (HexLinkData.String(parameters, "id", HexLinkAiRequest.LONGEST_ID) is { } doneOf && aiStreams.TryGetValue(doneOf, out AiStream? ending))
                {
                    if (HexLinkData.Bool(parameters, "ok") != true)
                    {
                        string error = HexLinkData.String(parameters, "error", 500) ?? "The chat failed.";
                        string status = HexLinkData.String(parameters, "status", 40) is { Length: > 0 } said ? said : HexLinkAiRequest.FAILED;
                        ending.Failed ??= status == HexLinkAiRequest.CANCELLED ? new OperationCanceledException(error) : HexLinkAiRequest.Failed(error, status);
                    }

                    ending.Chunks.Writer.TryComplete();
                }

                return true;
            default:
                return false;
        }
    }
}
