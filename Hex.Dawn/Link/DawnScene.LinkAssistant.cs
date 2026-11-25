namespace Hex.Dawn;

// This Hex's model answering in Link Chat (edit_predictions_link_chat). Like the prediction glue, the one place that knows
// both Link's chat and the model's settings. The assistant is there whenever Link is; the settings, read each time a
// question comes, say whether it answers.
internal sealed partial class DawnScene
{
    private LinkAssistant? linkAssistant;

    // Started with Link, hearing everything the chat hears
    private void StartLinkAssistant(HexLink started)
    {
        StopLinkAssistant();
        var assistant = linkAssistant = new LinkAssistant(
            () => MakeLinkAssistantOptions(settings),
            (text, model, thinking) => started.SayTalk(text, model, thinking),
            () => LinkDevice?.Id)
        {
            Failed = why => PostLink(() => Say(Said("link.talk.ai_failed_here", ("why", why)), error: true)),
            Unanswered = Said("link.talk.ai_failed"),
        Busy = Said("link.talk.ai_busy"),
        Queued = place => Said("link.ai_queued", ("device", LinkNameWanted), ("place", Ordinal(place))),
            // Answering on (and AI) but no usable model: the asker hears so rather than nothing
            // Answering off: the asker hears that too, so a question never goes unheard
            SpeaksForNoModel = LinkSpeaksForNoModel,
            NoModelGrace = TimeSpan.FromSeconds(4),
            NoModel = () => HexAi.Off ? null
                : !settings.EditPredictionsLinkChat ? Said("link.talk.ai_off", ("device", LinkDevice?.Name ?? string.Empty))
                : Said("link.talk.ai_no_model", ("device", LinkDevice?.Name ?? string.Empty)),
        };
        started.TalkHeard += assistant.Heard;
    }

    /// <summary>
    /// Whether this device says "no model" to a question asked by <paramref name="asker"/>: never to its own (the person
    /// typing it knows), and not when another linked device serves AI (that one answers). Of the rest the device with the
    /// lowest id speaks, so an unanswered @ai gets one line, not one from every device.
    /// </summary>
    private bool LinkSpeaksForNoModel(string asker)
    {
        if (LinkDevice is not { } me || string.Equals(asker, me.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (LinkPeers.Any(peer => !string.Equals(peer.Device.Id, asker, StringComparison.OrdinalIgnoreCase) && peer.ServesAi))
            return false;
        return LinkPeers.All(peer => string.Equals(peer.Device.Id, asker, StringComparison.OrdinalIgnoreCase)
            || string.Compare(me.Id, peer.Device.Id, StringComparison.OrdinalIgnoreCase) < 0);
    }

    private void StopLinkAssistant()
    {
        linkAssistant?.Dispose();
        linkAssistant = null;
    }

    /// <summary>
    /// What the model answering in Link Chat is asked with, null while it isn't to answer: No AI, the switch off, Hex Link
    /// as the provider (a model asked over a link is never passed on), no model, or a model that can't chat. It's always
    /// the model picked for code, the card's memory holding one model: Ollama's asked through its chat API, an OpenAI
    /// compatible server (Hex's own llama-server among them) through /v1/chat/completions beside the completions URL.
    /// Read from any thread.
    /// </summary>
    internal static LinkAssistantOptions? MakeLinkAssistantOptions(HexSettings from)
    {
        if (HexAi.Off || !from.EditPredictionsLinkChat)
            return null;
        // A linked device's model chosen for chat, or the only one there is when this machine has none of its own (Serve AI)
        AiEditModel? linked = LinkAiModelId.Is(from.LocalAiChatModel) ? AiEditModels.Resolve(from, out _) : null;
        LinkAssistantOptions? local = MakeLocalAssistantOptions(from);
        linked ??= local is null ? AiEditModels.LinkedFallback(from) : null;
        return linked is { Provider: PredictionProviders.LINKAI }
            ? new LinkAssistantOptions(string.Empty, linked.Model, from.EditPredictionsSystemPrompt, from.TuningFor(LocalAiRole.Chat)) { Provider = PredictionProviders.LINKAI }
            : local;
    }

    private static LinkAssistantOptions? MakeLocalAssistantOptions(HexSettings from)
    {
        // Hex's own llama-server is an OpenAI compatible one at its port, a router serving the models of the library
        bool llama = from.EditPredictionsProvider == PredictionProviders.LLAMACPP;
        // The chat model of the local AI roles: the code model when it chats, else the model set for chat (the router swaps it in)
        LocalAiRoleModels roles = LocalAiRoles.Resolve(from, LocalModelLibrary.Shared);
        if (llama ? roles.Chat is null && !ModelChats(from) : !ModelChats(from))
            return null;
        string provider = llama ? PredictionProviders.OPENAI : from.EditPredictionsProvider;
        string url = PredictionSettings.ApiUrl(from);
        string model = PredictionSettings.Model(from);
        if (llama && roles.TwoModels && from.LlamaCppConfig is { Router: true } routed)
            model = routed.EditName;
        bool reachable = provider switch
        {
            PredictionProviders.OLLAMA => OllamaChat.Url(url) is not null,
            PredictionProviders.OPENAI => OpenAiChat.Url(url) is not null,
            _ => false,
        };
        if (model.Length == 0 || !reachable)
            return null;
        return new LinkAssistantOptions(url, model, from.EditPredictionsSystemPrompt, from.TuningFor(LocalAiRole.Chat))
        {
            Provider = provider,
            ApiKey = provider == PredictionProviders.OPENAI && !llama ? PredictionSettings.ApiKey(from) : null,
        };
    }

    /// <summary>
    /// Whether the model picked for code can chat, follow instructions and answer: not an edit prediction model (Zeta 2
    /// and 2.1, by their prompt format or the catalog's word), which only rewrites code. A model Hex knows nothing of is
    /// taken to chat.
    /// </summary>
    internal static bool ModelChats(HexSettings from)
    {
        if (from.EditPredictionsProvider == PredictionProviders.LLAMACPP && LlamaCppModels.OfFile(from.LlamaCppModel) is { } known)
            return known.Chats;
        string format = from.EditPredictionsPromptFormat == PredictionFormats.INFER
            ? PredictionFormats.Infer(PredictionSettings.Model(from)).Name
            : from.EditPredictionsPromptFormat;
        return format is not (PredictionFormats.ZETA2 or PredictionFormats.ZETA21);
    }
}
