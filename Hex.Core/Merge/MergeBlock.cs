namespace Hex.Core.Merge;

/// <summary>
/// One region of the Result text of a merge: clean lines, or a conflict. An unresolved conflict holds its base text (or
/// nothing), never the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> marker lines, so the text of all the blocks is always real text.
/// </summary>
internal sealed record MergeBlock(MergeRegion Region, string Text, bool Resolved)
{
    public bool IsConflict => Region is MergeRegion.Conflict;

    public static string Lines(IReadOnlyList<string> lines) => lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";

    /// <summary>The text a region starts with: its lines when clean, the base text (or none) of a conflict.</summary>
    public static string InitialText(MergeRegion region) =>
        Lines(region is MergeRegion.Clean clean ? clean.Lines : ((MergeRegion.Conflict)region).BaseLines ?? []);

    public static List<MergeBlock> From(MergeModel model) =>
        [.. model.Regions.Select(region => new MergeBlock(region, InitialText(region), region is MergeRegion.Clean))];

    public static string Join(IEnumerable<MergeBlock> blocks) => string.Concat(blocks.Select(block => block.Text));

    /// <summary>The block answered with a choice, Unresolved taking the answer back to the base text.</summary>
    public MergeBlock Choose(MergeChoice choice)
    {
        if (Region is not MergeRegion.Conflict conflict)
            return this;
        IReadOnlyList<string> lines = choice switch
        {
            MergeChoice.Mine => conflict.MineLines,
            MergeChoice.Theirs => conflict.TheirsLines,
            MergeChoice.BothMineFirst => [.. conflict.MineLines, .. conflict.TheirsLines],
            MergeChoice.BothTheirsFirst => [.. conflict.TheirsLines, .. conflict.MineLines],
            _ => conflict.BaseLines ?? [],
        };
        return this with { Text = Lines(lines), Resolved = choice != MergeChoice.Unresolved };
    }

    /// <summary>
    /// The block taken by hand with its text as it is, or with both sides when it has none to start from, so there is
    /// something to edit.
    /// </summary>
    public MergeBlock Seed() => Region is MergeRegion.Conflict conflict && !Resolved
        ? this with { Text = Text.Length > 0 ? Text : Lines([.. conflict.MineLines, .. conflict.TheirsLines]), Resolved = true }
        : this;

    /// <summary>
    /// Put a text edit of the whole Result (replacing start..end by inserted) into the blocks it falls in. A conflict
    /// edited this way is resolved by hand. A pure insertion at the spot of the empty conflict at <paramref name="selected"/>
    /// goes into it and not into the block after.
    /// </summary>
    public static void ApplyEdit(List<MergeBlock> blocks, int start, int end, string inserted, ref int selected)
    {
        int offset = 0;
        bool placed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            MergeBlock block = blocks[i];
            int after = offset + block.Text.Length;
            bool emptySelected = i == selected && block.IsConflict && block.Text.Length == 0 && start == offset && start == end;
            bool owner = !placed && (start < after || i == blocks.Count - 1 || emptySelected);
            int from = Math.Clamp(start - offset, 0, block.Text.Length), to = Math.Clamp(end - offset, 0, block.Text.Length);
            if (owner || from < to)
            {
                string edited = block.Text[..from] + (owner ? inserted : "") + block.Text[to..];
                blocks[i] = block.IsConflict && !block.Resolved && edited != block.Text
                    ? block with { Text = edited, Resolved = true }
                    : block with { Text = edited };
                if (block.IsConflict)
                    selected = i;
                placed |= owner;
            }

            offset = after;
        }
    }
}
