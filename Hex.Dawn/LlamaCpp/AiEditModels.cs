namespace Hex.Dawn;

/// <summary>
/// The model an edit by instruction asks: the provider ("ollama" or "openai", Hex's own llama-server being "openai" at
/// the prediction settings' URL), its URL and key, the name to put in the request's "model", a human name for the UI,
/// whether the card's memory has to swap it in (it isn't the code model, so predictions pause meanwhile), and whether it
/// is let to think.
/// </summary>
internal sealed record AiEditModel(string Provider, string Url, string Model, string? ApiKey, string Title, bool Swaps, bool Thinks, string? Device = null);

/// <summary>Which model AI edits use now (docs/ai-edit-plan.md, 1): the code model when it can follow instructions, else an edit model.</summary>
internal static class AiEditModels
{
    /// <summary>
    /// The local models an agent chat may pick (docs/local-agent-plan.md), the one to start with first: the code model
    /// when it can chat, else the edit model, then every other installed model that can chat (llama.cpp: the catalog's
    /// chat models whose files are in the models folder or beside the code model; Ollama: the server's models with tools,
    /// left to the caller to ask, so here only the code and edit models). Empty when none can.
    /// </summary>
    public static IReadOnlyList<AiEditModel> AgentModels(HexSettings settings)
    {
        IReadOnlyList<AiEditModel> local = LocalAgentModels(settings);
        if (HexAi.Off)
            return local;
        IReadOnlyList<AiEditModel> linked = LinkedModels(settings);
        return linked.Count == 0 ? local : [.. local, .. linked];
    }

    /// <summary>
    /// The chat models of linked devices that serve AI (docs/hex-link-plan.md, 5.7), each with the device it runs on: the library's
    /// linked models when this Hex has the links, else (the Local agent is a process of its own) the ones the loopback file
    /// lists, asked through the loopback endpoint with its token.
    /// </summary>
    public static IReadOnlyList<AiEditModel> LinkedModels(HexSettings settings)
    {
        var models = new List<AiEditModel>();
        bool thinks = settings.AiEditThinking;
        foreach (LocalModel model in LocalModelLibrary.Shared.Models)
            if (model.Source == LocalModelSource.Linked && model.Caps.HasFlag(LocalModelCaps.Chat))
                models.Add(new AiEditModel(PredictionProviders.LINKAI, string.Empty, model.Id, null, model.Name, false, thinks, model.Device));
        if (models.Count == 0 && LinkAiLoopbackFile.Read() is { } file)
            foreach (LinkAiLoopbackModel model in file.Models)
                models.Add(new AiEditModel(PredictionProviders.OPENAI, file.Url, model.Id, file.Token, model.Name.Length > 0 ? model.Name : LinkAiModelId.TryParse(model.Id, out _, out string name) ? name : model.Id, false, thinks, model.Device));
        return models;
    }

    /// <summary>The chat models of this machine alone (what <see cref="AgentModels"/> is without the linked ones), the ones it serves to linked devices.</summary>
    public static IReadOnlyList<AiEditModel> LocalAgentModels(HexSettings settings)
    {
        var models = new List<AiEditModel>();
        if (HexAi.Off)
            return models;

        string provider = PredictionSettings.Provider(settings);
        string? key = PredictionSettings.ApiKey(settings) is { Length: > 0 } apiKey ? apiKey : null;
        bool thinks = settings.AiEditThinking;

        void Add(AiEditModel model)
        {
            if (model.Model.Length > 0 && !models.Any(have => string.Equals(have.Model, model.Model, StringComparison.Ordinal)))
                models.Add(model);
        }

        if (provider == HexSettings.LLAMACPP)
        {
            LlamaCppServerConfig? config = settings.LlamaCppConfig;
            if (config is null)
                return models;
            string url = PredictionSettings.ApiUrl(settings);
            // The one Resolve picks: the code model when it chats, else the edit model
            if (DawnScene.ModelChats(settings))
                Add(new AiEditModel(PredictionProviders.OPENAI, url, config.Alias, key, TitleOf(config.Model), false, thinks));
            else if (config.Router && File.Exists(config.EditModel))
                Add(new AiEditModel(PredictionProviders.OPENAI, url, config.EditName, key, TitleOf(config.EditModel), true, thinks));
            foreach ((string alias, string file) in config.Extras)
                if (File.Exists(file))
                    Add(new AiEditModel(PredictionProviders.OPENAI, url, alias, key, TitleOf(file), !string.Equals(alias, config.Alias, StringComparison.Ordinal), thinks));
            return models;
        }

        if (provider is PredictionProviders.OLLAMA or PredictionProviders.OPENAI && settings.EditPredictionsApiUrl.Length > 0)
        {
            string code = settings.EditPredictionsModel.Trim();
            string chosen = settings.AiEditModel.Trim();
            if (code.Length > 0 && DawnScene.ModelChats(settings))
                Add(new AiEditModel(provider, settings.EditPredictionsApiUrl, code, key, code, false, thinks));
            if (chosen.Length > 0)
                Add(new AiEditModel(provider, settings.EditPredictionsApiUrl, chosen, key, chosen, !string.Equals(chosen, code, StringComparison.Ordinal), thinks));

            // And the library's Ollama models that can chat (llama.cpp's are the router config's, which lists the library's)
            foreach (LocalModel local in ModelPicker.AgentChoices(LocalModelLibrary.Shared.Models).Where(model => model.Source == LocalModelSource.Ollama))
                Add(new AiEditModel(provider, settings.EditPredictionsApiUrl, local.Name, key, local.Name, !string.Equals(local.Name, code, StringComparison.Ordinal), thinks));
        }
        return models;
    }

