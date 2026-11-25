using Hex.Core;

using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>
/// Numbers for what a Link tree costs in memory and allocations, printed by the run and bounded loosely. The counts are
/// the process's (a scan reads files on many threads), so it runs alone after the other classes, and the allocation is
/// the least of three scans so a stray background allocation can't be the number.
/// </summary>
[Collection(Timed.NAME)]
public sealed class HexLinkMemoryProbeTests(ITestOutputHelper output)
{
    private const int FILES = 10_000;

    [Fact]
    public void A_ten_thousand_file_tree_stays_inside_its_memory_and_allocation_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), "hexlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (int i = 0; i < FILES; i++)
            {
                string folder = Path.Combine(root, "src", "module" + i / 100, "part" + i / 10);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"file{i}.cs"), $"class C{i} {{ }}\nline {i}\n");
            }

            var folder0 = new HexLinkFolder("probe", root);
            GC.Collect();
            long baseline = GC.GetTotalMemory(true);
            var tree = new HexLinkTree(folder0);
            HexLinkManifest manifest = tree.Manifest();
            Assert.Equal(FILES, manifest.Files.Count);
            long afterTree = GC.GetTotalMemory(true);

            long rescan = long.MaxValue;
            HexLinkManifest again = manifest;
            for (int round = 0; round < 3; round++)
            {
                long allocatedBefore = GC.GetTotalAllocatedBytes(true);
                again = tree.Manifest(verify: false, reuse: false);
                rescan = Math.Min(rescan, GC.GetTotalAllocatedBytes(true) - allocatedBefore);
                Assert.Equal(FILES, again.Files.Count);
            }

            long serialize = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var json = HexLinkTree.Json(manifest);
                serialize = Math.Min(serialize, GC.GetTotalAllocatedBytes(true) - allocatedBefore);
                GC.KeepAlive(json);
            }

            manifest = again;

            long beforeWatch = GC.GetTotalMemory(true);
            using var watch = new HexLinkTreeWatch(tree, _ => { }, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
            watch.Seed(manifest);
            long afterWatch = GC.GetTotalMemory(true);

            output.WriteLine($"PROBE tree+manifest retained: {(afterTree - baseline) / (double)FILES:F0} B/file");
            output.WriteLine($"PROBE watch seed retained: {(afterWatch - beforeWatch) / (double)FILES:F0} B/file");
            output.WriteLine($"PROBE cached rescan allocated: {rescan / (double)FILES:F0} B/file");
            output.WriteLine($"PROBE manifest json allocated: {serialize / (double)FILES:F0} B/file");
            string report = Environment.GetEnvironmentVariable("HEX_PROBE_REPORT") ?? "C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/link-probe.txt";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, $"tree+manifest {(afterTree - baseline) / (double)FILES:F0} B/file\nwatch {(afterWatch - beforeWatch) / (double)FILES:F0} B/file\nrescan {rescan / (double)FILES:F0} B/file\njson {serialize / (double)FILES:F0} B/file\n");
            }
            catch (IOException) { }
            GC.KeepAlive(manifest);
            GC.KeepAlive(tree);

            Assert.True((afterTree - baseline) / (double)FILES < 2_000, "tree and manifest memory per file");
            Assert.True(rescan / (double)FILES < 4_000, "cached rescan allocations per file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
