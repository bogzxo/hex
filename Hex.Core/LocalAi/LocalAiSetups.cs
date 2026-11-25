using System.Globalization;

namespace Hex.Core;

/// <summary>A model a setup uses and the profile (named sampling values, see <see cref="LocalModelPresets.Profiles"/>) it runs with. CatalogId is a catalog id, or "ollama:name" for an Ollama setup.</summary>
internal sealed record LocalAiSetupModel(string CatalogId, string ProfileId);

/// <summary>What a setup sets besides models: how predictions run and how they are shown.</summary>
internal sealed record LocalAiBehaviour(
    string Invocation /* "typing" | "shortcut" */, string Mode /* "eager" | "subtle" */,
    bool FollowRenames, bool EditsElsewhere, bool Autostart);

/// <summary>A recommended combination of models, profiles and behaviour (docs/local-ai-window-plan.md, 4).</summary>
internal sealed record LocalAiSetup(
    string Id, string TitleKey, string PitchKey, IReadOnlyList<string> GetKeys /* "what you get" bullets */,
    string TradeOffKey, LocalAiSetupModel Predict, LocalAiSetupModel Chat,
    double MinVramGb, LocalAiBehaviour Behaviour, bool Ollama = false);

/// <summary>
/// Whether a setup fits a card and what it would download. Fits: it runs (at a reduced context when it has to), Tight: it uses
/// over 85% of the card or needs the reduced context. NeedGb is the larger of its models' needs (the router keeps one in memory),
/// HaveGb 0 means the card is unknown.
/// </summary>
internal sealed record LocalAiSetupFit(bool Fits, bool Tight, double NeedGb, double HaveGb, long DownloadBytes,
    IReadOnlyList<string> MissingCatalogIds, long FreeDiskBytes);

/// <summary>One thing a setup would change: WhatKey is a string key (local_ai_setups.change_*), From and To are what it is now and what it would be.</summary>
internal sealed record LocalAiSetupChange(string WhatKey, string From, string To);

/// <summary>The recommended setups, how they fit a machine, and applying them in one settings write.</summary>
internal static class LocalAiSetups
{
    private const double GIB = 1024.0 * 1024.0 * 1024.0;
    private const int REDUCED_CONTEXT = 8192;
    private const string OLLAMA_ID = "ollama";

    private static LocalAiSetup Make(string id, string predict, string predictProfile, string chat, string chatProfile, double minVram, LocalAiBehaviour behaviour, int gets = 3)
    {
        string key = id.Replace('-', '_');
        return new LocalAiSetup(id, $"local_ai_setups.{key}_title", $"local_ai_setups.{key}_pitch",
            [.. Enumerable.Range(1, gets).Select(n => $"local_ai_setups.{key}_get{n}")], $"local_ai_setups.{key}_tradeoff",
            new LocalAiSetupModel(predict, predictProfile), new LocalAiSetupModel(chat, chatProfile), minVram, behaviour, id == OLLAMA_ID);
    }

    /// <summary>The catalog of docs/local-ai-window-plan.md 4.1 (the Ollama one is built at runtime by <see cref="ForOllama"/>).</summary>
    public static IReadOnlyList<LocalAiSetup> All { get; } =
    [
        Make("allrounder", "qwen3.5-9b", "recommended", "qwen3.5-9b", "recommended", 7, new("shortcut", "eager", true, true, true)),
        Make("best-of-both", "zeta-2.1", "recommended", "mellum2.1", "recommended", 9, new("typing", "eager", true, true, true)),
        Make("reasoning", "mellum2.1", "recommended", "mellum2.1", "recommended", 9, new("shortcut", "eager", true, true, true)),
        Make("light", "qwen2.5-coder-1.5b", "recommended", "qwen2.5-coder-1.5b", "recommended", 2, new("typing", "subtle", true, false, true)),
    ];

    /// <summary>The setup with this id, null for none.</summary>
    public static LocalAiSetup? Find(string id) => All.FirstOrDefault(setup => setup.Id == id);

