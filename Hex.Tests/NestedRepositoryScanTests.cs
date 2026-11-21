using System.Diagnostics;
using System.Text.Json;
using Hex.Dawn;

namespace Hex.Tests;

public sealed class NestedRepositoryScanTests
{
    [Fact]
    public void Finds_nested_repositories_and_worktrees_but_skips_build_and_hidden_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-nested-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Repository(string name, bool file = false)
            {
                string folder = Path.GetFullPath(Path.Combine(root, name));
                Directory.CreateDirectory(folder);
                if (file) File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: elsewhere");
                else Directory.CreateDirectory(Path.Combine(folder, ".git"));
                return folder;
            }
            string a = Repository("engine"), b = Repository("game/worktree", true);
            string c = Repository("one/two/three/four/five");
            Repository("bin/hidden"); Repository(".cache/hidden"); Repository("node_modules/hidden");
            Repository("one/two/three/four/five/six");
            Assert.Equal(new[] { a, b, c }.Order(), HexGit.FindDown([root]).Order());
            Directory.Delete(Path.Combine(a, ".git"));
            Assert.Equal(new[] { b, c }.Order(), HexGit.FindDown([root]).Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Looks_down_only_from_what_was_made_as_far_as_the_whole_look_goes()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hex-nested-made-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            string Folder(string name, bool git = false)
            {
                string folder = Path.GetFullPath(Path.Combine(root, name));
                Directory.CreateDirectory(folder);
                if (git) Directory.CreateDirectory(Path.Combine(folder, ".git"));
                return folder;
            }
            string clone = Folder("projects/clone", git: true), inner = Folder("projects/clone/external/mod", git: true);
            string initialised = Folder("lib", git: true), file = Path.Combine(initialised, "a.cs");
            File.WriteAllText(file, string.Empty);
            string empty = Folder("empty"), elsewhere = Folder("untouched", git: true);
            Folder("bin/hidden", git: true);
            string deep = Folder("one/two/three/four/five/six", git: true);

            var (found, made) = HexGit.FindDownFrom([root], [Path.Combine(root, "projects"), file, empty, Path.Combine(root, "bin/hidden"), deep, root]);
            Assert.Equal(new[] { clone, inner, initialised }.Order(), found.Order());
            Assert.DoesNotContain(elsewhere, found);
            Assert.Equal(new[] { Path.Combine(root, "projects"), empty }.Order(), made.Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Measure_requested_workspace_scan_without_modifying_it()
    {
        string? root = Environment.GetEnvironmentVariable("HEX_NESTED_BENCH_ROOT");
        string? output = Environment.GetEnvironmentVariable("HEX_NESTED_BENCH_OUT");
        if (root is null || output is null) return;
        for (int i = 0; i < 3; i++) HexGit.FindDown([root]);
        using var file = File.Create(output);
        using var json = new Utf8JsonWriter(file, new() { Indented = true });
        json.WriteStartObject(); json.WriteString("root", root); json.WriteStartArray("samples");
        for (int i = 0; i < 12; i++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            var found = HexGit.FindDown([root]);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            json.WriteStartObject(); json.WriteNumber("ms", ms); json.WriteNumber("bytes", allocated);
            json.WriteStartArray("repositories"); foreach (string path in found.Order()) json.WriteStringValue(path);
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();
    }
}
