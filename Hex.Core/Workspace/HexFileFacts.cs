using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Hex.Core;

/// <summary>What the start of a text file says about how it's written, its encoding, its line endings and its indentation.</summary>
internal readonly record struct HexTextFacts(string Encoding, string LineEndings, string Indentation, int Lines);

/// <summary>A picture's size in pixels and the format it's in, read from the first bytes of its file.</summary>
internal readonly record struct HexImageFacts(int Width, int Height, string Format);

/// <summary>
/// The facts of a file the properties panel shows, read off the disk without opening it in an editor. Only the start of
/// a file is read, a few hundred kilobytes say all of this for any file somebody would look at the properties of.
/// </summary>
internal static class HexFileFacts
{
    private const int READ = 256 * 1024;

    /// <summary>A size in bytes as people say it, 812 bytes, 14.2 KB, 3.1 MB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes == 1 ? "1 byte" : $"{bytes} bytes",
        < 1024 * 1024 => Invariant($"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => Invariant($"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => Invariant($"{bytes / (1024.0 * 1024 * 1024):0.##} GB")
    };

    /// <summary>The same with the exact count after it, the way Windows' own properties have it.</summary>
    public static string SizeExactly(long bytes) => bytes < 1024 ? Size(bytes) : $"{Size(bytes)} ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";

    /// <summary>The encoding, line endings and indentation of the text at the start of a file.</summary>
    public static HexTextFacts Text(string path)
    {
        byte[] bytes;
        using (FileStream file = File.OpenRead(path))
        {
            bytes = new byte[(int)Math.Min(file.Length, READ)];
            file.ReadExactly(bytes);
        }

        return Text(bytes);
    }

    /// <summary>The same of some bytes, for a test.</summary>
    public static HexTextFacts Text(byte[] bytes)
    {
        string encoding;
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            (encoding, text) = ("UTF-8 with BOM", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            (encoding, text) = ("UTF-16 LE", Encoding.Unicode.GetString(bytes, 2, (bytes.Length - 2) & ~1));
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            (encoding, text) = ("UTF-16 BE", Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1));
        else if (IsUtf8(bytes))
            (encoding, text) = (bytes.Any(b => b >= 0x80) ? "UTF-8" : "UTF-8 (ASCII)", Encoding.UTF8.GetString(bytes));
        else
            (encoding, text) = ("Windows-1252", Encoding.Latin1.GetString(bytes));

        int crlf = 0, lf = 0, cr = 0, lines = text.Length > 0 ? 1 : 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }

                lines++;
            }
            else if (text[i] == '\n')
            {
                lf++;
                lines++;
            }
        }

        string endings = (crlf, lf, cr) switch
        {
            (0, 0, 0) => "None",
            (> 0, 0, 0) => "CRLF",
            (0, > 0, 0) => "LF",
            (0, 0, > 0) => "CR",
            _ => crlf >= lf ? "Mixed, mostly CRLF" : "Mixed, mostly LF"
        };

        return new HexTextFacts(encoding, endings, Indentation(text), lines);
    }

    // Tabs, or spaces by the step the lines go in by most often, None for a file with no indented lines
    private static string Indentation(string text)
    {
        int tabs = 0, spaced = 0;
        var steps = new Dictionary<int, int>();
        int previous = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
                continue;

            if (line[0] == '\t')
            {
                tabs++;
                continue;
            }

            int spaces = 0;
            while (spaces < line.Length && line[spaces] == ' ')
                spaces++;
            if (spaces > 0)
                spaced++;

            int step = Math.Abs(spaces - previous);
            if (step is > 0 and <= 8)
                steps[step] = steps.GetValueOrDefault(step) + 1;
            previous = spaces;
        }

        if (tabs == 0 && spaced == 0)
            return "None";
        if (tabs > spaced)
            return "Tabs";

        int usual = steps.Count == 0 ? 4 : steps.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        return usual == 1 ? "1 space" : $"{usual} spaces";
    }

    // Whether bytes read as UTF-8 without a broken sequence (one cut off at the very end of what was read is fine)
    private static bool IsUtf8(byte[] bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            int more = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
            if (more < 0)
                return false;
            for (int j = 1; j <= more; j++)
            {
                if (i + j >= bytes.Length)
                    return true;
                if ((bytes[i + j] & 0xC0) != 0x80)
                    return false;
            }

            i += more + 1;
        }

        return true;
    }

    /// <summary>Whether a file reads as text, no NUL in its first few kilobytes the way git tells, UTF-16 with its BOM counted as text.</summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            byte[] head = new byte[(int)Math.Min(file.Length, 8000)];
            file.ReadExactly(head);
            bool utf16 = head.Length >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF);
            return utf16 || Array.IndexOf(head, (byte)0) < 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A picture's size and format by its header, PNG, JPEG, GIF, BMP, WebP and ICO, null for anything else.</summary>
    public static HexImageFacts? Image(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            byte[] head = new byte[(int)Math.Min(file.Length, 64 * 1024)];
            file.ReadExactly(head);
            return Image(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The same of the first bytes of a file, for a test.</summary>
    public static HexImageFacts? Image(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 24 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return new HexImageFacts(BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]), "PNG");
        if (head.Length >= 10 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
            return new HexImageFacts(BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..]), "GIF");
        if (head.Length >= 26 && head[0] == 'B' && head[1] == 'M')
            return new HexImageFacts(BinaryPrimitives.ReadInt32LittleEndian(head[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])), "BMP");
        if (head.Length >= 6 && head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0)
            return new HexImageFacts(head[6] == 0 ? 256 : head[6], head[7] == 0 ? 256 : head[7], "ICO");
        if (head.Length >= 30 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return WebP(head);
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8)
            return Jpeg(head);
        return null;
    }

    private static HexImageFacts? WebP(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> chunk = head[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
            return new HexImageFacts(1 + (head[24] | head[25] << 8 | head[26] << 16), 1 + (head[27] | head[28] << 8 | head[29] << 16), "WebP");
        if (chunk.SequenceEqual("VP8L"u8) && head.Length >= 25)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return new HexImageFacts((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, "WebP");
        }

        if (chunk.SequenceEqual("VP8 "u8) && head.Length >= 30)
            return new HexImageFacts(BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF, "WebP");
        return null;
    }

    // A JPEG's size is in its first start of frame, after however many other markers
    private static HexImageFacts? Jpeg(ReadOnlySpan<byte> head)
    {
        int at = 2;
        while (at + 9 < head.Length)
        {
            if (head[at] != 0xFF)
                return null;
            byte marker = head[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(head[(at + 2)..]);
            bool frame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (frame)
                return new HexImageFacts(BinaryPrimitives.ReadUInt16BigEndian(head[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(head[(at + 5)..]), "JPEG");
            at += 2 + length;
        }

        return null;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
