using Hex.Dawn;

namespace Hex.Tests;

/// <summary>The filter over Source Control's changes (HexGit.Filter), by a file's folder and name, either slash, any case.</summary>
public sealed class GitChangesFilterTests
{
    [Theory]
    [InlineData("", @"AutoVoxel\Program.cs", true)]
    [InlineData("program", @"AutoVoxel\Program.cs", true)]
    [InlineData("autovoxel/prog", @"AutoVoxel\Program.cs", true)]
    [InlineData(@"AutoVoxel\Prog", "AutoVoxel/Program.cs", true)]
    [InlineData("android", @"Horizon.Platform.Desktop\Hosting.cs", false)]
    public void APathIsLetThroughByAnyPartOfIt(string filter, string relative, bool through)
    {
        Assert.Equal(through, HexGit.Matches(filter, relative, null));
    }

    [Fact]
    public void ARenamedFileIsFoundByItsOldNameToo()
    {
        Assert.True(HexGit.Matches("old", "src/New.cs", "Old.cs"));
        Assert.False(HexGit.Matches("old", "src/New.cs", null));
    }
}
