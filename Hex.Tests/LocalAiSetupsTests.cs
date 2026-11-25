using Hex.Core;

namespace Hex.Tests;

/// <summary>The Local AI window's core: the recommended setups, their fit, applying them, the profiles of models and the help texts.</summary>
public sealed class LocalAiSetupsTests : IDisposable
{
    private const long GIB = 1L << 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("hex-setups-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // A library with only the catalog's models (nothing on disk)
    private LocalModelLibrary Library(long vram = 12 * GIB)
    {
        var library = new LocalModelLibrary(() => [_dir], Path.Combine(_dir, "cache.json"), null, () => vram, () => null) { OllamaEnabled = false };
        library.RefreshNow();
        return library;
    }

    private static LocalModel Ollama(string name, LocalModelCaps caps) =>
        new("ollama:" + name, name, LocalModelSource.Ollama, null, 0, caps, null, null, null, null, LocalModelFit.Unknown, null);

    private static string Localisation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private static string Section(string text, string name)
    {
        int start = text.IndexOf("\n" + name + ": {", StringComparison.Ordinal);
        Assert.True(start >= 0, name + " is in en-gb");
        int end = text.IndexOf("\n},", start, StringComparison.Ordinal);
        return text[start..end];
    }

    private static string Value(string section, string key)
    {
        string marker = "\n    " + key + ": \"";
        int at = section.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, key + " is in its section");
        int from = at + marker.Length;
        return section[from..section.IndexOf("\",", from, StringComparison.Ordinal)];
    }

    // ---- the catalog and its fit ----

    [Fact]
    public void The_catalog_names_models_that_are_in_the_model_catalog()
    {
        foreach (LocalAiSetup setup in LocalAiSetups.All)
        {
            Assert.NotNull(LlamaCppModels.Find(setup.Predict.CatalogId));
            Assert.NotNull(LlamaCppModels.Find(setup.Chat.CatalogId));
            Assert.Equal(setup, LocalAiSetups.Find(setup.Id));
        }
        Assert.Equal(["allrounder", "best-of-both", "reasoning", "light"], LocalAiSetups.All.Select(setup => setup.Id));
    }

    [Theory]
    [InlineData(4, "allrounder", false)]
    [InlineData(6, "allrounder", false)]
    [InlineData(8, "allrounder", true)]
    [InlineData(12, "allrounder", true)]
    [InlineData(16, "allrounder", true)]
    [InlineData(4, "light", true)]
    [InlineData(6, "light", true)]
    [InlineData(4, "best-of-both", false)]
    [InlineData(8, "best-of-both", false)]
    [InlineData(12, "best-of-both", true)]
    [InlineData(16, "best-of-both", true)]
    [InlineData(8, "reasoning", false)]
    [InlineData(12, "reasoning", true)]
    public void A_setup_fits_the_card_or_does_not(int gib, string id, bool fits)
    {
        LocalAiSetupFit fit = LocalAiSetups.FitOf(LocalAiSetups.Find(id)!, Library(gib * GIB), gib * GIB);
        Assert.Equal(fits, fit.Fits);
        Assert.True(fit.NeedGb > 0);
    }

    [Fact]
    public void A_setup_that_needs_a_smaller_context_to_fit_is_tight()
    {
        LocalAiSetup allrounder = LocalAiSetups.Find("allrounder")!;
        Assert.True(LocalAiSetups.FitOf(allrounder, Library(8 * GIB), 8 * GIB) is { Fits: true, Tight: true });
        Assert.True(LocalAiSetups.FitOf(allrounder, Library(16 * GIB), 16 * GIB) is { Fits: true, Tight: false });
    }

