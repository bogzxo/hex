using System.Text;

using Hex.Core;
using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Tests;

public sealed class GitUnmergedTests : IDisposable
{
    private const string Name = "file [1].txt";
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-git-conflict-" + Guid.NewGuid().ToString("N"));

    private string PathOfFile => Path.Combine(root, Name);

    public GitUnmergedTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData((int)GitOperation.Merging)]
    [InlineData((int)GitOperation.Rebasing)]
    [InlineData((int)GitOperation.CherryPicking)]
    [InlineData((int)GitOperation.None)]
    public async Task Text_conflicts_resolve_and_stage_without_continuing(int kind)
    {
        var operation = (GitOperation)kind;
        GitRepository repository = Init("base\n");
        if (operation == GitOperation.None)
        {
            File.WriteAllText(PathOfFile, "stashed\n");
            Git("stash", "push");
            Commit("main change\n", "Main changes");
            Fails("stash", "pop");
        }
        else
        {
            Diverge();
            string[] command = operation switch
            {
                GitOperation.Merging => ["merge", "other"],
                GitOperation.Rebasing => ["rebase", "other"],
                _ => ["cherry-pick", "other"],
            };
            Fails(command);
        }

        Assert.Equal(operation, repository.Operation());
        GitUnmergedFile index = Assert.Single(GitUnmerged.List(repository));
        Assert.Equal(Name, index.Path);
        Assert.NotNull(index.Base);
        Assert.NotNull(index.Mine);
        Assert.NotNull(index.Theirs);
        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, PathOfFile));
        (string mineLabel, string theirsLabel) = operation switch
        {
            GitOperation.Rebasing => ("other (onto)", $"{Git("rev-parse", "--short", "main").Trim()} Main changes"),
            GitOperation.CherryPicking => ("Mine: main", $"Theirs: {Git("rev-parse", "--short", "other").Trim()} Other changes"),
            GitOperation.Merging => ("Mine: main", "Theirs: other"),
            _ => ("Mine: working tree", "Theirs: stash"),
        };
        Assert.Equal(mineLabel, session.MineLabel);
        Assert.Equal(theirsLabel, session.TheirsLabel);
        MergeRegion.Conflict conflict = Assert.Single(session.Model.Conflicts);
        Assert.Equal([operation == GitOperation.Rebasing ? "other change" : "main change"], conflict.MineLines);
        Assert.Equal([operation == GitOperation.Rebasing ? "main change" :
            operation == GitOperation.None ? "stashed" : "other change"], conflict.TheirsLines);

        conflict.Resolution = new(MergeChoice.Custom, ["resolved by hand"]);
        await session.Complete(session.Model.ToBytes());

        Assert.Equal("resolved by hand\n", await File.ReadAllTextAsync(PathOfFile));
        Assert.Equal("resolved by hand\n", Git("show", $":{Name}"));
        Assert.Empty(GitUnmerged.List(repository));
        Assert.Equal(operation, repository.Operation());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_stage_can_stay_deleted_or_keep_modified_file(bool delete)
    {
        GitRepository repository = Init("base\n");
        Git("switch", "-q", "-c", "other");
        Git("--literal-pathspecs", "rm", "--", Name);
        Git("commit", "-qm", "Delete");
        Git("switch", "-q", "main");
        Commit("modified\n", "Modify");
        Fails("merge", "other");

        GitUnmergedFile index = Assert.Single(GitUnmerged.List(repository));
        Assert.Null(index.Theirs);
        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, PathOfFile));
        MergeSideChoices sides = Assert.IsType<MergeSideChoices>(session.SideChoices);
        Assert.False(sides.MineDeleted);
        Assert.True(sides.TheirsDeleted);
        await sides.Complete(delete ? MergeSide.Theirs : MergeSide.Mine);

        Assert.Empty(GitUnmerged.List(repository));
        if (delete)
        {
            Assert.False(File.Exists(PathOfFile));
            Assert.Equal($"D\t{Name}\n", Git("diff", "--cached", "--name-status"));
        }
        else
        {
            Assert.Equal("modified\n", await File.ReadAllTextAsync(PathOfFile));
            Assert.Equal("modified\n", Git("show", $":{Name}"));
        }
    }

    [Fact]
    public async Task Binary_choice_preserves_raw_bytes_in_file_and_index()
    {
        GitRepository repository = Init("base\n");
        byte[] other = [0, 0xfe, 0xff, 0x0c, 0x0a];
        Git("switch", "-q", "-c", "other");
        File.WriteAllBytes(PathOfFile, other);
        Git("commit", "-qam", "Their binary");
        Git("switch", "-q", "main");
        File.WriteAllBytes(PathOfFile, [0, 3, 254]);
        Git("commit", "-qam", "Our binary");
        Fails("merge", "other");

        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, PathOfFile));
        Assert.True(session.IsBinary);
        Assert.Equal(other, session.Model.BinarySides!.Theirs);
        await Assert.IsType<MergeSideChoices>(session.SideChoices).Complete(MergeSide.Theirs);

        Assert.Equal(other, await File.ReadAllBytesAsync(PathOfFile));
        using var staged = new MemoryStream();
        Assert.True(GitProcess.Run(root, ["show", $":{Name}"], standardOutput: staged).Ok);
        Assert.Equal(other, staged.ToArray());
        Assert.Empty(GitUnmerged.List(repository));
    }

    [Fact]
    public async Task Both_added_has_an_empty_base_and_two_real_versions()
    {
        GitRepository repository = Init("existing file\n");
        const string added = "added.txt";
        string full = Path.Combine(root, added);
        Git("switch", "-q", "-c", "other");
        File.WriteAllText(full, "their addition\n");
        Git("add", "--", added);
        Git("commit", "-qm", "They add");
        Git("switch", "-q", "main");
        File.WriteAllText(full, "my addition\n");
        Git("add", "--", added);
        Git("commit", "-qm", "I add");
        Fails("merge", "other");

        Assert.Null(Assert.Single(GitUnmerged.List(repository)).Base);
        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, full));
        Assert.Single(session.Model.Conflicts).Resolution = new(MergeChoice.BothMineFirst);
        await session.Complete(session.Model.ToBytes());

        Assert.Empty(GitUnmerged.List(repository));
        Assert.Equal("my addition\ntheir addition\n", Git("show", $":{added}"));
        Assert.Equal("my addition\ntheir addition\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task Markers_without_index_stages_can_be_resolved_and_staged()
    {
        GitRepository repository = Init("base\n");
        File.WriteAllText(PathOfFile, "header\n<<<<<<< main\nmy edit\n=======\ntheir edit\n>>>>>>> other\n");
        Assert.Empty(GitUnmerged.List(repository));

        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, PathOfFile));
        Assert.Single(session.Model.Conflicts).Resolution = new(MergeChoice.Theirs);
        await session.Complete(session.Model.ToBytes());

        Assert.Equal("header\ntheir edit\n", Git("show", $":{Name}"));
        Assert.Equal("header\ntheir edit\n", File.ReadAllText(PathOfFile));
    }

    [Fact]
    public async Task Completion_keeps_a_working_file_edited_after_opening()
    {
        GitRepository repository = Init("base\n");
        Diverge();
        Fails("merge", "other");
        MergeSession session = Assert.IsType<MergeSession>(await GitUnmerged.Open(repository, PathOfFile));
        File.WriteAllText(PathOfFile, "newer edit\n");

        await Assert.ThrowsAsync<HexLinkException>(() => session.Complete(Encoding.UTF8.GetBytes("merge result\n")));
        Assert.Equal("newer edit\n", await File.ReadAllTextAsync(PathOfFile));
        Assert.Single(GitUnmerged.List(repository));
    }

    private GitRepository Init(string text)
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "tester@example.com");
        Git("config", "core.autocrlf", "false");
        Commit(text, "Base");
        return Assert.IsType<GitRepository>(GitRepository.Find(root));
    }

    private void Diverge()
    {
        Git("switch", "-q", "-c", "other");
        Commit("other change\n", "Other changes");
        Git("switch", "-q", "main");
        Commit("main change\n", "Main changes");
    }

    private void Commit(string text, string message)
    {
        File.WriteAllText(PathOfFile, text);
        Git("--literal-pathspecs", "add", "--", Name);
        Git("commit", "-qm", message);
    }

    private void Fails(params string[] arguments) => Assert.False(GitProcess.Run(root, arguments).Ok);

    private string Git(params string[] arguments)
    {
        GitResult result = GitProcess.Run(root, arguments);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} said {result.Why}");
        return result.Output;
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
