namespace Hex.Core;

/// <summary>The pages of the Local AI window, in the order of its left nav.</summary>
internal enum LocalAiPage { Overview, Setups, Models, Presets, Predictions, Engine, Privacy, Diagnostics, Glossary }

/// <summary>The steps of the guided first run (docs/local-ai-setup-plan.md, 3.2).</summary>
internal enum FirstRunStep { Hardware, Engine, Models, Test }

/// <summary>What the guided first run knows now, from which each step's "can go on" follows.</summary>
internal sealed record FirstRunState(
    bool HardwareKnown,
    bool EngineReady,
    string? PredictModel, string? ChatModel,
    bool PredictReady, bool ChatReady,
    bool Measured);

/// <summary>The models the first run suggests for a machine: one that does both, or one per role.</summary>
internal sealed record LocalAiRecommendation(LlamaCppModel Predict, LlamaCppModel Chat)
{
    /// <summary>Whether one model does both roles.</summary>
    public bool OneModel => Predict.Id == Chat.Id;
}

/// <summary>The pure parts of the Local AI window: which page a name is, the first run's steps and what to suggest for a machine.</summary>
internal static class LocalAiSetupFlow
{
    /// <summary>The page a name says ("models", "Presets"...), null or an unknown name is the overview.</summary>
    public static LocalAiPage ParsePage(string? name) =>
        name is { Length: > 0 } && Enum.TryParse(name.Trim(), true, out LocalAiPage page) && Enum.IsDefined(page) ? page : LocalAiPage.Overview;

    /// <summary>Whether the guided first run opens by itself: no model is assigned to either role.</summary>
    public static bool NeedsFirstRun(HexSettings settings) =>
        settings.LocalAiPredictModel.Trim().Length == 0 && settings.LocalAiChatModel.Trim().Length == 0;

    /// <summary>The step after this one, the last stays.</summary>
    public static FirstRunStep Next(FirstRunStep step) => step == FirstRunStep.Test ? step : step + 1;

    /// <summary>The step before this one, the first stays.</summary>
    public static FirstRunStep Previous(FirstRunStep step) => step == FirstRunStep.Hardware ? step : step - 1;

    /// <summary>Whether Next is allowed on a step: the hardware looked at, the engine ready, a model for predictions that's on the disk.</summary>
    public static bool CanAdvance(FirstRunStep step, FirstRunState state) => step switch
    {
        FirstRunStep.Hardware => state.HardwareKnown,
        FirstRunStep.Engine => state.EngineReady,
        FirstRunStep.Models => !string.IsNullOrEmpty(state.PredictModel) && state.PredictReady && (string.IsNullOrEmpty(state.ChatModel) || state.ChatReady),
        _ => false,
    };

    /// <summary>The furthest step the state lets the person reach by its tab.</summary>
    public static FirstRunStep Reached(FirstRunState state)
    {
        FirstRunStep step = FirstRunStep.Hardware;
        while (step < FirstRunStep.Test && CanAdvance(step, state))
            step++;
        return step;
    }

    /// <summary>Whether the flow is done: the model answered and was timed.</summary>
    public static bool Finished(FirstRunState state) => Reached(state) == FirstRunStep.Test && state.Measured;

    /// <summary>
    /// What to suggest for a machine: Qwen3.5 9B for both roles when it runs on the card, else Zeta for predictions plus the
    /// best chat model that fits, else the smallest chat model for both.
    /// </summary>
    public static LocalAiRecommendation Recommend(LlamaCppPlatform platform, IReadOnlyList<LlamaCppModel>? catalog = null)
    {
        catalog ??= LlamaCppModels.All;
        bool Runs(LlamaCppModel model) => LlamaCppModels.Fit(model, platform) is LlamaCppFit.Gpu or LlamaCppFit.Experts;

        LlamaCppModel? both = catalog.FirstOrDefault(model => model.Id == "qwen3.5-9b" && model.Chats && Runs(model));
        if (both is not null)
            return new LocalAiRecommendation(both, both);

        LlamaCppModel? zeta = catalog.FirstOrDefault(model => !model.Chats && Runs(model));
        LlamaCppModel? chat = catalog.Where(model => model.Chats && Runs(model)).OrderByDescending(model => model.Size).FirstOrDefault();
        if (zeta is not null && chat is not null)
            return new LocalAiRecommendation(zeta, chat);

        LlamaCppModel smallest = chat ?? catalog.Where(model => model.Chats).OrderBy(model => model.Size).First();
        return new LocalAiRecommendation(smallest, smallest);
    }

