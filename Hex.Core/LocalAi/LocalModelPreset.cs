using System.Globalization;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// How one model is run and sampled: the context it's loaded with, its sampling, whether it thinks, where its weights go
/// and any extra llama-server arguments. Built in by family, overridden per model in local_ai_presets.
/// </summary>
internal sealed record LocalModelPreset(
    int Context, double Temperature, double TopP, int TopK, double MinP, double RepeatPenalty,
    string Thinking /* "model" | "on" | "off" */, string Offload /* auto|gpu|experts|cpu */, string Extra);

internal static class LocalModelPresets
{
    private const string THINK_MODEL = "model", THINK_ON = "on", THINK_OFF = "off";

    /// <summary>The families the built-in defaults tell apart.</summary>
    internal enum Family { Generic, Zeta, Qwen35, Mellum, Qwen25Coder }

    /// <summary>The family a model name (and architecture, when known) belongs to.</summary>
    internal static Family FamilyOf(string name, string? architecture = null, bool isZeta = false)
    {
        string key = name.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        if (isZeta || key.Contains("zeta", StringComparison.Ordinal))
            return Family.Zeta;
        if (key.Contains("mellum", StringComparison.Ordinal))
            return Family.Mellum;
        if (key.Contains("qwen3.5", StringComparison.Ordinal) || key.Contains("qwen35", StringComparison.Ordinal))
            return Family.Qwen35;
        if (key.Contains("qwen2.5coder", StringComparison.Ordinal) || key.Contains("qwen25coder", StringComparison.Ordinal))
            return Family.Qwen25Coder;
        return Family.Generic;
    }

    /// <summary>The context the default preset of a model with this name and largest context uses.</summary>
    internal static int DefaultContext(string name, int? contextMax, string? architecture = null, bool isZeta = false)
    {
        int family = FamilyOf(name, architecture, isZeta) switch
        {
            Family.Zeta => 8192,
            Family.Qwen35 or Family.Mellum => 32768,
            Family.Qwen25Coder => 16384,
            _ => 16384,
        };
        return contextMax is > 0 ? Math.Min(family, contextMax.Value) : family;
    }

    /// <summary>
    /// The built-in default by family: zeta (ctx 8192, temp 0); qwen3.5 (ctx 32768, temp 0.7, top_p 0.8, top_k 20, min_p 0);
    /// mellum thinking (ctx 32768, temp 0.6, top_p 0.95); qwen2.5-coder (ctx 16384, temp 0.2). Otherwise a generic default.
    /// Offload: experts for a mixture of experts that doesn't fit, else auto.
    /// </summary>
    public static LocalModelPreset Default(LocalModel model)
    {
        bool zeta = model.Gguf?.IsZeta ?? false;
        Family family = FamilyOf(model.Name + " " + (model.CatalogId ?? "") + " " + System.IO.Path.GetFileName(model.Path ?? ""), model.Gguf?.Architecture, zeta);
        int context = DefaultContext(model.Name + " " + (model.CatalogId ?? "") + " " + System.IO.Path.GetFileName(model.Path ?? ""), model.ContextMax, model.Gguf?.Architecture, zeta);
        bool moe = (model.Gguf?.ExpertCount ?? 0) > 1 || (model.CatalogId is { } id && LlamaCppModels.Find(id)?.MixtureOfExperts == true);
        string offload = moe && model.Fit is LocalModelFit.TooBig or LocalModelFit.Tight ? LlamaCppServerArgs.EXPERTS : LlamaCppServerArgs.AUTO;
        return family switch
        {
            Family.Zeta => new(context, 0.0, 1.0, 0, 0.0, 1.0, THINK_OFF, offload, ""),
            Family.Qwen35 => new(context, 0.7, 0.8, 20, 0.0, 1.0, THINK_MODEL, offload, ""),
            Family.Mellum => new(context, 0.6, 0.95, 20, 0.0, 1.0, THINK_MODEL, offload, ""),
            Family.Qwen25Coder => new(context, 0.2, 0.9, 40, 0.05, 1.05, THINK_OFF, offload, ""),
            _ => new(context, 0.7, 0.9, 40, 0.05, 1.0, THINK_MODEL, offload, ""),
        };
    }

    // Where the sampling values of the profiles come from
    private const string QWEN35_CARD = "https://huggingface.co/Qwen/Qwen3.5-9B";
    private const string MELLUM_CARD = "https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking";
    private const string QWEN25_CONFIG = "https://huggingface.co/Qwen/Qwen2.5-Coder-1.5B-Instruct/raw/main/generation_config.json";
    private const string HEX_DEFAULT = "Hex default";

    private static Family FamilyOfModel(LocalModel model) =>
        FamilyOf(model.Name + " " + (model.CatalogId ?? "") + " " + System.IO.Path.GetFileName(model.Path ?? ""), model.Gguf?.Architecture, model.Gguf?.IsZeta ?? false);

