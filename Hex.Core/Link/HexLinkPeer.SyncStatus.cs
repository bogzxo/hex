using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkSyncReport(string Folder, HexLinkSyncState State, string? Why, int Sent, int Received,
    int Conflicts, int Skipped, bool PullOnly, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, int Queued,
    string? Detail, IReadOnlyList<HexLinkSyncSkipped> ConflictSamples, IReadOnlyList<HexLinkSyncSkipped> SkipSamples)
{
    internal const int MAX_SAMPLES = 20;

    internal JsonObject Json() => new()
    {
        ["folder"] = Folder, ["state"] = (int)State, ["why"] = Clip(Why, 512),
        ["sent"] = Sent, ["received"] = Received, ["conflicts"] = Conflicts, ["skipped"] = Skipped, ["pull"] = PullOnly,
        ["files_done"] = FilesDone, ["files_total"] = FilesTotal, ["bytes_done"] = BytesDone, ["bytes_total"] = BytesTotal,
        ["queued"] = Queued, ["detail"] = Clip(Detail, 512),
        ["conflict_paths"] = Samples(ConflictSamples), ["skip_paths"] = Samples(SkipSamples)
    };

    internal static HexLinkSyncReport? Of(JsonObject json)
    {
        if (HexLinkData.String(json, "folder", 200) is not { Length: > 0 } folder ||
            HexLinkData.Int(json, "state") is not int state || !Enum.IsDefined((HexLinkSyncState)state) || state == (int)HexLinkSyncState.Off)
            return null;

        int Count(string key) => Math.Max(0, HexLinkData.Int(json, key) ?? 0);
        long Bytes(string key) => Math.Max(0, HexLinkData.Long(json, key) ?? 0);
        return new HexLinkSyncReport(folder, (HexLinkSyncState)state, HexLinkData.String(json, "why", 512),
            Count("sent"), Count("received"), Count("conflicts"), Count("skipped"), HexLinkData.Bool(json, "pull") == true,
            Count("files_done"), Count("files_total"), Bytes("bytes_done"), Bytes("bytes_total"),
            Count("queued"), HexLinkData.String(json, "detail", 512), ReadSamples(json, "conflict_paths"), ReadSamples(json, "skip_paths"));
    }

    private static JsonArray Samples(IEnumerable<HexLinkSyncSkipped> rows) =>
        new([.. rows.Take(MAX_SAMPLES).Select(row => new JsonObject
        {
            ["path"] = Clip(row.Path, HexLinkFiles.MOST_PATH), ["why"] = Clip(row.Why, 512)
        })]);

    private static HexLinkSyncSkipped[] ReadSamples(JsonObject json, string key) =>
        [.. HexLinkData.Objects(json, key, MAX_SAMPLES)
            .Select(row => (Path: HexLinkData.String(row, "path", HexLinkFiles.MOST_PATH), Why: HexLinkData.String(row, "why", 512)))
            .Where(row => row.Path is { Length: > 0 } && row.Why is not null)
            .Select(row => new HexLinkSyncSkipped(row.Path!, row.Why!))];

    private static string? Clip(string? text, int limit) => text?.Length > limit ? text[..limit] : text;
}

internal sealed partial class HexLinkPeer
{
    private HexLinkCoalesce? syncReports;
    private Func<IReadOnlyList<HexLinkSyncReport>>? makeSyncReport;
    private volatile HexLinkSyncReport[] remoteSyncReport = [];

    internal IReadOnlyList<HexLinkSyncReport> RemoteSyncReport =>
        IsClosed || !HexLinkAllowed.Has(Granted, HexLinkRights.View) ? [] : remoteSyncReport;

    internal void QueueSyncReport(Func<IReadOnlyList<HexLinkSyncReport>> snapshot)
    {
        Volatile.Write(ref makeSyncReport, snapshot);
        LazyInitializer.EnsureInitialized(ref syncReports,
            () => new HexLinkCoalesce(TimeSpan.FromMilliseconds(250), SendSyncReport)).Poke();
    }

    internal void RefreshSyncReport() => syncReports?.Poke();

    private async Task SendSyncReport()
    {
        if (IsClosed || !HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.View) ||
            Volatile.Read(ref makeSyncReport) is not { } snapshot)
            return;
        var said = new JsonObject { ["folders"] = new JsonArray([.. snapshot().Take(HexLinkHost.MOST_TREES).Select(row => row.Json())]) };
        if (HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.View))
            await session.TellAsync("sync/status", said, session.Closing);
    }

    private void TakeSyncReport(JsonObject said)
    {
        if (!HexLinkAllowed.Has(Granted, HexLinkRights.View) || said["folders"] is not JsonArray)
            return;
        remoteSyncReport = [.. HexLinkData.Objects(said, "folders", HexLinkHost.MOST_TREES)
            .Select(HexLinkSyncReport.Of).OfType<HexLinkSyncReport>()
            .DistinctBy(row => row.Folder, PlatformPaths.Comparer)];
        Changed?.Invoke();
    }
}
