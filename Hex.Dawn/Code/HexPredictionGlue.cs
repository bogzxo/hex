using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>Why nothing is asked for the file on screen now, or Open when something may be (docs/edit-prediction-plan.md, 4.1).</summary>
internal enum PredictionGate
{
    Open,
    NotOnScreen,
    NotCode,
    NotFocused,
    ReadOnly,
    Selection,
    Carets,
    PopupOpen,
    Off,
    LanguageOff,
    Excluded,
}

/// <summary>What the status bar's prediction item says, worked out from the settings, the file and the engine.</summary>
internal enum PredictionItemState
{
    Hidden,
    Off,
    NotSetUp,
    FolderOff,
    LanguageOff,
    FileOff,
    On,
    Waiting,
    Failed,
}

/// <summary>What is known of the file on screen when a prediction is to be asked for it, every gate of 4.1 in one place.</summary>
internal readonly record struct PredictionFacts(
    bool OnScreen,
    bool Code,
    bool Focused,
    bool ReadOnly,
    bool Selection,
    bool ExtraCarets,
    bool PopupOpen,
    bool On,
    bool LanguageOff,
    bool Allowed);

/// <summary>
/// The parts of edit prediction's glue that need no editor to be worked out: whether to ask, what the status item says,
/// and the prediction's offsets (into the lines joined with '\n') as places in the document.
/// </summary>
internal static class HexPredictionGlue
{
    /// <summary>How long a request is in flight before the status item says it's waiting, so a quick one doesn't flicker.</summary>
    public const double WAITING_AFTER_MS = 300.0;

    /// <summary>The lines round the last caret of another file sent with a prompt.</summary>
    public const int EXCERPT_LINES = 30;

    /// <summary>The first gate that is shut, or Open.</summary>
    public static PredictionGate Gate(in PredictionFacts facts) => facts switch
    {
        { OnScreen: false } => PredictionGate.NotOnScreen,
        { Code: false } => PredictionGate.NotCode,
        { On: false } => PredictionGate.Off,
        { LanguageOff: true } => PredictionGate.LanguageOff,
        { Allowed: false } => PredictionGate.Excluded,
        { ReadOnly: true } => PredictionGate.ReadOnly,
        { Focused: false } => PredictionGate.NotFocused,
        { Selection: true } => PredictionGate.Selection,
        { ExtraCarets: true } => PredictionGate.Carets,
        { PopupOpen: true } => PredictionGate.PopupOpen,
        _ => PredictionGate.Open,
    };

    /// <summary>
    /// What the status item is: hidden but for code files and under No AI, then why predictions are off, in the order the
    /// user would put it right (switched off, not set up, off in the folder, off for the language, the file excluded), and
    /// once they are on, waiting, what went wrong, or on.
    /// </summary>
    public static PredictionItemState ItemState(bool noAi, bool code, bool switchedOn, bool configured, bool folderOn,
        bool languageOff, bool allowed, PredictionStatus status, bool waitingLong)
    {
        if (noAi || !code)
            return PredictionItemState.Hidden;
        if (!switchedOn)
            return PredictionItemState.Off;
        if (!configured)
            return PredictionItemState.NotSetUp;
        if (!folderOn)
            return PredictionItemState.FolderOff;
        if (languageOff)
            return PredictionItemState.LanguageOff;
        if (!allowed)
            return PredictionItemState.FileOff;
        return status switch
        {
            PredictionStatus.Waiting when waitingLong => PredictionItemState.Waiting,
            PredictionStatus.Unreachable or PredictionStatus.Unauthorized or PredictionStatus.NoSuchModel or PredictionStatus.TimedOut
                or PredictionStatus.BadAnswer or PredictionStatus.Refused => PredictionItemState.Failed,
            _ => PredictionItemState.On,
        };
    }

    /// <summary>The key in the localisation of what the status item says, by its state and, when it failed, how.</summary>
    public static string ItemKey(PredictionItemState state, PredictionStatus status) => state switch
    {
        PredictionItemState.Off => "prediction.item_off",
        PredictionItemState.NotSetUp => "prediction.item_not_set_up",
        PredictionItemState.FolderOff => "prediction.item_folder_off",
        PredictionItemState.LanguageOff => "prediction.item_language_off",
        PredictionItemState.FileOff => "prediction.item_file_off",
        PredictionItemState.Waiting => "prediction.item_waiting",
        PredictionItemState.Failed => status switch
        {
            PredictionStatus.Unreachable => "prediction.item_unreachable",
            PredictionStatus.Unauthorized => "prediction.item_unauthorized",
            PredictionStatus.NoSuchModel => "prediction.item_no_model",
            PredictionStatus.TimedOut => "prediction.item_timed_out",
            PredictionStatus.Refused => "prediction.item_refused",
            _ => "prediction.item_bad_answer",
        },
        _ => "prediction.item_on",
    };

