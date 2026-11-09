using Hex.Dawn;

namespace Hex.Tests;

public sealed class LanguagePrerequisiteTests
{
    [Theory]
    [InlineData("npm install -g pyright", true)]
    [InlineData("  npm\tupdate -g pyright", true)]
    [InlineData("npm.cmd install -g typescript-language-server", true)]
    [InlineData("dotnet tool install -g csharp-ls", false)]
    [InlineData("npm-check install", false)]
    [InlineData("", false)]
    public void MissingNpmHintOnlyAppliesToNpmCommands(string command, bool expected)
    {
        Assert.Equal(expected, HexLanguages.MissingNpm(command, _ => null));
        Assert.False(HexLanguages.MissingNpm(command, _ => "npm.cmd"));
    }
}
