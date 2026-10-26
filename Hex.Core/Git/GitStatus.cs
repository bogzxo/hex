namespace Hex.Core.Git;

/// <summary>What git thinks of a file, the strongest thing first when a folder takes the colour of what's in it.</summary>
internal enum GitChange
{
    None,
    Ignored,
    Untracked,
    Added,
    Renamed,
    Modified,
    Deleted,
    Conflicted
}

/// <summary>A file's change, and whether it's staged (in the index, what the next commit takes) or only in the working tree.</summary>
/// <param name="Staged">Some of it is in the index.</param>
/// <param name="Unstaged">Some of it is still only in the working tree.</param>
/// <param name="From">Where a renamed file was, the full path, null for anything else.</param>
internal readonly record struct GitFileState(GitChange Change, bool Staged, bool Unstaged, string? From = null)
{
    /// <summary>The letter VS Code puts next to a file, M, A, D, R, U, C (conflict) or I.</summary>
    public string Letter => Change switch
    {
        GitChange.Modified => "M",
        GitChange.Added => "A",
        GitChange.Deleted => "D",
        GitChange.Renamed => "R",
        GitChange.Untracked => "U",
        GitChange.Conflicted => "C",
        GitChange.Ignored => "I",
        _ => string.Empty
    };
}

/// <summary>
/// One look at a repository, its branch and how far it is from its upstream, and every file that isn't as it was
/// committed, by full path. Read out of <c>git status --porcelain=v2</c>, which is made for programs to read and stays
/// the same from one git to the next. Never changed once made, a new one replaces it whole.
/// </summary>
internal sealed class GitStatus
{
    /// <summary>The folder the repository is, where .git is.</summary>
    public required string Root { get; init; }

    /// <summary>The branch checked out, null when HEAD isn't on one (detached, a rebase going on).</summary>
    public string? Branch { get; init; }

    /// <summary>The commit HEAD is on, its full hash, null in a repository with nothing committed yet.</summary>
    public string? Commit { get; init; }

    /// <summary>The branch this one pushes to and pulls from, "origin/main", null when it has none.</summary>
    public string? Upstream { get; init; }

    /// <summary>How many commits this branch has that its upstream doesn't, and the other way round.</summary>
    public int Ahead { get; init; }

    public int Behind { get; init; }

    /// <summary>Every file that isn't as committed, by full path. Ignored files only when a whole folder isn't.</summary>
    public IReadOnlyDictionary<string, GitFileState> Files { get; init; } = new Dictionary<string, GitFileState>();

    // The folders git ignores as a whole (bin, obj), each with a separator on the end
    private readonly List<string> ignoredFolders = [];

    // What a folder looks like from the outside, the strongest change of anything in it
    private readonly Dictionary<string, GitChange> folders = new(PlatformPaths.Comparer);

    /// <summary>What git thinks of a file, None when it's as committed.</summary>
    public GitFileState StateOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (Files.TryGetValue(full, out GitFileState state))
            return state;

