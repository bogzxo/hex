namespace Hex.Core;

/// <summary>
/// What the settings say about edit prediction, read the way the rest of it needs them (docs/edit-prediction-plan.md,
/// 4.2, 6 and 7). Hex keeps one <see cref="HexSettings"/>, the user's with the open folder's workspace settings laid over
/// them, so every member takes that one object. A folder's file only reaches edit_predictions_workspace (it switches
/// predictions off there) and edit_predictions_exclude (it adds patterns), everything else here is the user's own.
/// Plain values only, the options the prediction engine runs with are made from these by whoever starts it.
/// </summary>
internal static class PredictionSettings
{
    /// <summary>
    /// Whether predictions are on at all in this folder: not No AI this run, switched on in the user's settings, and not
    /// switched off by the folder. Says nothing of a language, a file or whether a provider is set up.
    /// </summary>
    public static bool On(HexSettings settings) => !HexAi.Off && settings.EditPredictions && settings.EditPredictionsWorkspace;

    /// <summary>
    /// Whether predictions are on for a file of a language, by its id (csharp, plaintext): <see cref="On(HexSettings)"/>
    /// and the language not one of the disabled ones. Whether the file itself may be sent is <see cref="Exclude"/>'s.
    /// </summary>
    public static bool On(HexSettings settings, string language) => On(settings) && !LanguageOff(settings, language);

    /// <summary>Whether a language, by its id, is one predictions are switched off for.</summary>
    public static bool LanguageOff(HexSettings settings, string language) =>
        Array.Exists(settings.EditPredictionsDisabledLanguages, off => string.Equals(off, language, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The globs (HexGlob's) of files never sent and never used as context: the user's patterns with the open folder's
    /// added to them. The defaults are in the user's list until the user takes them out.
    /// </summary>
    public static IReadOnlyList<string> Exclude(HexSettings settings) => settings.EditPredictionsExcludeInForce;

    /// <summary>Which provider is asked: <c>ollama</c>, <c>openai</c> or <c>link</c>.</summary>
    public static string Provider(HexSettings settings) => settings.EditPredictionsProvider;

    /// <summary>
    /// The server's URL: as the user gave it, trimmed (the provider adds its own path to it), or for llama.cpp the
    /// completions of the llama-server Hex runs, on this machine at its port.
    /// </summary>
    public static string ApiUrl(HexSettings settings) =>
        settings.EditPredictionsProvider == HexSettings.LLAMACPP ? LlamaCppServerArgs.CompletionsUrl(settings.LlamaCppPort) : settings.EditPredictionsApiUrl;

    /// <summary>The model's name as the server knows it, empty for none set; for llama.cpp the name Hex starts it with.</summary>
    public static string Model(HexSettings settings) =>
        settings.EditPredictionsProvider == HexSettings.LLAMACPP ? (settings.LlamaCppConfig?.Alias ?? string.Empty) : settings.EditPredictionsModel;

    /// <summary>The prompt format: <c>infer</c> (from the model's name), or <c>zeta2</c>, <c>qwen</c>, <c>code_llama</c>, <c>star_coder</c>, <c>deepseek_coder</c>, <c>code_gemma</c>, <c>codestral</c>.</summary>
    public static string PromptFormat(HexSettings settings) => settings.EditPredictionsPromptFormat;

    /// <summary>The kind of model: <c>auto</c> (from its name and the server), <c>edit_model</c>, <c>fim</c>, <c>instruct</c> or <c>fim_instruct</c>.</summary>
    public static string Kind(HexSettings settings) => settings.EditPredictionsKind;

    /// <summary>The most tokens one prediction may have, 16 to 2048.</summary>
    public static int MaxTokens(HexSettings settings) => settings.EditPredictionsMaxTokens;

    /// <summary>How long typing has to pause for before a model is asked, 0 to 2 seconds.</summary>
    public static TimeSpan Debounce(HexSettings settings) => TimeSpan.FromMilliseconds(settings.EditPredictionsDebounce);

    /// <summary>Whether this Hex offers its predictions to the devices paired with it over Hex Link.</summary>
    public static bool Share(HexSettings settings) => settings.EditPredictionsShare;

    /// <summary>
    /// The API key to send: the one in the settings, or when that's empty the environment's HEX_EDIT_PREDICTION_API_KEY.
    /// Empty for none. Never to be logged, shown or put in a message.
    /// </summary>
    public static string ApiKey(HexSettings settings) =>
        settings.EditPredictionsApiKey.Length > 0 ? settings.EditPredictionsApiKey : PredictionKey.FromEnvironment();

    /// <summary>Whether the settings file had a protected key that couldn't be read back, for a notice asking for it again.</summary>
    public static bool ApiKeyUnreadable(HexSettings settings) => settings.EditPredictionsApiKeyUnreadable;

    /// <summary>
    /// Whether a provider is set up enough to be asked: a URL and a model, Hex Link, whose device has its own, or llama.cpp
    /// with a llama-server and a model to run. Whether a device is attached is Hex Link's to say, whether the server is up
    /// the server's.
    /// </summary>
    public static bool Configured(HexSettings settings) => settings.EditPredictionsProvider switch
    {
        "link" => true,
        HexSettings.LLAMACPP => settings.LlamaCppConfig is not null,
        _ => settings.EditPredictionsApiUrl.Length > 0 && settings.EditPredictionsModel.Length > 0,
    };

    /// <summary>
    /// The URL to have after the provider is changed: Ollama's own address becomes its OpenAI completions when the
    /// provider goes to openai, and back to the address when it goes to ollama from exactly those. Any other URL is the
    /// user's and stays.
    /// </summary>
    public static string UrlForProvider(string provider, string url) => (provider, url) switch
    {
        ("openai", HexSettings.OLLAMA_URL) => HexSettings.OLLAMA_OPENAI_URL,
        ("ollama", HexSettings.OLLAMA_OPENAI_URL) => HexSettings.OLLAMA_URL,
        _ => url
    };
}