    [Fact]
    public void The_fit_counts_what_is_missing_and_its_download()
    {
        LocalAiSetupFit both = LocalAiSetups.FitOf(LocalAiSetups.Find("best-of-both")!, Library(), 12 * GIB);
        Assert.Equal(["zeta-2.1", "mellum2.1"], both.MissingCatalogIds);
        Assert.Equal(LlamaCppModels.Find("zeta-2.1")!.Size + LlamaCppModels.Find("mellum2.1")!.Size, both.DownloadBytes);
        LocalAiSetupFit one = LocalAiSetups.FitOf(LocalAiSetups.Find("allrounder")!, Library(), 12 * GIB);
        Assert.Equal(["qwen3.5-9b"], one.MissingCatalogIds);
        Assert.Equal(12.0, one.HaveGb, 1);               // the card as the hardware line says it (GiB), not 12.9
        Assert.InRange(one.NeedGb, 6.5, 8.0);            // Qwen3.5 9B: its weights, a small KV cache (8 attention layers) and the overhead
    }

    [Fact]
    public void A_model_on_disk_is_not_missing()
    {
        string file = Path.Combine(_dir, LlamaCppModels.Find("qwen3.5-9b")!.File);
        File.WriteAllBytes(file, new byte[64]);
        var library = new LocalModelLibrary(() => [_dir], Path.Combine(_dir, "cache.json"), _ => null, () => 12 * GIB, () => null) { OllamaEnabled = false };
        library.RefreshNow();
        LocalAiSetupFit fit = LocalAiSetups.FitOf(LocalAiSetups.Find("allrounder")!, library, 12 * GIB);
        Assert.Empty(fit.MissingCatalogIds);
        Assert.Equal(0, fit.DownloadBytes);
    }

    // ---- Recommend ----

    [Theory]
    [InlineData(16, "allrounder")]
    [InlineData(12, "allrounder")]
    [InlineData(10, "allrounder")]
    [InlineData(8, "allrounder")]
    [InlineData(6, "light")]
    [InlineData(4, "light")]
    [InlineData(0, "light")]
    public void The_recommendation_follows_the_card(int gib, string id) =>
        Assert.Equal(id, LocalAiSetups.Recommend(gib * GIB, Library(gib * GIB), [])!.Id);

    // ---- ChangesOf and Apply ----

    [Theory]
    [InlineData("allrounder")]
    [InlineData("best-of-both")]
    [InlineData("reasoning")]
    [InlineData("light")]
    public void Applying_a_setup_leaves_nothing_to_change(string id)
    {
        LocalModelLibrary library = Library();
        var settings = new HexSettings(null);
        LocalAiSetup setup = LocalAiSetups.Find(id)!;
        Assert.NotEmpty(LocalAiSetups.ChangesOf(setup, settings, library));
        LocalAiSetups.Apply(setup, settings, library);
        Assert.Equal(id, settings.LocalAiSetup);
        Assert.Empty(LocalAiSetups.ChangesOf(setup, settings, library));
    }

    [Fact]
    public void A_changed_chat_model_is_one_change()
    {
        LocalModelLibrary library = Library();
        var settings = new HexSettings(null);
        LocalAiSetup setup = LocalAiSetups.Find("allrounder")!;
        LocalAiSetups.Apply(setup, settings, library);
        LocalAiRoles.Set(settings, LocalAiRole.Chat, "catalog:mellum2.1");
        LocalAiSetupChange change = Assert.Single(LocalAiSetups.ChangesOf(setup, settings, library));
        Assert.Equal("local_ai_setups.change_chat_model", change.WhatKey);
        Assert.Equal("Mellum2.1 12B-A2.5B", change.From);
        Assert.Equal("Qwen3.5 9B", change.To);
    }

    [Fact]
    public void A_profile_picked_by_hand_and_a_changed_behaviour_are_changes()
    {
        LocalModelLibrary library = Library();
        var settings = new HexSettings(null);
        LocalAiSetup setup = LocalAiSetups.Find("allrounder")!;
        LocalAiSetups.Apply(setup, settings, library);
        LocalModelPresets.UseProfile(settings, "catalog:qwen3.5-9b", "precise");
        settings.EditPredictionsInvocation = "after_typing";
        string[] keys = [.. LocalAiSetups.ChangesOf(setup, settings, library).Select(change => change.WhatKey)];
        Assert.Contains("local_ai_setups.change_predict_profile", keys);
        Assert.Contains("local_ai_setups.change_invocation", keys);
        Assert.DoesNotContain("local_ai_setups.change_chat_model", keys);
    }

