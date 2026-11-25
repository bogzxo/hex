using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A conflict that was settled by keeping one side, with both versions kept so it can be undone: this side's and the
/// other device's, by hash (null where that side had deleted the file). <see cref="ResultHash"/> is what this device's
/// file was straight after the resolve, null when it was gone, so a later edit can be told from the resolve itself.
/// </summary>
internal sealed record HexLinkResolution(string Id, string Path, string Device, string DeviceId, DateTimeOffset At,
    HexLinkSyncKeep Keep, string? MineHash, string? TheirsHash, string? ResultHash, bool Undone = false)
{
    /// <summary>The hash of the version the resolve chose, null when it was a delete.</summary>
    public string? WinnerHash => Keep == HexLinkSyncKeep.Mine ? MineHash : TheirsHash;

    /// <summary>The hash of the version the resolve threw away, which Undo brings back, null when that was a delete.</summary>
    public string? LoserHash => Keep == HexLinkSyncKeep.Mine ? TheirsHash : MineHash;
}

/// <summary>How an undo ended.</summary>
internal enum HexLinkUndoStatus
{
    /// <summary>The other version is back as a local change and travels as an ordinary edit.</summary>
    Undone,

    /// <summary>The file changed since the resolve: nothing was written, the merge editor can put the versions together.</summary>
    NeedsMerge,

    /// <summary>Nothing was done, <see cref="HexLinkUndoResult.Message"/> says why.</summary>
    Refused
}

internal sealed record HexLinkUndoResult(HexLinkUndoStatus Status, string Message)
{
    public string Word => Status switch
    {
        HexLinkUndoStatus.Undone => "undone",
        HexLinkUndoStatus.NeedsMerge => "needs_merge",
        _ => "refused"
    };
}

/// <summary>
/// The losing versions of the conflicts a device settled, kept under the Link data folder so Undo has them: one folder
/// for each resolution with a small record and the bytes of both sides. The newest <see cref="MOST"/> are kept, for
/// <see cref="KEPT"/>; older ones go when the store is next used.
/// </summary>
internal sealed class HexLinkResolutions(string folder, TimeProvider? clock = null)
{
    /// <summary>The most resolutions kept.</summary>
    public const int MOST = 50;

    /// <summary>How long a resolution can be undone.</summary>
    public static readonly TimeSpan KEPT = TimeSpan.FromDays(7);

    private readonly Lock gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>Keeps a resolution with both versions, returning its record. Null when it couldn't be written.</summary>
    public HexLinkResolution? Add(string path, string device, string deviceId, HexLinkSyncKeep keep, byte[]? mine, byte[]? theirs, string? result)
    {
        var record = new HexLinkResolution(Guid.NewGuid().ToString("N")[..12], path, device, deviceId, time.GetUtcNow(), keep,
            mine is null ? null : HexLinkFiles.Hash(mine), theirs is null ? null : HexLinkFiles.Hash(theirs), result);
        try
        {
            lock (gate)
            {
                string directory = Path.Combine(folder, record.Id);
                Directory.CreateDirectory(directory);
                if (mine is not null)
                    HexLinkFiles.WriteAtomic(Path.Combine(directory, "mine"), mine);
                if (theirs is not null)
                    HexLinkFiles.WriteAtomic(Path.Combine(directory, "theirs"), theirs);
                Write(record);
                Prune();
            }

            return record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The resolutions that can still be undone, newest first.</summary>
    public IReadOnlyList<HexLinkResolution> List()
    {
        lock (gate)
        {
            Prune();
            return [.. Read().OrderByDescending(each => each.At)];
        }
    }

    /// <summary>One resolution by its id, null when it's gone or expired.</summary>
    public HexLinkResolution? Get(string id) => List().FirstOrDefault(each => each.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bytes of the version with this hash that resolution kept, null when there are none or they were changed.</summary>
    public byte[]? Bytes(HexLinkResolution record, string? hash)
    {
        if (hash is null)
            return null;
        try
        {
            string name = hash == record.MineHash ? "mine" : hash == record.TheirsHash ? "theirs" : string.Empty;
            string full = Path.Combine(folder, record.Id, name);
            if (name.Length == 0 || !File.Exists(full))
                return null;
            byte[] bytes = File.ReadAllBytes(full);
            return HexLinkFiles.Hash(bytes) == hash ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Marks a resolution undone, so it isn't offered again.</summary>
    public void MarkUndone(string id)
    {
        lock (gate)
        {
            if (Read().FirstOrDefault(each => each.Id == id) is { } record)
            {
                try { Write(record with { Undone = true }); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void Write(HexLinkResolution record) => HexLinkFiles.WriteAtomic(Path.Combine(folder, record.Id, "record.json"),
        System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["path"] = record.Path,
            ["device"] = record.Device,
            ["device_id"] = record.DeviceId,
            ["at"] = record.At.ToUnixTimeMilliseconds(),
            ["keep"] = record.Keep == HexLinkSyncKeep.Mine ? "mine" : "theirs",
            ["mine"] = record.MineHash,
            ["theirs"] = record.TheirsHash,
            ["result"] = record.ResultHash,
            ["undone"] = record.Undone
        }.ToJsonString()));

    private List<HexLinkResolution> Read()
    {
        var all = new List<HexLinkResolution>();
        if (!Directory.Exists(folder))
            return all;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(folder))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "record.json"))) is JsonObject node
                        && (string?)node["path"] is { } path && (long?)node["at"] is { } at)
                        all.Add(new HexLinkResolution(Path.GetFileName(directory), path, (string?)node["device"] ?? string.Empty,
                            (string?)node["device_id"] ?? string.Empty, DateTimeOffset.FromUnixTimeMilliseconds(at),
                            (string?)node["keep"] == "theirs" ? HexLinkSyncKeep.Theirs : HexLinkSyncKeep.Mine,
                            (string?)node["mine"], (string?)node["theirs"], (string?)node["result"], (bool?)node["undone"] ?? false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
                {
                    // A record that can't be read is as good as gone
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return all;
    }

    // Under the gate: what has expired or is past the newest MOST goes
    private void Prune()
    {
        DateTimeOffset now = time.GetUtcNow();
        List<HexLinkResolution> all = Read();
        var keep = all.Where(each => now - each.At <= KEPT).OrderByDescending(each => each.At).Take(MOST).Select(each => each.Id).ToHashSet();
        foreach (HexLinkResolution gone in all.Where(each => !keep.Contains(each.Id)))
        {
            try { Directory.Delete(Path.Combine(folder, gone.Id), true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
