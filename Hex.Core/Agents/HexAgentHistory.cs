using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The rows a chat no longer keeps in memory, in a file of its own, a JSON row a line, oldest first. A chat keeps its
/// latest rows and sends the oldest here a batch at a time (HexAgentChat.Spill), and they're read back only when somebody
/// scrolls to them. The file goes with the chat. Anything that can't be written or read is let go of, a chat must never
/// stop for its history.
/// </summary>
internal sealed class HexAgentHistory(string folder)
{
    /// <summary>The folder the files are in.</summary>
    public string Folder { get; } = folder;

    private string FileOf(string key) => Path.Combine(Folder, $"{key}.jsonl");

    /// <summary>Rows put at the end of a chat's file.</summary>
    public void Append(string key, IEnumerable<HexAgentEntry> rows)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using var writer = new StreamWriter(FileOf(key), append: true);
            foreach (HexAgentEntry row in rows)
                writer.WriteLine(HexAgentChatStore.RowOf(row).ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>How many rows a chat's file has.</summary>
    public int Count(string key)
    {
        try
        {
            return File.Exists(FileOf(key)) ? File.ReadLines(FileOf(key)).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Some of a chat's rows read back, <paramref name="count"/> of them ending <paramref name="before"/> rows from the end of
    /// the file, oldest first. Fewer, or none, when the file hasn't that many.
    /// </summary>
    public List<HexAgentEntry> Read(string key, int before, int count)
    {
        var rows = new List<HexAgentEntry>();
        try
        {
            if (!File.Exists(FileOf(key)))
                return rows;
            string[] lines = [.. File.ReadLines(FileOf(key))];
            int end = Math.Max(0, lines.Length - before), start = Math.Max(0, end - count);
            for (int i = start; i < end; i++)
            {
                if (HexAgentChatStore.EntryOf(JsonNode.Parse(lines[i])) is { } row)
                    rows.Add(row);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }

        return rows;
    }

    /// <summary>
    /// Files of chats that are gone, left when Hex closed with them open, taken away once they're a couple of days old.
    /// A Hex restarting into its own build hands its chats on at once, so their files are never that old.
    /// </summary>
    public void Tidy(TimeSpan older)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (string file in Directory.EnumerateFiles(Folder, "*.jsonl"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > older)
                    File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A chat's file gone with the chat.</summary>
    public void Delete(string key)
    {
        try
        {
            File.Delete(FileOf(key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
