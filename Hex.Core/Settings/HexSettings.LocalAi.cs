namespace Hex.Core;

// The local AI settings (docs/local-ai-setup-plan.md, 2.5): which model each role uses, the engine, the extra model folders
// and the user's preset overrides. The older llamacpp_model / edit_predictions_model / ai_edit_model keys stay readable,
// LocalAiRoles.Migrate fills these from them once.
internal sealed partial class HexSettings
{
    /// <summary>The engine local AI runs on: Hex's own llama-server.</summary>
    public const string LOCAL_AI_LLAMACPP = "llamacpp";

    /// <summary>The engine local AI runs on: an Ollama server.</summary>
    public const string LOCAL_AI_OLLAMA = "ollama";

    /// <summary>Every engine there is, as the settings write them.</summary>
    public static readonly string[] LocalAiEngines = [LOCAL_AI_LLAMACPP, LOCAL_AI_OLLAMA];

    /// <summary>The LocalModel id that predicts, empty for none.</summary>
    public string LocalAiPredictModel = string.Empty;

    /// <summary>The LocalModel id that chats (AI Edit, Link Chat, the Local agent), empty for the predictions' model when that can chat.</summary>
    public string LocalAiChatModel = string.Empty;

    /// <summary>llamacpp or ollama.</summary>
    public string LocalAiEngine = LOCAL_AI_LLAMACPP;

    /// <summary>Extra folders scanned for models, separated by semicolons or new lines.</summary>
    public string LocalAiModelFolders = string.Empty;

    /// <summary>The ids of the models starred in the picker, separated by semicolons. They sort first, and the cycle command goes through them.</summary>
    public string LocalAiFavourites = string.Empty;

    /// <summary>The user's preset overrides as JSON: { "&lt;model id&gt;": { fields } }.</summary>
    public string LocalAiPresets = string.Empty;

    /// <summary>The modes of the Local AI window, as local_ai_ui_mode writes them; "auto" lets <see cref="LocalAiUiModeInForce"/> decide.</summary>
    public static readonly string[] LocalAiUiModes = ["auto", "simple", "advanced"];

    /// <summary>auto, simple or advanced: which mode the Local AI window opens in.</summary>
    public string LocalAiUiMode = "auto";

    /// <summary>The id of the recommended setup in use (LocalAiSetups), empty for none.</summary>
    public string LocalAiSetup = string.Empty;

    /// <summary>
    /// The mode the Local AI window shows: what was chosen, else (auto) advanced for someone who has changed advanced-only
    /// settings before (a llama.cpp port, context, offload, arguments, alias or folders, or presets of their own), else simple.
    /// </summary>
    public string LocalAiUiModeInForce => LocalAiUiMode switch
    {
        "simple" => "simple",
        "advanced" => "advanced",
        // Presets a setup wrote (a profile other than the recommended one) don't count: only the user's own do
        // Nothing set up (no role assigned, no setup in use) is a first run: simple with the welcome, whatever folders were set before
        _ when LocalAiPredictModel.Length == 0 && LocalAiChatModel.Length == 0 && LocalAiSetup.Length == 0 => "simple",
        _ => (LocalAiPresets.Length > 0 && LocalAiSetup.Length == 0) || LlamaCppPort != LlamaCppServerArgs.USUAL_PORT || LlamaCppContext != LlamaCppServerArgs.USUAL_CONTEXT
            || LlamaCppOffload != LlamaCppServerArgs.AUTO || LlamaCppExtraArgs.Length > 0 || LlamaCppFolder.Length > 0 || LlamaCppModels.Length > 0
            || LocalAiModelFolders.Length > 0 || LlamaCppAlias != "local" ? "advanced" : "simple",
    };

    /// <summary>
    /// Carries the older keys over to local_ai_* once (llamacpp_model or edit_predictions_model to the predict model,
    /// ai_edit_model to the chat model), then keeps the older keys saying what local_ai_* does so everything that reads them
    /// still works. The older keys stay where they were.
    /// </summary>
    internal void MigrateLocalAi()
    {
        // Only fills what is empty, and clearing a role clears its older key too, so a cleared role is not brought back
        if (LocalAiPredictModel.Length == 0)
            LocalAiPredictModel = LegacyPredictId();
        if (LocalAiChatModel.Length == 0)
            LocalAiChatModel = IdOfName(AiEditModel);
        MirrorLocalAi();
    }

    private string LegacyPredictId()
    {
        if (EditPredictionsProvider == LLAMACPP)
            return LlamaCppModel.Length > 0 ? LocalModelLibrary.FileId(LlamaCppModel) : string.Empty;
        return EditPredictionsModel.Length > 0 ? "ollama:" + EditPredictionsModel : string.Empty;
    }

