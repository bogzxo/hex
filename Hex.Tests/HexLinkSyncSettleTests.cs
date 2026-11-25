using System.Text;

using Hex.Core;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    private static string Lines(string five) =>
        string.Concat(Enumerable.Range(1, 9).Select(n => n == 5 ? five + "\n" : $"line {n}\n"));

    // A's sync with its writes replaced
    private static HexLinkSync SyncWith(LinkedPair pair,
        Func<string, string, string?, byte[]?, CancellationToken, bool?, Task<string?>> apply) =>
        new(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            Quiet = TimeSpan.FromMilliseconds(50),
            Rescan = TimeSpan.FromSeconds(2),
            ApplyLocal = apply
        };

    // What an editor with the file in a shared document does with a write: refused, unless the text is what the room has saved
    private static Func<string, string, string?, byte[]?, CancellationToken, bool?, Task<string?>> GuardedBy(Func<string?> saved) =>
        async (full, wire, expected, bytes, stop, executable) =>
        {
            if (bytes is not null && Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n") != saved()?.ReplaceLineEndings("\n"))
                throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} is open in a shared document.");
            return await HexLinkFiles.CompareAndReplace(full, wire, expected, bytes, stop, executable);
        };

    [Fact]
    public async Task A_resolution_a_shared_document_hasnt_saved_is_refused_and_settles_once_the_room_has_saved_it()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "shared.txt";
        string wire = pair.A.Workspace.Folders[0].Name + "/" + path;
        string? saved = null;
        Put(pair.A, path, "start\n");
        Put(pair.B, path, "start\n");
        using (HexLinkSync first = SyncOf(pair))
        {
            await first.Start();
            await Settled(first);
            Put(pair.A, path, Lines("five"));
            await Settled(first);
            Assert.Equal(Lines("five"), Get(pair.B, path));
        }

        Put(pair.A, path, Lines("mine"));
        Put(pair.B, path, Lines("theirs"));
        using HexLinkSync sync = SyncWith(pair, GuardedBy(() => saved));
        await sync.Start();
        await Settled(sync);
        Assert.Single(sync.Conflicts);
        HexLinkMergePreview preview = sync.MergePreview(wire)!;
        Assert.NotNull(preview);

        string resolved = Lines("both");
        string? why = await sync.PublishMerge(wire, preview, resolved);
        Assert.Contains("is open in a shared document", why);
        Assert.Single(sync.Conflicts);

        // The room saves the resolution itself, which is what the guard then lets through
        saved = resolved;
        Assert.Null(await sync.ResolveToText(wire, resolved));
        Assert.Equal(resolved, Get(pair.A, path));
        Assert.Equal(resolved, Get(pair.B, path));
        Assert.Empty(sync.Conflicts);
        Assert.Null(sync.MergePreview(wire));
        Assert.DoesNotContain(Directory.EnumerateFiles(pair.A.Work, "*", SearchOption.AllDirectories),
            file => Path.GetFileName(file).Contains("theirs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Settling_on_a_text_leaves_a_side_that_already_holds_it_alone()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "held.txt";
        string wire = pair.A.Workspace.Folders[0].Name + "/" + path;
        byte[] held = Encoding.UTF8.GetBytes("x\r\ny\r\n");
        File.WriteAllBytes(Path.Combine(pair.A.Work, path), held);
        Put(pair.B, path, "x\nz\nw\n");

        // Nothing may be written here: a side that holds the text must not be touched
        using HexLinkSync sync = SyncWith(pair,
            (_, name, _, _, _, _) => throw new HexLinkException(HexLinkErrors.CONFLICT, $"{name} was written"));
        await sync.Start();
        await Settled(sync);
        Assert.Single(sync.Conflicts);

        Assert.Null(await sync.ResolveToText(wire, "x\ny\n"));
        Assert.Equal(held, File.ReadAllBytes(Path.Combine(pair.A.Work, path)));
        Assert.Equal("x\ny\n", Get(pair.B, path));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Settling_a_path_that_isnt_in_conflict_says_why()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "fine.txt", "same\n");
        Put(pair.B, "fine.txt", "same\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        string? why = await sync.ResolveToText(pair.A.Workspace.Folders[0].Name + "/fine.txt", "other\n");

        Assert.Contains("isn't in conflict", why);
        Assert.Equal("same\n", Get(pair.A, "fine.txt"));
        Assert.Equal("same\n", Get(pair.B, "fine.txt"));
    }
}
