using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    /// <summary>What marks the other side's version of a file in conflict, put beside it: <c>name.theirs-device.ext</c>.</summary>
    public const string THEIRS = ".theirs-";

    /// <summary>
    /// How many requests a second the sync makes of the other side at most, counted over any second, under the 60 a
    /// link takes (HexLinkSession.MOST_REQUESTS) so pings and the editor's own asks still get through.
    /// </summary>
    public const int MOST_ASKS_A_SECOND = 45;

    // How often the state is written while a lot is moving, at most
    private const long SAVE_EVERY_MS = 2000;

    // The state file's version, and the most of what it keeps
    private const int STATE_VERSION = 2, MOST_BASES = HexLinkTree.MOST_FILES;

    /// <summary>
    /// The name of the state file of a device and a folder: both made safe for a file name, and a short hash of the
    /// folder's name so two folders whose names only differ in what was made safe don't share one.
    /// </summary>
    public static string StateName(string deviceId, string folderName)
    {
        string device = HexLinkGit.Slug(deviceId);
        return $"sync-{(device.Length > 24 ? device[..24] : device)}-{HexLinkGit.Slug(folderName)}-{HexLinkFiles.Hash(Encoding.UTF8.GetBytes(folderName))[..8]}.json";
    }

    /// <summary>
    /// Whether a wire path is one of the sync's own that never travels and isn't listed: not of this folder at all, a
    /// copy put beside a file in conflict, a temp file of an atomic write on its way, the state file when it's kept in
    /// the folder.
    /// </summary>
    public bool Ours(string wire) => Ours(wire, includeMissing: true);

    private bool Ours(string wire, bool includeMissing)
    {
        if (!wire.StartsWith(folder.Name + "/", StringComparison.Ordinal))
            return true;
        if (HexLinkTree.SyncArtifact(wire) || includeMissing && BlockedRepository(wire))
            return true;
        return StateWire is { } state && state.Equals(wire, StringComparison.OrdinalIgnoreCase);
    }

    // The state file's wire path when it's inside the folder, null when it isn't
    private string? StateWire => stateWire ??= PlatformPaths.IsUnder(StateFile, tree.Root)
        ? folder.Name + "/" + Path.GetRelativePath(tree.Root, StateFile).Replace(Path.DirectorySeparatorChar, '/')
        : string.Empty;

    private string? stateWire;

    /// <summary>
    /// A wire path made a full path of this side's folder through the jail every request of the other side's goes
    /// through, the state file refused besides. Throws a <see cref="HexLinkException"/> (not allowed) with why.
    /// </summary>
    private string Jailed(string wire)
    {
        string full = HexLinkFiles.Resolve([folder], wire);
        if (BlockedRepository(wire))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, $"{wire} is in a repository missing on one device.");
        if (PlatformPaths.Same(full, StateFile) || Ours(wire, includeMissing: false))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, $"{wire} is the sync's own and never travels.", new JsonObject { ["needs"] = "a path inside the workspace" });
        return full;
    }

    // The bases and the conflicts kept from last time read, when they're of this device and folder. A file that isn't
    // there or can't be read is no bases, everything compared afresh, which only ever costs conflicts, never a loss.
    private void Load(HexLinkManifest mine, HexLinkManifest theirs, CancellationToken cancel)
    {
        JsonObject agreement = AgreementOf(mine, theirs);
        JsonObject? savedAgreement = null;
        var keptBases = new Dictionary<string, HexLinkSyncAgreed>(StringComparer.Ordinal);
        var keptConflicts = new Dictionary<string, HexLinkSyncConflict>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(StateFile) && JsonNode.Parse(File.ReadAllBytes(StateFile)) is JsonObject state
                && HexLinkData.Int(state, "version") is 1 or STATE_VERSION
                && HexLinkData.String(state, "device", 200) == peer.Device.Id
                && HexLinkData.String(state, "folder", 200) == folder.Name)
            {
                savedAgreement = HexLinkData.Object(state, "agreement");
                if (HexLinkData.Object(state, "bases") is { } said)
                {
                    foreach (var (path, kept) in said)
                    {
                        if (keptBases.Count >= MOST_BASES)
                            break;
                        if (path.Length > HexLinkFiles.MOST_PATH || Ours(path, includeMissing: false))
                            continue;
                        // Version 1 kept one hash, the same bytes on both sides; version 2 each side's and their line ends
                        if (kept is JsonValue && HashIn(kept) is { } both)
                        {
                            keptBases[path] = Identical(both);
                        }
                        else if (kept is JsonObject pair && HashIn(pair["local"]) is { } local && HashIn(pair["remote"]) is { } remote)
                        {
                            keptBases[path] = new HexLinkSyncAgreed(local, remote, HashIn(pair["text"]),
                                EolIn(HexLinkData.String(pair, "local_eol", 10)), EolIn(HexLinkData.String(pair, "remote_eol", 10)),
                                HexLinkData.Bool(pair, "executable"));
                        }
                    }
                }

                foreach (JsonObject conflict in HexLinkData.Objects(state, "conflicts", MOST_SKIPPED))
                {
                    if (HexLinkData.String(conflict, "path", HexLinkFiles.MOST_PATH) is not { } path || Ours(path, includeMissing: false))
                        continue;
                    keptConflicts[path] = new HexLinkSyncConflict(path, HexLinkData.String(conflict, "kind", 100) ?? "changed on both",
                        HexLinkData.String(conflict, "copy", HexLinkFiles.MOST_PATH), HexLinkData.String(conflict, "copy_hash", 64));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }

        var stale = StaleTracked(savedAgreement, agreement, keptBases.Keys.Union(keptConflicts.Keys, StringComparer.Ordinal), cancel);
        foreach (string path in stale)
        {
            keptBases.Remove(path);
            if (keptConflicts.GetValueOrDefault(path)?.HasUnsavedChanges != true)
                keptConflicts.Remove(path);
        }

        lock (gate)
        {
            baseAgreement = agreement;
            staleTracked = stale;
            bases.Clear();
            conflicts.Clear();
            skipped.Clear();
            tries.Clear();
            localLf = localCrlf = remoteLf = remoteCrlf = 0;
            foreach (var (path, agreed) in keptBases)
            {
                bases[path] = agreed;
                Count(agreed, 1);
            }

            foreach (var (path, conflict) in keptConflicts)
                conflicts[path] = conflict;
        }
    }

    // A hash kept in the state file, lower case, null when it isn't one
    private static string? HashIn(JsonNode? kept) =>
        kept is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: 64 } text && text.All(char.IsAsciiHexDigit)
            ? text.ToLowerInvariant()
            : null;

    private static HexLinkEol EolIn(string? said) => said switch
    {
        "none" => HexLinkEol.None,
        "lf" => HexLinkEol.Lf,
        "crlf" => HexLinkEol.Crlf,
        "mixed" => HexLinkEol.Mixed,
        "binary" => HexLinkEol.Binary,
        _ => HexLinkEol.Unknown
    };

    private static string? EolWord(HexLinkEol eol) => eol switch
    {
        HexLinkEol.None => "none",
        HexLinkEol.Lf => "lf",
        HexLinkEol.Crlf => "crlf",
        HexLinkEol.Mixed => "mixed",
        HexLinkEol.Binary => "binary",
        _ => null
    };

    private readonly Lock saving = new();
    private long savedAt, snapshotVersion, writtenVersion;
    internal Action<long>? BeforeStateWrite { get; init; }

    // The bases and the conflicts written, whole or not at all
    internal void Save()
    {
        JsonObject state;
        long version;
        lock (gate)
        {
            version = ++snapshotVersion;
            var said = new JsonObject();
            foreach (var (path, agreed) in bases.OrderBy(each => each.Key, StringComparer.Ordinal))
            {
                said[path] = new JsonObject
                {
                    ["local"] = agreed.Local,
                    ["remote"] = agreed.Remote,
                    ["text"] = agreed.Text,
                    ["local_eol"] = EolWord(agreed.LocalEol),
                    ["remote_eol"] = EolWord(agreed.RemoteEol),
                    ["executable"] = agreed.Executable
                };
            }
            state = new JsonObject
            {
                ["version"] = STATE_VERSION,
                ["device"] = peer.Device.Id,
                ["folder"] = folder.Name,
                ["agreement"] = baseAgreement?.DeepClone(),
                ["bases"] = said,
                ["conflicts"] = new JsonArray([.. conflicts.Values.OrderBy(each => each.Path, StringComparer.Ordinal).Select(conflict => (JsonNode)new JsonObject
                {
                    ["path"] = conflict.Path,
                    ["kind"] = conflict.Kind,
                    ["copy"] = conflict.Copy,
                    ["copy_hash"] = conflict.CopyHash
                })])
            };
        }

        BeforeStateWrite?.Invoke(version);
        lock (saving)
        {
            if (version <= writtenVersion)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                HexLinkFiles.WriteAtomic(StateFile, Encoding.UTF8.GetBytes(state.ToJsonString()));
                writtenVersion = version;
                Volatile.Write(ref savedAt, Environment.TickCount64);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Written next time, a state a little behind only ever costs a comparison
            }
        }
    }

    private int periodicSave;

    // Only one completing worker schedules the periodic snapshot. An idle or conflict save can still publish its own.
    private void MaybeSave()
    {
        if (Environment.TickCount64 - Volatile.Read(ref savedAt) <= SAVE_EVERY_MS ||
            Interlocked.CompareExchange(ref periodicSave, 1, 0) != 0)
            return;
        try
        {
            if (Environment.TickCount64 - Volatile.Read(ref savedAt) > SAVE_EVERY_MS)
                Save();
        }
        finally
        {
            Volatile.Write(ref periodicSave, 0);
        }
    }

    // When each of the last requests went, to keep under MOST_ASKS_A_SECOND over any second
    private readonly Queue<long> asks = new();

    // Waits until a request may go without the other side telling it to wait
    private async Task Paced(CancellationToken stop)
    {
        while (true)
        {
            int wait;
            lock (asks)
            {
                long now = Environment.TickCount64;
                while (asks.Count > 0 && now - asks.Peek() >= 1000)
                    asks.Dequeue();
                if (asks.Count < MOST_ASKS_A_SECOND)
                {
                    asks.Enqueue(now);
                    return;
                }

                wait = (int)(1000 - (now - asks.Peek())) + 1;
            }

            await Task.Delay(wait, stop);
        }
    }

    // A request of the other side's made at the pace, asked again a little later when it says it's busy
    private async Task<T> Asked<T>(Func<Task<T>> ask, CancellationToken stop)
    {
        for (int tried = 0; ; tried++)
        {
            await Paced(stop);
            try
            {
                return await ask();
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.BUSY && tried < 30)
            {
                await Task.Delay(250 + 100 * tried, stop);
            }
        }
    }

    private Task Asked(Func<Task> ask, CancellationToken stop) => Asked(async () =>
    {
        await ask();
        return true;
    }, stop);
}
