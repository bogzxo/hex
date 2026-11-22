using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

// Edit prediction's settings (docs/edit-prediction-plan.md, 6 and 7): a model asked what is typed next, its provider, URL,
// key and model. All of them are user settings but edit_predictions_workspace, which a folder sets to switch predictions
// off there, and edit_predictions_exclude, which a folder adds patterns to and can't take any away from. A folder's file
// can't switch predictions on, point them at another server or change the key, see WorkspaceKeys.
internal sealed partial class HexSettings
{
    /// <summary>The key of the patterns of files never sent, the one key of these whose folder value adds to the user's.</summary>
    public const string EDIT_PREDICTIONS_EXCLUDE = "edit_predictions_exclude";

    /// <summary>Where Ollama listens as it comes, the server's own address, which is also the URL these settings come with.</summary>
    public const string OLLAMA_URL = "http://localhost:11434";

    /// <summary>The same server's OpenAI shaped completions, what the URL becomes when the provider is switched to openai.</summary>
    public const string OLLAMA_OPENAI_URL = "http://localhost:11434/v1/completions";

    /// <summary>
    /// The files never sent to a model as they come: environment files, keys and certificates, files named for secrets or
    /// credentials, and the files package managers keep their tokens in. The user can add to them and a folder can too.
    /// </summary>
    public static readonly string[] UsualEditPredictionsExclude = ["**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.pfx", "**/*.p12",
        "**/id_rsa*", "**/id_ed25519*", "**/*secret*", "**/*credential*", "**/.npmrc", "**/.pypirc", "**/.netrc", "**/*.kdbx"];

    /// <summary>The languages nothing is predicted in as they come, by their ids: plain text and Markdown, prose more than code.</summary>
    public static readonly string[] UsualEditPredictionsDisabledLanguages = ["plaintext", "markdown"];

    /// <summary>Whether a model is asked what comes next as the code is typed. Off until the user switches it on, and only the user can.</summary>
    public bool EditPredictions;

    public string EditPredictionsInvocation = "after_typing";
    public bool PredictionShortcutOnly => EditPredictionsInvocation == "shortcut";

    /// <summary>What kind of server is asked: ollama for Ollama's own API, openai for any server with OpenAI's completions, link for the device Hex Link is attached to.</summary>
    public string EditPredictionsProvider = "ollama";

    /// <summary>The server's URL, Ollama's on this machine as it comes.</summary>
    public string EditPredictionsApiUrl = OLLAMA_URL;

    /// <summary>The model's name as the server knows it, empty until the user says.</summary>
    public string EditPredictionsModel = string.Empty;

    /// <summary>How the prompt is written for the model: infer works it out from the model's name, the rest name a family.</summary>
    public string EditPredictionsPromptFormat = "infer";

    /// <summary>The most tokens a model writes for one prediction.</summary>
    public int EditPredictionsMaxTokens = 256;

    /// <summary>How many milliseconds typing has to pause for before a model is asked.</summary>
    public int EditPredictionsDebounce = 100;

    /// <summary>The languages nothing is predicted in, by their ids. Each change is a new array.</summary>
    public string[] EditPredictionsDisabledLanguages = UsualEditPredictionsDisabledLanguages;

    /// <summary>
    /// The globs of files never sent, as the rows read them: the user's, or while a folder sets the key the folder's own.
    /// What is in force is <see cref="EditPredictionsExcludeInForce"/>, both of them together. Each change is a new array.
    /// </summary>
    public string[] EditPredictionsExclude = UsualEditPredictionsExclude;

    /// <summary>Whether predictions are on in this folder, set to false in a folder's .hex/settings.hor to switch them off there.</summary>
    public bool EditPredictionsWorkspace = true;

    /// <summary>Whether this Hex offers its predictions to the devices paired with it over Hex Link.</summary>
    public bool EditPredictionsShare;

    /// <summary>Whether the notice of what is sent and where was given, the first time predictions were switched on. Given once.</summary>
    public bool EditPredictionsNoticeDone;

    /// <summary>
    /// The patterns of files never sent that are in force: the user's and, while the folder has some of its own, the
    /// folder's added to them. A folder can add patterns, never take one of the user's away.
    /// </summary>
    public string[] EditPredictionsExcludeInForce =>
        IsOverridden(EDIT_PREDICTIONS_EXCLUDE) && UserValue(EDIT_PREDICTIONS_EXCLUDE) is ListValue users
            ? [.. Patterns(users).Concat(EditPredictionsExclude).Distinct(StringComparer.Ordinal)]
            : EditPredictionsExclude;

