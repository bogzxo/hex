namespace Hex.Core;

/// <summary>
/// The files to include and the files to exclude of Find in Files, VS Code's two boxes under the query. Globs split on
/// commas (not the ones inside braces), each matched against where a file is inside its folder the way files_exclude
/// is, and a glob that matches a folder counts for everything in it. Nothing in the include box is every file.
/// </summary>
internal sealed class HexScope
{
    private readonly HexGlob[] include, exclude;

    private HexScope(HexGlob[] include, HexGlob[] exclude)
    {
        this.include = include;
        this.exclude = exclude;
    }

    /// <summary>What two boxes say, null when both are empty and every file is looked through.</summary>
    public static HexScope? Of(string include, string exclude)
    {
        HexGlob[] kept = HexGlob.All(Split(include)), left = HexGlob.All(Split(exclude));
        return kept.Length == 0 && left.Length == 0 ? null : new HexScope(kept, left);
    }

    /// <summary>The globs of a box, "src/**, *.md" is two of them and "*.{cs,md}" is one.</summary>
    public static List<string> Split(string written)
    {
        var parts = new List<string>();
        int depth = 0, from = 0;
        for (int i = 0; i <= written.Length; i++)
        {
            if (i < written.Length)
            {
                char at = written[i];
                if (at == '{')
                    depth++;
                else if (at == '}')
                    depth = Math.Max(0, depth - 1);

                if (at != ',' || depth > 0)
                    continue;
            }

            string part = written[from..i].Trim();
            if (part.Length > 0)
                parts.Add(part);
            from = i + 1;
        }

        return parts;
    }

    /// <summary>
    /// Whether a file is looked through, by where it is inside its folder and, with more than one folder, by the same
    /// with the folder's name in front (VS Code's "app/src/**" for the src of the folder called app).
    /// </summary>
    public bool Keeps(string inside, string? named = null)
    {
        if (include.Length > 0 && !Covers(include, inside) && (named is null || !Covers(include, named)))
            return false;

        return !Covers(exclude, inside) && (named is null || !Covers(exclude, named));
    }

    // Whether one of the globs matches a file or one of the folders it's in
    private static bool Covers(HexGlob[] globs, string relative)
    {
        if (globs.Length == 0)
            return false;

        foreach (HexGlob glob in globs)
        {
            if (glob.Matches(relative, folder: false))
                return true;
        }

        for (int slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
        {
            string folder = relative[..slash];
            foreach (HexGlob glob in globs)
            {
                if (glob.Matches(folder, folder: true))
                    return true;
            }
        }

        return false;
    }
}