    /// <summary>The agent model of that name (an AiEditModel.Model of <see cref="AgentModels"/>), else the first; null when there's none.</summary>
    public static AiEditModel? ForAgent(HexSettings settings, string? model)
    {
        IReadOnlyList<AiEditModel> models = AgentModels(settings);
        string name = model?.Trim() ?? string.Empty;
        // No name asked: the chat role's own model when it is a linked one (the local ones are listed first otherwise)
        if (name.Length == 0 && LinkAiModelId.Is(settings.LocalAiChatModel))
            name = settings.LocalAiChatModel.Trim();
        if (name.Length > 0)
            foreach (AiEditModel candidate in models)
                if (string.Equals(candidate.Model, name, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.Title, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
        return models.Count > 0 ? models[0] : null;
    }

    /// <summary>The model AI edits use now, or null with the reason as an ai_edit.problem_* key (no_model, not_set_up).</summary>
    public static AiEditModel? Resolve(HexSettings settings, out string? problem)
    {
        problem = null;
        if (HexAi.Off)
            return null;

        // A linked device's model chosen for the chat role: asked over Link, whatever is here
        string role = settings.LocalAiChatModel.Trim();
        if (LinkAiModelId.TryParse(role, out string fingerprint, out string name))
        {
            LocalModel? listed = LocalModelLibrary.Shared.Find(role);
            string device = listed?.Device ?? string.Empty;
            // Said by the name the device gave it, not the alias it is asked by
            return new AiEditModel(PredictionProviders.LINKAI, string.Empty, role, null, listed?.Name is { Length: > 0 } shown ? shown : name, false, settings.AiEditThinking, device.Length > 0 ? device : fingerprint);
        }

        AiEditModel? local = ResolveLocal(settings, out problem);
        if (local is not null)
            return local;

        // Nothing here to chat with, and exactly one device serves AI: it is the chat model, the menu says via which
        if (LinkedFallback(settings) is { } fallback)
        {
            problem = null;
            return fallback;
        }

        return null;
    }

    /// <summary>
    /// The linked model used when this machine has none of its own (docs/hex-link-plan.md, 5.7): the first chat model of the one
    /// device that serves AI, null when none does or when more than one device could (the person picks then).
    /// </summary>
    public static AiEditModel? LinkedFallback(HexSettings settings)
    {
        if (HexAi.Off)
            return null;
        IReadOnlyList<AiEditModel> linked = [.. LinkedModels(settings).Where(model => model.Provider == PredictionProviders.LINKAI)];
        if (linked.Count == 0 || linked.Select(model => model.Device).Distinct(StringComparer.Ordinal).Count() != 1)
            return null;
        return linked[0];
    }

    private static AiEditModel? ResolveLocal(HexSettings settings, out string? problem)
    {
        problem = null;

        string provider = PredictionSettings.Provider(settings);
        string? key = PredictionSettings.ApiKey(settings) is { Length: > 0 } apiKey ? apiKey : null;
        bool thinks = settings.AiEditThinking;
        string chosen = settings.AiEditModel.Trim();

        if (provider == HexSettings.LLAMACPP)
        {
            LlamaCppServerConfig? config = settings.LlamaCppConfig;
            if (config is null)
            {
                problem = "not_set_up";
                return null;
            }
            string url = PredictionSettings.ApiUrl(settings);
            // The chat model of the local AI roles (the predictions' model when it chats and no other is set)
            LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
            if (config.Router)
            {
                if (!File.Exists(config.EditModel))
                {
                    problem = "not_set_up";
                    return null;
                }
                return new AiEditModel(PredictionProviders.OPENAI, url, config.EditName, key, roles.TwoModels && roles.Chat is { } chatModel ? chatModel.Name : TitleOf(config.EditModel!), true, thinks);
            }
            // One model does both: the code model, unless the person named a file that isn't there or the code model can't chat
            if (chosen.Length > 0 && settings.LlamaCppEditFileNamed(chosen) is { } named && !File.Exists(named))
            {
                problem = "not_set_up";
                return null;
            }
            if (chosen.Length == 0 && roles.Chat is null && !DawnScene.ModelChats(settings))
            {
                problem = "no_model";
                return null;
            }
            return new AiEditModel(PredictionProviders.OPENAI, url, config.Alias, key, TitleOf(config.Model), false, thinks);
        }

        if (provider is not (PredictionProviders.OLLAMA or PredictionProviders.OPENAI))
        {
            problem = "no_model";
            return null;
        }
        string code = settings.EditPredictionsModel.Trim();
        string model = chosen.Length > 0 ? chosen : code;
        if (settings.EditPredictionsApiUrl.Length == 0)
        {
            problem = "not_set_up";
            return null;
        }
        if (model.Length == 0 || (chosen.Length == 0 && !DawnScene.ModelChats(settings)))
        {
            problem = "no_model";
            return null;
        }
        return new AiEditModel(provider, settings.EditPredictionsApiUrl, model, key, model, !string.Equals(model, code, StringComparison.Ordinal), thinks);
    }

    /// <summary>The llama.cpp edit model's file the settings mean, or null when the code model does edits itself.</summary>
    public static string? LlamaCppEditFile(HexSettings settings) => settings.LlamaCppEditFile;

    // The catalog's name for a model's file, else the file's name
    private static string TitleOf(string? file) =>
        file is null ? string.Empty
            : LlamaCppModels.OfFile(file)?.Title ?? LocalModelLibrary.Shared.Find(LocalModelLibrary.FileId(file))?.Name ?? Path.GetFileNameWithoutExtension(file);
}