    private static string FamilyKey(Family family) => family switch
    {
        Family.Zeta => "zeta", Family.Qwen35 => "qwen35", Family.Mellum => "mellum", Family.Qwen25Coder => "qwen25coder", _ => "generic",
    };

    /// <summary>
    /// The named profiles of a model's family, the recommended one (the model's built-in default) first
    /// (docs/local-ai-window-plan.md, 5). Their values are checked against the model cards named in each Source.
    /// </summary>
    public static IReadOnlyList<LocalModelProfile> Profiles(LocalModel model)
    {
        Family family = FamilyOfModel(model);
        LocalModelPreset basis = Default(model);
        string key = FamilyKey(family);
        LocalModelProfile Make(string id, string source, LocalModelPreset preset) =>
            new(id, $"local_ai_profiles.{id}_title", $"local_ai_profiles.{key}_{id}_desc", source, preset);
        return family switch
        {
            // Qwen's cards: non-thinking 0.7 / 0.8 / 20, thinking for precise coding 0.6 / 0.95 / 20
            Family.Qwen35 =>
            [
                Make("recommended", QWEN35_CARD, basis),
                Make("thinking", QWEN35_CARD, basis with { Temperature = 0.6, TopP = 0.95, TopK = 20, MinP = 0.0, RepeatPenalty = 1.0, Thinking = THINK_ON }),
                Make("precise", HEX_DEFAULT, basis with { Temperature = 0.2, TopP = 0.9, TopK = 20, MinP = 0.0, RepeatPenalty = 1.0, Thinking = THINK_OFF }),
            ],
            // JetBrains' card: temperature 0.6, top_p 0.95, top_k 20, and it only thinks
            Family.Mellum =>
            [
                Make("recommended", MELLUM_CARD, basis with { Temperature = 0.6, TopP = 0.95, TopK = 20, MinP = 0.0, RepeatPenalty = 1.0 }),
                Make("fast", HEX_DEFAULT, basis with { Context = Math.Min(basis.Context, 16384), Temperature = 0.6, TopP = 0.95, TopK = 20, MinP = 0.0, RepeatPenalty = 1.0, Thinking = THINK_OFF }),
            ],
            Family.Zeta => [Make("recommended", HEX_DEFAULT, basis)],
            // The card gives no values: low temperature suits completion. Its generation_config.json is 0.7 / 0.8 / 20 / repeat 1.1
            Family.Qwen25Coder =>
            [
                Make("recommended", HEX_DEFAULT, basis),
                Make("balanced", QWEN25_CONFIG, basis with { Temperature = 0.7, TopP = 0.8, TopK = 20, MinP = 0.0, RepeatPenalty = 1.1, Thinking = THINK_OFF }),
            ],
            _ =>
            [
                Make("balanced", HEX_DEFAULT, basis),
                Make("precise", HEX_DEFAULT, basis with { Temperature = 0.2 }),
            ],
        };
    }

    /// <summary>The profile the model runs with: the stored "profile" id of its entry in local_ai_presets, else the first.</summary>
    public static LocalModelProfile ProfileOf(LocalModel model, HexSettings settings)
    {
        IReadOnlyList<LocalModelProfile> all = Profiles(model);
        return StoredProfile(settings.LocalAiPresets, model.Id) is { } id && all.FirstOrDefault(profile => profile.Id == id) is { } found ? found : all[0];
    }

    /// <summary>
    /// Stores a profile for a model and clears the field overrides, so the fields show the profile's values. The first
    /// (recommended) profile is stored as no entry at all, which is the same thing.
    /// </summary>
    public static void UseProfile(HexSettings settings, string modelId, string profileId)
    {
        LocalModel? model = LocalAiRoles.Lookup(settings, LocalModelLibrary.Shared, modelId);
        IReadOnlyList<LocalModelProfile>? all = model is null ? null : Profiles(model);
        JsonObject presets = Parse(settings.LocalAiPresets);
        string[] same = [.. presets.Select(pair => pair.Key).Where(name => string.Equals(name, modelId, StringComparison.OrdinalIgnoreCase))];
        foreach (string name in same)
            presets.Remove(name);
        if (all is null || (all.Count > 0 && all[0].Id != profileId && all.Any(profile => profile.Id == profileId)))
            presets[modelId] = new JsonObject { ["profile"] = profileId };
        settings.LocalAiPresets = presets.Count == 0 ? string.Empty : presets.ToJsonString();
    }

    private static string? StoredProfile(string json, string modelId) =>
        Overrides(json, modelId) is { } fields && fields["profile"] is JsonValue value && value.TryGetValue(out string? id) ? id : null;

