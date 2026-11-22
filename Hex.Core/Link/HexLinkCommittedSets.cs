using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkCommittedFile(string Path, string? Base, string? Hash, bool? Executable = null);

internal sealed record HexLinkCommittedSet(Guid Id, string Folder, string Scope, long Epoch, HexLinkFenceKind Kind,
    IReadOnlyList<HexLinkCommittedFile> Files)
{
    // Payload bytes are retained by the producer's journal until delivery. The receipt binds their expected hashes.
    public JsonObject ToJson()
    {
        var files = new JsonArray();
        foreach (var file in Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            files.Add((JsonNode)new JsonObject { ["path"] = file.Path, ["base"] = file.Base, ["hash"] = file.Hash, ["exec"] = file.Executable });
        return new JsonObject
        {
            ["id"] = Id.ToString("N"), ["folder"] = Folder, ["scope"] = Scope, ["epoch"] = Epoch,
            ["kind"] = Kind.ToString(), ["files"] = files
        };
    }

    public static HexLinkCommittedSet Read(JsonObject value)
    {
        if (!Guid.TryParseExact(HexLinkData.String(value, "id", 33), "N", out Guid id) || id == Guid.Empty ||
            HexLinkData.String(value, "folder", 201) is not { Length: > 0 and <= 200 } folder || !Plain(folder) || folder.Contains('/') ||
            HexLinkData.String(value, "scope", HexLinkFiles.MOST_PATH + 1) is not { Length: <= HexLinkFiles.MOST_PATH } scope || !Plain(scope, allowEmpty: true) ||
            HexLinkData.Long(value, "epoch") is not long epoch || epoch < 1 ||
            !Enum.TryParse(HexLinkData.String(value, "kind", 24), out HexLinkFenceKind kind) || !Enum.IsDefined(kind) ||
            value["files"] is not JsonArray rows || rows.Count is 0 or > HexLinkTree.MOST_FILES)
            throw HexLinkException.Invalid("The committed set is invalid.");

        string prefix = scope.Length == 0 ? folder + "/" : folder + "/" + scope + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<HexLinkCommittedFile>(rows.Count);
        foreach (JsonNode? node in rows)
        {
            if (node is not JsonObject row || HexLinkData.String(row, "path", HexLinkFiles.MOST_PATH + 1) is not { Length: <= HexLinkFiles.MOST_PATH } path ||
                !path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length || !Plain(path) || !seen.Add(path))
                throw HexLinkException.Invalid("A committed path is duplicated or outside its scope.");
            string? from = ReadHash(row, "base"), to = ReadHash(row, "hash");
            if (from is null && to is null)
                throw HexLinkException.Invalid("A committed delete needs an existing base.");
            bool? executable = HexLinkData.Bool(row, "exec");
            if (row["exec"] is not null && executable is null)
                throw HexLinkException.Invalid("A committed file mode is invalid.");
            files.Add(new(path, from, to, executable));
        }
        return new(id, folder, scope, epoch, kind, files.AsReadOnly());
    }

    private static string? ReadHash(JsonObject row, string name) => row[name] is null ? null :
        HexLinkData.String(row, name, 65) is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)
        ? hash.ToLowerInvariant() : throw HexLinkException.Invalid("A committed file hash is invalid.");

    private static bool Plain(string path, bool allowEmpty = false) => (allowEmpty && path.Length == 0) ||
        (!path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
         path.Split('/').All(part => part.Length > 0 && part is not "." and not ".." &&
            !part.Equals(".git", StringComparison.OrdinalIgnoreCase) && !part.EndsWith('.') && !part.EndsWith(' ')));
}

internal readonly record struct HexLinkCommittedAck(Guid Id, string Digest);

/// <summary>Durable outbox and receiver receipts. The caller authenticates peer and epoch and jails paths before projection.</summary>
internal sealed class HexLinkCommittedSets
{
    private readonly Lock gate = new();
    private readonly string directory;

    public HexLinkCommittedSets(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }

    public void Queue(HexLinkCommittedSet set, IEnumerable<string> recipients)
    {
        JsonObject body = Canonical(set);
        string path = PathFor(set.Id, outgoing: true);
        lock (gate)
        {
            if (Load(path) is { } existing)
            {
                RequireMatch(body, existing);
                return;
            }

            long sequence = Directory.EnumerateFiles(directory, "out-*.json")
                .Select(file => HexLinkData.Long(Load(file), "sequence") ?? 0L).DefaultIfEmpty().Max() + 1;
            string[] peers = [.. recipients.Distinct(StringComparer.OrdinalIgnoreCase)];
            Write(path, new JsonObject { ["set"] = body, ["sequence"] = sequence,
                ["pending"] = new JsonArray([.. peers.Select(peer => (JsonNode)JsonValue.Create(peer)!)]) });
        }
    }

