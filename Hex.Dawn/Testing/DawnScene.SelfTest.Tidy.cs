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
    // it laid out in another look than the one they pass in
    private (HexSide Code, HexSide Design, bool Left, bool Right, HexFace Face, float LeftWidth, float RightWidth, string? Look, float Scale)? startLayout;

    /// <summary>Keeps how the window is laid out as the run begins, for <see cref="TidyAfterSuite"/> to put back.</summary>
    private void KeepStartLayout() =>
        startLayout = (codeSide, designSide, left.Shown, right.Shown, Face, settings.LeftWidth, settings.RightWidth,
            HexLooks.Current(settings, terminals.Settings.Theme)?.Name, compositor.Scale);

    /// <summary>Helper method to put the window back as it was when the run began, the sides, what they show and how wide they are.</summary>
    private void RestoreStartLayout()
    {
        if (startLayout is not { } start)
            return;

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
        if (codeSide != start.Code || designSide != start.Design)
        {
            codeSide = start.Code;
            designSide = start.Design;
            extensionsUp = false;
            sideShown = null;
            treeDirty = true;
        }
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
        chrome.Dialog?.Close();

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
