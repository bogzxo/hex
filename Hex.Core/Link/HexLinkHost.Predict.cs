using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

// Predictions made for the device at the other end with this machine's model (docs/hex-link-plan.md, 5.6), so a weak
// laptop gets the autocomplete of the desktop it's linked to. Two things have to be so: the device has the Predict
// right, which nothing else gives and which needs nothing else, and this machine shares its predictions (its setting
// on and a model it can reach, IHexLinkServices.ServesPredictions). Hello says "serves": ["predictions"] when both are,
// and link/serves says it again whenever either changes.
//
// The asker cancels a prediction on every keystroke, so everything here is quick: predict/start is answered as soon as
// the request is checked and the model is running on a task of its own, its text goes back as predict/chunk
// notifications and predict/done ends it; predict/cancel is taken on the reading task and cancels the model's token at
// once. A device gets at most two at a time (a third cancels the oldest, the one nobody is waiting for any more), and
// the audit log gets a line a minute at most about them, not one a keystroke.
internal sealed partial class HexLinkHost
{
    /// <summary>The most predictions of one device running at once, a new one past it cancels the oldest.</summary>
    public const int MOST_PREDICTIONS = 2;

    // How often the audit log is told how many predictions a device asked for
    private static readonly TimeSpan PREDICT_AUDIT_EVERY = TimeSpan.FromMinutes(1);

    // A prediction running for the device, by the id it picked: its token and when it started, for the oldest
    private sealed record Predicting(string Id, CancellationTokenSource Cancel, long Started);

    private readonly ConcurrentDictionary<string, Predicting> predicting = new(StringComparer.Ordinal);
    private readonly Lock predictGate = new();
    private long predictStarts;

    // What link/serves last said (or hello), -1 before either did, so it's only said again when it changed
    private int servesSaid = -1;

    // The predictions asked since the audit log last heard, and when it did
    private int predictAsked;
    private long predictAudited = long.MinValue;

    /// <summary>Whether this machine makes predictions for the device at the other end now: its right, and the share setting.</summary>
    public bool ServesPredictions => Rights.HasFlag(HexLinkRights.Predict) && services.ServesPredictions;

    // What hello and link/serves say this machine does for the device
    private JsonArray Serves()
    {
        bool serves = ServesPredictions;
        Volatile.Write(ref servesSaid, serves ? 1 : 0);
        return ServesJson(serves);
    }

    // Said as a node each, JsonArray's generic Add would reach for the serializer
    private static JsonArray ServesJson(bool serves) => serves ? new JsonArray((JsonNode)"predictions") : new JsonArray();

    /// <summary>
    /// Whether this machine would serve the device may have changed (its rights, or the share setting or the model
    /// here), told to it when it did, and what it was running cancelled when it may no longer ask.
    /// </summary>
    public void ServesMoved()
    {
        bool serves = ServesPredictions;
        if (!serves)
            StopPredicting();

        if (Interlocked.Exchange(ref servesSaid, serves ? 1 : 0) != (serves ? 1 : 0))
            session.TellLatest("link/serves", new JsonObject { ["serves"] = ServesJson(serves) });
    }

