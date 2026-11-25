namespace Hex.Core;

// llama.cpp's settings: where its builds and models go, the llama-server Hex runs and the model it runs, and how. All of
// them are user settings, a folder's file can't point Hex at a program to run (see WorkspaceKeys). Set by Set Up
// llama.cpp, each one changeable on its own after.
internal sealed partial class HexSettings
{
    /// <summary>The provider that is a llama-server Hex runs itself.</summary>
    public const string LLAMACPP = "llamacpp";

    /// <summary>The folder llama.cpp's builds are unpacked in, empty for llama.cpp in Hex's data folder.</summary>
    public string LlamaCppFolder = string.Empty;

    /// <summary>The folder models are downloaded to, empty for models beside the builds.</summary>
    public string LlamaCppModels = string.Empty;

    /// <summary>The llama-server program Hex runs, empty until it's set up.</summary>
    public string LlamaCppServer = string.Empty;

    /// <summary>The model's file (.gguf) it runs, empty until one is picked.</summary>
    public string LlamaCppModel = string.Empty;

    /// <summary>The name the model answers to, what predictions, Link Chat and edits by instruction ask for.</summary>
    public string LlamaCppAlias = "local";

    /// <summary>The port it listens on, on this machine only.</summary>
    public int LlamaCppPort = LlamaCppServerArgs.USUAL_PORT;

    /// <summary>The context it's started with, in tokens.</summary>
    public int LlamaCppContext = LlamaCppServerArgs.USUAL_CONTEXT;

    /// <summary>Where the model's weights go: auto (llama.cpp fits them), gpu, experts (a mixture of experts' in RAM) or cpu.</summary>
    public string LlamaCppOffload = LlamaCppServerArgs.AUTO;

    /// <summary>Anything else llama-server is started with, split as a shell splits it but never run through one.</summary>
    public string LlamaCppExtraArgs = string.Empty;

    /// <summary>Whether Hex starts it when something needs it (predictions, Link Chat, a paired device) and stops it when nothing does.</summary>
    public bool LlamaCppAutostart = true;

    /// <summary>The folder builds go in, the setting's or llama.cpp in Hex's data folder.</summary>
    public string LlamaCppFolderInForce => LlamaCppFolder.Length > 0 ? LlamaCppFolder : Path.Combine(HexData.Root, "llama.cpp");

    /// <summary>The folder models go in, the setting's or models beside the builds.</summary>
    public string LlamaCppModelsInForce => LlamaCppModels.Length > 0 ? LlamaCppModels : Path.Combine(LlamaCppFolderInForce, "models");

    /// <summary>What llama-server is started with, null until a program and a model are set.</summary>
    public LlamaCppServerConfig? LlamaCppConfig
    {
        get
        {
            // Read every frame: rebuilt only when what feeds it changes, and the disk is looked at again at most every few seconds
            // so a model downloaded meanwhile is still noticed.
            LlamaCppConfigKey key = new(LlamaCppServer, LlamaCppModel, LlamaCppAlias, LlamaCppPort, LlamaCppContext, LlamaCppOffload, LlamaCppExtraArgs,
                LlamaCppFolder, LlamaCppModels, AiEditModel, EditPredictionsProvider, EditPredictionsPromptFormat, EditPredictionsModel, LocalAiPresets, LocalAiPredictModel, LocalAiChatModel);
            long now = LlamaCppClock();
            LlamaCppConfigCache? cache = llamaCppConfigCache;
            if (cache is not null && cache.Key.Equals(key) && now - cache.At < LLAMACPP_DISK_RECHECK_MS)
                return cache.Config;
            LlamaCppServerConfig? config = BuildLlamaCppConfig();
            llamaCppConfigCache = new LlamaCppConfigCache(key, now, config);
            return config;
        }
    }

    /// <summary>How long the files on disk are trusted for, in milliseconds.</summary>
    internal const long LLAMACPP_DISK_RECHECK_MS = 30000;

    /// <summary>Drops the config kept, so the next read looks at the disk: called as the library of models is scanned, which is when the files changed.</summary>
    internal void ForgetLlamaCppDisk() => llamaCppConfigCache = null;

    /// <summary>The clock the disk re-check is timed by, in milliseconds. A test swaps it.</summary>
    internal Func<long> LlamaCppClock = static () => Environment.TickCount64;

    /// <summary>How many times the files of llama.cpp's models were looked for on disk, for tests.</summary>
    internal int LlamaCppDiskChecks;

    // A struct: the key is made on every read (every frame), so it mustn't be a heap object each time
    private readonly record struct LlamaCppConfigKey(string Server, string Model, string Alias, int Port, int Context, string Offload, string ExtraArgs,
        string Folder, string Models, string AiEditModel, string Provider, string PromptFormat, string PredictionsModel, string Presets, string PredictModel, string ChatModel);

    private sealed record LlamaCppConfigCache(LlamaCppConfigKey Key, long At, LlamaCppServerConfig? Config);

