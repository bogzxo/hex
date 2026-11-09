namespace Hex.Dawn;

// The filter over the changes of Source Control, Visual Studio's Git Changes has one. With four hundred files changed there
// was no finding the one wanted but scrolling, a few letters of its name or its folder narrow every section to the files
// that have them, and how many of how many there are says what's left out.
internal sealed partial class HexGit
{
    // What the files are narrowed to, nothing for all of them
    private string changesFilter = string.Empty;

    /// <summary>What the changes are narrowed to, by the box over the side.</summary>
    internal string ChangesFilter => changesFilter;

    /// <summary>The changes narrowed to the files with some text in their path, the side made again once and only if it's another text.</summary>
    internal void FilterChanges(string text)
    {
        text = text.Trim();
        if (text == changesFilter)
            return;

        changesFilter = text;
        context.Panels.RefreshSide(HexSide.SourceControl);
    }

    /// <summary>
    /// Whether a changed file is one the filter lets through, by its path in the repository (folder and name, either way of
    /// writing the slashes) or the name it had before a rename.
    /// </summary>
    internal static bool Matches(string filter, string relative, string? from) =>
        filter.Length == 0
        || relative.Replace('\\', '/').Contains(filter.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        || from is not null && from.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
