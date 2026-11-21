namespace Hex.Dawn;

// Between the parts of the self test, what one left behind that would get in the way of the next. A file of the test
// typed into and not saved asked about itself the moment anything closed it, and that question sat over every part that
// ran after it, every click landing on it, so a part passed on its own and failed after the wrong one.
internal sealed partial class DawnScene
{
    // How the window was laid out when the run began, which every part starts from. A part that ended on the Agents view
    // left the side bar there, and the code suite after it found the agents where it looked for a file's outline, and an
    // Agents list under the pointer where it hovered over the code. The look and the size of the interface too: a part
    // that went through every look at 100, 150 and 200% and put Solstice back, whatever the run had, left the parts after
    // it laid out in another look than the one they pass in. And the workspace's folders, and nothing listed as found:
    // a part's folders left in made every search and every look at the disk after it go through them as well
    private (HexSide Code, HexSide Design, bool Left, bool Right, HexFace Face, float LeftWidth, float RightWidth, string? Look, float Scale, string[] Folders)? startLayout;

    // The settings as the run began, and the folder File > Open shows. A part that went through the looks to the Visual
    // Studio one put Solstice's skin back but kept Visual Studio's keys, its font and its glass, and every part after it
    // pressed keys of another keyboard; it left Open in a folder of its own as well
    private Hex.Core.HexSettings? startSettings;
    private string startBrowse = string.Empty;

    /// <summary>Keeps how the window is laid out as the run begins, for <see cref="TidyAfterSuite"/> to put back.</summary>
    private void KeepStartLayout()
    {
        startSettings = settings.Snapshot();
        startBrowse = browseDirectory;
        KeepStartWindow();
    }

    private void KeepStartWindow() =>
        startLayout = (codeSide, designSide, left.Shown, right.Shown, Face, settings.LeftWidth, settings.RightWidth,
            HexLooks.Current(settings, terminals.Settings.Theme)?.Name, compositor.Scale, [.. explorer.workspace.Folders]);

    /// <summary>Helper method to put the window back as it was when the run began, the sides, what they show and how wide they are.</summary>
    private void RestoreStartLayout()
    {
        if (startLayout is not { } start)
            return;

        // Properties asked for (Alt+Enter, a file's Properties) stay up for every file after, pinned, and the parts after
        // tree-looks had the editor a third narrower for it, a notebook's bar over its status at 150 and 200%. Put away
        // the way the button puts it away, which unpins it, before the settings that says it's hidden are put back
        if (properties.Pinned && right.Shown)
            ToggleProperties();
        properties.Pinned = false;

        // Settings first, the look and the sizes after them go by what they are then
        if (startSettings is { } kept && SettingsDiffer(kept, settings))
        {
            Hex.Core.HexSettings before = settings;
            settings = kept.Snapshot();
            ApplyReadSettings(before);
            SaveSettings();
        }

        browseDirectory = startBrowse;

        if (Face != start.Face)
            ShowFace(start.Face);

        if (start.Look is { } look && HexLooks.Current(settings, terminals.Settings.Theme)?.Name != look
            && HexLooks.BuiltIn.FirstOrDefault(each => each.Name == look) is { } was)
            PickLook(was);
        if (compositor.Scale != start.Scale)
            SetInterfaceSize(start.Scale, remember: false);

        settings.LeftWidth = start.LeftWidth;
        settings.RightWidth = start.RightWidth;
        if (left.Shown != start.Left) left.Show(start.Left, instantly: true);
        if (right.Shown != start.Right) right.Show(start.Right, instantly: true);
        foreach (string folder in explorer.workspace.Folders.ToArray())
        {
            if (!start.Folders.Any(each => PlatformPaths.Same(each, folder)))
                explorer.RemoveFolder(folder);
        }

        foreach (string folder in start.Folders)
        {
            if (!explorer.workspace.Folders.Any(each => PlatformPaths.Same(each, folder)) && Directory.Exists(folder))
                explorer.AddFolder(folder);
        }

        if (results is not null || resultsFound is not null)
            CloseResults();

        if (codeSide != start.Code || designSide != start.Design)
        {
            codeSide = start.Code;
            designSide = start.Design;
            extensionsUp = false;
            sideShown = null;
            treeDirty = true;
        }
    }

    /// <summary>Helper method to tell whether two settings differ in any value of their own, a number, a word or a switch.</summary>
    private static bool SettingsDiffer(Hex.Core.HexSettings a, Hex.Core.HexSettings b)
    {
        const System.Reflection.BindingFlags FIELDS = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (System.Reflection.FieldInfo field in typeof(Hex.Core.HexSettings).GetFields(FIELDS))
        {
            Type type = field.FieldType;
            if ((type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) && !Equals(field.GetValue(a), field.GetValue(b)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to close every tab of a file under a folder a part made, before the folder goes. Only the one on
    /// screen was closed, the one behind it was put back by a checkout and read again from the disk a moment later, in
    /// the next part, saying so over what that part looked for in the status bar.
    /// </summary>
    private void CloseDocumentsUnder(string folder)
    {
        foreach (HexDocument open in documents.ToList())
        {
            if (open.Path is { } path && PlatformPaths.IsUnder(path, folder) && documents.Contains(open))
            {
                Show(open);
                CloseNow();
            }
        }
    }

    private void TidyAfterSuite()
    {
        // Whatever asks something left up, answered by nobody: a part that ran out of time with a chooser over the
        // window (a configuration to pick) left it there for every part after it
        chrome.Dialog?.Close();
        if (textDialog is { IsOpen: true } asking)
            asking.Close();
        contextMenu?.Close();

        // The test's own files that were typed into, closed without asking, whatever was typed in them is the test's. A
        // layout with no file that a suite added to as well: left open and changed it put "Unsaved Changes" up in the
        // next suite the moment that one closed its tabs, and every check after it was of the dialog and not of Hex
        foreach (HexDocument open in documents.ToList())
        {
            bool ours = open.Path is { } path ? PlatformPaths.IsUnder(path, testFolder) : documents.Count > 1;
            if ((open.Modified || open.CodeEdited) && ours)
            {
                Show(open);
                CloseNow();
            }
        }

        RestoreStartLayout();
    }
}
