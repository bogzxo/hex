using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    // The files the device has open, made the first time it watches one
    private HexLinkWatch? files;

    private IReadOnlyList<HexLinkFolder> Folders => services.Workspace.Folders;

    private string ResolveFile(string wire) => ResolveUsableTreeFile(wire);

    private JsonObject FsList(JsonObject parameters)
    {
        Need(HexLinkRights.View, "fs/list");
        string wire = HexLinkData.String(parameters, "path", HexLinkFiles.MOST_PATH) ?? string.Empty;
        return PublishedListing(wire, HexLinkFiles.List(Folders, wire, services.Excludes));
    }

    private async Task<JsonNode?> FsRead(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "fs/read");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = ResolveFile(wire);
        await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel);
        await EnsureGitFollowFile(full, cancel);
        Need(HexLinkRights.View, "fs/read");
        return HexLinkFiles.Read(PublishedFile(wire, full), wire);
    }

    // A file written if it's still what the writer read, through the editor so an open tab takes it
    private async Task<JsonNode?> FsWrite(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "fs/write");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = ResolveFile(wire);
        string? baseHash = HexLinkData.String(parameters, "base", 64);
        byte[] bytes = BytesOf(parameters);
        await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel);
        await EnsureGitFollowFile(full, cancel);
        Need(HexLinkRights.Edit, "fs/write");
        if (!PlatformPaths.Comparer.Equals(full, ResolveFile(wire)))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The file path changed before it was written.");
        services.Audit("edit", Device, $"fs/write {wire}, {bytes.Length} bytes");

        if (TakeHeldWrite(wire, full, bytes))
            return HeldWriteReply(wire, HexLinkFiles.Hash(bytes), parameters);
        string? hash = await ApplyHeldSubmission(full, wire, baseHash, bytes, cancel,
            HexLinkData.Bool(parameters, "held_submission") == true);
        return new JsonObject { ["path"] = wire, ["hash"] = hash };
    }

    private async Task<JsonNode?> FsDeleteChecked(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "fs/delete");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = ResolveFile(wire);
        string baseHash = HexLinkData.Need(parameters, "base", 64);
        await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(cancel);
        await EnsureGitFollowFile(full, cancel);
        Need(HexLinkRights.Edit, "fs/delete");
        if (!PlatformPaths.Comparer.Equals(full, ResolveFile(wire)))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The file path changed before it was deleted.");
        services.Audit("edit", Device, $"fs/delete {wire}");
        if (TakeHeldWrite(wire, full, null))
            return HeldWriteReply(wire, null, parameters);
        if (!File.Exists(full))
            throw HexLinkException.NotFound($"There's no file {wire}.");
        await ApplyHeldSubmission(full, wire, baseHash, null, cancel,
            HexLinkData.Bool(parameters, "held_submission") == true);
        return new JsonObject { ["path"] = wire };
    }

    // What fs/write is to write, base64 as it came, or text in the encoding it names (utf-8 when it names none)
    private static byte[] BytesOf(JsonObject parameters)
    {
        if (HexLinkData.String(parameters, "base64", HexLinkFiles.MOST_READ / 3 * 4 + 4) is { } base64)
        {
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw HexLinkException.Invalid("base64 isn't base64.");
            }
        }

        if (HexLinkData.String(parameters, "text", HexLinkFiles.MOST_READ) is { } text)
        {
            return HexLinkFiles.Encode(text, HexLinkData.String(parameters, "encoding", 20) ?? "utf-8")
                ?? throw HexLinkException.Invalid("encoding is one of utf-8, utf-8-bom, utf-16le and utf-16be.");
        }

        throw HexLinkException.Invalid("fs/write needs text or base64.");
    }

    private JsonObject FsWatch(JsonObject parameters)
    {
        Need(HexLinkRights.View, "fs/watch");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = ResolveFile(wire);
        HexLinkWatch? watch = files;
        if (watch is null)
        {
            var made = new HexLinkWatch(FileMoved, WatchEvery, PublishedWatchHash);
            watch = Interlocked.CompareExchange(ref files, made, null) ?? made;
            if (watch != made)
                made.Dispose();
        }

        if (!watch.Add(wire, full, out string? hash))
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A device may watch {HexLinkWatch.MOST} files at once.");
        return new JsonObject { ["path"] = wire, ["hash"] = hash };
    }

    private JsonObject FsUnwatch(JsonObject parameters)
    {
        files?.Remove(HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH));
        return [];
    }

    // A watched file changed here, said to the device if it may still see it
    private void FileMoved(string wire, string? hash)
    {
        if (HexLinkAllowed.Has(Rights, HexLinkRights.View) && HexLinkFiles.MayReach(Folders, wire, services.Excludes))
            session.Tell("fs/changed", new JsonObject { ["path"] = wire, ["hash"] = hash });
    }

    /// <summary>How often watched files are looked at, a second unless a test wants it sooner. Set before the first watch.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(1);
}