    /// <summary>The LocalModel id a model named the old way means: a catalog id, title or file name, a path, or an Ollama name.</summary>
    internal string IdOfName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return string.Empty;
        if (name.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("catalog:", StringComparison.OrdinalIgnoreCase))
            return name;
        LlamaCppModel? catalog = global::Hex.Core.LlamaCppModels.All.FirstOrDefault(model =>
            string.Equals(model.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(model.Title, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model.File, name, StringComparison.OrdinalIgnoreCase));
        if (catalog is not null)
            return LlamaCppInstalled(catalog) is { } file ? LocalModelLibrary.FileId(file) : "catalog:" + catalog.Id;
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || name.Contains('\\') || name.Contains('/'))
            return LocalModelLibrary.FileId(name);
        return EditPredictionsProvider == LLAMACPP ? string.Empty : "ollama:" + name;
    }

    /// <summary>The file of a file: or catalog: id on disk for llama.cpp, null for another kind or one that isn't there.</summary>
    internal string? LocalAiFileOf(string id)
    {
        if (LocalModelLibrary.PathOfId(id) is { } path)
            return LocalModelLibrary.Shared.Find(id)?.Path ?? path;
        if (id.StartsWith("catalog:", StringComparison.OrdinalIgnoreCase) && global::Hex.Core.LlamaCppModels.Find(id[8..]) is { } catalog)
            return LlamaCppInstalled(catalog);
        return null;
    }

    /// <summary>Writes what local_ai_* says into llamacpp_model and ai_edit_model, the keys the older code reads.</summary>
    internal void MirrorLocalAi()
    {
        if (EditPredictionsProvider != LLAMACPP)
            return;
        if (LocalAiPredictModel.Length > 0 && LocalAiFileOf(LocalAiPredictModel) is { } code && File.Exists(code) && !SameFile(code, LlamaCppModel))
            LlamaCppModel = code;
        if (LocalAiChatModel.Length == 0)
            return;
        if (string.Equals(LocalAiChatModel, LocalAiPredictModel, StringComparison.OrdinalIgnoreCase))
        {
            AiEditModel = string.Empty;
            return;
        }
        string named = LocalAiChatModel;
        if (LocalAiFileOf(LocalAiChatModel) is { } chat)
            named = global::Hex.Core.LlamaCppModels.OfFile(chat)?.Id ?? chat;
        else if (LocalAiChatModel.StartsWith("catalog:", StringComparison.OrdinalIgnoreCase))
            named = LocalAiChatModel[8..];
        // The catalog's own spelling (Mellum2.1 becomes mellum2.1) is what the older code looks up
        AiEditModel = named;
    }

    /// <summary>
    /// The sampling a role's model is asked with: its preset's for llama.cpp, with the global edit_predictions_* ones as the
    /// fallback (another provider, or no model known).
    /// </summary>
    public PredictionTuning TuningFor(LocalAiRole role)
    {
        PredictionTuning global = EditPredictionsTuning;
        if (EditPredictionsProvider != LLAMACPP)
            return global;
        LocalAiRoleModels roles = LocalAiRoles.Resolve(this, LocalModelLibrary.Shared);
        LocalModel? model = role == LocalAiRole.Predict ? roles.Predict : roles.Chat;
        return model is null ? global : LocalModelPresets.Tuning(LocalModelPresets.For(model, this), global).Clamped();
    }

    private static HexSettingRow[] LocalAiRows =>
    [
        Text("local_ai_predict_model", settings => settings.LocalAiPredictModel, (settings, value) => settings.LocalAiPredictModel = value.Trim()),
        Text("local_ai_chat_model", settings => settings.LocalAiChatModel, (settings, value) => settings.LocalAiChatModel = value.Trim()),
        Choice("local_ai_engine", settings => settings.LocalAiEngine, (settings, value) => settings.LocalAiEngine = value, LocalAiEngines),
        Text("local_ai_model_folders", settings => settings.LocalAiModelFolders, (settings, value) => settings.LocalAiModelFolders = value.Trim()),
        Text("local_ai_presets", settings => settings.LocalAiPresets, (settings, value) => settings.LocalAiPresets = value.Trim()),
        Text("local_ai_favourites", settings => settings.LocalAiFavourites, (settings, value) => settings.LocalAiFavourites = value.Trim()),
        Choice("local_ai_ui_mode", settings => settings.LocalAiUiMode, (settings, value) => settings.LocalAiUiMode = value, LocalAiUiModes),
        Text("local_ai_setup", settings => settings.LocalAiSetup, (settings, value) => settings.LocalAiSetup = value.Trim()),
    ];
}
