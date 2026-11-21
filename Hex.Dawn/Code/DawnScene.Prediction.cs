using Hex.Core;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Edit prediction in the editor (docs/edit-prediction-plan.md, 4): a model asked what comes next as the code is typed,
// what it says shown as ghost text or a small diff, Tab to take it. The engine (Hex.Core/Prediction) does the waiting,
// the asking and the reading off the editor's thread; this tells it when the file on screen was typed in, hands it the
// file's lines when the typing has paused and every gate is open, and gives the editor what comes back, if it is still
// for the text and the caret as they are. On the editor's thread nothing is made for a keystroke: two fields and a call.
internal sealed partial class DawnScene
{
    private readonly PredictionEngine predictions = new(TimeProvider.System);

    // The options last made and what they were made from, so the engine's question every tick of the debounce makes none
    private PredictionOptions? predictionOptions;
    private HexSettings? predictionOptionsOf;
    private string? predictionKeyOf;

    // What was last asked for: the document, its version, the caret, its lines (references to the document's strings)
    // and its path as sent. A prediction for anything else is let go of
    private HexDocument? predictionAsked;
    private int predictionAskedVersion;
    private UITextPosition predictionAskedCaret;
    private string[]? predictionAskedLines;
    private string predictionAskedPath = string.Empty;

    // The prediction the editor is showing, and what it was shown for
    private HexDocument? predictionShownIn;
    private int predictionShownVersion;
    private UITextPosition predictionShownCaret;
    private bool predictionShownGhost;
    private string[]? predictionShownLines;
    private string predictionShownPath = string.Empty;

    // Set by the editor when the character just typed was the ghost text's next, read by the edit that follows it
    private bool predictionTypedThrough;

    // A prediction taken: its edit going into the history off the editor's thread, the next asked for once it's in
    private Task? predictionAccepting;
    private HexDocument? predictionAcceptedIn;

    // The code files most recently typed in, newest first, with their caret's line then, for the excerpts sent along
    private readonly (HexDocument? Document, int Line)[] predictionRecent = new (HexDocument?, int)[3];

    // Which files may be sent, made again when the patterns or the folders change
    private PredictionPrivacy? predictionPrivacy;
    private HexSettings? predictionPrivacyOf;
    private string[]? predictionPrivacyPatterns;
    private List<string>? predictionPrivacyRoots;

    // For the self test: predictions shown and taken
    private int predictionsShown, predictionsTaken;

    /// <summary>The engine is told where the settings are, and Hex Link's relay with it. Once, as the editor is made.</summary>
    private void BuildPredictions()
    {
        predictions.Options = PredictionOptionsNow;
        predictions.Snapshot = PredictionSnapshotOf;
        HexPredictionRelay.Configure(() => MakePredictionOptions(settings), () => !HexAi.Off && PredictionSettings.Share(settings));
        documentClosed += PredictionClosed;
        AddPredictionCommands();
    }

    /// <summary>An editor told what to call when its prediction ends or is typed through. Once, as its document is made.</summary>
    private void PreparePrediction(HexDocument prepared)
    {
        CodeEditor editor = prepared.Editor;
        editor.OnPredictionEnded = taken => PredictionEnded(prepared, taken);
        editor.OnPredictionTypedThrough = () => predictionTypedThrough = true;
    }

    /// <summary>
    /// A document typed into (CodeChanged): the engine told its version and whether the edit typed through the ghost text,
    /// which keeps it. Allocates nothing.
    /// </summary>
    private void PredictionEdited(HexDocument changed)
    {
        bool through = predictionTypedThrough;
        predictionTypedThrough = false;
        if (changed != document || !PredictionSettings.On(settings))
            return;

        // An edit the prediction is still showing after was its own, typed through or a word of it taken (Ctrl+Right):
        // whatever ended it would have said so first
        UITextDocument text = changed.Editor.Document;
        through = predictionShownIn == changed && (through || changed.Editor.HasPrediction);
        if (through)
        {
            predictionShownVersion = text.Version;
            predictionShownCaret = text.Caret;
        }

        RememberEdited(changed, text.Caret.Line);
        predictions.Edited(changed, text.Version, through);
    }

    /// <summary>Every tick: the engine asked, a finished prediction given to the editor, the status item brought up to date.</summary>
    private void UpdatePredictions()
    {
        if (HexAi.Off)
            return;

        SayPredictionsOnce();

        // A prediction taken is in the history now, the next is asked for at once
        if (predictionAccepting is { IsCompleted: true })
        {
            predictionAccepting = null;
            if (predictionAcceptedIn is { } accepted && accepted == document)
                predictions.Accepted(accepted);
            predictionAcceptedIn = null;
        }

        predictions.Tick();
        if (predictions.Take() is { } result)
            ShowPrediction(result);

        UpdatePredictionItem();
    }