    /// <summary>
    /// The API key, plain, in memory only. On disk it's protected by the system (PredictionKey), written the first time it's
    /// saved after it changed. Empty for none, when the environment's HEX_EDIT_PREDICTION_API_KEY is used instead.
    /// </summary>
    public string EditPredictionsApiKey
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            apiKeyOnDisk = null;
            apiKeyKept = null;
            EditPredictionsApiKeyUnreadable = false;
        }
    } = string.Empty;

    /// <summary>
    /// Whether the settings file had a protected key that couldn't be read back (made by another user or on another
    /// machine, or changed), so the key is empty and the user should be told to enter it again.
    /// </summary>
    public bool EditPredictionsApiKeyUnreadable { get; private set; }

    // The key as the file has it, protected, made once for the key it is so each save doesn't protect it again
    private string? apiKeyOnDisk;

    // A protected key whose store can't be reached now (the Keychain locked), kept as it was so a save doesn't lose it
    private string? apiKeyKept;

    // What the file is given for the key: the protected text, the one kept, or nothing
    private string ApiKeyOnDisk() =>
        EditPredictionsApiKey.Length > 0 ? apiKeyOnDisk ??= PredictionKey.Wrap(EditPredictionsApiKey) : apiKeyKept ?? string.Empty;

    // The key read out of the file: protected is unwrapped, anything else is a key typed in by hand, protected at the next save
    private void ReadApiKey(Dictionary<string, IRuntimeValue> values)
    {
        if (values.GetValueOrDefault("edit_predictions_api_key") is not StringValue text)
            return;

        string stored = text.Value.Trim();
        if (!PredictionKey.IsProtected(stored))
        {
            EditPredictionsApiKey = stored;
            return;
        }

        try
        {
            string key = PredictionKey.Unwrap(stored, out bool failed);
            EditPredictionsApiKey = key;
            EditPredictionsApiKeyUnreadable = failed;
            if (!failed)
                apiKeyOnDisk = stored;
        }
        catch (HexLinkSecretsUnavailableException)
        {
            EditPredictionsApiKey = string.Empty;
            apiKeyKept = stored;
        }
    }

    // The rows of edit prediction, one block of HexSettings.Rows
    private static HexSettingRow[] PredictionRows =>
    [
        Flag("edit_predictions", settings => settings.EditPredictions, (settings, value) => settings.EditPredictions = value),
        Choice("edit_predictions_invocation", settings => settings.EditPredictionsInvocation, (settings, value) => settings.EditPredictionsInvocation = value, "after_typing", "shortcut"),
        Choice("edit_predictions_provider", settings => settings.EditPredictionsProvider, (settings, value) => settings.EditPredictionsProvider = value, "ollama", "openai", "link"),
        Text("edit_predictions_api_url", settings => settings.EditPredictionsApiUrl, (settings, value) => settings.EditPredictionsApiUrl = value.Trim()),
        Special("edit_predictions_api_key",
            (settings, values) => settings.ReadApiKey(values),
            (settings, written) => written.Add("edit_predictions_api_key", settings.ApiKeyOnDisk())),
        Text("edit_predictions_model", settings => settings.EditPredictionsModel, (settings, value) => settings.EditPredictionsModel = value.Trim()),
        Choice("edit_predictions_prompt_format", settings => settings.EditPredictionsPromptFormat, (settings, value) => settings.EditPredictionsPromptFormat = value,
            "infer", "zeta2", "qwen", "code_llama", "star_coder", "deepseek_coder", "code_gemma", "codestral"),
        Whole("edit_predictions_max_tokens", settings => settings.EditPredictionsMaxTokens, (settings, value) => settings.EditPredictionsMaxTokens = value, 16.0f, 2048.0f),
        Whole("edit_predictions_debounce", settings => settings.EditPredictionsDebounce, (settings, value) => settings.EditPredictionsDebounce = value, 0.0f, 2000.0f),
        Special("edit_predictions_disabled_languages",
            (settings, values) =>
            {
                if (values.GetValueOrDefault("edit_predictions_disabled_languages") is ListValue languages) settings.EditPredictionsDisabledLanguages = Patterns(languages);
            },
            (settings, written) => written.Add("edit_predictions_disabled_languages", Horizon.HIDL.Library.Natives.List(settings.EditPredictionsDisabledLanguages))),
        Special(EDIT_PREDICTIONS_EXCLUDE,
            (settings, values) =>
            {
                if (values.GetValueOrDefault(EDIT_PREDICTIONS_EXCLUDE) is ListValue exclude) settings.EditPredictionsExclude = Patterns(exclude);
            },
            (settings, written) => written.Add(EDIT_PREDICTIONS_EXCLUDE, Horizon.HIDL.Library.Natives.List(settings.EditPredictionsExclude))),
        Flag("edit_predictions_workspace", settings => settings.EditPredictionsWorkspace, (settings, value) => settings.EditPredictionsWorkspace = value),
        Flag("edit_predictions_share", settings => settings.EditPredictionsShare, (settings, value) => settings.EditPredictionsShare = value),
        Flag("edit_predictions_notice_done", settings => settings.EditPredictionsNoticeDone, (settings, value) => settings.EditPredictionsNoticeDone = value)
    ];
}
