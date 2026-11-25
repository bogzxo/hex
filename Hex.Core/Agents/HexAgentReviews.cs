using System.Collections.Concurrent;
using System.Text.Json;

using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// What an agent's edits are reviewed against, per chat: how each file was the first time that chat touched it, kept until
/// its hunks are accepted (the baseline moves up to what the file is now) or rejected (the baseline's lines put back).
/// Zed's review of agent edits, the agent writes straight to the files and the person settles the hunks after.
/// </summary>
internal sealed partial class HexAgentReviews
{
    /// <summary>The reviews of this Hex, what the edit tools and the Agents panel share.</summary>
    public static HexAgentReviews Shared { get; } = new();

    /// <summary>The largest file kept a baseline of, a bigger one is no hand-reviewed edit.</summary>
    public const int MOST_BYTES = 2_000_000;

    // Each chat's files and what their baselines are known by in the blobs, which keep a text once whoever has it
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, string>> baselines = new();
    private readonly ReviewBlobs blobs = new();

    // Per file, how much a baseline differs from a text, remembered by what the two say so a summary drawn again makes no diff
    private readonly Lock statsLock = new();
    private readonly Dictionary<(string Baseline, string Current), (int Added, int Removed, bool Differs)> stats = [];

    /// <summary>Told that a chat's baselines changed (a file was first touched, a hunk was settled), for the panel to draw again.</summary>
    public event Action<int>? Changed;

    /// <summary>
    /// A file about to be changed by a chat, its text as it is now kept as the baseline unless the chat has one for it already.
    /// <paramref name="text"/> is null for a file that isn't there yet, which is a baseline of nothing.
    /// </summary>
    public bool Record(int chat, string path, string? text)
    {
        if (path.Length == 0 || (text is not null && text.Length > MOST_BYTES))
            return false;

        ConcurrentDictionary<string, string> files = baselines.GetOrAdd(chat, _ => new(StringComparer.OrdinalIgnoreCase));
        if (files.ContainsKey(path))
            return false;

        string hash = blobs.Add(text ?? string.Empty);
        if (!files.TryAdd(path, hash))
        {
            blobs.Release(hash);
            return false;
        }

        Moved(chat);
        return true;
    }

    // A file's baseline put to a text, the one it had let go of
    private void Swap(ConcurrentDictionary<string, string> files, string path, string text)
    {
        string hash = blobs.Add(text);
        string? old = null;
        files.AddOrUpdate(path, hash, (_, was) =>
        {
            old = was;
            return hash;
        });
        if (old is not null)
            blobs.Release(old);
    }

    /// <summary>How many different texts the baselines come to, however many chats and files hold them; for the tests.</summary>
    internal int DistinctTexts => blobs.Count;

    /// <summary>
    /// A tool call of a chat seen, the baseline of the file it names (an absolute path) taken from the disk if it changes
    /// files. A call that's already been seen by the harness has its file kept already, the first one stands.
    /// </summary>
    public void Observe(int chat, string? tool, string? input)
    {
        foreach (string path in PathsOf(tool, input))
        {
            if (Path.IsPathRooted(path))
                Record(chat, path, TextOnDisk(path));
        }
    }

