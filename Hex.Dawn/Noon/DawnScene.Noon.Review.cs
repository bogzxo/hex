using System.Globalization;
using System.Numerics;

using Hex.Core.Git;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// A file of a turn reviewed in place in Changes, its hunks against the turn's checkpoint as cards under its row, the lines
// taken out in red and put in in green. Keep puts a hunk out of the way as looked at, Undo puts its lines back as they were
// before the turn and leaves the rest of the agent's changes where they are. Before, a file could only be seen in a diff
// tab and put back whole. The hunks are worked out off the editor's thread and kept until the file changes on the disk.
internal sealed partial class DawnScene
{
    // The most lines of a hunk shown, a longer one says how many more and is seen whole in the diff tab
    private const int REVIEW_LINES = 40;

    // The files being reviewed, by the turn and the path, and the hunks worked out for each with the time of the file then
    private readonly HashSet<(HexAgentEntry Asked, string Path)> reviewing = [];
    private readonly Dictionary<(HexAgentEntry Asked, string Path), (DateTime Written, List<(GitHunk Hunk, string[] Old, string[] New)>? Hunks)> reviewHunks = [];

    // The hunks kept, by the file and their lines, put out of the way until the file changes them again
    private readonly HashSet<string> reviewKept = [];

    private static string ReviewKey(string path, string[] old, string[] made) =>
        $"{path}\n{string.Join('\n', old).GetHashCode(StringComparison.Ordinal)}\n{string.Join('\n', made).GetHashCode(StringComparison.Ordinal)}";

    /// <summary>The button on a file's row in Changes that opens its hunks under it, or closes them.</summary>
    private Button ReviewButton(HexAgentEntry asked, string path)
    {
        bool open = reviewing.Contains((asked, path));
        return new Button
        {
            Label = Said(open ? "noon.review_close" : "noon.review"), Icon = open ? "chevron-up" : "eye", Style = "button_flat", Size = new Vector2(84, 22), LabelScale = 0.12f, Animated = false,
            Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-168, 0), Selected = open,
            OnPressed = () =>
            {
                if (!reviewing.Remove((asked, path)))
                    reviewing.Add((asked, path));
                noonStageDrawn = NOT_DRAWN;
            }
        };
    }

    /// <summary>The hunks of a file being reviewed, under its row, worked out in the background the first time and when the file moved.</summary>
    private void DrawReview(StackPanel card, HexAgentEntry asked, string path)
    {
        if (!reviewing.Contains((asked, path)) || CheckpointFor(asked, path) is not { } at)
            return;

        DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (!reviewHunks.TryGetValue((asked, path), out var known) || known.Written != written)
        {
            reviewHunks[(asked, path)] = (written, null);
            InBackground("review", () => HexHunkReview.Hunks(HexCheckpoints.TextAt(at.Top, at.Tree, path) ?? string.Empty, File.Exists(path) ? File.ReadAllText(path) : string.Empty), hunks =>
            {
                reviewHunks[(asked, path)] = (written, hunks);
                noonStageDrawn = NOT_DRAWN;
            }, quiet: true);
            card.Add(ContextText(Said("noon.review_working"), dim: true));
            return;
        }

        if (known.Hunks is not { } all)
        {
            card.Add(ContextText(Said("noon.review_working"), dim: true));
            return;
        }

        var left = all.Where(each => !reviewKept.Contains(ReviewKey(path, each.Old, each.New))).ToList();
        if (left.Count == 0)
        {
            card.Add(ContextText(Said(all.Count == 0 ? "noon.review_none" : "noon.review_all_kept"), dim: true));
            return;
        }

        var column = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 6, Padding = new UIEdges(18, 2, 0, 6) });
        Vector4 added = Skin?.Palette.Success ?? new Vector4(0.35f, 0.8f, 0.45f, 1.0f);
        Vector4 taken = Skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f);
        foreach (var (hunk, old, made) in left)
            column.Add(ReviewHunk(asked, path, hunk, old, made, added, taken));
    }

    // A hunk as a card, where it is and its two buttons along the top, its lines under them
    private Panel ReviewHunk(HexAgentEntry asked, string path, GitHunk hunk, string[] old, string[] made, Vector4 added, Vector4 taken)
    {
        Panel card = StripedCard(hunk.IsDeleted ? taken : hunk.IsAdded ? added : Skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f), 8.0f);
        var stack = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });
        var head = stack.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
        string where = made.Length == 0
            ? Said("noon.review_at", ("line", (hunk.NewStart + 1).ToString(CultureInfo.InvariantCulture)))
            : Said("noon.review_lines", ("from", (hunk.NewStart + 1).ToString(CultureInfo.InvariantCulture)), ("to", (hunk.NewStart + made.Length).ToString(CultureInfo.InvariantCulture)));
        head.Add(new Label { Text = $"{UIRichText.Literal(where)}  [color:{HexColour(taken)}]−{old.Length}[/color] [color:{HexColour(added)}]+{made.Length}[/color]", TextScale = 0.12f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
        head.Add(new Button
        {
            Label = Said("noon.review_undo"), Icon = "discard", Style = "button_flat", Danger = true, Size = new Vector2(78, 22), LabelScale = 0.12f, Animated = false,
            Anchor = Origin.Right, Pivot = Origin.Right, Tooltip = Said("noon.review_undo_tip"), OnPressed = () => UndoHunk(asked, path, old, made)
        });
        head.Add(new Button
        {
            Label = Said("noon.review_keep"), Icon = "check", Style = "button_flat", Size = new Vector2(72, 22), LabelScale = 0.12f, Animated = false,
            Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-82, 0), Tooltip = Said("noon.review_keep_tip"),
            OnPressed = () =>
            {
                reviewKept.Add(ReviewKey(path, old, made));
                noonStageDrawn = NOT_DRAWN;
            }
        });

        // The lines taken out over the ones put in, as a unified diff writes them, cut to a screen's worth
        var lines = new List<string>();
        foreach (string line in old.Take(REVIEW_LINES))
            lines.Add($"[color:{HexColour(taken)}]- {UIRichText.Literal(line)}[/color]");
        foreach (string line in made.Take(Math.Max(0, REVIEW_LINES - Math.Min(old.Length, REVIEW_LINES))))
            lines.Add($"[color:{HexColour(added)}]+ {UIRichText.Literal(line)}[/color]");
        int hidden = old.Length + made.Length - lines.Count;
        if (hidden > 0)
            lines.Add(UIRichText.Literal(Said("noon.review_more", ("count", hidden.ToString(CultureInfo.InvariantCulture)))));
        stack.Add(new Label { Text = string.Join('\n', lines), TextScale = 0.13f, Align = Origin.Left, Fill = UIFill.Horizontal }).Also(label => label.CodeFont = code.Font);
        return card;
    }

    /// <summary>One hunk of a file put back as it was before the turn, off the editor's thread, the rest of the file left as it is.</summary>
    private void UndoHunk(HexAgentEntry asked, string path, string[] old, string[] made)
    {
        if (CheckpointFor(asked, path) is not { } at)
            return;
        InBackground("review", () =>
        {
            string before = HexCheckpoints.TextAt(at.Top, at.Tree, path) ?? string.Empty;
            string now = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (HexHunkReview.Undo(before, now, old, made) is not { } undone)
                return false;
            File.WriteAllText(path, undone);
            return true;
        }, done =>
        {
            Say(Said(done ? "noon.review_undone" : "noon.review_gone", ("name", Path.GetFileName(path))));
            reviewHunks.Remove((asked, path));
            noonStageDrawn = NOT_DRAWN;
        });
    }
}
