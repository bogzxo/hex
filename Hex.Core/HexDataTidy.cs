using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>How much of the disk the agents' data takes, and what tidying it would take away.</summary>
/// <param name="Chats">The chats of every workspace with their older rows, and the rows no workspace keeps.</param>
/// <param name="Images">The pictures pasted into the agents' box.</param>
/// <param name="Temp">The agents' temporary files, where they are now and where they were.</param>
/// <param name="Cache">The harness's index and what else is cached.</param>
/// <param name="ChatCount">How many chats are kept, every workspace's.</param>
/// <param name="TempFiles">How many temporary files could go, and their size.</param>
public sealed record HexDataUsage(long Chats, long Images, long Temp, long Cache, int ChatCount, int ImageCount,
    (int Count, long Size) TempFiles)
{
    /// <summary>All of it.</summary>
    public long Total => Chats + Images + Temp + Cache;
}

/// <summary>A chat kept on the disk that hasn't moved since before a day asked about.</summary>
/// <param name="Folder">Its workspace's folder of chats.</param>
/// <param name="Key">Its key, its file is chat-key.json and its older rows key.jsonl.</param>
/// <param name="Size">Its file and its older rows.</param>
public readonly record struct HexOldChat(string Folder, string Key, long Size);

/// <summary>
/// The agents' data measured and tidied, off the editor's thread. Nothing here throws past it: a file that can't be read
/// or deleted is counted as it is and left.
/// </summary>
public static partial class HexDataTidy
{
    /// <summary>How recently a temporary file was written for it to be left, an agent may be starting on it.</summary>
    public static readonly TimeSpan TEMP_IN_USE = TimeSpan.FromMinutes(10);

    private const string LIST = "chats.json";

    [GeneratedRegex(@"pasted-[0-9]{8}-[0-9]{6}-[0-9]+\.png", RegexOptions.CultureInvariant)]
    private static partial Regex PastedName();

    /// <summary>
    /// The usage of the folders in the data folder as it is now. Only sizes and file times are read, it's asked for every
    /// half a minute while it's on screen, which pictures are unused (every chat read) is found only when they're to go.
    /// </summary>
    public static HexDataUsage Measure(DateTime now) => Measure(
        HexData.AgentChats, HexData.AgentHistory, HexData.AgentImages, [HexData.AgentTemp, HexData.OldAgentTemp], HexData.Cache, now);

    internal static HexDataUsage Measure(string chats, string history, string images, IReadOnlyList<string> temps, string cache, DateTime now)
    {
        int count = Files(chats, "chat-*.json").Count();
        var temp = OldTemp(temps, now);
        return new HexDataUsage(
            SizeOf(chats) + SizeOf(history), SizeOf(images), temps.Sum(SizeOf), SizeOf(cache), count, Files(images, "*.png").Count(),
            (temp.Count, temp.Sum(Size)));
    }

    /// <summary>The size of everything in a folder, nothing for one that isn't there.</summary>
    public static long SizeOf(string folder) => Files(folder, "*").Sum(Size);

    /// <summary>
    /// The chats of every workspace not written since <paramref name="before"/>. The workspace that's open
    /// (<paramref name="open"/>) has its chats in memory and only those named in <paramref name="closable"/> are said,
    /// the ones that aren't open, running or started by a chat that is, which the editor closes itself.
    /// </summary>
    public static List<HexOldChat> OldChats(string root, DateTime before, string? open, IReadOnlySet<string> closable)
    {
        var old = new List<HexOldChat>();
        if (!Directory.Exists(root))
            return old;

        foreach (string folder in Folders(root))
        {
            bool isOpen = open is not null && SamePlace(folder, open);
            foreach (string file in Files(folder, "chat-*.json", deep: false))
            {
                string key = Path.GetFileNameWithoutExtension(file)["chat-".Length..];
                if (isOpen && !closable.Contains(key))
                    continue;
                if (WrittenUtc(file) is not { } written || written >= before)
                    continue;
                old.Add(new HexOldChat(folder, key, Size(file) + Size(HistoryOf(folder, key))));
            }
        }

        return old;
    }

