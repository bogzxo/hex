using System.Text;
using System.Text.RegularExpressions;

using Horizon.Core.Platform;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>What Replace in Files did to a file, how many matches it swapped, and why it couldn't when it couldn't (null when it could).</summary>
internal readonly record struct HexReplaced(string Path, int Count, string? Error = null);

// Replace in Files. The matches a search listed swapped for what's in the replace box, a regex's groups put in ($1,
// ${name}) the way the find bar's replace does it. Only the matches still listed and still there are swapped, one that
// was dismissed or typed away since stays as it is. A file open in the editor is changed through its document, every
// other one is written on the disk in the encoding it was in, its BOM and its line endings kept
internal static partial class HexSearch
{
    /// <summary>
    /// The matches listed, file by file, swapped for something. A file the editor has open is handed back changed
    /// through <paramref name="rewriteOpen"/> (one step to undo, nothing saved), every other one is written.
    /// </summary>
    /// <param name="openText">The text of a file the editor has open by its full path, null for one that isn't open.</param>
    /// <returns>How many were swapped in each file.</returns>
    public static List<HexReplaced> ReplaceIn(IEnumerable<HexResult> results, string query, string with, UIFindOptions options,
        Func<string, string?>? openText = null, Action<string, string>? rewriteOpen = null)
    {
        var done = new List<HexReplaced>();
        Regex? pattern = null;
        if (query.Length == 0 || (options.Regex && (pattern = Pattern(query, options, out _)) is null))
            return done;

        foreach (var file in results.GroupBy(result => Path.GetFullPath(result.Path), PlatformPaths.Comparer))
        {
            var wanted = file.Select(result => (result.Line, result.Column)).ToHashSet();
            try
            {
                if (openText?.Invoke(file.Key) is { } text)
                {
                    string swapped = ReplaceInText(text, wanted, query, with, options, pattern, out int count);
                    if (count > 0)
                        rewriteOpen?.Invoke(file.Key, swapped);
                    done.Add(new HexReplaced(file.Key, count));
                }
                else
                {
                    done.Add(new HexReplaced(file.Key, ReplaceInFile(file.Key, wanted, query, with, options, pattern)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                done.Add(new HexReplaced(file.Key, 0, e.Message));
            }
        }

        return done;
    }

    /// <summary>
    /// A file's matches at some places (line and column from 0) swapped and the file written, as it was but for them.
    /// </summary>
    /// <returns>How many were swapped, 0 and the file left alone when none of them are there any more.</returns>
    public static int ReplaceInFile(string path, IReadOnlySet<(int Line, int Column)> wanted, string query, string with, UIFindOptions options, Regex? pattern)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var (encoding, bom) = EncodingOf(bytes);
        string text = encoding.GetString(bytes, bom, bytes.Length - bom);

        string swapped = ReplaceInText(text, wanted, query, with, options, pattern, out int count);
        if (count == 0)
            return 0;

        File.WriteAllBytes(path, [.. bytes.AsSpan(0, bom), .. encoding.GetBytes(swapped)]);
        return count;
    }

    /// <summary>
    /// A text's matches at some places swapped, every line ending left the way it was (\n, \r\n or a mix of them).
    /// </summary>
    public static string ReplaceInText(string text, IReadOnlySet<(int Line, int Column)> wanted, string query, string with, UIFindOptions options, Regex? pattern, out int count)
    {
        count = 0;
        var lines = new HashSet<int>(wanted.Select(place => place.Line));
        var built = new StringBuilder(text.Length);
        var matches = new List<(int Column, int Length)>();

        int line = 0, start = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;

            // A \r before the \n is the line's ending, not what's on it, the way the search read it
            int content = newline >= 0 && end > start && text[end - 1] == '\r' ? end - 1 : end;

            if (lines.Contains(line))
            {
                string said = text[start..content];
                AllMatches(said, query, options, pattern, matches);
                int from = 0;
                foreach (var (column, length) in matches)
                {
                    if (!wanted.Contains((line, column)))
                        continue;

                    built.Append(said, from, column - from).Append(Replacement(said, column, length, with, pattern));
                    from = column + length;
                    count++;
                }
                built.Append(said, from, said.Length - from);
            }
            else
            {
                built.Append(text, start, content - start);
            }

            if (newline < 0)
                break;

            built.Append(text, content, newline + 1 - content);
            start = newline + 1;
            line++;
        }

        return built.ToString();
    }

    /// <summary>What a match is swapped for, a regex's groups put in ($1, ${name}), the replacement as it is otherwise.</summary>
    public static string Replacement(string line, int column, int length, string with, Regex? pattern)
    {
        if (pattern is null)
            return with;

        try
        {
            Match match = pattern.Match(line, column, length);
            return match.Success && match.Index == column ? match.Result(with) : with;
        }
        catch (RegexMatchTimeoutException)
        {
            return with;
        }
    }

    // The encoding a file is in by its BOM, and how many bytes the BOM is. Without one it's UTF-8 when it reads as that,
    // Latin-1 otherwise, which gives every byte back as it was
    private static (Encoding Encoding, int Bom) EncodingOf(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return (new UTF8Encoding(false), 3);
        if (bytes is [0xFF, 0xFE, 0x00, 0x00, ..])
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        if (bytes is [0xFF, 0xFE, ..])
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        if (bytes is [0xFE, 0xFF, ..])
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, 0);
        }
    }
}