    [Fact]
    public void A_setup_sets_the_behaviour()
    {
        var settings = new HexSettings(null);
        LocalAiSetups.Apply(LocalAiSetups.Find("light")!, settings, Library());
        Assert.Equal("after_typing", settings.EditPredictionsInvocation);
        Assert.Equal("subtle", settings.EditPredictionsMode);
        Assert.False(settings.EditPredictionsFar);
        Assert.True(settings.EditPredictionsRenames);
        Assert.Equal("catalog:qwen2.5-coder-1.5b", settings.LocalAiPredictModel);
        Assert.Equal(HexSettings.LOCAL_AI_LLAMACPP, settings.LocalAiEngine);
    }

    // ---- Ollama ----

    [Fact]
    public void An_ollama_setup_comes_from_what_the_models_can_do()
    {
        LocalModel fim = Ollama("qwen2.5-coder:7b", LocalModelCaps.Fim | LocalModelCaps.Predict | LocalModelCaps.Chat);
        LocalModel chat = Ollama("llama3.1:8b", LocalModelCaps.Chat | LocalModelCaps.Tools);
        LocalModel both = Ollama("qwen3:8b", LocalModelCaps.Fim | LocalModelCaps.Predict | LocalModelCaps.Chat | LocalModelCaps.Tools);

        LocalAiSetup split = Assert.Single(LocalAiSetups.ForOllama([fim, chat]));
        Assert.True(split.Ollama);
        Assert.Equal("ollama:qwen2.5-coder:7b", split.Predict.CatalogId);
        Assert.Equal("ollama:llama3.1:8b", split.Chat.CatalogId);
        Assert.Equal(0, split.MinVramGb);

        LocalAiSetup one = Assert.Single(LocalAiSetups.ForOllama([fim, chat, both]));
        Assert.Equal("ollama:qwen3:8b", one.Predict.CatalogId);
        Assert.Equal(one.Predict.CatalogId, one.Chat.CatalogId);
    }

    [Fact]
    public void No_ollama_setup_without_a_model_for_each_job()
    {
        Assert.Empty(LocalAiSetups.ForOllama([]));
        Assert.Empty(LocalAiSetups.ForOllama([Ollama("llama3.1:8b", LocalModelCaps.Chat | LocalModelCaps.Tools)]));
        Assert.Empty(LocalAiSetups.ForOllama([Ollama("mxbai", LocalModelCaps.None)]));
    }

    [Fact]
    public void An_ollama_setup_is_applied_and_matches()
    {
        LocalModelLibrary library = Library();
        var settings = new HexSettings(null);
        LocalAiSetup setup = LocalAiSetups.ForOllama([Ollama("qwen3:8b", LocalModelCaps.Fim | LocalModelCaps.Predict | LocalModelCaps.Chat | LocalModelCaps.Tools)])[0];
        LocalAiSetups.Apply(setup, settings, library);
        Assert.Equal(HexSettings.LOCAL_AI_OLLAMA, settings.LocalAiEngine);
        Assert.Equal("ollama:qwen3:8b", settings.LocalAiPredictModel);
        Assert.Empty(LocalAiSetups.ChangesOf(setup, settings, library));
        Assert.Contains(LocalAiSetups.ChangesOf(LocalAiSetups.Find("light")!, settings, library), change => change.WhatKey == "local_ai_setups.change_engine");
    }

    // ---- profiles ----

    private static LocalModel Catalog(string id)
    {
        LlamaCppModel entry = LlamaCppModels.Find(id)!;
        return new("catalog:" + id, entry.Title, LocalModelSource.Catalog, null, entry.Size, LocalModelLibrary.CatalogCaps(entry), entry.Context, null, null, id, LocalModelFit.Fits, null);
    }

