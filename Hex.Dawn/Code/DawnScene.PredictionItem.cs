using Hex.Core;
using Hex.Shell;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Edit prediction's commands, its item in the status bar and the notice the first time it's switched on. The item is
// there for code files while No AI is off: on, off, off for the folder, the language or the file, waiting, or what went
// wrong in a few words, with the server, the model and how long the last one took in its tooltip, and the switches and
// the settings when it's pressed. Its text is written again only when what it says changes.
internal sealed partial class DawnScene
{
    private const string PREDICTION_MENU = "Edit/comment/prediction";

    private StatusItem predictionItem = null!;
    private Menu predictionMenu = null!;
    private bool activePredictionCommandsAdded;

    // What the item was last written from
    private PredictionItemState predictionItemState = PredictionItemState.Hidden;
    private PredictionStatus predictionItemStatus;
    private ICodeHighlighter? predictionItemLanguage;
    private PredictionTiming predictionItemTiming;
    private string? predictionItemStatusText;
    private bool predictionItemWritten;

    // Whether the file on screen may be sent, worked out again when the file, the settings or the patterns change
    private HexDocument? predictionAllowedFor;
    private HexSettings? predictionAllowedOf;
    private string[]? predictionAllowedPatterns;
    private bool predictionAllowedNow;

    // Whether the language on screen is one predictions are off for, worked out again when it or the list changes
    private ICodeHighlighter? predictionLanguageFor;
    private string[]? predictionLanguagesOf;
    private bool predictionLanguageOffNow, predictionLanguageKnown;

    // When the request in flight started being waited for, the item says so after a moment
    private long predictionWaitingSince = -1;

    // How many times the first-time notice was given this run, for the self test
    private int predictionNotices;

    // Called every tick, for a self test to time the editor's updates; null otherwise
    private Action? predictionTickProbe;

    // The server, model and failure the notice about a missing model or server was last given for
    private string? predictionFailureNoticed;

    /// <summary>The status bar's item, beside the language's.</summary>
    private void BuildPredictionItem()
    {
        predictionItem = statusBar.Add(string.Empty, right: true, pressed: ShowPredictionMenu, tooltip: string.Empty);
        predictionItem.IsVisible = () => predictionItemState != PredictionItemState.Hidden;
    }

    /// <summary>The commands, in a menu of their own under Edit and in the palette, their keys rebindable.</summary>
    private void AddPredictionCommands()
    {
        predictionMenu = commands.DeclareSubmenu("Edit/comment", "prediction", Said("prediction.menu"), "sparkle");
        var toggle = commands.Add(PREDICTION_MENU, Said("prediction.toggle"), TogglePredictions, icon: "sparkle");
        toggle.IsChecked = () => settings.EditPredictions;
        commands.Add(PREDICTION_MENU, Said("prediction.settings"), () => OpenSettings(EditorSettingsCatalog.AI), icon: "settings-gear");
    }

    private MenuItem? predictNowCommand;

    private void AddActivePredictionCommands()
    {
        if (activePredictionCommandsAdded)
            return;
        activePredictionCommandsAdded = true;
        bool InCode() => settings.EditPredictions && !LayoutTools && IsPredictedCode(document);
        bool Asking() => InCode() && PredictionSettings.On(settings) && PredictionSettings.Configured(settings);

        void Add(MenuItem item, Func<bool> enabled)
        {
            item.IsEnabled = enabled;
            item.IsVisible = () => settings.EditPredictions;
            predictionMenu.Add(item);
        }

        Add(commands.Add(PREDICTION_MENU, Said("prediction.toggle_language"), TogglePredictionsForLanguage, icon: "symbol-misc"), InCode);
        predictNowCommand = commands.Add(PREDICTION_MENU, Said("prediction.predict_now"), PredictNow, "Ctrl+Alt+.", "sparkle", UIKeyUse.BoundWhileTyping);
        Add(predictNowCommand, Asking);
        BindShared("Ctrl+Alt+.", whileTyping: true, predictNowCommand);
        Add(commands.Add(PREDICTION_MENU, Said("prediction.accept"), () => code.AcceptPrediction(), "Alt+L", "check", UIKeyUse.BoundWhileTyping), () => InCode() && code.HasPrediction);
        Add(commands.Add(PREDICTION_MENU, Said("prediction.accept_word"), () => code.AcceptPredictionWord(), icon: "arrow-right"), () => InCode() && code.PredictionIsGhost);
        Add(commands.Add(PREDICTION_MENU, Said("prediction.dismiss"), () => code.ClearPrediction(), icon: "close"), () => InCode() && code.HasPrediction);
        ApplyKeyLayer(force: true);
    }

