using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core;

internal enum LocalModelSource { File, Ollama, LmStudio, Catalog /* recommended, not downloaded */, Linked /* served by a linked device, runs there */ }

internal enum LocalModelFit { Unknown, Fits, Tight, TooBig }

internal sealed record LocalModel(
    string Id,                   // stable: "file:<full path lowercased>" | "ollama:<name>" | "catalog:<catalog id>"
    string Name,
    LocalModelSource Source,
    string? Path,
    long Bytes,
    LocalModelCaps Caps,
    int? ContextMax,
    string? SizeLabel, string? Quant,
    string? CatalogId,
    LocalModelFit Fit,
    GgufInfo? Gguf,
    string? Architecture = null,
    int? Layers = null,          // a catalog entry's, from the real file, so its context estimate is the file's once downloaded
    int? KvHeads = null,
    string? Device = null)       // a linked model's: the name of the device it runs on
{
    /// <summary>Whether the model is on disk or served; false for a catalog entry not downloaded yet.</summary>
    public bool IsDownloaded => Source != LocalModelSource.Catalog;
}

/// <summary>
/// The models Hex can run: the .gguf files in the model folders, Ollama's models when it answers, and the catalog's
/// recommended ones not on disk yet. One shared instance (<see cref="Shared"/>) serves the scene, the pickers and the settings.
/// </summary>
internal sealed partial class LocalModelLibrary
{
    private const int MAX_DEPTH = 4;
    private const double GIB = 1024.0 * 1024.0 * 1024.0;

    /// <summary>The one library the scene, the pickers and the settings share. <see cref="Settings"/> is set by the scene; call <see cref="Refresh"/> to scan.</summary>
    public static LocalModelLibrary Shared { get; } = new();

    private readonly Func<IEnumerable<string>> folderSource;
    private readonly Func<string, GgufInfo?> reader;
    private readonly Func<long> vram;
    private readonly string cachePath;
    private readonly Func<string?> ollamaUrl;
    private readonly object gate = new();
    private IReadOnlyList<string> folders = [];
    private IReadOnlyList<LocalModel> models = [];
    private int version;

    /// <summary>The settings the folders, the assigned roles and Ollama's address come from; set by the scene.</summary>
    public HexSettings? Settings { get; set; }

    /// <summary>How many GGUF files were read from disk (not from the cache) by the scans so far, for tests.</summary>
    public int FilesRead { get; private set; }

    public LocalModelLibrary() : this(null, null, null, null, null) { }

    internal LocalModelLibrary(Func<IEnumerable<string>>? folders, string? cachePath, Func<string, GgufInfo?>? reader, Func<long>? vram, Func<string?>? ollamaUrl)
    {
        folderSource = folders ?? DefaultFolders;
        this.cachePath = cachePath ?? System.IO.Path.Combine(HexData.Root, "local-ai", "gguf-cache.json");
        this.reader = reader ?? GgufReader.Read;
        this.vram = vram ?? VramBytes;
        this.ollamaUrl = ollamaUrl ?? (() => Settings is { } s && (s.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA || s.EditPredictionsProvider == "ollama") ? s.EditPredictionsApiUrl : HexSettings.OLLAMA_URL);
    }

    /// <summary>The folders scanned for *.gguf, recursively to depth 4.</summary>
    public IReadOnlyList<string> Folders => folders;

    /// <summary>The last scan, sorted: assigned roles first, then fit, then name.</summary>
    public IReadOnlyList<LocalModel> Models
    {
        get
        {
            IReadOnlyList<LocalModel> own = models, borrowed = linked;
            if (borrowed.Count == 0)
                return own;
            (IReadOnlyList<LocalModel> Own, IReadOnlyList<LocalModel> Borrowed, IReadOnlyList<LocalModel> All) cached = combined;
            if (ReferenceEquals(cached.Own, own) && ReferenceEquals(cached.Borrowed, borrowed))
                return cached.All;
            IReadOnlyList<LocalModel> all = [.. own, .. borrowed];
            combined = (own, borrowed, all);
            return all;
        }
    }

