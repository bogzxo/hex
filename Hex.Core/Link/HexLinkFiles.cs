using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Win32.SafeHandles;

namespace Hex.Core;

/// <summary>
/// The workspace's files as a link reaches them, and the jail they're kept in. A path on the wire is
/// <c>folder-name/relative/path</c> with forward slashes, the folder one of the workspace's by its name; nothing else
/// names a file. <see cref="Resolve"/> turns one into a full path here or refuses it: rooted paths, drive letters,
/// <c>..</c>, alternate data streams, Windows' device names and short names, anything under <c>.git</c>, and a path
/// whose links (symbolic links, junctions) lead out of the folder. Everything here is pure enough to be tested alone.
/// </summary>
internal static partial class HexLinkFiles
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
    public static string Resolve(IReadOnlyList<HexLinkFolder> folders, string wire, HexExcludes? excludes = null,
        bool respectLinkIgnore = false)
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
            if (HexLinkTree.CacheArtifact(part) || HexLinkHoldStore.StorageArtifact(part))
                throw Refused(wire, "Link's cache and staging files are never shared");
        }

        if (parts.Length > 1)
        {
            string requested = string.Join('/', parts, 1, parts.Length - 1);
            string real = inside.Replace('\\', '/');
            if (!folder.IncludeSystemFiles && (HexLinkSystemFiles.Matches(requested) || HexLinkSystemFiles.Matches(real)))
                throw Refused(wire, "Link excludes operating system metadata in this folder");
            if (respectLinkIgnore)
            {
                HexIgnoreFile? rules = HexLinkIgnore.ForRoot(root).Read();
                if (rules is not null)
                {
                    bool directory = Directory.Exists(at);
                    if (HexLinkIgnore.Matches(rules, requested, directory) || HexLinkIgnore.Matches(rules, real, directory))
                        throw Refused(wire, "the folder's .hex-link-ignore excludes it");
                }
            }
            bool rulesFile = respectLinkIgnore && HexLinkIgnore.IsRulesFile(requested) && HexLinkIgnore.IsRulesFile(real);
            if (excludes is not null && !rulesFile)
            {
                bool directory = Directory.Exists(at);
                if (Excluded(excludes, requested, directory) || Excluded(excludes, real, directory))
                    throw Refused(wire, "the workspace excludes it");
            }
        }

        return at;
    }

    private static bool Excluded(HexExcludes excludes, string relative, bool directory)
    {
        for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
        {
            if (excludes.Hidden(relative[..slash], folder: true))
                return true;
        }
        return excludes.Hidden(relative, directory);
    }

    internal static bool MayReach(IReadOnlyList<HexLinkFolder> folders, string wire, HexExcludes excludes)
    {
        try
        {
            Resolve(folders, wire, excludes);
            return true;
        }
        catch (HexLinkException)
        {
            return false;
        }
    }

    // A name along a wire path, refused when it isn't a plain one
    internal static void Check(string wire, string part)
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
            var file = new FileInfo(path);
            FileAttributes attributes = file.Attributes;
            if (attributes == (FileAttributes)(-1) || (attributes & FileAttributes.ReparsePoint) == 0)
                return path;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : file;
            return info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target
                ? Path.GetFullPath(target.FullName) : path;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
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
    /// path lists the workspace's folders themselves. Entries outside the jail and excluded entries are left out.
    /// </summary>
    public static JsonObject List(IReadOnlyList<HexLinkFolder> folders, string wire, HexExcludes? excludes = null)
    {
        var entries = new JsonArray();
        if (string.IsNullOrEmpty(wire))
        {
            foreach (HexLinkFolder folder in folders)
                entries.Add((JsonNode)new JsonObject { ["name"] = folder.Name, ["kind"] = "folder", ["size"] = 0 });
            return new JsonObject { ["path"] = string.Empty, ["entries"] = entries, ["more"] = false };
        }

        excludes ??= HexExcludes.Usual;
        string full = Resolve(folders, wire, excludes);
        if (!Directory.Exists(full))
            throw HexLinkException.NotFound($"There's no folder {wire}.");

        var found = new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false })
            .Where(info => MayReach(folders, wire.TrimEnd('/') + "/" + info.Name, excludes))
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

    /// <summary>The hash of a shareable file as it is now, null when it is absent, unreadable or too large.</summary>
    public static string? HashOf(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length <= MOST_READ ? Hash(ReadBounded(stream, full)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException || e is HexLinkException { Code: HexLinkErrors.TOO_BIG })
        {
            return null;
        }
    }

    /// <summary>
    /// A workspace file's bytes, read without holding anybody else off it: File.ReadAllBytes shares the file for reading
    /// only, so on Windows the person saving it, or a program appending to it, would fail while a linked device reads.
    /// </summary>
    public static byte[] ReadShared(string full)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadAll(stream);
    }

    /// <summary>Asynchronously reads a workspace file with the same sharing as <see cref="ReadShared"/>.</summary>
    public static async Task<byte[]> ReadSharedAsync(string full, CancellationToken cancel)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: true);
        using var bytes = new MemoryStream(stream.Length is > 0 and < int.MaxValue ? (int)stream.Length : 0);
        await stream.CopyToAsync(bytes, cancel).ConfigureAwait(false);
        return bytes.ToArray();
    }

    // All of a stream, the length it has now a guess only, a file being written may grow or shrink as it's read
    private static byte[] ReadAll(FileStream stream)
    {
        var bytes = new MemoryStream(stream.Length is > 0 and < int.MaxValue ? (int)stream.Length : 0);
        stream.CopyTo(bytes);
        return bytes.ToArray();
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

        byte[] bytes;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = ReadBounded(stream, wire);
        }
        catch (FileNotFoundException)
        {
            throw HexLinkException.NotFound($"There's no file {wire}.");
        }
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

    internal static byte[] ReadBounded(Stream stream, string wire)
    {
        using var bytes = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, MOST_READ) : 0);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (bytes.Length < MOST_READ)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MOST_READ - bytes.Length));
                if (read == 0)
                    return bytes.ToArray();
                bytes.Write(buffer, 0, read);
            }
            if (stream.ReadByte() >= 0)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{wire} exceeds the {MOST_READ / 1024 / 1024} MB read limit.",
                    new JsonObject { ["size"] = MOST_READ + 1 });
            return bytes.ToArray();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
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
    public static Task<string?> CompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable = null) =>
        CompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories: null);

    internal sealed class PackDirectories
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<DirectoryInfo>> created = new(PlatformPaths.Comparer);

        public void Ensure(string directory) =>
            _ = created.GetOrAdd(directory, static path => new Lazy<DirectoryInfo>(() => MakeDirectory(path))).Value;
    }

    private static DirectoryInfo MakeDirectory(string directory)
    {
        using var timing = HexLinkSyncTiming.Measure("cas.create-directory");
        return Directory.CreateDirectory(directory);
    }

    internal static async Task<string?> CompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable, PackDirectories? directories)
    {
        using var duration = HexLinkSyncTiming.Measure("cas.small-total");
        if (bytes is { Length: > MOST_READ })
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A link writes files of up to {MOST_READ / 1024 / 1024} MB.");
        full = Path.GetFullPath(full);
        cancel.ThrowIfCancellationRequested();
        if (Directory.Exists(full))
            throw HexLinkException.Invalid($"{wire} is a folder.");

        string? temp = null;
        try
        {
            if (bytes is not null)
            {
                string folder = Path.GetDirectoryName(full)!;
                if (directories is null)
                    MakeDirectory(folder);
                else
                    directories.Ensure(folder);
                temp = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.hexlink");
                bool asyncIo = directories is null || bytes.Length > 64 * 1024;
                FileStream staged;
                using (HexLinkSyncTiming.Measure("cas.open-stage"))
                    staged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: asyncIo);
                try
                {
                    using var write = HexLinkSyncTiming.Measure("cas.write-stage", bytes.Length);
                    if (asyncIo)
                        await staged.WriteAsync(bytes, cancel).ConfigureAwait(false);
                    else
                        staged.Write(bytes);
                }
                finally
                {
                    using var close = HexLinkSyncTiming.Measure("cas.close-stage");
                    if (asyncIo)
                        await staged.DisposeAsync().ConfigureAwait(false);
                    else
                        staged.Dispose();
                }
            }

            string? next;
            using (HexLinkSyncTiming.Measure("cas.hash-new"))
                next = bytes is null ? null : Hash(bytes);
            IDisposable publication;
            using (HexLinkSyncTiming.Measure("cas.path-lock-wait"))
                publication = await FilePublication.EnterAsync(full, cancel).ConfigureAwait(false);
            using (publication)
            {
                await ReplaceChecked(full, wire, baseHash, temp, executable, cancel).ConfigureAwait(false);
                temp = null;
                return next;
            }
        }
        finally
        {
            if (temp is not null)
            {
                using var cleanup = HexLinkSyncTiming.Measure("cas.clean-stage");
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }


    /// <summary>
    /// Stages a length- and hash-checked stream beside the destination and publishes it through the same
    /// compare-and-replace gate as a small linked write. The caller owns the source stream.
    /// </summary>
    internal static async Task<string?> CompareAndReplace(string full, string wire, string? baseHash, Stream? source,
        long length, string? expectedHash, CancellationToken cancel, bool? executable = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        full = Path.GetFullPath(full);
        cancel.ThrowIfCancellationRequested();
        if (Directory.Exists(full))
            throw HexLinkException.Invalid($"{wire} is a folder.");

        if (source is null)
        {
            if (baseHash is null)
                throw HexLinkException.Invalid("A streamed deletion needs a base hash.");
            await PublishStreamed(full, wire, baseHash, null, executable, cancel).ConfigureAwait(false);
            return null;
        }

        string folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.hexlink");
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            string next;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var staged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
                long remaining = length;
                while (remaining != 0)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancel).ConfigureAwait(false);
                    if (count == 0)
                        throw HexLinkException.Invalid($"The streamed file {wire} ended early.");
                    hash.AppendData(buffer, 0, count);
                    await staged.WriteAsync(buffer.AsMemory(0, count), cancel).ConfigureAwait(false);
                    remaining -= count;
                }
                if (await source.ReadAsync(buffer.AsMemory(0, 1), cancel).ConfigureAwait(false) != 0)
                    throw HexLinkException.Invalid($"The streamed file {wire} exceeded its declared length.");
                next = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(next, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw HexLinkException.Invalid($"The streamed file {wire} did not match its SHA-256.");
            }

            await PublishStreamed(full, wire, baseHash, temp, executable, cancel).ConfigureAwait(false);
            return next;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task PublishStreamed(string full, string wire, string? baseHash, string? temp,
        bool? executable, CancellationToken cancel)
    {
        using IDisposable publication = await FilePublication.EnterAsync(full, cancel).ConfigureAwait(false);
        await ReplaceChecked(full, wire, baseHash, temp, executable, cancel, fullHash: true).ConfigureAwait(false);
    }

    internal static IDisposable LockForLocalWrite(string full) => FilePublication.Enter(full);

    private static async Task ReplaceChecked(string full, string wire, string? baseHash, string? temp,
        bool? executable, CancellationToken cancel, bool fullHash = false)
    {
        cancel.ThrowIfCancellationRequested();
        string? now = null;
        try
        {
            if (OperatingSystem.IsWindows() && baseHash is null && temp is not null)
            {
                // A same-directory move with overwrite disabled atomically checks absence and publishes.
                using var publication = HexLinkSyncTiming.Measure("cas.rename-or-delete");
                File.Move(temp, full, overwrite: false);
                return;
            }

            DateTime written;
            using (HexLinkSyncTiming.Measure("cas.destination-stat"))
                written = File.GetLastWriteTimeUtc(full);
            FileStream? opened;
            using (HexLinkSyncTiming.Measure("cas.destination-open"))
                opened = OpenForComparison(full);
            using FileStream? current = opened;
            long length = current?.Length ?? 0;
            if (current is not null && (fullHash || length <= MOST_READ))
            {
                using var rehash = HexLinkSyncTiming.Measure("cas.destination-rehash");
                now = fullHash
                    ? Convert.ToHexStringLower(await SHA256.HashDataAsync(current, cancel).ConfigureAwait(false))
                    : await HashForComparison(current, cancel).ConfigureAwait(false);
            }

            if (baseHash is null ? current is not null : !string.Equals(baseHash, now, StringComparison.OrdinalIgnoreCase))
                throw Conflict(wire, now);

            if (temp is not null && !OperatingSystem.IsWindows() && (current is not null || executable.HasValue))
            {
                const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                UnixFileMode mode = File.GetUnixFileMode(current is null ? temp : full);
                mode = executable switch
                {
                    true => mode | UnixFileMode.UserExecute,
                    false => mode & ~execute,
                    _ => mode
                };
                File.SetUnixFileMode(temp, mode);
            }

            cancel.ThrowIfCancellationRequested();
            if (current is not null && (current.Length != length || File.GetLastWriteTimeUtc(full) != written))
                throw Conflict(wire, null);

            // The open handle denies writers on Windows, while sharing delete for the rename itself. Unix sharing
            // is advisory. The metadata recheck also catches ordinary saves there, up to the final rename window.
            using var publish = HexLinkSyncTiming.Measure("cas.rename-or-delete");
            if (temp is not null)
            {
                if (OperatingSystem.IsWindows() && current is not null)
                    ReplaceWhileOpenOnWindows(temp, full);
                else
                    File.Move(temp, full, overwrite: current is not null);
            }
            else if (current is not null)
                File.Delete(full);
        }
        catch (IOException failure) when ((failure.HResult & 0xffff) is 17 or 32 or 33 or 80 or 183)
        {
            throw Conflict(wire, now ?? HashOf(full));
        }
    }

    private static FileStream? OpenForComparison(string full)
    {
        try
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<string?> HashForComparison(FileStream current, CancellationToken cancel)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int total = 0;
            while (total <= MOST_READ)
            {
                int read = await current.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MOST_READ + 1 - total)), cancel).ConfigureAwait(false);
                if (read == 0)
                    return Convert.ToHexStringLower(hash.GetHashAndReset());
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            return null;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // MoveFileEx refuses to replace a file with any open handles. FileRenameInfoEx can replace the checked file
    // while we hold its write-denying handle.
    internal static void ReplaceWhileOpenOnWindows(string temp, string full)
    {
        const uint delete = 0x00010000, openExisting = 3;
        const int fileRenameInfoEx = 22, replaceWithOpenHandles = 0x01 | 0x02;
        using SafeFileHandle staged = CreateFile(LongWindowsPath(temp), delete, (uint)(FileShare.ReadWrite | FileShare.Delete),
            0, openExisting, 0, 0);
        if (staged.IsInvalid)
            throw RenameFailure(Marshal.GetLastPInvokeError());

        int lengthOffset = IntPtr.Size * 2;
        int nameOffset = lengthOffset + sizeof(int);
        string path = LongWindowsPath(full);
        int nameBytes = Encoding.Unicode.GetByteCount(path);
        byte[] info = new byte[nameOffset + nameBytes + sizeof(char)];
        BinaryPrimitives.WriteInt32LittleEndian(info, replaceWithOpenHandles);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(lengthOffset), nameBytes);
        Encoding.Unicode.GetBytes(path, info.AsSpan(nameOffset));
        if (SetFileInformationByHandle(staged, fileRenameInfoEx, info, (uint)info.Length) == 0)
            throw RenameFailure(Marshal.GetLastPInvokeError());
    }

    private static string LongWindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static IOException RenameFailure(int error) =>
        new(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string file, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] information, uint size);

    private static HexLinkException Conflict(string wire, string? hash) =>
        new(HexLinkErrors.CONFLICT, $"{wire} changed since it was read.", new JsonObject { ["hash"] = hash });

    /// <summary>
    /// A file written whole or not at all: into a temp file beside it, then moved over it, so a reader or a crash
    /// halfway sees the old file or the new one, never half of one.
    /// </summary>
    public static void WriteAtomic(string full, byte[] bytes) => WriteAtomic(full, (ReadOnlySpan<byte>)bytes);

    public static void WriteAtomic(string full, ReadOnlySpan<byte> bytes)
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
    private readonly Func<string, string, (bool Held, string? Hash)>? projection;
    private readonly Timer timer;
    private int looking;

    private sealed class Watched(string full)
    {
        public string Full { get; } = full;
        public long Length { get; set; } = -1;
        public long Written { get; set; }
        public string? Hash { get; set; }
        public bool WasHeld { get; set; }
    }

    public HexLinkWatch(Action<string, string?> changed, TimeSpan? every = null,
        Func<string, string, (bool Held, string? Hash)>? projection = null)
    {
        this.changed = changed;
        this.projection = projection;
        TimeSpan period = every ?? TimeSpan.FromSeconds(1);
        timer = new Timer(_ => Look(), null, period, period);
    }

    /// <summary>A file watched from now, its hash as it is, false past the most.</summary>
    public bool Add(string wire, string full, out string? hash)
    {
        var file = new Watched(full);
        var published = projection?.Invoke(wire, file.Full) ?? default;
        file.WasHeld = published.Held;
        if (!published.Held)
            Stamp(file);
        hash = file.Hash = published.Held ? published.Hash : HexLinkFiles.HashOf(full);
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
                    var published = projection?.Invoke(wire, file.Full) ?? default;
                    bool wasHeld = file.WasHeld;
                    file.WasHeld = published.Held;
                    if (!published.Held && !Stamp(file) && !wasHeld)
                        continue;
                    string? hash = published.Held ? published.Hash : HexLinkFiles.HashOf(file.Full);
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
