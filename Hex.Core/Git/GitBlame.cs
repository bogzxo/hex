namespace Hex.Core.Git;

/// <summary>Who last changed a line, when and in which commit. A line nobody committed yet has no commit.</summary>
/// <param name="Commit">The commit's full hash, null for a line that's only in the working tree.</param>
internal sealed record GitBlameLine(string? Commit, string Author, string Mail, DateTimeOffset When, string Summary)
{
    public bool Committed => Commit is not null;

    /// <summary>The hash short the way git log --oneline has it.</summary>
    public string Short => Commit is { } hash ? hash[..Math.Min(8, hash.Length)] : string.Empty;
}

/// <summary>
/// Reads <c>git blame --porcelain</c>, a header for every line ("hash original final"), the commit's details the first
/// time it comes up and only then, and the line itself after a tab.
/// </summary>
internal static class GitBlame
{
    public static IReadOnlyList<GitBlameLine> Parse(string porcelain)
    {
        var lines = new List<GitBlameLine>();
        var commits = new Dictionary<string, GitBlameLine>(StringComparer.Ordinal);
        string? hash = null;
        string author = string.Empty, mail = string.Empty, summary = string.Empty;
        long time = 0;
        int zone = 0;

        foreach (string raw in porcelain.Split('\n'))
        {
            if (raw.Length == 0)
                continue;

            // The line itself, which ends what's said about it
            if (raw[0] == '\t')
            {
                if (hash is null)
                    continue;

                if (!commits.TryGetValue(hash, out GitBlameLine? line))
                {
                    var when = DateTimeOffset.FromUnixTimeSeconds(time).ToOffset(TimeSpan.FromMinutes(zone));
                    line = hash.All(c => c == '0')
                        ? new GitBlameLine(null, author, mail, when, summary)
                        : new GitBlameLine(hash, author, mail, when, summary);
                    commits[hash] = line;
                }

                lines.Add(line);
                continue;
            }

            int space = raw.IndexOf(' ');
            string key = space < 0 ? raw : raw[..space];
            string value = space < 0 ? string.Empty : raw[(space + 1)..];

            // A header, a hash and then numbers
            // 40 characters for SHA-1, 64 for a repository made with SHA-256
            if (key.Length is 40 or 64 && IsHash(key))
            {
                hash = key;
                if (commits.TryGetValue(hash, out GitBlameLine? known))
                {
                    author = known.Author;
                    mail = known.Mail;
                    summary = known.Summary;
                }
                continue;
            }

            switch (key)
            {
                case "author": author = value; break;
                case "author-mail": mail = value.Trim('<', '>'); break;
                case "author-time": long.TryParse(value, out time); break;
                case "author-tz": zone = Zone(value); break;
                case "summary": summary = value; break;
            }
        }

        return lines;
    }

    private static bool IsHash(string word)
    {
        foreach (char c in word)
        {
            if (!char.IsAsciiHexDigitLower(c))
                return false;
        }

        return true;
    }

    // +0200 as minutes
    private static int Zone(string value)
    {
        if (value.Length != 5 || !int.TryParse(value.AsSpan(1, 2), out int hours) || !int.TryParse(value.AsSpan(3, 2), out int minutes))
            return 0;
        int total = hours * 60 + minutes;
        return value[0] == '-' ? -total : total;
    }
}

/// <summary>A branch, here or on a remote, which is checked out, what it follows, and when it last had a commit.</summary>
internal sealed record GitBranch(string Name, bool Remote, bool Current, string? Upstream, string When, string Subject)
{
    /// <summary>Reads for-each-ref's NUL separated fields, HEAD, refname, short name, upstream, date and subject.</summary>
    public static IReadOnlyList<GitBranch> Parse(string output)
    {
        var branches = new List<GitBranch>();
        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.Split('\0');
            if (fields.Length < 6)
                continue;

            // origin/HEAD is a pointer at another branch, not one to check out
            if (fields[1].EndsWith("/HEAD", StringComparison.Ordinal))
                continue;

            bool remote = fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal);
            branches.Add(new GitBranch(fields[2], remote, fields[0] == "*", fields[3].Length > 0 ? fields[3] : null, fields[4], fields[5]));
        }

        // The one checked out, then the local ones, then the remotes, each newest first as git sorted them
        return [.. branches.OrderByDescending(branch => branch.Current).ThenBy(branch => branch.Remote)];
    }
}

/// <summary>A commit that changed a file, who made it, when, its summary, and the path the file had in it (from the repository's root).</summary>
internal sealed record GitCommitOfFile(string Hash, string Author, DateTimeOffset When, string Subject, string Path)
{
    public string Short => Hash[..Math.Min(8, Hash.Length)];
}
