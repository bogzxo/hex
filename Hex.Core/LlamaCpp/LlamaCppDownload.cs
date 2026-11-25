using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;

namespace Hex.Core;

/// <summary>
/// Getting llama.cpp and its models onto the disk: a file downloaded beside where it goes (name.part) and carried on
/// from where it stopped the next time, its size and SHA-256 checked before it's given its name, and an archive unpacked
/// with nothing of it let out of the folder it goes in.
/// </summary>
internal static class LlamaCppDownload
{
    private static readonly HttpClient http = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hex", version));
        return client;
    }

    /// <summary>
    /// A file downloaded to a path, its progress said as bytes done of all of them. A .part beside it from before is
    /// carried on from where it stopped. With a size or a SHA-256 the file has to have them, or it's thrown away and an
    /// <see cref="InvalidDataException"/> says so. A file already at the path with both is left as it is.
    /// </summary>
    public static async Task File(string url, string path, long? size, string? sha256, Action<long, long>? progress, CancellationToken cancel,
        HttpClient? client = null)
    {
        client ??= http;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (System.IO.File.Exists(path) && size is { } want && new FileInfo(path).Length == want)
        {
            if (sha256 is null || await Sha256Of(path, progress, cancel).ConfigureAwait(false) == sha256)
                return;
        }

        string part = path + ".part";
        long have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0;
        if (size is { } whole && have > whole)
        {
            System.IO.File.Delete(part);
            have = 0;
        }

        if (size is not { } known || have < known)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0)
                request.Headers.Range = new RangeHeaderValue(have, null);
            using HttpResponseMessage answered = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (answered.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && size is null)
                have = new FileInfo(part).Length;
            else
            {
                if (!answered.IsSuccessStatusCode)
                    throw new HttpRequestException($"the download said {(int)answered.StatusCode} {answered.ReasonPhrase}");

                // A server that sends the whole file again, not the rest, is written from the start
                bool resumed = have > 0 && answered.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed)
                    have = 0;
                long total = size ?? (answered.Content.Headers.ContentLength is { } length ? length + have : -1);

                await using Stream from = await answered.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using (var into = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    byte[] buffer = new byte[1 << 20];
                    int read;
                    long said = 0;
                    while ((read = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                    {
                        await into.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                        have += read;
                        if (have - said >= 4 << 20)
                        {
                            said = have;
                            progress?.Invoke(have, total);
                        }
                    }
                }
                progress?.Invoke(have, total);
            }
        }

        if (size is { } exact && new FileInfo(part).Length != exact)
            throw new InvalidDataException($"the download is {new FileInfo(part).Length:N0} bytes, not the {exact:N0} it should be; start it again to carry on");
        if (sha256 is not null && await Sha256Of(part, progress, cancel).ConfigureAwait(false) is var got && got != sha256)
        {
            System.IO.File.Delete(part);
            throw new InvalidDataException($"the download isn't the file it should be (its SHA-256 is {got}), it was thrown away");
        }
        System.IO.File.Move(part, path, overwrite: true);
    }

    /// <summary>A file's SHA-256 in lower case hex, its progress said as it's read.</summary>
    public static async Task<string> Sha256Of(string path, Action<long, long>? progress, CancellationToken cancel)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] buffer = new byte[1 << 20];
        long done = 0, said = 0, total = file.Length;
        int read;
        while ((read = await file.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (done - said >= 64 << 20)
            {
                said = done;
                progress?.Invoke(done, total);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// An archive (.zip or .tar.gz) unpacked into a folder, made if it isn't there. An entry whose path would land outside
    /// the folder, or that is a link, is refused, and nothing of the archive is trusted to say where it goes. On Linux and
    /// macOS what was executable stays so.
    /// </summary>
    public static void Unpack(string archive, string folder)
    {
        string root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(archive);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string to = Inside(root, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(to);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                entry.ExtractToFile(to, overwrite: true);
                if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0b001_001_001) != 0)
                    System.IO.File.SetUnixFileMode(to, System.IO.File.GetUnixFileMode(to) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            return;
        }

        if (!archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) && !archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(archive)} isn't a .zip or a .tar.gz");

        using FileStream file = System.IO.File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            string to = Inside(root, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(to);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    entry.ExtractToFile(to, overwrite: true);
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    // A library's versioned names (libllama.so -> libllama.so.1) are made as copies, never as links out
                    string target = Inside(root, Path.Combine(Path.GetDirectoryName(entry.Name) ?? string.Empty, entry.LinkName));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (System.IO.File.Exists(target))
                        System.IO.File.Copy(target, to, overwrite: true);
                    break;
            }
        }
    }

    // The path an entry goes to, refused when it would be outside the folder
    private static string Inside(string root, string name)
    {
        string to = Path.GetFullPath(Path.Combine(root, name.Replace('\\', '/').TrimStart('/')));
        string within = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!to.StartsWith(within, PlatformPaths.Comparison) && !string.Equals(to, root, PlatformPaths.Comparison))
            throw new InvalidDataException($"the archive has a file outside its folder ({name}), it wasn't unpacked");
        return to;
    }

    /// <summary>
    /// The llama-server program in a folder of llama.cpp, wherever the archive put it (its builds put it at the top or in
    /// build/bin), null when there isn't one.
    /// </summary>
    public static string? FindServer(string folder)
    {
        if (!Directory.Exists(folder))
            return null;
        string name = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        try
        {
            return Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .OrderBy(path => path.Length).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