    /// <summary>The place of an offset into the lines joined with '\n', the end of the last line for one past it.</summary>
    public static UITextPosition Position(IReadOnlyList<string> lines, int offset)
    {
        if (lines.Count == 0)
            return default;
        int line = 0;
        while (line < lines.Count - 1 && offset > lines[line].Length)
        {
            offset -= lines[line].Length + 1;
            line++;
        }
        return new UITextPosition(line, Math.Clamp(offset, 0, lines[line].Length));
    }

    /// <summary>
    /// A prediction's edits as the editor takes them, against the document's lines as they were when it was asked for (the
    /// same text, its version unchanged), in order. Null when an edit is past the end of the text or they overlap.
    /// </summary>
    public static UITextEdit[]? Edits(IReadOnlyList<PredictionEdit> edits, IReadOnlyList<string> lines)
    {
        if (edits.Count == 0)
            return null;

        int length = Length(lines);
        var made = new UITextEdit[edits.Count];
        int last = -1;
        for (int i = 0; i < edits.Count; i++)
        {
            PredictionEdit edit = edits[i];
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > length || edit.Start < last)
                return null;
            last = edit.End;
            made[i] = new UITextEdit(Position(lines, edit.Start), Position(lines, edit.End), edit.Text);
        }
        return made;
    }

    /// <summary>
    /// Where the caret goes once the edits are made, from an offset into the text as it is then, as a place in that text.
    /// Worked out from the lines before and the edits, without making the text.
    /// </summary>
    public static UITextPosition? CaretAfter(IReadOnlyList<PredictionEdit> edits, IReadOnlyList<string> lines, int? caretAfter)
    {
        if (caretAfter is not { } after || edits.Count == 0)
            return null;

        int delta = 0;
        UITextPosition? endBefore = null, endAfter = null;

        // A place in the text before, moved by the edits before it
        UITextPosition Moved(UITextPosition old)
        {
            if (endBefore is not { } before || endAfter is not { } now)
                return old;
            return old.Line == before.Line
                ? new UITextPosition(now.Line, now.Column + old.Column - before.Column)
                : old with { Line = old.Line + now.Line - before.Line };
        }

        foreach (PredictionEdit edit in edits)
        {
            int start = edit.Start + delta;
            if (after < start)
                return Moved(Position(lines, after - delta));

            UITextPosition from = Moved(Position(lines, edit.Start));
            if (after <= start + edit.Text.Length)
                return Within(from, edit.Text, after - start);

            endBefore = Position(lines, edit.End);
            endAfter = Within(from, edit.Text, edit.Text.Length);
            delta += edit.Text.Length - (edit.End - edit.Start);
        }

        return Moved(Position(lines, after - delta));
    }

    /// <summary>The 30 lines round a line, its own in the middle, as references to the document's strings.</summary>
    public static string[] Excerpt(IReadOnlyList<string> lines, int line)
    {
        if (lines.Count == 0)
            return [];
        int from = Math.Clamp(line - EXCERPT_LINES / 2, 0, Math.Max(0, lines.Count - EXCERPT_LINES));
        int count = Math.Min(EXCERPT_LINES, lines.Count - from);
        var taken = new string[count];
        for (int i = 0; i < count; i++)
            taken[i] = lines[from + i];
        return taken;
    }

    /// <summary>A path as it's sent, inside its folder with forward slashes.</summary>
    public static string Sent(IReadOnlyList<string> roots, string path) => HexSearch.RelativeTo(roots, path).Replace('\\', '/');

    // The place a number of characters into text that starts at a place
    private static UITextPosition Within(UITextPosition from, string text, int count)
    {
        int line = from.Line, column = from.Column;
        for (int i = 0; i < count && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 0;
            }
            else
                column++;
        }
        return new UITextPosition(line, column);
    }

    // How long the lines are joined with '\n'
    private static int Length(IReadOnlyList<string> lines)
    {
        int length = Math.Max(0, lines.Count - 1);
        for (int i = 0; i < lines.Count; i++)
            length += lines[i].Length;
        return length;
    }
}