    /// <summary>The preset in force: the model's profile (the default unless one was picked) with the user's overrides from local_ai_presets laid over it.</summary>
    public static LocalModelPreset For(LocalModel model, HexSettings settings)
    {
        LocalModelPreset preset = Default(model);
        if (Overrides(settings.LocalAiPresets, model.Id) is not { } fields)
            return preset;
        if (fields["profile"] is not null)
            preset = ProfileOf(model, settings).Preset;
        return Apply(preset, fields);
    }

    /// <summary>Stores a preset as the user's override for a model: only the fields that differ from the default are kept.</summary>
    public static void Save(HexSettings settings, string modelId, LocalModelPreset preset, LocalModel? model = null)
    {
        model ??= LocalModelLibrary.Shared.Find(modelId);
        // What the fields are compared with is the profile in use (the default when none was picked)
        LocalModelPreset basis = model is null ? preset with { } : ProfileOf(model, settings).Preset;
        JsonObject all = Parse(settings.LocalAiPresets);
        var fields = new JsonObject();
        string? profile = StoredProfile(settings.LocalAiPresets, modelId);
        if (profile is not null)
            fields["profile"] = profile;
        if (model is null || preset.Context != basis.Context) fields["context"] = preset.Context;
        if (model is null || preset.Temperature != basis.Temperature) fields["temperature"] = preset.Temperature;
        if (model is null || preset.TopP != basis.TopP) fields["top_p"] = preset.TopP;
        if (model is null || preset.TopK != basis.TopK) fields["top_k"] = preset.TopK;
        if (model is null || preset.MinP != basis.MinP) fields["min_p"] = preset.MinP;
        if (model is null || preset.RepeatPenalty != basis.RepeatPenalty) fields["repeat_penalty"] = preset.RepeatPenalty;
        if (model is null || preset.Thinking != basis.Thinking) fields["thinking"] = preset.Thinking;
        if (model is null || preset.Offload != basis.Offload) fields["offload"] = preset.Offload;
        if (model is null || preset.Extra != basis.Extra) fields["extra"] = preset.Extra;
        if (fields.Count == 0)
            all.Remove(modelId);
        else if (fields.Count == 1 && fields["profile"] is not null && model is not null && Profiles(model)[0].Id == profile)
            all.Remove(modelId);
        else
            all[modelId] = fields;
        settings.LocalAiPresets = all.Count == 0 ? string.Empty : all.ToJsonString();
    }

    /// <summary>Drops the user's overrides of a model, back to its default.</summary>
    public static void Reset(HexSettings settings, string modelId)
    {
        JsonObject all = Parse(settings.LocalAiPresets);
        if (all.Remove(modelId))
            settings.LocalAiPresets = all.Count == 0 ? string.Empty : all.ToJsonString();
    }

    /// <summary>The request-side tuning a preset makes, thinking carried as the settings write it.</summary>
    public static PredictionTuning Tuning(LocalModelPreset preset, PredictionTuning fallback) => fallback with
    {
        Temperature = (float)preset.Temperature,
        TopP = (float)preset.TopP,
        TopK = preset.TopK,
        RepeatPenalty = (float)preset.RepeatPenalty,
        Thinking = preset.Thinking switch { THINK_ON => "on", THINK_OFF => "off", _ => fallback.Thinking },
    };

    private static JsonObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static JsonObject? Overrides(string json, string modelId)
    {
        JsonObject all = Parse(json);
        foreach (var (key, value) in all)
            if (string.Equals(key, modelId, StringComparison.OrdinalIgnoreCase) && value is JsonObject fields)
                return fields;
        return null;
    }

    private static LocalModelPreset Apply(LocalModelPreset preset, JsonObject fields)
    {
        static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue(out double d)) return d;
            if (value.TryGetValue(out int i)) return i;
            if (value.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
            return null;
        }
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

        if (Number(fields["context"]) is { } context && context > 0) preset = preset with { Context = (int)context };
        if (Number(fields["temperature"]) is { } temperature) preset = preset with { Temperature = Math.Clamp(temperature, 0, 2) };
        if (Number(fields["top_p"]) is { } topP) preset = preset with { TopP = Math.Clamp(topP, 0, 1) };
        if (Number(fields["top_k"]) is { } topK) preset = preset with { TopK = (int)Math.Clamp(topK, 0, 200) };
        if (Number(fields["min_p"]) is { } minP) preset = preset with { MinP = Math.Clamp(minP, 0, 1) };
        if (Number(fields["repeat_penalty"]) is { } repeat) preset = preset with { RepeatPenalty = Math.Clamp(repeat, 0, 2) };
        if (Text(fields["thinking"]) is { } thinking && thinking is THINK_MODEL or THINK_ON or THINK_OFF) preset = preset with { Thinking = thinking };
        if (Text(fields["offload"]) is { } offload && Array.IndexOf(LlamaCppServerArgs.Offloads, offload) >= 0) preset = preset with { Offload = offload };
        if (Text(fields["extra"]) is { } extra) preset = preset with { Extra = extra.Trim() };
        return preset;
    }
}
