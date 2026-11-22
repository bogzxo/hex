using Hex.Core;

namespace Hex.Tests;

/// <summary>Which program folders are a macOS app's, the one Hex writes nothing into.</summary>
public sealed class HexBundleTests
{
    [Theory]
    [InlineData("/Applications/Hex.app/Contents/MacOS/", "/Applications/Hex.app")]
    [InlineData("/Users/a/Hex Dev.app/Contents/MacOS", "/Users/a/Hex Dev.app")]
    public void AnAppsMacOSFolderHasItsBundle(string folder, string app)
    {
        Assert.Equal(app, HexBundle.RootOf(folder));
        Assert.True(HexBundle.IsBundleProgram(folder));
    }

    [Theory]
    [InlineData("/usr/local/bin/")]
    [InlineData("/Applications/Hex.app/Contents/Resources/")]
    [InlineData("/Applications/Hex/Contents/MacOS/")]
    [InlineData("/tmp/MacOS/")]
    public void AnyOtherFolderIsNotOne(string folder)
    {
        Assert.Null(HexBundle.RootOf(folder));
        Assert.False(HexBundle.IsBundleProgram(folder));
    }
}
