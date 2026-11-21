using System.Globalization;
using System.Numerics;

using Hex.Core.Git;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The head of the properties of files, what they are before any row says it: the icon the explorer gives it in the same
// colour, its name, where it is in the workspace, what git says of it, and what's done with a file from here (open it,
// copy where it is, show it in the system's file explorer, open it with the program the system has for it). The rows
// under it are the facts, grouped, the head is the one thing that leads. Several picked say how many and where.
internal sealed partial class HexProperties
{
    private StackPanel head = null!;

    // The side's own title, the name of the panel and its button that puts it away, over everything
    private UIComponent title = null!;
    private Symbol headIcon = null!;
    private Label headName = null!, headWhere = null!;
    private Badge headGit = null!;
    private Button openButton = null!, copyButton = null!, copyRelativeButton = null!, revealButton = null!, systemButton = null!;

    // The name and the place as they were worked out, cut to the room the head has when it's laid out
    private string headNameWhole = string.Empty, headWhereWhole = string.Empty;
    private float headFittedFor = -1.0f;

    // How tall the head is, its icon and two lines, then its buttons
    private const float HEAD_HEIGHT = 74.0f, HEAD_ICON = 30.0f, HEAD_GAP = 10.0f, ACTION_SIZE = 28.0f;
    private const float NAME_SCALE = 0.165f, WHERE_SCALE = 0.125f;

    /// <summary>A file's icon and its colour, the explorer's.</summary>
    internal Func<string, (string Icon, Vector4? Color)>? IconOf { get; set; }

    /// <summary>Where something is inside the workspace's folder it's in, the whole path for one outside them all.</summary>
    internal Func<string, string>? RelativeOf { get; set; }

    /// <summary>Something shown picked in the system's own file explorer.</summary>
    internal Action<string>? Reveal { get; set; }

    /// <summary>A file opened with the program the system has for it.</summary>
    internal Action<string>? OpenWithSystem { get; set; }

    /// <summary>Where the head is on screen, for a test.</summary>
    internal UIRect HeadBounds => head.Bounds;

    /// <summary>The head, for a test.</summary>
    internal (string Name, string Where, string Git, bool Actions) HeadShown => (headNameWhole, headWhereWhole, headGit.Visible ? headGit.Text : string.Empty, copyButton.Visible);

