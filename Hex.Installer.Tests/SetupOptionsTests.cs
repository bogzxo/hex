using Hex.Installation;

namespace Hex.Installer.Tests;

public sealed class SetupOptionsTests
{
    [Theory]
    [InlineData("--package")]
    [InlineData("--uninstall")]
    public void Cached_packages_are_refused_without_an_update_handoff(string mode)
    {
        string[] args = mode == "--package" ? ["--package", "update.zip"] : ["--uninstall", "--package", "update.zip"];
        Assert.Throws<ArgumentException>(() => SetupOptions.Parse(args));
    }

    [Fact]
    public void Cached_update_preserves_target_version_parent_identity_and_absolute_package_path()
    {
        string root = Directory.CreateTempSubdirectory("hex-options-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, InstallPaths.Manifest), """
                {"product":"bogzxo.Hex","schema":1,"version":"2.5.1","files":[]}
                """);
            var options = SetupOptions.Parse(["--update", "--target", root, "--version", "v2.6.0", "--wait-pid", "123", "--wait-start", "42", "--package", "cached.zip"]);
            Assert.True(options.Update);
            Assert.False(options.Uninstall);
            Assert.Equal(root, options.Target);
            Assert.Equal("v2.6.0", options.Version);
            Assert.Equal(123, options.Parent);
            Assert.Equal(42, options.ParentStart);
            Assert.Equal(Path.GetFullPath("cached.zip"), options.Package);
        }
        finally { Directory.Delete(root, true); }
    }
}
