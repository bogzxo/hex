using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Next-edit suggestions beyond the caret (docs/next-edit-plan.md, 2): edits waiting at other places in a file, a rename's
// remaining occurrences for now. The rename tracker (in the prediction history) notices a rename from the events as they
// close; once the typing has paused, the places left become NextEdits in the document's queue. The nearest edit at or
// after the caret is given to the editor as its prediction, the one renderer there is: Tab goes to it when it's out of
// sight, the next Tab takes it, Escape lets go of it. This adds the marks (an arrow in the gutter, a faint box, the hint
// "Tab ↓ 42" after the caret's line), Alt+] and Alt+[ to step round them, Shift+Esc to let go of all, and
// Alt+Shift+L to take every one of a source as one undo step. Every edit rebases the queue, a suggestion whose text
// was touched going. Four documents keep a queue, the one least used going first.
internal sealed partial class DawnScene
{
    private sealed class NextEditDoc(string path)
    {
        public string Path = path;
        public readonly NextEditQueue Queue = new();

        // The lines the queue is for (the document's own strings, not a copy of the text), kept only while it has something to rebase
        public string[] Lines = [];
        public int LineCount;
        public bool Based;
        public int Version = -1;

        // What the queue was last made from, so a tick that changed nothing makes nothing
        public int BuiltVersion = -1, BuiltStamp = -1;
        public long QuietSince;

        // What the editor was last told of the queue's marks, and the hint
        public int MarkedVersion = -1, MarkedCount = -1;
        public (int Caret, int Target, int More, int Version) HintKey = (-1, -1, -1, -1);
        public int ShownLine = -1;
    }

    // At most this many documents keep a queue, and typing has to pause this long before a rename's places are found
    private const int NEXT_EDIT_DOCS = 4;
    private const long NEXT_EDIT_QUIET_MS = 500;

    private readonly Dictionary<HexDocument, NextEditDoc> nextEditDocs = [];
    private readonly List<HexDocument> nextEditOrder = [];

    // The suggestion the editor is showing as its prediction, and what it was shown for
    private NextEdit? nextEditShown;
    private PredictionTarget? nextEditShownIn;
    private int nextEditShownVersion;

    // For the self test and the telemetry of the plan, 7: suggestions taken and let go of
    private int nextEditsTaken, nextEditsDismissed;

    private NextEditDoc NextEditDocOf(HexDocument of, string sent)
    {
        if (nextEditDocs.TryGetValue(of, out NextEditDoc? known))
        {
            nextEditOrder.Remove(of);
            nextEditOrder.Add(of);
            if (known.Path != sent)
            {
                known.Path = sent;
                known.Queue.Clear();
                known.Based = false;
            }
            return known;
        }

        while (nextEditOrder.Count >= NEXT_EDIT_DOCS)
        {
            HexDocument oldest = nextEditOrder[0];
            nextEditOrder.RemoveAt(0);
            if (nextEditDocs.Remove(oldest, out NextEditDoc? gone))
                gone.Queue.Clear();
            if (oldest.Editor is { } editor)
            {
                editor.SetJumpMarkers([]);
                editor.SetJumpHint(-1, null);
            }
        }
        var made = new NextEditDoc(sent);
        nextEditDocs[of] = made;
        nextEditOrder.Add(of);
        return made;
    }

    // Every tick: the document on screen's queue rebased for what was typed, the rename's places made once the typing has
    // paused, its marks and hint brought up to date, and the nearest suggestion shown as the editor's prediction
    private void UpdateNextEdits()
    {
        if (predictionSession is null)
            return;

        if (PredictionTargetOnScreen() is not { Cell: null } target || target.Document.Path is not { } path
            || !PredictionSettings.On(settings) || target.Document != document || !AllowedOnScreen())
        {
            ClearNextEditShown();
            return;
        }

        UITextDocument text = target.Editor.Document;
        if (!nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state))
        {
            // Nothing is made for a file until a rename has been seen in it
            if (predictions.History.Renames.Stamp == 0 && nextEditDocs.Count == 0 && !predictions.HasFar)
                return;
            state = NextEditDocOf(target.Document, HexPredictionGlue.Sent(WorkspaceRoots(), path));
        }

        RenameTracker renames = predictions.History.Renames;
        bool edited = state.Version != text.Version;
        if (edited)
        {
            if (state.Based && state.Queue.Count > 0 && HexNextEditGlue.Change(state.Lines, state.LineCount, text) is { } change)
                state.Queue.Rebase([change]);
            state.Version = text.Version;
            state.QuietSince = Environment.TickCount64;
            BaseOn(state, text);
        }

