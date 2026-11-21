using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hex.Core;

// Predictions asked of the other machine's model (docs/hex-link-plan.md, 5.6), the asking half of HexLinkHost.Predict.
// Whether it would make them for this machine is what its hello said in "serves" and link/serves has said since. A
// prediction is a predict/start with an id picked here, so the text that comes back as predict/chunk (which may come
// before the start's answer) is taken by the id from the first moment, and predict/done ends it. Letting go of the
// stream early (the person typed on, the token cancelled) tells the other machine predict/cancel at once.
internal sealed partial class HexLinkPeer
{
    // How long a predict/start may go unanswered, its checks are quick and somebody is waiting on it
    private static readonly TimeSpan PREDICT_PATIENCE = TimeSpan.FromSeconds(30);

    // A prediction being received, its text in order and how it ended when it didn't end well
    private sealed class PredictionStream
    {
        public Channel<string> Text { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public Exception? Failed { get; set; }
    }

    private readonly ConcurrentDictionary<string, PredictionStream> predictionStreams = new(StringComparer.Ordinal);
    private long predictIds;
    private volatile bool servesPredictions;

    /// <summary>Whether the other machine would make predictions for this one now: it shares them and lets this one ask.</summary>
    public bool ServesPredictions => servesPredictions && !IsClosed;

    // What hello and link/serves say the other machine does for this one
    private void TakeServes(JsonObject said)
    {
        bool serves = HexLinkData.Strings(said, "serves", 16, 40).Contains("predictions");
        if (serves == servesPredictions)
            return;
        servesPredictions = serves;
        Changed?.Invoke();
    }

    /// <summary>
    /// A prediction made by the other machine's model, its text as it comes. The link closing, or the other machine
    /// refusing or failing, throws the <see cref="HexLinkException"/> it was: NOT_ALLOWED (data.needs "predict") without
    /// the right, PREDICTION_FAILED with data.status otherwise. Cancelling the token, or letting go of the stream before
    /// it ended, cancels it there at once; the other machine cancelling it (a newer one past its two) is an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async IAsyncEnumerable<string> Predict(HexLinkPrediction request, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        string id = "p" + Interlocked.Increment(ref predictIds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stream = new PredictionStream();
        predictionStreams[id] = stream;
        bool ended = false;
        try
        {
            // The link going ends the stream with why; registered after it's in, so a close a moment before still lands
            using CancellationTokenRegistration closing = session.Closing.Register(() =>
            {
                stream.Failed ??= new HexLinkException(HexLinkErrors.CLOSED, $"The link closed, {session.Why}");
                stream.Text.Writer.TryComplete();
            });

            await session.Ask("predict/start", request.ToJson(id), cancel, PREDICT_PATIENCE).ConfigureAwait(false);
            while (await stream.Text.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (stream.Text.Reader.TryRead(out string? text))
                    yield return text;
            }

            ended = true;
            if (stream.Failed is { } failed)
                throw failed;
        }
        finally
        {
            predictionStreams.TryRemove(id, out _);
            if (!ended && !session.IsClosed)
                session.Tell("predict/cancel", new JsonObject { ["id"] = id });
        }
    }

    // A notification of predictions', true when it was (the reading task calls it, it never throws)
    private bool PredictTold(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "link/serves":
                TakeServes(parameters);
                return true;
            case "predict/chunk":
                if (HexLinkData.String(parameters, "id", HexLinkPrediction.LONGEST_ID) is { } chunkOf && predictionStreams.TryGetValue(chunkOf, out PredictionStream? into)
                    && HexLinkData.String(parameters, "text", HexLinkPrediction.MOST_CHUNK) is { Length: > 0 } text)
                    into.Text.Writer.TryWrite(text);
                return true;
            case "predict/done":
                if (HexLinkData.String(parameters, "id", HexLinkPrediction.LONGEST_ID) is { } doneOf && predictionStreams.TryGetValue(doneOf, out PredictionStream? ending))
                {
                    if (HexLinkData.Bool(parameters, "ok") != true)
                    {
                        string error = HexLinkData.String(parameters, "error", 500) ?? "The prediction failed.";
                        string status = HexLinkData.String(parameters, "status", 40) is { Length: > 0 } said ? said : HexLinkPrediction.FAILED;
                        ending.Failed ??= status == HexLinkPrediction.CANCELLED ? new OperationCanceledException(error) : HexLinkPrediction.Failed(error, status);
                    }

                    ending.Text.Writer.TryComplete();
                }

                return true;
            default:
                return false;
        }
    }
}