    [Fact]
    public void Qwen_has_recommended_thinking_and_precise_with_the_cards_values()
    {
        IReadOnlyList<LocalModelProfile> profiles = LocalModelPresets.Profiles(Catalog("qwen3.5-9b"));
        Assert.Equal(["recommended", "thinking", "precise"], profiles.Select(profile => profile.Id));
        LocalModelPreset recommended = profiles[0].Preset, thinking = profiles[1].Preset, precise = profiles[2].Preset;
        Assert.Equal((32768, 0.7, 0.8, 20, 0.0, 1.0, "model"), (recommended.Context, recommended.Temperature, recommended.TopP, recommended.TopK, recommended.MinP, recommended.RepeatPenalty, recommended.Thinking));
        Assert.Equal((0.6, 0.95, 20, "on"), (thinking.Temperature, thinking.TopP, thinking.TopK, thinking.Thinking));
        Assert.Equal((0.2, "off"), (precise.Temperature, precise.Thinking));
        Assert.Contains("huggingface.co/Qwen/Qwen3.5-9B", profiles[0].Source);
        Assert.Equal(LocalModelPresets.Default(Catalog("qwen3.5-9b")), recommended);
    }

    [Fact]
    public void Mellum_zeta_and_the_small_coder_have_theirs()
    {
        IReadOnlyList<LocalModelProfile> mellum = LocalModelPresets.Profiles(Catalog("mellum2.1"));
        Assert.Equal(["recommended", "fast"], mellum.Select(profile => profile.Id));
        Assert.Equal((0.6, 0.95, 20), (mellum[0].Preset.Temperature, mellum[0].Preset.TopP, mellum[0].Preset.TopK));
        Assert.Contains("huggingface.co/JetBrains/Mellum2.1", mellum[0].Source);
        Assert.Equal(16384, mellum[1].Preset.Context);
        Assert.Equal("off", mellum[1].Preset.Thinking);

        LocalModelProfile zeta = Assert.Single(LocalModelPresets.Profiles(Catalog("zeta-2.1")));
        Assert.Equal((8192, 0.0, 1.0, 0), (zeta.Preset.Context, zeta.Preset.Temperature, zeta.Preset.TopP, zeta.Preset.TopK));

        IReadOnlyList<LocalModelProfile> coder = LocalModelPresets.Profiles(Catalog("qwen2.5-coder-1.5b"));
        Assert.Equal((0.2, 0.9, 40, 0.05, 1.05), (coder[0].Preset.Temperature, coder[0].Preset.TopP, coder[0].Preset.TopK, coder[0].Preset.MinP, coder[0].Preset.RepeatPenalty));
        Assert.Equal((0.7, 0.8, 20, 1.1), (coder[1].Preset.Temperature, coder[1].Preset.TopP, coder[1].Preset.TopK, coder[1].Preset.RepeatPenalty));
        Assert.Contains("generation_config.json", coder[1].Source);
    }

    [Fact]
    public void An_unknown_model_is_balanced_or_precise()
    {
        var model = new LocalModel("file:c:/m/foo.gguf", "Foo 7B", LocalModelSource.File, "c:/m/foo.gguf", 4_000_000_000, LocalModelCaps.Chat, 8192, null, null, null, LocalModelFit.Fits, null);
        IReadOnlyList<LocalModelProfile> profiles = LocalModelPresets.Profiles(model);
        Assert.Equal(["balanced", "precise"], profiles.Select(profile => profile.Id));
        Assert.Equal(8192, profiles[0].Preset.Context);
        Assert.Equal(0.7, profiles[0].Preset.Temperature);
        Assert.Equal(0.2, profiles[1].Preset.Temperature);
    }

