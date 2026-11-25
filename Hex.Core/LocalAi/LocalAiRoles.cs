namespace Hex.Core;

/// <summary>What <see cref="LocalAiRoles.Resolve"/> says: the model that predicts, the one that chats, and whether they differ (the router swaps).</summary>
internal sealed record LocalAiRoleModels(LocalModel? Predict, LocalModel? Chat, bool TwoModels, bool ChatAutomatic = false);

/// <summary>
/// Which model plays which role (docs/local-ai-setup-plan.md, 2.5): the predictions' model (local_ai_predict_model) and the
/// chat model (local_ai_chat_model: AI Edit, Link Chat, the Local agent). One place says it for everything that asks.
/// </summary>
internal static class LocalAiRoles
{
    /// <summary>
    /// The two models. A model the library doesn't have (yet: it hasn't been scanned, or the file went) is still answered for
    /// from its id, so what was set up keeps working before the first scan. The chat model is the predictions' when none is set and it can chat.
    /// </summary>
    public static LocalAiRoleModels Resolve(HexSettings settings, LocalModelLibrary library)
    {
        LocalModel? predict = Lookup(settings, library, settings.LocalAiPredictModel);
        LocalModel? chat = Lookup(settings, library, settings.LocalAiChatModel);
        if (chat is null && predict is not null && predict.Caps.HasFlag(LocalModelCaps.Chat))
            chat = predict;
        // Still none: the router's edit model is the one AI Edit really asks, so it's the effective chat model, "automatic"
        bool automatic = false;
        if (chat is null && settings.EditPredictionsProvider == HexSettings.LLAMACPP && settings.LlamaCppConfig is { Router: true } config)
        {
            chat = FromFile(settings, library, config.EditModel!);
            automatic = chat is not null;
        }
        bool two = predict is not null && chat is not null && !string.Equals(predict.Id, chat.Id, StringComparison.OrdinalIgnoreCase);
        return new LocalAiRoleModels(predict, chat, two, automatic);
    }

    // The library's model for a file, else one made up from the file's name
    private static LocalModel? FromFile(HexSettings settings, LocalModelLibrary library, string file)
    {
        foreach (LocalModel model in library.Models)
            if (model.Path is { } path && string.Equals(path, file, StringComparison.OrdinalIgnoreCase))
                return model;
        LlamaCppModel? catalog = LlamaCppModels.OfFile(file);
        return new LocalModel("file:" + file, catalog?.Title ?? Path.GetFileNameWithoutExtension(file), LocalModelSource.File, file, 0,
            catalog is not null ? LocalModelLibrary.CatalogCaps(catalog) : LocalModelCaps.Predict | LocalModelCaps.Chat, null, null, null, catalog?.Id, LocalModelFit.Unknown, null);
    }

    /// <summary>Assigns a model (a LocalModel id, "" to clear) to a role, and keeps the older keys saying the same.</summary>
    public static void Set(HexSettings settings, LocalAiRole role, string modelId)
    {
        modelId = modelId.Trim();
        if (role == LocalAiRole.Predict)
            settings.LocalAiPredictModel = modelId;
        else
            settings.LocalAiChatModel = modelId;
        // Cleared: the older key goes too, or the next load would carry it over again
        if (modelId.Length == 0 && settings.EditPredictionsProvider == HexSettings.LLAMACPP)
        {
            if (role == LocalAiRole.Predict) settings.LlamaCppModel = string.Empty;
            else settings.AiEditModel = string.Empty;
        }
        settings.MirrorLocalAi();
    }

    /// <summary>Fills local_ai_* from the older keys once; the old keys stay as they were.</summary>
    public static void Migrate(HexSettings settings) => settings.MigrateLocalAi();

    /// <summary>The model of an id, from the library or made up from the id alone.</summary>
    internal static LocalModel? Lookup(HexSettings settings, LocalModelLibrary library, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (library.Find(id) is { } found)
            return found;
        if (LocalModelLibrary.PathOfId(id) is { } path)
        {
            string file = settings.LocalAiFileOf(id) ?? path;
            LlamaCppModel? catalog = LlamaCppModels.OfFile(file);
            LocalModelCaps caps = catalog is not null ? LocalModelLibrary.CatalogCaps(catalog) : LocalModelCaps.Predict | LocalModelCaps.Chat;
            long bytes = 0;
            try { bytes = File.Exists(file) ? new FileInfo(file).Length : 0; }
            catch (IOException) { }
            return new LocalModel(id, catalog?.Title ?? Path.GetFileNameWithoutExtension(file), LocalModelSource.File, file, bytes, caps, null, null, null, catalog?.Id,
                LocalModelFit.Unknown, null);
        }
        if (id.StartsWith("catalog:", StringComparison.OrdinalIgnoreCase) && LlamaCppModels.Find(id[8..]) is { } entry)
            return new LocalModel(id, entry.Title, LocalModelSource.Catalog, null, entry.Size, LocalModelLibrary.CatalogCaps(entry), null, null, null, entry.Id, LocalModelFit.Unknown, null);
        if (id.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
            return new LocalModel(id, id[7..], LocalModelSource.Ollama, null, 0, LocalModelCaps.Predict | LocalModelCaps.Chat, null, null, null, null, LocalModelFit.Unknown, null);
        // A linked device's model that isn't listed now (the device is off or not linked): still the one chosen, said by its name
        if (LinkAiModelId.TryParse(id, out _, out string linkedName))
            return new LocalModel(id, linkedName, LocalModelSource.Linked, null, 0, LocalModelCaps.Chat, null, null, null, null, LocalModelFit.Unknown, null, Device: null);
        return null;
    }
}
