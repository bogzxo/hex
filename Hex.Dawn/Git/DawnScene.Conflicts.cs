using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// Merge conflicts, the blocks a merge leaves for somebody to decide. What's current (ours) is lit in one colour and
// what's incoming (theirs) in another, the markers dimmed, and the first marker of each says what does what. Alt+1 keeps
// the current side, Alt+2 the incoming, Alt+3 both, for the conflict the caret is in (or the next one after it), one step
// to undo each, from the right click as well. Alt+F5 goes from one conflict to the next.
internal sealed partial class DawnScene
{
    // The conflicts of the file on screen, and the version of its text they were found in
    private List<GitConflict> conflicts = [];
    private (HexDocument? Of, int Version) conflictsFor = (null, -1);

    // When the text last changed, to look again once the typing stops
    private const long CONFLICTS_SETTLE_MS = 300;
    private int conflictsSeenVersion = -1;
    private long conflictsChangedAt;

    private void BuildConflicts()
    {
        commands.Add("Git/changes", Said("conflicts.accept_current"), () => SettleConflict(GitConflictPick.Current), "Alt+1", "arrow-up", UIKeyUse.BoundWhileTyping);
        commands.Add("Git/changes", Said("conflicts.accept_incoming"), () => SettleConflict(GitConflictPick.Incoming), "Alt+2", "arrow-down", UIKeyUse.BoundWhileTyping);
        commands.Add("Git/changes", Said("conflicts.accept_both"), () => SettleConflict(GitConflictPick.Both), "Alt+3", "arrow-both", UIKeyUse.BoundWhileTyping);
    }

    /// <summary>Every tick, the file on screen looked through for conflicts again when its text changed, and lit.</summary>
    private void UpdateConflicts(bool now = false)
    {
        if (conflictsFor == (document, code.Document.Version))
            return;

        // Looked for once the typing stops for a moment, a file just shown at once. Looking every key was a good part of a
        // millisecond a key in a big file
        long time = System.Environment.TickCount64;
        if (!now && conflictsFor.Of == document && code.Document.Version != conflictsSeenVersion)
        {
            conflictsSeenVersion = code.Document.Version;
            conflictsChangedAt = time;
            return;
        }
        if (!now && conflictsFor.Of == document && time - conflictsChangedAt < CONFLICTS_SETTLE_MS)
            return;

        bool had = conflicts.Count > 0 && conflictsFor.Of == document;
        conflictsFor = (document, code.Document.Version);

        // Most files have none, which costs a look at the start of each line and nothing put together (the whole text as one
        // string every key was what made typing in a big file slow before)
        if (!document.IsText || document.DiffOf is not null || document.Picture is not null || !HasConflictMarker())
        {
            conflicts = [];
            if (had)
                LightConflicts();
            return;
        }

        conflicts = GitConflicts.Find(code.Text.Replace("\r\n", "\n").Split('\n'));
        LightConflicts();
    }

    private bool HasConflictMarker()
    {
        for (int i = 0; i < code.Document.LineCount; i++)
        {
            if (code.Document.LineAt(i).StartsWith("<<<<<<<", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // The colours behind each side and the markers, and the first marker's note of what does what
    private void LightConflicts()
    {
        Vector4 current = (compositor.Skin?.Palette.Link ?? new Vector4(0.4f, 0.6f, 1.0f, 1.0f)) with { W = 0.2f };
        Vector4 incoming = (compositor.Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f)) with { W = 0.16f };
        Vector4 marker = DimColor with { W = 0.18f };

        var lit = new List<UICodeLineHighlight>();
        var notes = new List<UICodeHint>();
        foreach (GitConflict conflict in conflicts)
        {
            lit.Add(new UICodeLineHighlight(conflict.Start, 1, marker));
            lit.Add(new UICodeLineHighlight(conflict.Start + 1, conflict.CurrentEnd - conflict.Start - 1, current));
            if (conflict.Base >= 0)
                lit.Add(new UICodeLineHighlight(conflict.Base, conflict.Middle - conflict.Base + 1, marker));
            else
                lit.Add(new UICodeLineHighlight(conflict.Middle, 1, marker));
            lit.Add(new UICodeLineHighlight(conflict.Middle + 1, conflict.End - conflict.Middle - 1, incoming));
            lit.Add(new UICodeLineHighlight(conflict.End, 1, marker));
            notes.Add(new UICodeHint(conflict.Start, Said("conflicts.note")));
        }

        code.LineHighlights = lit;

        // The note goes where the values of a run do, so a file of HIDL (which has those) goes without it
        if (!ReferenceEquals(code.Highlighter, CodeLanguages.Hidl))
            code.InlineHints = notes;

        if (conflicts.Count > 0)
            Say(Said("conflicts.found", ("count", HexText.Count("conflict", conflicts.Count))));
    }

    /// <summary>The conflict the caret is in, or the next one after it, settled with a side kept, one step to undo.</summary>
    private void SettleConflict(GitConflictPick pick)
    {
        UpdateConflicts(now: true);
        int caret = code.Document.Caret.Line;
        GitConflict? at = conflicts.Cast<GitConflict?>().FirstOrDefault(conflict => caret >= conflict!.Value.Start && caret <= conflict.Value.End)
            ?? conflicts.Cast<GitConflict?>().FirstOrDefault(conflict => conflict!.Value.Start > caret);
        if (at is not { } conflict)
        {
            Say(Said("conflicts.none"));
            return;
        }

        // Only the block's lines swapped for what's kept of them, the rest of the file (its folds, the scroll, what's
        // kept by line) left alone, one step to undo
        string[] lines = code.Text.Replace("\r\n", "\n").Split('\n');
        List<string> settled = GitConflicts.Settle(lines, conflict, pick);
        int kept = settled.Count - (lines.Length - (conflict.End - conflict.Start + 1));
        string block = string.Join('\n', settled.GetRange(conflict.Start, kept));

        UITextDocument text = code.Document;
        text.CollapseCarets();
        if (conflict.End + 1 < text.LineCount)
        {
            text.Select(new UITextPosition(conflict.Start, 0), new UITextPosition(conflict.End + 1, 0));
            text.Insert(kept > 0 ? block + "\n" : string.Empty);
        }
        else
        {
            text.Select(new UITextPosition(conflict.Start, 0), new UITextPosition(conflict.End, text.LineAt(conflict.End).Length));
            text.Insert(block);
        }
        UpdateConflicts(now: true);
        code.GoTo(conflict.Start, 0);
        Say(Said(pick switch
        {
            GitConflictPick.Current => "conflicts.kept_current",
            GitConflictPick.Incoming => "conflicts.kept_incoming",
            _ => "conflicts.kept_both"
        }));
    }

    /// <summary>The right click's ways of settling the conflict the caret is in, when it's in one.</summary>
    private void AddConflictMenu(Menu menu)
    {
        int caret = code.Document.Caret.Line;
        if (!conflicts.Any(conflict => caret >= conflict.Start && caret <= conflict.End))
            return;

        menu.Add(Said("conflicts.accept_current"), () => SettleConflict(GitConflictPick.Current), "Alt+1", "arrow-up");
        menu.Add(Said("conflicts.accept_incoming"), () => SettleConflict(GitConflictPick.Incoming), "Alt+2", "arrow-down");
        menu.Add(Said("conflicts.accept_both"), () => SettleConflict(GitConflictPick.Both), "Alt+3", "arrow-both");
        menu.AddSeparator();
    }
}
