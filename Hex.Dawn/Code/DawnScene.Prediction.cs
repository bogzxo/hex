using Hex.Core;
using Hex.Core.Notebooks;

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
    private sealed class PredictionSession(PredictionEngine engine)
    {
        internal PredictionEngine Engine { get; } = engine;
    }

    private readonly record struct PredictionTarget(HexDocument Document, CodeEditor Editor,
        HexNotebook? Notebook = null, NotebookCell? Cell = null)
    {
        public object Key => Cell is null ? Document : Editor;
    }

    private PredictionSession? predictionSession;
    private PredictionTarget? predictionEditingIn;
    private PredictionEngine predictions => predictionSession!.Engine;

    // The options last made and what they were made from, so the engine's question every tick of the debounce makes none
    private PredictionOptions? predictionOptions;
    private HexSettings? predictionOptionsOf;
    private string? predictionKeyOf;

    // What was last asked for: the document, its version, the caret, its lines (references to the document's strings)
    // and its path as sent. A prediction for anything else is let go of
    private PredictionTarget? predictionAsked;
    private int predictionAskedVersion;
    private UITextPosition predictionAskedCaret;
    private string[]? predictionAskedLines;
    private string predictionAskedPath = string.Empty;

    // The prediction the editor is showing, and what it was shown for
    private PredictionTarget? predictionShownIn;
    private int predictionShownVersion;
    private UITextPosition predictionShownCaret;
    private bool predictionShownGhost;
    private string[]? predictionShownLines;
    private string predictionShownPath = string.Empty;

    // A prediction taken: its edit going into the history off the editor's thread, the next asked for once it's in
    private Task? predictionAccepting;
    private PredictionTarget? predictionAcceptedIn;
    private int predictionAcceptedVersion;

    // The code files most recently typed in, newest first, with their caret's line then, for the excerpts sent along
    private (HexDocument? Document, int Line)[] predictionRecent = [];

    // Which files may be sent, made again when the patterns or the folders change
    private PredictionPrivacy? predictionPrivacy;
    private HexSettings? predictionPrivacyOf;
    private string[]? predictionPrivacyPatterns;
    private List<string>? predictionPrivacyRoots;

    // For the self test: predictions shown and taken
    private int predictionsShown, predictionsTaken;

    private void StartPredictions()
    {
        predictionSession = new(new PredictionEngine(TimeProvider.System)
        {
            ShortcutOnly = settings.PredictionShortcutOnly,
            Options = PredictionOptionsNow,
            Snapshot = PredictionSnapshotOf
        });
        AddActivePredictionCommands();
        predictionRecent = new (HexDocument?, int)[3];
        documentClosed += PredictionClosed;
        foreach (var open in documents)
            PreparePrediction(open);
        foreach (NotebookCellView view in cellViews)
        {
            if (view.Cell.Kind != CellKind.Raw)
                PreparePrediction(CellPredictionTarget(view));
        }
        if (predictionItem is null)
            BuildPredictionItem();
        predictionItemWritten = false;
    }

    private void StopPredictions()
    {
        predictions.ShutDown();
        documentClosed -= PredictionClosed;
        ClearShownPrediction();
        foreach (var open in documents)
            StopEditorPrediction(open.Editor);
        foreach (NotebookCellView view in cellViews)
            StopEditorPrediction(view.Editor);
        predictionSession = null;
        predictionRecent = [];
        predictionAccepting = null;
        predictionAcceptedIn = predictionAsked = predictionEditingIn = null;
        predictionAllowedFor = null;
        predictionShownLines = predictionAskedLines = null;
        predictionOptions = null;
        predictionPrivacy = null;
        predictionPrivacyRoots = null;
        predictionPrivacyPatterns = predictionAllowedPatterns = predictionLanguagesOf = null;
        predictionLanguageFor = null;
        predictionKernelLanguage = predictionLanguageId = predictionLanguageName = predictionItemLanguage = null;
        predictionLanguageKnown = false;
        predictionAskedPath = predictionShownPath = string.Empty;
        predictionOptionsOf = predictionPrivacyOf = predictionAllowedOf = null;
        predictionKeyOf = predictionFailureNoticed = predictionItemStatusText = null;
        predictionItemState = PredictionItemState.Hidden;
        predictionWaitingSince = -1;
    }

    private void PreparePrediction(HexDocument prepared) => PreparePrediction(new PredictionTarget(prepared, prepared.Editor));

    /// <summary>An editor told what to call when its prediction ends or is typed through.</summary>
    private void PreparePrediction(PredictionTarget prepared)
    {
        CodeEditor editor = prepared.Editor;
        editor.OnPredictionEnded = taken => PredictionEnded(prepared, taken);
        editor.OnPredictionTypedThrough = () =>
        {
            // Stepping over an auto-closed character moves the ghost without a text change.
            if (predictionShownIn == prepared)
                predictionShownCaret = editor.Document.Caret;
        };
    }

    private static void StopEditorPrediction(CodeEditor editor)
    {
        editor.OnPredictionEnded = null;
        editor.OnPredictionTypedThrough = null;
        editor.ClearPrediction();
    }

    private void PredictionEdited(HexDocument changed) => PredictionEdited(new PredictionTarget(changed, changed.Editor));

    /// <summary>Tell the engine which editor version changed and whether the prediction survived the edit.</summary>
    private void PredictionEdited(PredictionTarget changed)
    {
        if (predictionSession is null || changed.Document != document || !changed.Editor.IsFocused
            || !PredictionSettings.On(settings) || LayoutTools)
            return;
        if (changed.Notebook is { } notebook
            && (!NotebookShown || notebookShown != notebook || notebook.Remake || changed.Cell?.Kind == CellKind.Raw))
            return;
        if (changed.Cell is null && !IsPredictedFile(changed.Document))
            return;

        // An edit the prediction is still showing after was its own, typed through or a word of it taken (Ctrl+Right):
        // whatever ended it would have said so first
        UITextDocument text = changed.Editor.Document;
        bool through = predictionShownIn == changed && changed.Editor.HasPrediction;
        if (through)
        {
            predictionShownVersion = text.Version;
            predictionShownCaret = text.Caret;
        }

        if (changed.Cell is null)
            RememberEdited(changed.Document, text.Caret.Line);
        QueuePrediction(changed, through);
    }

    private void QueuePrediction(PredictionTarget target, bool through = false)
    {
        if (predictionEditingIn is { } previous && previous != target)
            ForgetPrediction(previous);

        predictionEditingIn = target;
        predictions.ShortcutOnly = settings.PredictionShortcutOnly;
        predictions.Edited(target.Key, target.Editor.Document.Version, through);
    }

    /// <summary>Every tick: the engine asked, a finished prediction given to the editor, the status item brought up to date.</summary>
    private void UpdatePredictions()
    {
        if (!settings.EditPredictions || HexAi.Off)
        {
            if (predictionSession is not null)
                StopPredictions();
            return;
        }
        if (predictionSession is null)
            StartPredictions();

        predictions.ShortcutOnly = settings.PredictionShortcutOnly;
        SayPredictionsOnce();

        if (predictionEditingIn is { } target && (!PredictionFocused(target)
            || !PredictionSettings.On(settings) || LanguageOffOnScreen(target) || !AllowedOnScreen()
            || target.Editor.ReadOnly || target.Document.Editor.ReadOnly))
            ForgetPrediction(target);

        // A prediction taken is in the history now, the next is asked for at once
        if (predictionAccepting is { IsCompleted: true })
        {
            predictionAccepting = null;
            if (predictionAcceptedIn is { } accepted && PredictionFocused(accepted)
                && accepted.Editor.Document.Version == predictionAcceptedVersion)
            {
                QueuePrediction(accepted);
                predictions.Accepted(accepted.Key);
            }
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
    /// The editor's text as it is when typing has paused, null when a gate is shut (4.1). The editable lines, version
    /// and caret belong to the file or cell being typed into. A notebook adds nearby cells' source as context and uses
    /// the cell's id in the path sent to the model, so its edit history follows the cell when it moves.
    /// </summary>
    private PredictionSnapshot? PredictionSnapshotOf(object asked)
    {
        if (predictionEditingIn is not { } target || !ReferenceEquals(asked, target.Key)
            || PredictionGateOf(target) != PredictionGate.Open || target.Document.Path is not { } path)
            return null;

        UITextDocument text = target.Editor.Document;
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
        PredictionNotebookContext? context = null;
        if (target.Notebook is { } notebook && target.Cell is { } cell)
        {
            context = PredictionNotebookContext.Capture(notebook.Model, cell);
            if (context is null)
                return null;
            sent += "#cell-" + Uri.EscapeDataString(cell.Id);
            notebook.PredictionPaths.Add(sent);
        }

        predictionAsked = target;
        predictionAskedVersion = text.Version;
        predictionAskedCaret = text.Caret;
        predictionAskedLines = lines;
        predictionAskedPath = sent;

        return new PredictionSnapshot(target.Key, text.Version, sent, PredictionLanguage(target), lines,
            text.Caret.Line, text.Caret.Column, RelatedExcerpts(target.Document, roots), context);
    }

    /// <summary>The first gate of 4.1 that's shut for the editor on screen, or Open.</summary>
    private PredictionGate PredictionGateOf(PredictionTarget target)
    {
        CodeEditor editor = target.Editor;
        UITextDocument text = editor.Document;
        bool onScreen = PredictionTargetOnScreen() == target;
        return HexPredictionGlue.Gate(new PredictionFacts(
            OnScreen: onScreen,
            Code: onScreen,
            Focused: editor.IsFocused,
            ReadOnly: editor.ReadOnly || target.Document.Editor.ReadOnly,
            Selection: text.HasSelection,
            ExtraCarets: text.HasExtraCarets,
            PopupOpen: editor.CompletionOpen || editor.Signature is not null,
            On: PredictionSettings.On(settings),
            LanguageOff: onScreen && LanguageOffOnScreen(target),
            Allowed: onScreen && target.Document.Path is { } path && PredictionAllowed(path)));
    }

    // The editor of a file on its own, or the editor of the picked code or Markdown cell.
    private PredictionTarget? PredictionTargetOnScreen()
    {
        if (LayoutTools)
            return null;
        if (NotebookShown)
        {
            HexNotebook notebook = notebookShown!;
            if (notebook.Document == document && !notebook.Remake && notebook.Unreadable is null
                && notebook.Selected >= 0 && notebook.Selected < cellViews.Count
                && cellViews[notebook.Selected] is { } view && view.Notebook == notebook
                && view.Cell.Kind is CellKind.Code or CellKind.Markdown)
                return CellPredictionTarget(view);
            return null;
        }
        return IsPredictedFile(document) ? new PredictionTarget(document, document.Editor) : null;
    }

    private static PredictionTarget CellPredictionTarget(NotebookCellView view) =>
        new(view.Notebook.Document, view.Editor, view.Notebook, view.Cell);

    private bool PredictionFocused(PredictionTarget target) =>
        target.Editor.IsFocused && PredictionTargetOnScreen() == target;

    private static bool IsPredictedFile(HexDocument of) =>
        of.IsText && of.Picture is null && of.DiffOf is null && of.Path is not null;

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
            if (other is null || other == of || !documents.Contains(other) || !IsPredictedFile(other) || !PredictionAllowed(other.Path!)
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
        if (predictionAsked is not { } target || !ReferenceEquals(result.Document, target.Key)
            || predictionAskedLines is not { } lines || PredictionGateOf(target) != PredictionGate.Open)
            return;

        CodeEditor editor = target.Editor;
        UITextDocument text = editor.Document;
        if (text.Version != result.Version || text.Version != predictionAskedVersion)
            return;
        if (text.Caret != predictionAskedCaret || text.HasSelection || text.HasExtraCarets)
        {
            QueuePrediction(target);
            return;
        }
        if (HexPredictionGlue.Edits(result.Edits, lines) is not { } edits)
            return;

        predictionShownIn = target;
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
    private void PredictionEnded(PredictionTarget target, bool taken)
    {
        if (predictionShownIn != target)
            return;
        predictionShownIn = null;

        UITextDocument text = target.Editor.Document;
        if (taken)
        {
            predictionsTaken++;
            string[] before = predictionShownLines ?? [];
            var after = new string[text.LineCount];
            for (int i = 0; i < after.Length; i++)
                after[i] = text.LineAt(i);
            string path = predictionShownPath;
            PredictionHistory history = predictions.History;
            predictionAcceptedIn = target;
            predictionAcceptedVersion = text.Version;
            predictionAccepting = Task.Run(() => history.Accepted(path, string.Join('\n', before), string.Join('\n', after)));
            return;
        }

        if (text.Version != predictionShownVersion || !PredictionFocused(target))
            return;
        if (predictionShownGhost && (text.Caret != predictionShownCaret || text.HasSelection || text.HasExtraCarets))
        {
            QueuePrediction(target);
            return;
        }
        predictions.Dismissed(target.Key);
    }

    private void ClearShownPrediction()
    {
        if (predictionShownIn is not { } shown)
            return;
        predictionShownIn = null;
        shown.Editor.ClearPrediction();
    }

    // A cell left the screen or lost the keyboard, so its request and anything it was showing go.
    private void ForgetPrediction(PredictionTarget target)
    {
        predictionSession?.Engine.Forget(target.Key);
        if (predictionEditingIn == target)
            predictionEditingIn = null;
        if (predictionShownIn == target)
            ClearShownPrediction();
        if (predictionAsked == target)
        {
            predictionAsked = null;
            predictionAskedLines = null;
        }
        if (predictionAcceptedIn == target)
            predictionAcceptedIn = null;
    }

    // A document closed: what was asked for it goes, and its edits from the history
    private void PredictionClosed(HexDocument closed)
    {
        if (predictionEditingIn is { } target && target.Document == closed)
            ForgetPrediction(target);
        if (closed.Path is { } path)
            predictions.History.Closed(HexPredictionGlue.Sent(WorkspaceRoots(), path));
        for (int i = 0; i < predictionRecent.Length; i++)
        {
            if (predictionRecent[i].Document == closed)
                predictionRecent[i] = default;
        }
    }
}