    [Fact]
    public void Every_profile_has_its_words_in_the_localisation()
    {
        string profiles = Section(Localisation(), "local_ai_profiles");
        foreach (string id in new[] { "qwen3.5-9b", "mellum2.1", "zeta-2.1", "qwen2.5-coder-1.5b" })
            foreach (LocalModelProfile profile in LocalModelPresets.Profiles(Catalog(id)))
            {
                Assert.False(string.IsNullOrWhiteSpace(Value(profiles, profile.TitleKey["local_ai_profiles.".Length..])));
                Assert.False(string.IsNullOrWhiteSpace(Value(profiles, profile.DescriptionKey["local_ai_profiles.".Length..])));
                Assert.False(string.IsNullOrWhiteSpace(profile.Source));
            }
        foreach (LocalModelProfile profile in LocalModelPresets.Profiles(new LocalModel("file:x", "Foo", LocalModelSource.File, "x", 1, LocalModelCaps.Chat, null, null, null, null, LocalModelFit.Fits, null)))
            Assert.False(string.IsNullOrWhiteSpace(Value(profiles, profile.DescriptionKey["local_ai_profiles.".Length..])));
    }

    [Fact]
    public void Using_a_profile_clears_the_fields_changed_by_hand()
    {
        LocalModel model = Catalog("qwen3.5-9b");
        var settings = new HexSettings(null);
        LocalModelPresets.Save(settings, model.Id, LocalModelPresets.For(model, settings) with { Temperature = 0.9, TopK = 55 }, model);
        Assert.Equal(0.9, LocalModelPresets.For(model, settings).Temperature);

        LocalModelPresets.UseProfile(settings, model.Id, "precise");
        Assert.Equal("precise", LocalModelPresets.ProfileOf(model, settings).Id);
        Assert.Equal(LocalModelPresets.Profiles(model)[2].Preset, LocalModelPresets.For(model, settings));
        Assert.Contains("\"profile\":\"precise\"", settings.LocalAiPresets);
        Assert.DoesNotContain("temperature", settings.LocalAiPresets);

        // A field changed on top of the profile is kept beside the profile, and is compared with the profile not the default
        LocalModelPresets.Save(settings, model.Id, LocalModelPresets.For(model, settings) with { Temperature = 0.3 }, model);
        Assert.Equal("precise", LocalModelPresets.ProfileOf(model, settings).Id);
        Assert.Equal(0.3, LocalModelPresets.For(model, settings).Temperature);
        Assert.Equal(0.9, LocalModelPresets.For(model, settings).TopP);
        Assert.DoesNotContain("top_p", settings.LocalAiPresets);

        // The first profile is stored as nothing at all
        LocalModelPresets.UseProfile(settings, model.Id, "recommended");
        Assert.Equal(string.Empty, settings.LocalAiPresets);
        Assert.Equal("recommended", LocalModelPresets.ProfileOf(model, settings).Id);
        Assert.Equal(LocalModelPresets.Default(model), LocalModelPresets.For(model, settings));
    }

    [Fact]
    public void An_unknown_stored_profile_falls_back_to_the_first()
    {
        LocalModel model = Catalog("zeta-2.1");
        var settings = new HexSettings(null) { LocalAiPresets = "{\"catalog:zeta-2.1\":{\"profile\":\"gone\"}}" };
        Assert.Equal("recommended", LocalModelPresets.ProfileOf(model, settings).Id);
        Assert.Equal(LocalModelPresets.Default(model), LocalModelPresets.For(model, settings));
    }

    // ---- the settings ----

    [Fact]
    public void The_window_mode_is_automatic_until_the_user_chooses()
    {
        var settings = new HexSettings(null);
        Assert.Equal("auto", settings.LocalAiUiMode);
        Assert.Equal("simple", settings.LocalAiUiModeInForce);
        Assert.Equal(string.Empty, settings.LocalAiSetup);

        settings.LlamaCppPort = 9000;                                  // an advanced setting changed before, but nothing is set up
        Assert.Equal("simple", settings.LocalAiUiModeInForce);
        settings.LocalAiPredictModel = "catalog:zeta-2.1";
        Assert.Equal("advanced", settings.LocalAiUiModeInForce);
        settings.LocalAiUiMode = "simple";
        Assert.Equal("simple", settings.LocalAiUiModeInForce);
        settings.LocalAiUiMode = "advanced";
        Assert.Equal("advanced", settings.LocalAiUiModeInForce);
    }