        // What the model predicted for other places: the rename's suggestions win where they overlap
        if (predictions.HasFar)
        {
            IReadOnlyList<NextEdit> far = predictions.TakeFar(target.Key, text.Version, text.Text);
            foreach (NextEdit model in far)
            {
                if (NextEditMerge.Add(state.Queue, model))
                    BaseOn(state, text);
            }
            if (far.Count > 0)
                state.Version = text.Version;
        }

        int stamp = renames.Stamp;
        if ((state.BuiltVersion != text.Version || state.BuiltStamp != stamp) && Environment.TickCount64 - state.QuietSince >= NEXT_EDIT_QUIET_MS)
            BuildRenameEdits(state, renames, text);

        string[]? lines = null;
        string[] Lines()
        {
            if (lines is null)
            {
                string[] made = [];
                int count = HexNextEditGlue.Snapshot(text, ref made);
                lines = count == made.Length ? made : made[..count];
            }
            return lines;
        }

        NextEditQueue queue = state.Queue;
        CodeEditor editor = target.Editor;
        if (queue.Count == 0)
        {
            ClearNextEditShown();
            if (state.MarkedCount != 0)
            {
                editor.SetJumpMarkers([]);
                editor.SetJumpHint(-1, null);
                state.MarkedCount = 0;
                state.MarkedVersion = text.Version;
            }
            return;
        }

        if (state.MarkedVersion != text.Version || state.MarkedCount != queue.Count)
        {
            editor.SetJumpMarkers(HexNextEditGlue.Markers(queue.All, Lines()));
            state.MarkedVersion = text.Version;
            state.MarkedCount = queue.Count;
            state.HintKey = (-1, -1, -1, -1);
        }

        // The editor's prediction is the model's when it has one, and not a suggestion's while anything else is shown
        if (predictionShownIn is not null || PredictionGateOf(target) != PredictionGate.Open)
        {
            ClearNextEditShown();
            editor.SetJumpHint(-1, null);
            state.HintKey = (-1, -1, -1, -1);
            return;
        }

        NextEdit? pick = queue.Peek(HexNextEditGlue.Offset(text, text.Caret));
        if (pick is null)
            return;

        if (!ReferenceEquals(pick, nextEditShown) || nextEditShownVersion != text.Version || !editor.HasPrediction)
        {
            if (editor.HasPrediction && nextEditShownIn is null)
                return;

            ClearNextEditShown();
            string body = text.Text;
            if (HexNextEditGlue.Anchored(pick, body) is null || HexPredictionGlue.Edits(pick.Edits, Lines()) is not { } edits)
            {
                queue.Drop(pick);
                return;
            }

            nextEditShownIn = target;
            nextEditShown = pick;
            nextEditShownVersion = text.Version;
            editor.SetPrediction(new UICodePrediction(edits));
            if (!editor.HasPrediction)
            {
                nextEditShownIn = null;
                nextEditShown = null;
                queue.Drop(pick);
                return;
            }
            state.ShownLine = HexPredictionGlue.Position(Lines(), pick.Start).Line;
            PredictionLog.JumpShown();
        }

