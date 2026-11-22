using Hex.Core;
using Hex.Core.Git;

namespace Hex.Tests;

public sealed class GitProcessTests
{
    [Fact]
    public void Large_check_ignore_batches_can_read_and_write_at_the_same_time()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        GitResult init = GitProcess.Run(site.Work, ["init", "-q"]);
        Assert.True(init.Ok, init.Why);
        File.WriteAllText(Path.Combine(site.Work, ".gitignore"), "*.ignored\n");
        string[] paths = [.. Enumerable.Range(0, 2048).Select(i => $"{i:0000}-{new string('a', 96)}.ignored")];
        string input = string.Join('\0', paths) + "\0";

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        GitResult result = GitProcess.Run(site.Work, ["check-ignore", "--stdin", "-z"], input,
            patience: TimeSpan.FromSeconds(10), cancel: cancel.Token);

        Assert.True(result.Ok, result.Why);
        Assert.Equal(input, result.Output);
    }
}
