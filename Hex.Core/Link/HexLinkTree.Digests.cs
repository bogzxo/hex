using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>Directory digests let a complete manifest omit directories the caller already has exactly.</summary>
internal sealed class HexLinkDirectoryDigests
{
    private const int MaximumDirectories = 4096;
    private const int DigestBudget = 1024 * 1024;
    private readonly Dictionary<string, List<HexLinkTreeFile>> directories = new(StringComparer.Ordinal);

    private HexLinkDirectoryDigests(HexLinkManifest manifest)
    {
        foreach (HexLinkTreeFile file in manifest.Files)
        {
            string directory = DirectoryOf(file.Path);
            if (!directories.TryGetValue(directory, out List<HexLinkTreeFile>? files))
                directories.Add(directory, files = []);
            files.Add(file);
        }
    }

    internal static HexLinkDirectoryDigests From(HexLinkManifest manifest) => new(manifest);

    private static string DirectoryOf(string path) => path[..path.LastIndexOf('/')];

    public JsonObject Offer()
    {
        var groups = new JsonObject();
        int budget = DigestBudget;
        foreach (var (name, files) in directories.OrderBy(each => each.Key, StringComparer.Ordinal))
        {
            budget -= 6 * name.Length + 90;
            if (budget <= 0 || groups.Count == MaximumDirectories)
                break;
            groups[name] = Digest(files);
        }
        return groups;
    }

    public static (HexLinkManifest Changed, JsonArray? Equal) Select(HexLinkManifest manifest, JsonObject parameters)
    {
        if (!manifest.Certified || parameters["directories"] is not JsonObject said || said.Count is <= 0 or > MaximumDirectories)
            return (manifest, null);

        var seen = new HexLinkDirectoryDigests(manifest);
        var same = new HashSet<string>(StringComparer.Ordinal);
        var names = new JsonArray();
        foreach (var (directory, hash) in said)
        {
            if (!seen.directories.TryGetValue(directory, out List<HexLinkTreeFile>? group) ||
                hash is not JsonValue value || !value.TryGetValue(out string? digest) || digest != Digest(group))
                continue;
            same.Add(directory);
            names.Add((JsonNode?)JsonValue.Create(directory));
        }

        if (same.Count == 0)
            return (manifest, null);
        return (manifest with { Files = [.. manifest.Files.Where(file => !same.Contains(DirectoryOf(file.Path)))] }, names);
    }

    public HexLinkManifest Reconstruct(HexLinkManifest partial, JsonArray equal, JsonObject offer, int total)
    {
        HexLinkTree.CheckCount(total);
        if (!partial.Certified || equal.Count is <= 0 or > MaximumDirectories || total < partial.Files.Count)
            throw HexLinkException.Invalid("The peer sent an invalid directory digest manifest.");
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<HexLinkTreeFile>(total);
        files.AddRange(partial.Files);
        foreach (JsonNode? candidate in equal)
        {
            if (candidate is not JsonValue value || !value.TryGetValue(out string? name) || name is null ||
                !accepted.Add(name) || !offer.ContainsKey(name) || !directories.TryGetValue(name, out List<HexLinkTreeFile>? group))
                throw HexLinkException.Invalid("The peer claimed an unoffered or duplicate directory digest.");
            if (group.Count > total - files.Count)
                throw HexLinkException.Invalid("The reconstructed manifest has too many entries.");
            files.AddRange(group);
        }

        if (files.Count != total || partial.Files.Any(file => accepted.Contains(DirectoryOf(file.Path))))
            throw HexLinkException.Invalid("The directory digest pages did not form a complete, disjoint manifest.");
        return partial with { Files = files };
    }

    private static string Digest(List<HexLinkTreeFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> values = stackalloc byte[9];
        foreach (HexLinkTreeFile file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            Append(hash, file.Path);
            Append(hash, file.Hash);
            Append(hash, file.Text);
            Append(hash, file.Eol);
            BinaryPrimitives.WriteInt64LittleEndian(values, file.Size);
            values[8] = file.Executable switch { true => 2, false => 1, null => 0 };
            hash.AppendData(values);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is not null)
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}
