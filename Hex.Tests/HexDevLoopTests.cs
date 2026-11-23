// What these test is only in a Debug build of Hex, a published Release one never restarts into a build of itself
#if DEBUG
using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

// Hex restarting into a build of itself, what it hands the new Hex and how it gets a running Hex's files out of the way
public sealed class HexDevLoopTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-dev-loop-" + Guid.NewGuid().ToString("N"));

    public HexDevLoopTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_chat_written_down_reads_back_as_it_was_with_its_session_and_rows()
    {
        var chat = new HexAgentChat(4, "claude_code") { Parent = 2, Depth = 1, Name = "scout", Session = "s-1", Model = "haiku", Cost = 0.25, ContextUsed = 1200, ContextLimit = 200_000 };
        chat.Options["model"] = "haiku";
        chat.Ask("find it");
        chat.Say("found it");
        chat.Tool("t1", "mcp__hex__hex_symbols", "{\"query\":\"X\"}");
        chat.Tool("t2", "Bash", "{}");
        chat.ToolDone("t1", true, "a.cs:1");
        chat.Done();

        HexAgentChat back = HexAgentChatStore.Read(System.Text.Json.Nodes.JsonNode.Parse(HexAgentChatStore.Write(chat).ToJsonString()))!;
        Assert.Equal((4, "claude_code", 2, 1, "scout", "s-1", "haiku"), (back.Id, back.Agent, back.Parent, back.Depth, back.Name, back.Session, back.Model));
        Assert.Equal(("find it", 0.25, 1200L, 200_000L), (back.Title, back.Cost, back.ContextUsed, back.ContextLimit));
        Assert.Equal("haiku", back.Options["model"]);
        Assert.Equal(chat.Entries.Select(entry => (entry.Kind, entry.Text, entry.State)), back.Entries.Select(entry => (entry.Kind, entry.Text, entry.State)));
        Assert.False(back.Running);
        Assert.False(back.Unread);
    }

    [Fact]
    public void A_file_in_use_is_moved_aside_for_a_build_and_put_back_when_it_fails()
    {
        string locked = Path.Combine(folder, "Hex.Dawn.dll"), free = Path.Combine(folder, "notes.txt");
        File.WriteAllText(locked, "old build");
        File.WriteAllText(free, "free");

        List<(string From, string Aside)> moved;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            moved = DawnScene.MoveLockedAside(folder);
            Assert.Equal(locked, Assert.Single(moved).From);
            Assert.False(File.Exists(locked));

            // A failed build wrote half of something in its place, putting back takes the old one again
            File.WriteAllText(locked, "half a build");
            DawnScene.PutBack(moved);
        }

        Assert.Equal("old build", File.ReadAllText(locked));
        Assert.Equal("free", File.ReadAllText(free));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.hexold"));
    }

    [Fact]
    public void Every_image_of_the_build_is_renamed_for_a_build_so_none_is_written_over_in_place()
    {
        string apphost = Path.Combine(folder, "Hex"), library = Path.Combine(folder, "Hex.Core.dll"), notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(apphost, "apphost");
        File.WriteAllText(library, "library");
        File.WriteAllText(notes, "free");

        var moved = DawnScene.MoveLockedAside(folder, everyImage: true);
        Assert.Equal([apphost, library], moved.Select(item => item.From).Order());
        Assert.All(moved, item => Assert.True(File.Exists(item.Aside) && !File.Exists(item.From)));
        Assert.Equal("free", File.ReadAllText(notes));
        Assert.Equal("apphost", File.ReadAllText(moved.Single(item => item.From == apphost).Aside));

        DawnScene.PutBack(moved);
        Assert.Equal("library", File.ReadAllText(library));
        Assert.Equal("apphost", File.ReadAllText(apphost));
    }

    [Fact]
    public async Task Child_processes_can_hold_two_generations_of_a_build_dll()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string dll = Path.Combine(folder, "Hex.Dawn.dll");
        string source = Path.Combine(System.Environment.SystemDirectory, "version.dll");
        File.Copy(source, dll);
        using var firstChild = MapBuildDll();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("mapped", await firstChild.StandardOutput.ReadLineAsync(stop.Token));
            var first = Assert.Single(DawnScene.MoveLockedAside(folder));
            Assert.Equal(dll, first.From);
            Assert.False(File.Exists(dll));

            File.Copy(source, dll);
            using var secondChild = MapBuildDll();
            try
            {
                Assert.Equal("mapped", await secondChild.StandardOutput.ReadLineAsync(stop.Token));
                var second = Assert.Single(DawnScene.MoveLockedAside(folder));
                Assert.Equal(dll, second.From);
                Assert.NotEqual(first.Aside, second.Aside);
                Assert.False(File.Exists(dll));

                File.WriteAllText(dll, "incomplete replacement");
                DawnScene.PutBack([second]);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(dll));
                Assert.True(File.Exists(first.Aside));
                Assert.False(File.Exists(second.Aside));
            }
            finally
            {
                await StopChild(secondChild);
            }

            await StopChild(firstChild);
            DawnScene.CleanAside(folder);
            Assert.False(File.Exists(first.Aside));
        }
        finally
        {
            await StopChild(firstChild);
        }
    }

    private System.Diagnostics.Process MapBuildDll()
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            Add-Type -Name Loader -Namespace Test -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern System.IntPtr LoadLibraryEx(string name, System.IntPtr file, uint flags);
            '@
            $path = [IO.Path]::Combine((Get-Location).Path, 'Hex.Dawn.dll')
            if ([Test.Loader]::LoadLibraryEx($path, [IntPtr]::Zero, 1) -eq [IntPtr]::Zero) { exit 1 }
            [Console]::WriteLine('mapped')
            [void][Console]::ReadLine()
            """);
        return System.Diagnostics.Process.Start(start)!;
    }

    private static async Task StopChild(System.Diagnostics.Process child)
    {
        if (child.HasExited)
            return;

        child.StandardInput.Close();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await child.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public void A_build_in_agents_scratch_under_data_is_neither_moved_aside_nor_cleaned_away()
    {
        string scratch = Path.Combine(folder, "data", "agents", "temp", "scratch", "worker", "bin");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(Path.Combine(folder, "runtimes"));
        string theirs = Path.Combine(scratch, "Hex.dll"), theirsAside = Path.Combine(scratch, "Hex.Core.dll.7.hexold");
        string mine = Path.Combine(folder, "runtimes", "native.dll");
        File.WriteAllText(theirs, "a worker's build");
        File.WriteAllText(theirsAside, "a worker's own");
        File.WriteAllText(mine, "this build");

        List<(string From, string Aside)> moved;
        using (new FileStream(theirs, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (new FileStream(mine, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            moved = DawnScene.MoveLockedAside(folder);

        Assert.Equal(mine, Assert.Single(moved).From);
        Assert.True(File.Exists(theirs));
        DawnScene.PutBack(moved);

        DawnScene.CleanAside(folder);
        Assert.True(File.Exists(theirsAside));
    }

    [Fact]
    public void The_good_build_is_copied_without_Hexs_state_and_only_what_differs()
    {
        string bin = Path.Combine(folder, "bin"), good = HexDevBuilds.GoodFolder(bin);
        Directory.CreateDirectory(Path.Combine(bin, "settings"));
        Directory.CreateDirectory(Path.Combine(bin, "Assets"));
        File.WriteAllText(Path.Combine(bin, "Hex.dll"), "build");
        File.WriteAllText(Path.Combine(bin, "Assets", "a.hor"), "asset");
        File.WriteAllText(Path.Combine(bin, "settings", "settings.hor"), "mine");
        File.WriteAllText(Path.Combine(bin, "debug.log"), "log");
        File.WriteAllText(Path.Combine(bin, "Hex.dll.1.hexold"), "aside");

        Assert.Equal(2, HexDevBuilds.Mirror(bin, good));
        Assert.Equal(["Hex.dll"], Directory.EnumerateFiles(good).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(good, "Assets", "a.hor")));
        Assert.False(Directory.Exists(Path.Combine(good, "settings")));
        Assert.Equal(0, HexDevBuilds.Mirror(bin, good));

        // A crashed build's files put right from the good one, the settings it ran with left as they are
        File.WriteAllText(Path.Combine(bin, "Hex.dll"), "a build that crashed");
        File.WriteAllText(Path.Combine(bin, "settings", "settings.hor"), "changed since");
        Assert.Equal(1, HexDevBuilds.Mirror(good, bin));
        Assert.Equal("build", File.ReadAllText(Path.Combine(bin, "Hex.dll")));
        Assert.Equal("changed since", File.ReadAllText(Path.Combine(bin, "settings", "settings.hor")));
    }

    [Fact]
    public void What_an_earlier_Hex_moved_aside_is_cleaned_away()
    {
        File.WriteAllText(Path.Combine(folder, "Hex.dll.123.hexold"), "old");
        File.WriteAllText(Path.Combine(folder, "Hex.dll"), "new");
        DawnScene.CleanAside(folder);
        Assert.Equal(["Hex.dll"], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
    }
}
#endif
