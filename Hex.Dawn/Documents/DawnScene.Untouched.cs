namespace Hex.Dawn;

// What saving leaves alone. A file nobody changed keeps its bytes, a file with a mark at the start keeps it, and a change
// the designer couldn't write into a layout's code is said rather than written as something else.
internal sealed partial class DawnScene
{
    /// <summary>
    /// Whether a save would write what's in the file already, the text the same as when it was read, to the file it was
    /// read from, and the file still what it was then. Writing the text out again would be the same text but maybe not the
    /// same bytes, a file with its line endings mixed came out with them all one way.
    /// </summary>
    private static bool Untouched(HexDocument saving, string path, string written) =>
        saving.DiskText is { } disk && saving.Path is { } own
        && Path.GetFullPath(own).Equals(Path.GetFullPath(path), PlatformPaths.Comparison)
        && written == saving.SavedCode && saving.CrLf == saving.SavedCrLf
        && File.Exists(path) && ReadNow(path) == disk;

    /// <summary>
    /// A change in the designer that the code of its layout couldn't take, said, and the layout put back to its code on
    /// screen. Nothing of it was written anywhere.
    /// </summary>
    private void SayTurnedDown(HexDocument of)
    {
        if (of.TakeTurnedDown() is not { } why)
            return;

        Console.WriteLine($"[Hex] A change to '{of.Name}' was taken back, {why}.");
        Say(Said("documents.turned_down", ("name", of.Name), ("why", why)), error: true);
        if (of == document)
        {
            designer.MarkSelection();
            treeDirty = inspectorDirty = tabsDirty = true;
        }
    }
}
