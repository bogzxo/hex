using Horizon.Platform;
using Horizon.Rendering.Scaling;

namespace Hex.Dawn;

// How many pixels Hex is drawn with, the engine's render scale (WindowManager.Scaling) as Appearance, Rendering has it.
// Put in force as the editor comes up and whenever a row of it changes, the next frame takes it. Auto is whatever the
// platform would pick by itself, the screen's density and size on a tablet and every pixel on a desktop, so a desktop
// Hex that never touches it draws exactly as it did. HORIZON_RENDER_SCALE and the rest of the engine's settings still
// win over the file, they're what a test or a person running it asked for.
internal sealed partial class DawnScene
{
    // Set on the render thread when the engine works out a new plan (a new size, a new setting), taken on the next tick
    private volatile bool renderPlanChanged;
    private string? renderFallbackSaid;

    /// <summary>Puts the settings' render scale in force and has the Render scale now row kept up with what it comes to.</summary>
    private void BuildRenderScale()
    {
        Engine.WindowManager.Scaling.PlanChanged += _ => renderPlanChanged = true;
        ApplyRenderScale();
    }

    /// <summary>The render scale as the settings have it now, from the next frame on.</summary>
    internal void ApplyRenderScale()
    {
        RenderScaleSettings wanted = RenderScaleOf(settings, Engine.WindowManager.Features);

        var problems = new List<string>();
        wanted = RenderScaleEnvironment.Apply(wanted, System.Environment.GetEnvironmentVariable, problems);
        foreach (string problem in problems)
            Horizon.Logging.Log.Warning("[Hex] " + problem);

        Engine.WindowManager.Scaling.Settings = wanted;
        renderPlanChanged = true;
    }

    /// <summary>What the settings' rows make of the engine's render scale on a platform.</summary>
    internal static RenderScaleSettings RenderScaleOf(HexSettings settings, PlatformFeatures features)
    {
        RenderScaleSettings policy = settings.RenderScale switch
        {
            "native" => RenderScaleSettings.Native,
            "manual" => RenderScaleSettings.At(settings.RenderScalePercent / 100.0f),
            _ => (features & PlatformFeatures.AutomaticRenderScale) != 0 ? RenderScaleSettings.Auto : RenderScaleSettings.Native
        };

        return policy with
        {
            Upscaler = settings.RenderUpscaler switch
            {
                "fsr1" => Upscaler.Fsr1,
                "gsr1" => Upscaler.Gsr1,
                "bilinear" => Upscaler.Bilinear,
                "nearest" => Upscaler.Nearest,
                _ => Upscaler.Cas
            },
            Sharpness = settings.RenderSharpness,
            Composition = settings.RenderComposition == "whole_frame" ? RenderComposition.WholeFrame : RenderComposition.NativeOverlay
        };
    }

    /// <summary>What the render scale came to, for the Render scale now row.</summary>
    internal string DescribeRenderScale() => DescribeRenderScale(Engine.WindowManager.Scaling.Plan);

    internal static string DescribeRenderScale(RenderScalePlan plan)
    {
        if (plan.OutputWidth == 0)
            return "Not drawn yet";

        // Short enough for the row's box, the numbers first. The engine's log line (FrameScaler.Describe) has the rest
        string what = plan.Composition == RenderComposition.WholeFrame ? "whole frame" : "interface native";
        string fallback = plan.Fallback is { Length: > 0 } why ? $", {why}" : string.Empty;
        if (!plan.Active)
            return $"100%, {plan.OutputWidth}×{plan.OutputHeight}, every pixel{fallback}";

        // Past what CAS can do in one go the engine stretches it first, and says why when it fell back to another
        string stretched = plan.HasPrepass ? $" after {plan.PrepassWidth}×{plan.PrepassHeight}" : string.Empty;
        return $"{plan.EffectiveScale * 100.0f:0}%, {plan.SceneWidth}×{plan.SceneHeight} → {plan.OutputWidth}×{plan.OutputHeight}, {plan.Effective}{stretched}, {what}{fallback}";
    }

    /// <summary>Every tick, the Render scale now row put right when the engine worked out something new.</summary>
    private void UpdateRenderScale()
    {
        if (!renderPlanChanged)
            return;

        renderPlanChanged = false;
        SettingsPage?.Refresh("render_scale_now");

        // Missing shaders must never rewrite the saved choice. The engine picks a usable pass before drawing,
        // and the editor explains what happened once, even if the settings page has never been opened.
        RenderScalePlan plan = Engine.WindowManager.Scaling.Plan;
        string? fallback = plan.Fallback;
        if (!string.IsNullOrEmpty(fallback) && fallback != renderFallbackSaid)
            Notify(Said("settings_catalog.render_fallback", ("why", fallback)),
                actions: (Said("settings_catalog.render_fallback_settings"), () => OpenSettings(EditorSettingsCatalog.RENDERING)));
        renderFallbackSaid = fallback;
    }
}