    /// <summary>The setup to recommend for a card (4.4): 10 GB or more all-rounder; 6 to 10 GB all-rounder when it fits, else light; less, or none, light.</summary>
    public static LocalAiSetup? Recommend(long vramBytes, LocalModelLibrary library, IReadOnlyList<LocalModel> ollama)
    {
        LocalAiSetup allrounder = Find("allrounder")!, light = Find("light")!;
        if (vramBytes >= 10_000_000_000L)
            return allrounder;
        if (vramBytes >= 6_000_000_000L)
            return FitOf(allrounder, library, vramBytes).Fits ? allrounder : light;
        return light;
    }

    private static (double Need, bool Reduced) NeedOf(LlamaCppModel catalog, long vram)
    {
        int context = LocalModelPresets.DefaultContext(catalog.Title + " " + catalog.Id, catalog.Context > 0 ? catalog.Context : null);
        double Need(int tokens) => catalog.Size + LocalModelLibrary.KvBytes(catalog.Layers, catalog.KvHeads, tokens) + 0.8 * GIB;
        double full = Need(context);
        if (vram > 0 && full > vram && context > REDUCED_CONTEXT)
        {
            double reduced = Need(REDUCED_CONTEXT);
            if (reduced <= vram)
                return (reduced, true);
        }
        return (full, false);
    }

