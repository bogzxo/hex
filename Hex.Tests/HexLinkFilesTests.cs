using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// The workspace's files over a link: the path jail (.., rooted paths, drives, streams, .git, links out of the folder,
/// case tricks, Windows' device and short names), reading and writing that keep every byte and mark, the conflict
/// rule, too big, a watched file changing, and a file deleted only if it's what was read.
/// </summary>
public sealed class HexLinkFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hexlink-files", Guid.NewGuid().ToString("N"));
    private readonly string work, outside;
    private readonly IReadOnlyList<HexLinkFolder> folders;

    public HexLinkFilesTests()
    {
        work = Path.Combine(root, "work");
        outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(work, "src"));
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(work, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(work, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        folders = [new HexLinkFolder("work", work)];
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (Exception) { }
    }

    private void Refused(string wire)
    {
        var refused = Assert.Throws<HexLinkException>(() => HexLinkFiles.Resolve(folders, wire));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
    }

    [Fact]
    public void A_plain_path_resolves_inside_its_folder()
    {
        Assert.Equal(Path.Combine(work, "src", "a.cs"), HexLinkFiles.Resolve(folders, "work/src/a.cs"));
        Assert.Equal(Path.Combine(work, "src", "new.cs"), HexLinkFiles.Resolve(folders, "work/src/new.cs"));
        Assert.Equal(work, HexLinkFiles.Resolve(folders, "work"));
    }

    [Fact]
    public void A_file_being_written_here_is_read_for_a_device_and_the_writer_carries_on()
    {
        string log = Path.Combine(work, "src", "log.txt");
        File.WriteAllText(log, "first\n");

        // A program here appending, as a log or the person's editor would, while the device reads it
        using (var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            JsonObject read = HexLinkFiles.Read(log, "work/src/log.txt");
            Assert.Equal("first\n", read["text"]!.GetValue<string>());
            Assert.Equal(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("first\n")), HexLinkFiles.HashOf(log));
            writer.Write(Encoding.UTF8.GetBytes("second\n"));
        }

        // And a save here while the device holds the file open for reading isn't turned away
        using (var reading = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            File.AppendAllText(log, "third\n");
        Assert.Equal("first\nsecond\nthird\n", File.ReadAllText(log));
    }

    [Theory]
    [InlineData("work/../outside/secret.txt")]
    [InlineData("work/src/../../outside/secret.txt")]
    [InlineData("work/./src/a.cs")]
    [InlineData("work//src/a.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("work/C:/Windows")]
    [InlineData("work\\..\\outside")]
    [InlineData("work/src/a.cs:secret")]
    [InlineData("work/src/a.cs::$DATA")]
    [InlineData("work/.git/config")]
    [InlineData("work/.GIT/config")]
    [InlineData("work/.git./config")]
    [InlineData("work/.git /config")]
    [InlineData("work/src/a.cs.")]
    [InlineData("work/src/a.cs ")]
    [InlineData("work/CON")]
    [InlineData("work/src/nul.txt")]
    [InlineData("work/com1")]
    [InlineData("work/LPT9.log")]
    [InlineData("work/src/a\u0000.cs")]
    [InlineData("work/a?.cs")]
    [InlineData("outside/secret.txt")]
    [InlineData("")]
    [InlineData("..")]
    public void A_path_out_of_the_jail_is_refused(string wire) => Refused(wire);

    [Fact]
    public void A_short_name_is_refused_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Refused("work/GIT~1/config");
    }

    [Fact]
    public void A_folder_name_of_another_case_is_the_platforms_rule()
    {
        if (OperatingSystem.IsLinux())
            Refused("WORK/src/a.cs");
        else
            Assert.Equal(Path.Combine(work, "src", "a.cs"), HexLinkFiles.Resolve(folders, "WORK/src/a.cs"));
    }

    [Fact]
    public void A_link_out_of_the_folder_is_refused_and_one_inside_it_is_followed()
    {
        string link = Path.Combine(work, "escape");
        string inside = Path.Combine(work, "inner");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No right to make links here (Windows without developer mode); a junction needs none
            if (!OperatingSystem.IsWindows() || !Junction(link, outside))
                return;
        }

        Refused("work/escape/secret.txt");
        Refused("work/escape");

        // A link to .git inside the folder is still .git
        try
        {
            Directory.CreateSymbolicLink(inside, Path.Combine(work, ".git"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!Junction(inside, Path.Combine(work, ".git")))
                return;
        }

        Refused("work/inner/config");
    }

    // A junction made the way Windows makes one without any right, false when it couldn't be
    private static bool Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        });
        made?.WaitForExit(10_000);
        return Directory.Exists(link);
    }

    [Fact]
    public void A_listing_leaves_out_git_and_lists_folders_first()
    {
        File.WriteAllText(Path.Combine(work, "readme.md"), "hi");
        JsonObject listed = HexLinkFiles.List(folders, "work");
        string[] names = [.. listed["entries"]!.AsArray().Select(entry => entry!["name"]!.GetValue<string>())];
        Assert.Equal(["src", "readme.md"], names);
        Assert.Equal("folder", listed["entries"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(2, listed["entries"]![1]!["size"]!.GetValue<long>());
        Assert.Equal("work", HexLinkFiles.List(folders, string.Empty)["entries"]![0]!["name"]!.GetValue<string>());
        Assert.Throws<HexLinkException>(() => HexLinkFiles.List(folders, "work/.git"));
    }

    [Fact]
    public void Workspace_excludes_apply_to_children_and_symlink_targets()
    {
        var excludes = new HexExcludes(["**/*.private", "**/secrets"], [], false);
        string secrets = Path.Combine(work, "src", "secrets");
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(secrets, "file.txt"), "private");
        File.WriteAllText(Path.Combine(work, "src", "hidden.private"), "private");
        Assert.Equal("a.cs", Assert.Single(HexLinkFiles.List(folders, "work/src", excludes)["entries"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.False(HexLinkFiles.MayReach(folders, "work/src/secrets/file.txt", excludes));
        Assert.False(HexLinkFiles.MayReach(folders, "work/src/new.private", excludes));

        string alias = Path.Combine(work, "alias");
        try { Directory.CreateSymbolicLink(alias, secrets); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!Junction(alias, secrets))
                return;
        }
        Assert.False(HexLinkFiles.MayReach(folders, "work/alias/file.txt", excludes));
    }

    [Fact]
    public async Task Excluded_files_are_refused_by_every_file_rpc()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string hidden = Path.Combine(pair.A.Work, ".vs");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "private.txt"), "private");
        const string wire = "work/.vs/private.txt";
        string hash = HexLinkFiles.Hash("private"u8);
        Assert.DoesNotContain(await pair.PeerA.List("work"), entry => entry.Name == ".vs");
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.List("work/.vs")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Read(wire)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Write(wire, hash, "changed"u8.ToArray())));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Delete(wire, hash)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Watch(wire)));
        Assert.Equal("private", File.ReadAllText(Path.Combine(hidden, "private.txt")));
    }

    [Fact]
    public void The_read_checks_actual_bytes_past_the_limit()
    {
        byte[] bytes = new byte[HexLinkFiles.MOST_READ + 2];
        using var growing = new MemoryStream(bytes) { Position = 1 };
        var failure = Assert.Throws<HexLinkException>(() => HexLinkFiles.ReadBounded(growing, "work/file.txt"));
        Assert.Equal(HexLinkErrors.TOO_BIG, failure.Code);
        Assert.Equal(HexLinkFiles.MOST_READ + 1, growing.Position - 1);
    }

    [Fact]
    public async Task Writes_and_deletes_with_the_same_base_cannot_both_win()
    {
        string full = Path.Combine(work, "src", "a.cs");
        const string wire = "work/src/a.cs";
        string before = HexLinkFiles.HashOf(full)!;
        async Task<HexLinkException?> Change(byte[]? bytes)
        {
            try
            {
                await HexLinkFiles.CompareAndReplace(full, wire, before, bytes, default);
                return null;
            }
            catch (HexLinkException failure)
            {
                return failure;
            }
        }

        HexLinkException?[] outcomes = await Task.WhenAll(Change("new contents"u8.ToArray()), Change(null));
        var conflict = Assert.Single(outcomes.OfType<HexLinkException>());
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.Equal(HexLinkFiles.HashOf(full), HexLinkPeer.ConflictHash(conflict));
        if (outcomes[0] is null)
            Assert.Equal("new contents", File.ReadAllText(full));
        else
            Assert.False(File.Exists(full));
    }

    [Fact]
    public async Task An_editor_save_and_a_link_write_share_the_publication_gate()
    {
        string full = Path.Combine(work, "src", "a.cs");
        string before = File.ReadAllText(full);
        Task local, remote;
        using (HexLinkFiles.LockForLocalWrite(full))
        {
            local = Task.Run(() => DocumentFile.Write(full, "local", new UTF8Encoding(false)));
            remote = HexLinkFiles.CompareAndReplace(full, "work/src/a.cs", HexLinkFiles.HashOf(full), "remote"u8.ToArray(), default);
            Assert.True(Eventually(() => Directory.EnumerateFiles(Path.GetDirectoryName(full)!, ".hex-save-*.tmp").Any()));
            Assert.False(local.IsCompleted);
            Assert.False(remote.IsCompleted);
            Assert.Equal(before, File.ReadAllText(full));
        }

        await local;
        int? remoteResult = await CodeOf(remote);
        Assert.True(remoteResult is null or HexLinkErrors.CONFLICT);
        Assert.Equal("local", File.ReadAllText(full));
    }

    [Fact]
    public async Task A_local_writer_with_an_open_handle_on_windows_is_not_overwritten()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string full = Path.Combine(work, "src", "a.cs");
        string before = HexLinkFiles.HashOf(full)!;
        using (var writer = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var conflict = await Assert.ThrowsAsync<HexLinkException>(() =>
                HexLinkFiles.CompareAndReplace(full, "work/src/a.cs", before, "new contents"u8.ToArray(), default));
            Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        }
        Assert.Equal(before, HexLinkFiles.HashOf(full));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', 13, 10 }, "utf-8-bom", "crlf")]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0, 10, 0 }, "utf-16le", "lf")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i' }, "utf-16be", "")]
    [InlineData(new byte[] { 0xC3, 0xA9, (byte)'t', (byte)'e', 13 }, "utf-8", "cr")]
    [InlineData(new byte[] { 0xC3, 0x28, 0xFF, 0 }, "binary", "")]
    [InlineData(new byte[] { (byte)'a', 0, (byte)'b' }, "binary", "")]
    public async Task Read_and_write_back_keep_every_byte_and_mark(byte[] bytes, string encoding, string eol)
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string full = Path.Combine(pair.A.Work, "file.txt");
        File.WriteAllBytes(full, bytes);

        HexLinkFile read = await pair.PeerA.Read("work/file.txt");
        Assert.Equal(encoding, read.Encoding);
        Assert.Equal(eol, read.Eol);
        Assert.Equal(bytes, read.Bytes);
        Assert.Equal(HexLinkFiles.Hash(bytes), read.Hash);
        Assert.Equal(encoding == "binary", read.Text is null);

        // Changed and written back as bytes, in the encoding it was in
        byte[] changed = read.Text is { } text ? HexLinkFiles.Encode(text + "!", encoding)! : [.. bytes, 7];
        string hash = await pair.PeerA.Write("work/file.txt", read.Hash, changed);
        Assert.Equal(changed, File.ReadAllBytes(full));
        Assert.Equal(HexLinkFiles.Hash(changed), hash);
        Assert.Equal(bytes.Take(2), File.ReadAllBytes(full).Take(2));
        Assert.Contains(pair.A.Audited, line => line.What == "edit" && line.Detail.Contains("work/file.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stale_base_conflicts_with_the_hash_the_file_has_now()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string full = Path.Combine(pair.A.Work, "a.txt");
        File.WriteAllText(full, "one");
        HexLinkFile read = await pair.PeerA.Read("work/a.txt");
        File.WriteAllText(full, "two, from the editor");

        var conflict = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Write("work/a.txt", read.Hash, Encoding.UTF8.GetBytes("three")));
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.Equal(HexLinkFiles.Hash(File.ReadAllBytes(full)), HexLinkPeer.ConflictHash(conflict));
        Assert.Equal("two, from the editor", File.ReadAllText(full));

        // Overwriting is sending again with the hash it has now
        await pair.PeerA.Write("work/a.txt", HexLinkPeer.ConflictHash(conflict), Encoding.UTF8.GetBytes("three"));
        Assert.Equal("three", File.ReadAllText(full));
    }

    [Fact]
    public async Task A_new_file_over_one_that_is_there_conflicts_and_one_that_isnt_is_made_with_its_folders()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        File.WriteAllText(Path.Combine(pair.A.Work, "there.txt"), "here");
        var conflict = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Write("work/there.txt", null, [1, 2]));
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.NotNull(HexLinkPeer.ConflictHash(conflict));

        await pair.PeerA.Write("work/deep/er/new.txt", null, [1, 2]);
        Assert.Equal([1, 2], File.ReadAllBytes(Path.Combine(pair.A.Work, "deep", "er", "new.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.A.Work, "deep", "er"), "*.hexlink"));

        // A file that went since it was read is a conflict with no hash
        var gone = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Write("work/missing.txt", new string('0', 64), [1]));
        Assert.Equal(HexLinkErrors.CONFLICT, gone.Code);
        Assert.Null(HexLinkPeer.ConflictHash(gone));
    }

    [Fact]
    public async Task A_file_too_big_isnt_read()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        using (var big = File.Create(Path.Combine(pair.A.Work, "big.bin")))
            big.SetLength(HexLinkFiles.MOST_READ + 1);
        Assert.Equal(HexLinkErrors.TOO_BIG, await CodeOf(pair.PeerA.Read("work/big.bin")));
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.Read("work/nothing.txt")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Read("work/../x")));
    }

    [Fact]
    public async Task A_file_of_the_most_size_goes_over_whole()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        byte[] bytes = new byte[HexLinkFiles.MOST_READ];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(pair.A.Work, "most.bin"), bytes);
        HexLinkFile read = await pair.PeerA.Read("work/most.bin");
        Assert.Equal(bytes, read.Bytes);
    }

    [Fact]
    public async Task A_watched_file_that_changes_is_said_with_its_new_hash()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string full = Path.Combine(pair.A.Work, "watched.txt");
        File.WriteAllText(full, "before");
        var heard = new System.Collections.Concurrent.ConcurrentQueue<(string, string?)>();
        pair.PeerA.FileChanged = (path, hash) => heard.Enqueue((path, hash));

        Assert.Equal(HexLinkFiles.Hash(File.ReadAllBytes(full)), await pair.PeerA.Watch("work/watched.txt"));
        File.WriteAllText(full, "after, longer");
        Assert.True(Eventually(() => heard.Contains(("work/watched.txt", HexLinkFiles.Hash(File.ReadAllBytes(full))))));

        File.Delete(full);
        Assert.True(Eventually(() => heard.Contains(("work/watched.txt", null))));

        await pair.PeerA.Unwatch("work/watched.txt");
        int count = heard.Count;
        File.WriteAllText(full, "unwatched");
        Thread.Sleep(400);
        Assert.Equal(count, heard.Count);
    }

    [Fact]
    public async Task A_file_is_deleted_only_when_it_is_what_was_read()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string full = Path.Combine(pair.A.Work, "doomed.txt");
        File.WriteAllText(full, "one");
        string hash = HexLinkFiles.Hash(File.ReadAllBytes(full));
        File.WriteAllText(full, "two");

        var conflict = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Delete("work/doomed.txt", hash));
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.True(File.Exists(full));

        await pair.PeerA.Delete("work/doomed.txt", HexLinkPeer.ConflictHash(conflict)!);
        Assert.False(File.Exists(full));
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.Delete("work/doomed.txt", hash)));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Delete("work/.git/config", hash)));
    }
}
