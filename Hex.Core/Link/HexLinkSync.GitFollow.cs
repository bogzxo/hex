namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    internal Func<bool>? GitFollowEnabled { get; init; }
    internal string? GitFollowData { get; init; }
    internal Action<string>? GitFollowAudited { get; init; }
    internal string? GitFollowIssue => Volatile.Read(ref followIssue);
    private string? followIssue;

    private sealed record FollowRepository(string Path, string Root, HexLinkGitFollowScan Scan)
    {
        public int Active;
        public TaskCompletionSource? Paused;
        // This side's tip as the last full check left it, what the quick local poll compares with
        public volatile HexLinkHeadTip? Seen;
    }

    private sealed class FollowRun(HexLinkSync sync, FollowRepository repo, bool pause) : IDisposable
    {
        public void Dispose()
        {
            lock (sync.followAdmission)
            {
                if (pause)
                    repo.Paused = null;
                else if (--repo.Active == 0)
                    repo.Paused?.TrySetResult();
            }
        }
    }

    private readonly Lock followAdmission = new();
    private sealed record FollowAgreement(HexLinkHeadTip Local, HexLinkHeadTip Remote);
    private readonly SemaphoreSlim followLock = new(1, 1), followHint = new(0, 1);
    private volatile FollowRepository[] followRepositories = [];
    private volatile Dictionary<string, FollowAgreement> followReady = new(StringComparer.Ordinal);
    private volatile Dictionary<string, string> followBase = new(StringComparer.Ordinal);
    private bool followUnsupported;

    private async Task<bool> StartGitFollow(HexLinkManifest mine, HexLinkManifest theirs, CancellationToken cancel)
    {
        if (GitFollowEnabled is null || GitFollowData is null
            || (folder.Authority is { } pinned && theirs.Authority != pinned
                && (theirs.Authority is not null || pinned.DeviceId != Device.Id)))
            return false;
        followRepositories = [.. RepositoryPaths(mine, theirs)
            .Where(path => HeadAt(mine, path) is not null && HeadAt(theirs, path) is not null)
            .Select(path => (Path: path, Root: LocalRepository(path)))
            .Where(repo => repo.Root is not null)
            .Select(repo => new FollowRepository(repo.Path, repo.Root!, HexLinkGitFollowScan.For(repo.Root!)))
            .OrderByDescending(repo => repo.Root.Length)];
        if (followRepositories.Length == 0 || GitFollowEnabled() != true)
            return false;
        await CheckGitFollow(cancel);
        return !followUnsupported;
    }

    // A local HEAD move is seen from its files within this, the other side's by its hint or the full check
    private static readonly TimeSpan LocalTipPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>How often both sides' tips are compared without a hint or a local move, 2 seconds.</summary>
    internal TimeSpan GitFollowFullCheck { get; init; } = TimeSpan.FromSeconds(2);

    private async Task PollGitFollow(CancellationToken cancel)
    {
        peer.GitFollowHint += WakeGitFollow;
        List<FileSystemWatcher> watchers = [];
        watched = false;
        try
        {
            long every = (long)GitFollowFullCheck.TotalMilliseconds;
            long fullAt = Environment.TickCount64 + every;
            while (!followUnsupported)
            {
                // A local move is told by the watchers on the git directories, so the files are only looked at by the clock as a
                // slow fallback (and by the quick poll where no watcher could be made)
                if (watchers.Count == 0 && !watched)
                    watchers = WatchGitDirectories();
                bool hinted = await followHint.WaitAsync(watchers.Count > 0 ? WatchedFallback : LocalTipPoll, cancel);
                if (Interlocked.Exchange(ref localTipEvent, 0) != 0)
                {
                    // Bursts of ref writes (a checkout, a fetch) are one look
                    await Task.Delay(100, cancel);
                    Interlocked.Exchange(ref localTipEvent, 0);
                    hinted = false;
                    if (!LocalTipMoved(cancel) && Environment.TickCount64 < fullAt)
                        continue;
                }
                // The full check asks the other side for its tips. Between them only this side's files are read.
                else if (!hinted && Environment.TickCount64 < fullAt && !LocalTipMoved(cancel))
                    continue;
                // With the watchers on this side and the other side's hint for its own moves, the comparing is a slow safety net
                fullAt = Environment.TickCount64 + (watchers.Count > 0 ? Math.Max(every, (long)WatchedFallback.TotalMilliseconds) : every);
                try { await CheckGitFollow(cancel); }
                catch (Exception error) when (GitFollowRecoverable(error))
                {
                    Volatile.Write(ref followIssue, error.Message);
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally
        {
            peer.GitFollowHint -= WakeGitFollow;
            foreach (FileSystemWatcher each in watchers)
                each.Dispose();
        }
    }

    // How often this side's tips are read when watchers are on the git directories and say nothing
    private static readonly TimeSpan WatchedFallback = TimeSpan.FromSeconds(10);
    private int localTipEvent;
    private bool watched;

    // A watcher on each followed repository's git directories (HEAD, the refs, packed-refs, a lock appearing) that wakes the
    // loop. Only events on those files count: an object written by a fetch or commit says nothing about a tip.
    private List<FileSystemWatcher> WatchGitDirectories()
    {
        List<FileSystemWatcher> made = [];
        bool located = true;
        foreach (FollowRepository repo in followRepositories)
        {
            string[] directories = repo.Scan.GitDirectories();
            if (directories.Length == 0)
            {
                located = false;
                continue;
            }

            foreach (string directory in directories)
            {
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 16 * 1024
                    };
                    FileSystemEventHandler moved = (_, e) => GitFollowFileMoved(e.Name);
                    watcher.Changed += moved;
                    watcher.Created += moved;
                    watcher.Deleted += moved;
                    watcher.Renamed += (_, e) => GitFollowFileMoved(e.Name);
                    watcher.Error += (_, _) => { Interlocked.Exchange(ref localTipEvent, 1); WakeGitFollow(); };
                    watcher.EnableRaisingEvents = true;
                    made.Add(watcher);
                }
                catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // No watcher for it (too many, or the directory is gone): the quick poll stays on
                    foreach (FileSystemWatcher each in made)
                        each.Dispose();
                    watched = true;
                    return [];
                }
            }
        }

        // Directories not found yet (the first read hasn't run) are tried again next loop
        watched = located;
        return made;
    }

    private void GitFollowFileMoved(string? name)
    {
        if (name is null || !(name.Contains("HEAD", StringComparison.Ordinal) || name.StartsWith("refs", StringComparison.Ordinal)
            || name.Contains("packed-refs", StringComparison.Ordinal) || name.Contains("index.lock", StringComparison.Ordinal)
            || name.Contains("reftable", StringComparison.Ordinal) || name.Contains("hexlink", StringComparison.OrdinalIgnoreCase)))
            return;
        Interlocked.Exchange(ref localTipEvent, 1);
        WakeGitFollow();
    }

    // A followed repository's HEAD here moved since it was last seen, read from HEAD and its loose ref alone. Each move
    // is taken once: a pair left apart (diverged, view only, a failed move) goes back to the full check's pace rather
    // than a check every poll pausing the repository's file sync.
    private bool LocalTipMoved(CancellationToken cancel)
    {
        bool moved = false;
        foreach (FollowRepository repo in followRepositories)
        {
            try
            {
                if (repo.Scan.Read(cancel) is not { } local || local == repo.Seen)
                    continue;
                moved |= repo.Seen is not null;
                repo.Seen = local;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The full check reports an unreadable repository.
            }
        }
        return moved;
    }

    private void WakeGitFollow()
    {
        if (followHint.CurrentCount != 0)
            return;
        try { followHint.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task CheckGitFollow(CancellationToken cancel)
    {
        if (followUnsupported || followRepositories.Length == 0)
            return;
        await followLock.WaitAsync(cancel);
        try
        {
            if (GitFollowEnabled?.Invoke() != true)
            {
                if (followReady.Count != 0)
                {
                    await peer.StopGitFollow(folder.Name, cancel);
                    followReady = new(StringComparer.Ordinal);
                    followBase = new(StringComparer.Ordinal);
                }
                if (Interlocked.Exchange(ref followIssue, null) is not null)
                    Notify();
                return;
            }

            string[] paths = [.. followRepositories.Select(repo => repo.Path)];
            Dictionary<string, HexLinkHeadTip> there;
            Dictionary<string, string> remoteIssues;
            try { (there, remoteIssues) = await peer.GitFollowTips(folder.Name, paths, cancel); }
            catch (HexLinkException error) when (followReady.Count == 0 && error.Code is HexLinkErrors.METHOD_NOT_FOUND or HexLinkErrors.INVALID_PARAMS)
            {
                followUnsupported = true;
                return;
            }

            var state = new HexLinkGitFollowState(GitFollowData!, folder, Device.Id);
            var agreements = new Dictionary<string, FollowAgreement>(followReady, StringComparer.Ordinal);
            var baselines = new Dictionary<string, string>(followBase, StringComparer.Ordinal);
            var issues = new List<string>();
            foreach (FollowRepository repo in followRepositories)
            {
                cancel.ThrowIfCancellationRequested();
                string path = repo.Path.Length == 0 ? folder.Name : repo.Path;
                if (!there.TryGetValue(repo.Path, out HexLinkHeadTip? remote))
                {
                    if (remoteIssues.TryGetValue(repo.Path, out string? why))
                        issues.Add($"{path} on {Device.Name}. {why}");
                    continue;
                }
                try
                {
                    HexLinkHeadTip? local = await Task.Run(() => repo.Scan.ReadStable(cancel), cancel);
                    if (local is null)
                        continue;
                    var observed = new FollowAgreement(local, remote);
                    if (agreements.TryGetValue(repo.Path, out FollowAgreement? agreed) && agreed == observed && local == remote)
                    {
                        repo.Seen = local;
                        continue;
                    }

                    using var pause = await PauseGitFollow(repo, cancel);
                    if (await Task.Run(() => repo.Scan.Read(cancel), cancel) != local)
                        continue;
                    HexLinkHeadTip? common = state.Agreed(repo.Path);
                    if (local != remote)
                    {
                        (observed, string? issue) = await MoveGitFollow(repo, path, observed, common, cancel);
                        if (issue is not null)
                            issues.Add(issue);
                        if (common is not null)
                            baselines[repo.Path] = common.Head;
                        else if (issue is null)
                            baselines[repo.Path] = local == observed.Local ? remote.Head : local.Head;
                    }

                    bool same = observed.Local == observed.Remote;
                    if (agreed != observed)
                    {
                        await peer.ConfirmGitFollow(folder.Name, repo.Path, observed.Remote, same, cancel);
                        if (agreed is not null)
                            await peer.Flush(folder.Name, cancel);
                    }
                    if (same && common != observed.Local)
                        state.Agree(repo.Path, observed.Local);
                    agreements[repo.Path] = observed;
                    repo.Seen = observed.Local;
                    if (repo.Path.Length == 0)
                    {
                        LocalHead = observed.Local.Head;
                        RemoteHead = observed.Remote.Head;
                    }
                }
                catch (Exception error) when (GitFollowRecoverable(error))
                {
                    issues.Add(path + ": " + error.Message);
                }
            }

            followBase = baselines;
            followReady = agreements;
            string? issueSummary = issues.Count == 0 ? null : string.Join(" ", issues);
            if (GitFollowIssue != issueSummary)
            {
                Volatile.Write(ref followIssue, issueSummary);
                Notify();
            }
        }
        finally { followLock.Release(); }
    }

    private async Task<(FollowAgreement Agreement, string? Issue)> MoveGitFollow(FollowRepository repo, string display,
        FollowAgreement observed, HexLinkHeadTip? common, CancellationToken cancel)
    {
        var (here, there) = observed;
        bool fromRemote;
        bool fastForwardOnly = common is null || (common != here && common != there);
        if (!fastForwardOnly)
            fromRemote = common == here;
        else if (here.Head == there.Head)
            fromRemote = folder.FolderAuthorityId is null || folder.FolderAuthorityId == Device.Id;
        else if (!CanReceive)
        {
            fromRemote = false;
            if (!await Task.Run(() => HexLinkGitFollow.Ancestor(repo.Root, there.Head, here.Head, cancel), cancel))
                return (observed, $"{display}: replica Git change {VIEW_ONLY_REPLICA}.");
        }
        else
        {
            string wire = repo.Path.Length == 0 ? folder.Name : folder.Name + "/" + repo.Path;
            using var allowed = ReceiveLease(cancel);
            await HexLinkGitFollow.Fetch(peer, wire, repo.Root, here, there, allowed?.Token ?? cancel);
            fromRemote = await Task.Run(() => HexLinkGitFollow.Ancestor(repo.Root, here.Head, there.Head, cancel), cancel);
            if (!fromRemote && !await Task.Run(() => HexLinkGitFollow.Ancestor(repo.Root, there.Head, here.Head, cancel), cancel))
                return (observed, $"{display}: {Device.Name} has commits this device lacks, and this device has commits {Device.Name} lacks. Align to host when ready.");
        }

        HexLinkHeadMove move;
        HexLinkHeadTip target = fromRemote ? there : here;
        if (fromRemote)
        {
            if (!CanReceive)
                return (observed, $"{display}: replica Git change {VIEW_ONLY_REPLICA}.");
            string wire = repo.Path.Length == 0 ? folder.Name : folder.Name + "/" + repo.Path;
            using var allowed = ReceiveLease(cancel);
            await HexLinkGitFollow.Fetch(peer, wire, repo.Root, here, there, allowed?.Token ?? cancel);
            var heldChanges = await HexLinkHoldGit.Changes(Holds, repo.Root, here.Head, there.Head, allowed?.Token ?? cancel);
            move = await HexLinkGitFollow.Move(repo.Root, here, there, fastForwardOnly ? null : common, allowed?.Token ?? cancel);
            if (move.Why is null)
                HexLinkHoldGit.Take(Holds, heldChanges, Device.Name);
            if (move.Backup is { } backup)
                GitFollowAudited?.Invoke($"HEAD follow {display} from {here.Head} to {there.Head}; backup {backup}" + (move.Rewrite ? " (rewrite)" : ""));
        }
        else
        {
            if (Holds?.InRepository(repo.Root).Count is > 0 and var held)
                return (observed, $"{display}: {HexLinkHoldGit.HeldCommit(held)}.");
            if (BoundPullMirror || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
                return (observed, $"{display}: local Git change not sent: view only.");
            try { move = await peer.AdoptGitFollow(folder.Name, repo.Path, there, here, cancel); }
            catch (HexLinkException error) when (error.Code == HexLinkErrors.NOT_ALLOWED)
            {
                return (observed, display + ": " + error.Message);
            }
        }

        return move.Why is null ? (new(target, target), null)
            : (observed, $"{display}: {(fromRemote ? "This device" : Device.Name)}: {move.Why} Align to host when ready.");
    }

    private FollowRepository? FollowRepositoryFor(string wire)
    {
        if (wire.Length <= folder.Name.Length + 1)
            return null;
        foreach (FollowRepository entry in followRepositories)
        {
            ReadOnlySpan<char> relative = wire.AsSpan(folder.Name.Length + 1);
            if (entry.Path.Length == 0 || (relative.Length > entry.Path.Length && relative[entry.Path.Length] == '/'
                && relative.StartsWith(entry.Path, StringComparison.Ordinal)))
                return entry;
        }
        return null;
    }

    private bool HasFollowBase(string wire) =>
        followBase.Count != 0 && FollowRepositoryFor(wire) is { } repo && followBase.ContainsKey(repo.Path);

    private FollowRun? AdmitGitFollowPath(string wire)
    {
        if (followUnsupported || GitFollowEnabled?.Invoke() != true || FollowRepositoryFor(wire) is not { } repo)
            return null;
        lock (followAdmission)
        {
            if (repo.Paused is not null)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "Git is moving this repository. Link will retry.");
            repo.Active++;
            return new(this, repo, pause: false);
        }
    }

    private async Task<FollowRun> PauseGitFollow(FollowRepository repo, CancellationToken cancel)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (followAdmission)
        {
            repo.Paused = drained;
            if (repo.Active == 0)
                drained.TrySetResult();
        }
        var pause = new FollowRun(this, repo, pause: true);
        try
        {
            await drained.Task.WaitAsync(cancel);
            return pause;
        }
        catch
        {
            pause.Dispose();
            throw;
        }
    }

    private async Task EnsureLocalGitFollowFile(string full, CancellationToken cancel)
    {
        if (followUnsupported || GitFollowEnabled?.Invoke() != true)
            return;
        FollowRepository? repo = followRepositories.FirstOrDefault(each => PlatformPaths.IsUnder(full, each.Root));
        if (repo is not null && (!followReady.TryGetValue(repo.Path, out FollowAgreement? accepted)
            || await Task.Run(() => repo.Scan.Read(cancel), cancel) != accepted.Local))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "Git is moving this repository. Link will compare its new branch and commit.");
    }

    private static bool GitFollowRecoverable(Exception error) =>
        error is HexLinkException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException;
}
