using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>The device that orders a shared folder's changes, and the generation of that assignment.</summary>
internal sealed record HexLinkAuthority(string DeviceId, long Epoch)
{
    public JsonObject ToJson() => new() { ["device"] = DeviceId, ["epoch"] = Epoch };

    public static HexLinkAuthority? Of(JsonObject? said) =>
        said is not null && HexLinkData.String(said, "device", 64) is { Length: 64 } id &&
        id.All(char.IsAsciiHexDigit) && HexLinkData.Long(said, "epoch") is >= 1 and var epoch
            ? new HexLinkAuthority(id.ToUpperInvariant(), epoch) : null;

    internal static HexLinkAuthority Validate(HexLinkAuthority authority) =>
        Of(authority.ToJson()) ?? throw HexLinkException.Invalid("The folder authority identity or epoch is invalid.");
}

internal enum HexLinkAuthorityProvenance { None, Provisioned, Explicit }

/// <summary>Local bindings are pinned by provision/share and survive disconnects and restarts.</summary>
internal sealed class HexLinkAuthorityStore
{
    private readonly string file;
    private readonly Lock gate = new();
    private sealed record Binding(HexLinkAuthority Authority, HexLinkAuthorityProvenance Provenance);
    private Dictionary<string, Binding> bindings;
    private HexLinkWorkspace? advertisedSource, advertised;

    public HexLinkAuthorityStore(string file)
    {
        this.file = Path.GetFullPath(file);
        bindings = Load(this.file);
    }

    public HexLinkAuthority? Of(HexLinkFolder folder)
    {
        lock (gate)
            return bindings.GetValueOrDefault(Key(folder))?.Authority;
    }

    public HexLinkAuthority Bind(HexLinkFolder folder, HexLinkAuthority authority,
        HexLinkAuthorityProvenance provenance = HexLinkAuthorityProvenance.Explicit)
    {
        authority = HexLinkAuthority.Validate(authority);
        string path = Key(folder);
        lock (gate)
        {
            if (bindings.TryGetValue(path, out Binding? pinned))
            {
                if (pinned.Authority != authority)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "This folder already has a different authority binding.");
                if (pinned.Provenance != HexLinkAuthorityProvenance.None)
                    return pinned.Authority;
            }
            Keep(path, new Binding(authority, provenance));
            return authority;
        }
    }

    /// <summary>Applies a transfer authorized by the old authority or confirmed locally after permanent loss.</summary>
    public HexLinkAuthority Transfer(HexLinkFolder folder, HexLinkAuthority expected, string deviceId)
    {
        expected = HexLinkAuthority.Validate(expected);
        if (expected.Epoch == long.MaxValue)
            throw HexLinkException.Invalid("The folder authority epoch cannot advance.");
        var next = HexLinkAuthority.Validate(new HexLinkAuthority(deviceId, expected.Epoch + 1));
        string path = Key(folder);
        lock (gate)
        {
            if (bindings.GetValueOrDefault(path)?.Authority == next)
                return next;
            if (bindings.GetValueOrDefault(path)?.Authority != expected)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The folder authority has already changed.");
            if (next.DeviceId == expected.DeviceId)
                throw HexLinkException.Invalid("This device is already the folder authority.");
            Keep(path, new Binding(next, HexLinkAuthorityProvenance.Explicit));
            return next;
        }
    }

    public void Require(HexLinkFolder folder, HexLinkAuthority expected)
    {
        if (Of(folder) != HexLinkAuthority.Validate(expected))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The folder authority or its epoch changed.");
    }

    public HexLinkWorkspace Advertise(HexLinkWorkspace workspace)
    {
        lock (gate)
        {
            if (ReferenceEquals(workspace, advertisedSource))
                return advertised!;
            advertised = bindings.Count == 0 ? workspace : workspace with
            {
                Folders = [.. workspace.Folders.Select(folder => folder with { Authority = bindings.GetValueOrDefault(Key(folder))?.Authority })]
            };
            advertisedSource = workspace;
            return advertised;
        }
    }

    /// <summary>Retains provable legacy Get bindings and transfers, and drops unmarked first-epoch election pins.</summary>
    public int MigrateLegacy(IEnumerable<(HexLinkFolder Folder, string Source)> provisioned)
    {
        lock (gate)
        {
            if (bindings.Values.All(binding => binding.Provenance != HexLinkAuthorityProvenance.None))
                return 0;

            var savedSources = provisioned.ToLookup(binding => Key(binding.Folder), binding => binding.Source, PlatformPaths.Comparer);
            var next = new Dictionary<string, Binding>(PlatformPaths.Comparer);
            foreach (var (path, binding) in bindings)
            {
                var provenance = binding.Provenance;
                if (provenance == HexLinkAuthorityProvenance.None)
                {
                    if (binding.Authority.Epoch > 1)
                        provenance = HexLinkAuthorityProvenance.Explicit;
                    else if (savedSources[path].Contains(binding.Authority.DeviceId, StringComparer.OrdinalIgnoreCase))
                        provenance = HexLinkAuthorityProvenance.Provisioned;
                    else
                        continue;
                }
                next.Add(path, binding with { Provenance = provenance });
            }
            int removed = bindings.Count - next.Count;
            Keep(next);
            return removed;
        }
    }

    private void Keep(string path, Binding binding) =>
        Keep(new Dictionary<string, Binding>(bindings, PlatformPaths.Comparer) { [path] = binding });

    private void Keep(Dictionary<string, Binding> next)
    {
        var values = new JsonObject();
        foreach (var (folder, binding) in next)
        {
            JsonObject item = binding.Authority.ToJson();
            item["provenance"] = binding.Provenance switch
            {
                HexLinkAuthorityProvenance.Provisioned => "provisioned",
                HexLinkAuthorityProvenance.Explicit => "explicit",
                _ => "none"
            };
            values[folder] = item;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        HexLinkFiles.WriteAtomic(file, Encoding.UTF8.GetBytes(values.ToJsonString()));
        bindings = next;
        advertisedSource = advertised = null;
    }

    private static Dictionary<string, Binding> Load(string file)
    {
        var found = new Dictionary<string, Binding>(PlatformPaths.Comparer);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return found;
        }
        if (JsonNode.Parse(bytes) is not JsonObject kept)
            throw new IOException("The folder authority bindings could not be read.");
        foreach (var (path, value) in kept)
        {
            if (!Path.IsPathFullyQualified(path) || value is not JsonObject item || HexLinkAuthority.Of(item) is not { } authority)
                throw new IOException("The folder authority bindings contain an invalid entry.");
            var provenance = HexLinkData.String(item, "provenance", 20) switch
            {
                "provisioned" => HexLinkAuthorityProvenance.Provisioned,
                "explicit" => HexLinkAuthorityProvenance.Explicit,
                _ => HexLinkAuthorityProvenance.None
            };
            found.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), new Binding(authority, provenance));
        }
        return found;
    }

    private static string Key(HexLinkFolder folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
}