    private void BuildHead(StackPanel right)
    {
        headIcon = new Symbol { Size = new Vector2(HEAD_ICON), Anchor = Origin.Left, Pivot = Origin.Left };
        headName = new Label { TextScale = NAME_SCALE, Bold = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left };
        headWhere = new Label { TextScale = WHERE_SCALE, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left };
        headGit = new Badge { TextScale = 0.11f, Visible = false, Anchor = Origin.Left, Pivot = Origin.Left };

        var where = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        where.Add(headWhere);
        where.Add(headGit);
        var lines = new StackPanel { Spacing = 3.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        lines.Add(headName);
        lines.Add(where);
        var top = new StackPanel { Direction = UIDirection.Horizontal, Spacing = HEAD_GAP, Anchor = Origin.Left, Pivot = Origin.Left };
        top.Add(headIcon);
        top.Add(lines);

        // The same flat square buttons as the explorer's at the end of the side bar's title, so the two read as one
        Button Action(string icon, string tip, Action pressed) => new()
        {
            Icon = icon,
            Style = "button_flat",
            Size = new Vector2(ACTION_SIZE, ACTION_SIZE - 2.0f),
            Animated = false,
            Tooltip = Said(tip),
            OnPressed = pressed
        };

        openButton = Action("go-to-file", "open_tip", () => Each(path => context.Documents.Open(path), files: true));
        copyButton = Action("link", "copy_path_tip", () => Copy(relative: false));
        copyRelativeButton = Action("file-symlink-file", "copy_relative_tip", () => Copy(relative: true));
        revealButton = Action("link-external", "reveal_tip", () => Each(path => Reveal?.Invoke(path), files: false));
        systemButton = Action("window", "open_with_tip", () => Each(path => OpenWithSystem?.Invoke(path), files: true));
        var actions = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        foreach (Button button in (Button[])[openButton, copyButton, copyRelativeButton, revealButton, systemButton])
            actions.Add(button);

        head = new StackPanel { Spacing = 8.0f, Padding = new UIEdges(6.0f, 4.0f, 6.0f, 2.0f), Visible = false, Anchor = Origin.Top, Pivot = Origin.Top };
        head.Add(top);
        head.Add(actions);
        right.Add(head);
    }

    // The side's title, the head, the designer's filter and the rows, first and in that order down the side whatever the
    // side's parts were put in as. Moved only when they aren't, so a rebuild in order moves nothing
    private void KeepHeadOverPage()
    {
        if (head.Parent is not { } side)
            return;

        UIComponent[] order = [title, head, filter, page];
        IReadOnlyList<UIComponent> parts = side.Children;
        bool sorted = parts.Count >= order.Length;
        for (int i = 0; i < order.Length && sorted; i++)
            sorted = parts[i] == order[i];
        if (sorted)
            return;

        for (int i = 0; i < order.Length; i++)
            side.MoveChild(order[i], i);
    }

    // What the head's buttons are for, the files and folders shown, the open button's only the files
    private void Each(Action<string> act, bool files)
    {
        foreach (string path in shown)
        {
            if (!files || File.Exists(path))
                act(path);
        }
    }

    // Where they all are copied, a line each, relative to the workspace's folder or whole
    private void Copy(bool relative)
    {
        if (shown.Count == 0)
            return;

        string said = string.Join(System.Environment.NewLine, shown.Select(path => relative && RelativeOf is { } of ? of(path) : path));
        UIClipboard.Copy(said);
        context.Notices.Say(context.Strings.Said("explorer_files.path_copied", ("path", shown.Count == 1 ? said : HexText.Count("path", shown.Count))));
    }

    /// <summary>Git said something new, the rows and the head of what's shown say it too.</summary>
    internal void GitChanged()
    {
        if (!page.Visible)
            return;

        rowsStale = true;
        context.Window.RefreshRight();
    }

    /// <summary>The head said again for what's shown, with the panel's rows.</summary>
    private void FillHead(HexDocument document)
    {
        head.Visible = true;
        int files = shown.Count(File.Exists), folders = shown.Count - files;
        string first = shown.Count > 0 ? shown[0] : document.Path ?? document.Name;
        bool folder = shown.Count == 1 && folders == 1;

        (string icon, Vector4? color) = shown.Count > 1 ? ("files", null)
            : folder ? ("folder-opened", null)
            : IconOf?.Invoke(first) ?? ("file", null);
        headIcon.Icon = icon;
        headIcon.Color = color;

        if (shown.Count > 1)
        {
            headNameWhole = files == 0 ? Said("several_folders", ("count", folders.ToString(CultureInfo.InvariantCulture)))
                : folders == 0 ? Said("several_files", ("count", files.ToString(CultureInfo.InvariantCulture)))
                : Said("count_items", ("count", shown.Count.ToString(CultureInfo.InvariantCulture)));
            headWhereWhole = Said("all_in", ("folder", FolderSaid(CommonFolder(shown))));
        }
        else if (shown.Count == 1)
        {
            headNameWhole = Path.GetFileName(first.TrimEnd('\\', '/'));
            headWhereWhole = Path.GetDirectoryName(first.TrimEnd('\\', '/')) is { } parent ? FolderSaid(parent) : first;
        }
        else
        {
            headNameWhole = document.Name;
            headWhereWhole = Said("not_saved");
        }

        // What git says of them, in the colour Source Control and the explorer give it
        headGit.Visible = false;
        if (Git is { } git && shown.Count > 0 && git.StatusFor(shown[0]) is not null)
        {
            GitChange worst = GitChange.None;
            foreach (string path in shown)
            {
                GitChange change = Directory.Exists(path) ? git.FolderChange(path) : git.StateOf(path).Change;
                if (Weight(change) > Weight(worst))
                    worst = change;
            }

            headGit.Visible = worst is not GitChange.None;
            headGit.Text = StatusText(new GitFileState(worst, false, false));
            headGit.Tone = worst switch
            {
                GitChange.Modified or GitChange.Renamed => UIBadgeTone.Warning,
                GitChange.Added or GitChange.Untracked => UIBadgeTone.Success,
                GitChange.Deleted or GitChange.Conflicted => UIBadgeTone.Danger,
                _ => UIBadgeTone.Neutral
            };
        }

        // A file of nothing on disk has nothing to copy or show
        bool onDisk = shown.Count > 0;
        copyButton.Visible = copyRelativeButton.Visible = revealButton.Visible = onDisk;
        openButton.Visible = systemButton.Visible = onDisk && files > 0;
        openButton.Enabled = shown.Any(path => File.Exists(path) && !PlatformPaths.Same(path, context.Documents.Current.Path ?? string.Empty));
        headFittedFor = -1.0f;
    }

    // How much a change weighs against another, the strongest of several said
    private static int Weight(GitChange change) => change switch
    {
        GitChange.None => 0,
        GitChange.Ignored => 1,
        GitChange.Untracked => 2,
        GitChange.Added or GitChange.Renamed => 3,
        GitChange.Modified => 4,
        GitChange.Deleted => 5,
        _ => 6
    };

    // A folder as the head says it, from the workspace's folder's name down with forward slashes, whole when outside it
    private string FolderSaid(string folder)
    {
        if (RelativeOf?.Invoke(folder) is not { } inside || Path.IsPathFullyQualified(inside))
            return folder;

        return inside;
    }

    // The name and the place cut to the room the head has, once a width
    private void FitHead(float width)
    {
        if (!head.Visible || page.Module?.Compositor.Skin is not { } skin || width == headFittedFor)
            return;

        headFittedFor = width;
        float room = width - head.Padding.Left - head.Padding.Right - HEAD_ICON - HEAD_GAP;
        headName.Text = HexFit.Cut(skin, headNameWhole, NAME_SCALE, bold: true, room);
        float badge = headGit.Visible ? skin.Font.Measure(headGit.Text, 0.11f, markup: false).X + 30.0f : 0.0f;
        headWhere.Text = HexFit.Cut(skin, headWhereWhole, WHERE_SCALE, bold: false, room - badge);
        headName.Tooltip = ReferenceEquals(headName.Text, headNameWhole) ? string.Empty : headNameWhole;
        headWhere.Tooltip = ReferenceEquals(headWhere.Text, headWhereWhole) ? string.Empty : headWhereWhole;
    }
}

/// <summary>Text cut short with dots to the room it has, a title or a name in a row too narrow for it.</summary>
internal static class HexFit
{
    private const string CUT = "...";

    /// <summary>As much of some text as fits in so much room with dots after it, the text itself when it all fits.</summary>
    internal static string Cut(UISkin skin, string text, float scale, bool bold, float room)
    {
        float Width(ReadOnlySpan<char> part) => skin.Font.MeasureLine(part, scale, bold ? UITextStyle.Bold : UITextStyle.None, default, markup: false);

        if (room <= 0.0f || Width(text) <= room)
            return text;

        float dots = Width(CUT);
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Width(text.AsSpan(0, middle)) + dots <= room)
                low = middle;
            else
                high = middle - 1;
        }

        return text[..low].TrimEnd() + CUT;
    }
}
