using Hex.Shell;

namespace Hex.Tests;

/// <summary>What Dawn and Dusk share, which mode a start is and one of a mode at a time.</summary>
public class HexShellTests
{
    [Theory]
    [InlineData(new[] { "--dusk" }, "Hex.exe", HexMode.Dusk)]
    [InlineData(new[] { "--DUSK", "--size", "800x600" }, "Hex.exe", HexMode.Dusk)]
    [InlineData(new string[0], "Dusk.exe", HexMode.Dusk)]
    [InlineData(new string[0], "dusk", HexMode.Dusk)]
    [InlineData(new[] { "--open", "a.hor" }, "Hex.exe", HexMode.Dawn)]
    public void A_start_is_dusk_when_it_says_so_or_is_called_dusk(string[] args, string exe, HexMode mode) =>
        // In a folder written the way this system writes one, a backslash is no separator on Linux or macOS
        Assert.Equal(mode, HexIdentity.ModeOf(args, Path.Combine(Path.GetTempPath(), "apps", exe)));

    [Fact]
    public void The_two_modes_are_two_programs_to_windows()
    {
        Assert.NotEqual(HexIdentity.AppIdOf(HexMode.Dawn), HexIdentity.AppIdOf(HexMode.Dusk));
        Assert.NotEqual(HexInstance.PipeOf(HexMode.Dawn), HexInstance.PipeOf(HexMode.Dusk));
    }

    [Fact]
    public void A_second_start_tells_the_first_and_the_first_takes_it()
    {
        // a pipe of the test's own, so it never reaches a Dusk somebody has open
        string pipe = $"hex-test-{Guid.NewGuid():N}";
        Assert.False(HexInstance.Tell(pipe, "tab|nowhere|"));

        using var first = new HexInstance(pipe);
        string? heard = null;
        for (int tries = 0; tries < 40 && heard is null; tries++)
        {
            if (HexInstance.Tell(pipe, @"tab|C:\work|cmd"))
            {
                for (int wait = 0; wait < 40 && !first.TryTake(out heard!); wait++)
                    Thread.Sleep(10);
            }
            else
            {
                Thread.Sleep(10);
            }
        }

        Assert.Equal(@"tab|C:\work|cmd", heard);
    }
}