    // A prediction started: checked, the oldest cancelled when there are too many, the model run on a task of its own
    private Task<JsonNode?> PredictStart(JsonObject parameters)
    {
        string id = HexLinkData.Need(parameters, "id", HexLinkPrediction.LONGEST_ID + 1);
        if (id.Length == 0 || id.Length > HexLinkPrediction.LONGEST_ID)
            throw HexLinkException.Invalid($"A prediction's id is a string of 1 to {HexLinkPrediction.LONGEST_ID} characters.");

        CountPrediction();
        if (!Rights.HasFlag(HexLinkRights.Predict))
            throw HexLinkException.NotAllowed(HexLinkData.Word(HexLinkRights.Predict));
        if (!services.ServesPredictions)
            throw HexLinkPrediction.Failed("This Hex doesn't share its predictions.", HexLinkPrediction.REFUSED);

        HexLinkPrediction request = HexLinkPrediction.Of(parameters);
        var running = new Predicting(id, CancellationTokenSource.CreateLinkedTokenSource(session.Closing), Interlocked.Increment(ref predictStarts));
        lock (predictGate)
        {
            if (!ServesPredictions)
            {
                running.Cancel.Dispose();
                throw HexLinkPrediction.Failed("This Hex doesn't share its predictions.", HexLinkPrediction.REFUSED);
            }
            if (!predicting.TryAdd(id, running))
            {
                running.Cancel.Dispose();
                throw HexLinkException.Invalid($"A prediction called {id} is running already.");
            }

            // The oldest let go of, the asker has typed past it
            while (predicting.Count > MOST_PREDICTIONS && predicting.Values.MinBy(each => each.Started) is { } oldest && oldest != running)
                Cancel(oldest);
        }

        IAsyncEnumerable<string> text;
        try
        {
            text = services.Predict(request, running.Cancel.Token);
        }
        catch (Exception e)
        {
            Let(running);
            throw e as HexLinkException ?? HexLinkPrediction.Failed(e.Message, HexLinkPrediction.FAILED);
        }

        _ = Task.Run(() => PredictRun(running, text));
        return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = id });
    }

    // The model's text sent as it comes, then done with how it ended. Never throws, whatever the model does
    private async Task PredictRun(Predicting running, IAsyncEnumerable<string> text)
    {
        CancellationToken cancel = running.Cancel.Token;
        var done = new JsonObject { ["id"] = running.Id, ["ok"] = true };
        try
        {
            await foreach (string piece in text.WithCancellation(cancel).ConfigureAwait(false))
            {
                if (cancel.IsCancellationRequested || !ServesPredictions)
                    break;
                if (piece.Length > 0 && !await session.TellAsync("predict/chunk", new JsonObject { ["id"] = running.Id, ["text"] = piece }, cancel))
                    break;
            }

            if (cancel.IsCancellationRequested || !ServesPredictions)
                Ended(done, "The prediction was cancelled.", HexLinkPrediction.CANCELLED);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Ended(done, "The prediction was cancelled.", HexLinkPrediction.CANCELLED);
        }
        catch (HexLinkException e)
        {
            Ended(done, e.Message, HexLinkPrediction.StatusOf(e));
        }
        catch (Exception e)
        {
            Ended(done, e.Message, HexLinkPrediction.FAILED);
        }
        finally
        {
            Let(running);
        }

        // A link that's gone has nobody to tell; one cancelled by the asker is told anyway, it's a line and it's harmless
        if (!session.IsClosed)
            await session.TellAsync("predict/done", done);
    }

    private static void Ended(JsonObject done, string error, string status)
    {
        done["ok"] = false;
        done["error"] = error.Length > 500 ? error[..500] : error;
        done["status"] = status;
    }

    // predict/cancel, on the reading task: the model's token cancelled at once, nothing answered
    private bool PredictCancel(JsonObject parameters)
    {
        if (HexLinkData.String(parameters, "id", HexLinkPrediction.LONGEST_ID) is { } id && predicting.TryGetValue(id, out Predicting? running))
            Cancel(running);
        return true;
    }

    // Cancelled and let go of; its run sends done when the model has stopped
    private void Cancel(Predicting running)
    {
        predicting.TryRemove(new KeyValuePair<string, Predicting>(running.Id, running));
        try
        {
            running.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended as it was cancelled
        }
    }

    // A prediction that ended let go of, its token's link to the session's undone
    private void Let(Predicting running)
    {
        predicting.TryRemove(new KeyValuePair<string, Predicting>(running.Id, running));
        running.Cancel.Dispose();
    }

    // Every prediction cancelled, when the device may no longer ask or the link goes
    private void StopPredicting()
    {
        Predicting[] current;
        lock (predictGate)
            current = [.. predicting.Values];
        foreach (Predicting running in current)
            Cancel(running);
    }

    // A start counted, and the audit log told how many there were at most once a minute
    private void CountPrediction()
    {
        int asked = Interlocked.Increment(ref predictAsked);
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref predictAudited);
        if (last != long.MinValue && now - last < (long)PREDICT_AUDIT_EVERY.TotalMilliseconds)
            return;
        if (Interlocked.CompareExchange(ref predictAudited, now, last) != last)
            return;

        Interlocked.Add(ref predictAsked, -asked);
        services.Audit("predict", Device, asked == 1 ? "asked for a prediction" : $"asked for {asked} predictions since the last line");
    }
}