    // What the settings say, made into options only when one of them changed
    private PredictionOptions? PredictionOptionsNow()
    {
        HexSettings now = settings;
        if (!PredictionSettings.On(now) || !PredictionSettings.Configured(now))
            return null;

        if (predictionOptions is not { } made || !ReferenceEquals(predictionOptionsOf, now) || !ReferenceEquals(predictionKeyOf, now.EditPredictionsApiKey)
            || made.Provider != now.EditPredictionsProvider || made.ApiUrl != now.EditPredictionsApiUrl || made.Model != now.EditPredictionsModel
            || made.Format != now.EditPredictionsPromptFormat || made.MaxTokens != now.EditPredictionsMaxTokens || made.DebounceMs != now.EditPredictionsDebounce)
        {
            predictionOptions = MakePredictionOptions(now);
            predictionOptionsOf = now;
            predictionKeyOf = now.EditPredictionsApiKey;
        }
        return predictionOptions;
    }

    // The options the settings make, null when predictions are off or no provider is set up. Read from any thread
    private static PredictionOptions? MakePredictionOptions(HexSettings from) =>
        PredictionSettings.On(from) && PredictionSettings.Configured(from)
            ? new PredictionOptions(true, from.EditPredictionsProvider, from.EditPredictionsApiUrl, PredictionSettings.ApiKey(from),
                from.EditPredictionsModel, from.EditPredictionsPromptFormat, from.EditPredictionsMaxTokens, from.EditPredictionsDebounce)
            : null;

    /// <summary>
    /// The document as it is when the typing has paused, null when a gate is shut (4.1). Its lines are the document's own
    /// strings in a new array, the caret, the version, its path inside its folder with forward slashes, its language and
    /// the lines round the last caret of the two other code files typed in most recently.
    /// </summary>
    private PredictionSnapshot? PredictionSnapshotOf(object asked)
    {
        if (asked is not HexDocument of || PredictionGateOf(of) != PredictionGate.Open || of.Path is not { } path)
            return null;

        UITextDocument text = of.Editor.Document;
        var lines = new string[text.LineCount];
        long size = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = text.LineAt(i);
            size += lines[i].Length + 1;
        }
        if (size > PredictionPrivacy.MOST_BYTES)
            return null;

        List<string> roots = WorkspaceRoots();
        string sent = HexPredictionGlue.Sent(roots, path);
        predictionAsked = of;
        predictionAskedVersion = text.Version;
        predictionAskedCaret = text.Caret;
        predictionAskedLines = lines;
        predictionAskedPath = sent;

