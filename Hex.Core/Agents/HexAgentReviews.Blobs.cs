using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>
/// The baselines' texts, each kept once by what it says (128 bits of its SHA-256) however many chats and files have it,
/// Brotli-packed so a file costs a fraction of its size in memory. A text is counted by its holders and goes with the last
/// of them. The few read most lately are kept unpacked, since the review draws one many times over. Safe from any thread.
/// </summary>
internal sealed class ReviewBlobs
{
    // Texts kept unpacked, by when they were last asked for
    private const int UNPACKED = 4;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Blob> blobs = new(StringComparer.Ordinal);
    private readonly List<(string Hash, string Text)> unpacked = [];

    private sealed class Blob(byte[]? packed, string? diskPath = null)
    {
        public byte[]? Packed = packed;
        public string? DiskPath = diskPath;
        public int Holders;
    }

    /// <summary>What a text is known by.</summary>
    public static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())), 0, 16);

    /// <summary>How many different texts are kept.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return blobs.Count;
        }
    }

    /// <summary>Whether a text is kept.</summary>
    public bool Has(string hash)
    {
        lock (gate)
            return blobs.ContainsKey(hash);
    }

    /// <summary>Keeps a text for one more holder, and says what it's known by.</summary>
    public string Add(string text)
    {
        string hash = HashOf(text);
        lock (gate)
        {
            if (blobs.TryGetValue(hash, out Blob? known))
            {
                known.Holders++;
                return hash;
            }

            blobs[hash] = new Blob(Pack(text)) { Holders = 1 };
            Remember(hash, text);
            return hash;
        }
    }

    /// <summary>Keeps a text already packed (read back from the disk) for one more holder.</summary>
    public void AddPacked(string hash, byte[] packed)
    {
        lock (gate)
        {
            if (blobs.TryGetValue(hash, out Blob? known))
                known.Holders++;
            else
                blobs[hash] = new Blob(packed) { Holders = 1 };
        }
    }

    /// <summary>Registers a packed blob that already lives on disk so its bytes stay out of RAM until inspected.</summary>
    public void AddDiskBacked(string hash, string diskPath)
    {
        lock (gate)
        {
            if (blobs.TryGetValue(hash, out Blob? known))
            {
                known.Holders++;
                known.DiskPath ??= diskPath;
            }
            else
            {
                blobs[hash] = new Blob(null, diskPath) { Holders = 1 };
            }
        }
    }

    /// <summary>Notes that a blob is now persisted at <paramref name="diskPath"/> and drops its in-memory packed bytes.</summary>
    public void MarkWrittenToDisk(string hash, string diskPath)
    {
        lock (gate)
        {
            if (blobs.TryGetValue(hash, out Blob? blob))
            {
                blob.DiskPath = diskPath;
                blob.Packed = null;
            }
        }
    }

    /// <summary>The packed bytes of a text, for the disk.</summary>
    public byte[]? PackedOf(string hash)
    {
        string? diskPath;
        lock (gate)
        {
            if (!blobs.TryGetValue(hash, out Blob? blob))
                return null;
            if (blob.Packed is { } packed)
                return packed;
            diskPath = blob.DiskPath;
        }

        if (diskPath is null)
            return null;

        try
        {
            return File.Exists(diskPath) ? File.ReadAllBytes(diskPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A text, null for one that isn't kept (or can't be read).</summary>
    public string? Get(string hash)
    {
        byte[]? packed;
        string? diskPath;
        lock (gate)
        {
            for (int i = unpacked.Count - 1; i >= 0; i--)
            {
                if (unpacked[i].Hash == hash)
                    return unpacked[i].Text;
            }

            if (!blobs.TryGetValue(hash, out Blob? blob))
                return null;

            packed = blob.Packed;
            diskPath = blob.DiskPath;
        }

        string? text = packed is not null ? Unpack(packed) : (diskPath is not null ? UnpackFile(diskPath) : null);
        if (text is not null)
        {
            lock (gate)
            {
                if (blobs.ContainsKey(hash))
                    Remember(hash, text);
            }
        }
        return text;
    }

    /// <summary>A holder let go of its text; the last to do so takes it away.</summary>
    public void Release(string hash)
    {
        lock (gate)
        {
            if (!blobs.TryGetValue(hash, out Blob? blob) || --blob.Holders > 0)
                return;

            blobs.Remove(hash);
            unpacked.RemoveAll(entry => entry.Hash == hash);
        }
    }

    /// <summary>Every text let go of.</summary>
    public void Clear()
    {
        lock (gate)
        {
            blobs.Clear();
            blobs.TrimExcess();
            unpacked.Clear();
            unpacked.TrimExcess();
        }
    }

    // Called with the gate held
    private void Remember(string hash, string text)
    {
        unpacked.RemoveAll(entry => entry.Hash == hash);
        unpacked.Add((hash, text));
        if (unpacked.Count > UNPACKED)
            unpacked.RemoveAt(0);
    }

    private static byte[] Pack(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using var packed = new MemoryStream(bytes.Length / 3 + 16);
        using (var brotli = new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(bytes);
        return packed.ToArray();
    }

    private static string? Unpack(byte[] packed)
    {
        try
        {
            using var from = new MemoryStream(packed);
            using var brotli = new BrotliStream(from, CompressionMode.Decompress);
            using var bytes = new MemoryStream(packed.Length * 3);
            brotli.CopyTo(bytes);
            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? UnpackFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var from = File.OpenRead(path);
            using var brotli = new BrotliStream(from, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
