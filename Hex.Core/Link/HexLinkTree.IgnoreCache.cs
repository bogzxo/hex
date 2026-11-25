using System.Collections.Concurrent;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    // What Git said of a path last time, for the callers that ask the same paths again and again while nothing changes: the
    // empty-directory pass (every directory of the tree, each time) and the watcher's look at events under ignored folders.
    // Each unknown path costs a `git check-ignore` process, a known one a dictionary lookup.
    private const int MOST_IGNORE_ANSWERS = 50_000;
    private static readonly TimeSpan IgnoreAnswersLife = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, bool> ignoreAnswers = new(StringComparer.Ordinal);
    private ((long, long), (long, long), (long, long), (long, long)) ignoreAnswersFor;
    private long ignoreAnswersUntil;

    /// <summary>
    /// <see cref="Ignored"/> with the answers kept: dropped when a rules file moves (the root .gitignore, Git's exclude, Link's
    /// own file, or the index that says what's tracked), when the watcher sees a .gitignore anywhere change, or after a minute.
    /// </summary>
    public HashSet<string> IgnoredKept(IReadOnlyCollection<string> relative)
    {
        long now = Environment.TickCount64;
        var stamp = IgnoreStamp();
        lock (ignoreAnswers)
        {
            if (!stamp.Equals(ignoreAnswersFor) || now > ignoreAnswersUntil || ignoreAnswers.Count > MOST_IGNORE_ANSWERS)
            {
                ignoreAnswers.Clear();
                ignoreAnswersFor = stamp;
                ignoreAnswersUntil = now + (long)IgnoreAnswersLife.TotalMilliseconds;
            }
        }

        List<string>? unknown = null;
        foreach (string path in relative)
        {
            if (!ignoreAnswers.ContainsKey(path))
                (unknown ??= []).Add(path);
        }

        if (unknown is not null)
        {
            HashSet<string> said = Ignored(unknown);
            foreach (string path in unknown)
                ignoreAnswers[path] = said.Contains(path);
        }

        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in relative)
        {
            if (ignoreAnswers.TryGetValue(path, out bool answer) && answer)
                ignored.Add(path);
        }

        return ignored;
    }

    /// <summary>A .gitignore changed somewhere in the tree (told by the watcher): what's kept of what Git said is let go.</summary>
    internal void ForgetIgnoreAnswers() => ignoreAnswersUntil = 0;

    private ((long, long), (long, long), (long, long), (long, long)) IgnoreStamp()
    {
        (long Length, long Written) Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }

        var rules = Of(Path.Combine(Root, ".gitignore"));
        var link = Of(Path.Combine(Root, HexLinkIgnore.FileName));
        var exclude = Of(Path.Combine(Root, ".git", "info", "exclude"));
        var index = Of(Path.Combine(Root, ".git", "index"));
        return (rules, link, exclude, index);
    }
}
