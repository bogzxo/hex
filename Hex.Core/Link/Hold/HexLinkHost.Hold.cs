using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private (HexLinkHoldStore Store, HexLinkHold Entry)? HoldOf(string wire, string? full = null)
    {
        int slash = wire.IndexOf('/');
        if (slash <= 0)
            return null;
        foreach (HexLinkFolder folder in Folders)
        {
            if (services.Holds(folder) is not { Count: > 0 } store)
                continue;
            if (PlatformPaths.Comparer.Equals(folder.Name, wire[..slash]) && store.Get(wire) is { } entry)
                return (store, entry);
            if (full is not null && store.WireFromFullPath(full) is { } physical && store.Get(physical) is { } alias)
                return (store, alias);
        }
        return null;
    }

    // All Link reads use the same published version, including when only the other device is running sync.
    private string PublishedFile(string wire, string full) =>
        HoldOf(wire, full) switch
        {
            { Entry.BaseDeleted: true } => throw HexLinkException.NotFound($"There's no file {wire}."),
            { } held => held.Store.BaseFile(held.Entry),
            null => full,
        };

    private JsonObject PublishedListing(string wire, JsonObject listing)
    {
        if (wire.Length == 0 || listing["entries"] is not JsonArray entries)
            return listing;
        string prefix = wire.TrimEnd('/') + "/";
        string folderName = prefix[..prefix.IndexOf('/')];
        HexLinkFolder? folder = Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folderName));
        if (folder is null || services.Holds(folder) is not { Count: > 0 } store)
            return listing;

        foreach (HexLinkHold entry in store.List())
        {
            if (!entry.Path.StartsWith(prefix, PlatformPaths.Comparison) || entry.Path.AsSpan(prefix.Length).Contains('/'))
                continue;
            string name = entry.Path[prefix.Length..];
            JsonObject? published = entries.OfType<JsonObject>().FirstOrDefault(row =>
                PlatformPaths.Comparer.Equals(row["name"]?.GetValue<string>(), name));
            if (entry.BaseDeleted)
            {
                if (published is not null)
                    entries.Remove(published);
            }
            else if (published is not null)
            {
                published["kind"] = "file";
                published["size"] = new FileInfo(store.BaseFile(entry)).Length;
            }
            else if (entries.Count < HexLinkFiles.MOST_LISTED && HexLinkFiles.MayReach(Folders, entry.Path, services.Excludes))
            {
                entries.Add((JsonNode)new JsonObject
                {
                    ["name"] = name, ["kind"] = "file", ["size"] = new FileInfo(store.BaseFile(entry)).Length,
                });
            }
        }
        return listing;
    }

    private (bool Held, string? Hash) PublishedWatchHash(string wire, string full) => HoldOf(wire, full) is { } held
        ? (true, held.Entry.BaseDeleted ? null : held.Entry.BaseHash) : (false, null);

    private void RequirePublishableGit(string repository)
    {
        int held = Folders.Sum(folder => services.Holds(folder)?.InRepository(repository).Count ?? 0);
        if (held > 0)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, HexLinkHoldGit.HeldCommit(held));
    }

    private bool TakeHeldWrite(string wire, string full, byte[]? bytes) => HoldOf(wire, full) is { } held &&
        (bytes is null ? held.Store.SetIncomingDeleted(held.Entry.Path, Device.Name) : held.Store.SetIncoming(held.Entry.Path, bytes, Device.Name));

    private JsonObject HeldWriteReply(string path, string? hash, JsonObject request)
    {
        if (HexLinkData.Bool(request, "accept_held") != true)
            throw HexLinkHoldReply.HeldOn(services.Device.Name);
        return new JsonObject { ["path"] = path, ["hash"] = hash, ["held"] = true };
    }
}

/// <summary>Interprets a successful held reply without telling the sync engine that a write reached the remote tree.</summary>
internal static class HexLinkHoldReply
{
    public static HexLinkException HeldOn(string device) => new(HexLinkErrors.NOT_ALLOWED,
        $"Held on {device}", new JsonObject { ["held"] = true });

    public static void Check(JsonObject reply, HexLinkDevice device)
    {
        if (HexLinkData.Bool(reply, "held") == true)
            throw HeldOn(device.Name);
    }
}
