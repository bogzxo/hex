using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

// Chat completions made for the device at the other end with this machine's chat models (docs/hex-link-plan.md, 5.7), so a
// machine without a model gets AI Edit, the agents and @ai from the one it's linked to. Two things have to be so: the device
// has the Ai right, which nothing else gives and which needs nothing else, and this machine serves AI (the Serve AI setting
// on and a chat model it can reach, IHexLinkServices.ServesAi). Hello says "serves": ["ai"] when both are, and link/serves
// says it again whenever either changes.
//
// Every request is bounded (HexLinkAiRequest.Bound: a body of 1 MB, an answer of 8192 tokens, one choice), cancellable (ai/cancel,
// the link closing, the right going) and audited, one line each. A device has one request running; up to MOST_QUEUED - 1 more
// wait behind it, in the order they came, and are told their place (ai/queued) as it moves; one past that is refused as busy.
// ai/start is answered as soon as the request is checked; the model's chunks go back as ai/chunk notifications and ai/done ends it.
internal sealed partial class HexLinkHost
{
    // A chat running or waiting for the device, by the id it picked
    // Each run waits for the one asked before it (Previous) and says it is over when it is (Done): the device's turns in the order asked
    private sealed record AiRun(string Id, string Model, CancellationTokenSource Cancel, Task Previous, TaskCompletionSource Done);

    private readonly ConcurrentDictionary<string, AiRun> aiRuns = new(StringComparer.Ordinal);
    private readonly List<AiRun> aiWaiting = [];
    private readonly Lock aiLock = new();
    private Task aiTail = Task.CompletedTask;

    /// <summary>Whether this machine serves AI to the device now: its right, and the Serve AI setting.</summary>
    public bool ServesAi => Rights.HasFlag(HexLinkRights.Ai) && services.ServesAi;

    /// <summary>How many chat requests of the device are running or waiting now.</summary>
    public int AiRequests => aiRuns.Count;

    // Serve AI needs nothing else, not even View (HexLinkAllowed.Has would ask for it)
    private void NeedAi(string what)
    {
        if (Rights.HasFlag(HexLinkRights.Ai))
            return;
        services.Audit("refused", Device, $"{what}, it needs ai");
        throw HexLinkException.NotAllowed("ai");
    }

    // ai/models: the chat models this machine serves
    private async Task<JsonNode?> AiModelsAsk(CancellationToken cancel)
    {
        NeedAi("ai/models");
        if (!services.ServesAi)
            throw HexLinkAiRequest.Failed("This Hex doesn't serve AI.", HexLinkAiRequest.REFUSED);
        IReadOnlyList<HexLinkAiModel> models = await services.AiModels(cancel).ConfigureAwait(false);
        return new JsonObject { ["models"] = new JsonArray([.. models.Take(HexLinkAiModel.MOST_MODELS).Select(model => (JsonNode)model.ToJson())]) };
    }

