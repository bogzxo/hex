using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    // The files the device has open, made the first time it watches one
    private HexLinkWatch? files;

    // One write at a time through this link, so the check of a file's hash and the write after it aren't split by another
    private readonly SemaphoreSlim writing = new(1, 1);

    private IReadOnlyList<HexLinkFolder> Folders => services.Workspace.Folders;

    private JsonObject FsList(JsonObject parameters)
    {
        Need(HexLinkRights.View, "fs/list");
        return HexLinkFiles.List(Folders, HexLinkData.String(parameters, "path", HexLinkFiles.MOST_PATH) ?? string.Empty);
    }

    private JsonObject FsRead(JsonObject parameters)
    {
        Need(HexLinkRights.View, "fs/read");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        return HexLinkFiles.Read(HexLinkFiles.Resolve(Folders, wire), wire);
    }

    // A file written if it's still what the writer read, through the editor so an open tab takes it
    private async Task<JsonNode?> FsWrite(JsonObject parameters)
    {
        Need(HexLinkRights.Edit, "fs/write");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = HexLinkFiles.Resolve(Folders, wire);
        string? baseHash = HexLinkData.String(parameters, "base", 64);
        byte[] bytes = BytesOf(parameters);
        services.Audit("edit", Device, $"fs/write {wire}, {bytes.Length} bytes");

        await writing.WaitAsync();
        try
        {
            string hash = await HexLinkFiles.Write(full, wire, baseHash, bytes, services.WriteFile);
            return new JsonObject { ["path"] = wire, ["hash"] = hash };
        }
        finally
        {
            writing.Release();
        }
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
        string full = HexLinkFiles.Resolve(Folders, wire);
        HexLinkWatch? watch = files;
        if (watch is null)
        {
            var made = new HexLinkWatch(FileMoved, WatchEvery);
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
        if (HexLinkAllowed.Has(Rights, HexLinkRights.View))
            session.Tell("fs/changed", new JsonObject { ["path"] = wire, ["hash"] = hash });
    }

    /// <summary>How often watched files are looked at, a second unless a test wants it sooner. Set before the first watch.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(1);
}