    private IReadOnlyList<LocalModel> linked = [];
    private (IReadOnlyList<LocalModel> Own, IReadOnlyList<LocalModel> Borrowed, IReadOnlyList<LocalModel> All) combined = ([], [], []);

    /// <summary>
    /// The models linked devices serve (Serve AI, docs/hex-link-plan.md, 5.7), kept apart from the scanned ones and listed after
    /// them in <see cref="Models"/>: the scene sets them as links come and go and as what they serve changes. Changed is raised when they differ.
    /// </summary>
    public void SetLinked(IReadOnlyList<LocalModel> borrowed)
    {
        IReadOnlyList<LocalModel> was = linked;
        if (was.Count == borrowed.Count && was.Zip(borrowed).All(pair => pair.First == pair.Second))
            return;
        linked = borrowed;
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Whether Ollama is asked by the scans. Off for tests and for No AI.</summary>
    public bool OllamaEnabled { get; set; } = true;

    public LocalModel? Find(string id) =>
        string.IsNullOrEmpty(id) ? null : Models.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The id a file has: <c>file:</c> and its full path, lowercased.</summary>
    public static string FileId(string path) => "file:" + System.IO.Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>The path a <c>file:</c> id names, null for another kind of id.</summary>
    public static string? PathOfId(string id) => id.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? id[5..] : null;

    // ---- the scan ----

    /// <summary>
    /// Scans in the background: the files (cached by path, size and modification time in data/local-ai/gguf-cache.json),
    /// Ollama's models if it answers, and the catalog's models not on disk.
    /// </summary>
    public Task Refresh(CancellationToken cancel = default)
    {
        // Taken here, not in the task, so a caller asking "is it stale" straight after sees this scan's folders
        List<string> asked = CurrentFolders();
        Volatile.Write(ref askedKey, string.Join("\u001f", asked));
        return Task.Run(() => ScanAsync(asked, cancel), cancel);
    }

    private string? askedKey;

    private List<string> CurrentFolders() =>
        [.. folderSource().Where(folder => !string.IsNullOrWhiteSpace(folder)).Select(Normal).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether the folders now (the settings' models folder and extra folders) differ from the ones the last scan was asked for, so a scan is due. True before any scan.</summary>
    public bool FoldersStale() => Volatile.Read(ref askedKey) != string.Join("\u001f", CurrentFolders());

    private async Task ScanAsync(List<string> scanned, CancellationToken cancel)
    {
        int mine = Interlocked.Increment(ref version);
        long memory = vram();
        List<LocalModel> found = ScanFiles(scanned, memory, cancel);
        if (OllamaEnabled && ollamaUrl() is { Length: > 0 } url)
            found.AddRange(await ScanOllama(url, cancel).ConfigureAwait(false));
        found.AddRange(CatalogEntries(found, memory));
        IReadOnlyList<LocalModel> sorted = Sort(found);
        if (cancel.IsCancellationRequested || mine != Volatile.Read(ref version))
            return;
        lock (gate)
        {
            folders = scanned;
            models = sorted;
        }
        Settings?.ForgetLlamaCppDisk();
        Changed?.Invoke();
    }

    /// <summary>Scans synchronously, without Ollama: the files and the catalog. For tests and first start.</summary>
    internal void RefreshNow(CancellationToken cancel = default)
    {
        List<string> scanned = CurrentFolders();
        Volatile.Write(ref askedKey, string.Join("\u001f", scanned));
        long memory = vram();
        List<LocalModel> found = ScanFiles(scanned, memory, cancel);
        found.AddRange(CatalogEntries(found, memory));
        lock (gate)
        {
            folders = scanned;
            models = Sort(found);
        }
        Settings?.ForgetLlamaCppDisk();
        Changed?.Invoke();
    }

    private static string Normal(string folder)
    {
        try { return System.IO.Path.GetFullPath(folder.Trim()); }
        catch (ArgumentException) { return folder.Trim(); }
    }

    private IReadOnlyList<LocalModel> Sort(List<LocalModel> found)
    {
        HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase);
        if (Settings is { } settings)
        {
            if (settings.LocalAiPredictModel.Length > 0) assigned.Add(settings.LocalAiPredictModel);
            if (settings.LocalAiChatModel.Length > 0) assigned.Add(settings.LocalAiChatModel);
        }
        static int Rank(LocalModelFit fit) => fit switch { LocalModelFit.Fits => 0, LocalModelFit.Tight => 1, LocalModelFit.Unknown => 2, _ => 3 };
        return [.. found
            .OrderBy(model => assigned.Contains(model.Id) ? 0 : 1)
            .ThenBy(model => model.Source == LocalModelSource.Catalog ? 1 : 0)
            .ThenBy(model => Rank(model.Fit))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The folders scanned by default and from the settings: the models folder, the one beside the code model, Hex's own, LM Studio's, and the user's.</summary>
    private IEnumerable<string> DefaultFolders()
    {
        var all = new List<string>();
        if (Settings is { } settings)
        {
            all.Add(settings.LlamaCppModelsInForce);
            if (settings.LlamaCppModel.Length > 0 && System.IO.Path.GetDirectoryName(settings.LlamaCppModel) is { Length: > 0 } beside)
                all.Add(beside);
            all.AddRange(SplitFolders(settings.LocalAiModelFolders));
        }
        all.Add(System.IO.Path.Combine(HexData.Root, "llama.cpp", "models"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            all.Add(System.IO.Path.Combine(home, ".lmstudio", "models"));
            all.Add(System.IO.Path.Combine(home, ".cache", "lm-studio", "models"));
        }
        return all;
    }

    /// <summary>The folders a local_ai_model_folders value lists: split by semicolons or new lines.</summary>
    internal static IEnumerable<string> SplitFolders(string value) =>
        value.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsLmStudio(string path) =>
        path.Contains(System.IO.Path.DirectorySeparatorChar + ".lmstudio" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Contains(System.IO.Path.DirectorySeparatorChar + "lm-studio" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitPart();

    /// <summary>The .gguf files under the folders, to depth 4, without projectors (mmproj-*) and the later parts of split files.</summary>
    internal static IEnumerable<string> FindFiles(IEnumerable<string> folders, CancellationToken cancel = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            var pending = new Stack<(string Folder, int Depth)>();
            pending.Push((folder, 0));
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                (string current, int depth) = pending.Pop();
                string[] files = [];
                try { files = Directory.GetFiles(current, "*.gguf"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                foreach (string file in files)
                {
                    string name = System.IO.Path.GetFileName(file);
                    if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (SplitPart().Match(name) is { Success: true } part && int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture) != 1)
                        continue;
                    if (seen.Add(System.IO.Path.GetFullPath(file)))
                        yield return file;
                }
                if (depth >= MAX_DEPTH)
                    continue;
                try
                {
                    foreach (string sub in Directory.GetDirectories(current))
                        pending.Push((sub, depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private List<LocalModel> ScanFiles(List<string> scanned, long memory, CancellationToken cancel)
    {
        var cache = GgufCache.Load(cachePath);
        var found = new List<LocalModel>();
        bool changed = false;
        foreach (string file in FindFiles(scanned, cancel))
        {
            FileInfo info;
            try { info = new FileInfo(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            if (!info.Exists)
                continue;
            GgufInfo? gguf = cache.Get(file, info.Length, info.LastWriteTimeUtc.Ticks);
            if (gguf is null)
            {
                gguf = reader(file);
                FilesRead++;
                if (gguf is not null)
                {
                    cache.Put(file, info.Length, info.LastWriteTimeUtc.Ticks, gguf);
                    changed = true;
                }
            }
            found.Add(FromFile(file, info.Length, gguf, memory));
        }
        if (changed || cache.Prune(found.Select(model => model.Path!)))
            cache.Save(cachePath);
        return found;
    }

    internal static LocalModel FromFile(string path, long bytes, GgufInfo? gguf, long memory)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        LlamaCppModel? catalog = LlamaCppModels.OfFile(path);
        // One name for a model everywhere: the catalog's ("Qwen3.5 9B", also "Zeta 2.1" over a generic "ggml-model"), else the file's own
        string name = catalog?.Title ?? (!string.IsNullOrWhiteSpace(gguf?.Name) ? gguf!.Name! : fileName);
        LocalModelCaps caps = gguf is null ? CatalogCaps(catalog) : LocalModelCapsOf.From(gguf);
        if (caps == LocalModelCaps.None && catalog is not null)
            caps = CatalogCaps(catalog);
        int? contextMax = gguf?.ContextLength;
        int context = LocalModelPresets.DefaultContext(name + " " + fileName, contextMax, gguf?.Architecture, gguf?.IsZeta ?? false);
        LocalModelFit fit = FitOf(bytes, gguf, context, memory);
        return new LocalModel(FileId(path), name, IsLmStudio(path) ? LocalModelSource.LmStudio : LocalModelSource.File, path, bytes, caps, contextMax,
            LocalModelSize.Label(gguf?.SizeLabel, name, fileName), gguf?.Quant, catalog?.Id, fit, gguf, gguf?.Architecture);
    }

    internal static LocalModelCaps CatalogCaps(LlamaCppModel? model)
    {
        if (model is null)
            return LocalModelCaps.None;
        if (model.Caps != LocalModelCaps.None)
            return model.Caps;
        if (!model.Chats)
            return LocalModelCaps.Predict | LocalModelCaps.EditModel;
        LocalModelCaps caps = LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Predict;
        if (model.PromptFormat == "qwen")
            caps |= LocalModelCaps.Fim;
        return caps;
    }

    private static IEnumerable<LocalModel> CatalogEntries(List<LocalModel> found, long memory)
    {
        foreach (LlamaCppModel catalog in LlamaCppModels.All)
        {
            if (found.Any(model => model.CatalogId == catalog.Id))
                continue;
            int? contextMax = catalog.Context > 0 ? catalog.Context : null;
            int context = LocalModelPresets.DefaultContext(catalog.Title + " " + catalog.Id, contextMax);
            string? sizeLabel = LocalModelSize.Label(catalog.SizeLabel.Length > 0 ? catalog.SizeLabel : null, catalog.Title, catalog.File)
                ?? (catalog.MixtureOfExperts ? string.Create(CultureInfo.InvariantCulture, $"{catalog.BillionParameters:0.#}B-A{catalog.BillionActive:0.#}B")
                    : string.Create(CultureInfo.InvariantCulture, $"{catalog.BillionParameters:0.#}B"));
            yield return new LocalModel("catalog:" + catalog.Id, catalog.Title, LocalModelSource.Catalog, null, catalog.Size, CatalogCaps(catalog), contextMax,
                sizeLabel, catalog.Quant.Length > 0 ? catalog.Quant : null, catalog.Id, FitOf(catalog.Size, catalog.Layers, catalog.KvHeads, context, memory), null,
                catalog.Architecture.Length > 0 ? catalog.Architecture : null, catalog.Layers > 0 ? catalog.Layers : null, catalog.KvHeads > 0 ? catalog.KvHeads : null);
        }
    }

    // ---- Ollama ----

    private static async Task<List<LocalModel>> ScanOllama(string url, CancellationToken cancel)
    {
        var found = new List<LocalModel>();
        string root = url.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3];
        int at = root.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        if (at > 0)
            root = root[..at];
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string tags = await http.GetStringAsync(root + "/api/tags", cancel).ConfigureAwait(false);
            if (JsonNode.Parse(tags)?["models"] is not JsonArray list)
                return found;
            foreach (JsonNode? entry in list)
            {
                string? name = entry?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;
                long bytes = entry?["size"]?.GetValue<long>() ?? 0;
                var capabilities = new List<string>();
                int? context = null;
                string? size = entry?["details"]?["parameter_size"]?.GetValue<string>();
                string? quant = entry?["details"]?["quantization_level"]?.GetValue<string>();
                try
                {
                    using var content = new StringContent(new JsonObject { ["model"] = name }.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await http.PostAsync(root + "/api/show", content, cancel).ConfigureAwait(false);
                    JsonNode? show = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
                    if (show?["capabilities"] is JsonArray caps)
                        capabilities.AddRange(caps.Select(cap => cap?.GetValue<string>() ?? string.Empty).Where(cap => cap.Length > 0));
                    if (show?["model_info"] is JsonObject info)
                        foreach (var (key, value) in info)
                            if (key.EndsWith(".context_length", StringComparison.Ordinal) && value is JsonValue number && number.TryGetValue(out long length))
                                context = (int)Math.Min(length, int.MaxValue);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { }
                found.Add(new LocalModel("ollama:" + name, name, LocalModelSource.Ollama, null, bytes, LocalModelCapsOf.FromOllama(capabilities, name), context,
                    size, quant, null, LocalModelFit.Unknown, null));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { }
        return found;
    }

    // ---- the card ----

    private static long vramBytes = -1;

    /// <summary>A test's answer for <see cref="VramBytes"/>, -1 to ask the card.</summary>
    internal static long VramOverride { get; set; } = -1;

    /// <summary>The graphics card's memory in bytes by nvidia-smi, 0 for unknown. Asked once.</summary>
    public static long VramBytes()
    {
        if (VramOverride >= 0)
            return VramOverride;
        if (Volatile.Read(ref vramBytes) >= 0)
            return vramBytes;
        long found = LocalAiGpu.Usage() is { } card ? card.TotalMiB * 1024 * 1024 : 0;
        Volatile.Write(ref vramBytes, found);
        return found;
    }

    /// <summary>
    /// Whether a model fits the card at a context: its weights, a KV cache estimate (2 x layers x context x KV heads x 128 x
    /// 2 bytes; 64 KiB a token when the layers aren't known) and 0.8 GB of overhead. Fits up to 85% of the card, Tight up to
    /// all of it, TooBig beyond; Unknown with no card.
    /// </summary>
    public static LocalModelFit FitOf(long modelBytes, GgufInfo? gguf, int context, long vram) =>
        FitOf(modelBytes, gguf?.Layers ?? 0, gguf?.HeadCountKv ?? 0, context, vram);

    /// <summary>The same with the layers and KV heads given, as a catalog entry knows them from its real file.</summary>
    public static LocalModelFit FitOf(long modelBytes, int layers, int kvHeads, int context, long vram)
    {
        if (vram <= 0 || modelBytes <= 0)
            return LocalModelFit.Unknown;
        double need = modelBytes + KvBytes(layers, kvHeads, context) + 0.8 * GIB;
        if (need <= vram * 0.85)
            return LocalModelFit.Fits;
        return need <= vram ? LocalModelFit.Tight : LocalModelFit.TooBig;
    }

    /// <summary>The bytes the context's KV cache takes (fp16).</summary>
    public static double KvBytes(GgufInfo? gguf, int context) => KvBytes(gguf?.Layers ?? 0, gguf?.HeadCountKv ?? 0, context);

    /// <summary>The bytes the context's KV cache takes (fp16), 64 KiB a token when the layers or heads aren't known.</summary>
    public static double KvBytes(int layers, int kvHeads, int context)
    {
        int tokens = Math.Max(context, 0);
        if (layers > 0 && kvHeads > 0)
            return 2.0 * layers * tokens * kvHeads * 128 * 2;
        return tokens * 64.0 * 1024;
    }

    /// <summary>A size in bytes as the GB the lists show, decimal (1 GB = 1,000,000,000 bytes) wherever one is shown: "5.6".</summary>
    public static string Gb(double bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The figures behind a model's fit, "7.5 GB weights + 1.1 GB context of 12 GB (fits)": its weights, the context's KV
    /// cache and the card, then the verdict of <see cref="FitOf"/>. With no card (<paramref name="vram"/> 0) it says so.
    /// </summary>
    public static string FitDetail(LocalModel model, int context, long vram)
    {
        if (model.Source == LocalModelSource.Linked)
            return $"Runs on {model.Device ?? "a linked device"}, not on this card";
        int layers = model.Gguf?.Layers ?? model.Layers ?? 0, kvHeads = model.Gguf?.HeadCountKv ?? model.KvHeads ?? 0;
        string weights = Gb(model.Bytes);
        string kv = Gb(KvBytes(layers, kvHeads, context));
        if (vram <= 0 || model.Bytes <= 0)
            return $"{weights} GB weights + {kv} GB context (card size unknown)";

        string verdict = FitOf(model.Bytes, layers, kvHeads, context, vram) switch
        {
            LocalModelFit.Fits => "fits",
            LocalModelFit.Tight => "tight",
            LocalModelFit.TooBig => "too big",
            _ => "unknown",
        };
        return $"{weights} GB weights + {kv} GB context of {(vram / 1e9).ToString("0.#", CultureInfo.InvariantCulture)} GB ({verdict})";
    }
}

/// <summary>The GGUF metadata read, kept in data/local-ai/gguf-cache.json by path, size and modification time.</summary>
internal sealed class GgufCache
{
    private readonly Dictionary<string, (long Size, long Mtime, GgufInfo Info)> entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => entries.Count;

    public GgufInfo? Get(string path, long size, long mtime) =>
        entries.TryGetValue(path, out var entry) && entry.Size == size && entry.Mtime == mtime ? entry.Info : null;

    public void Put(string path, long size, long mtime, GgufInfo info) => entries[path] = (size, mtime, info);

    /// <summary>Drops what isn't among the paths any more; true when something went.</summary>
    public bool Prune(IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        string[] gone = [.. entries.Keys.Where(path => !kept.Contains(path))];
        foreach (string path in gone)
            entries.Remove(path);
        return gone.Length > 0;
    }

    public static GgufCache Load(string path)
    {
        var cache = new GgufCache();
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return cache;
            foreach (var (key, node) in root)
            {
                if (node is not JsonObject e)
                    continue;
                cache.entries[key] = (e["size"]?.GetValue<long>() ?? 0, e["mtime"]?.GetValue<long>() ?? 0, ReadInfo(key, e));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            cache.entries.Clear();
        }
        return cache;
    }

    public void Save(string path)
    {
        try
        {
            var root = new JsonObject();
            foreach (var (key, entry) in entries)
            {
                GgufInfo i = entry.Info;
                root[key] = new JsonObject
                {
                    ["size"] = entry.Size, ["mtime"] = entry.Mtime, ["bytes"] = i.Bytes, ["arch"] = i.Architecture, ["name"] = i.Name,
                    ["size_label"] = i.SizeLabel, ["params"] = i.Parameters, ["file_type"] = i.FileType, ["quant"] = i.Quant,
                    ["context"] = i.ContextLength, ["layers"] = i.Layers, ["embedding"] = i.EmbeddingLength, ["head_kv"] = i.HeadCountKv,
                    ["experts"] = i.ExpertCount, ["template"] = i.ChatTemplate, ["tools"] = i.TemplateTools, ["thinking"] = i.TemplateThinking, ["fim"] = i.HasFimTokens, ["zeta"] = i.IsZeta,
                };
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static GgufInfo ReadInfo(string path, JsonObject e)
    {
        static string? S(JsonNode? n) => n?.GetValue<string>();
        static long? L(JsonNode? n) => n?.GetValue<long>();
        static int? I(JsonNode? n) => n is null ? null : n.GetValue<int>();
        // A cache written before only the head of the template was kept holds the whole template: it's digested on the way in
        (string? head, bool tools, bool thinking) = GgufReader.Digest(S(e["template"]));
        if (e["tools"] is { } kept)
            tools = kept.GetValue<bool>();
        if (e["thinking"] is { } thought)
            thinking = thought.GetValue<bool>();
        return new GgufInfo(path, L(e["bytes"]) ?? 0, S(e["arch"]), S(e["name"]), S(e["size_label"]), L(e["params"]), I(e["file_type"]), S(e["quant"]),
            I(e["context"]), I(e["layers"]), I(e["embedding"]), I(e["head_kv"]), I(e["experts"]), head,
            e["fim"]?.GetValue<bool>() ?? false, e["zeta"]?.GetValue<bool>() ?? false, tools, thinking);
    }
}
