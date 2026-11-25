using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

using Horizon.UI.Components;

namespace Hex.Dawn;

// AI over a link (docs/hex-link-plan.md, 5.7). As the machine with the models: Serve AI is a right each device is given in the
// Devices window (nothing is served until it is ticked), and a request from a device with it is forwarded to this machine's own
// chat server (llama-server or Ollama, both speak OpenAI's chat completions), its chunks sent back as they come. A request
// waits its turn behind the other linked requests; this machine's own use shares the server, which queues. As the machine without
// a model: the models linked devices serve are listed in the library (Source Linked, "On <device>" in the picker) so any
// chat role can be set to one, chats go through LinkAiRouter, and a loopback endpoint (LinkAiLoopback) lets the Local agent,
// a process of its own, use them too.
internal sealed partial class DawnScene
{
    // The one linked chat the server is asked for at a time, across devices
    private readonly SemaphoreSlim linkAiGate = new(1, 1);

    // The requests being served for each device, by its id, with its name; read on any thread
    private readonly Dictionary<string, (string Name, int Requests)> linkAiServing = [];
    private bool linkAiServed;

    // The models each serving device listed, by its id: the peer asked (a new link asks again), what it said, and when it was last tried
    private readonly Dictionary<string, (HexLinkPeer Peer, IReadOnlyList<HexLinkAiModel>? Models, long Tried)> linkAiListed = [];
    private readonly HashSet<string> linkAiAsking = [];
    private bool linkAiBound;
    private LinkAiLoopback? linkAiLoopback;

    /* Serving */