    /// <summary>How a setup fits a card: the memory it needs, what is missing on disk, the download and the free disk space.</summary>
    public static LocalAiSetupFit FitOf(LocalAiSetup setup, LocalModelLibrary library, long vramBytes)
    {
        double need = 0;
        bool reduced = false;
        var missing = new List<string>();
        long download = 0;
        foreach (string id in new[] { setup.Predict.CatalogId, setup.Chat.CatalogId }.Distinct())
        {
            if (id.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase) || LlamaCppModels.Find(id) is not { } catalog)
                continue;
            (double each, bool cut) = NeedOf(catalog, vramBytes);
            need = Math.Max(need, each);
            reduced |= cut;
            if (!library.Models.Any(model => model.CatalogId == id && model.IsDownloaded))
            {
                missing.Add(id);
                download += catalog.Size;
            }
        }
        bool cpuOk = setup.Ollama || setup.MinVramGb <= 2;
        bool fits = setup.Ollama || (vramBytes > 0 ? need <= vramBytes : cpuOk);
        bool tight = fits && vramBytes > 0 && !setup.Ollama && (reduced || need > vramBytes * 0.85);
        return new LocalAiSetupFit(fits, tight, Math.Round(need / 1e9, 1), Math.Round(vramBytes / GIB, 1), download, missing, FreeDisk(library));
    }

    private static long FreeDisk(LocalModelLibrary library)
    {
        try
        {
            string folder = library.Settings?.LlamaCppModelsInForce ?? Path.Combine(HexData.Root, "llama.cpp", "models");
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    // The LocalModel id a setup's model is stored as: an Ollama one as it is, a catalog one by its file when downloaded, else catalog:id
    private static string IdOf(LocalAiSetupModel model, LocalModelLibrary library)
    {
        if (model.CatalogId.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
            return model.CatalogId;
        return library.Models.FirstOrDefault(each => each.CatalogId == model.CatalogId && each.IsDownloaded)?.Id ?? "catalog:" + model.CatalogId;
    }

    private static bool Is(LocalModel? model, LocalAiSetupModel wanted) =>
        model is not null && (model.CatalogId == wanted.CatalogId || string.Equals(model.Id, wanted.CatalogId, StringComparison.OrdinalIgnoreCase));

    private static string NameOf(LocalModel? model) => model?.Name ?? string.Empty;

    private static string TitleOf(LocalAiSetupModel model) => LlamaCppModels.Find(model.CatalogId)?.Title ?? model.CatalogId.Replace("ollama:", "");

    private static string Invocation(string value) => value == "typing" ? "after_typing" : value;

    private static string Flag(bool on) => on ? "on" : "off";

    /// <summary>What applying the setup would change. Empty when the settings already match it.</summary>
    public static IReadOnlyList<LocalAiSetupChange> ChangesOf(LocalAiSetup setup, HexSettings settings, LocalModelLibrary library)
    {
        var changes = new List<LocalAiSetupChange>();
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        if (!Is(roles.Predict, setup.Predict))
            changes.Add(new("local_ai_setups.change_predict_model", NameOf(roles.Predict), TitleOf(setup.Predict)));
        if (!Is(roles.Chat, setup.Chat))
            changes.Add(new("local_ai_setups.change_chat_model", NameOf(roles.Chat), TitleOf(setup.Chat)));
        CheckProfile("local_ai_setups.change_predict_profile", setup.Predict, roles.Predict);
        if (setup.Chat != setup.Predict)
            CheckProfile("local_ai_setups.change_chat_profile", setup.Chat, roles.Chat);

        void CheckProfile(string key, LocalAiSetupModel wanted, LocalModel? current)
        {
            // The model it would have, the one in use when it is the right one (its profile is what is compared), else the one that would be set
            LocalModel? model = Is(current, wanted) ? current : LocalAiRoles.Lookup(settings, library, IdOf(wanted, library));
            if (model is null || LocalModelPresets.Profiles(model).FirstOrDefault(profile => profile.Id == wanted.ProfileId) is not { } profile)
                return;
            if (LocalModelPresets.For(model, settings) != profile.Preset)
            {
                LocalModelProfile now = LocalModelPresets.ProfileOf(model, settings);
                bool changedBy = now.Id == wanted.ProfileId;
                changes.Add(new(key, changedBy ? "local_ai_setups.profile_changed" : now.TitleKey, profile.TitleKey));
            }
        }

        LocalAiBehaviour behaviour = setup.Behaviour;
        if (settings.EditPredictionsInvocation != Invocation(behaviour.Invocation))
            changes.Add(new("local_ai_setups.change_invocation", settings.EditPredictionsInvocation, Invocation(behaviour.Invocation)));
        if (settings.EditPredictionsMode != behaviour.Mode)
            changes.Add(new("local_ai_setups.change_mode", settings.EditPredictionsMode, behaviour.Mode));
        if (settings.EditPredictionsRenames != behaviour.FollowRenames)
            changes.Add(new("local_ai_setups.change_renames", Flag(settings.EditPredictionsRenames), Flag(behaviour.FollowRenames)));
        if (settings.EditPredictionsFar != behaviour.EditsElsewhere)
            changes.Add(new("local_ai_setups.change_far", Flag(settings.EditPredictionsFar), Flag(behaviour.EditsElsewhere)));
        if (settings.LlamaCppAutostart != behaviour.Autostart)
            changes.Add(new("local_ai_setups.change_autostart", Flag(settings.LlamaCppAutostart), Flag(behaviour.Autostart)));
        string engine = setup.Ollama ? HexSettings.LOCAL_AI_OLLAMA : HexSettings.LOCAL_AI_LLAMACPP;
        if (settings.LocalAiEngine != engine)
            changes.Add(new("local_ai_setups.change_engine", settings.LocalAiEngine, engine));
        return changes;
    }

    /// <summary>
    /// Sets the roles, the profiles and the behaviour, and records local_ai_setup, all on the settings given (the caller saves
    /// them once). It downloads and starts nothing; the models should be on disk, one that isn't is set as catalog:id.
    /// </summary>
    public static void Apply(LocalAiSetup setup, HexSettings settings, LocalModelLibrary library)
    {
        string predict = IdOf(setup.Predict, library), chat = IdOf(setup.Chat, library);
        settings.LocalAiEngine = setup.Ollama ? HexSettings.LOCAL_AI_OLLAMA : HexSettings.LOCAL_AI_LLAMACPP;
        LocalAiRoles.Set(settings, LocalAiRole.Predict, predict);
        LocalAiRoles.Set(settings, LocalAiRole.Chat, chat);
        LocalModelPresets.UseProfile(settings, predict, setup.Predict.ProfileId);
        LocalModelPresets.UseProfile(settings, chat, setup.Chat.ProfileId);
        LocalAiBehaviour behaviour = setup.Behaviour;
        settings.EditPredictionsInvocation = Invocation(behaviour.Invocation);
        settings.EditPredictionsMode = behaviour.Mode;
        settings.EditPredictionsRenames = behaviour.FollowRenames;
        settings.EditPredictionsFar = behaviour.EditsElsewhere;
        settings.LlamaCppAutostart = behaviour.Autostart;
        settings.LocalAiSetup = setup.Id;
    }

    /// <summary>The "Use my Ollama models" setup, when Ollama has a model that can predict and one that can chat with tools; else empty.</summary>
    public static IReadOnlyList<LocalAiSetup> ForOllama(IReadOnlyList<LocalModel> ollama)
    {
        LocalModel[] mine = [.. ollama.Where(model => model.Source == LocalModelSource.Ollama)];
        // The first model that can do the job, a model that does both preferred so nothing is swapped
        LocalModel? both = mine.FirstOrDefault(model => model.Caps.HasFlag(LocalModelCaps.Fim) && model.Caps.HasFlag(LocalModelCaps.Chat) && model.Caps.HasFlag(LocalModelCaps.Tools));
        LocalModel? predict = both ?? mine.FirstOrDefault(model => model.Caps.HasFlag(LocalModelCaps.Fim)) ?? mine.FirstOrDefault(model => model.Caps.HasFlag(LocalModelCaps.EditModel));
        LocalModel? chat = both ?? mine.FirstOrDefault(model => model.Caps.HasFlag(LocalModelCaps.Chat) && model.Caps.HasFlag(LocalModelCaps.Tools));
        if (predict is null || chat is null)
            return [];
        return [Make(OLLAMA_ID, predict.Id, "balanced", chat.Id, "balanced", 0, new("shortcut", "eager", true, true, true), 2)];
    }
}

/// <summary>Named sampling values for a model, with where they come from.</summary>
internal sealed record LocalModelProfile(string Id, string TitleKey, string DescriptionKey, string Source, LocalModelPreset Preset);

/// <summary>The help content of the Local AI window: keys in en-gb's local_ai_help section, each with _title, _short and _long.</summary>
internal static class LocalAiHelp
{
    /// <summary>Every help key (without the section and the suffix), for search and the tests.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "ui_mode", "setup", "setup_custom", "status",
        "feature_predictions", "feature_edit", "feature_agent", "feature_linkchat",
        "role_predict", "role_chat", "model_library", "model_fit", "model_folders", "favourites",
        "profile", "context", "temperature", "top_p", "top_k", "min_p", "repeat_penalty", "thinking", "offload", "extra_args",
        "engine_choice", "engine_build", "engine_backend", "engine_folder", "engine_port", "engine_autostart", "engine_ollama_url",
        "invocation", "mode", "kind", "debounce", "max_tokens", "renames", "far", "disabled_languages",
        "exclude_globs", "link_chat_answers", "serve_predictions",
        "time_it", "server_log", "router_models", "graphics_memory",
        "glossary_model", "glossary_gguf", "glossary_quantisation", "glossary_context", "glossary_vram", "glossary_offload",
        "glossary_temperature", "glossary_top_p", "glossary_top_k", "glossary_min_p", "glossary_repeat_penalty", "glossary_thinking",
        "glossary_fim", "glossary_swapping",
    ];

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The recommended value of a key as the field would show it, null when it has none (or a model is needed and none given).</summary>
    public static string? RecommendedValue(string key, LocalModel? model, HexSettings settings)
    {
        switch (key)
        {
            case "invocation": return "after_typing";
            case "mode": return "eager";
            case "kind": return "auto";
            case "debounce": return "100";
            case "renames": case "far": case "engine_autostart": return "true";
        }
        if (model is null)
            return null;
        LocalModelPreset preset = LocalModelPresets.Profiles(model)[0].Preset;
        return key switch
        {
            "context" => preset.Context.ToString(CultureInfo.InvariantCulture),
            "temperature" => Number(preset.Temperature),
            "top_p" => Number(preset.TopP),
            "top_k" => preset.TopK.ToString(CultureInfo.InvariantCulture),
            "min_p" => Number(preset.MinP),
            "repeat_penalty" => Number(preset.RepeatPenalty),
            "thinking" => preset.Thinking,
            "offload" => preset.Offload,
            _ => null,
        };
    }
}
