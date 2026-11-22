using Hex.Core;
using Hex.Core.Git;
using Hex.Shell;

namespace Hex.Tests;

[Collection("Git integration preference")]
public sealed class GitSupportTests
{
    [Fact]
    public void DefaultIsOnAndAnExplicitOffSurvivesReload()
    {
        var folder = Directory.CreateTempSubdirectory("hex-git-setting-");
        try
        {
            string path = Path.Combine(folder.FullName, "settings.hor");
            var settings = new HexSettings(path);
            Assert.True(settings.GitEnabled);
            Assert.False(HexSettings.IsWorkspaceKey(EditorSettingsCatalog.GIT_ENABLED));
            settings.GitEnabled = false;
            Assert.Null(settings.Save());

            var reloaded = new HexSettings(path);
            Assert.False(reloaded.GitEnabled);
            reloaded.GitEnabled = true;
            Assert.Null(reloaded.Save());
            Assert.True(new HexSettings(path).GitEnabled);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void OffSkipsProbeDiscoveryAndProcessStartUntilReenabled()
    {
        bool before = GitProcess.Enabled;
        try
        {
            GitProcess.SetEnabled(false);
            Assert.False(GitProcess.InstalledNow);
            Assert.False(GitProcess.Installed);
            Assert.False(GitProcess.ProbeStarted);
            Assert.Null(GitRepository.Find("\0"));
            Assert.Contains("turned off", GitProcess.Run(Environment.CurrentDirectory, ["--version"]).Why);

            GitProcess.SetEnabled(true);
            _ = GitProcess.InstalledNow;
            Assert.True(GitProcess.ProbeStarted);
        }
        finally
        {
            GitProcess.SetEnabled(before);
        }
    }
}

[CollectionDefinition("Git integration preference", DisableParallelization = true)]
public sealed class GitSupportCollection;
