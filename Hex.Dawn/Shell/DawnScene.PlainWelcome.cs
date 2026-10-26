using Horizon.UI.Components;

namespace Hex.Dawn;

// The welcome for a start on code, see DawnScene.Welcome. Whether the folders have layouts is a cheap look at their top
// and the folders right under it, nothing deeper, it's done as Hex starts.
internal sealed partial class DawnScene
{
    // Hex was started on code or on nothing, the welcome comes up without the designer round it
    private bool plainWelcome;

    // The empty layout last put in its code view for the plain welcome, so it's done once for it
    private HexDocument? plainFor;

    // Whether the toolbar and the Properties are put away for the plain welcome right now
    private bool plainShown;

    // The welcome over the code panel, the plain one, made the first time it is wanted
    private Panel? plainPage;

    // How many folders under the top of one are looked in for a layout, a folder of thousands isn't gone through
    private const int LAYOUT_LOOK_FOLDERS = 64;

    // Folders that are never where somebody's layouts are
    private static readonly string[] notLayoutFolders = ["bin", "obj", "node_modules", "target", "dist", "build", "out", "vendor"];

    /// <summary>Whether this start is on code, no self test, no checks and no layout near the top of any folder that's open.</summary>
    private bool WantsPlainWelcome() =>
        !options.SelfTest && options.Check.Length == 0 && !options.Browse && !HasLayoutsNear(explorer.workspace.Folders);

    /// <summary>
    /// Whether any of the folders has a .hor in it or in a folder right under it. Hidden folders and the ones builds and
    /// packages go in are skipped, and only so many are looked in.
    /// </summary>
    internal static bool HasLayoutsNear(IEnumerable<string> folders)
    {
        var shallow = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        foreach (string folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;
                if (Directory.EnumerateFiles(folder, "*.hor", shallow).Any())
                    return true;

                int looked = 0;
                foreach (string under in Directory.EnumerateDirectories(folder, "*", shallow))
                {
                    string name = Path.GetFileName(under);
                    if (name.StartsWith('.') || notLayoutFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (Directory.EnumerateFiles(under, "*.hor", shallow).Any())
                        return true;
                    if (++looked >= LAYOUT_LOOK_FOLDERS)
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read has no layouts as far as this goes
            }
        }

        return false;
    }

    /// <summary>
    /// The toolbar and the Properties put away while the plain welcome is up, and back as it goes. The view itself is
    /// put back by whatever took the welcome away (a file opened, F7).
    /// </summary>
    private void ShowPlainChrome(bool plain)
    {
        // Kept so every tick, the toolbar is put up again by nothing else but this
        if (plain && toolbar.Visible)
            toolbar.Visible = false;

        if (plain == plainShown)
            return;

        plainShown = plain;
        if (plain)
        {
            // Nothing typed goes into the layout's code under the welcome
            code.Unfocus();

            // The side folded the way a file with nothing for it folds it, back for the next file that has something
            if (right.Shown)
            {
                right.Show(false);
                valuesFoldedForFile = true;
            }

            return;
        }

        toolbar.Visible = true;
        FoldValuesFor(document);
    }
}
