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

    private async Task PollGitFollow(CancellationToken cancel)
    {
        peer.GitFollowHint += WakeGitFollow;
        try
        {
            while (!followUnsupported)
            {
                await followHint.WaitAsync(TimeSpan.FromSeconds(2), cancel);
                try { await CheckGitFollow(cancel); }
                catch (Exception error) when (GitFollowRecoverable(error))
                {
                    Volatile.Write(ref followIssue, error.Message);
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { peer.GitFollowHint -= WakeGitFollow; }
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
                        continue;

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
            move = await HexLinkGitFollow.Move(repo.Root, here, there, fastForwardOnly ? null : common, allowed?.Token ?? cancel);
            if (move.Backup is { } backup)
                GitFollowAudited?.Invoke($"HEAD follow {display} from {here.Head} to {there.Head}; backup {backup}" + (move.Rewrite ? " (rewrite)" : ""));
        }
        else
        {
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
