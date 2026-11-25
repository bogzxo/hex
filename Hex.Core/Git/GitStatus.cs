using System.Buffers;
using System.Text;

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

/// <summary>
/// What a row that isn't a plain file is, a submodule (another repository whose commit this one keeps) and what moved in
/// it, or a repository of its own inside this one that nobody added. None for a plain file.
/// </summary>
[Flags]
internal enum GitSubmodule
{
    None = 0,

    /// <summary>A submodule, its path is a folder and what changed is a commit of another repository.</summary>
    Module = 1,

    /// <summary>Its HEAD isn't on the commit this repository keeps for it, new commits in it or another one checked out.</summary>
    Commits = 2,

    /// <summary>Files git knows in it are changed.</summary>
    Modified = 4,

    /// <summary>It has files nobody added.</summary>
    Untracked = 8,

    /// <summary>A repository inside this one that nobody added, git lists it as one folder and looks no further.</summary>
    Nested = 16
}

/// <summary>A file's change, and whether it's staged (in the index, what the next commit takes) or only in the working tree.</summary>
/// <param name="Staged">Some of it is in the index.</param>
/// <param name="Unstaged">Some of it is still only in the working tree.</param>
/// <param name="From">Where a renamed file was, the full path, null for anything else.</param>
/// <param name="Submodule">What it is when it isn't a plain file, a submodule or a repository nobody added.</param>
internal readonly record struct GitFileState(GitChange Change, bool Staged, bool Unstaged, string? From = null, GitSubmodule Submodule = GitSubmodule.None)
{
    /// <summary>Whether it's a submodule or a repository inside this one, a folder of another repository and not a file.</summary>
    public bool IsRepository => Submodule != GitSubmodule.None;

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
    public IReadOnlyDictionary<string, GitFileState> Files { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, GitFileState>.Empty;

    // The folders git ignores as a whole (bin, obj), each with a separator on the end
    private string[] ignoredFolders = [];

    // What a folder looks like from the outside, the strongest change of anything in it
    private readonly Dictionary<string, GitChange> folders = new(PlatformPaths.Comparer);

    /// <summary>What git thinks of a file, None when it's as committed.</summary>
    public GitFileState StateOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (Files.TryGetValue(full, out GitFileState state))
            return state;

        return ignoredFolders.Length > 0 && IsIgnored(full) ? new GitFileState(GitChange.Ignored, false, false) : default;
    }

    /// <summary>The colour a folder takes, the strongest change of anything under it (nothing for being ignored, that's its own).</summary>
    public GitChange FolderChange(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (folders.TryGetValue(full, out GitChange change))
            return change;

        return ignoredFolders.Length > 0 && IsIgnoredFolder(full) ? GitChange.Ignored : GitChange.None;
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

    private bool IsIgnoredFolder(string fullWithoutTrailingSep)
    {
        ReadOnlySpan<char> fullSpan = fullWithoutTrailingSep.AsSpan();
        foreach (string folder in ignoredFolders)
        {
            // Each ignored folder ends with Path.DirectorySeparatorChar.
            if (fullSpan.Length >= folder.Length)
            {
                if (fullSpan.StartsWith(folder.AsSpan(), PlatformPaths.Comparison))
                    return true;
            }
            else if (folder.Length == fullSpan.Length + 1
                && folder.AsSpan(0, fullSpan.Length).Equals(fullSpan, PlatformPaths.Comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads what <c>git status --porcelain=v2 -z --branch</c> (or porcelain v1) wrote. Every record in <c>-z</c> mode
    /// ends in a NUL, a renamed file's two, its path and the one it came from.
    /// </summary>
    public static GitStatus Parse(string root, string porcelain) => Parse(root, porcelain.AsSpan());

    /// <summary>
    /// Reads porcelain v2 (or v1) status output directly from a character span without requiring an intermediate string.
    /// </summary>
    public static GitStatus Parse(string root, ReadOnlySpan<char> porcelain)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? branch = null, commit = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new Dictionary<string, GitFileState>(PlatformPaths.Comparer);
        List<string>? ignored = null;

        bool nulDelimited = porcelain.IndexOf('\0') >= 0;
        int pos = 0;
        while (pos < porcelain.Length)
        {
            ReadOnlySpan<char> record = nulDelimited
                ? NextRecord(porcelain, ref pos, '\0')
                : NextRecord(porcelain, ref pos, '\n').TrimEnd('\r');
            if (record.Length < 2)
                continue;

            // Porcelain v2 records always have a space at index 1; porcelain v1 records have a two-char XY status and a space at index 2.
            if (record[1] == ' ')
            {
                switch (record[0])
                {
                    case '#':
                        ReadHeader(record, ref branch, ref commit, ref upstream, ref ahead, ref behind);
                        continue;

                    // 1 XY sub mH mI mW hH hI path
                    case '1':
                        if (TryField(record, 8, out ReadOnlySpan<char> changed))
                            files[FullPath(root, changed, nulDelimited)] = Tracked(record[2], record[3], null) with { Submodule = Sub(record) };
                        continue;

                    // 2 XY sub mH mI mW hH hI Xscore path, then the path it was renamed from as a record of its own
                    case '2':
                        if (TryField(record, 9, out ReadOnlySpan<char> renamed))
                        {
                            string? from = null;
                            if (nulDelimited)
                            {
                                if (pos < porcelain.Length)
                                    from = FullPath(root, NextRecord(porcelain, ref pos, '\0'), nulDelimited: true);
                            }
                            else
                            {
                                int tab = renamed.IndexOf('\t');
                                if (tab >= 0)
                                {
                                    from = FullPath(root, renamed[(tab + 1)..], nulDelimited: false);
                                    renamed = renamed[..tab];
                                }
                            }
                            files[FullPath(root, renamed, nulDelimited)] = Tracked(record[2], record[3], from) with { Submodule = Sub(record) };
                        }
                        continue;

                    // u XY sub m1 m2 m3 mW h1 h2 h3 path, both sides changed it and the merge left it to the user
                    case 'u':
                        if (TryField(record, 10, out ReadOnlySpan<char> conflicted))
                            files[FullPath(root, conflicted, nulDelimited)] = new GitFileState(GitChange.Conflicted, false, true, Submodule: Sub(record));
                        continue;

                    // A folder here is a repository of its own that nobody added, the rest are listed file by file. Its path
                    // is kept without the slash on the end, which left its row on the side with no name
                    case '?':
                        AddUntracked(root, record[2..], nulDelimited, files);
                        continue;

                    case '!':
                        AddIgnored(root, record[2..], nulDelimited, files, ref ignored);
                        continue;
                }
            }

            // Porcelain v1 fallback: "XY path" (or "## branch...upstream [ahead N, behind M]")
            if (record.Length >= 4 && record[2] == ' ')
            {
                char x = record[0], y = record[1];
                ReadOnlySpan<char> payload = record[3..];
                if (x == '#' && y == '#')
                {
                    ReadV1BranchHeader(payload, ref branch, ref upstream, ref ahead, ref behind);
                }
                else if (x == '?' && y == '?')
                {
                    AddUntracked(root, payload, nulDelimited, files);
                }
                else if (x == '!' && y == '!')
                {
                    AddIgnored(root, payload, nulDelimited, files, ref ignored);
                }
                else if (IsV1Conflict(x, y))
                {
                    files[FullPath(root, payload, nulDelimited)] = new GitFileState(GitChange.Conflicted, false, true);
                }
                else
                {
                    string? from = null;
                    ReadOnlySpan<char> target = payload;
                    if (x is 'R' or 'C' || y is 'R' or 'C')
                    {
                        if (nulDelimited)
                        {
                            if (pos < porcelain.Length)
                                from = FullPath(root, NextRecord(porcelain, ref pos, '\0'), nulDelimited: true);
                        }
                        else if (TrySplitV1Rename(payload, out ReadOnlySpan<char> oldPath, out ReadOnlySpan<char> newPath))
                        {
                            from = FullPath(root, oldPath, nulDelimited: false);
                            target = newPath;
                        }
                    }

                    files[FullPath(root, target, nulDelimited)] = Tracked(x == ' ' ? '.' : x, y == ' ' ? '.' : y, from);
                }
            }
        }

        files.TrimExcess();
        var status = new GitStatus
        {
            Root = root,
            Branch = branch,
            Commit = commit,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Files = files.Count > 0 ? files : System.Collections.ObjectModel.ReadOnlyDictionary<string, GitFileState>.Empty,
            ignoredFolders = ignored is { Count: > 0 } ? ignored.ToArray() : []
        };
        status.ColourFolders();
        return status;
    }

    private static ReadOnlySpan<char> NextRecord(ReadOnlySpan<char> span, ref int pos, char delimiter)
    {
        int next = span[pos..].IndexOf(delimiter);
        if (next < 0)
        {
            ReadOnlySpan<char> tail = span[pos..];
            pos = span.Length;
            return tail;
        }

        ReadOnlySpan<char> record = span.Slice(pos, next);
        pos += next + 1;
        return record;
    }

    private static void AddUntracked(string root, ReadOnlySpan<char> rawPath, bool nulDelimited, Dictionary<string, GitFileState> files)
    {
        if (rawPath.IsEmpty)
            return;

        if (nulDelimited || rawPath[0] != '"')
        {
            if (rawPath[^1] == '/')
                files[Full(root, rawPath[..^1])] = new GitFileState(GitChange.Untracked, false, true, Submodule: GitSubmodule.Nested);
            else
                files[Full(root, rawPath)] = new GitFileState(GitChange.Untracked, false, true);
            return;
        }

        string unescaped = UnescapeQuotedPath(rawPath);
        ReadOnlySpan<char> span = unescaped.AsSpan();
        if (span.Length > 0 && span[^1] == '/')
            files[Full(root, span[..^1])] = new GitFileState(GitChange.Untracked, false, true, Submodule: GitSubmodule.Nested);
        else
            files[Full(root, span)] = new GitFileState(GitChange.Untracked, false, true);
    }

    private static void AddIgnored(string root, ReadOnlySpan<char> rawPath, bool nulDelimited, Dictionary<string, GitFileState> files, ref List<string>? ignored)
    {
        if (rawPath.IsEmpty)
            return;

        if (nulDelimited || rawPath[0] != '"')
        {
            if (rawPath[^1] == '/')
                (ignored ??= []).Add(Full(root, rawPath[..^1], appendTrailingSeparator: true));
            else
                files[Full(root, rawPath)] = new GitFileState(GitChange.Ignored, false, false);
            return;
        }

        string unescaped = UnescapeQuotedPath(rawPath);
        ReadOnlySpan<char> span = unescaped.AsSpan();
        if (span.Length > 0 && span[^1] == '/')
            (ignored ??= []).Add(Full(root, span[..^1], appendTrailingSeparator: true));
        else
            files[Full(root, span)] = new GitFileState(GitChange.Ignored, false, false);
    }

    private static string FullPath(string root, ReadOnlySpan<char> rawPath, bool nulDelimited, bool appendTrailingSeparator = false)
    {
        if (!nulDelimited && rawPath.Length >= 2 && rawPath[0] == '"' && rawPath[^1] == '"')
            return Full(root, UnescapeQuotedPath(rawPath).AsSpan(), appendTrailingSeparator);
        return Full(root, rawPath, appendTrailingSeparator);
    }

    private static bool IsV1Conflict(char x, char y) =>
        x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    private static bool TrySplitV1Rename(ReadOnlySpan<char> payload, out ReadOnlySpan<char> oldPath, out ReadOnlySpan<char> newPath)
    {
        int sep = -1;
        bool inQuotes = false;
        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[i];
            if (c == '\\' && inQuotes && i + 1 < payload.Length)
            {
                i++;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && payload[i..].StartsWith(" -> ", StringComparison.Ordinal))
            {
                sep = i;
                break;
            }
        }

        if (sep < 0)
        {
            oldPath = newPath = default;
            return false;
        }

        oldPath = payload[..sep];
        newPath = payload[(sep + 4)..];
        return true;
    }

    private static void ReadV1BranchHeader(ReadOnlySpan<char> header, ref string? branch, ref string? upstream, ref int ahead, ref int behind)
    {
        if (header.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
            return;
        if (header.StartsWith("No commits yet on ", StringComparison.Ordinal))
        {
            branch = header["No commits yet on ".Length..].ToString();
            return;
        }
        if (header.StartsWith("Initial commit on ", StringComparison.Ordinal))
        {
            branch = header["Initial commit on ".Length..].ToString();
            return;
        }

        int dots = header.IndexOf("...", StringComparison.Ordinal);
        if (dots < 0)
        {
            branch = header.ToString();
            return;
        }

        branch = header[..dots].ToString();
        ReadOnlySpan<char> rest = header[(dots + 3)..];
        int bracket = rest.IndexOf(" [", StringComparison.Ordinal);
        if (bracket < 0)
        {
            upstream = rest.ToString();
            return;
        }

        upstream = rest[..bracket].ToString();
        ReadOnlySpan<char> info = rest[(bracket + 2)..];
        int close = info.IndexOf(']');
        if (close >= 0)
            info = info[..close];

        while (!info.IsEmpty)
        {
            int comma = info.IndexOf(',');
            ReadOnlySpan<char> part = (comma >= 0 ? info[..comma] : info).Trim();
            if (part.StartsWith("ahead ", StringComparison.Ordinal))
                int.TryParse(part["ahead ".Length..], System.Globalization.CultureInfo.InvariantCulture, out ahead);
            else if (part.StartsWith("behind ", StringComparison.Ordinal))
                int.TryParse(part["behind ".Length..], System.Globalization.CultureInfo.InvariantCulture, out behind);

            if (comma < 0)
                break;
            info = info[(comma + 1)..];
        }
    }

    /// <summary>Decodes a git C-quoted path (including UTF-8 octal byte escapes such as <c>\303\251</c>).</summary>
    internal static string UnescapeQuotedPath(ReadOnlySpan<char> quoted)
    {
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"')
            return quoted.ToString();

        ReadOnlySpan<char> inner = quoted[1..^1];
        int maxBytes = Encoding.UTF8.GetMaxByteCount(inner.Length);
        byte[]? rented = maxBytes > 512 ? ArrayPool<byte>.Shared.Rent(maxBytes) : null;
        Span<byte> bytes = rented is not null ? rented.AsSpan(0, maxBytes) : stackalloc byte[maxBytes];
        try
        {
            int count = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '\\' && i + 1 < inner.Length)
                {
                    char next = inner[++i];
                    if (next is >= '0' and <= '7' && i + 2 < inner.Length
                        && inner[i + 1] is >= '0' and <= '7'
                        && inner[i + 2] is >= '0' and <= '7')
                    {
                        bytes[count++] = (byte)(((next - '0') << 6) | ((inner[i + 1] - '0') << 3) | (inner[i + 2] - '0'));
                        i += 2;
                        continue;
                    }

                    bytes[count++] = next switch
                    {
                        'a' => (byte)'\a',
                        'b' => (byte)'\b',
                        'f' => (byte)'\f',
                        'n' => (byte)'\n',
                        'r' => (byte)'\r',
                        't' => (byte)'\t',
                        'v' => (byte)'\v',
                        '\\' => (byte)'\\',
                        '"' => (byte)'"',
                        _ => (byte)next
                    };
                    continue;
                }

                if (c < 0x80)
                {
                    bytes[count++] = (byte)c;
                }
                else
                {
                    int charLen = char.IsHighSurrogate(c) && i + 1 < inner.Length ? 2 : 1;
                    count += Encoding.UTF8.GetBytes(inner.Slice(i, charLen), bytes[count..]);
                    i += charLen - 1;
                }
            }

            return Encoding.UTF8.GetString(bytes[..count]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Every folder from a changed file up to the root takes its change if it's stronger than what it has. A submodule or a
    // repository nobody added is a folder itself, so it takes the change too
    private void ColourFolders()
    {
        var lookup = folders.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (path, state) in Files)
        {
            if (state.Change is GitChange.Ignored or GitChange.None)
                continue;

            ReadOnlySpan<char> folder = state.IsRepository ? path.AsSpan() : Path.GetDirectoryName(path.AsSpan());
            while (!folder.IsEmpty && folder.Length >= Root.Length)
            {
                if (lookup.TryGetValue(folder, out GitChange had) && had >= state.Change)
                    break;

                lookup[folder] = state.Change;
                folder = Path.GetDirectoryName(folder);
            }
        }
        folders.TrimExcess();
    }

    private static void ReadHeader(ReadOnlySpan<char> record, ref string? branch, ref string? commit, ref string? upstream, ref int ahead, ref int behind)
    {
        if (record.Length < 4 || record[1] != ' ')
            return;

        ReadOnlySpan<char> body = record[2..];
        int space = body.IndexOf(' ');
        if (space <= 0 || space + 1 >= body.Length)
            return;

        ReadOnlySpan<char> key = body[..space];
        ReadOnlySpan<char> rest = body[(space + 1)..];
        int nextSpace = rest.IndexOf(' ');
        ReadOnlySpan<char> first = nextSpace >= 0 ? rest[..nextSpace] : rest;

        if (key.SequenceEqual("branch.oid"))
        {
            commit = first.SequenceEqual("(initial)") ? null : first.ToString();
        }
        else if (key.SequenceEqual("branch.head"))
        {
            branch = first.SequenceEqual("(detached)") ? null : first.ToString();
        }
        else if (key.SequenceEqual("branch.upstream"))
        {
            upstream = first.ToString();
        }
        else if (key.SequenceEqual("branch.ab") && nextSpace >= 0 && nextSpace + 1 < rest.Length)
        {
            ReadOnlySpan<char> second = rest[(nextSpace + 1)..];
            int secondEnd = second.IndexOf(' ');
            if (secondEnd >= 0)
                second = second[..secondEnd];
            int.TryParse(first.TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture, out ahead);
            int.TryParse(second.TrimStart('-'), System.Globalization.CultureInfo.InvariantCulture, out behind);
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

    // The sub field of a changed file's record, N... for a file, S then C, M and U (or dots) for a submodule, its commit
    // moved, files in it changed and files in it nobody added
    private static GitSubmodule Sub(ReadOnlySpan<char> record)
    {
        if (record.Length < 9 || record[5] != 'S')
            return GitSubmodule.None;

        GitSubmodule sub = GitSubmodule.Module;
        if (record[6] == 'C') sub |= GitSubmodule.Commits;
        if (record[7] == 'M') sub |= GitSubmodule.Modified;
        if (record[8] == 'U') sub |= GitSubmodule.Untracked;
        return sub;
    }

    // What comes after the nth space, the path, which can have spaces of its own
    private static bool TryField(ReadOnlySpan<char> record, int skip, out ReadOnlySpan<char> field)
    {
        int at = 0;
        for (int i = 0; i < skip; i++)
        {
            int next = record[at..].IndexOf(' ');
            if (next < 0)
            {
                field = default;
                return false;
            }
            at += next + 1;
        }

        field = record[at..];
        return true;
    }

    private static string Full(string root, ReadOnlySpan<char> relative, bool appendTrailingSeparator = false)
    {
        char sep = Path.DirectorySeparatorChar;
        if (!relative.IsEmpty
            && !Path.IsPathRooted(relative)
            && relative.IndexOf("..", StringComparison.Ordinal) < 0
            && relative.IndexOf(':') < 0
            && !relative.StartsWith("./", StringComparison.Ordinal)
            && relative.IndexOf("/./", StringComparison.Ordinal) < 0
            && !relative.EndsWith("/.", StringComparison.Ordinal))
        {
            bool rootEndsWithSep = root.Length > 0 && root[^1] == sep;
            int total = root.Length + (rootEndsWithSep ? 0 : 1) + relative.Length + (appendTrailingSeparator ? 1 : 0);
            if (total <= 512)
            {
                Span<char> buffer = stackalloc char[total];
                root.AsSpan().CopyTo(buffer);
                int dst = root.Length;
                if (!rootEndsWithSep)
                    buffer[dst++] = sep;
                for (int i = 0; i < relative.Length; i++)
                {
                    char c = relative[i];
                    buffer[dst++] = c == '/' ? sep : c;
                }
                if (appendTrailingSeparator)
                    buffer[dst] = sep;
                return new string(buffer);
            }
        }

        string full = Path.GetFullPath(Path.Combine(root, relative.ToString().Replace('/', sep)));
        return appendTrailingSeparator ? Path.TrimEndingDirectorySeparator(full) + sep : full;
    }
}
