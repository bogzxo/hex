using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Properties panel wired into the editor (HexProperties), Alt+Enter and Properties on the explorer's and the tabs'
// right click, the explorer's selection followed, Ctrl and Shift with a click picking several files.
internal sealed partial class DawnScene
{
    private HexProperties properties = null!;

    private void BuildProperties()
    {
        properties = new HexProperties(Context, right, rightHead, inspectorScroll)
        {
            Git = git,
            PickLanguage = PickLanguage,
            ChangeLanguageMode = ChangeLanguageMode,
            Rename = explorer.RenameTo,
            FilterRows = designer.FilterRows
        };
        explorer.PropertiesOf = path => ShowPropertiesOf(SelectedWith(path));

        // The explorer picks several rows with Ctrl and Shift, and the panel follows what's picked, a folder pressed too
        sideTree.MultiSelect = true;
        sideTree.OnSelectionChanged = picked =>
        {
            if (Side == HexSide.Explorer)
                properties.Follow([.. picked.Select(item => item.Tooltip).Where(path => path.Length > 0)]);
        };
        Action<UITreeItem>? pressed = sideTree.OnPressed;
        sideTree.OnPressed = item =>
        {
            pressed?.Invoke(item);
            if (Side == HexSide.Explorer && item.Expandable && Directory.Exists(item.Tooltip))
                properties.Follow([item.Tooltip]);
        };

        commands.Add("View/toggles", Said("file_properties.command"), () => ShowPropertiesOf(PropertiesSubject()), "Alt+Enter", "info", UIKeyUse.BoundWhileTyping);
    }

    // The rows selected in the explorer when one of them is right clicked, or that one alone
    private List<string> SelectedWith(string path)
    {
        List<string> selected = [.. sideTree.SelectedItems.Select(item => item.Tooltip).Where(each => each.Length > 0)];
        return selected.Contains(path, PlatformPaths.Comparer) ? selected : [path];
    }

    // What Alt+Enter is about, the explorer's selection while it has the keyboard, otherwise what's on screen (null)
    private List<string>? PropertiesSubject() =>
        compositor.Focus == sideTree && Side == HexSide.Explorer && sideTree.SelectedItems.Count > 0
            ? [.. sideTree.SelectedItems.Select(item => item.Tooltip).Where(path => path.Length > 0)]
            : null;

    /// <summary>The Properties panel out on the right showing the properties of some files, or of what's on screen for none.</summary>
    private void ShowPropertiesOf(IReadOnlyList<string>? paths)
    {
        properties.PreferProperties();
        properties.Pinned = true;
        properties.Follow(paths);
        if (!right.Shown)
            ToggleProperties();
        inspectorDirty = true;
    }
}
