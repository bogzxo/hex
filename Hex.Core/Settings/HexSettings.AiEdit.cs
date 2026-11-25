namespace Hex.Core;

// AI edits (right-click, AI, Edit): which model does them and how. Empty model is automatic: the code model when it can
// follow instructions, else an installed instruct model of the catalog.
internal sealed partial class HexSettings
{
    /// <summary>The edit answer's limit as it comes, thinking included.</summary>
    public const int USUAL_AI_EDIT_MAX_TOKENS = 4096;

    /// <summary>The model that does AI edits: empty for automatic, for llama.cpp a .gguf path or a catalog id, else a model name as the server knows it.</summary>
    public string AiEditModel = string.Empty;

    /// <summary>Whether a model that can think does, before an edit.</summary>
    public bool AiEditThinking = true;

    /// <summary>The most tokens an edit may write, thinking included.</summary>
    public int AiEditMaxTokens = USUAL_AI_EDIT_MAX_TOKENS;

    /// <summary>Whether the code model can follow instructions: not an edit prediction model (Zeta 2, 2.1), which only rewrites code. One Hex knows nothing of is taken to chat.</summary>
    public bool CodeModelChats
    {
        get
        {
            if (EditPredictionsProvider == LLAMACPP && global::Hex.Core.LlamaCppModels.OfFile(LlamaCppModel) is { } known)
                return known.Chats;
            // The prompt format by its name (PredictionFormats', which Core doesn't see), or the one the model's name suggests
            string format = EditPredictionsPromptFormat.Trim().ToLowerInvariant();
            if (format is "" or "infer")
                format = (EditPredictionsProvider == LLAMACPP ? Path.GetFileName(LlamaCppModel) : EditPredictionsModel).ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(".", "").Contains("zeta2", StringComparison.Ordinal) ? "zeta2" : "other";
            return format is not ("zeta2" or "zeta21");
        }
    }

    /// <summary>
    /// The llama.cpp edit model's file the settings mean, or null when the code model does edits itself (or llama.cpp isn't
    /// the provider). An explicit ai_edit_model is a catalog id (its file in the models folder) or a path, whether or not it
    /// exists; automatic is the first of the catalog's edit models whose file is in the models folder or beside the code model.
    /// </summary>
    public string? LlamaCppEditFile
    {
        get
        {
            if (EditPredictionsProvider != LLAMACPP || LlamaCppModel.Length == 0)
                return null;
            string? file;
            if (AiEditModel.Length > 0)
                file = LlamaCppEditFileNamed(AiEditModel);
            else if (CodeModelChats)
                return null;
            else
                file = global::Hex.Core.LlamaCppModels.EditModels.Select(model => LlamaCppInstalled(model)).FirstOrDefault(found => found is not null);
            return file is not null && !SameFile(file, LlamaCppModel) ? file : null;
        }
    }

    /// <summary>The file an ai_edit_model means for llama.cpp: a catalog id's file in the models folder, else the path as given.</summary>
    public string LlamaCppEditFileNamed(string name)
    {
        if (global::Hex.Core.LlamaCppModels.Find(name) is { } catalog)
            return LlamaCppInstalled(catalog) ?? Path.Combine(LlamaCppModelsInForce, catalog.File);
        return name;
    }

    // The catalog model's file in the models folder or the code model's folder, null when it's in neither
    private string? LlamaCppInstalled(LlamaCppModel model)
    {
        LlamaCppDiskChecks++;
        string? beside = LlamaCppModel.Length > 0 ? Path.GetDirectoryName(LlamaCppModel) : null;
        foreach (string? folder in new[] { LlamaCppModelsInForce, beside })
        {
            if (string.IsNullOrEmpty(folder))
                continue;
            string path = Path.Combine(folder, model.File);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static bool SameFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // The name the router knows the edit model by: its catalog id, or its file's name and -edit
    private string LlamaCppEditAlias(string file)
    {
        string name = global::Hex.Core.LlamaCppModels.OfFile(file)?.Id ?? Path.GetFileNameWithoutExtension(file) + "-edit";
        return string.Equals(name, LlamaCppAlias, StringComparison.Ordinal) ? name + "-edit" : name;
    }

    // The rows of AI edits, one block of HexSettings.Rows
    private static HexSettingRow[] AiEditRows =>
    [
        Text("ai_edit_model", settings => settings.AiEditModel, (settings, value) =>
        {
            settings.AiEditModel = value.Trim();
            // The older key still sets the chat model, local_ai_chat_model read after it wins when the file has both
            if (settings.AiEditModel.Length > 0 && settings.IdOfName(settings.AiEditModel) is { Length: > 0 } id)
                settings.LocalAiChatModel = id;
        }),
        Flag("ai_edit_thinking", settings => settings.AiEditThinking, (settings, value) => settings.AiEditThinking = value),
        Whole("ai_edit_max_tokens", settings => settings.AiEditMaxTokens, (settings, value) => settings.AiEditMaxTokens = value, 512.0f, 16384.0f),
    ];
}
