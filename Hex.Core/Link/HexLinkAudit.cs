using System.Globalization;
using System.Text;

namespace Hex.Core;

/// <summary>A line of the audit log.</summary>
internal sealed record HexLinkAuditEntry(DateTime Time, string Event, string Device, string Detail);

/// <summary>
/// What happened over Link, a line an event in <c>data/link/audit.log</c> (docs/hex-link-plan.md, 3.7): links up and
/// down, pairings, refusals and why, every Steer and Edit request. A line is the UTC time, the event, the device and
/// the detail, a tab between each, so it reads in a text editor and splits back without a parser. The file is kept
/// under 2 MB by cutting the oldest half when it passes that, which keeps the latest without a second file.
/// </summary>
internal sealed class HexLinkAudit
{
    /// <summary>How big the log may grow before its older half is cut.</summary>
    public const long MOST_BYTES = 2 * 1024 * 1024;

    private readonly Lock gate = new();
    private readonly long most;

    /// <summary>Where it's written.</summary>
    public string Path { get; }

    /// <param name="most">How big it may grow, <see cref="MOST_BYTES"/> but for tests.</param>
    public HexLinkAudit(string path, long most = MOST_BYTES)
    {
        Path = path;
        this.most = most;
    }

    /// <summary>Adds an event, from any thread. A log that can't be written is said on the console and otherwise let be.</summary>
    public void Write(string what, string device = "", string detail = "")
    {
        string line = $"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}\t{Clean(what)}\t{Clean(device)}\t{Clean(detail)}\n";
        lock (gate)
        {
            try
            {
                if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder)
                    Directory.CreateDirectory(folder);
                File.AppendAllText(Path, line);
                if (new FileInfo(Path).Length > most)
                    Cut();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Hex] Link's audit log couldn't be written, {e.Message}");
            }
        }
    }

    /// <summary>The latest events, oldest first, at most <paramref name="count"/>.</summary>
    public List<HexLinkAuditEntry> Latest(int count)
    {
        var latest = new List<HexLinkAuditEntry>();
        string[] lines;
        lock (gate)
        {
            try
            {
                if (!File.Exists(Path))
                    return latest;
                lines = File.ReadAllLines(Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return latest;
            }
        }

        foreach (string line in lines.Skip(Math.Max(0, lines.Length - count)))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 4 && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime time))
                latest.Add(new HexLinkAuditEntry(time, parts[1], parts[2], parts[3]));
        }

        return latest;
    }

    // Keeps the newer half of the file, from the first whole line after its middle. Under the gate
    private void Cut()
    {
        byte[] all = File.ReadAllBytes(Path);
        int from = all.Length / 2;
        while (from < all.Length && all[from - 1] != (byte)'\n')
            from++;

        string next = Path + ".new";
        File.WriteAllBytes(next, all.AsSpan(from).ToArray());
        File.Move(next, Path, overwrite: true);
    }

    // A tab or a line break inside a field would split it, they're spaces
    private static string Clean(string text)
    {
        if (text.AsSpan().IndexOfAny('\t', '\n', '\r') < 0)
            return text;
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
            clean.Append(c is '\t' or '\n' or '\r' ? ' ' : c);
        return clean.ToString();
    }
}
