using System.Numerics;
using System.Text;
using System.Security.Cryptography;
using Hex.Core.Merge;
using Horizon.Rendering;

namespace Hex.Dawn;

internal sealed class MergeEditor
{
    private const float PaneLabelHeight = 26, Gap = 6, ControlHeight = 26;
    private const int HistoryLimit = 301;

    private readonly MergeSession session;
    private readonly CodeEditor result, mine, theirs, basis;
    private readonly Panel header, body, resultPane, minePane, theirsPane, basePane;
    private readonly List<(int Block, Panel Top, Panel Bottom, Panel Left, Panel Right)> outlines = [];
    private readonly Label count;
    private readonly Button complete, cancel;
    private readonly Button? showBase;
    private readonly List<(UIComponent Item, float Width)> controls = [];
    private readonly List<Button> textActions = [];
    private readonly List<Button> resolutionActions = [];
    private readonly List<Block> blocks = [];
    private readonly Dictionary<string, Block[]> history = new(StringComparer.Ordinal);
    private readonly Queue<string> historyOrder = new();
    private readonly Action requestComplete, requestCancel;
    private readonly Func<Vector4> warning, success;
    private string text = "";
    private bool applying, wide, showingBase;
    private Vector2 fitted;
    private (CodeEditor? Editor, int Block) scrolled = (null, -1);
    private int selected;
    private readonly int[] mineLines, theirLines, baseLines;

    private sealed record Block(MergeRegion Region, string Text, bool Resolved)
    {
        public bool IsConflict => Region is MergeRegion.Conflict;
    }

