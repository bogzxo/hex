using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The chats of a workspace kept on disk, so they're there again when the workspace is opened next, after Hex was closed,
/// crashed or the machine went down. A folder a workspace (named by its first folder), a file a chat written only when
/// that chat moved, and a list of them in order with which one was open. The rows a chat no longer keeps in memory are in
/// the folder's history beside them, so a chat and its older rows go together. Writes go to a file beside the one they
/// replace and are moved over it, a chat half written is never read back. Anything that can't be written or read is let go
/// of, chats must never stop for their files.
/// </summary>
internal sealed class HexAgentChatsKept
{
    /// <summary>The most chats a workspace keeps, the oldest that aren't running let go of past it.</summary>
    public const int MOST_CHATS = 40;

    private const string LIST = "chats.json";

    // What each chat was written at, by its key, so only the ones that moved are written again
    private readonly Dictionary<string, long> written = new(StringComparer.Ordinal);
    private string listWritten = string.Empty;

    public HexAgentChatsKept(string folder)
    {
        Folder = folder;
        History = new HexAgentHistory(Path.Combine(folder, "history"));
    }

    /// <summary>The workspace's folder of chats.</summary>
    public string Folder { get; }

    /// <summary>Where its chats' older rows are.</summary>
    public HexAgentHistory History { get; }

    /// <summary>
    /// The folder of a workspace's chats under <paramref name="root"/>, named by the workspace's first folder (the one it
    /// keeps through folders added and taken out beside it), "none" for a workspace with no folder.
    /// </summary>
    public static string FolderFor(string root, string? workspaceFolder)
    {
        if (workspaceFolder is not { Length: > 0 })
            return Path.Combine(root, "none");

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceFolder));
        string said = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(said)), 0, 8).ToLowerInvariant();
        // The folder's name too, so somebody looking in can tell which is which
        string name = new([.. Path.GetFileName(full).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(32)]);
        return Path.Combine(root, name.Length > 0 ? $"{name}-{hash}" : hash);
    }

    private string FileOf(string key) => Path.Combine(Folder, $"chat-{key}.json");

    /// <summary>
    /// The chats as they are now kept, each written again only when it moved since it was, the files of chats that are gone
    /// taken away, and the list written when it changed. <paramref name="state"/> is what goes with the list (the chat that
    /// was open, how many were made).
    /// </summary>
    public void Keep(IReadOnlyList<HexAgentChat> chats, JsonObject state)
    {
        // What moved made into text here, on the thread that owns the chats, and written to the disk on a thread of its own
        // in the order it was made. Written here, a streaming agent's chat was a file write every few seconds on the
        // editor's thread
        var writes = new List<(string Path, string? Text)>();
        var keys = new JsonArray();
        var now = new HashSet<string>(StringComparer.Ordinal);
        foreach (HexAgentChat chat in chats)
        {
            now.Add(chat.Key);
            keys.Add((JsonNode)chat.Key);
            long stamp = Stamp(chat);
            if (written.TryGetValue(chat.Key, out long was) && was == stamp)
                continue;

            writes.Add((FileOf(chat.Key), HexAgentChatStore.Write(chat).ToJsonString()));
            written[chat.Key] = stamp;
        }

        // Its older rows went with it when it was closed, one only past the most kept has them tidied as it's read back
        foreach (string gone in written.Keys.Where(key => !now.Contains(key)).ToArray())
        {
            written.Remove(gone);
            writes.Add((FileOf(gone), null));
        }

        state["chats"] = keys;
        string list = state.ToJsonString();
        if (list != listWritten)
        {
            writes.Add((Path.Combine(Folder, LIST), list));
            listWritten = list;
        }

        if (writes.Count == 0)
            return;
        string folder = Folder;
        lock (writing)
            lastWrite = lastWrite.ContinueWith(_ => WriteAll(folder, writes), TaskScheduler.Default);
    }

    // The writes made so far, one after another
    private readonly Lock writing = new();
    private Task lastWrite = Task.CompletedTask;

    // A batch of writes, a file's text or its deletion for null, never throwing past here
    private static void WriteAll(string folder, List<(string Path, string? Text)> writes)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var (path, text) in writes)
            {
                if (text is null)
                    File.Delete(path);
                else
                    WriteOver(path, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Waits for what's been kept to be on the disk, as Hex closes or before another folder's chats are read.</summary>
    public void Flush()
    {
        Task last;
        lock (writing)
            last = lastWrite;
        last.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The chats kept, in their order, not running, and what went with the list (null when there's none). Files of chats the
    /// list doesn't name, and older rows of chats that aren't kept, are taken away.
    /// </summary>
    public (List<HexAgentChat> Chats, JsonObject? State) Read()
    {
        var chats = new List<HexAgentChat>();
        JsonObject? state = null;
        try
        {
            string list = Path.Combine(Folder, LIST);
            if (!File.Exists(list))
                return (chats, null);

            state = JsonNode.Parse(File.ReadAllText(list)) as JsonObject;
            // Every chat the list names is kept on the disk whether it reads or not. One that didn't read was left out of
            // what was kept and its file and history deleted as nobody's
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? key in state?["chats"] as JsonArray ?? [])
            {
                if (key is not JsonValue value || !value.TryGetValue(out string? said) || said is not { Length: > 0 } name)
                    continue;
                named.Add(name);
                if (!File.Exists(FileOf(name)))
                    continue;
                // A chat that doesn't read is skipped, a value of the wrong kind threw past this and lost every other chat
                try
                {
                    if (HexAgentChatStore.Read(JsonNode.Parse(File.ReadAllText(FileOf(name)))) is { } chat)
                    {
                        chats.Add(chat);
                        written[chat.Key] = Stamp(chat);
                    }
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or IOException)
                {
                }
            }

            listWritten = state?.ToJsonString() ?? string.Empty;
            Tidy(named);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }

        return (chats, state);
    }

    // The files nothing kept names any more, left by a Hex that went down between a chat going and the list being written
    private void Tidy(HashSet<string> keys)
    {
        foreach (string file in Directory.EnumerateFiles(Folder, "chat-*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(file)["chat-".Length..];
            if (!keys.Contains(key))
                File.Delete(file);
        }

        if (Directory.Exists(History.Folder))
        {
            foreach (string file in Directory.EnumerateFiles(History.Folder, "*.jsonl"))
            {
                if (!keys.Contains(Path.GetFileNameWithoutExtension(file)))
                    File.Delete(file);
            }
        }
    }

    /// <summary>
    /// The chats past <see cref="MOST_CHATS"/> to let go of, the oldest first, never one that's running or one another kept
    /// chat was started by.
    /// </summary>
    public static List<HexAgentChat> PastMost(IReadOnlyList<HexAgentChat> chats)
    {
        var over = new List<HexAgentChat>();
        int extra = chats.Count - MOST_CHATS;
        foreach (HexAgentChat chat in chats)
        {
            if (extra <= 0)
                break;
            if (chat.Running || chats.Any(other => other.Parent == chat.Id && !over.Contains(other)))
                continue;
            over.Add(chat);
            extra--;
        }

        return over;
    }

    // What a chat is at, moved whenever anything of it that's written does
    private static long Stamp(HexAgentChat chat) => chat.Version * 4L + (chat.Running ? 1 : 0) + (chat.Stopped ? 2 : 0);

    // Written beside and moved over, so a file is either what it was or what it's to be
    private static void WriteOver(string file, string text)
    {
        string beside = file + ".new";
        File.WriteAllText(beside, text);
        File.Move(beside, file, overwrite: true);
    }
}
