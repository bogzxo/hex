using System.Text;

namespace Hex.Core;

/// <summary>Preserves a text document's Unicode encoding and replaces its bytes only after a complete write.</summary>
internal static class DocumentFile
{
    public static string WithLineEndings(string text, string? original, bool crlf, bool savedCrLf)
    {
        string preferred = crlf ? "\r\n" : "\n";
        // Uniform files and an explicit line-ending conversion need no diff or per-line metadata.
        if (crlf != savedCrLf || original is null || !original.Contains("\r\n", StringComparison.Ordinal) ||
            !original.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'))
            return crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

        string[] before = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] after = text.Split('\n');
        var endings = new List<string>();
        for (int i = 0; i < original.Length; i++)
            if (original[i] == '\n') endings.Add(i > 0 && original[i - 1] == '\r' ? "\r\n" : "\n");
        int[] source = new int[after.Length];
        Array.Fill(source, -1);
        int oldAt = 0, newAt = 0;
        foreach (var hunk in Git.GitLineDiff.Diff(before, after))
        {
            while (newAt < hunk.NewStart) source[newAt++] = oldAt++;
            for (int i = 0; i < Math.Min(hunk.OldCount, hunk.NewCount); i++) source[hunk.NewStart + i] = hunk.OldStart + i;
            oldAt = hunk.OldStart + hunk.OldCount;
            newAt = hunk.NewStart + hunk.NewCount;
        }
        while (newAt < after.Length) source[newAt++] = oldAt++;
        var result = new StringBuilder(text.Length + endings.Count);
        for (int i = 0; i < after.Length; i++)
        {
            result.Append(after[i]);
            if (i < after.Length - 1)
                result.Append(source[i] >= 0 && source[i] < endings.Count ? endings[source[i]] : preferred);
        }
        return result.ToString();
    }

    public static Encoding EncodingOf(string path)
    {
        if (!File.Exists(path)) return new UTF8Encoding(false, true);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> start = stackalloc byte[4];
        int count = file.ReadAtLeast(start, 4, throwOnEndOfStream: false);
        if (count >= 4 && start.SequenceEqual(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, true, true);
        if (count >= 4 && start.SequenceEqual(new byte[] { 0xFF, 0xFE, 0, 0 })) return new UTF32Encoding(false, true, true);
        if (count >= 2 && start[0] == 0xFF && start[1] == 0xFE) return new UnicodeEncoding(false, true, true);
        if (count >= 2 && start[0] == 0xFE && start[1] == 0xFF) return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(count >= 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF, true);
    }

    public static void Write(string path, string text, Encoding encoding)
    {
        // Follow a symbolic link as an ordinary text save does; do not replace the link itself.
        path = Path.GetFullPath(path);
        if (File.Exists(path) && new FileInfo(path).ResolveLinkTarget(true) is { } linked) path = linked.FullName;
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".hex-save-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(file, encoding, leaveOpen: true)) writer.Write(text);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
