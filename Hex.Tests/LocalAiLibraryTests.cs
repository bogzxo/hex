using System.Text;
using Hex.Core;

namespace Hex.Tests;

/// <summary>The local model library (scan, cache, fit), the presets, the roles with their migration and the router's preset text.</summary>
public sealed class LocalAiLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hex-localai-").FullName;
    private const long GIB = 1L << 30;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // A tiny GGUF: the header and string KVs
    private static byte[] Gguf(string architecture, string name, string? template = null)
    {
        using var kv = new MemoryStream();
        using var w = new BinaryWriter(kv, Encoding.UTF8, true);
        int count = 0;
        void Str(string s) { byte[] b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void Text(string key, string value) { Str(key); w.Write(8u); Str(value); count++; }
        Text("general.architecture", architecture);
        Text("general.name", name);
        if (template is not null) Text("tokenizer.chat_template", template);
        using var ms = new MemoryStream();
        using var o = new BinaryWriter(ms);
        o.Write(Encoding.ASCII.GetBytes("GGUF"));
        o.Write(3u); o.Write(0UL); o.Write((ulong)count);
        w.Flush();
        o.Write(kv.ToArray());
        o.Flush();
        return ms.ToArray();
    }

    private string Put(string relative, byte[]? bytes = null)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Gguf("llama", Path.GetFileNameWithoutExtension(relative), "{{ messages }}"));
        return path;
    }

    private LocalModelLibrary Library(string? cache = null, Func<string, GgufInfo?>? reader = null, long vram = 12 * GIB) =>
        new(() => [_dir], cache ?? Path.Combine(_dir, "cache", "gguf-cache.json"), reader, () => vram, () => null) { OllamaEnabled = false };

    // ---- scan ----

    [Fact]
    public void A_changed_folder_list_makes_the_library_stale_until_it_scans_again()
    {
        string other = Path.Combine(_dir, "other");
        string[] folders = [_dir];
        var library = new LocalModelLibrary(() => folders, Path.Combine(_dir, "cache", "gguf-cache.json"), null, () => 12 * GIB, () => null) { OllamaEnabled = false };
        Assert.True(library.FoldersStale());          // never scanned
        library.RefreshNow();
        Assert.False(library.FoldersStale());
        folders = [_dir, other];
        Assert.True(library.FoldersStale());
        library.RefreshNow();
        Assert.False(library.FoldersStale());
        Assert.Contains(Path.GetFullPath(other), library.Folders);
    }

    [Fact]
    public void The_scan_finds_ggufs_recursively_and_skips_projectors_and_later_split_parts()
    {
        Put("a.gguf");
        Put("sub/b.gguf");
        Put("sub/deep/c/d/e/f.gguf");        // depth 5: too deep
        Put("sub/mmproj-vision.gguf");
        Put("big-00001-of-00003.gguf");
        Put("big-00002-of-00003.gguf");
        Put("big-00003-of-00003.gguf");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "no");

        LocalModelLibrary library = Library();
        library.RefreshNow();

        string[] files = [.. library.Models.Where(model => model.Source == LocalModelSource.File).Select(model => Path.GetFileName(model.Path!)).Order()];
        Assert.Equal(["a.gguf", "b.gguf", "big-00001-of-00003.gguf"], files);
        Assert.Contains(library.Models, model => model.Source == LocalModelSource.Catalog);   // the catalog's, not on disk
    }

    [Fact]
    public void A_scanned_model_has_a_stable_id_and_is_found_by_it()
    {
        string path = Put("m.gguf");
        LocalModelLibrary library = Library();
        library.RefreshNow();
        string id = LocalModelLibrary.FileId(path);
        Assert.StartsWith("file:", id);
        Assert.Equal(id, id.ToLowerInvariant());
        Assert.NotNull(library.Find(id));
        Assert.NotNull(library.Find(id.ToUpperInvariant().Replace("FILE:", "file:")));
        Assert.Null(library.Find("file:nothing"));
    }

    [Fact]
    public void The_catalog_lists_a_model_only_while_its_file_is_not_on_disk()
    {
        LocalModelLibrary library = Library();
        library.RefreshNow();
        Assert.Contains(library.Models, model => model.Id == "catalog:zeta-2.1");
        Put("zeta-2.1.i1-Q4_K_M.gguf");
        library.RefreshNow();
        Assert.DoesNotContain(library.Models, model => model.Id == "catalog:zeta-2.1");
        LocalModel zeta = Assert.Single(library.Models, model => model.CatalogId == "zeta-2.1");
        Assert.Equal(LocalModelSource.File, zeta.Source);
        Assert.False(zeta.Caps.HasFlag(LocalModelCaps.Chat) && !zeta.Caps.HasFlag(LocalModelCaps.Predict));
    }

    [Fact]
    public void Catalog_entries_carry_their_own_caps_and_facts_and_say_they_are_not_downloaded()
    {
        LocalModelLibrary library = Library();
        library.RefreshNow();

        LocalModel qwen = Assert.Single(library.Models, model => model.Id == "catalog:qwen2.5-coder-1.5b");
        Assert.False(qwen.IsDownloaded);
        Assert.Equal(LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Predict | LocalModelCaps.Fim, qwen.Caps);
        Assert.Equal(("qwen2", 32768, "Q8_0"), (qwen.Architecture, qwen.ContextMax, qwen.Quant));

        LocalModel zeta = Assert.Single(library.Models, model => model.Id == "catalog:zeta-2.1");
        Assert.Equal(LocalModelCaps.Predict | LocalModelCaps.EditModel, zeta.Caps);

        LocalModel mellum = Assert.Single(library.Models, model => model.Id == "catalog:mellum2.1");
        Assert.True(mellum.Caps.HasFlag(LocalModelCaps.Fim) && mellum.Caps.HasFlag(LocalModelCaps.Predict));
        Assert.Equal("12B-A2.5B", mellum.SizeLabel);
        Assert.All(library.Models.Where(model => model.Source == LocalModelSource.Catalog),
            model => Assert.True(model.Architecture is not null && model.ContextMax is > 0 && model.Quant is not null));

        Put("sub/own.gguf");
        library.RefreshNow();
        Assert.True(Assert.Single(library.Models, model => model.Name == "own").IsDownloaded);
    }

    // ---- cache ----

    [Fact]
    public void The_cache_keeps_what_was_read_until_the_size_or_the_time_changes()
    {
        string path = Put("cached.gguf");
        string cache = Path.Combine(_dir, "cache", "gguf-cache.json");
        int reads = 0;
        GgufInfo? Counting(string file) { reads++; return GgufReader.Read(file); }

        Library(cache, Counting).RefreshNow();
        Assert.Equal(1, reads);
        Assert.True(File.Exists(cache));

        // A new library (a new run) reads it from the cache
        Library(cache, Counting).RefreshNow();
        Assert.Equal(1, reads);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));
        Library(cache, Counting).RefreshNow();
        Assert.Equal(2, reads);

        File.AppendAllText(path, "more");
        Library(cache, Counting).RefreshNow();
        Assert.Equal(3, reads);
    }

    [Fact]
    public void The_cache_drops_files_that_are_gone_and_survives_a_broken_file()
    {
        string path = Put("gone.gguf");
        string cache = Path.Combine(_dir, "cache", "gguf-cache.json");
        Library(cache).RefreshNow();
        Assert.Contains("gone.gguf", File.ReadAllText(cache), StringComparison.OrdinalIgnoreCase);
        File.Delete(path);
        Library(cache).RefreshNow();
        Assert.DoesNotContain("gone.gguf", File.ReadAllText(cache), StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(cache, "{ not json");
        Put("again.gguf");
        LocalModelLibrary library = Library(cache);
        library.RefreshNow();
        Assert.Contains(library.Models, model => model.Name.Contains("again", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cache_round_trips_the_metadata()
    {
        var info = new GgufInfo("p", 10, "llama", "N", "9B", 9, 15, "Q4_K_M", 4096, 32, 4096, 8, 4, "tpl", true, false);
        var cache = new GgufCache();
        cache.Put("p", 10, 20, info);
        string file = Path.Combine(_dir, "c.json");
        cache.Save(file);
        GgufInfo? back = GgufCache.Load(file).Get("p", 10, 20);
        Assert.Equal(info, back);
        Assert.Null(GgufCache.Load(file).Get("p", 11, 20));
    }

    // ---- fit ----

    [Fact]
    public void Fit_counts_the_weights_the_context_cache_and_overhead_against_the_card()
    {
        var gguf = new GgufInfo("p", 0, "llama", "n", null, null, null, null, 32768, 32, 4096, 8, null, null, false, false);
        // 32 layers x 8192 tokens x 8 kv heads x 128 x 2 (K and V) x 2 bytes = 1 GiB
        Assert.Equal(1.0 * GIB, LocalModelLibrary.KvBytes(gguf, 8192));
        Assert.Equal(LocalModelFit.Fits, LocalModelLibrary.FitOf(6 * GIB, gguf, 8192, 12 * GIB));      // 6 + 1 + 0.8
        Assert.Equal(LocalModelFit.Fits, LocalModelLibrary.FitOf(6 * GIB, gguf, 8192, 12 * GIB));
        Assert.Equal(LocalModelFit.Tight, LocalModelLibrary.FitOf(9 * GIB, gguf, 8192, 12 * GIB));     // 10.8 of 12
        Assert.Equal(LocalModelFit.TooBig, LocalModelLibrary.FitOf(11 * GIB, gguf, 8192, 12 * GIB));
        Assert.Equal(LocalModelFit.Unknown, LocalModelLibrary.FitOf(6 * GIB, gguf, 8192, 0));
        // A longer context costs more and can push a model over
        Assert.Equal(LocalModelFit.TooBig, LocalModelLibrary.FitOf(9 * GIB, gguf, 32768, 12 * GIB));
        // Unknown layers: 64 KiB a token
        Assert.Equal(8192 * 64.0 * 1024, LocalModelLibrary.KvBytes(null, 8192));
    }

    // ---- presets ----

    private static LocalModel Model(string name, int? contextMax = null, LocalModelFit fit = LocalModelFit.Fits, int experts = 0, string? catalog = null, bool zeta = false, string? id = null) =>
        new(id ?? "file:" + name.ToLowerInvariant(), name, LocalModelSource.File, @"C:\m\" + name + ".gguf", GIB, LocalModelCaps.Chat, contextMax, null, null, catalog, fit,
            new GgufInfo(@"C:\m\" + name + ".gguf", GIB, "llama", name, null, null, null, null, contextMax, null, null, null, experts > 0 ? experts : null, null, false, zeta));

    [Fact]
    public void Presets_have_a_default_by_family()
    {
        LocalModelPreset zeta = LocalModelPresets.Default(Model("zeta-2.1.i1-Q4_K_M", zeta: true));
        Assert.Equal(8192, zeta.Context);
        Assert.Equal(0.0, zeta.Temperature);

        LocalModelPreset qwen = LocalModelPresets.Default(Model("Qwen3.5-9B-UD-Q4_K_XL", 262144));
        Assert.Equal((32768, 0.7, 0.8, 20, 0.0), (qwen.Context, qwen.Temperature, qwen.TopP, qwen.TopK, qwen.MinP));

        LocalModelPreset mellum = LocalModelPresets.Default(Model("Mellum2.1-12B-A2.5B-Thinking", 131072));
        Assert.Equal((32768, 0.6, 0.95), (mellum.Context, mellum.Temperature, mellum.TopP));

        LocalModelPreset coder = LocalModelPresets.Default(Model("qwen2.5-coder-1.5b-instruct-q8_0", 32768));
        Assert.Equal((16384, 0.2), (coder.Context, coder.Temperature));

        LocalModelPreset generic = LocalModelPresets.Default(Model("some-model", 4096));
        Assert.Equal((4096, 0.7, 0.9), (generic.Context, generic.Temperature, generic.TopP));
        Assert.Equal(16384, LocalModelPresets.Default(Model("some-model", 131072)).Context);
    }

    [Fact]
    public void A_mixture_of_experts_that_does_not_fit_offloads_its_experts()
    {
        Assert.Equal("experts", LocalModelPresets.Default(Model("moe-model", experts: 64, fit: LocalModelFit.TooBig)).Offload);
        Assert.Equal("auto", LocalModelPresets.Default(Model("moe-model", experts: 64, fit: LocalModelFit.Fits)).Offload);
        Assert.Equal("auto", LocalModelPresets.Default(Model("dense-model", fit: LocalModelFit.TooBig)).Offload);
    }

    [Fact]
    public void Overrides_are_kept_in_the_settings_and_missing_fields_come_from_the_default()
    {
        var settings = new HexSettings(null);
        LocalModel model = Model("Qwen3.5-9B-UD-Q4_K_XL", 262144);
        Assert.Equal(LocalModelPresets.Default(model), LocalModelPresets.For(model, settings));

        LocalModelPreset changed = LocalModelPresets.Default(model) with { Temperature = 0.3, Context = 16384 };
        LocalModelPresets.Save(settings, model.Id, changed, model);
        Assert.Contains("temperature", settings.LocalAiPresets);
        Assert.DoesNotContain("top_p", settings.LocalAiPresets);       // only what differs
        LocalModelPreset back = LocalModelPresets.For(model, settings);
        Assert.Equal(0.3, back.Temperature);
        Assert.Equal(16384, back.Context);
        Assert.Equal(0.8, back.TopP);                                    // from the default

        LocalModelPresets.Reset(settings, model.Id);
        Assert.Equal(string.Empty, settings.LocalAiPresets);
        Assert.Equal(LocalModelPresets.Default(model), LocalModelPresets.For(model, settings));
    }

    [Fact]
    public void A_preset_that_is_the_default_is_no_override_and_a_broken_json_is_ignored()
    {
        var settings = new HexSettings(null);
        LocalModel model = Model("some-model", 4096);
        LocalModelPresets.Save(settings, model.Id, LocalModelPresets.Default(model), model);
        Assert.Equal(string.Empty, settings.LocalAiPresets);
        settings.LocalAiPresets = "{ nonsense";
        Assert.Equal(LocalModelPresets.Default(model), LocalModelPresets.For(model, settings));
        settings.LocalAiPresets = "{\"" + model.Id + "\": {\"temperature\": 99, \"offload\": \"bogus\", \"top_k\": 5}}";
        LocalModelPreset clamped = LocalModelPresets.For(model, settings);
        Assert.Equal(2.0, clamped.Temperature);
        Assert.Equal("auto", clamped.Offload);
        Assert.Equal(5, clamped.TopK);
    }

    [Fact]
    public void The_request_side_tuning_comes_from_the_preset()
    {
        LocalModelPreset preset = new(8192, 0.6, 0.95, 20, 0, 1.0, "off", "auto", "");
        PredictionTuning tuning = LocalModelPresets.Tuning(preset, new PredictionTuning(Thinking: "on"));
        Assert.Equal((0.6f, 0.95f, 20, "off"), (tuning.Temperature, tuning.TopP, tuning.TopK, tuning.Thinking));
        Assert.Equal("high", LocalModelPresets.Tuning(preset with { Thinking = "model" }, new PredictionTuning(Thinking: "high")).Thinking);
    }

    // ---- roles ----

    private static HexSettings Settings(Action<HexSettings>? configure = null)
    {
        var settings = new HexSettings(null) { EditPredictionsProvider = HexSettings.LLAMACPP };
        configure?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void Migration_takes_the_predict_model_from_llamacpp_model_and_the_chat_model_from_ai_edit_model()
    {
        string models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(models);
        string zeta = Path.Combine(models, "zeta-2.1.i1-Q4_K_M.gguf");
        string mellum = Path.Combine(models, "Mellum2.1-12B-A2.5B-Thinking-Q4_K_M.gguf");
        File.WriteAllText(zeta, "x"); File.WriteAllText(mellum, "x");
        HexSettings settings = Settings(s => { s.LlamaCppModels = models; s.LlamaCppModel = zeta; s.AiEditModel = "Mellum2.1"; });

        LocalAiRoles.Migrate(settings);

        Assert.Equal(LocalModelLibrary.FileId(zeta), settings.LocalAiPredictModel);
        Assert.Equal(LocalModelLibrary.FileId(mellum), settings.LocalAiChatModel);
        // The older keys are still there, and say the same thing
        Assert.Equal(zeta, settings.LlamaCppModel);
        Assert.Equal("mellum2.1", settings.AiEditModel);

        // A role cleared through Set stays cleared: its older key goes too
        LocalAiRoles.Set(settings, LocalAiRole.Chat, string.Empty);
        LocalAiRoles.Migrate(settings);
        Assert.Equal(string.Empty, settings.LocalAiChatModel);
    }

    [Fact]
    public void Migration_names_a_catalog_model_that_is_not_downloaded_by_its_catalog_id()
    {
        HexSettings settings = Settings(s => { s.AiEditModel = "qwen3.5-9b"; s.LlamaCppModels = Path.Combine(_dir, "empty"); });
        LocalAiRoles.Migrate(settings);
        Assert.Equal("catalog:qwen3.5-9b", settings.LocalAiChatModel);
        Assert.Equal(string.Empty, settings.LocalAiPredictModel);
    }

    [Fact]
    public void The_migration_survives_a_settings_file_round_trip()
    {
        string file = Path.Combine(_dir, "settings.hor");
        string zetaFile = Path.Combine(_dir, "zeta-2.1.i1-Q4_K_M.gguf");
        File.WriteAllText(zetaFile, "x");
        var first = new HexSettings(null) { EditPredictionsProvider = HexSettings.LLAMACPP, LlamaCppModel = zetaFile, LocalAiPredictModel = LocalModelLibrary.FileId(zetaFile), LocalAiEngine = "ollama" };
        Assert.Equal("ollama", first.LocalAiEngine);
        Assert.Contains(first.EditorValues(), pair => pair.Key == "local_ai_engine");
        Assert.Contains(first.EditorValues(), pair => pair.Key == "local_ai_predict_model");
        Assert.Contains(first.EditorValues(), pair => pair.Key == "local_ai_presets");
        _ = file;
    }

    [Fact]
    public void Resolve_with_one_model_that_chats_gives_the_same_model_for_both_roles()
    {
        string path = Put("Qwen3.5-9B-UD-Q4_K_XL.gguf");
        LocalModelLibrary library = Library();
        library.RefreshNow();
        HexSettings settings = Settings(s => s.LocalAiPredictModel = LocalModelLibrary.FileId(path));
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        Assert.NotNull(roles.Predict);
        Assert.Same(roles.Predict, roles.Chat);
        Assert.False(roles.TwoModels);
    }

    [Fact]
    public void Resolve_with_one_model_that_cannot_chat_has_no_chat_model()
    {
        string path = Put("zeta-2.1.i1-Q4_K_M.gguf");
        LocalModelLibrary library = Library();
        library.RefreshNow();
        HexSettings settings = Settings(s => s.LocalAiPredictModel = LocalModelLibrary.FileId(path));
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        Assert.NotNull(roles.Predict);
        Assert.Null(roles.Chat);
        Assert.False(roles.TwoModels);
    }

    [Fact]
    public void Resolve_with_two_models_says_the_router_swaps()
    {
        string zeta = Put("zeta-2.1.i1-Q4_K_M.gguf");
        string mellum = Put("Mellum2.1-12B-A2.5B-Thinking-Q4_K_M.gguf");
        LocalModelLibrary library = Library();
        library.RefreshNow();
        HexSettings settings = Settings();
        LocalAiRoles.Set(settings, LocalAiRole.Predict, LocalModelLibrary.FileId(zeta));
        LocalAiRoles.Set(settings, LocalAiRole.Chat, LocalModelLibrary.FileId(mellum));
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        Assert.Equal(LocalModelLibrary.FileId(zeta), roles.Predict!.Id);
        Assert.Equal(LocalModelLibrary.FileId(mellum), roles.Chat!.Id);
        Assert.True(roles.TwoModels);
    }

    [Fact]
    public void Resolve_knows_a_model_the_library_has_not_scanned_yet_from_its_id()
    {
        string zeta = Put("zeta-2.1.i1-Q4_K_M.gguf");
        HexSettings settings = Settings(s => s.LocalAiPredictModel = LocalModelLibrary.FileId(zeta));
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, Library());     // not scanned
        Assert.Equal("Zeta 2.1", roles.Predict!.Name);
        Assert.Null(roles.Chat);
    }

    [Fact]
    public void Setting_a_role_keeps_the_older_keys_in_step_and_an_empty_id_clears()
    {
        string a = Put("a-model.gguf");
        string b = Put("b-model.gguf");
        HexSettings settings = Settings();
        LocalAiRoles.Set(settings, LocalAiRole.Predict, LocalModelLibrary.FileId(a));
        Assert.Equal(a, settings.LlamaCppModel, ignoreCase: true);
        LocalAiRoles.Set(settings, LocalAiRole.Chat, LocalModelLibrary.FileId(b));
        Assert.Equal(b, settings.AiEditModel, ignoreCase: true);
        LocalAiRoles.Set(settings, LocalAiRole.Chat, LocalModelLibrary.FileId(a));
        Assert.Equal(string.Empty, settings.AiEditModel);        // the same model does both
        LocalAiRoles.Set(settings, LocalAiRole.Chat, string.Empty);
        Assert.Equal(string.Empty, settings.LocalAiChatModel);
    }

    // ---- the router's preset ----

    [Fact]
    public void The_router_preset_has_a_section_per_model_with_its_context_and_sampling()
    {
        LocalModelPreset zeta = new(8192, 0, 1, 0, 0, 1, "off", "auto", "");
        LocalModelPreset mellum = new(32768, 0.6, 0.95, 20, 0, 1, "model", "experts", "--threads 8 --no-mmap");
        var config = new LlamaCppServerConfig("llama-server", @"D:\models\zeta.gguf", "local", 8080, 32768, "auto", "", @"D:\models\mellum.gguf", "mellum2.1", null,
            new Dictionary<string, LocalModelPreset> { ["local"] = zeta, ["mellum2.1"] = mellum });
        string text = LlamaCppServerArgs.Preset(config);

        string[] sections = text.Split("\n[", StringSplitOptions.None);
        string code = sections.Single(section => section.StartsWith("local]", StringComparison.Ordinal));
        string chat = sections.Single(section => section.StartsWith("mellum2.1]", StringComparison.Ordinal));
        Assert.Contains("c = 8192", code);
        Assert.Contains("temp = 0\n", code);
        Assert.Contains("top-p = 1\n", code);
        Assert.Contains("load-on-startup = true", code);
        Assert.DoesNotContain("cpu-moe", code);
        Assert.Contains("c = 32768", chat);
        Assert.Contains("temp = 0.6\n", chat);
        Assert.Contains("top-p = 0.95", chat);
        Assert.Contains("top-k = 20", chat);
        Assert.Contains("min-p = 0", chat);
        Assert.Contains("repeat-penalty = 1", chat);
        Assert.Contains("cpu-moe = true", chat);            // its offload differs from the global one
        Assert.Contains("threads = 8", chat);
        Assert.Contains("no-mmap = true", chat);
        Assert.Contains("load-on-startup = false", chat);
    }

    [Fact]
    public void Without_presets_the_router_preset_is_what_it_was()
    {
        var config = new LlamaCppServerConfig("llama-server", @"D:\models\zeta.gguf", "local", 8080, 32768, "auto", "", @"D:\models\mellum.gguf", "mellum2.1");
        string text = LlamaCppServerArgs.Preset(config);
        Assert.Equal("version = 1\n\n[*]\nparallel = 1\ncache-reuse = 256\njinja = true\nfit = on\nflash-attn = auto\n"
            + "\n[local]\nmodel = D:\\models\\zeta.gguf\nc = 32768\nload-on-startup = true\n"
            + "\n[mellum2.1]\nmodel = D:\\models\\mellum.gguf\nc = 32768\nload-on-startup = false\n", text);
    }

    [Fact]
    public void Presets_change_the_configs_identity_and_a_single_model_server_uses_the_presets_context()
    {
        var plain = new LlamaCppServerConfig("s", "m.gguf", "local", 8080, 32768, "auto", "");
        var withPreset = plain with { ModelPresets = new Dictionary<string, LocalModelPreset> { ["local"] = new(8192, 0, 1, 0, 0, 1, "off", "auto", "") } };
        Assert.NotEqual(plain, withPreset);
        Assert.Equal(withPreset, plain with { ModelPresets = new Dictionary<string, LocalModelPreset> { ["local"] = new(8192, 0, 1, 0, 0, 1, "off", "auto", "") } });
        List<string> args = LlamaCppServerArgs.Build(withPreset);
        Assert.Equal("8192", args[args.IndexOf("-c") + 1]);
        Assert.Equal("32768", LlamaCppServerArgs.Build(plain) is var p ? p[p.IndexOf("-c") + 1] : "");
    }

    [Fact]
    public void Extra_arguments_become_preset_lines()
    {
        Assert.Equal(["threads = 8", "no-mmap = true", "ctx-size = 4096"], LlamaCppServerArgs.ExtraKeys("--threads 8 --no-mmap --ctx-size 4096"));
        Assert.Empty(LlamaCppServerArgs.ExtraKeys(""));
    }
}
