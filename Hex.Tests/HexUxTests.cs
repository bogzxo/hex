using Hex.Core;
using Hex.Shell;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Tests;

public class HexUxTests
{
    [Fact]
    public void Shared_motion_off_and_terminal_motion_off_both_disable_pane_animation_without_changing_preferences()
    {
        using var panel = new TerminalPanel();
        var terminal = new TerminalSettings(null) { Animations = true, AnimationTime = 0.16f, CursorGlide = 0.06f };
        HexMotion.Apply(panel, terminal, 0);
        Assert.False(panel.Animated);
        Assert.Equal(0, panel.CursorGlide);
        Assert.True(terminal.Animations);
        Assert.Equal(0.16f, terminal.AnimationTime);
        HexMotion.Apply(panel, terminal, 2);
        Assert.True(panel.Animated);
        Assert.Equal(0.08f, panel.AnimationTime);
        Assert.Equal(0.06f, panel.CursorGlide);
        terminal.Animations = false;
        HexMotion.Apply(panel, terminal, 1);
        Assert.False(panel.Animated);
        Assert.Equal(0, panel.CursorGlide);
    }

    [Fact]
    public void New_settings_match_the_motion_free_flat_look_and_reset_values()
    {
        var settings = new HexSettings(null);
        Assert.Equal(HexLooks.FLAT, HexLooks.Current(settings, "")?.Name);
        Assert.False(settings.EffectGlass);
        Assert.False(settings.EffectMotion);
        Assert.Equal(0, settings.UiAnimation);
        Assert.Equal(0, settings.CaretGlide);
        foreach (string key in HexLooks.Flat.Values.Keys)
            Assert.True(SettingValues.Same(HexLooks.Get(settings, key, ""), EditorSettingsCatalog.Usual.GetValueOrDefault(key)), key);
    }

    [Fact]
    public void Flat_clears_effects_and_motion_after_an_animated_look()
    {
        var settings = new HexSettings(null);
        HexLooks.Apply(HexLooks.Fluid, settings);
        settings.EffectSparks = settings.EffectTrail = settings.EffectRipples = true;
        HexLooks.Apply(HexLooks.Flat, settings);
        Assert.Equal(HexLooks.FLAT, HexLooks.Current(settings, "")?.Name);
        Assert.False(settings.EffectSparks || settings.EffectTrail || settings.EffectRipples || settings.EffectCaretGlow);
        Assert.Equal(0, settings.UiAnimation);
        Assert.Equal(0, settings.CaretGlide);
    }

    [Fact]
    public void Saved_appearance_survives_the_new_install_defaults()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-ux-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.hor");
            var original = new HexSettings(path);
            HexLooks.Apply(HexLooks.Fluid, original);
            original.UiAnimation = 1.5f;
            original.CaretGlide = 0.08f;
            Assert.Null(original.Save());
            var restored = new HexSettings(path);
            Assert.True(restored.EffectGlass && restored.EffectMotion);
            Assert.Equal(1.5f, restored.UiAnimation);
            Assert.Equal(0.08f, restored.CaretGlide);
            Assert.Equal(HexLooks.FLUID, restored.Look);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(HexMode.Dawn)]
    [InlineData(HexMode.Dusk)]
    public void Mode_selector_marks_current_and_only_destination_switches(HexMode current)
    {
        var bar = new TitleBar();
        int switched = 0;
        Button destination = HexModeSwitch.Add(bar, current, () => switched++);
        var row = Assert.IsType<StackPanel>(Assert.Single(bar.Children));
        Assert.Equal(Horizon.Rendering.Origin.Right, row.Anchor);
        var buttons = row.Children.Cast<Button>().ToArray();
        Assert.Equal(new[] { "Dawn", "Dusk" }, buttons.Select(button => button.Label));
        Button active = Assert.Single(buttons, button => button.Selected);
        Assert.Equal(current.ToString(), active.Label);
        Assert.Null(active.OnPressed);
        Assert.False(destination.Selected);
        destination.OnPressed!();
        Assert.Equal(1, switched);
    }
}
