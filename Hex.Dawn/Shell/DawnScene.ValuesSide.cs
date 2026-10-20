namespace Hex.Dawn;

// The right side for a file that has nothing to show on it. A file of HIDL has its values there and a layout its
// properties, a C# file or a Markdown one has neither, and the side took a third of the window to say so. It folds away
// for such a file and comes back for the next that has something, as the settings have it, without the settings being
// changed (Ctrl+Alt+B still puts it up for a file that hasn't).
internal sealed partial class DawnScene
{
    // whether the side was folded because of the file rather than by somebody
    private bool valuesFoldedForFile;

    private void FoldValuesFor(HexDocument shown)
    {
        bool nothing = shown.IsText && shown.Path is { } path && !path.EndsWith(".hor", StringComparison.OrdinalIgnoreCase);
        if (nothing && right.Shown)
        {
            valuesFoldedForFile = true;
            right.Show(false);
        }
        else if (!nothing && valuesFoldedForFile)
        {
            valuesFoldedForFile = false;
            right.Show(!settings.PropertiesHidden);
        }
    }
}
