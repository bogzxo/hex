using System.Globalization;

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
    public static IReadOnlyList<GitBlameLine> Parse(string porcelain) => Parse(porcelain.AsSpan());

    public static IReadOnlyList<GitBlameLine> Parse(ReadOnlySpan<char> porcelain)
    {
        var lines = new List<GitBlameLine>();
        var commits = new Dictionary<string, GitBlameLine>(StringComparer.Ordinal);
        var commitsLookup = commits.GetAlternateLookup<ReadOnlySpan<char>>();
        var strings = new HashSet<string>(StringComparer.Ordinal);
        var stringsLookup = strings.GetAlternateLookup<ReadOnlySpan<char>>();

        GitBlameLine? currentKnown = null;
        string? hash = null;
        string author = string.Empty, mail = string.Empty, summary = string.Empty;
        long time = 0;
        int zone = 0;

        int pos = 0;
        while (pos < porcelain.Length)
        {
            int nl = porcelain[pos..].IndexOf('\n');
            ReadOnlySpan<char> raw;
            if (nl < 0)
            {
                raw = porcelain[pos..];
                pos = porcelain.Length;
            }
            else
            {
                raw = porcelain.Slice(pos, nl);
                pos += nl + 1;
            }

            if (raw.Length > 0 && raw[^1] == '\r')
                raw = raw[..^1];
            if (raw.IsEmpty)
                continue;

            // The line itself, which ends what's said about it
            if (raw[0] == '\t')
            {
                if (currentKnown is not null)
                {
                    lines.Add(currentKnown);
                    continue;
                }

                if (hash is null)
                    continue;

                if (!commits.TryGetValue(hash, out GitBlameLine? line))
                {
                    var when = DateTimeOffset.FromUnixTimeSeconds(time).ToOffset(TimeSpan.FromMinutes(zone));
                    line = IsAllZeros(hash.AsSpan())
                        ? new GitBlameLine(null, author, mail, when, summary)
                        : new GitBlameLine(hash, author, mail, when, summary);
                    commits[hash] = line;
                }

                currentKnown = line;
                lines.Add(line);
                continue;
            }

            int space = raw.IndexOf(' ');
            ReadOnlySpan<char> key = space < 0 ? raw : raw[..space];

            // A header, a hash and then numbers
            // 40 characters for SHA-1, 64 for a repository made with SHA-256
            if (key.Length is 40 or 64 && IsHash(key))
            {
                if (commitsLookup.TryGetValue(key, out GitBlameLine? known))
                {
                    currentKnown = known;
                    hash = null;
                }
                else
                {
                    currentKnown = null;
                    hash = key.ToString();
                }
                continue;
            }

            if (currentKnown is not null)
                continue;

            ReadOnlySpan<char> value = space < 0 ? ReadOnlySpan<char>.Empty : raw[(space + 1)..];
            if (key.SequenceEqual("author"))
                author = Intern(stringsLookup, value);
            else if (key.SequenceEqual("author-mail"))
                mail = Intern(stringsLookup, value.Trim("<>"));
            else if (key.SequenceEqual("author-time"))
                long.TryParse(value, CultureInfo.InvariantCulture, out time);
            else if (key.SequenceEqual("author-tz"))
                zone = Zone(value);
            else if (key.SequenceEqual("summary"))
                summary = value.IsEmpty ? string.Empty : value.ToString();
        }

        return lines;
    }

    private static string Intern(HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return string.Empty;
        if (lookup.TryGetValue(value, out string? existing))
            return existing;
        string created = value.ToString();
        lookup.Set.Add(created);
        return created;
    }

    private static bool IsHash(ReadOnlySpan<char> word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(word[i]))
                return false;
        }

        return true;
    }

    private static bool IsAllZeros(ReadOnlySpan<char> word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (word[i] != '0')
                return false;
        }

        return true;
    }

    // +0200 as minutes
    private static int Zone(ReadOnlySpan<char> value)
    {
        if (value.Length != 5
            || !int.TryParse(value.Slice(1, 2), CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(value.Slice(3, 2), CultureInfo.InvariantCulture, out int minutes))
            return 0;
        int total = hours * 60 + minutes;
        return value[0] == '-' ? -total : total;
    }
}

