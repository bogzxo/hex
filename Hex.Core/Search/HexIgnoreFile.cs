namespace Hex.Core;

/// <summary>
/// A .gitignore read for walking the files of a folder, VS Code's search.useIgnoreFiles. Its lines are globs (see
/// <see cref="HexGlob"/>) matched against a path inside the folder the file is in, a name, dir/, *.ext, /from_the_top,
/// **/anywhere and ! to take one back. The last line that matches a path says, and a deeper file's lines come after
/// the ones above it, the way git has it.
/// </summary>
internal sealed class HexIgnoreFile
{
    public const string FILE = ".gitignore";

    // Where the file's folder is inside what's being walked ("" for the top of it), and its lines in order
    private readonly string under;
    private readonly (HexGlob Glob, bool Back)[] rules;

    private HexIgnoreFile(string under, (HexGlob, bool)[] rules)
    {
        this.under = under;
        this.rules = rules;
    }

    /// <summary>The .gitignore of a folder, null when it has none (or none that says anything). Under is where the folder is in what's walked.</summary>
    public static HexIgnoreFile? In(string folder, string under)
    {
        string path = Path.Combine(folder, FILE);
        if (!File.Exists(path))
            return null;

        try
        {
            return Of(File.ReadAllLines(path), under);
        }
        catch (Exception)
        {
            // One that can't be read leaves out nothing
            return null;
        }
    }

    /// <summary>The lines of a .gitignore read as one, null for nothing in them.</summary>
    public static HexIgnoreFile? Of(IEnumerable<string> lines, string under = "")
    {
        var rules = new List<(HexGlob, bool)>();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
                continue;

            bool back = line[0] == '!';
            if (back)
                line = line[1..];

            // \# and \! are a name that starts with one
            if (line.StartsWith('\\'))
                line = line[1..];

            foreach (HexGlob glob in HexGlob.Parse(line))
                rules.Add((glob, back));
        }

        return rules.Count == 0 ? null : new HexIgnoreFile(under, [.. rules]);
    }

    /// <summary>
    /// What the file says of a path inside what's walked, true for left out, false for taken back with !, and what it
    /// was told when none of its lines match.
    /// </summary>
    public bool Says(string relative, bool folder, bool otherwise)
    {
        string inside = relative;
        if (under.Length > 0)
        {
            if (relative.Length <= under.Length + 1 || !relative.StartsWith(under, StringComparison.OrdinalIgnoreCase) || relative[under.Length] != '/')
                return otherwise;
            inside = relative[(under.Length + 1)..];
        }

        bool ignored = otherwise;
        foreach (var (glob, back) in rules)
        {
            if (glob.Matches(inside, folder))
                ignored = !back;
        }

        return ignored;
    }

    /// <summary>Whether the files from the top of what's walked down to a path leave it out, the top one first.</summary>
    public static bool Ignored(IReadOnlyList<HexIgnoreFile> files, string relative, bool folder)
    {
        bool ignored = false;
        for (int i = 0; i < files.Count; i++)
            ignored = files[i].Says(relative, folder, ignored);
        return ignored;
    }
}