    [Fact]
    public void Presets_of_the_users_own_make_auto_advanced_but_a_setups_do_not()
    {
        var settings = new HexSettings(null) { LocalAiPresets = "{\"x\":{\"temperature\":0.5}}", LocalAiPredictModel = "catalog:zeta-2.1" };
        Assert.Equal("advanced", settings.LocalAiUiModeInForce);

        var used = new HexSettings(null);
        LocalAiSetups.Apply(LocalAiSetups.Find("allrounder")!, used, Library());
        LocalModelPresets.UseProfile(used, "catalog:qwen3.5-9b", "precise");
        Assert.NotEqual(string.Empty, used.LocalAiPresets);
        Assert.Equal("simple", used.LocalAiUiModeInForce);
    }

    [Fact]
    public void Every_advanced_only_setting_counts_for_the_migration()
    {
        Action<HexSettings>[] changes =
        [
            s => s.LlamaCppContext = 4096, s => s.LlamaCppOffload = "cpu", s => s.LlamaCppExtraArgs = "--threads 8",
            s => s.LlamaCppFolder = @"D:\llama", s => s.LlamaCppModels = @"D:\models", s => s.LocalAiModelFolders = @"D:\more", s => s.LlamaCppAlias = "mine",
        ];
        foreach (Action<HexSettings> change in changes)
        {
            var settings = new HexSettings(null);
            change(settings);
            Assert.Equal("simple", settings.LocalAiUiModeInForce);     // folders alone, before any setup, are not advanced use
            settings.LocalAiChatModel = "catalog:zeta-2.1";
            Assert.Equal("advanced", settings.LocalAiUiModeInForce);
        }
    }

    [Fact]
    public void The_two_settings_are_rows_with_a_usual_default()
    {
        var settings = new HexSettings(null);
        var values = settings.EditorValues().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Contains("local_ai_ui_mode", values.Keys);
        Assert.Contains("local_ai_setup", values.Keys);
    }

    // ---- the help ----

    [Fact]
    public void The_help_has_at_least_sixty_unique_keys()
    {
        Assert.True(LocalAiHelp.Keys.Count >= 60);
        Assert.Equal(LocalAiHelp.Keys.Count, LocalAiHelp.Keys.Distinct().Count());
    }

    [Fact]
    public void Every_help_key_has_a_title_a_short_line_and_a_long_text_in_en_gb()
    {
        string help = Section(Localisation(), "local_ai_help");
        foreach (string key in LocalAiHelp.Keys)
        {
            string title = Value(help, key + "_title"), shortLine = Value(help, key + "_short"), longText = Value(help, key + "_long");
            Assert.False(string.IsNullOrWhiteSpace(title), key);
            Assert.True(shortLine.Length is > 10 and <= 100, key + " short is " + shortLine.Length);
            Assert.True(longText.Length > shortLine.Length, key + " long");
            Assert.True(longText.Count(c => c is '.' or '?') is >= 2 and <= 6, key + " long is two to four sentences");
        }
    }

    [Fact]
    public void The_help_in_en_gb_has_no_key_that_is_not_listed()
    {
        string help = Section(Localisation(), "local_ai_help");
        var listed = new HashSet<string>(LocalAiHelp.Keys);
        foreach (string line in help.Split('\n'))
        {
            string name = line.Trim().Split(':')[0];
            foreach (string suffix in new[] { "_title", "_short", "_long" })
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                    Assert.Contains(name[..^suffix.Length], listed);
        }
    }

    [Fact]
    public void The_help_names_the_ui_keys_it_was_asked_for()
    {
        string[] asked =
        [
            "feature_predictions", "feature_edit", "feature_agent", "feature_linkchat", "role_predict", "role_chat", "profile", "context", "temperature",
            "top_p", "top_k", "min_p", "repeat_penalty", "thinking", "offload", "extra_args", "engine_choice", "engine_build", "engine_folder", "engine_port",
            "engine_autostart", "engine_ollama_url", "invocation", "kind", "debounce", "renames", "far", "disabled_languages", "exclude_globs",
            "link_chat_answers", "serve_predictions", "glossary_model", "glossary_gguf", "glossary_quantisation", "glossary_context", "glossary_vram",
            "glossary_offload", "glossary_temperature", "glossary_top_p", "glossary_top_k", "glossary_min_p", "glossary_repeat_penalty", "glossary_thinking",
            "glossary_fim", "glossary_swapping",
        ];
        foreach (string key in asked)
            Assert.Contains(key, LocalAiHelp.Keys);
    }

