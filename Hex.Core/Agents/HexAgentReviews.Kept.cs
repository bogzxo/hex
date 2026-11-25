using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

// What the reviews keep on the disk beside the workspace's chats, a file a chat (reviews/review-<chat id>.json, a path and
// its baseline each), written when that chat's baselines moved and read back as the chats are. A chat closed has its file
// taken away. Anything that can't be written or read is let go of, a review must never stop a chat.
internal sealed partial class HexAgentReviews
{
    private readonly HashSet<int> moved = [];
    private readonly Lock movedLock = new();

    // The files a tool call's baselines were first taken for, to look at again if the call turns out to have been told after it ran
    private readonly ConditionalWeakTable<HexAgentEntry, List<string>> fresh = [];

    /// <summary>
    /// What a file was before the agent changed it, asked of git when a call came in already done and the disk's text is
    /// after the edit. Null for no way to say.
    /// </summary>
    public Func<string, string?>? LateBaseline { get; set; }

    /// <summary>How long after a call was told it may be told done and still have been told after it ran, in milliseconds.</summary>
    public const long ARRIVED_DONE_MS = 2;

    private void Moved(int chat)
    {
        lock (movedLock)
            moved.Add(chat);
        Changed?.Invoke(chat);
    }

    private static string FileOf(string folder, int chat) => Path.Combine(folder, "reviews", $"review-{chat}.json");

    /// <summary>The chats whose baselines moved written (or their files taken away when none are left), in <paramref name="folder"/>.</summary>
    public void Keep(string folder)
    {
        int[] chats;
        lock (movedLock)
        {
            chats = [.. moved];
            moved.Clear();
        }

        foreach (int chat in chats)
        {
            string file = FileOf(folder, chat);
            try
            {
                if (!baselines.TryGetValue(chat, out var files) || files.IsEmpty)
                {
                    File.Delete(file);
                    continue;
                }

                // The chat's file lists its files by what their baselines are known by, the texts being files of their own
                // that every chat with the same one shares
                var list = new JsonObject();
                foreach (var (path, hash) in files)
                {
                    WriteBlob(folder, hash);
                    list[path] = hash;
                }
                var written = new JsonObject { ["format"] = 2, ["files"] = list };
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string temp = file + ".tmp";
                File.WriteAllText(temp, written.ToJsonString());
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lock (movedLock)
                    moved.Add(chat);
            }
        }

        // Texts no chat holds any more go from the disk too
        string[] onDisk;
        lock (movedLock)
            onDisk = [.. blobsWritten];
        foreach (string hash in onDisk)
        {
            if (blobs.Has(hash))
                continue;
            try
            {
                File.Delete(BlobFile(folder, hash));
                lock (movedLock)
                    blobsWritten.Remove(hash);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Taken away the next time
            }
        }
    }

    // The texts known to be on the disk, in the folder that's kept to
    private readonly HashSet<string> blobsWritten = new(StringComparer.Ordinal);

    private static string BlobFile(string folder, string hash) => Path.Combine(folder, "reviews", "blobs", hash + ".br");

    private void WriteBlob(string folder, string hash)
    {
        string file = BlobFile(folder, hash);
        lock (movedLock)
        {
            if (blobsWritten.Contains(hash))
                return;
        }

        if (blobs.PackedOf(hash) is not { } packed)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            string temp = file + ".tmp";
            File.WriteAllBytes(temp, packed);
            File.Move(temp, file, overwrite: true);
        }

        blobs.MarkWrittenToDisk(hash, file);
        lock (movedLock)
            blobsWritten.Add(hash);
    }

    /// <summary>The baselines of these chats read back from <paramref name="folder"/>, those in memory already left as they are.</summary>
    public void Load(string folder, IEnumerable<int> chats)
    {
        foreach (int chat in chats)
        {
            try
            {
                string file = FileOf(folder, chat);
                if (!File.Exists(file))
                    continue;

                using FileStream stream = File.OpenRead(file);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                ConcurrentDictionaryOf(chat, out var files);
                if (root.TryGetProperty("format", out _)
                    && root.TryGetProperty("files", out JsonElement listed)
                    && listed.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty entry in listed.EnumerateObject())
                    {
                        string path = entry.Name;
                        if (entry.Value.ValueKind != JsonValueKind.String
                            || entry.Value.GetString() is not { Length: > 0 } said
                            || files.ContainsKey(path))
                            continue;

                        string blob = BlobFile(folder, said);
                        if (blobs.Has(said))
                        {
                            blobs.AddDiskBacked(said, blob);
                        }
                        else if (File.Exists(blob))
                        {
                            // Keep compressed baseline bytes on disk until the review is actually inspected.
                            blobs.AddDiskBacked(said, blob);
                            lock (movedLock)
                                blobsWritten.Add(said);
                        }
                        else
                        {
                            continue;
                        }

                        if (!files.TryAdd(path, said))
                            blobs.Release(said);
                    }
                    continue;
                }

                // The first format: each file's text itself
                foreach (JsonProperty entry in root.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } said)
                    {
                        string hash = blobs.Add(said);
                        if (!files.TryAdd(entry.Name, hash))
                            blobs.Release(hash);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file half written, the chat has no review of it
            }
        }
    }

    private void ConcurrentDictionaryOf(int chat, out System.Collections.Concurrent.ConcurrentDictionary<string, string> files) =>
        files = baselines.GetOrAdd(chat, _ => new(StringComparer.OrdinalIgnoreCase));

    /// <summary>Every baseline let go of from memory without a word and without touching the files, for another workspace's chats to come in.</summary>
    public void Unload()
    {
        baselines.Clear();
        blobs.Clear();
        lock (statsLock)
        {
            stats.Clear();
            stats.TrimExcess();
            stamped.Clear();
            stamped.TrimExcess();
        }
        lock (movedLock)
        {
            moved.Clear();
            moved.TrimExcess();
            blobsWritten.Clear();
            blobsWritten.TrimExcess();
        }
    }

    /// <summary>
    /// A tool call seen of a chat, the baselines of the files its input names taken from the disk. The files it took a
    /// baseline of are remembered for the call, see <see cref="Told"/>.
    /// </summary>
    public void Observe(int chat, HexAgentEntry entry)
    {
        List<string>? took = null;
        foreach (string path in PathsOf(entry.ToolName, entry.Input))
        {
            if (Path.IsPathRooted(path) && Record(chat, path, TextOnDisk(path)))
                (took ??= []).Add(path);
        }

        if (took is not null)
            fresh.AddOrUpdate(entry, took);
    }

    /// <summary>
    /// A call told done. One that was told no sooner than it was seen came in already done, the file is after the edit
    /// by then and the baselines taken just now are of the edited file, so they're replaced by what
    /// <see cref="LateBaseline"/> says the file was (git's), where it can say.
    /// </summary>
    public void Told(int chat, HexAgentEntry entry)
    {
        if (!fresh.TryGetValue(entry, out List<string>? took) || entry.Ended - entry.Started > ARRIVED_DONE_MS || LateBaseline is not { } before)
            return;

        fresh.Remove(entry);
        foreach (string path in took)
        {
            if (before(path) is { } text && text.Length <= MOST_BYTES && baselines.TryGetValue(chat, out var files) && files.ContainsKey(path))
            {
                Swap(files, path, text);
                Moved(chat);
            }
        }
    }
}