    public MergeEditor(MergeSession session, CodeEditor result, Action complete, Action cancel,
        Action<MergeSide> side, Func<Vector4> warning, Func<Vector4> success)
    {
        this.session = session;
        this.result = result;
        requestComplete = complete;
        requestCancel = cancel;
        this.warning = warning;
        this.success = success;
        Root = new Panel { Fill = UIFill.Both, Color = Vector4.Zero, Clip = true };
        header = Root.Add(PlainPanel());
        body = Root.Add(PlainPanel());
        count = header.Add(new Label { Align = Origin.Left, TextScale = 0.145f });
        controls.Add((count, 150));

        if (!session.IsBinary)
        {
            textActions.Add(AddButton("Previous", "Go to the previous conflict.", 72, () => Go(-1)));
            textActions.Add(AddButton("Next", "Go to the next conflict.", 60, () => Go(1)));
            textActions.Add(AddButton("Accept all mine", "Resolve every conflict with Mine.", 112, () => AcceptAll(MergeChoice.Mine)));
            textActions.Add(AddButton("Accept all theirs", "Resolve every conflict with Theirs.", 115, () => AcceptAll(MergeChoice.Theirs)));
            if (session.Model.Regions.Any(region => region is MergeRegion.Conflict { BaseLines: not null } or MergeRegion.Clean { BaseLines: not null }))
                showBase = AddButton("Base", "Show or hide the common ancestor.", 62, ToggleBase);

            foreach (MergeRegion region in session.Model.Regions)
                blocks.Add(new(region, Lines(Initial(region)), region is MergeRegion.Clean));
        }

        this.complete = AddButton("Complete merge", "Write the resolved result and close the merge editor.", 124, requestComplete);
        this.cancel = AddButton("Cancel", "Close the merge editor.", 68, requestCancel);

        if (!session.IsBinary)
        {
            resolutionActions.Add(AddButton("Accept mine", "Keep Mine in the current conflict.", 94, () => AcceptCurrent(MergeChoice.Mine)));
            resolutionActions.Add(AddButton("Accept theirs", "Keep Theirs in the current conflict.", 96, () => AcceptCurrent(MergeChoice.Theirs)));
            resolutionActions.Add(AddButton("Accept both (mine first)", "Keep both versions, with Mine first.", 154, () => AcceptCurrent(MergeChoice.BothMineFirst)));
            resolutionActions.Add(AddButton("Accept both (theirs first)", "Keep both versions, with Theirs first.", 156, () => AcceptCurrent(MergeChoice.BothTheirsFirst)));
            resolutionActions.Add(AddButton("Reset this conflict", "Make the current conflict unresolved again.", 132, () => AcceptCurrent(MergeChoice.Unresolved)));
        }

        if (session.IsBinary && session.Model.BinarySides is { } sides)
        {
            this.complete.Visible = false;
            count.Text = session.SideChoices is { MineDeleted: true } or { TheirsDeleted: true } ? "One side deleted the file" : "Choose a file version";
            AddButton(session.SideChoices is { MineDeleted: true } ? "Delete file" :
                session.SideChoices is { TheirsDeleted: true } ? "Keep file" : "Keep mine",
                Describe(session.MineLabel, sides.Mine, session.MineModified, session.SideChoices?.MineDeleted == true), 104, () => side(MergeSide.Mine));
            AddButton(session.SideChoices is { TheirsDeleted: true } ? "Delete file" :
                session.SideChoices is { MineDeleted: true } ? "Keep file" : "Take theirs",
                Describe(session.TheirsLabel, sides.Theirs, session.TheirsModified, session.SideChoices?.TheirsDeleted == true), 104, () => side(MergeSide.Theirs));
            foreach (var (description, width) in new[]
            {
                (Describe(session.MineLabel, sides.Mine, session.MineModified, session.SideChoices?.MineDeleted == true), 300f),
                (Describe(session.TheirsLabel, sides.Theirs, session.TheirsModified, session.SideChoices?.TheirsDeleted == true), 300f),
            })
            {
                var line = header.Add(new Label { Text = description, Align = Origin.Left, TextScale = 0.135f });
                controls.Add((line, width));
            }
        }

        mine = SideEditor(result);
        theirs = SideEditor(result);
        basis = SideEditor(result);
        minePane = MakePane(session.MineLabel, mine);
        resultPane = MakePane("Result", result);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].IsConflict)
                outlines.Add((i, Edge(), Edge(), Edge(), Edge()));
        }
        theirsPane = MakePane(session.TheirsLabel, theirs);
        basePane = MakePane(session.BaseLabel, basis);
        mineLines = ReadOnlyText(mine, MergeSide.Mine);
        theirLines = ReadOnlyText(theirs, MergeSide.Theirs);
        baseLines = ReadOnlyText(basis, null);

        if (!session.IsBinary)
        {
            text = string.Concat(blocks.Select(block => block.Text));
            result.Text = text;
            Remember();
            result.Document.Changed += TextChanged;
            result.OnLensAbovePressed = (lens, _) => PressLens(lens.Id);
            Decorate();
        }
        else
            body.Visible = false;
        Fit(new(800, 500));
    }

    public Panel Root { get; }
    public MergeSession Session => session;
    public bool IsNarrow => !wide;
    public int ConflictCount => blocks.Count(block => block.IsConflict);
    public int UnresolvedCount => blocks.Count(block => block.IsConflict && !block.Resolved);
    public bool Changed => blocks.Any(block => block.Text != Lines(Initial(block.Region)) || block.IsConflict && block.Resolved);
    public bool CanComplete => !session.IsBinary && complete.Enabled;
    public bool Busy { get; private set; }

    public void SetBusy(bool busy)
    {
        Busy = busy;
        cancel.Enabled = !busy;
        result.ReadOnly = busy;
        foreach (var (item, _) in controls)
            if (item is Button button && !ReferenceEquals(button, cancel))
                button.Enabled = !busy;
        Decorate();
    }

    public void Fit(Vector2 size)
    {
        if (size.X <= 0 || size.Y <= 0)
            return;
        bool wasWide = wide;
        wide = size.X >= 1100;
        if (size == fitted && wide == wasWide)
            return;
        fitted = size;
        float x = Gap, y = Gap;
        Button? firstResolution = resolutionActions.FirstOrDefault();
        foreach (var (item, wanted) in controls)
        {
            if (!item.Visible)
                continue;
            if (ReferenceEquals(item, firstResolution))
            {
                x = Gap;
                y += ControlHeight + 3;
            }
            float width = MathF.Min(wanted, MathF.Max(1, size.X - Gap * 2));
            if (x > Gap && x + width + Gap > size.X)
            {
                x = Gap;
                y += ControlHeight + 3;
            }
            Place(item, x, y, width, ControlHeight);
            x += width + Gap;
        }
        float top = y + ControlHeight + Gap;
        Place(header, 0, 0, size.X, top);
        Place(body, 0, top, size.X, MathF.Max(1, size.Y - top));
        PlacePanes();
        if (wide != wasWide)
            Decorate();
    }

    public void FollowScroll()
    {
        mine.Color = theirs.Color = basis.Color = result.Color;
        PlaceOutlines();
        if (!wide || session.IsBinary || blocks.Count == 0)
            return;
        CodeEditor from = mine.IsFocused ? mine : theirs.IsFocused ? theirs : basis.IsFocused ? basis : result;
        if (from.CodeBounds.Height <= 0)
            return;
        int line = from.PositionAt(new(from.CodeBounds.Min.X + 2, from.CodeBounds.Max.Y - 2)).Line;
        int[] origins = from == result ? ResultStarts() : from == mine ? mineLines : from == theirs ? theirLines : baseLines;
        int region = AtLine(origins, line);
        if (scrolled == (from, region))
            return;
        scrolled = (from, region);
        if (minePane.Visible && from != mine)
            Reveal(mine, mineLines[region]);
        if (theirsPane.Visible && from != theirs)
            Reveal(theirs, theirLines[region]);
        if (basePane.Visible && from != basis)
            Reveal(basis, baseLines[region]);
        if (from != result)
            Reveal(result, ResultStarts()[region]);
    }

    public void Go(int direction)
    {
        if (Busy || ConflictCount == 0)
            return;
        int[] starts = ResultStarts();
        int current = AtLine(starts, result.Document.Caret.Line);
        for (int step = 1; step <= blocks.Count; step++)
        {
            int index = (current + direction * step + blocks.Count) % blocks.Count;
            if (!blocks[index].IsConflict)
                continue;
            selected = index;
            Reveal(result, starts[index]);
            result.Focus();
            return;
        }
    }

    public void AcceptCurrent(MergeChoice choice)
    {
        if (Busy || blocks.Count == 0)
            return;
        int current = AtLine(ResultStarts(), result.Document.Caret.Line);
        if (blocks[current].IsConflict)
            Accept(current, choice);
        else if (blocks[selected].IsConflict)
            Accept(selected, choice);
    }

    internal void Accept(int block, MergeChoice choice)
    {
        if (Busy || blocks[block].Region is not MergeRegion.Conflict conflict)
            return;
        IReadOnlyList<string> lines = choice switch
        {
            MergeChoice.Mine => conflict.MineLines,
            MergeChoice.Theirs => conflict.TheirsLines,
            MergeChoice.BothMineFirst => [.. conflict.MineLines, .. conflict.TheirsLines],
            MergeChoice.BothTheirsFirst => [.. conflict.TheirsLines, .. conflict.MineLines],
            _ => conflict.MarkedLines,
        };
        blocks[block] = blocks[block] with { Text = Lines(lines), Resolved = choice != MergeChoice.Unresolved };
        selected = block;
        WriteBlocks();
        result.GoTo(Math.Min(ResultStarts()[block], result.Document.LineCount - 1));
        result.Focus();
    }

    public byte[] ResultBytes() => session.Model.EncodeResult(result.Text);

    private void AcceptAll(MergeChoice choice)
    {
        if (Busy)
            return;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Region is not MergeRegion.Conflict conflict)
                continue;
            blocks[i] = blocks[i] with { Text = Lines(choice == MergeChoice.Mine ? conflict.MineLines : conflict.TheirsLines), Resolved = true };
        }
        WriteBlocks();
    }

    private void PressLens(int id)
    {
        if (Busy || id <= 0)
            return;
        int index = (id - 1) / 6, action = (id - 1) % 6;
        if (index >= blocks.Count)
            return;
        if (action == 5)
        {
            if (blocks[index].Region is MergeRegion.Clean { BaseLines: { } original })
            {
                blocks[index] = blocks[index] with { Text = Lines(original) };
                WriteBlocks();
            }
        }
        else
            Accept(index, action switch
            {
                0 => MergeChoice.Mine, 1 => MergeChoice.Theirs, 2 => MergeChoice.BothMineFirst,
                3 => MergeChoice.BothTheirsFirst, _ => MergeChoice.Unresolved,
            });
    }

    private void WriteBlocks()
    {
        applying = true;
        try
        {
            text = string.Concat(blocks.Select(block => block.Text));
            result.Document.Replace(text);
            Remember();
        }
        finally { applying = false; }
        Decorate();
    }

    private void TextChanged()
    {
        if (applying)
            return;
        string next = result.Text;
        if (text == next)
            return;
        string key = TextKey(next);
        if (history.TryGetValue(key, out Block[]? known))
        {
            blocks.Clear();
            blocks.AddRange(known);
        }
        else if (blocks.Count > 0)
        {
            int start = 0, oldEnd = text.Length, newEnd = next.Length;
            while (start < oldEnd && start < newEnd && text[start] == next[start])
                start++;
            while (oldEnd > start && newEnd > start && text[oldEnd - 1] == next[newEnd - 1])
            {
                oldEnd--;
                newEnd--;
            }
            ReplaceRange(start, oldEnd, next[start..newEnd]);
            string resolved = string.Concat(blocks.Select(block => block.Text));
            if (resolved != next)
            {
                applying = true;
                try { result.Document.ApplyExternal(resolved, static (_, older, _) => older); }
                finally { applying = false; }
                next = resolved;
                key = TextKey(next);
            }
        }
        text = next;
        Remember(key);
        Decorate();
    }

    private void ReplaceRange(int start, int end, string inserted)
    {
        int offset = 0;
        bool placed = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            int after = offset + block.Text.Length;
            bool owner = !placed && (start < after || i == blocks.Count - 1);
            int from = Math.Clamp(start - offset, 0, block.Text.Length), to = Math.Clamp(end - offset, 0, block.Text.Length);
            if (owner || from < to)
            {
                string edited = block.Text[..from] + (owner ? inserted : "") + block.Text[to..];
                if (!block.Resolved && block.Region is MergeRegion.Conflict conflict)
                    edited = ManualResult(conflict, block.Text[..from].Count(ch => ch == '\n'), edited);
                blocks[i] = block with { Text = edited, Resolved = true };
                if (block.IsConflict)
                    selected = i;
                placed |= owner;
            }
            offset = after;
        }
    }

    private static string ManualResult(MergeRegion.Conflict original, int editedLine, string edited)
    {
        MergeModel parsed = MergeModel.FromMarkers(edited);
        if (parsed.Conflicts.Count != 1)
            return edited;
        var remaining = parsed.Conflicts[0];
        int theirStart = original.MarkedLines.Count - original.TheirsLines.Count - 1;
        int baseStart = original.MineLines.Count + 2;
        remaining.Resolution = editedLine >= theirStart ? new(MergeChoice.Theirs) :
            remaining.BaseLines is { } baseContent && editedLine >= baseStart && editedLine < baseStart + baseContent.Count
                ? new(MergeChoice.Custom, baseContent) : new(MergeChoice.Mine);
        return parsed.ToText();
    }

    private void Remember(string? key = null)
    {
        key ??= TextKey(text);
        if (history.TryAdd(key, [.. blocks]))
            historyOrder.Enqueue(key);
        if (historyOrder.Count > HistoryLimit)
            history.Remove(historyOrder.Dequeue());
    }

    private void Decorate()
    {
        if (session.IsBinary)
            return;
        int unresolved = UnresolvedCount, total = ConflictCount;
        count.Text = $"{unresolved} of {total} conflicts left";
        complete.Enabled = unresolved == 0 && !Busy;
        foreach (Button action in textActions)
            action.Enabled = total > 0 && !Busy;
        foreach (Button action in resolutionActions)
            action.Enabled = total > 0 && !Busy;
        int[] starts = ResultStarts();
        var lenses = new List<UICodeLensAbove>();
        var tint = new List<UICodeLineHighlight>();
        var markers = new List<UICodeLineChange>();
        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            int line = Math.Min(starts[i], result.Document.LineCount - 1);
            int length = Math.Max(1, block.Text.Count(ch => ch == '\n'));
            if (block.Region is MergeRegion.Conflict conflict)
            {
                int id = i * 6 + 1;
                Vector4 color = (block.Resolved ? success() : warning()) with { W = 0.15f };
                tint.Add(new(line, length, color));
                markers.Add(new(line, length, block.Resolved ? UILineChangeKind.Added : UILineChangeKind.Modified));
                if (!wide && !block.Resolved)
                {
                    int previewLine = Math.Min(line + 1, result.Document.LineCount - 1);
                    lenses.Add(new(previewLine, 0, Preview(session.MineLabel, conflict.MineLines), -1));
                    lenses.Add(new(previewLine, 1, Preview(session.TheirsLabel, conflict.TheirsLines), -1));
                }
                lenses.Add(new(line, 2, "Accept mine", id));
                lenses.Add(new(line, 3, "Accept theirs", id + 1));
                lenses.Add(new(line, 4, "Accept both (mine first)", id + 2));
                lenses.Add(new(line, 5, "Accept both (theirs first)", id + 3));
                lenses.Add(new(line, 6, "Reset this conflict", id + 4));
            }
            else if (block.Region is MergeRegion.Clean { BaseLines: not null })
            {
                tint.Add(new(line, length, success() with { W = 0.13f }));
                lenses.Add(new(line, 0, "Use base", i * 6 + 6));
            }
        }
        result.LineHighlights = tint;
        result.LineChanges = markers;
        result.LensesAbove = lenses;
        ShadeSide(mine, mineLines);
        ShadeSide(theirs, theirLines);
        ShadeSide(basis, baseLines);
    }

    private void ShadeSide(CodeEditor editor, int[] starts)
    {
        var tinted = new List<UICodeLineHighlight>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Region is not MergeRegion.Conflict && blocks[i].Region is not MergeRegion.Clean { BaseLines: not null })
                continue;
            int end = i + 1 == starts.Length ? editor.Document.LineCount - 1 : starts[i + 1];
            if (end > starts[i])
                tinted.Add(new(starts[i], end - starts[i], (blocks[i].IsConflict ? warning() : success()) with { W = 0.13f }));
        }
        editor.LineHighlights = tinted;
    }

    private Panel Edge() => resultPane.Add(new Panel { Color = Vector4.Zero });

    private void PlaceOutlines()
    {
        if (outlines.Count == 0 || result.CodeBounds.IsEmpty)
            return;
        int[] starts = ResultStarts();
        float left = result.CodeBounds.Min.X - resultPane.Bounds.Min.X, width = result.CodeBounds.Width;
        float lineHeight = MathF.Max(1f, result.CaretLineHeight);
        foreach (var (index, top, bottom, first, last) in outlines)
        {
            Block block = blocks[index];
            int start = Math.Min(starts[index], result.Document.LineCount - 1);
            int end = Math.Clamp(start + block.Text.Count(ch => ch == '\n') - 1, start, result.Document.LineCount - 1);
            float upper = result.PlaceOf(new(start, 0)).Y + lineHeight * 0.5f;
            float lower = result.PlaceOf(new(end, 0)).Y - lineHeight * 0.5f;
            bool visible = !block.Resolved && upper > result.CodeBounds.Min.Y && lower < result.CodeBounds.Max.Y;
            top.Visible = bottom.Visible = first.Visible = last.Visible = visible;
            if (!visible)
                continue;
            float y = resultPane.Bounds.Max.Y - MathF.Min(upper, result.CodeBounds.Max.Y);
            float height = MathF.Min(upper, result.CodeBounds.Max.Y) - MathF.Max(lower, result.CodeBounds.Min.Y);
            Vector4 color = warning() with { W = 0.68f };
            top.Color = bottom.Color = first.Color = last.Color = color;
            Place(top, left, y, width, 1);
            Place(bottom, left, y + MathF.Max(0, height - 1), width, 1);
            Place(first, left, y, 1, height);
            Place(last, left + width - 1, y, 1, height);
        }
    }

    private void ToggleBase()
    {
        showingBase = !showingBase;
        if (showBase is not null)
            showBase.Selected = showingBase;
        PlacePanes();
    }

    private void PlacePanes()
    {
        resultPane.Visible = !session.IsBinary;
        basePane.Visible = !session.IsBinary && showingBase;
        minePane.Visible = wide && !showingBase && !session.IsBinary;
        theirsPane.Visible = minePane.Visible;
        if (!resultPane.Visible)
            return;
        float width = body.Size.X, height = body.Size.Y;
        if (basePane.Visible)
        {
            if (wide)
            {
                float half = (width - Gap) / 2;
                Place(basePane, 0, 0, half, height);
                Place(resultPane, half + Gap, 0, half, height);
            }
            else
            {
                float upper = MathF.Min(150, height / 3);
                Place(basePane, 0, 0, width, upper);
                Place(resultPane, 0, upper + Gap, width, MathF.Max(1, height - upper - Gap));
            }
        }
        else if (wide)
        {
            float side = (width - Gap * 2) * 0.30f;
            Place(minePane, 0, 0, side, height);
            Place(resultPane, side + Gap, 0, width - 2 * (side + Gap), height);
            Place(theirsPane, width - side, 0, side, height);
        }
        else
            Place(resultPane, 0, 0, width, height);

        foreach (var (pane, editor) in new[] { (minePane, mine), (resultPane, result), (theirsPane, theirs), (basePane, basis) })
            Place(editor, 0, PaneLabelHeight, pane.Size.X, MathF.Max(1, pane.Size.Y - PaneLabelHeight));
    }

    private Panel MakePane(string label, CodeEditor editor)
    {
        var pane = body.Add(PlainPanel());
        var title = pane.Add(new Label { Text = label, Align = Origin.Left, TextScale = 0.15f });
        Place(title, 8, 0, 350, PaneLabelHeight);
        pane.Add(editor);
        return pane;
    }

    private Button AddButton(string label, string tooltip, float width, Action pressed)
    {
        var button = header.Add(new Button { Label = label, Tooltip = tooltip, OnPressed = pressed, Style = "button_flat" });
        controls.Add((button, width));
        return button;
    }

    private int[] ReadOnlyText(CodeEditor editor, MergeSide? side)
    {
        var lines = new List<string>();
        var starts = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            starts[i] = lines.Count;
            var region = blocks[i].Region;
            lines.AddRange(region switch
            {
                MergeRegion.Conflict conflict => side switch
                {
                    MergeSide.Mine => conflict.MineLines, MergeSide.Theirs => conflict.TheirsLines, _ => conflict.BaseLines ?? [],
                },
                MergeRegion.Clean clean => side is null ? clean.BaseLines ?? clean.Lines : clean.Lines,
                _ => [],
            });
        }
        string[]? original = session.Model.Originals is { } sources ? side switch
        {
            MergeSide.Mine => sources.Mine, MergeSide.Theirs => sources.Theirs, _ => sources.Base,
        } : null;
        if (original is not null)
            MergeModel.AlignStarts(lines, original, starts);
        editor.Text = Lines(original ?? (IReadOnlyList<string>)lines);
        return starts;
    }

    private int[] ResultStarts()
    {
        var starts = new int[blocks.Count];
        int line = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            starts[i] = line;
            line += blocks[i].Text.Count(ch => ch == '\n');
        }
        return starts;
    }

    private static IReadOnlyList<string> Initial(MergeRegion region) =>
        region is MergeRegion.Clean clean ? clean.Lines : ((MergeRegion.Conflict)region).MarkedLines;

    private static string Preview(string label, IReadOnlyList<string> lines) =>
        label + "  " + (lines.Count == 0 ? "(empty)" : string.Join("  ", lines));

    private static string TextKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Lines(IReadOnlyList<string> lines) => lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";

    private static int AtLine(int[] starts, int line)
    {
        int index = 0;
        while (index + 1 < starts.Length && starts[index + 1] <= line)
            index++;
        return index;
    }

    private static void Reveal(CodeEditor editor, int line)
    {
        editor.GoTo(Math.Clamp(line, 0, editor.Document.LineCount - 1));
        editor.RevealCaret(center: true);
    }

    private static string Describe(string label, byte[] bytes, DateTimeOffset? modified, bool deleted) =>
        deleted ? $"{label}  Deleted" : modified is { } date
            ? $"{label}  {bytes.LongLength:N0} bytes · {date.ToLocalTime():g}" : $"{label}  {bytes.LongLength:N0} bytes";

    private static Panel PlainPanel() => new() { Color = Vector4.Zero, Clip = true };

    private static CodeEditor SideEditor(CodeEditor reference) => new()
    {
        Highlighter = reference.Highlighter, TextScale = reference.TextScale, Font = reference.Font,
        Color = reference.Color, ReadOnly = true, CheckSyntax = false, Minimap = false,
        WordWrap = false, StickyScroll = false, Padding = reference.Padding,
    };

    private static void Place(UIComponent item, float x, float y, float width, float height)
    {
        item.Fill = UIFill.None;
        item.Anchor = Origin.TopLeft;
        item.Pivot = Origin.TopLeft;
        item.Position = new Vector2(x, -y);
        item.Size = new Vector2(width, height);
    }
}