    [Fact]
    public void Every_setup_has_its_words_in_en_gb()
    {
        string setups = Section(Localisation(), "local_ai_setups");
        LocalAiSetup ollama = LocalAiSetups.ForOllama([Ollama("qwen3:8b", LocalModelCaps.Fim | LocalModelCaps.Predict | LocalModelCaps.Chat | LocalModelCaps.Tools)])[0];
        foreach (LocalAiSetup setup in LocalAiSetups.All.Append(ollama))
        {
            foreach (string key in new[] { setup.TitleKey, setup.PitchKey, setup.TradeOffKey }.Concat(setup.GetKeys))
                Assert.False(string.IsNullOrWhiteSpace(Value(setups, key["local_ai_setups.".Length..])), key);
            Assert.InRange(setup.GetKeys.Count, 2, 3);
        }
        foreach (string key in new[] { "recommended_ribbon", "use_setup", "details", "return_to", "custom_based_on", "fits", "fits_tight", "does_not_fit", "profile_changed",
            "change_predict_model", "change_chat_model", "change_predict_profile", "change_chat_profile", "change_invocation", "change_mode", "change_renames",
            "change_far", "change_autostart", "change_engine" })
            Assert.False(string.IsNullOrWhiteSpace(Value(setups, key)), key);
    }

    [Fact]
    public void The_two_new_settings_have_their_words()
    {
        string text = Localisation();
        foreach (string key in new[] { "local_ai_ui_mode_title", "local_ai_ui_mode_help", "local_ai_ui_mode_auto", "local_ai_ui_mode_simple", "local_ai_ui_mode_advanced",
            "local_ai_setup_title", "local_ai_setup_help", "local_ai_setup_placeholder" })
            Assert.Contains(key + ": \"", text);
    }

    [Fact]
    public void Recommended_values_are_the_first_profiles_as_the_field_shows_them()
    {
        var settings = new HexSettings(null);
        LocalModel qwen = Catalog("qwen3.5-9b");
        Assert.Equal("0.7", LocalAiHelp.RecommendedValue("temperature", qwen, settings));
        Assert.Equal("0.8", LocalAiHelp.RecommendedValue("top_p", qwen, settings));
        Assert.Equal("20", LocalAiHelp.RecommendedValue("top_k", qwen, settings));
        Assert.Equal("0", LocalAiHelp.RecommendedValue("min_p", qwen, settings));
        Assert.Equal("1", LocalAiHelp.RecommendedValue("repeat_penalty", qwen, settings));
        Assert.Equal("32768", LocalAiHelp.RecommendedValue("context", qwen, settings));
        Assert.Equal("model", LocalAiHelp.RecommendedValue("thinking", qwen, settings));
        Assert.Equal("auto", LocalAiHelp.RecommendedValue("offload", qwen, settings));
        Assert.Equal("after_typing", LocalAiHelp.RecommendedValue("invocation", null, settings));
        Assert.Equal("auto", LocalAiHelp.RecommendedValue("kind", null, settings));
        Assert.Equal("100", LocalAiHelp.RecommendedValue("debounce", null, settings));
        Assert.Equal("true", LocalAiHelp.RecommendedValue("renames", null, settings));
        Assert.Equal("true", LocalAiHelp.RecommendedValue("engine_autostart", null, settings));
        Assert.Null(LocalAiHelp.RecommendedValue("temperature", null, settings));
        Assert.Null(LocalAiHelp.RecommendedValue("extra_args", qwen, settings));
    }
}
