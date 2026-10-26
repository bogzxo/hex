namespace Hex.Core;

/// <summary>
/// What the files of a workspace leave out, VS Code's files.exclude (gone from the explorer and from everything else),
/// search.exclude (still in the explorer, but not in quick open, Find in Files or the watcher) and search.useIgnoreFiles
/// (each .gitignore on the way down left out of the walk as well). Paths are inside the folder of the workspace they're
/// in, written with forward slashes.
/// </summary>
internal sealed class HexExcludes
{
    private readonly HexGlob[] hidden, skipped;

    /// <summary>Whether a walk of the files reads every .gitignore it comes to and leaves out what they say.</summary>
    public bool UseIgnoreFiles { get; }

    public HexExcludes(IEnumerable<string> files, IEnumerable<string> search, bool useIgnoreFiles)
    {
        hidden = HexGlob.All(files);
        skipped = [.. hidden, .. HexGlob.All(search)];
        UseIgnoreFiles = useIgnoreFiles;
    }

    /// <summary>What they are as Hex comes, the settings' own.</summary>
    public static HexExcludes Usual { get; } = new(HexSettings.UsualFilesExclude, HexSettings.UsualSearchExclude, true);

    // The last ones made out of settings and what they were made of, asked for every walk and made again only when the
    // settings changed. A class so a walk off the editor's thread never reads half of one
    private sealed record Made(string[] Files, string[] Search, bool Ignore, HexExcludes Excludes);
    private static Made? last;

    /// <summary>What a set of settings leaves out, made once for each change of them.</summary>
    public static HexExcludes Of(HexSettings settings)
    {
        string[] files = settings.FilesExclude, search = settings.SearchExclude;
        bool ignore = settings.SearchUseIgnoreFiles;
        if (last is { } was && was.Files == files && was.Search == search && was.Ignore == ignore)
            return was.Excludes;

        var made = new HexExcludes(files, search, ignore);
        last = new Made(files, search, ignore, made);
        return made;
    }

    /// <summary>Whether files.exclude leaves a path out, gone from the explorer as well.</summary>
    public bool Hidden(string relative, bool folder) => Any(hidden, relative, folder);

    /// <summary>Whether files.exclude or search.exclude leaves a path out of the walk.</summary>
    public bool Skipped(string relative, bool folder) => Any(skipped, relative, folder);

    /// <summary>Whether a path is in a folder that's left out of the walk, or is one, what the watcher has nothing to say of.</summary>
    public bool InSkippedFolder(string relative)
    {
        for (int slash = relative.IndexOf('/'); ; slash = relative.IndexOf('/', slash + 1))
        {
            string folder = slash < 0 ? relative : relative[..slash];
            if (folder.Length > 0 && Skipped(folder, folder: true))
                return true;
            if (slash < 0)
                return false;
        }
    }

    /// <summary>A path inside a folder with forward slashes, "" for the folder itself.</summary>
    public static string Relative(string root, string path)
    {
        string inside = Path.GetRelativePath(root, path).Replace('\\', '/');
        return inside == "." ? string.Empty : inside;
    }

    private static bool Any(HexGlob[] globs, string relative, bool folder)
    {
        for (int i = 0; i < globs.Length; i++)
        {
            if (globs[i].Matches(relative, folder))
                return true;
        }

        return false;
    }
}