    /// <summary>
    /// The chats of workspaces that aren't open deleted, each with its older rows, and taken out of its workspace's list.
    /// A workspace with none left goes altogether. How many went.
    /// </summary>
    public static int DeleteChats(IEnumerable<HexOldChat> chats)
    {
        int gone = 0;
        foreach (var folder in chats.GroupBy(chat => chat.Folder))
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (HexOldChat chat in folder)
            {
                if (Delete(Path.Combine(folder.Key, $"chat-{chat.Key}.json")))
                {
                    Delete(HistoryOf(folder.Key, chat.Key));
                    keys.Add(chat.Key);
                    gone++;
                }
            }

            Unlist(folder.Key, keys);
        }

        return gone;
    }

    // The chats gone taken out of the list of their workspace, the workspace's folder gone when no chat is left in it
    private static void Unlist(string folder, HashSet<string> gone)
    {
        try
        {
            string list = Path.Combine(folder, LIST);
            if (File.Exists(list) && JsonNode.Parse(File.ReadAllText(list)) is JsonObject state && state["chats"] is JsonArray keys)
            {
                foreach (JsonNode? key in keys.ToArray())
                {
                    if (key is JsonValue value && value.TryGetValue(out string? said) && gone.Contains(said))
                        keys.Remove(key);
                }

                File.WriteAllText(list + ".new", state.ToJsonString());
                File.Move(list + ".new", list, overwrite: true);
            }

            if (!Directory.EnumerateFiles(folder, "chat-*.json").Any())
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// The pictures pasted into the box that no chat's file or older rows mention by name, and that aren't in
    /// <paramref name="keep"/> (the box's, not sent yet), nor pasted in the last <see cref="TEMP_IN_USE"/>, a chat that
    /// was just sent one may not have been written yet.
    /// </summary>
    public static List<string> UnusedImages(string images, IReadOnlyList<string> mentioning, IReadOnlyCollection<string> keep, DateTime now)
    {
        var unused = new List<string>();
        if (!Directory.Exists(images))
            return unused;

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in mentioning)
        {
            foreach (string file in Files(folder, "*"))
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    foreach (Match match in PastedName().Matches(File.ReadAllText(file)))
                        named.Add(match.Value);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a chat that can't be read may mention any of them, none go
                    return [];
                }
            }
        }

        var kept = new HashSet<string>(keep.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string picture in Files(images, "pasted-*.png", deep: false))
        {
            string name = Path.GetFileName(picture);
            if (!named.Contains(name) && !kept.Contains(name) && WrittenUtc(picture) is { } written && now.ToUniversalTime() - written > TEMP_IN_USE)
                unused.Add(picture);
        }

        return unused;
    }

    /// <summary>The temporary files not written in the last <see cref="TEMP_IN_USE"/>, which can go.</summary>
    public static List<string> OldTemp(IReadOnlyList<string> folders, DateTime now)
    {
        var old = new List<string>();
        foreach (string folder in folders)
        {
            foreach (string file in Files(folder, "*"))
            {
                if (WrittenUtc(file) is { } written && now.ToUniversalTime() - written > TEMP_IN_USE)
                    old.Add(file);
            }
        }

        return old;
    }

    /// <summary>Files deleted, those that won't (in use) left, and how many went.</summary>
    public static int DeleteFiles(IEnumerable<string> files) => files.Count(Delete);

    private static string HistoryOf(string folder, string key) => Path.Combine(folder, "history", $"{key}.jsonl");

    private static bool Delete(string file)
    {
        try
        {
            if (!File.Exists(file))
                return false;
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Folders(string root)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(root)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Files(string folder, string pattern, bool deep = true)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return [.. Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = deep, IgnoreInaccessible = true })];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long Size(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime? WrittenUtc(string file)
    {
        try
        {
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SamePlace(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Horizon.Core.Platform.PlatformPaths.Comparison);

    /// <summary>A size as people read it, 1.2 MB.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => FormattableString.Invariant($"{bytes} B"),
        < 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => FormattableString.Invariant($"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => FormattableString.Invariant($"{bytes / (1024.0 * 1024 * 1024):0.##} GB")
    };
}
