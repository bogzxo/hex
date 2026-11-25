using Hex.Core.Merge;

namespace Hex.Core;

internal enum HexLinkHoldOutcome { SendMine, TakeTheirs, MergedClean, NeedsEditor, DeleteConflict, Binary }

internal sealed record HexLinkHoldBasis(byte[] Bytes, HexLinkHoldBaseSource Source, bool Deleted = false, bool? Executable = null);

internal sealed record HexLinkHoldPlanEntry(HexLinkHoldSnapshot Snapshot, string FullPath, byte[]? Mine, byte[]? Result,
    HexLinkHoldOutcome Outcome, MergeSession? Session = null, Guid? SessionId = null)
{
    public string Path => Snapshot.Hold.Path;
    public bool RequiresResolution => Session is not null;
}

internal sealed record HexLinkHoldOperation(IReadOnlyList<string> Paths, IReadOnlyList<HexLinkHoldPlanEntry> NeedsMerge,
    HexLinkWorktreeGateResult? Gate = null, string? DiscardedDirectory = null)
{
    public bool Passed => Gate?.Passed != false;
}

/// <summary>Plans and applies changes to a shared folder's local holds.</summary>
internal sealed class HexLinkHoldService(HexLinkFolder folder, HexLinkHoldStore store)
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Lock pendingGate = new();
    private readonly Dictionary<string, PendingMerge> pending = new(PlatformPaths.Comparer);
    // Paths a resolved submit merge sent since the last submit, for the next submit (which finds nothing held) to report
    private readonly List<string> settled = [];

    private sealed class PendingMerge(IReadOnlyList<HexLinkHoldPlanEntry> plan, bool update, bool? check)
    {
        public IReadOnlyList<HexLinkHoldPlanEntry> Plan { get; } = plan;
        public bool Update { get; } = update;
        public bool? Check { get; } = check;
        public Dictionary<string, byte[]?> Results { get; } = new(PlatformPaths.Comparer);
    }

    internal Func<string, CancellationToken, Task<HexLinkHoldBasis?>>? AgreedBasis { get; init; }
    internal Func<HexLinkWorktreeGatePolicy?>? GatePolicy { get; init; }
    internal Func<IReadOnlyList<string>, CancellationToken, Task>? Prepare { get; init; }
    internal Func<string, string, string?, byte[]?, CancellationToken, bool?, Task<string?>> Apply { get; init; } =
        HexLinkFiles.CompareAndReplace;
    internal Func<string, string, string?, Stream?, long, string?, CancellationToken, bool?, Task<string?>> ApplyStream { get; init; } =
        HexLinkFiles.CompareAndReplace;

    public IReadOnlyList<HexLinkHold> List() => store.List();

    public async Task<HexLinkHold> Hold(string path, CancellationToken cancel = default)
    {
        string full = Resolve(path);
        path = store.WireFromFullPath(full) ?? throw HexLinkException.Invalid("Choose a file in the shared folder.");
        await using var held = await HexLinkPublicationFence.ForRoot(full).EnterExclusive(HexLinkFenceKind.Hold, cancel);
        if (store.Get(path) is { } existing)
            return existing;
        HexLinkHoldBasis? basis = AgreedBasis is null ? null : await AgreedBasis(path, held.Stop);
        if (basis is null)
        {
            byte[]? current = ReadWorking(full);
            basis = new(current ?? [], HexLinkHoldBaseSource.Local, current is null, Mode(full));
        }
        return store.Hold(path, basis.Bytes, basis.Source, basis.Deleted, basis.Executable);
    }

    public Task<IReadOnlyList<HexLinkHoldPlanEntry>> Plan(IReadOnlyList<string>? paths = null, CancellationToken cancel = default) =>
        BuildPlan(Entries(paths), cancel);

    public async Task<HexLinkHoldOperation> Submit(IReadOnlyList<string>? paths = null, bool partial = false, bool? check = null,
        CancellationToken cancel = default)
    {
        await operations.WaitAsync(cancel);
        try
        {
            IReadOnlyList<HexLinkHold> entries = Entries(paths);
            if (entries.Count == 0)
            {
                // Nothing is held: what a merge resolved a moment ago was submitted by that, say so rather than an empty list
                lock (pendingGate)
                {
                    string[] done = [.. settled];
                    settled.Clear();
                    return new(done, []);
                }
            }
            lock (pendingGate)
                settled.Clear();
            await PrepareFiles(entries, cancel);
            IReadOnlyList<HexLinkHoldPlanEntry> plan = await BuildPlan(entries, cancel);
            var conflicts = plan.Where(entry => entry.RequiresResolution).ToArray();
            RemovePending(entries);

            if (conflicts.Length > 0 && !partial)
            {
                AddPending(new(plan, update: false, check));
                return new([], conflicts);
            }

            HexLinkHoldPlanEntry[] ready = [.. plan.Where(entry => !entry.RequiresResolution)];
            HexLinkHoldOperation sent = await Install(ready, update: false, check, cancel);
            if (!sent.Passed || conflicts.Length == 0)
                return sent;
            AddPending(new(conflicts, update: false, check));
            return sent with { NeedsMerge = conflicts };
        }
        finally { operations.Release(); }
    }

    public async Task<HexLinkHoldOperation> Update(IReadOnlyList<string>? paths = null, CancellationToken cancel = default)
    {
        await operations.WaitAsync(cancel);
        try
        {
            IReadOnlyList<HexLinkHold> entries = Entries(paths).Where(entry => entry.HasIncoming).ToArray();
            await PrepareFiles(entries, cancel);
            IReadOnlyList<HexLinkHoldPlanEntry> plan = await BuildPlan(entries, cancel);
            RemovePending(entries);
            var updated = new List<string>();
            var conflicts = new List<HexLinkHoldPlanEntry>();
            foreach (HexLinkHoldPlanEntry entry in plan)
            {
                if (entry.RequiresResolution)
                {
                    conflicts.Add(entry);
                    AddPending(new([entry], update: true, check: false));
                }
                else
                    updated.AddRange((await Install([entry], update: true, check: false, cancel)).Paths);
            }
            return new(updated, conflicts);
        }
        finally { operations.Release(); }
    }

    public async Task<HexLinkHoldOperation> Discard(IReadOnlyList<string>? paths = null, CancellationToken cancel = default)
    {
        await operations.WaitAsync(cancel);
        try
        {
            IReadOnlyList<HexLinkHold> entries = Entries(paths);
            await PrepareFiles(entries, cancel);
            RemovePending(entries);
            var plan = new List<HexLinkHoldPlanEntry>();
            string backups = Path.Combine(store.BlobDirectory, "discarded");
            string directory = Path.Combine(backups, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}");
            foreach (HexLinkHold entry in entries)
            {
                cancel.ThrowIfCancellationRequested();
                HexLinkHoldSnapshot snapshot = Snapshot(entry);
                string full = Resolve(entry.Path);
                byte[]? mine = ReadWorking(full);
                byte[]? restored = entry.HasIncoming ? snapshot.IncomingBytes : BaseOf(snapshot);
                plan.Add(new(snapshot, full, mine, restored, HexLinkHoldOutcome.TakeTheirs));
                if (mine is null)
                    continue;
                string backup = Path.Combine(directory, entry.Path[(folder.Name.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                HexLinkFiles.WriteAtomic(backup, mine);
            }

            HexLinkHoldOperation discarded = await Install(plan, update: false, check: false, cancel);
            RetireDiscarded(backups);
            return discarded with { DiscardedDirectory = Directory.Exists(directory) ? directory : null };
        }
        finally { operations.Release(); }
    }

    public Task<HexLinkHoldOperation> CompleteMerge(string path, byte[]? bytes, CancellationToken cancel = default) =>
        Complete(path, sessionId: null, bytes, cancel);

    /// <summary>The files an update or submit left waiting for a decision, each with its merge, for an agent that has no merge editor to read.</summary>
    public IReadOnlyList<HexLinkHoldPlanEntry> PendingConflicts(IReadOnlyList<string>? paths = null)
    {
        lock (pendingGate)
            return [.. pending.Where(pair => (paths is null || paths.Contains(pair.Key, PlatformPaths.Comparer))
                    && !pair.Value.Results.ContainsKey(pair.Key))
                .Select(pair => pair.Value.Plan.FirstOrDefault(entry => entry.RequiresResolution && PlatformPaths.Same(entry.Path, pair.Key)))
                .OfType<HexLinkHoldPlanEntry>().OrderBy(entry => entry.Path, PlatformPaths.Comparer)];
    }

    /// <summary>
    /// A waiting conflict settled the way an editor's choices would: mine, theirs (a side that was deleted deletes the file) or the
    /// whole merged text supplied. What's left to settle comes back in NeedsMerge, the files written in Paths.
    /// </summary>
    public Task<HexLinkHoldOperation> ResolveMerge(string path, string resolution, string? text, CancellationToken cancel = default)
    {
        HexLinkHoldPlanEntry entry = PendingConflicts([path]).FirstOrDefault()
            ?? throw HexLinkException.NotFound($"There's no pending merge for {path}, update or submit first.");
        byte[]? bytes;
        switch (resolution)
        {
            case "mine":
                bytes = entry.Mine;
                break;
            case "theirs":
                bytes = entry.Snapshot.Hold.IncomingDeleted ? null : entry.Snapshot.IncomingBytes;
                break;
            case "merged":
                if (text is null)
                    throw HexLinkException.Invalid("Say the merged text of the whole file.");
                if (entry.Session?.Model is not { IsBinary: false } model)
                    throw HexLinkException.Invalid("This file can't be merged as text, keep mine or theirs.");
                bytes = model.EncodeResult(text);
                break;
            default:
                throw HexLinkException.Invalid("The resolution is mine, theirs or merged.");
        }

        return CompleteMerge(entry.Path, bytes, cancel);
    }

    private async Task<HexLinkHoldOperation> Complete(string path, Guid? sessionId, byte[]? bytes, CancellationToken cancel,
        bool? checkOverride = null)
    {
        await operations.WaitAsync(cancel);
        try
        {
            PendingMerge batch;
            lock (pendingGate)
            {
                if (!pending.TryGetValue(path, out PendingMerge? active)
                    || !active.Plan.Any(entry => entry.RequiresResolution && PlatformPaths.Same(entry.Path, path)
                        && (sessionId is null || entry.SessionId == sessionId)))
                    throw HexLinkException.NotFound($"There's no pending merge for {path}.");
                batch = active;
                batch.Results[path] = bytes;
                HexLinkHoldPlanEntry[] remaining = [.. batch.Plan.Where(entry => entry.RequiresResolution && !batch.Results.ContainsKey(entry.Path))];
                if (remaining.Length > 0)
                    return new([], remaining);
            }

            try
            {
                HexLinkHoldPlanEntry[] ready = [.. batch.Plan.Select(entry => entry.RequiresResolution
                    ? entry with { Result = batch.Results[entry.Path], Session = null }
                    : entry)];
                HexLinkHoldOperation result = await Install(ready, batch.Update, checkOverride ?? batch.Check, cancel);
                if (result.Passed)
                {
                    Forget(batch);
                    if (!batch.Update)
                        lock (pendingGate)
                            foreach (string settledPath in result.Paths)
                                if (!settled.Contains(settledPath, PlatformPaths.Comparer))
                                    settled.Add(settledPath);
                }
                return result;
            }
            catch
            {
                Forget(batch);
                throw;
            }
        }
        finally { operations.Release(); }
    }

    private async Task<HexLinkHoldOperation> Install(IReadOnlyList<HexLinkHoldPlanEntry> plan, bool update, bool? check,
        CancellationToken cancel)
    {
        if (plan.Count == 0)
            return new([], []);
        var written = new List<HexLinkHoldPlanEntry>();
        bool committed = false;
        try
        {
            foreach (HexLinkHoldPlanEntry entry in plan)
            {
                cancel.ThrowIfCancellationRequested();
                EnsureCurrent(entry, entry.Mine);
                if (!Equal(entry.Mine, entry.Result))
                {
                    written.Add(entry);
                    await Write(entry, entry.Mine, entry.Result, cancel);
                }
            }

            HexLinkWorktreeGateOptions? options = check == false ? null : GatePolicy?.Invoke()?.Options ?? folder.WorktreeGate?.Options;
            if (check == true && options is null)
                options = new();
            HexLinkWorktreeGateResult? gate = null;
            if (options is not null)
            {
                string log = Path.Combine(store.BlobDirectory, "submit-gate-" + Guid.NewGuid().ToString("N") + ".log");
                gate = await HexLinkWorktreeGate.Run(folder.Path, log, options, cancel);
                if (!gate.Passed)
                    return new([], [], gate);
            }

            await using var exclusive = await HexLinkPublicationFence.ForRoot(folder.Path).EnterExclusive(HexLinkFenceKind.Hold, cancel);
            foreach (HexLinkHoldPlanEntry entry in plan)
                EnsureCurrent(entry, entry.Result);
            committed = update
                ? Rebase(plan.Single())
                : store.Release([.. plan.Select(entry => entry.Snapshot.Hold)],
                    plan.ToDictionary(entry => entry.Path, entry => Hash(entry.Result), PlatformPaths.Comparer));
            if (!committed)
                throw ChangedDuringOperation(plan[0].Path);
            return new([.. plan.Select(entry => entry.Path)], [], gate);
        }
        finally
        {
            if (!committed)
            {
                foreach (HexLinkHoldPlanEntry entry in written.AsEnumerable().Reverse())
                {
                    // A user save during the check wins over rollback.
                    if (Equal(ReadWorking(entry.FullPath), entry.Result))
                        await Write(entry, entry.Result, entry.Mine, CancellationToken.None);
                }
            }
        }
    }

    private bool Rebase(HexLinkHoldPlanEntry entry) => store.Rebase(entry.Path, entry.Snapshot.IncomingBytes ?? [],
        entry.Snapshot.Hold.IncomingDeleted, entry.Snapshot.Hold);

    private async Task Write(HexLinkHoldPlanEntry entry, byte[]? before, byte[]? after, CancellationToken cancel)
    {
        string? expected = Hash(before), result = Hash(after);
        string? hash;
        if (after is { Length: > HexLinkFiles.MOST_READ })
        {
            using var stream = new MemoryStream(after, writable: false);
            hash = await ApplyStream(entry.FullPath, entry.Path, expected, stream, after.LongLength, result, cancel, null);
        }
        else
            hash = await Apply(entry.FullPath, entry.Path, expected, after, cancel, null);
        if (hash != result)
            throw ChangedDuringOperation(entry.Path);
    }

    private void EnsureCurrent(HexLinkHoldPlanEntry entry, byte[]? expected)
    {
        if (!store.Matches(entry.Snapshot.Hold) || !PlatformPaths.Same(entry.FullPath, Resolve(entry.Path))
            || !Equal(ReadWorking(entry.FullPath), expected))
            throw ChangedDuringOperation(entry.Path);
    }

    private async Task<IReadOnlyList<HexLinkHoldPlanEntry>> BuildPlan(IReadOnlyList<HexLinkHold> entries, CancellationToken cancel)
    {
        var plan = new List<HexLinkHoldPlanEntry>(entries.Count);
        foreach (HexLinkHold entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            HexLinkHoldSnapshot snapshot = Snapshot(entry);
            string full = Resolve(entry.Path);
            byte[]? mine = ReadWorking(full), basis = BaseOf(snapshot), theirs = snapshot.IncomingBytes;
            if (!entry.HasIncoming)
                plan.Add(new(snapshot, full, mine, mine, HexLinkHoldOutcome.SendMine));
            else if (Equal(mine, basis))
                plan.Add(new(snapshot, full, mine, theirs, HexLinkHoldOutcome.TakeTheirs));
            else if (Equal(theirs, basis) || Equal(mine, theirs))
                plan.Add(new(snapshot, full, mine, mine, HexLinkHoldOutcome.MergedClean));
            else if (mine is null || theirs is null)
                plan.Add(Conflict(HexLinkHoldOutcome.DeleteConflict, MergeModel.Binary(basis ?? [], mine ?? [], theirs ?? [])));
            else
            {
                // With no base, merging against an empty one would make both whole files added, so they are compared as they are
                MergeModel model = basis is null ? MergeModel.FromTwo(mine, theirs) : await MergeModel.FromThree(basis, mine, theirs, cancel);
                if (model.IsBinary)
                    plan.Add(Conflict(HexLinkHoldOutcome.Binary, model));
                else if (model.ConflictCount > 0)
                    plan.Add(Conflict(HexLinkHoldOutcome.NeedsEditor, model));
                else
                    plan.Add(new(snapshot, full, mine, model.ToBytes(), HexLinkHoldOutcome.MergedClean));
            }

            HexLinkHoldPlanEntry Conflict(HexLinkHoldOutcome outcome, MergeModel model)
            {
                string title = Path.GetFileName(full);
                Guid sessionId = Guid.NewGuid();
                async Task Finish(byte[]? result)
                {
                    HexLinkHoldOperation completed = await Complete(entry.Path, sessionId, result, CancellationToken.None);
                    if (completed.Gate is { Passed: false } failed)
                        throw new MergeCheckFailed(failed.Output, async () =>
                        {
                            await Complete(entry.Path, sessionId, result, CancellationToken.None, checkOverride: false);
                        });
                }
                var session = new MergeSession(title, "Mine", "Theirs", "Base", model, bytes => Finish(bytes),
                    () => Cancel(entry.Path, sessionId), full)
                {
                    SideChoices = model.IsBinary ? new(mine is null, theirs is null, side => Finish(side == MergeSide.Mine ? mine : theirs)) : null,
                    MineModified = mine is null ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(full)),
                    TheirsModified = entry.IncomingAt,
                };
                return new(snapshot, full, mine, null, outcome, session, sessionId);
            }
        }
        return plan;
    }

    private IReadOnlyList<HexLinkHold> Entries(IReadOnlyList<string>? paths) => paths is null
        ? store.List()
        : [.. paths.Select(path => store.Get(path) ?? throw HexLinkException.NotFound($"{path} is not held."))
            .DistinctBy(entry => entry.Path, PlatformPaths.Comparer)];

    private HexLinkHoldSnapshot Snapshot(HexLinkHold expected)
    {
        if (store.Read(expected.Path) is not { } snapshot || !store.Matches(expected))
            throw ChangedDuringOperation(expected.Path);
        return snapshot;
    }

    private Task PrepareFiles(IReadOnlyList<HexLinkHold> entries, CancellationToken cancel) =>
        entries.Count == 0 || Prepare is null ? Task.CompletedTask : Prepare([.. entries.Select(entry => entry.Path)], cancel);

    private void AddPending(PendingMerge batch)
    {
        lock (pendingGate)
            foreach (HexLinkHoldPlanEntry entry in batch.Plan)
                pending[entry.Path] = batch;
    }

    private void RemovePending(IReadOnlyList<HexLinkHold> entries)
    {
        lock (pendingGate)
            foreach (HexLinkHold entry in entries)
                if (pending.TryGetValue(entry.Path, out PendingMerge? batch))
                    Forget(batch);
    }

    private void Cancel(string path, Guid sessionId)
    {
        lock (pendingGate)
            if (pending.TryGetValue(path, out PendingMerge? batch)
                && batch.Plan.Any(entry => entry.Path == path && entry.SessionId == sessionId))
                Forget(batch);
    }

    private void Forget(PendingMerge batch)
    {
        lock (pendingGate)
            foreach (HexLinkHoldPlanEntry entry in batch.Plan)
                if (pending.GetValueOrDefault(entry.Path) == batch)
                    pending.Remove(entry.Path);
    }

    private static void RetireDiscarded(string backups)
    {
        if (!Directory.Exists(backups))
            return;
        foreach (string path in Directory.EnumerateDirectories(backups))
        {
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.CreationTimeUtc >= DateTime.UtcNow.AddDays(-7))
                continue;
            try { info.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string Resolve(string wire) => HexLinkFiles.Resolve([folder], wire);

    private byte[]? ReadWorking(string full)
    {
        if (HexLinkTree.RegularFile(full, folder.Path) is not { } info)
            return null;
        if (info.Length > HexLinkFileTransfer.MaximumLength)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{full} is too large to hold.");
        return HexLinkFiles.ReadShared(full);
    }

    private static byte[]? BaseOf(HexLinkHoldSnapshot snapshot) => snapshot.Hold.BaseDeleted ? null : snapshot.BaseBytes;

    private static string? Hash(byte[]? bytes) => bytes is null ? null : HexLinkFiles.Hash(bytes);

    private static bool Equal(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static bool? Mode(string full) => !OperatingSystem.IsWindows() && File.Exists(full)
        ? File.GetUnixFileMode(full).HasFlag(UnixFileMode.UserExecute) : null;

    private static HexLinkException ChangedDuringOperation(string path) =>
        new(HexLinkErrors.CONFLICT, $"The working file or incoming version of {path} changed. Plan it again.");
}
