using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The workspace's files as a link reaches them, and the jail they're kept in. A path on the wire is
/// <c>folder-name/relative/path</c> with forward slashes, the folder one of the workspace's by its name; nothing else
/// names a file. <see cref="Resolve"/> turns one into a full path here or refuses it: rooted paths, drive letters,
/// <c>..</c>, alternate data streams, Windows' device names and short names, anything under <c>.git</c>, and a path
/// whose links (symbolic links, junctions) lead out of the folder. Everything here is pure enough to be tested alone.
/// </summary>
internal static class HexLinkFiles
{
    /// <summary>The biggest file read or written over a link, in bytes.</summary>
    public const int MOST_READ = 8 * 1024 * 1024;

    /// <summary>The most entries a folder's listing gives, the rest left off (and said so).</summary>
    public const int MOST_LISTED = 5000;

    /// <summary>The longest wire path taken.</summary>
    public const int MOST_PATH = 2048;

    // Windows' names for devices, a file of which name opens the device whatever its folder or extension
    private static readonly HashSet<string> DEVICES = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>
    /// A wire path made a full path inside one of the workspace's folders, or refused (not allowed) with why. The full
    /// path is the real one, every link along it followed, which is what's then read or written.
    /// </summary>
    public static string Resolve(IReadOnlyList<HexLinkFolder> folders, string wire)
    {
        if (string.IsNullOrEmpty(wire) || wire.Length > MOST_PATH)
            throw Refused(wire, "it's empty or too long");
        if (wire.Contains('\\'))
            throw Refused(wire, "a link's paths use forward slashes");
        if (wire.Contains(':'))
            throw Refused(wire, "a colon is a drive or a stream");
        if (wire[0] == '/')
            throw Refused(wire, "it's rooted, a path starts with the name of a workspace folder");
        foreach (char c in wire)
        {
            if (char.IsControl(c))
                throw Refused(wire, "it has a control character in it");
        }

        string[] parts = wire.TrimEnd('/').Split('/');
        HexLinkFolder folder = folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, parts[0]))
            ?? throw Refused(wire, $"the workspace has no folder called {parts[0]}");

        for (int i = 1; i < parts.Length; i++)
            Check(wire, parts[i]);

        string root = Real(Path.GetFullPath(folder.Path));
        string at = root;
        for (int i = 1; i < parts.Length; i++)
        {
            at = Path.Combine(at, parts[i]);
            // Followed as it goes, a link at any step (a folder that's a junction to C:\) leaving the folder refused there
            at = Real(at);
            if (!PlatformPaths.IsUnder(at, root))
                throw Refused(wire, "it leads out of its folder");
        }

        // Somewhere a link led to inside the folder, still not .git
        string inside = Path.GetRelativePath(root, at);
        foreach (string part in inside.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw Refused(wire, "git's own folder is never shared");
        }

        return at;
    }

    // A name along a wire path, refused when it isn't a plain one
    private static void Check(string wire, string part)
    {
        if (part.Length == 0 || part == "." || part == "..")
            throw Refused(wire, "it has an empty, . or .. part");
        if (part.TrimEnd('.', ' ').Equals(".git", StringComparison.OrdinalIgnoreCase))
            throw Refused(wire, "git's own folder is never shared");
        // Windows takes "name." and "name " as "name", which would get round every check of a name
        if (part[^1] is '.' or ' ')
            throw Refused(wire, "a name ends in a dot or a space");
        if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw Refused(wire, "a name has a character Windows won't take");
        string stem = part.Split('.')[0].TrimEnd(' ');
        if (DEVICES.Contains(stem))
            throw Refused(wire, $"{stem} is a device on Windows");
        // An 8.3 short name (GIT~1 is .git) reaches a file by a name nobody checked
        int tilde = part.IndexOf('~');
        if (OperatingSystem.IsWindows() && tilde >= 0 && tilde + 1 < part.Length && char.IsAsciiDigit(part[tilde + 1]))
            throw Refused(wire, "a short name like GIT~1 isn't taken");
    }

    // Where a path really is, its last part followed when it's a link (to the end of a chain), as it is otherwise
    private static string Real(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
                return Path.GetFullPath(target.FullName);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A link that can't be followed (a loop, a target gone) is no way in
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "A link along that path can't be followed.", new JsonObject { ["needs"] = "a path inside the workspace" });
        }
    }

    private static HexLinkException Refused(string wire, string why) =>
        new(HexLinkErrors.NOT_ALLOWED, $"{(wire.Length > 200 ? wire[..200] + "..." : wire)} isn't a path a link may reach, {why}.",
            new JsonObject { ["needs"] = "a path inside the workspace" });

    /// <summary>
    /// What's in a folder of the workspace, its entries {name, kind (file or folder), size}, folders first. The empty
    /// path lists the workspace's folders themselves. <c>.git</c> is left out.
    /// </summary>
    public static JsonObject List(IReadOnlyList<HexLinkFolder> folders, string wire)
    {
        var entries = new JsonArray();
        if (string.IsNullOrEmpty(wire))
        {
            foreach (HexLinkFolder folder in folders)
                entries.Add((JsonNode)new JsonObject { ["name"] = folder.Name, ["kind"] = "folder", ["size"] = 0 });
            return new JsonObject { ["path"] = string.Empty, ["entries"] = entries, ["more"] = false };
        }

        string full = Resolve(folders, wire);
        if (!Directory.Exists(full))
            throw HexLinkException.NotFound($"There's no folder {wire}.");

        var found = new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false })
            .Where(info => !info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            .Take(MOST_LISTED + 1)
            .ToList();
        bool more = found.Count > MOST_LISTED;
        foreach (FileSystemInfo info in found.Take(MOST_LISTED).OrderBy(info => info is FileInfo).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add((JsonNode)new JsonObject
            {
                ["name"] = info.Name,
                ["kind"] = info is DirectoryInfo ? "folder" : "file",
                ["size"] = info is FileInfo file ? file.Length : 0
            });
        }

        return new JsonObject { ["path"] = wire, ["entries"] = entries, ["more"] = more };
    }

    /// <summary>The hash a file's bytes are known by, lower-case hex SHA-256.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The hash of a file as it is now, null when there's none.</summary>
    public static string? HashOf(string full)
    {
        try
        {
            return File.Exists(full) ? Hash(File.ReadAllBytes(full)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A file read for sending: its text with the encoding it was in and its line ends when it's text (UTF-8, or UTF-8
    /// or UTF-16 with a byte order mark), its bytes as base64 otherwise, the hash of its bytes, its size and when it was
    /// last changed (Unix milliseconds). Too big past <see cref="MOST_READ"/>.
    /// </summary>
    public static JsonObject Read(string full, string wire)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
            throw HexLinkException.NotFound($"There's no file {wire}.");
        if (info.Length > MOST_READ)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{wire} is {info.Length / 1024 / 1024} MB, a link reads files of up to {MOST_READ / 1024 / 1024} MB.", new JsonObject { ["size"] = info.Length });

        byte[] bytes = File.ReadAllBytes(full);
        var read = new JsonObject
        {
            ["path"] = wire,
            ["hash"] = Hash(bytes),
            ["size"] = bytes.Length,
            ["modified"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
        };

        if (Decode(bytes) is { } text)
        {
            read["text"] = text.Text;
            read["encoding"] = text.Encoding;
            read["eol"] = Eol(text.Text);
        }
        else
        {
            read["base64"] = Convert.ToBase64String(bytes);
            read["encoding"] = "binary";
            read["eol"] = string.Empty;
        }

        return read;
    }

    /// <summary>
    /// Bytes as text when they are, and the encoding they're in: utf-8-bom, utf-16le and utf-16be (with their marks),
    /// or plain utf-8. Null for anything else, or text with a NUL in it, which is a binary file that happens to decode.
    /// </summary>
    public static (string Text, string Encoding)? Decode(byte[] bytes)
    {
        (Encoding encoding, int skip, string name) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false, true), 3, "utf-8-bom"),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false, true), 2, "utf-16le"),
            [0xFE, 0xFF, ..] => ((Encoding)new UnicodeEncoding(true, false, true), 2, "utf-16be"),
            _ => (new UTF8Encoding(false, true), 0, "utf-8")
        };

        try
        {
            string text = encoding.GetString(bytes, skip, bytes.Length - skip);
            return text.Contains('\0') ? null : (text, name);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Text made bytes again in an encoding <see cref="Decode"/> named, its mark in front, null for a name it doesn't know.</summary>
    public static byte[]? Encode(string text, string encoding) => encoding switch
    {
        "utf-8" => Encoding.UTF8.GetBytes(text),
        "utf-8-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        "utf-16le" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        "utf-16be" => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => null
    };

    /// <summary>The line ends text has, crlf, lf or cr by the first one, empty for one line.</summary>
    public static string Eol(string text)
    {
        int at = text.IndexOfAny(['\r', '\n']);
        if (at < 0)
            return string.Empty;
        return text[at] == '\n' ? "lf" : at + 1 < text.Length && text[at + 1] == '\n' ? "crlf" : "cr";
    }

    /// <summary>
    /// A file written if it's still what the writer read, by its hash (<paramref name="baseHash"/>): conflict, with the
    /// hash it has now, when it isn't. A null base is a new file, which conflicts with one that's there. The bytes are
    /// written as they came, so whatever encoding and mark they were in stays. Hands back the new hash.
    /// </summary>
    public static async Task<string> Write(string full, string wire, string? baseHash, byte[] bytes, Func<string, byte[], Task> write)
    {
        if (bytes.Length > MOST_READ)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A link writes files of up to {MOST_READ / 1024 / 1024} MB.");
        if (Directory.Exists(full))
            throw HexLinkException.Invalid($"{wire} is a folder.");

        string? now = HashOf(full);
        bool exists = File.Exists(full);
        if (baseHash is null ? exists : !string.Equals(now, baseHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new HexLinkException(HexLinkErrors.CONFLICT,
                exists ? $"{wire} changed since it was read." : baseHash is null ? $"{wire} is already there." : $"{wire} isn't there any more.",
                new JsonObject { ["hash"] = now });
        }

        if (Path.GetDirectoryName(full) is { } folder)
            Directory.CreateDirectory(folder);
        await write(full, bytes);
        return Hash(bytes);
    }

    /// <summary>
    /// A file written whole or not at all: into a temp file beside it, then moved over it, so a reader or a crash
    /// halfway sees the old file or the new one, never half of one.
    /// </summary>
    public static void WriteAtomic(string full, byte[] bytes)
    {
        string temp = Path.Combine(Path.GetDirectoryName(full) ?? ".", $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.hexlink");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { }
        }
    }
}

/// <summary>
/// The files a link's other end has open, looked at every second (their length and when they were last written, the
/// hash only when one of those moved), each that changed told by its wire path and new hash (null when it went).
/// Polling a handful of files is cheaper and steadier than a FileSystemWatcher per file across three platforms.
/// </summary>
internal sealed class HexLinkWatch : IDisposable
{
    /// <summary>The most files one link may watch.</summary>
    public const int MOST = 64;

    private readonly Dictionary<string, Watched> watched = new(StringComparer.Ordinal);
    private readonly Action<string, string?> changed;
    private readonly Timer timer;
    private int looking;

    private sealed class Watched(string full)
    {
        public string Full { get; } = full;
        public long Length { get; set; } = -1;
        public long Written { get; set; }
        public string? Hash { get; set; }
    }

    public HexLinkWatch(Action<string, string?> changed, TimeSpan? every = null)
    {
        this.changed = changed;
        TimeSpan period = every ?? TimeSpan.FromSeconds(1);
        timer = new Timer(_ => Look(), null, period, period);
    }

    /// <summary>A file watched from now, its hash as it is, false past the most.</summary>
    public bool Add(string wire, string full, out string? hash)
    {
        var file = new Watched(full);
        Stamp(file);
        hash = file.Hash = HexLinkFiles.HashOf(full);
        lock (watched)
        {
            if (!watched.ContainsKey(wire) && watched.Count >= MOST)
                return false;
            watched[wire] = file;
            return true;
        }
    }

    public void Remove(string wire)
    {
        lock (watched)
            watched.Remove(wire);
    }

    public int Count
    {
        get
        {
            lock (watched)
                return watched.Count;
        }
    }

    // Whether a file's length or time moved since it was last looked at, and they're kept for next time
    private static bool Stamp(Watched file)
    {
        var info = new FileInfo(file.Full);
        long length = info.Exists ? info.Length : -1, written = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        bool moved = length != file.Length || written != file.Written;
        file.Length = length;
        file.Written = written;
        return moved;
    }

    // Every period, off any thread, one look at a time
    private void Look()
    {
        if (Interlocked.Exchange(ref looking, 1) != 0)
            return;
        try
        {
            KeyValuePair<string, Watched>[] all;
            lock (watched)
                all = [.. watched];
            foreach (var (wire, file) in all)
            {
                try
                {
                    if (!Stamp(file))
                        continue;
                    string? hash = HexLinkFiles.HashOf(file.Full);
                    if (hash == file.Hash)
                        continue;
                    file.Hash = hash;
                    changed(wire, hash);
                }
                catch (Exception)
                {
                    // A file that can't be looked at now is looked at again next time
                }
            }
        }
        finally
        {
            Volatile.Write(ref looking, 0);
        }
    }

    public void Dispose() => timer.Dispose();
}