    /// <summary>A file's text on the disk, null for one that isn't there or can't be read.</summary>
    public static string? TextOnDisk(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= MOST_BYTES ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Baseline(int chat, string path) =>
        baselines.TryGetValue(chat, out var files) && files.TryGetValue(path, out string? hash) ? blobs.Get(hash) : null;

    // What a baseline is known by, null for a file the chat has none for
    private string? HashOfBaseline(int chat, string path) =>
        baselines.TryGetValue(chat, out var files) && files.TryGetValue(path, out string? hash) ? hash : null;

    /// <summary>The files a chat has a baseline for, in path order.</summary>
    public IReadOnlyList<string> Paths(int chat) =>
        baselines.TryGetValue(chat, out var files) ? [.. files.Keys.Order(StringComparer.OrdinalIgnoreCase)] : [];

    /// <summary>A file's baseline moved, to what <see cref="Accept"/> made of it.</summary>
    public void SetBaseline(int chat, string path, string text)
    {
        if (baselines.TryGetValue(chat, out var files) && files.ContainsKey(path))
        {
            Swap(files, path, text);
            Moved(chat);
        }
    }

    /// <summary>A file no longer reviewed, all its hunks settled.</summary>
    public void Drop(int chat, string path)
    {
        if (baselines.TryGetValue(chat, out var files) && files.TryRemove(path, out string? hash))
        {
            blobs.Release(hash);
            Moved(chat);
        }
    }

    public void Forget(int chat)
    {
        if (baselines.TryRemove(chat, out var files))
        {
            foreach (string hash in files.Values)
                blobs.Release(hash);
            Moved(chat);
        }
    }

    /// <summary>The hunks of a file, its baseline against <paramref name="current"/>, none when it's the same.</summary>
    public IReadOnlyList<GitHunk> Hunks(int chat, string path, string current) =>
        Baseline(chat, path) is { } baseline ? HunksOf(baseline, current) : [];

    public static List<GitHunk> HunksOf(string baseline, string current) => GitLineDiff.Diff(Split(baseline), Split(current));

    /// <summary>How many files differ from their baselines and the lines added and taken out in all of them.</summary>
    public (int Files, int Added, int Removed) Totals(int chat, Func<string, string?> currentOf, Func<string, long>? stampOf = null)
    {
        int files = 0, added = 0, removed = 0;
        foreach ((_, int fileAdded, int fileRemoved) in Differing(chat, currentOf, stampOf))
        {
            files++;
            added += fileAdded;
            removed += fileRemoved;
        }

        return (files, added, removed);
    }

    /// <summary>
    /// Each file that differs from its baseline, with the lines added and taken out in it. What a baseline and a text came
    /// to is remembered by what they say, so asking again of files that haven't changed diffs nothing.
    /// </summary>
    public List<(string Path, int Added, int Removed)> Differing(int chat, Func<string, string?> currentOf, Func<string, long>? stampOf = null)
    {
        var differing = new List<(string, int, int)>();
        foreach (string path in Paths(chat))
        {
            if (HashOfBaseline(chat, path) is not { } hash)
                continue;

            // A text whose stamp (an open file's version, a disk file's size and time) is what it was when last asked of
            // is not read, hashed or diffed again: the answer it came to is the answer
            long stamp = stampOf?.Invoke(path) ?? 0;
            if (stamp != 0)
            {
                (long Stamp, string Hash, (int Added, int Removed, bool Differs) Found) last;
                bool seen;
                lock (statsLock)
                    seen = stamped.TryGetValue((chat, path), out last);
                if (seen && last.Stamp == stamp && last.Hash == hash)
                {
                    if (last.Found.Differs)
                        differing.Add((path, last.Found.Added, last.Found.Removed));
                    continue;
                }
            }

            string? current = currentOf(path);
            if (current is null)
                continue;

            var key = (hash, ReviewBlobs.HashOf(current));
            bool known;
            (int Added, int Removed, bool Differs) found;
            lock (statsLock)
                known = stats.TryGetValue(key, out found);
            if (!known)
            {
                if (blobs.Get(hash) is not { } baseline)
                    continue;

                List<GitHunk> hunks = HunksOf(baseline, current);
                found = (hunks.Sum(hunk => hunk.NewCount), hunks.Sum(hunk => hunk.OldCount), hunks.Count > 0);
                lock (statsLock)
                {
                    if (stats.Count >= MOST_STATS)
                        stats.Clear();
                    stats[key] = found;
                }
            }

            if (stamp != 0)
            {
                lock (statsLock)
                {
                    if (stamped.Count >= MOST_STAMPED)
                        stamped.Clear();
                    stamped[(chat, path)] = (stamp, hash, found);
                }
            }

            if (found.Differs)
                differing.Add((path, found.Added, found.Removed));
        }

        return differing;
    }

    // The most files whose answer is kept by a stamp, and those answers, under statsLock
    private const int MOST_STAMPED = 256;
    private readonly Dictionary<(int Chat, string Path), (long Stamp, string Hash, (int Added, int Removed, bool Differs) Found)> stamped = new();

    // The most pairs of texts whose difference is remembered
    private const int MOST_STATS = 64;

    /// <summary>
    /// The text with a hunk put back the way it was in the baseline, one hunk rejected. The line endings and the last
    /// newline are the current text's.
    /// </summary>
    public static string Reject(string baseline, string current, GitHunk hunk)
    {
        string[] old = Split(baseline), now = Split(current);
        var lines = new List<string>(now.Length + old.Length);
        lines.AddRange(now.Take(hunk.NewStart));
        lines.AddRange(old.Skip(hunk.OldStart).Take(hunk.OldCount));
        lines.AddRange(now.Skip(hunk.NewStart + hunk.NewCount));
        return Join(lines, current);
    }

    /// <summary>The baseline with a hunk taken into it, what it'll be after the hunk is accepted.</summary>
    public static string Accept(string baseline, string current, GitHunk hunk)
    {
        string[] old = Split(baseline), now = Split(current);
        var lines = new List<string>(now.Length + old.Length);
        lines.AddRange(old.Take(hunk.OldStart));
        lines.AddRange(now.Skip(hunk.NewStart).Take(hunk.NewCount));
        lines.AddRange(old.Skip(hunk.OldStart + hunk.OldCount));
        return Join(lines, current);
    }

    /// <summary>The files a tool's arguments name, the ones its edit changes, empty for a tool that names none.</summary>
    public static List<string> PathsOf(string? toolName, string? input)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(input) || !IsEdit(toolName))
            return paths;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException)
        {
            // Codex's file change is a line a file, "update C:\x\y.cs", the kind and then the path
            if (toolName!.Contains("file_change", StringComparison.OrdinalIgnoreCase) || toolName.Contains("filechange", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] words = line.Split(' ');
                    for (int i = 0; i < words.Length; i++)
                    {
                        string rest = string.Join(' ', words.Skip(i));
                        if (Path.IsPathRooted(rest))
                        {
                            paths.Add(rest);
                            break;
                        }
                    }
                }
            }

            return paths;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return paths;

            foreach (string key in (string[])["path", "file_path", "filePath", "file", "notebook_path", "TargetFile", "AbsolutePath", "FilePath", "targetFile"])
            {
                if (document.RootElement.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                {
                    paths.Add(text);
                    break;
                }
            }
        }

        return paths;
    }

    /// <summary>Whether a tool changes a file, by its name, the edits and writes of every agent system.</summary>
    public static bool IsEdit(string? tool)
    {
        string name = tool ?? string.Empty;
        if (name.Contains("editor", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.Contains("file_change", StringComparison.OrdinalIgnoreCase) || name.Contains("filechange", StringComparison.OrdinalIgnoreCase)
            || name.Contains("edit", StringComparison.OrdinalIgnoreCase) || name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("patch", StringComparison.OrdinalIgnoreCase) || name.Contains("replace", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Split(string text) => Lines(text);

    /// <summary>The lines of a text, none for an empty one.</summary>
    public static string[] Lines(string text) => text.Length == 0 ? [] : GitLineDiff.Lines(text);

    private static string Join(List<string> lines, string like)
    {
        string ending = like.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string joined = string.Join(ending, lines);
        return lines.Count > 0 && like.EndsWith('\n') ? joined + ending : joined;
    }
}