    // ai/start: checked, queued behind the device's own running chat, the model run on a task of its own
    private Task<JsonNode?> AiStart(JsonObject parameters)
    {
        string id = HexLinkData.Need(parameters, "id", HexLinkAiRequest.LONGEST_ID + 1);
        if (id.Length == 0 || id.Length > HexLinkAiRequest.LONGEST_ID)
            throw HexLinkException.Invalid($"A chat's id is a string of 1 to {HexLinkAiRequest.LONGEST_ID} characters.");

        NeedAi("ai/start");
        if (!services.ServesAi)
        {
            services.Audit("refused", Device, "asked for a chat, this Hex doesn't serve AI");
            throw HexLinkAiRequest.Failed("This Hex doesn't serve AI.", HexLinkAiRequest.REFUSED);
        }

        HexLinkAiRequest request = HexLinkAiRequest.Of(parameters);
        AiRun run;
        int place;
        lock (aiLock)
        {
            if (!ServesAi)
                throw HexLinkAiRequest.Failed("This Hex doesn't serve AI.", HexLinkAiRequest.REFUSED);
            if (aiRuns.Count >= HexLinkAiRequest.MOST_QUEUED)
            {
                services.Audit("refused", Device, $"asked for a chat, {aiRuns.Count} are running or waiting already");
                throw HexLinkAiRequest.Failed($"{services.Device.Name} is busy with {aiRuns.Count} of this device's chats, wait for one to finish.", HexLinkAiRequest.BUSY);
            }
            if (aiRuns.ContainsKey(id))
                throw HexLinkException.Invalid($"A chat called {id} is running already.");

            // Its turn is when the one asked before it is over
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = new AiRun(id, request.Model, CancellationTokenSource.CreateLinkedTokenSource(session.Closing), aiTail, done);
            aiTail = done.Task;
            aiRuns[id] = run;
            aiWaiting.Add(run);
            place = aiRuns.Count;
        }

        services.Audit("ai", Device, $"asked {request.Model} for a chat ({request.Body.Length / 1024 + 1} KB)");
        services.AiServing(Device, AiRequests);
        _ = Task.Run(() => AiRunAsync(run, request));
        return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = id, ["place"] = place });
    }

    // Waits for the device's turn, then the model's chunks sent as they come, then done with how it ended. Never throws
    private async Task AiRunAsync(AiRun run, HexLinkAiRequest request)
    {
        CancellationToken cancel = run.Cancel.Token;
        var done = new JsonObject { ["id"] = run.Id, ["ok"] = true };
        bool turn = false;
        try
        {
            turn = await AiAwaitTurn(run, cancel).ConfigureAwait(false);
            IAsyncEnumerable<string> chunks = services.Ai(Device, request, cancel);
            await foreach (string chunk in chunks.WithCancellation(cancel).ConfigureAwait(false))
            {
                if (cancel.IsCancellationRequested || !ServesAi)
                    break;
                if (chunk.Length > 0 && !await session.TellAsync("ai/chunk", new JsonObject { ["id"] = run.Id, ["data"] = chunk }, cancel))
                    break;
            }

            if (!ServesAi)
                Ended(done, "This Hex stopped serving AI.", HexLinkAiRequest.REFUSED);
            else if (cancel.IsCancellationRequested)
                Ended(done, "The chat was cancelled.", HexLinkAiRequest.CANCELLED);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Stopped serving (the setting, or the right) is not the asker's cancel, and is said so
            if (!ServesAi && !session.Closing.IsCancellationRequested)
                Ended(done, "This Hex stopped serving AI.", HexLinkAiRequest.REFUSED);
            else
                Ended(done, "The chat was cancelled.", HexLinkAiRequest.CANCELLED);
        }
        catch (HexLinkException e)
        {
            Ended(done, e.Message, HexLinkAiRequest.StatusOf(e));
        }
        catch (Exception e)
        {
            Ended(done, e.Message, HexLinkAiRequest.FAILED);
        }
        finally
        {
            // A chat cancelled while it waited still holds its place in line until the one before it is over
            if (!turn)
                await run.Previous.ConfigureAwait(false);
            run.Done.TrySetResult();
            lock (aiLock)
                aiWaiting.Remove(run);
            aiRuns.TryRemove(new KeyValuePair<string, AiRun>(run.Id, run));
            run.Cancel.Dispose();
            services.AiServing(Device, AiRequests);
            AiTellPlaces();
        }

        if (!session.IsClosed)
            await session.TellAsync("ai/done", done);
    }

    // The device's turn: at once when nothing of its is running, else after those before it, told its place as it moves
    private async Task<bool> AiAwaitTurn(AiRun run, CancellationToken cancel)
    {
        if (!run.Previous.IsCompleted)
        {
            AiTellPlaces();
            await run.Previous.WaitAsync(cancel).ConfigureAwait(false);
        }

        lock (aiLock)
            aiWaiting.Remove(run);
        AiTellPlaces();
        return true;
    }

    // Each waiting chat told its place (2nd is next after the running one), off the request's own path
    private void AiTellPlaces()
    {
        AiRun[] waiting;
        lock (aiLock)
            waiting = [.. aiWaiting];
        for (int i = 0; i < waiting.Length; i++)
        {
            if (session.IsClosed)
                return;
            _ = session.TellAsync("ai/queued", new JsonObject { ["id"] = waiting[i].Id, ["place"] = i + 2 });
        }
    }

    // ai/cancel, on the reading task: the model's token cancelled at once, nothing answered
    private bool AiCancel(JsonObject parameters)
    {
        if (HexLinkData.String(parameters, "id", HexLinkAiRequest.LONGEST_ID) is { } id && aiRuns.TryGetValue(id, out AiRun? run))
        {
            try
            {
                run.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended as it was cancelled
            }
        }

        return true;
    }

    // Every chat cancelled, when the device may no longer ask or the setting went off
    private void StopAi()
    {
        foreach (AiRun run in aiRuns.Values)
        {
            try
            {
                run.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended as it was cancelled
            }
        }
    }
}
