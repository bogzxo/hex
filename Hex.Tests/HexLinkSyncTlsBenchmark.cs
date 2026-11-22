using System.Diagnostics;

using Hex.Core;
using Hex.Core.Git;

using Horizon.Core.Platform;

using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>Opt-in working-tree benchmark. The two fixture workspaces are private clones, linked over pinned loopback TLS.</summary>
public sealed class HexLinkSyncTlsBenchmark(ITestOutputHelper output)
{
    private static readonly HexLinkRights Rights = HexLinkRights.View | HexLinkRights.Edit;
    private static readonly string[] Candidates = [".", "hex", "horizon", "dusk", "Fighter2D", "hidl", "hex/hex-extensions", "horizon/Horizon.HIDL"];
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);
    private readonly string? progress = Environment.GetEnvironmentVariable("HEX_LINK_SYNC_BENCH_LOG");
    private readonly bool profile = Environment.GetEnvironmentVariable("HEX_LINK_SYNC_BENCH_PROFILE") == "1";

    [Fact]
    public async Task Benchmark_working_tree_over_loopback_TLS()
    {
        string? source = Environment.GetEnvironmentVariable("HEX_LINK_SYNC_BENCH_ROOT");
        if (string.IsNullOrWhiteSpace(source))
            return;

        string mode = Environment.GetEnvironmentVariable("HEX_LINK_SYNC_BENCH_MODE") ?? "both";
        List<(string Relative, string Full, string Commit)> repositories = Repositories(Path.GetFullPath(source));
        Assert.NotEmpty(repositories);
        Write($"Workspace repositories: {string.Join(", ", repositories.Select(repo => repo.Relative + "@" + repo.Commit))}");
        if (mode is "bulk" or "both")
            await Run(repositories, HexLinkBulkSupport.Zstandard);
        if (mode is "legacy" or "both")
            await Run(repositories, HexLinkBulkSupport.None);
    }

    private void Write(string message)
    {
        output.WriteLine(message);
        if (!string.IsNullOrEmpty(progress))
            File.AppendAllText(progress, message + Environment.NewLine);
    }

    private void BeginProfile()
    {
        if (profile)
            HexLinkSyncTiming.Start();
    }

    private void EndProfile(string phase)
    {
        if (!profile)
            return;
        Write($"{phase} timings (aggregate wall time across workers and endpoints):");
        foreach (string line in HexLinkSyncTiming.Finish())
            Write(line);
    }

    private async Task Run(List<(string Relative, string Full, string Commit)> repositories, HexLinkBulkSupport capability)
    {
        using var a = new HexLinkTestMachine("Benchmark A");
        using var b = new HexLinkTestMachine("Benchmark B");
        using var here = new LinkServices(a.Device);
        using var there = new LinkServices(b.Device);
        here.TreeCacheDirectory = Path.Combine(here.Root, "states");
        there.TreeCacheDirectory = Path.Combine(there.Root, "states");
        using var cancel = new CancellationTokenSource(Limit);
        CancellationToken stop = cancel.Token;
        foreach (var repo in repositories)
            Clone(repo.Full, repo.Commit, Path.Combine(here.Work, repo.Relative), checkout: true);

        const string branchFiles = "hexlink-benchmark-branch";
        string folder = Path.Combine(here.Work, branchFiles);
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 500; i++)
            File.WriteAllText(Path.Combine(folder, $"{i:0000}.txt"), $"initial {i}\n");
        Git(here.Work, "add", "--force", branchFiles);
        Commit(here.Work, "Baseline benchmark files");
        string baseline = Git(here.Work, "rev-parse", "HEAD").Trim();

        foreach (var repo in repositories)
        {
            string peerSource = Path.Combine(here.Work, repo.Relative);
            string commit = repo.Relative == "." ? baseline : repo.Commit;
            Clone(peerSource, commit, Path.Combine(there.Work, repo.Relative), checkout: false);
        }

        for (int i = 0; i < 500; i++)
            File.WriteAllText(Path.Combine(folder, $"{i:0000}.txt"), $"switched {i}\n");
        Git(here.Work, "add", "--", branchFiles);
        Commit(here.Work, "Change the benchmark branch");
        string switched = Git(here.Work, "rev-parse", "HEAD").Trim();
        Git(here.Work, "checkout", "--detach", baseline);

        string state = Path.Combine(here.Root, "states");
        string label = capability == HexLinkBulkSupport.None ? "JSON fallback" : "binary";
        Write($"{label}: cloned the workspace. Starting the cold transfer.");
        using (var transport = new BenchmarkLink(a, b, here, there, capability))
        {
            HexLinkPeer peer = await transport.Start(stop);
            using var sync = new HexLinkSync(peer, here.Workspace.Folders[0], state);
            int files = 0;
            long bytes = 0;
            sync.Changed = () =>
            {
                if (sync.FilesTotal > files)
                    files = sync.FilesTotal;
                if (sync.BytesTotal > bytes)
                    bytes = sync.BytesTotal;
            };
            BeginProfile();
            var elapsed = Stopwatch.StartNew();
            await sync.Start(cancel: stop);
            await Idle(sync, stop, () => Write($"{label} cold progress: {sync.Sent} sent, {sync.FilesDone}/{sync.FilesTotal} complete, {sync.Summary}"));
            Assert.Empty(sync.Conflicts);
            Write($"{label} cold: {elapsed.Elapsed.TotalSeconds:F3} s, {files} files compared for transfer, " +
                $"{bytes / 1_000_000d:F1} MB, {sync.Sent} files sent, {sync.Skipped.Count} skipped.");
            EndProfile("cold");
            foreach (HexLinkSyncSkipped skipped in sync.Skipped)
                Write($"{label} skipped {skipped.Path}: {skipped.Why}");
            if (capability != HexLinkBulkSupport.None)
            {
                Assert.Empty(sync.Skipped);
                Assert.Equal(files, sync.Sent + sync.Received);
            }
            await sync.Flush(stop);
        }

        using (var transport = new BenchmarkLink(a, b, here, there, capability))
        {
            HexLinkPeer peer = await transport.Start(stop);
            using var sync = new HexLinkSync(peer, here.Workspace.Folders[0], state);
            BeginProfile();
            var elapsed = Stopwatch.StartNew();
            await sync.Start(cancel: stop);
            await Idle(sync, stop);
            Write($"{label} warm restart: {elapsed.Elapsed.TotalSeconds:F3} s, {sync.Sent + sync.Received} files transferred.");
            EndProfile("warm restart");
            Assert.Equal(0, sync.Sent + sync.Received);

            string remoteLive = Path.Combine(there.Work, "hexlink-benchmark-live.txt");
            BeginProfile();
            elapsed.Restart();
            File.WriteAllText(Path.Combine(here.Work, "hexlink-benchmark-live.txt"), "one saved file\n");
            await Until(() => Read(remoteLive) == "one saved file\n", stop);
            Write($"{label} one saved file: {elapsed.Elapsed.TotalSeconds:F3} s.");
            EndProfile("one save");

            BeginProfile();
            elapsed.Restart();
            Git(here.Work, "checkout", "--detach", switched);
            await Until(() => Enumerable.Range(0, 500).All(i =>
                Read(Path.Combine(there.Work, branchFiles, $"{i:0000}.txt")) == $"switched {i}\n"), stop);
            Write($"{label} branch switch, 500 paths: {elapsed.Elapsed.TotalSeconds:F3} s.");
            EndProfile("branch switch");

            const int size = 200 * 1024 * 1024;
            string next = Path.Combine(here.Root, "benchmark-medium.bin");
            using (FileStream file = File.Create(next))
            {
                byte[] random = new byte[256 * 1024], zero = new byte[random.Length];
                new Random(123).NextBytes(random);
                for (int i = 0; i < size / random.Length; i++)
                    file.Write((i & 1) == 0 ? random : zero);
            }
            string large = Path.Combine(there.Work, "hexlink-benchmark-big.bin");
            BeginProfile();
            elapsed.Restart();
            File.Move(next, Path.Combine(here.Work, Path.GetFileName(large)));
            if (capability == HexLinkBulkSupport.None)
            {
                await Until(() => sync.Skipped.Any(path => path.Path.EndsWith(Path.GetFileName(large), StringComparison.Ordinal)), stop);
                Write($"{label} 200 MiB: legacy size limit, skipped.");
            }
            else
            {
                await Until(() => File.Exists(large), stop);
                Assert.Equal(size, new FileInfo(large).Length);
                Write($"{label} 200 MiB: {elapsed.Elapsed.TotalSeconds:F3} s.");
            }
            EndProfile("200 MiB");
        }
    }

    private static List<(string Relative, string Full, string Commit)> Repositories(string source)
    {
        var repositories = new List<(string, string, string)>();
        var pinned = (Environment.GetEnvironmentVariable("HEX_LINK_SYNC_BENCH_PINS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(each => each.Split('@', 2))
            .ToDictionary(each => each[0], each => each[1], StringComparer.Ordinal);
        foreach (string relative in Candidates)
        {
            string path = Path.GetFullPath(Path.Combine(source, relative));
            if (!Directory.Exists(path))
                continue;
            GitResult top = GitProcess.Run(path, ["rev-parse", "--show-toplevel"]);
            if (top.Ok && PlatformPaths.Same(path, top.Output.Trim()))
                repositories.Add((relative, path, pinned.GetValueOrDefault(relative) ?? Git(path, "rev-parse", "HEAD").Trim()));
        }

        return repositories;
    }

    private static void Clone(string source, string commit, string destination, bool checkout)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        Git(parent, "clone", "-q", "--shared", "--no-checkout", source, destination);
        if (checkout)
            Git(destination, "checkout", "-q", "--detach", commit);
        else
        {
            Git(destination, "update-ref", "--no-deref", "HEAD", commit);
            Git(destination, "read-tree", commit);
        }
    }

    private static void Commit(string folder, string message) =>
        Git(folder, "-c", "user.name=Link benchmark", "-c", "user.email=link-benchmark@example.invalid", "commit", "-qm", message);

    private static string Git(string folder, params string[] args)
    {
        GitResult answer = GitProcess.Run(folder, ["-c", "core.longpaths=true", .. args], patience: TimeSpan.FromMinutes(5));
        Assert.True(answer.Ok, answer.Why);
        return answer.Output;
    }

    private static async Task Idle(HexLinkSync sync, CancellationToken cancel, Action? progress = null)
    {
        var since = Stopwatch.StartNew();
        await Until(() =>
        {
            if (progress is not null && since.Elapsed >= TimeSpan.FromSeconds(10))
            {
                progress();
                since.Restart();
            }
            return sync.State is HexLinkSyncState.Idle or HexLinkSyncState.Failed or HexLinkSyncState.NeedsGit;
        }, cancel);
        Assert.True(sync.State == HexLinkSyncState.Idle, sync.Summary);
    }

    private static async Task Until(Func<bool> predicate, CancellationToken cancel)
    {
        while (!predicate())
            await Task.Delay(20, cancel);
    }

    private static string? Read(string path)
    {
        try { return System.Text.Encoding.UTF8.GetString(HexLinkFiles.ReadShared(path)).ReplaceLineEndings("\n"); }
        catch (IOException) { return null; }
    }

    private sealed class BenchmarkLink(HexLinkTestMachine a, HexLinkTestMachine b, LinkServices here, LinkServices there,
        HexLinkBulkSupport capability) : IDisposable
    {
        private readonly HexLinkTransport listener = new(b.Identity, b.Trust) { BulkSupport = capability };
        private readonly HexLinkTransport dialler = new(a.Identity, a.Trust) { BulkSupport = capability };
        private readonly HexLink local = new(here);
        private readonly HexLink other = new(there);

        public async Task<HexLinkPeer> Start(CancellationToken stop)
        {
            HexLinkTestMachine.Know(a, Rights, b, Rights);
            var accepted = new TaskCompletionSource<HexLinkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Accepted = connection => accepted.TrySetResult(connection);
            listener.Start(0, loopbackOnly: true);
            HexLinkConnection dialled = await dialler.Dial("127.0.0.1", listener.Port, b.Device.Id, stop);
            HexLinkConnection heard = await accepted.Task.WaitAsync(stop);
            Task<HexLinkPeer> first = local.Add(dialled, stop);
            await Task.WhenAll(first, other.Add(heard, stop));
            return await first;
        }

        public void Dispose()
        {
            local.Dispose();
            other.Dispose();
            dialler.Dispose();
            listener.Dispose();
        }
    }
}
