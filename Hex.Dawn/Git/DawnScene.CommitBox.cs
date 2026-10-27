using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The box over the Source Control side, VS Code's. The commit's message typed straight in, as many lines as it takes
// (Enter a new line, Ctrl+Enter commits), and Commit under it as a real button saying what it'd take, with the other
// ways of committing behind the chevron next to it. It's the active repository's. Commit with nothing typed asks for
// the message the way the Git menu's Commit always has, and whatever's typed is the message of a commit asked for from
// the menu or the keys too.
internal sealed partial class DawnScene
{
    // How many lines of the message the box shows at least and at most before it scrolls
    private const int BOX_LEAST_LINES = 2, BOX_MOST_LINES = 8;

    // How tall the Commit row is, and how wide the chevron at its end
    private const float BOX_BUTTON = 28.0f, BOX_MORE = 28.0f;

    private StackPanel? commitBox;
    private Panel commitWell = null!;
    private CodeEditor commitMessage = null!;
    private Label commitPlaceholder = null!;
    private Button commitButton = null!, commitMore = null!;

    // The repository the box is for, by root, so a message typed for one isn't committed to another, and what was typed
    // for the others while it was on this one
    private string? commitBoxFor;
    private readonly Dictionary<string, string> commitDrafts = new(PlatformPaths.Comparer);

    /// <summary>The box over the tree, made the first time Source Control is shown, and shown only there while there's a repository.</summary>
    private void PlaceCommitBox(bool shown)
    {
        var state = shown ? git.CommitBox() : null;
        if (commitBox is null)
        {
            if (state is null)
                return;
            BuildCommitBox();
        }

        commitBox!.Visible = state is not null;
        if (state is not { } box)
            return;

        // Another repository became the active one, what was typed is kept for the last and what was typed for this one
        // comes back, a message a repository each
        if (commitBoxFor != box.Root)
        {
            if (commitBoxFor is { } was)
                commitDrafts[was] = commitMessage.Text;
            commitBoxFor = box.Root;
            commitMessage.Text = commitDrafts.GetValueOrDefault(box.Root) ?? string.Empty;
        }

        commitButton.Label = box.Button;
        commitButton.Primary = box.Ready;
        commitPlaceholder.Text = box.Placeholder;
        ShowCommitPlaceholder();
    }

    private void BuildCommitBox()
    {
        commitBox = new StackPanel { Fill = UIFill.Horizontal, Spacing = 6.0f, Visible = false };

        commitWell = commitBox.Add(new Panel { Fill = UIFill.Horizontal, Radius = 6.0f, Clip = true });
        commitMessage = commitWell.Add(new CodeEditor
        {
            Fill = UIFill.Both,
            LineNumbers = false,
            WordWrap = true,
            StickyScroll = false,
            IndentGuides = false,
            Minimap = false,
            CheckSyntax = false,
            TextScale = CODE_TEXT,
            Highlighter = CodeLanguages.ForFile("COMMIT_EDITMSG.md")
        });
        commitPlaceholder = commitWell.Add(new Label
        {
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Position = new Vector2(8.0f, -6.0f),
            TextScale = 0.13f,
            Color = DimColor,
            Align = Origin.TopLeft
        });

        // Commit as wide as the side less the chevron at its end, the way VS Code's button and its dropdown sit
        var row = commitBox.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0.0f, BOX_BUTTON), Padding = new UIEdges(0.0f, 0.0f, BOX_MORE + 2.0f, 0.0f) });
        commitButton = row.Add(new Button { Fill = UIFill.Horizontal, Size = new Vector2(0.0f, BOX_BUTTON), Icon = "git-commit", LabelScale = 0.13f, Tooltip = Said("git.commit_tip") });
        commitMore = row.Add(new Button
        {
            Anchor = Origin.Right,
            Pivot = Origin.Right,

            // Out in the room the row leaves past the end of Commit, which is in its padding
            Position = new Vector2(BOX_MORE + 2.0f, 0.0f),
            Size = new Vector2(BOX_MORE, BOX_BUTTON),
            Icon = "chevron-down",
            Style = "button_flat",
            Tooltip = Said("git.commit_more")
        });

        commitButton.OnPressed = git.CommitFromBox;
        commitMore.OnPressed = git.BoxMenu;
        commitMessage.OnChanged = () =>
        {
            ShowCommitPlaceholder();
            FitCommitBox();
        };
        commitMessage.OnControlEnter = () =>
        {
            git.CommitFromBox();
            return true;
        };

        // A tick of its own, so the edge of the box lights while it has the keys the way a text box does
        commitBox.Add(new HexTicker { Visible = false, Ticked = _ => FitCommitBox() });

        // Over the tree, the same place the search view's boxes have
        left.Add(commitBox);
        int tree = -1;
        for (int i = 0; i < left.Children.Count && tree < 0; i++)
        {
            if (left.Children[i] == sideTree)
                tree = i;
        }
        left.MoveChild(commitBox, tree);
        FitCommitBox();
    }

    /// <summary>What the box says while nothing's typed, gone as soon as something is.</summary>
    private void ShowCommitPlaceholder() => commitPlaceholder.Visible = commitMessage.Text.Length == 0;

    /// <summary>The box as tall as the message's lines, between its least and its most.</summary>
    private void FitCommitBox()
    {
        if (commitBox is null || compositor.Skin is not { } skin)
            return;

        commitWell.Color = skin.FieldColor;
        commitWell.Outline = commitMessage.IsFocused ? skin.Palette.Highlight : skin.BorderColor;
        commitMessage.Color = skin.FieldColor;
        int lines = Math.Clamp(commitMessage.RowCount, BOX_LEAST_LINES, BOX_MOST_LINES);
        float tall = MathF.Ceiling(lines * (commitMessage.Font ?? skin.Font).LineHeight * CODE_TEXT + 12.0f);
        if (commitWell.Size.Y != tall)
            commitWell.Size = commitWell.Size with { Y = tall };
    }

    /// <summary>The text of the box for a repository, null when it's for another or nothing's typed.</summary>
    private string? CommitBoxMessage(string root) =>
        commitBox is not null && commitBoxFor is { } at && PlatformPaths.Comparer.Equals(at, root) && commitMessage.Text.Trim().Length > 0 ? commitMessage.Text : null;

    /// <summary>A commit went in, the box it was typed in emptied for the next.</summary>
    private void CommitBoxCommitted(string root)
    {
        commitDrafts.Remove(root);
        if (commitBox is not null && commitBoxFor is { } at && PlatformPaths.Comparer.Equals(at, root))
            commitMessage.Text = string.Empty;
    }
}