    /// <summary>Edit predictions switched on or off, in the user's settings.</summary>
    private void TogglePredictions()
    {
        if (HexAi.Off)
            return;
        settings.EditPredictions = !settings.EditPredictions;
        if (!settings.EditPredictions && predictionSession is not null)
            StopPredictions();
        SaveSettings();
        SettingsPage?.Refresh("edit_predictions");
        Say(Said(settings.EditPredictions ? "prediction.on_now" : "prediction.off_now"));
    }

    /// <summary>Edit predictions switched off, or on again, for the language of the file on screen.</summary>
    private void TogglePredictionsForLanguage()
    {
        if (HexAi.Off || LayoutTools || !IsPredictedCode(document))
            return;

        string language = CodeLanguages.LanguageIdOf(code.Highlighter);
        bool off = PredictionSettings.LanguageOff(settings, language);
        settings.EditPredictionsDisabledLanguages = off
            ? [.. settings.EditPredictionsDisabledLanguages.Where(each => !string.Equals(each, language, StringComparison.OrdinalIgnoreCase))]
            : [.. settings.EditPredictionsDisabledLanguages, language];
        if (!off)
        {
            code.ClearPrediction();
            predictionSession?.Engine.Forget(document);
        }
        SaveSettings();
        SettingsPage?.Refresh("edit_predictions_disabled_languages");
        string name = code.Highlighter?.Name ?? language;
        Say(Said(off ? "prediction.language_on" : "prediction.language_off", ("language", name)));
    }

    /// <summary>A prediction asked for now, whatever was dismissed or failed before.</summary>
    private void PredictNow()
    {
        if (!PredictionSettings.On(settings) || LayoutTools || !IsPredictedCode(document))
            return;
        if (predictionSession is null)
            StartPredictions();
        predictions.ShortcutOnly = settings.PredictionShortcutOnly;
        predictions.Edited(document, code.Document.Version);
        predictions.PredictNow(document);
    }

    /// <summary>The item pressed: the switches, Predict Now and the settings.</summary>
    private void ShowPredictionMenu()
    {
        if (HexAi.Off)
            return;

        var choices = new List<(string Text, Action Picked)>
        {
            (Said(settings.EditPredictions ? "prediction.menu_off" : "prediction.menu_on"), TogglePredictions),
        };
        if (!LayoutTools && IsPredictedCode(document))
        {
            string language = code.Highlighter?.Name ?? CodeLanguages.LanguageIdOf(code.Highlighter);
            bool off = PredictionSettings.LanguageOff(settings, CodeLanguages.LanguageIdOf(code.Highlighter));
            choices.Add((Said(off ? "prediction.menu_language_on" : "prediction.menu_language_off", ("language", language)), TogglePredictionsForLanguage));
            if (PredictionSettings.On(settings) && PredictionSettings.Configured(settings))
                choices.Add((Said("prediction.predict_now"), PredictNow));
        }
        choices.Add((Said("prediction.settings"), () => OpenSettings(EditorSettingsCatalog.AI)));
        ChooseFrom(Said("prediction.menu"), Said("prediction.menu_text"), choices);
    }

    /// <summary>
    /// The first time predictions are on, by the setting or the command, a notice of what is sent and where. Once: it's
    /// remembered in the user's settings.
    /// </summary>
    private void SayPredictionsOnce()
    {
        if (settings.EditPredictionsNoticeDone || !PredictionSettings.On(settings))
            return;

        settings.EditPredictionsNoticeDone = true;
        predictionNotices++;
        Notify(Said("prediction.notice", ("host", PredictionHost())));
        SaveSettings();
    }

    // Where predictions come from in a word: the device over Hex Link, or the server's host
    private string PredictionHost()
    {
        if (settings.EditPredictionsProvider == PredictionProviders.LINK)
            return HexPredictionRelay.RemoteName ?? Said("prediction.a_device");
        return PredictionUrl() is { } url ? url.Authority : settings.EditPredictionsApiUrl;
    }

    // The server's URL as the provider makes it, null when it isn't one
    private Uri? PredictionUrl() => settings.EditPredictionsProvider == PredictionProviders.OPENAI
        ? PredictionProviders.OpenAiUrl(settings.EditPredictionsApiUrl)
        : PredictionProviders.OllamaUrl(settings.EditPredictionsApiUrl);

