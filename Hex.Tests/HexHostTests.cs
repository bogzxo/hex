using System.Numerics;

using Hex.Core;
using Hex.Dawn;

using Horizon.Core;
using Horizon.Engine;

namespace Hex.Tests;

/// <summary>
/// Hex as a host other than Hex.exe starts it, Hex.Android's activity. The desktop keeps every capability as it had
/// them, and the editor's settings go over the host's without losing the host's screen.
/// </summary>
public class HexHostTests
{
    [Fact]
    public void ADesktopHexCanDoEverything()
    {
        Assert.True(HexHost.Processes);
        Assert.True(HexHost.RunsDownloads);
        Assert.True(HexHost.Pipes);
        Assert.True(HexHost.NativeDialogs);
        Assert.Null(HexHost.NoProcesses("git"));
        Assert.Null(HexHost.NoDownloads("clangd"));
        Assert.Equal(AppContext.BaseDirectory, HexHost.ProgramFolder);
    }

    [Fact]
    public void TheHostedEditorKeepsTheHostsScreenAndIsAnApplication()
    {
        var host = new GameEngineConfiguration
        {
            WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
            {
                WindowSize = new Vector2(2560, 1600),
                Fullscreen = true,
                WindowTitle = "Horizon",
                Mode = RunMode.Game
            }
        };

        GameEngineConfiguration hosted = DawnProgram.Hosted(host);

        Assert.False(hosted.Gamepads);
        Assert.Equal(new Vector2(2560, 1600), hosted.WindowConfiguration.WindowSize);
        Assert.True(hosted.WindowConfiguration.Fullscreen);
        Assert.Equal("Hex", hosted.WindowConfiguration.WindowTitle);
        Assert.Equal(RunMode.Application, hosted.WindowConfiguration.Mode);
        Assert.Equal(ApplicationBehaviour.All, hosted.WindowConfiguration.Behaviour);
        Assert.True(hosted.WindowConfiguration.VSync);
    }
}