    public IReadOnlyList<HexLinkCommittedSet> Pending(string peer)
    {
        lock (gate)
        {
            return [.. Directory.EnumerateFiles(directory, "out-*.json").Select(file => Load(file)!)
                .Where(item => PendingFor(item, peer)).OrderBy(item => HexLinkData.Long(item, "sequence"))
                .Select(item => HexLinkCommittedSet.Read(Body(item)))];
        }
    }

    // Delivery retries after disconnect by calling this with the same authenticated peer ID.
    public async Task Deliver(string peer, Func<HexLinkCommittedSet, CancellationToken, Task<HexLinkCommittedAck>> send,
        CancellationToken cancel)
    {
        foreach (HexLinkCommittedSet set in Pending(peer))
        {
            cancel.ThrowIfCancellationRequested();
            HexLinkCommittedAck ack = await send(set, cancel);
            if (ack != Acknowledge(set))
                throw HexLinkException.Invalid("The receiver acknowledged a different committed set.");
            lock (gate)
            {
                string path = PathFor(ack.Id, outgoing: true);
                JsonObject row = Load(path)!;
                if (row["pending"] is not JsonArray pending)
                    throw new IOException("The committed set outbox is invalid.");
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!string.Equals(pending[i]?.GetValue<string>(), peer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.RemoveAt(i);
                    Write(path, row);
                    break;
                }
            }
        }
    }

    // Call under the exclusive lease, before the first write. A replay of a completed set returns its receipt.
    public HexLinkCommittedAck? PrepareIncoming(HexLinkCommittedSet set)
    {
        JsonObject body = Canonical(set);
        string path = PathFor(set.Id, outgoing: false);
        lock (gate)
        {
            if (Load(path) is { } existing)
            {
                RequireMatch(body, existing);
                return HexLinkData.Bool(existing, "complete") == true ? Acknowledge(body) : null;
            }
            Write(path, new JsonObject { ["set"] = body, ["complete"] = false });
            return null;
        }
    }

    public IReadOnlyList<HexLinkCommittedSet> UnfinishedIncoming()
    {
        lock (gate)
        {
            return [.. Directory.EnumerateFiles(directory, "in-*.json").Select(file => Load(file)!)
                .Where(item => HexLinkData.Bool(item, "complete") != true)
                .Select(item => HexLinkCommittedSet.Read(Body(item)))];
        }
    }

    // Call after projection and durable baseline update, before sending the ACK.
    public HexLinkCommittedAck CompleteIncoming(HexLinkCommittedSet set)
    {
        JsonObject body = Canonical(set);
        string path = PathFor(set.Id, outgoing: false);
        lock (gate)
        {
            JsonObject row = Load(path) ?? throw new InvalidOperationException("The committed set was not prepared.");
            RequireMatch(body, row);
            if (HexLinkData.Bool(row, "complete") != true)
            {
                row["complete"] = true;
                Write(path, row);
            }
            return Acknowledge(body);
        }
    }

    private static JsonObject Canonical(HexLinkCommittedSet set) => HexLinkCommittedSet.Read(set.ToJson()).ToJson();
    private static HexLinkCommittedAck Acknowledge(HexLinkCommittedSet set) => Acknowledge(Canonical(set));
    private static HexLinkCommittedAck Acknowledge(JsonObject body) =>
        new(Guid.ParseExact(body["id"]!.GetValue<string>(), "N"), HexLinkFiles.Hash(Encoding.UTF8.GetBytes(body.ToJsonString())));

    private static bool PendingFor(JsonObject row, string peer) => row["pending"] is JsonArray peers &&
        peers.Any(item => string.Equals(item?.GetValue<string>(), peer, StringComparison.OrdinalIgnoreCase));

    private static void RequireMatch(JsonObject body, JsonObject row)
    {
        if (!JsonNode.DeepEquals(body, Body(row)))
            throw HexLinkException.Invalid("A committed set ID was reused with different contents.");
    }

    private static JsonObject Body(JsonObject row) => row["set"] as JsonObject ?? throw new IOException("Missing committed set.");
    private static JsonObject? Load(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new IOException("Invalid committed set record."); }
        catch (FileNotFoundException) { return null; }
    }
    private static void Write(string path, JsonObject content) => HexLinkFiles.WriteAtomic(path, Encoding.UTF8.GetBytes(content.ToJsonString()));
    private string PathFor(Guid id, bool outgoing) => Path.Combine(directory, $"{(outgoing ? "out" : "in")}-{id:N}.json");
}