    private volatile LlamaCppConfigCache? llamaCppConfigCache;

    private LlamaCppServerConfig? BuildLlamaCppConfig()
    {
        if (LlamaCppServer.Length == 0 || LlamaCppModel.Length == 0)
            return null;
        string? edit = LlamaCppEditFile;
        string alias = LlamaCppAlias.Length > 0 ? LlamaCppAlias : "local";
        string? editAlias = edit is null ? null : LlamaCppEditAlias(edit);
        IReadOnlyList<(string Alias, string Path)> others = LlamaCppOthers;
        var presets = new Dictionary<string, LocalModelPreset>(StringComparer.Ordinal);
        void Preset(string name, string file)
        {
            if (!presets.ContainsKey(name) && LocalAiRoles.Lookup(this, LocalModelLibrary.Shared, LocalModelLibrary.FileId(file)) is { } model)
                presets[name] = LocalModelPresets.For(model, this);
        }
        Preset(alias, LlamaCppModel);
        if (edit is not null && editAlias is not null)
            Preset(editAlias, edit);
        foreach ((string other, string file) in others)
            Preset(other, file);
        return new LlamaCppServerConfig(LlamaCppServer, LlamaCppModel, alias, LlamaCppPort, LlamaCppContext, LlamaCppOffload, LlamaCppExtraArgs,
            edit, editAlias, others, presets);
    }

    /// <summary>
    /// The installed catalog models that can chat besides the code model (files in the models folder or beside the code
    /// model), each by its catalog id: what the router serves for agents and edits, one in memory at a time.
    /// </summary>
    public IReadOnlyList<(string Alias, string Path)> LlamaCppOthers
    {
        get
        {
            var found = new List<(string Alias, string Path)>();
            if (LlamaCppModel.Length == 0)
                return found;
            foreach (LlamaCppModel model in global::Hex.Core.LlamaCppModels.All)
            {
                if (!model.Chats || LlamaCppInstalled(model) is not { } file || SameFile(file, LlamaCppModel))
                    continue;
                if (found.Any(other => SameFile(other.Path, file)))
                    continue;
                found.Add((model.Id, file));
            }
            // Every other model of the library that can chat: files scanned in the folders, by their catalog id or file name
            foreach (LocalModel model in LocalModelLibrary.Shared.Models)
            {
                if (model.Path is not { } file || model.Source is not (LocalModelSource.File or LocalModelSource.LmStudio) || !model.Caps.HasFlag(LocalModelCaps.Chat)
                    || !System.IO.File.Exists(file) || SameFile(file, LlamaCppModel) || found.Any(other => SameFile(other.Path, file)))
                    continue;
                string name = model.CatalogId ?? System.IO.Path.GetFileNameWithoutExtension(file);
                if (found.Any(other => other.Alias == name))
                    continue;
                found.Add((name, file));
            }
            return found;
        }
    }

    // The rows of llama.cpp, one block of HexSettings.Rows
    private static HexSettingRow[] LlamaCppRows =>
    [
        OnDisk("llamacpp_folder", settings => settings.LlamaCppFolder, (settings, value) => settings.LlamaCppFolder = value.Trim()),
        OnDisk("llamacpp_models", settings => settings.LlamaCppModels, (settings, value) => settings.LlamaCppModels = value.Trim()),
        OnDisk("llamacpp_server", settings => settings.LlamaCppServer, (settings, value) => settings.LlamaCppServer = value.Trim()),
        OnDisk("llamacpp_model", settings => settings.LlamaCppModel, (settings, value) =>
        {
            settings.LlamaCppModel = value.Trim();
            // The older key still sets the predictions' model, local_ai_predict_model read after it wins when the file has both
            if (settings.LlamaCppModel.Length > 0 && settings.EditPredictionsProvider == LLAMACPP)
                settings.LocalAiPredictModel = LocalModelLibrary.FileId(settings.LlamaCppModel);
        }),
        Text("llamacpp_alias", settings => settings.LlamaCppAlias, (settings, value) => settings.LlamaCppAlias = value.Trim()),
        Whole("llamacpp_port", settings => settings.LlamaCppPort, (settings, value) => settings.LlamaCppPort = value, 1024.0f, 65535.0f),
        Whole("llamacpp_context", settings => settings.LlamaCppContext, (settings, value) => settings.LlamaCppContext = value, 2048.0f, 262144.0f),
        Choice("llamacpp_offload", settings => settings.LlamaCppOffload, (settings, value) => settings.LlamaCppOffload = value, LlamaCppServerArgs.Offloads),
        Text("llamacpp_extra_args", settings => settings.LlamaCppExtraArgs, (settings, value) => settings.LlamaCppExtraArgs = value.Trim()),
        Flag("llamacpp_autostart", settings => settings.LlamaCppAutostart, (settings, value) => settings.LlamaCppAutostart = value),
    ];
}