    /// <summary>Every tick, what the item says, written only when it changes. Nothing is made while it doesn't.</summary>
    private void UpdatePredictionItem()
    {
        predictionTickProbe?.Invoke();
        bool code = !LayoutTools && IsPredictedCode(document);
        PredictionStatus status = predictions.Status;
        if (status == PredictionStatus.Waiting)
        {
            if (predictionWaitingSince < 0)
                predictionWaitingSince = Environment.TickCount64;
        }
        else
            predictionWaitingSince = -1;
        bool waitingLong = predictionWaitingSince >= 0 && Environment.TickCount64 - predictionWaitingSince > HexPredictionGlue.WAITING_AFTER_MS;

        PredictionItemState state = HexPredictionGlue.ItemState(HexAi.Off, code, settings.EditPredictions, PredictionSettings.Configured(settings),
            settings.EditPredictionsWorkspace, code && LanguageOffOnScreen(), !code || AllowedOnScreen(), status, waitingLong);

        if (predictionItemWritten && state == predictionItemState && status == predictionItemStatus
            && ReferenceEquals(predictionItemLanguage, document.Editor.Highlighter) && predictionItemTiming == predictions.LastTiming
            && ReferenceEquals(predictionItemStatusText, predictions.StatusText))
            return;

        predictionItemWritten = true;
        predictionItemState = state;
        predictionItemStatus = status;
        predictionItemLanguage = document.Editor.Highlighter;
        predictionItemTiming = predictions.LastTiming;
        predictionItemStatusText = predictions.StatusText;
        if (state == PredictionItemState.Hidden)
            return;

        if (state == PredictionItemState.On)
            predictions.Warm();

        // A server or model that isn't there is said once in a notice, not only in the item's tooltip; again only when
        // the server, the model or the way it failed changes
        if (state == PredictionItemState.Failed && status is PredictionStatus.NoSuchModel or PredictionStatus.Unreachable && predictions.StatusText.Length > 0)
        {
            string failure = $"{status}|{settings.EditPredictionsApiUrl}|{settings.EditPredictionsModel}";
            if (failure != predictionFailureNoticed)
            {
                predictionFailureNoticed = failure;
                predictionNotices++;
                Notify(Said("prediction.notice_failed", ("why", predictions.StatusText)), error: true);
            }
        }

        string icon = state switch
        {
            PredictionItemState.On => "sparkle",
            PredictionItemState.Waiting => "loading~spin",
            PredictionItemState.Failed => "warning",
            _ => "circle-slash",
        };
        string language = document.Editor.Highlighter?.Name ?? Said("status_bar.kind_plain_text");
        predictionItem.Text = $"$({icon}) {Said(HexPredictionGlue.ItemKey(state, status), ("language", language))}";
        predictionItem.Tooltip = PredictionTooltip(state);
    }

    // Where predictions come from, what they're made with, how long the last took, and whether the code leaves the machine
    private string PredictionTooltip(PredictionItemState state)
    {
        var lines = new List<string>();
        if (state == PredictionItemState.Failed && predictions.StatusText.Length > 0)
            lines.Add(predictions.StatusText);

        if (settings.EditPredictionsProvider == PredictionProviders.LINK)
            lines.Add(Said("prediction.tip_device", ("device", HexPredictionRelay.RemoteName ?? Said("prediction.a_device"))));
        else
        {
            Uri? url = PredictionUrl();
            lines.Add(Said("prediction.tip_from", ("host", url?.Authority ?? settings.EditPredictionsApiUrl), ("model", settings.EditPredictionsModel)));
            if (url is not null && !PredictionProviders.IsLocal(url))
                lines.Add(Said("prediction.tip_remote", ("host", url.Host)));
        }

        PredictionTiming timing = predictions.LastTiming;
        if (timing.TotalMs > 0)
        {
            lines.Add(timing.OutputTokens == 0 && timing.PromptTokens == 0
                ? Said("prediction.tip_cache", ("total", timing.TotalMs.ToString("0")))
                : Said("prediction.tip_latency", ("first", timing.FirstMs.ToString("0")), ("total", timing.TotalMs.ToString("0")),
                    ("prompt", timing.PromptTokens.ToString()), ("output", timing.OutputTokens.ToString())));
        }
        lines.Add(Said("prediction.tip_press"));
        return string.Join('\n', lines);
    }

    // Whether the file on screen may be sent, worked out once for it and its settings
    private bool AllowedOnScreen()
    {
        if (predictionAllowedFor != document || !ReferenceEquals(predictionAllowedOf, settings) || !ReferenceEquals(predictionAllowedPatterns, settings.EditPredictionsExclude))
        {
            predictionAllowedFor = document;
            predictionAllowedOf = settings;
            predictionAllowedPatterns = settings.EditPredictionsExclude;
            predictionAllowedNow = document.Path is { } path && PredictionAllowed(path);
        }
        return predictionAllowedNow;
    }

    // Whether the language on screen is one predictions are off for, worked out once for it and the list
    private bool LanguageOffOnScreen()
    {
        ICodeHighlighter? language = document.Editor.Highlighter;
        if (!predictionLanguageKnown || !ReferenceEquals(predictionLanguageFor, language) || !ReferenceEquals(predictionLanguagesOf, settings.EditPredictionsDisabledLanguages))
        {
            predictionLanguageKnown = true;
            predictionLanguageFor = language;
            predictionLanguagesOf = settings.EditPredictionsDisabledLanguages;
            predictionLanguageOffNow = PredictionSettings.LanguageOff(settings, CodeLanguages.LanguageIdOf(language));
        }
        return predictionLanguageOffNow;
    }
}
