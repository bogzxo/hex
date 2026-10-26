namespace Hex.Dawn;

// The files Hex was started on, each opened in the order the command line has them at its line and column, the last
// one on screen. One that isn't there is said in a card that goes by itself, the rest open all the same.
internal sealed partial class DawnScene
{
    private void OpenLaunchFiles(IReadOnlyList<HexLaunchFile> files)
    {
        var missing = new List<string>();
        foreach (HexLaunchFile file in files)
        {
            if (!File.Exists(file.Path))
            {
                missing.Add(file.Path);
                continue;
            }

            if (file.Line > 0 || file.Column > 0)
                OpenAt(file.Path, Math.Max(0, file.Line - 1), Math.Max(0, file.Column - 1));
            else
                Open(file.Path);
        }

        if (missing.Count > 0)
            Notify(Said("launch.missing", ("files", string.Join(", ", missing))));
    }
}