        return IsIgnored(full) ? new GitFileState(GitChange.Ignored, false, false) : default;
    }

    /// <summary>The colour a folder takes, the strongest change of anything under it (nothing for being ignored, that's its own).</summary>
    public GitChange FolderChange(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (folders.TryGetValue(full, out GitChange change))
            return change;

        return IsIgnored(full + Path.DirectorySeparatorChar) ? GitChange.Ignored : GitChange.None;
    }

    private bool IsIgnored(string full)
    {
        foreach (string folder in ignoredFolders)
        {
            if (full.StartsWith(folder, PlatformPaths.Comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads what <c>git status --porcelain=v2 -z --branch</c> wrote. Every record ends in a NUL, a renamed file's two,
    /// its path and the one it came from.
    /// </summary>
    public static GitStatus Parse(string root, string porcelain)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? branch = null, commit = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new Dictionary<string, GitFileState>(PlatformPaths.Comparer);
        var ignored = new List<string>();

        string[] records = porcelain.Split('\0');
        for (int i = 0; i < records.Length; i++)
        {
            string record = records[i];
            if (record.Length < 2)
                continue;

            switch (record[0])
            {
                case '#':
                    ReadHeader(record, ref branch, ref commit, ref upstream, ref ahead, ref behind);
                    break;

                // 1 XY sub mH mI mW hH hI path
                case '1':
                    if (Field(record, 8) is { } changed)
                        files[Full(root, changed)] = Tracked(record[2], record[3], null);
                    break;

                // 2 XY sub mH mI mW hH hI Xscore path, then the path it was renamed from as a record of its own
                case '2':
                    if (Field(record, 9) is { } renamed)
                    {
                        string? from = i + 1 < records.Length ? Full(root, records[++i]) : null;
                        files[Full(root, renamed)] = Tracked(record[2], record[3], from);
                    }
                    break;

                // u XY sub m1 m2 m3 mW h1 h2 h3 path, both sides changed it and the merge left it to the user
                case 'u':
                    if (Field(record, 10) is { } conflicted)
                        files[Full(root, conflicted)] = new GitFileState(GitChange.Conflicted, false, true);
                    break;

                case '?':
                    files[Full(root, record[2..])] = new GitFileState(GitChange.Untracked, false, true);
                    break;

                case '!':
                    string path = Full(root, record[2..]);
                    if (record.EndsWith('/'))
                        ignored.Add(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar);
                    else
                        files[path] = new GitFileState(GitChange.Ignored, false, false);
                    break;
            }
        }

        var status = new GitStatus
        {
            Root = root,
            Branch = branch,
            Commit = commit,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Files = files
        };
        status.ignoredFolders.AddRange(ignored);
        status.ColourFolders();
        return status;
    }

    // Every folder from a changed file up to the root takes its change if it's stronger than what it has
    private void ColourFolders()
    {
        foreach (var (path, state) in Files)
        {
            if (state.Change is GitChange.Ignored or GitChange.None)
                continue;

            string? folder = Path.GetDirectoryName(path);
            while (folder is not null && folder.Length >= Root.Length)
            {
                if (folders.TryGetValue(folder, out GitChange had) && had >= state.Change)
                    break;

                folders[folder] = state.Change;
                folder = Path.GetDirectoryName(folder);
            }
        }
    }

    private static void ReadHeader(string record, ref string? branch, ref string? commit, ref string? upstream, ref int ahead, ref int behind)
    {
        string[] words = record.Split(' ');
        if (words.Length < 3)
            return;

        switch (words[1])
        {
            case "branch.oid":
                commit = words[2] == "(initial)" ? null : words[2];
                break;
            case "branch.head":
                branch = words[2] == "(detached)" ? null : words[2];
                break;
            case "branch.upstream":
                upstream = words[2];
                break;
            case "branch.ab" when words.Length >= 4:
                int.TryParse(words[2].TrimStart('+'), out ahead);
                int.TryParse(words[3].TrimStart('-'), out behind);
                break;
        }
    }

    // X is the index, Y the working tree, a dot for unchanged
    private static GitFileState Tracked(char index, char tree, string? from)
    {
        char which = tree != '.' ? tree : index;
        GitChange change = which switch
        {
            'A' => GitChange.Added,
            'D' => GitChange.Deleted,
            'R' or 'C' => GitChange.Renamed,
            _ => GitChange.Modified
        };

        // Added to the index and changed since is still a new file as far as anybody looking is concerned
        if (index == 'A' && tree == 'M')
            change = GitChange.Added;

        return new GitFileState(change, index != '.', tree != '.', from);
    }

    // What comes after the nth space, the path, which can have spaces of its own
    private static string? Field(string record, int skip)
    {
        int at = 0;
        for (int i = 0; i < skip; i++)
        {
            at = record.IndexOf(' ', at) + 1;
            if (at == 0)
                return null;
        }

        return record[at..];
    }

    private static string Full(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