    /// <summary>Whether applying a changed preset has to restart the server: its context, where the weights go or its extra arguments changed.</summary>
    public static bool NeedsRestart(LocalModelPreset before, LocalModelPreset after) =>
        before.Context != after.Context || before.Offload != after.Offload || before.Extra.Trim() != after.Extra.Trim();

    /// <summary>
    /// Whether the server has to start again for a changed preset of one model: a field it was started with changed (the router
    /// reads its models preset only as it starts) and the model is one of the roles the router preset holds. A sampling-only
    /// change never restarts (sampling is asked per request), and a model in no role is next written into the preset when needed.
    /// </summary>
    public static bool RestartsServer(string modelId, string? predictId, string? chatId, LocalModelPreset before, LocalModelPreset after) =>
        NeedsRestart(before, after) && modelId.Length > 0
        && (string.Equals(modelId, predictId, StringComparison.OrdinalIgnoreCase) || string.Equals(modelId, chatId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the draft of a preset page was made from other saved presets than are saved now.</summary>
    public static bool DraftStale(string? draftSource, string saved) => draftSource is not null && !string.Equals(draftSource, saved, StringComparison.Ordinal);

    /// <summary>Whether the "(changed from ...)" marker shows for a value typed into a box: something typed that isn't what the profile says.</summary>
    public static bool ChangedFromProfile(string typed, string fromProfile)
    {
        typed = typed.Trim();
        if (typed.Length == 0)
            return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(typed, System.Globalization.NumberStyles.Float, culture, out double a) && double.TryParse(fromProfile.Trim(), System.Globalization.NumberStyles.Float, culture, out double b))
            return a != b;
        return !string.Equals(typed, fromProfile.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The preset with a field typed in a box: empty or unreadable gives the default's value, numbers are kept in their range.</summary>
    public static LocalModelPreset WithField(LocalModelPreset preset, LocalModelPreset defaults, string field, string typed)
    {
        typed = typed.Trim();
        bool empty = typed.Length == 0;
        bool Number(out double value) => double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        switch (field)
        {
            case "context": return preset with { Context = !empty && int.TryParse(typed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int tokens) ? Math.Clamp(tokens, 512, 1_048_576) : defaults.Context };
            case "temperature": return preset with { Temperature = !empty && Number(out double t) ? Math.Clamp(t, 0, 2) : defaults.Temperature };
            case "top_p": return preset with { TopP = !empty && Number(out double p) ? Math.Clamp(p, 0, 1) : defaults.TopP };
            case "top_k": return preset with { TopK = !empty && int.TryParse(typed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int k) ? Math.Clamp(k, 0, 1000) : defaults.TopK };
            case "min_p": return preset with { MinP = !empty && Number(out double m) ? Math.Clamp(m, 0, 1) : defaults.MinP };
            case "repeat_penalty": return preset with { RepeatPenalty = !empty && Number(out double r) ? Math.Clamp(r, 0, 2) : defaults.RepeatPenalty };
            case "extra": return preset with { Extra = typed };
            default: return preset;
        }
    }

    /// <summary>Whether a llama.cpp build of this tag is the one at the server's path (builds are unpacked into a folder named by the tag).</summary>
    public static bool BuildInstalled(string? serverPath, string tag, string? backend = null)
    {
        if (string.IsNullOrEmpty(serverPath) || string.IsNullOrEmpty(tag))
            return false;
        foreach (string part in serverPath.Split('\\', '/'))
        {
            if (!string.IsNullOrEmpty(backend))
            {
                // A folder named by the tag alone (D:\llama.cpp\b12651\llama-server.exe) is that build too
                if (part.Equals(tag + "-" + backend, StringComparison.OrdinalIgnoreCase) || part.Equals(tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (part.Equals(tag, StringComparison.OrdinalIgnoreCase) || part.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Where the folder dialog starts for adding models: the models folder when it's there, else the home folder.</summary>
    public static string ModelsDialogStart(string modelsFolder, string home) =>
        modelsFolder.Length > 0 && Directory.Exists(modelsFolder) ? modelsFolder : home;

    /// <summary>Whether a library model is the one suggested for the machine (a model on the disk that the first run would otherwise offer to download).</summary>
    public static bool IsRecommended(LocalAiRecommendation recommendation, string? catalogId) =>
        !string.IsNullOrEmpty(catalogId) && (recommendation.Predict.Id == catalogId || recommendation.Chat.Id == catalogId);

    /// <summary>The suggested models that aren't on the disk yet, which are the ones offered as downloads; one found already is shown once, as found.</summary>
    public static IReadOnlyList<LlamaCppModel> RecommendedToDownload(LocalAiRecommendation recommendation, Func<LlamaCppModel, bool> onDisk) =>
        [.. (recommendation.OneModel ? [recommendation.Predict] : new[] { recommendation.Predict, recommendation.Chat }).Where(model => !onDisk(model))];

    /// <summary>A key for what a window shows, equal when nothing it shows has moved (parts are compared by their text).</summary>
    public static string Signature(params object?[] parts) =>
        string.Join("\u001f", parts.Select(part => part switch
        {
            null => string.Empty,
            System.Collections.IEnumerable list and not string => string.Join(",", list.Cast<object?>().Select(each => each?.ToString())),
            _ => Convert.ToString(part, System.Globalization.CultureInfo.InvariantCulture),
        }));

    /// <summary>The lines of the "Local AI is ready" confirmation, as string keys: how predictions run, the chat model and Link Chat.</summary>
    public static IReadOnlyList<string> ReadyKeys(bool predictions, string invocation, bool hasChatModel, bool linkChat) =>
    [
        !predictions ? "local_ai.ready_predictions_off" : invocation == "shortcut" ? "local_ai.ready_predictions_request" : "local_ai.ready_predictions_typing",
        hasChatModel ? "local_ai.ready_chat_on" : "local_ai.ready_chat_off",
        linkChat ? "local_ai.ready_link_on" : "local_ai.ready_link_off",
    ];

    /// <summary>The string key of the build button: download when none is installed, reinstall for the same build, update for a different one.</summary>
    public static string BuildButtonKey(bool foundAny, bool sameBuild) =>
        !foundAny ? "llamacpp.build_download" : sameBuild ? "local_ai.build_reinstall" : "llamacpp.build_update";

    /// <summary>The string key of the nav's setup button: "Set Up…" until something was set up, then "Set Up Again".</summary>
    public static string SetUpKey(bool setUpBefore) => setUpBefore ? "local_ai.set_up_again" : "local_ai.set_up_first";

    /// <summary>A file's size in words: MB under a gigabyte (never "0.0 GB"), else GB with a decimal.</summary>
    public static string SizeText(long bytes)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes >= 950_000_000)
            return (bytes / 1e9).ToString("0.0", culture) + " GB";
        return Math.Max(1, (long)Math.Round(bytes / 1e6)).ToString(culture) + " MB";
    }

    /// <summary>The string key that says what applying a preset did.</summary>
    public static string AppliedKey(bool restarted, bool needsRestart) =>
        restarted ? "local_ai.applied_restart" : needsRestart ? "local_ai.applied_saved" : "local_ai.applied_now";

    /// <summary>
    /// How many of these items (widths in pixels) fit a row of <paramref name="available"/> when the rest is cut for a "+N" chip of
    /// <paramref name="reserve"/>: all of them when they fit as they are, at least one.
    /// </summary>
    public static int FitCount(IReadOnlyList<float> widths, float available, float gap, float reserve)
    {
        float all = widths.Sum() + gap * Math.Max(0, widths.Count - 1);
        if (all <= available)
            return widths.Count;
        float used = 0;
        int fit = 0;
        foreach (float width in widths)
        {
            float next = used + width + gap;
            if (next + reserve > available)
                break;
            used = next;
            fit++;
        }
        return Math.Max(1, Math.Min(fit, widths.Count - 1));
    }

    /// <summary>Where a row of buttons (widths in pixels) breaks into lines of at most <paramref name="available"/>: the count of each line, a button wider than a line alone on one.</summary>
    public static IReadOnlyList<int> RowBreaks(IReadOnlyList<float> widths, float available, float gap)
    {
        var lines = new List<int>();
        float used = 0;
        int count = 0;
        foreach (float width in widths)
        {
            float needed = count == 0 ? width : used + gap + width;
            if (count > 0 && needed > available)
            {
                lines.Add(count);
                used = width;
                count = 1;
            }
            else
            {
                used = needed;
                count++;
            }
        }
        if (count > 0)
            lines.Add(count);
        return lines;
    }

    /// <summary>The one sentence that says what the suggestion trades off.</summary>
    public static string TradeOffKey(LocalAiRecommendation recommendation) =>
        recommendation.OneModel ? "local_ai.tradeoff_one" : "local_ai.tradeoff_two";
}
