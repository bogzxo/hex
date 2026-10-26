using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>
/// The GitHub account setting, asked of git itself, so a credential helper with more than one account is told which and
/// doesn't ask every time.
/// </summary>
public sealed class GitAccountTests
{
    [Fact]
    public void Git_is_told_the_account_and_nothing_without_one()
    {
        if (!GitProcess.Installed)
            return;

        string key = "credential.https://github.com.username";
        string before = GitProcess.Account;
        try
        {
            GitProcess.Account = "  someone  ";
            Assert.Equal("someone", GitProcess.Run(Path.GetTempPath(), ["config", "--get", key]).Output.Trim());

            // Unattended as well, and the helper told not to put anything up
            Assert.Equal("false", GitProcess.Run(Path.GetTempPath(), ["config", "--get", "credential.interactive"], unattended: true).Output.Trim());

            GitProcess.Account = string.Empty;
            Assert.DoesNotContain("someone", GitProcess.Run(Path.GetTempPath(), ["config", "--get", key]).Output);
        }
        finally
        {
            GitProcess.Account = before;
        }
    }
}