        // The hint after the caret's line, made again only when the caret's line or the count moved
        var key = (text.Caret.Line, state.ShownLine, queue.Count - 1, text.Version);
        if (key != state.HintKey)
        {
            state.HintKey = key;
            editor.SetJumpHint(text.Caret.Line, HexNextEditGlue.JumpHint(text.Caret.Line, state.ShownLine, queue.Count - 1));
        }
    }

    // The rename's places left made into suggestions (replacing those made before), a rename with none left forgotten
    private void BuildRenameEdits(NextEditDoc state, RenameTracker renames, UITextDocument text)
    {
        state.BuiltVersion = text.Version;
        state.BuiltStamp = renames.Stamp;

        string body = text.Text;
        int caret = HexNextEditGlue.Offset(text, text.Caret);
        state.Queue.DropAll(NextEditSource.Rename);
        foreach (Rename rename in renames.Active(state.Path))
        {
            List<NextEdit> made = HexNextEditGlue.ForRename(rename, body, text.Version, caret);
            if (made.Count == 0)
            {
                renames.Drop(rename);
                continue;
            }
            foreach (NextEdit edit in made)
                NextEditMerge.AddRename(state.Queue, edit);
        }
        state.BuiltStamp = renames.Stamp;
        BaseOn(state, text);
    }

    // The queue's base: the document's lines now, or nothing while the queue is empty
    private static void BaseOn(NextEditDoc state, UITextDocument text)
    {
        if (state.Queue.Count > 0)
        {
            state.LineCount = HexNextEditGlue.Snapshot(text, ref state.Lines);
            state.Based = true;
        }
        else
        {
            state.Based = false;
            state.Lines = [];
        }
    }

    // The suggestion in the editor goes, the queue keeping it
    private void ClearNextEditShown()
    {
        if (nextEditShownIn is not { } shown)
            return;
        nextEditShownIn = null;
        nextEditShown = null;
        shown.Editor.ClearPrediction();
    }

    /// <summary>The editor's prediction ended; true when it was a suggestion's, which then is taken out of its queue (taken, or let go of with Escape).</summary>
    private bool NextEditEnded(PredictionTarget target, bool taken)
    {
        if (nextEditShownIn != target || nextEditShown is not { } shown)
            return false;
        nextEditShownIn = null;
        nextEditShown = null;

        if (!nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state))
            return true;
        UITextDocument text = target.Editor.Document;
        if (taken)
        {
            state.Queue.Drop(shown);
            nextEditsTaken++;
            PredictionLog.JumpAccepted();
        }
        else if (text.Version == nextEditShownVersion && PredictionFocused(target))
        {
            // Escape: this one only, and it isn't made again
            state.Queue.Dismiss(shown);
            nextEditsDismissed++;
            PredictionLog.JumpDismissed();
        }
        return true;
    }

    // The suggestions waiting in the document on screen's queue, for the status item
    private int PendingJumps() =>
        predictionSession is not null && PredictionTargetOnScreen() is { Cell: null } target
        && nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state) ? state.Queue.Count : 0;

    private bool HasNextEdits() =>
        predictionSession is not null && PredictionTargetOnScreen() is { Cell: null } target
        && nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state) && state.Queue.Count > 0;

    /// <summary>Alt+] and Alt+[: the caret to the next suggestion (or the one before), round past the last, nothing taken.</summary>
    private void NextEditStep(int direction)
    {
        if (PredictionTargetOnScreen() is not { Cell: null } target || !nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state))
            return;
        UITextDocument text = target.Editor.Document;
        int caret = HexNextEditGlue.Offset(text, text.Caret);
        NextEdit? to = direction >= 0 ? state.Queue.Next(caret) : state.Queue.Previous(caret);
        if (to is null)
            return;

        UITextPosition at = HexPredictionGlue.Position(text.Text.Split('\n'), to.Start);
        ClearNextEditShown();
        target.Editor.GoTo(at.Line, at.Column);
    }

    /// <summary>Shift+Esc: every suggestion let go of, a rename not followed any more.</summary>
    private void NextEditDismissAll()
    {
        if (PredictionTargetOnScreen() is not { Cell: null } target || !nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state))
            return;
        ClearNextEditShown();
        foreach (NextEdit edit in state.Queue.All.ToArray())
        {
            state.Queue.Dismiss(edit);
            nextEditsDismissed++;
            PredictionLog.JumpDismissed();
        }
        predictions.History.Renames.Forget(state.Path);
        state.BuiltStamp = predictions.History.Renames.Stamp;
        state.Based = false;
        state.Lines = [];
    }

    /// <summary>Alt+Shift+L: every suggestion of the shown one's source (the nearest's) taken, together as one undo step.</summary>
    private void NextEditAcceptAll()
    {
        if (PredictionTargetOnScreen() is not { Cell: null } target || !nextEditDocs.TryGetValue(target.Document, out NextEditDoc? state)
            || target.Editor.ReadOnly || target.Document.Editor.ReadOnly)
            return;
        UITextDocument text = target.Editor.Document;
        NextEdit? lead = nextEditShown ?? state.Queue.Peek(HexNextEditGlue.Offset(text, text.Caret));
        if (lead is null)
            return;

        List<NextEdit> same = [.. state.Queue.All.Where(edit => edit.Source == lead.Source)];
        ClearNextEditShown();
        int taken = HexNextEditGlue.AcceptAll(text, same);
        if (taken == 0)
            return;
        foreach (NextEdit edit in same)
            state.Queue.Drop(edit);
        nextEditsTaken += taken;
        for (int i = 0; i < taken; i++)
            PredictionLog.JumpAccepted();
        state.Based = false;
        state.Lines = [];
        state.Queue.DropAll(lead.Source);
    }

    // A document closed, or predictions stopped: its queue and marks go
    private void ForgetNextEdits(HexDocument? only = null)
    {
        ClearNextEditShown();
        foreach (HexDocument held in only is null ? nextEditDocs.Keys.ToArray() : new[] { only })
        {
            if (!nextEditDocs.Remove(held))
                continue;
            nextEditOrder.Remove(held);
            if (held.Editor is { } editor)
            {
                editor.SetJumpMarkers([]);
                editor.SetJumpHint(-1, null);
            }
        }
    }
}