        return new PredictionSnapshot(of, text.Version, sent, CodeLanguages.LanguageIdOf(of.Editor.Highlighter), lines,
            text.Caret.Line, text.Caret.Column, RelatedExcerpts(of, roots));
    }

    /// <summary>The first gate of 4.1 that's shut for a document, or Open.</summary>
    private PredictionGate PredictionGateOf(HexDocument of)
    {
        CodeEditor editor = of.Editor;
        UITextDocument text = editor.Document;
        bool code = IsPredictedCode(of);
        return HexPredictionGlue.Gate(new PredictionFacts(
            OnScreen: of == document && !LayoutTools,
            Code: code,
            Focused: editor.IsFocused,
            ReadOnly: editor.ReadOnly,
            Selection: text.HasSelection,
            ExtraCarets: text.HasExtraCarets,
            PopupOpen: editor.CompletionOpen || editor.Signature is not null,
            On: PredictionSettings.On(settings),
            LanguageOff: code && PredictionSettings.LanguageOff(settings, CodeLanguages.LanguageIdOf(editor.Highlighter)),
            Allowed: code && PredictionAllowed(of.Path!)));
    }

    // A file of code with a path, not a layout, a picture or a diff
    private static bool IsPredictedCode(HexDocument of) => of.IsText && of.Picture is null && of.DiffOf is null && of.Path is not null;

    // Whether a file may be sent: inside the folders open and matching none of the patterns. Its size is checked as it's taken
    private bool PredictionAllowed(string path)
    {
        string[] patterns = settings.EditPredictionsExclude;
        List<string> roots = WorkspaceRoots();
        if (predictionPrivacy is null || !ReferenceEquals(predictionPrivacyOf, settings) || !ReferenceEquals(predictionPrivacyPatterns, patterns)
            || predictionPrivacyRoots is null || !predictionPrivacyRoots.SequenceEqual(roots, StringComparer.Ordinal))
        {
            predictionPrivacy = new PredictionPrivacy(PredictionSettings.Exclude(settings), roots);
            predictionPrivacyOf = settings;
            predictionPrivacyPatterns = patterns;
            predictionPrivacyRoots = roots;
        }
        return predictionPrivacy.Allowed(path, 0);
    }

    // The lines round the last caret of the two other code files typed in most recently, taken now as references and joined
    // off the editor's thread when the prompt is built. Never a file the privacy rules keep back
    private Func<IReadOnlyList<PredictionExcerpt>>? RelatedExcerpts(HexDocument of, List<string> roots)
    {
        List<(string Path, string[] Lines)>? taken = null;
        foreach (var (other, line) in predictionRecent)
        {
            if (other is null || other == of || !documents.Contains(other) || !IsPredictedCode(other) || !PredictionAllowed(other.Path!)
                || PredictionSettings.LanguageOff(settings, CodeLanguages.LanguageIdOf(other.Editor.Highlighter)))
                continue;
            UITextDocument text = other.Editor.Document;
            var lines = new string[text.LineCount];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = text.LineAt(i);
            (taken ??= []).Add((HexPredictionGlue.Sent(roots, other.Path!), HexPredictionGlue.Excerpt(lines, line)));
            if (taken.Count == 2)
                break;
        }

        if (taken is null)
            return null;
        return () => [.. taken.Select(excerpt => new PredictionExcerpt(excerpt.Path, string.Join('\n', excerpt.Lines)))];
    }

    // A file typed in put first of the recent ones, the rest moved down, nothing made
    private void RememberEdited(HexDocument edited, int line)
    {
        int at = predictionRecent.Length - 1;
        for (int i = 0; i < predictionRecent.Length; i++)
        {
            if (predictionRecent[i].Document == edited)
            {
                at = i;
                break;
            }
        }
        for (int i = at; i > 0; i--)
            predictionRecent[i] = predictionRecent[i - 1];
        predictionRecent[0] = (edited, line);
    }

    /// <summary>
    /// A finished prediction given to the editor, if it's for the document on screen at the version it's at and the caret
    /// hasn't moved; a caret that moved asks again where it is now.
    /// </summary>
    private void ShowPrediction(PredictionResult result)
    {
        if (result.Document is not HexDocument of || of != document || of != predictionAsked || predictionAskedLines is not { } lines)
            return;

        CodeEditor editor = of.Editor;
        UITextDocument text = editor.Document;
        if (text.Version != result.Version || text.Version != predictionAskedVersion)
            return;
        if (text.Caret != predictionAskedCaret || text.HasSelection || text.HasExtraCarets)
        {
            predictions.Edited(of, text.Version);
            return;
        }
        if (HexPredictionGlue.Edits(result.Edits, lines) is not { } edits)
            return;

        predictionShownIn = of;
        predictionShownVersion = text.Version;
        predictionShownCaret = text.Caret;
        predictionShownGhost = result.Ghost;
        predictionShownLines = lines;
        predictionShownPath = predictionAskedPath;
        editor.SetPrediction(new UICodePrediction(edits, HexPredictionGlue.CaretAfter(result.Edits, lines, result.CaretAfter)));
        if (editor.HasPrediction)
            predictionsShown++;
        else
            predictionShownIn = null;
    }

    /// <summary>
    /// The editor's prediction ended. Taken, its edit goes into the history and the next is asked for at once. Let go of
    /// with Escape (or Dismiss), nothing more is asked until something's typed. Ended by typing something else, the edit
    /// tells the engine; by the caret going elsewhere, the engine asks again where it is now; by the keyboard going
    /// elsewhere, nothing.
    /// </summary>
    private void PredictionEnded(HexDocument of, bool taken)
    {
        if (predictionShownIn != of)
            return;
        predictionShownIn = null;

        UITextDocument text = of.Editor.Document;
        if (taken)
        {
            predictionsTaken++;
            string[] before = predictionShownLines ?? [];
            var after = new string[text.LineCount];
            for (int i = 0; i < after.Length; i++)
                after[i] = text.LineAt(i);
            string path = predictionShownPath;
            PredictionHistory history = predictions.History;
            predictionAcceptedIn = of;
            predictionAccepting = Task.Run(() => history.Accepted(path, string.Join('\n', before), string.Join('\n', after)));
            return;
        }

        if (text.Version != predictionShownVersion || !of.Editor.IsFocused)
            return;
        if (predictionShownGhost && (text.Caret != predictionShownCaret || text.HasSelection || text.HasExtraCarets))
        {
            predictions.Edited(of, text.Version);
            return;
        }
        predictions.Dismissed(of);
    }

    // A document closed: what was asked for it goes, and its edits from the history
    private void PredictionClosed(HexDocument closed)
    {
        predictions.Forget(closed);
        if (closed.Path is { } path)
            predictions.History.Closed(HexPredictionGlue.Sent(WorkspaceRoots(), path));
        for (int i = 0; i < predictionRecent.Length; i++)
        {
            if (predictionRecent[i].Document == closed)
                predictionRecent[i] = default;
        }
        if (predictionShownIn == closed)
            predictionShownIn = null;
        if (predictionAsked == closed)
        {
            predictionAsked = null;
            predictionAskedLines = null;
        }
        if (predictionAcceptedIn == closed)
            predictionAcceptedIn = null;
    }
}
