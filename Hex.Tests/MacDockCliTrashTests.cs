using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

public sealed class MacDockCliTrashTests
{
    [Fact]
    public void ChoosesTheFolderOfTheirOwnWhenItIsOnThePath()
    {
        var place = MacCli.Choose("/Users/a", "/usr/bin:/Users/a/.local/bin:/bin");
        Assert.Equal("/Users/a/.local/bin/hex", place.Link);
        Assert.False(place.NeedsAdministrator);

        Assert.Equal("/Users/a/.local/bin/hex", MacCli.Choose("/Users/a", "~/.local/bin/:/bin").Link);
    }

    [Fact]
    public void FallsBackToUsrLocalBinWithAnAdministrator()
    {
        var place = MacCli.Choose("/Users/a", "/usr/bin:/bin:/Users/a/.local/binary");
        Assert.Equal("/usr/local/bin/hex", place.Link);
        Assert.True(place.NeedsAdministrator);
        Assert.True(MacCli.Choose("/Users/a", null).NeedsAdministrator);
    }

    [Fact]
    public void TheTargetIsTheBundlesProgramElseTheProcess()
    {
        Assert.Equal("/Applications/Hex.app/Contents/MacOS/Hex", MacCli.Target("/Applications/Hex.app", "/x/y"));
        Assert.Equal("/x/y/Hex", MacCli.Target(null, "/x/y/Hex"));
    }

    [Fact]
    public void TheAdministratorScriptIsQuotedForTheShellAndAppleScript()
    {
        string script = MacCli.AdministratorScript("/Apps/it's \"Hex\".app/Contents/MacOS/Hex", "/usr/local/bin/hex", "/usr/local/bin");
        Assert.StartsWith("do shell script \"mkdir -p '/usr/local/bin' && ln -sf ", script);
        Assert.EndsWith("\" with administrator privileges", script);
        Assert.Contains("it'\\\\''s \\\"Hex\\\".app", script);
    }

    [Fact]
    public void LinkMakesReplacesAndNeverOverwritesAFile()
    {
        // Windows doesn't grant symlink rights by default and the suite has no other skip mechanism.
        if (OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(Path.GetTempPath(), "hex-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            string link = Path.Combine(root, ".local", "bin", "hex");
            Assert.Null(MacCli.Link("/first/Hex", link));
            Assert.Equal("/first/Hex", new FileInfo(link).LinkTarget);

            Assert.Null(MacCli.Link("/second/Hex", link));
            Assert.Equal("/second/Hex", new FileInfo(link).LinkTarget);

            string plain = Path.Combine(root, "plain");
            File.WriteAllText(plain, "mine");
            Assert.Equal("exists", MacCli.Link("/second/Hex", plain));
            Assert.Equal("mine", File.ReadAllText(plain));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TheDockBadgesApprovalsAndBouncesOnWhatIsNew()
    {
        var dock = new MacDockState();
        var first = dock.Update(0, 0, false);
        Assert.False(first.BadgeChanged);
        Assert.False(first.Informational || first.Critical);

        var approval = dock.Update(1, 0, false);
        Assert.True(approval.BadgeChanged);
        Assert.Equal("1", approval.Badge);
        Assert.True(approval.Informational);

        var same = dock.Update(1, 0, false);
        Assert.False(same.BadgeChanged || same.Informational || same.Critical);

        var question = dock.Update(1, 1, false);
        Assert.True(question.Critical);
        Assert.False(question.Informational);

        var cleared = dock.Update(0, 0, false);
        Assert.True(cleared.BadgeChanged);
        Assert.Null(cleared.Badge);

        Assert.True(dock.Update(0, 0, true).Informational);
    }

    [Fact]
    public void ATaskEndingBouncesOnce()
    {
        var dock = new MacDockState();
        MacDockState.NoteTaskEnded();
        Assert.True(dock.Update(0, 0).Informational);
        Assert.False(dock.Update(0, 0).Informational);
    }

    [Fact]
    public void TrashPutsAFileInTheTrashWithPutBack()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string file = Path.Combine(Path.GetTempPath(), "hex-trash-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "a file of the test's own");
        Assert.True(HexTrash.TryDiscardNative(file, out string? why, out string? trashed), why);
        try
        {
            Assert.False(File.Exists(file));
            Assert.NotNull(trashed);
            Assert.True(File.Exists(trashed));
            Assert.Contains(".Trash", trashed);
        }
        finally
        {
            if (trashed is not null && File.Exists(trashed))
                File.Delete(trashed);
        }
    }
}
