using Hex.Core;
using Hex.Dawn;
using Hex.Shell;

using Horizon.Platform;
using Horizon.Rendering.Scaling;

namespace Hex.Tests;

/// <summary>
/// Hex's render scale, Appearance, Rendering. Auto is whatever the platform does by itself, every pixel on a desktop and
/// the engine's own policy where it has one, nothing worked out in Hex. What's picked is kept in settings.hor.
/// </summary>
public class HexRenderScaleTests
{
    [Fact]
    public void AutoOnADesktopIsEveryPixelWithTheInterfaceNative()
    {
        RenderScaleSettings made = DawnScene.RenderScaleOf(new HexSettings(null), PlatformFeatures.None);

        Assert.Equal(RenderScalePolicy.Native, made.Policy);
        Assert.Equal(RenderComposition.NativeOverlay, made.Composition);
        Assert.Equal(Upscaler.Cas, made.Upscaler);
    }

    [Fact]
    public void AutoWhereThePlatformScalesIsTheEnginesOwnPolicy()
    {
        RenderScaleSettings made = DawnScene.RenderScaleOf(new HexSettings(null), PlatformFeatures.AutomaticRenderScale);

        Assert.Equal(RenderScalePolicy.Automatic, made.Policy);
        Assert.Equal(RenderComposition.NativeOverlay, made.Composition);
    }

    [Fact]
    public void ManualIsTheSharePickedAndTheRestAsSet()
    {
        var settings = new HexSettings(null)
        {
            RenderScale = "manual",
            RenderScalePercent = 50.0f,
            RenderUpscaler = "nearest",
            RenderSharpness = 0.4f,
            RenderComposition = "whole_frame"
        };

        RenderScaleSettings made = DawnScene.RenderScaleOf(settings, PlatformFeatures.AutomaticRenderScale);

        Assert.Equal(RenderScalePolicy.Manual, made.Policy);
        Assert.Equal(0.5f, made.Scale);
        Assert.Equal(Upscaler.Nearest, made.Upscaler);
        Assert.Equal(0.4f, made.Sharpness);
        Assert.Equal(RenderComposition.WholeFrame, made.Composition);
    }

    [Theory]
    [InlineData("fsr1", Upscaler.Fsr1)]
    [InlineData("gsr1", Upscaler.Gsr1)]
    [InlineData("bilinear", Upscaler.Bilinear)]
    [InlineData("cas", Upscaler.Cas)]
    public void EveryUpscalerPickedIsTheEnginesOwn(string picked, Upscaler upscaler)
    {
        var settings = new HexSettings(null) { RenderUpscaler = picked };

        Assert.Equal(upscaler, DawnScene.RenderScaleOf(settings, PlatformFeatures.AutomaticRenderScale).Upscaler);
    }

    [Fact]
    public void NativeIsNativeEvenWhereThePlatformWouldScale()
    {
        var settings = new HexSettings(null) { RenderScale = "native" };

        Assert.Equal(RenderScalePolicy.Native, DawnScene.RenderScaleOf(settings, PlatformFeatures.AutomaticRenderScale).Policy);
    }

    [Fact]
    public void TheRenderScaleIsKeptInTheSettingsFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-render-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.hor");
            var original = new HexSettings(path)
            {
                RenderScale = "manual",
                RenderScalePercent = 60.0f,
                RenderUpscaler = "bilinear",
                RenderSharpness = 0.25f,
                RenderComposition = "whole_frame"
            };
            Assert.Null(original.Save());

            var restored = new HexSettings(path);
            Assert.Equal("manual", restored.RenderScale);
            Assert.Equal(60.0f, restored.RenderScalePercent);
            Assert.Equal("bilinear", restored.RenderUpscaler);
            Assert.Equal(0.25f, restored.RenderSharpness);
            Assert.Equal("whole_frame", restored.RenderComposition);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ARenderScaleNeverSetIsAuto()
    {
        Assert.Equal("auto", new HexSettings(null).RenderScale);
        Assert.Equal("auto", ((Horizon.HIDL.Runtime.StringValue)EditorSettingsCatalog.Usual["render_scale"]).Value);
        Assert.Equal("native_ui", ((Horizon.HIDL.Runtime.StringValue)EditorSettingsCatalog.Usual["render_composition"]).Value);
    }

    [Fact]
    public void NativeRecoveryStillExplainsTheUnavailableUpscaler()
    {
        RenderScalePlan recovered = RenderScalePlan.NativeFor(2560, 1600) with
        {
            Requested = Upscaler.Fsr1,
            Fallback = "FSR 1 is unavailable; drawing at native resolution"
        };

        string shown = DawnScene.DescribeRenderScale(recovered);
        Assert.Contains("100%", shown);
        Assert.Contains(recovered.Fallback, shown);
    }

    [Fact]
    public void FallbackShowsTheActualUpscalerAndPreservesTheRequestedPreference()
    {
        var settings = new HexSettings(null) { RenderScale = "manual", RenderScalePercent = 50, RenderUpscaler = "fsr1" };
        RenderScalePlan recovered = RenderScalePlan.For(DawnScene.RenderScaleOf(settings, PlatformFeatures.None), 2560, 1600, 1, 4096) with
        {
            Effective = Upscaler.Bilinear,
            Fallback = "FSR 1 is unavailable"
        };

        Assert.Contains("Bilinear", DawnScene.DescribeRenderScale(recovered));
        Assert.Contains(recovered.Fallback, DawnScene.DescribeRenderScale(recovered));
        Assert.Equal("fsr1", settings.RenderUpscaler);
    }
}
