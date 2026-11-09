using System.Numerics;
using Hex.Dawn;

namespace Hex.Tests;

public class CanvasResizeTests
{
    [Theory]
    [InlineData(0.5f, 1.5f, 2f)]
    [InlineData(1.5f, 0.5f, 0.5f)]
    [InlineData(1f, 1f, 3f)]
    public void ResizeKeepsLayoutPointAtViewportCentre(float before, float after, float zoom)
    {
        var pan = new Vector2(120, -75);
        Vector2 pointBefore = -pan / (before * zoom);
        Vector2 resized = HexDesigner.ResizeCanvasPan(pan, before, after);
        Vector2 pointAfter = -resized / (after * zoom);
        Assert.True(Vector2.Distance(pointBefore, pointAfter) < 0.0001f);
        Assert.True(Vector2.Distance(pan, HexDesigner.ResizeCanvasPan(resized, after, before)) < 0.0001f);
    }

    [Fact]
    public void EmptyViewportDoesNotDestroyPan()
    {
        var pan = new Vector2(120, -75);
        Assert.Equal(pan, HexDesigner.ResizeCanvasPan(pan, 1, 0));
        Assert.Equal(pan, HexDesigner.ResizeCanvasPan(pan, 0, 1));
    }
}
