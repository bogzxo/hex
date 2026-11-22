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

/// <summary>Local bindings are pinned by provision/share and survive disconnects and restarts.</summary>
internal sealed class HexLinkAuthorityStore
{
    private readonly string file;
    private readonly Lock gate = new();
    private Dictionary<string, HexLinkAuthority> bindings;
    private HexLinkWorkspace? advertisedSource, advertised;

    public HexLinkAuthorityStore(string file)
    {
        this.file = Path.GetFullPath(file);
        bindings = Load(this.file);
    }

    public HexLinkAuthority? Of(HexLinkFolder folder)
    {
        lock (gate)
            return bindings.GetValueOrDefault(Key(folder));
    }

    public HexLinkAuthority Bind(HexLinkFolder folder, HexLinkAuthority authority)
    {
        authority = HexLinkAuthority.Validate(authority);
        string path = Key(folder);
        lock (gate)
        {
            if (bindings.TryGetValue(path, out HexLinkAuthority? pinned))
            {
                if (pinned != authority)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "This folder already has a different authority binding.");
                return pinned;
            }
            Keep(path, authority);
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
            if (bindings.GetValueOrDefault(path) == next)
                return next;
            if (bindings.GetValueOrDefault(path) != expected)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The folder authority has already changed.");
            if (next.DeviceId == expected.DeviceId)
                throw HexLinkException.Invalid("This device is already the folder authority.");
            Keep(path, next);
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
                Folders = [.. workspace.Folders.Select(folder => folder with { Authority = bindings.GetValueOrDefault(Key(folder)) })]
            };
            advertisedSource = workspace;
            return advertised;
        }
    }

    private void Keep(string path, HexLinkAuthority authority)
    {
        var next = new Dictionary<string, HexLinkAuthority>(bindings, PlatformPaths.Comparer) { [path] = authority };
        var values = new JsonObject();
        foreach (var (folder, binding) in next)
            values[folder] = binding.ToJson();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        HexLinkFiles.WriteAtomic(file, Encoding.UTF8.GetBytes(values.ToJsonString()));
        bindings = next;
        advertisedSource = advertised = null;
    }

    private static Dictionary<string, HexLinkAuthority> Load(string file)
    {
        var found = new Dictionary<string, HexLinkAuthority>(PlatformPaths.Comparer);
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
            found.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), authority);
        }
        return found;
    }

    private static string Key(HexLinkFolder folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
}
