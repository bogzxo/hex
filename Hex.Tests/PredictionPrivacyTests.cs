using Hex.Core;

namespace Hex.Tests;

/// <summary>Which files may be sent to a model: none that hold secrets, none outside the folders open, none over a megabyte.</summary>
public sealed class PredictionPrivacyTests
{
    private static readonly string root = Path.Combine(Path.GetTempPath(), "hex-predict-privacy", "project");

    private static string In(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Theory]
    [InlineData(".env")]
    [InlineData("web/.env.local")]
    [InlineData("certs/server.pem")]
    [InlineData("certs/server.KEY")]
    [InlineData("deploy/site.pfx")]
    [InlineData("deploy/site.p12")]
    [InlineData("home/id_rsa.pub")]
    [InlineData("home/id_ed25519")]
    [InlineData("config/client_secret.json")]
    [InlineData("config/Credentials.xml")]
    [InlineData(".npmrc")]
    [InlineData("tools/.pypirc")]
    [InlineData(".netrc")]
    [InlineData("vault.kdbx")]
    public void The_usual_patterns_keep_secrets_back(string relative)
    {
        var privacy = new PredictionPrivacy(PredictionPrivacy.UsualExclude, [root]);

        Assert.False(privacy.Allowed(In(relative), 100));
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("src/keyboard.cs")]
    public void Ordinary_files_are_sent(string relative)
    {
        var privacy = new PredictionPrivacy(PredictionPrivacy.UsualExclude, [root]);

        Assert.True(privacy.Allowed(In(relative), 100));
    }

    [Fact]
    public void A_folder_adds_patterns_files_and_folders_alike()
    {
        var privacy = new PredictionPrivacy([.. PredictionPrivacy.UsualExclude, "generated/", "**/*.local.json"], [root]);

        Assert.False(privacy.Allowed(In("generated/Api.cs"), 100));
        Assert.False(privacy.Allowed(In("src/generated/deep/Api.cs"), 100));
        Assert.False(privacy.Allowed(In("config/app.local.json"), 100));
        Assert.True(privacy.Allowed(In("src/Api.cs"), 100));
    }

    [Fact]
    public void Files_outside_the_folders_open_and_large_files_are_never_sent()
    {
        var privacy = new PredictionPrivacy(PredictionPrivacy.UsualExclude, [root]);

        Assert.False(privacy.Allowed(Path.Combine(Path.GetTempPath(), "hex-predict-privacy", "settings.hor"), 100));
        Assert.False(privacy.Allowed(root + "2" + Path.DirectorySeparatorChar + "a.cs", 100));
        Assert.False(privacy.Allowed(In("src/big.cs"), PredictionPrivacy.MOST_BYTES + 1));
        Assert.True(privacy.Allowed(In("src/big.cs"), PredictionPrivacy.MOST_BYTES));
        Assert.False(new PredictionPrivacy([], []).Allowed(In("src/a.cs"), 1));
    }
}