/// <summary>A branch, here or on a remote, which is checked out, what it follows, and when it last had a commit.</summary>
internal sealed record GitBranch(string Name, bool Remote, bool Current, string? Upstream, string When, string Subject)
{
    /// <summary>Reads for-each-ref's NUL separated fields, HEAD, refname, short name, upstream, date and subject.</summary>
    public static IReadOnlyList<GitBranch> Parse(string output) => Parse(output.AsSpan());

    /// <summary>Reads for-each-ref's NUL separated fields directly from a character span.</summary>
    public static IReadOnlyList<GitBranch> Parse(ReadOnlySpan<char> output)
    {
        List<GitBranch>? currentBranches = null;
        List<GitBranch>? localBranches = null;
        List<GitBranch>? remoteBranches = null;

        int pos = 0;
        while (pos < output.Length)
        {
            int nl = output[pos..].IndexOf('\n');
            ReadOnlySpan<char> line;
            if (nl < 0)
            {
                line = output[pos..];
                pos = output.Length;
            }
            else
            {
                line = output.Slice(pos, nl);
                pos += nl + 1;
            }

            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];
            if (line.IsEmpty)
                continue;

            int n0 = line.IndexOf('\0');
            if (n0 < 0) continue;
            ReadOnlySpan<char> head = line[..n0];
            ReadOnlySpan<char> r1 = line[(n0 + 1)..];

            int n1 = r1.IndexOf('\0');
            if (n1 < 0) continue;
            ReadOnlySpan<char> refname = r1[..n1];
            // origin/HEAD is a pointer at another branch, not one to check out
            if (refname.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;

            ReadOnlySpan<char> r2 = r1[(n1 + 1)..];
            int n2 = r2.IndexOf('\0');
            if (n2 < 0) continue;
            ReadOnlySpan<char> shortName = r2[..n2];

            ReadOnlySpan<char> r3 = r2[(n2 + 1)..];
            int n3 = r3.IndexOf('\0');
            if (n3 < 0) continue;
            ReadOnlySpan<char> upstream = r3[..n3];

            ReadOnlySpan<char> r4 = r3[(n3 + 1)..];
            int n4 = r4.IndexOf('\0');
            if (n4 < 0) continue;
            ReadOnlySpan<char> when = r4[..n4];

            ReadOnlySpan<char> r5 = r4[(n4 + 1)..];
            int n5 = r5.IndexOf('\0');
            ReadOnlySpan<char> subject = n5 >= 0 ? r5[..n5] : r5;

            bool remote = refname.StartsWith("refs/remotes/", StringComparison.Ordinal);
            bool current = head.SequenceEqual("*");
            var branch = new GitBranch(
                shortName.ToString(),
                remote,
                current,
                upstream.Length > 0 ? upstream.ToString() : null,
                when.ToString(),
                subject.ToString());

            if (current)
                (currentBranches ??= new List<GitBranch>(1)).Add(branch);
            else if (!remote)
                (localBranches ??= new List<GitBranch>()).Add(branch);
            else
                (remoteBranches ??= new List<GitBranch>()).Add(branch);
        }

        int total = (currentBranches?.Count ?? 0) + (localBranches?.Count ?? 0) + (remoteBranches?.Count ?? 0);
        if (total == 0)
            return [];

        // The one checked out, then the local ones, then the remotes, each newest first as git sorted them
        var ordered = new GitBranch[total];
        int at = 0;
        if (currentBranches is not null)
        {
            for (int i = 0; i < currentBranches.Count; i++)
                ordered[at++] = currentBranches[i];
        }
        if (localBranches is not null)
        {
            for (int i = 0; i < localBranches.Count; i++)
                ordered[at++] = localBranches[i];
        }
        if (remoteBranches is not null)
        {
            for (int i = 0; i < remoteBranches.Count; i++)
                ordered[at++] = remoteBranches[i];
        }
        return ordered;
    }
}

/// <summary>A commit that changed a file, who made it, when, its summary, and the path the file had in it (from the repository's root).</summary>
internal sealed record GitCommitOfFile(string Hash, string Author, DateTimeOffset When, string Subject, string Path)
{
    public string Short => Hash[..Math.Min(8, Hash.Length)];
}
