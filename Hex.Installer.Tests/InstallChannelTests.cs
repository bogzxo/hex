using System.IO.Compression;
using System.Text.Json.Nodes;
using Hex.Installation;

namespace Hex.Installer.Tests;

/// <summary>
/// The stable release and the development branch's nightly build, told apart by their releases, their folders, their
/// entries on the system and how each knows a newer one.
/// </summary>
public sealed class InstallChannelTests
{
    private static JsonObject Release(string tag, bool prerelease, string title = "") => new()
    {
        ["tag_name"] = tag, ["name"] = title, ["draft"] = false, ["prerelease"] = prerelease,
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = "Hex-win-x64.zip", ["size"] = 1234,
            ["browser_download_url"] = $"https://github.com/bogzxo/hex/releases/download/{tag}/Hex-win-x64.zip",
            ["digest"] = "sha256:" + new string('a', 64)
        })
    };

    [Fact]
    public void The_newest_nightly_is_the_one_built_by_the_latest_run()
    {
        var list = new JsonArray(Release("v3.1.0", false), Release("nightly-41", true, "Hex 3.1.0 nightly 41"),
            Release("v3.2.0-rc.1", true), Release("nightly-57", true, "Hex 3.1.0 nightly 57 (abc1234)"), Release("nightly-49", true));
        ReleaseAsset newest = ReleaseClient.ParseNightly(list.ToJsonString(), "win-x64");
        Assert.Equal("nightly-57", newest.Version);
        Assert.Equal(InstallChannel.Dev, newest.Channel);
        Assert.Equal("3.1.0 nightly 57 (abc1234)", newest.Shown);
    }

    [Fact]
    public void Neither_channel_takes_the_others_releases()
    {
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release("nightly-3", true).ToJsonString(), "win-x64"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release("v3.1.0", false).ToJsonString(), "win-x64", InstallChannel.Dev));
        // A release candidate is a prerelease but not a nightly
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release("v3.2.0-rc.1", true).ToJsonString(), "win-x64", InstallChannel.Dev));
        Assert.Throws<InvalidOperationException>(() => ReleaseClient.ParseNightly(new JsonArray(Release("v3.1.0", false)).ToJsonString(), "win-x64"));
    }

    [Fact]
    public void A_newer_one_is_a_later_run_for_dev_and_a_higher_version_for_stable()
    {
        Assert.True(InstallChannels.IsNewer(InstallChannel.Dev, "nightly-12", "nightly-9"));
        Assert.False(InstallChannels.IsNewer(InstallChannel.Dev, "nightly-9", "nightly-12"));
        Assert.False(InstallChannels.IsNewer(InstallChannel.Dev, "nightly-12", "nightly-12"));
        Assert.True(InstallChannels.IsNewer(InstallChannel.Stable, "v3.1.0", "v3.0.0"));
        Assert.False(InstallChannels.IsNewer(InstallChannel.Stable, "v3.0.0", "v3.1.0"));
    }

    [Fact]
    public void Each_channel_has_a_folder_and_names_of_its_own()
    {
        Assert.NotEqual(InstallChannels.DefaultRoot(InstallChannel.Stable), InstallChannels.DefaultRoot(InstallChannel.Dev));
        Assert.Equal("Hex Dev", InstallChannels.Names(InstallChannel.Dev).Name);
        Assert.Equal("hex-dev", InstallChannels.Names(InstallChannel.Dev).Prefix);
        // The texts aren't read in a test, the name is whatever they say with Dev after it
        Assert.Matches(@"Name=[^\n]* Dev\n", PlatformRegistration.DesktopEntry("/opt/hex-dev", channel: InstallChannel.Dev));
        Assert.Contains("com.bogzxo.hex.dev", PlatformRegistration.MacPlist("nightly-3", InstallChannel.Dev));
    }

    [Fact]
    public void An_installation_remembers_its_channel()
    {
        string home = Directory.CreateTempSubdirectory("hex-channel-test-").FullName;
        try
        {
            string zip = Path.Combine(home, "package.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string name in new[] { InstallPaths.Executable, "installer/" + InstallPaths.InstallerExecutable })
                    using (var writer = new StreamWriter(archive.CreateEntry("Hex/" + name).Open()))
                        writer.Write(name);
            string root = Path.Combine(home, "Hex Dev"), stage = root + ".stage";
            InstallTransaction.Prepare(zip, stage, root, "nightly-7", channel: InstallChannel.Dev);
            InstallTransaction.Commit(stage, root, () => { });
            Assert.Equal(InstallChannel.Dev, InstallChannels.Of(root));
            Assert.Equal(InstallChannel.Stable, InstallChannels.Of(Path.Combine(home, "nothing here")));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void The_command_line_picks_the_dev_build_and_its_folder()
    {
        var options = SetupOptions.Parse(["--channel", "dev", "--preview"]);
        Assert.Equal(InstallChannel.Dev, options.Channel);
        Assert.Equal(InstallChannels.DefaultRoot(InstallChannel.Dev), options.Target);
        Assert.False(options.TargetGiven);
        Assert.Equal(InstallChannel.Stable, SetupOptions.Parse(["--preview"]).Channel);
    }
}
