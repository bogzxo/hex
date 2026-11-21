using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    internal bool CanReadFilePacks => session.SupportsBulk && HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.View);
    internal bool CanWriteFilePacks => session.SupportsBulk;

    private readonly Lock filePacks = new();
    private bool readingFilePacks;
    private readonly ConcurrentDictionary<string, FilePackRead> packReads = new(StringComparer.Ordinal);

    private sealed class FilePackRead(IReadOnlyList<string> paths)
    {
        public HashSet<string> Wanted { get; } = new(paths, StringComparer.Ordinal);
        public Dictionary<string, HexLinkFile>? Files { get; set; }
    }

    internal async Task<(Dictionary<string, HexLinkFile> Files, Dictionary<string, HexLinkException> Errors)>
        ReadFilePack(IReadOnlyList<string> paths, CancellationToken cancel)
    {
        lock (filePacks)
        {
            if (!readingFilePacks)
            {
                session.RegisterBulkReceiver("sync/pack/read", HexLinkRights.View, OpenFilePackRead);
                readingFilePacks = true;
            }
        }

        string id = Guid.NewGuid().ToString("N");
        var read = new FilePackRead(paths);
        packReads[id] = read;
        try
        {
            var files = new JsonArray([.. paths.Select(path => (JsonNode?)JsonValue.Create(path))]);
            JsonObject answer = await AskObject("sync/pack/read", new JsonObject { ["id"] = id, ["paths"] = files }, cancel);
            var errors = new Dictionary<string, HexLinkException>(StringComparer.Ordinal);
            Dictionary<string, HexLinkFile> received = read.Files ?? new(StringComparer.Ordinal);
            foreach (HexLinkFilePack.Outcome outcome in OutOfPack(answer))
            {
                if (outcome.Error is null || received.ContainsKey(outcome.Path) || !read.Wanted.Contains(outcome.Path) ||
                    !errors.TryAdd(outcome.Path, outcome.Error))
                    throw HexLinkException.Invalid("The peer sent an invalid file pack outcome.");
            }
            if (received.Count + errors.Count != read.Wanted.Count)
                throw HexLinkException.Invalid("The peer omitted a requested file pack entry.");
            return (received, errors);
        }
        finally
        {
            packReads.TryRemove(id, out _);
        }
    }

    private ValueTask<IHexLinkBulkSink> OpenFilePackRead(long length, JsonObject metadata, CancellationToken cancel)
    {
        string id = HexLinkData.Need(metadata, "id", 33);
        if (!packReads.TryGetValue(id, out FilePackRead? read))
            throw HexLinkException.NotFound("There is no pending request for this Link file pack.");

        return ValueTask.FromResult<IHexLinkBulkSink>(new HexLinkFilePack.Sink(length, (entries, _) =>
        {
            var answer = new Dictionary<string, HexLinkFile>(StringComparer.Ordinal);
            foreach (HexLinkFilePack.Entry entry in entries)
            {
                if (!read.Wanted.Contains(entry.Path))
                    throw HexLinkException.Invalid("The peer sent an unsolicited file pack entry.");
                var decoded = HexLinkFiles.Decode(entry.Bytes);
                answer.Add(entry.Path, new HexLinkFile(entry.Path, entry.Bytes, decoded?.Text, decoded?.Encoding ?? "binary",
                    decoded is { } text ? HexLinkFiles.Eol(text.Text) : string.Empty, HexLinkFiles.Hash(entry.Bytes), 0, entry.Executable));
            }
            lock (read)
            {
                if (read.Files is not null)
                    throw HexLinkException.Invalid("The peer sent multiple packs for one read request.");
                read.Files = answer;
            }
            return Task.FromResult<JsonNode?>(null);
        }));
    }

    internal async Task<Dictionary<string, HexLinkFilePack.Outcome>> WriteFilePack(string folder,
        IReadOnlyList<HexLinkFilePack.Entry> files, CancellationToken cancel)
    {
        JsonNode? reply = await HexLinkFilePack.Send(session, "sync/pack/write", new JsonObject { ["folder"] = folder }, files, cancel);
        if (reply is not JsonObject said)
            throw HexLinkException.Invalid("The peer sent no file pack outcome.");
        var paths = new HashSet<string>(files.Select(entry => entry.Path), StringComparer.Ordinal);
        var outcomes = new Dictionary<string, HexLinkFilePack.Outcome>(StringComparer.Ordinal);
        foreach (HexLinkFilePack.Outcome outcome in OutOfPack(said))
        {
            if (!paths.Contains(outcome.Path) || !outcomes.TryAdd(outcome.Path, outcome))
                throw HexLinkException.Invalid("The peer sent an invalid file pack outcome.");
        }
        if (outcomes.Count != files.Count)
            throw HexLinkException.Invalid("The peer omitted a file pack outcome.");
        return outcomes;
    }

    private static IEnumerable<HexLinkFilePack.Outcome> OutOfPack(JsonObject answer)
    {
        if (answer["outcomes"] is not JsonArray outcomes || outcomes.Count > HexLinkFilePack.MaximumFiles)
            throw HexLinkException.Invalid("The link sent too many or no outcomes for a file pack.");
        foreach (JsonNode? item in outcomes)
        {
            if (item is not JsonObject outcome)
                throw HexLinkException.Invalid("The link sent an invalid file pack outcome.");
            yield return HexLinkFilePack.Outcome.Of(outcome);
        }
    }
}