    /// <summary>Whether this machine has chat models it could serve (the right of each device and the link are checked by Link). Any thread.</summary>
    internal bool LinkAiServes()
    {
        try
        {
            return !HexAi.Off && AiEditModels.LocalAgentModels(settings).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Off the editor's thread, from the links
    internal Task<IReadOnlyList<HexLinkAiModel>> LinkAiModelsListed()
    {
        var models = new List<HexLinkAiModel>();
        foreach (AiEditModel model in AiEditModels.LocalAgentModels(settings))
        {
            LocalModel? known = LocalModelLibrary.Shared.Models.FirstOrDefault(each => each.Source != LocalModelSource.Linked && string.Equals(each.Name, model.Title, StringComparison.OrdinalIgnoreCase));
            LocalModelCaps caps = known?.Caps ?? LocalModelCaps.Chat | LocalModelCaps.Tools;
            models.Add(new HexLinkAiModel(model.Model, model.Title, (int)(caps | LocalModelCaps.Chat), known?.ContextMax ?? settings.LlamaCppContext,
                known is null ? string.Empty : ModelPicker.DetailOf(known)));
        }

        return Task.FromResult<IReadOnlyList<HexLinkAiModel>>(models);
    }

    /// <summary>
    /// A chat for a device: this machine's chat server asked with the bounded request and its server-sent events passed on one
    /// chunk at a time, after the chats before it. Fails with the status Link says (nomodel, failed) in a sentence the other
    /// machine shows. Off the editor's thread.
    /// </summary>
    internal async IAsyncEnumerable<string> LinkAiServe(HexLinkDevice device, HexLinkAiRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        AiEditModel? model = AiEditModels.LocalAgentModels(settings).FirstOrDefault(each => string.Equals(each.Model, request.Model, StringComparison.Ordinal));
        if (model is null)
            throw HexLinkAiRequest.Failed($"{LinkDevice?.Name ?? "This Hex"} has no model called {request.Model} now.", HexLinkAiRequest.NO_MODEL);
        if (OpenAiChat.Url(model.Url) is not { } api)
            throw HexLinkAiRequest.Failed("The model's server address isn't a http or https URL.", HexLinkAiRequest.FAILED);

        await linkAiGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (model.Provider == PredictionProviders.LLAMACPP)
                await LinkAiEnsureLlama(cancel).ConfigureAwait(false);

            using var message = new HttpRequestMessage(HttpMethod.Post, api) { Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json") };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!string.IsNullOrWhiteSpace(model.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await PredictionProviders.Shared.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw HexLinkAiRequest.Failed("The model's server isn't answering.", HexLinkAiRequest.FAILED);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string said = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                    throw HexLinkAiRequest.Failed($"The model's server answered {(int)response.StatusCode}: {OneLine(said, 200)}", HexLinkAiRequest.FAILED);
                }

                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false), System.Text.Encoding.UTF8);
                while (await reader.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;
                    string data = line[5..].Trim();
                    if (data == "[DONE]")
                        yield break;
                    if (data.Length > 0)
                        yield return data;
                }
            }
        }
        finally
        {
            linkAiGate.Release();
        }
    }

    // Hex's own llama-server started when it isn't, and waited for (off the editor's thread, the start on it)
    private async Task LinkAiEnsureLlama(CancellationToken cancel)
    {
        var made = new TaskCompletionSource<LlamaCppServer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostLink(() =>
        {
            try
            {
                if (settings.LlamaCppConfig is null)
                    made.SetResult(null);
                else
                {
                    if (LlamaServer.State is not (LlamaCppServerState.Ready or LlamaCppServerState.Starting))
                        StartLlamaServer(false);
                    made.SetResult(LlamaServer);
                }
            }
            catch (Exception e)
            {
                made.SetException(e);
            }
        });
        LlamaCppServer? llama = await made.Task.WaitAsync(cancel).ConfigureAwait(false);
        if (llama is null)
            throw HexLinkAiRequest.Failed("Its llama.cpp isn't set up.", HexLinkAiRequest.FAILED);
        for (int waited = 0; llama.State != LlamaCppServerState.Ready; waited += 200)
        {
            if (llama.State == LlamaCppServerState.Failed || waited > 240_000)
                throw HexLinkAiRequest.Failed(llama.Problem is { Length: > 0 } problem ? problem : "Its llama.cpp didn't start.", HexLinkAiRequest.FAILED);
            await Task.Delay(200, cancel).ConfigureAwait(false);
        }
    }

    // The requests being served for a device, from Link's threads
    internal void LinkAiServingChanged(HexLinkDevice device, int requests)
    {
        lock (linkAiServing)
        {
            if (requests <= 0)
                linkAiServing.Remove(device.Id);
            else
                linkAiServing[device.Id] = (device.Name, requests);
        }

        PostLink(() => linkWindowStale = true);
    }

    /// <summary>The line the Devices window says under a device while this machine answers its chats: "Serving AI to Alpha: 1 request". Null when none.</summary>
    internal string? LinkAiStatusLine(string deviceId)
    {
        (string Name, int Requests) serving;
        lock (linkAiServing)
        {
            if (!linkAiServing.TryGetValue(deviceId, out serving))
                return null;
        }

        string count = serving.Requests == 1 ? "1 request" : serving.Requests.ToString(CultureInfo.InvariantCulture) + " requests";
        return Said("link.ai_serving", ("device", LinkNameOf(deviceId, serving.Name)), ("count", count));
    }

    /// <summary>Adds the status line to a device's card when there is one.</summary>
    private void LinkAiStatusRow(StackPanel column, string deviceId)
    {
        if (LinkAiStatusLine(deviceId) is { } line)
            column.Add(LinkLine(line));
    }

    // Once a second while Link is up: whether this machine could serve changed, the links told when it did
    private void LinkAiLookServing()
    {
        bool serving = LinkAiServes();
        if (serving != linkAiServed)
        {
            linkAiServed = serving;
            link?.PredictionsMoved();
        }

        LinkAiLookSource();
    }

    /* Using */

    private void LinkAiBind()
    {
        if (linkAiBound)
            return;
        linkAiBound = true;
        LinkAiRouter.Find = fingerprint => LinkPeers.FirstOrDefault(peer => !peer.IsClosed && string.Equals(peer.Device.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        LinkAiRouter.Queued = (device, place) => PostLink(() =>
        {
            // Kept for AI Edit's progress row too, which shows it while nothing has come back yet
            linkAiQueuedAt = (device, place);
            Say(Said("link.ai_queued", ("device", device), ("place", Ordinal(place))));
        });
    }

    // Where the latest chat of this machine waits on a linked device's machine, the device's name and its place (2 is next)
    private (string Device, int Place)? linkAiQueuedAt;

    private static string Ordinal(int place) => place + (place % 100 is 11 or 12 or 13 ? "th" : (place % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>
    /// The models the linked devices that serve this one AI offer, kept in the library (on the editor's thread): each serving
    /// device asked once for its models when it starts serving, the ones that stopped dropped, the loopback endpoint kept
    /// for the Local agent while there are any.
    /// </summary>
    private void LinkAiLookSource()
    {
        if (link is null)
        {
            if (linkAiListed.Count > 0 || linkAiBound)
                LinkAiClear();
            return;
        }

        LinkAiBind();
        long now = Environment.TickCount64;
        bool changed = false;
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        foreach (string gone in linkAiListed.Keys.Where(id => !peers.Any(peer => peer.Device.Id == id && peer.ServesAi && peer == linkAiListed[id].Peer)).ToList())
        {
            linkAiListed.Remove(gone);
            changed = true;
        }

        foreach (HexLinkPeer peer in peers)
        {
            if (!peer.ServesAi || !peer.Granted.HasFlag(HexLinkRights.Ai))
                continue;
            string id = peer.Device.Id;
            bool known = linkAiListed.TryGetValue(id, out var have) && have.Peer == peer;
            // A device whose list failed is asked again after a while
            if ((known && (have.Models is not null || now - have.Tried < 20_000)) || !linkAiAsking.Add(id))
                continue;
            linkAiListed[id] = (peer, known ? have.Models : null, now);
            _ = Task.Run(async () =>
            {
                IReadOnlyList<HexLinkAiModel>? models = null;
                try
                {
                    models = await peer.AiModels(linkCancel?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HexLinkException or OperationCanceledException or TimeoutException)
                {
                }

                PostLink(() =>
                {
                    linkAiAsking.Remove(id);
                    if (models is not null && peer.ServesAi && linkAiListed.TryGetValue(id, out var current) && current.Peer == peer)
                    {
                        linkAiListed[id] = (peer, models, Environment.TickCount64);
                        LinkAiPublish();
                    }
                });
            });
        }

        if (changed)
            LinkAiPublish();
    }

    // The library told what the devices offer, the loopback endpoint and its file brought up to date
    private void LinkAiPublish()
    {
        var models = new List<LocalModel>();
        foreach (var (peer, listed, _) in linkAiListed.Values.OrderBy(each => each.Peer.Device.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (listed is null)
                continue;
            string device = LinkNameOf(peer.Device.Id, peer.Device.Name);
            foreach (HexLinkAiModel model in listed)
                models.Add(new LocalModel(LinkAiModelId.Make(peer.Device, model.Id), model.Name, LocalModelSource.Linked, null, 0, (LocalModelCaps)model.Caps | LocalModelCaps.Chat,
                    model.Context > 0 ? model.Context : null, model.Detail.Length > 0 ? model.Detail : null, null, null, LocalModelFit.Fits, null, Device: device));
        }

        LocalModelLibrary.Shared.SetLinked(models);
        LinkAiSyncLoopback(models);
    }

    private void LinkAiSyncLoopback(IReadOnlyList<LocalModel> models)
    {
        if (models.Count == 0)
        {
            LinkAiStopLoopback();
            return;
        }

        try
        {
            linkAiLoopback ??= new LinkAiLoopback(() => [.. LocalModelLibrary.Shared.Models.Where(model => model.Source == LocalModelSource.Linked)
                .Select(model => new LinkAiLoopbackModel(model.Id, model.Device ?? string.Empty, model.Name))]);
            LinkAiLoopbackFile.Write(linkAiLoopback, [.. models.Select(model => new LinkAiLoopbackModel(model.Id, model.Device ?? string.Empty, model.Name))]);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // No loopback port: only the Local agent can't use the linked models
        }
    }

    private void LinkAiStopLoopback()
    {
        linkAiLoopback?.Dispose();
        linkAiLoopback = null;
        LinkAiLoopbackFile.Delete();
    }

    // Link switched off: nothing is served by anyone any more
    private void LinkAiClear()
    {
        linkAiListed.Clear();
        linkAiAsking.Clear();
        linkAiBound = false;
        LinkAiRouter.Find = null;
        LinkAiRouter.Queued = null;
        LocalModelLibrary.Shared.SetLinked([]);
        LinkAiStopLoopback();
    }

    /// <summary>The model's line in AI's menu and box: its name, "via <device>" when a linked device runs it, or that it loads in a moment.</summary>
    internal string AiEditModelText(AiEditModel model) =>
        LinkAiDeviceOf(model) is { Length: > 0 } device ? Said("ai_edit.model_via", ("model", model.Title), ("device", device))
            : Said(model.Swaps ? "ai_edit.model_swaps" : "ai_edit.model", ("model", model.Title));

    /// <summary>The device name a model runs on when it's a linked one, null for a local model.</summary>
    internal static string? LinkAiDeviceOf(AiEditModel model) => model.Provider == PredictionProviders.LINKAI ? model.Device : null;
}
