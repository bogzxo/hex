using System.Numerics;

namespace Hex.Installer.Tests;

public class InstallerLayoutTests
{
    [Theory]
    [InlineData(1f)][InlineData(1.25f)][InlineData(1.5f)][InlineData(2f)][InlineData(3f)]
    public void NativeSizeRetainsMonitorScale(float dpi)
    {
        float fit = InstallerLayout.Fit(new Vector2(1000, 680) * dpi, dpi);
        Assert.Equal(1, fit);
        Assert.Equal(dpi, fit * dpi);
    }

    [Theory]
    [InlineData(1f)][InlineData(1.5f)][InlineData(2f)]
    public void SmallWindowFitsInLogicalUnits(float dpi)
    {
        var logical = new Vector2(700, 500);
        float fit = InstallerLayout.Fit(logical * dpi, dpi);
        Assert.True(904 * fit <= logical.X);
        Assert.True(520 * fit <= logical.Y);
        Assert.Equal(InstallerLayout.Fit(logical, 1), fit);
    }

    [Fact]
    public void ChangingMonitorDoesNotApplyDpiTwice()
    {
        Assert.Equal(InstallerLayout.Fit(new(1000, 680), 1), InstallerLayout.Fit(new(2000, 1360), 2));
        Assert.True(InstallerLayout.Fit(new(1000, 680), 2) < 1);
        Assert.Equal(1, InstallerLayout.Fit(Vector2.Zero, 2));
    }
}
