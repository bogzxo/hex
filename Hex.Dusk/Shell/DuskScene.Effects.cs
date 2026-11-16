using System.Numerics;

using Hex.Core;
using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dusk;

// Dawn's effects in the terminal, the same switch as Dawn's (effects, effect_sparks and effect_glass in the editor's
// section of the settings, one setting for both modes). Subtle is the lights and a burst of sparks when a command that
// took a while went well and the sparks are on, all is sparks off the cursor as you type at a prompt as well. The glass,
// the palette, the menus and the settings over a blur of the terminals behind them, is its own switch whatever the
// effects are. None of it draws a thing while nothing flies and nothing pops up, and the glass keeps its blur for as
// long as nothing under it changed, a laptop on its battery pays for neither.
internal sealed partial class DuskScene
{
    // The editor's section of the settings as it was last read, which is both modes': the effects, the glass and the
    // motion all come out of it, the way Dawn has them straight from its own. Seven copies of what's in it were kept
    // here, each to be remembered whenever one more of them was wanted
    private HexSettings shared = new(null);

    // The same glass as Dawn's popups, as thick as the sliders of the effects say
    private UIGlass popupGlass => HexFrame.PopupGlass(shared);

    // Dawn's sides' glass out of the same sliders, Dusk has no side panels but shows it in the glass's preview
    private UIGlass sideGlass => HexFrame.SideGlass(shared);

    // How long a command has to have run for it going well to be worth a burst, in seconds. Every ls throwing sparks
    // is the power mode's problem, not a feature
    private const float BURST_AFTER = 2.0f;

    // The lights and the sparks and the module over everything that they're in, the frame's (HexFrame)
    private UIModule effectsModule => frame.EffectsModule;
    private Sparks sparks => frame.Sparks;

    private string effects
    {
        get => shared.Effects;
        set => shared.Effects = value;
    }

    private bool effectSparks
    {
        get => shared.EffectSparks;
        set => shared.EffectSparks = value;
    }

    private bool effectGlass
    {
        get => shared.EffectGlass;
        set => shared.EffectGlass = value;
    }

    // How quickly Dusk's own UI moves, the motion's ui_animation, Dawn's too
    private float uiAnimation
    {
        get => shared.UiAnimation;
        set => shared.UiAnimation = value;
    }

    // The pane sparks were thrown off last and where its cursor was, and how many commands had finished in each pane
    private TerminalView? sparkedIn;
    private int sparkedColumn = -1, sparkedRow = -1;
    private TerminalView? finishedIn;
    private int finishedSeen;

    private bool EffectsOn => HexFrame.EffectsOn(shared);

    private void BuildEffects()
    {
        // Nothing behind Dusk's UI but the window, the terminals are the UI, so the picture under the glass is kept
        frame.BuildEffects();
        ReadEffects();
    }

    /// <summary>The effects as the settings file has them now, put in force.</summary>
    private void ReadEffects()
    {
        // The settings can change before there's anything to put them in force on
        if (frame?.Sparks is null)
            return;

        // Read as Dawn reads them, the editor's section is both modes'
        shared = new HexSettings(host.Settings.Path);
        ApplyEffects();
    }

    /// <summary>The effects as they are now, put in force.</summary>
    private void ApplyEffects()
    {
        // The popups' glass, how quickly the UI moves and scrolls, which lights are on and nothing left flying that is
        // switched off: what Dawn does the same way, the frame's. The terminal's animations off is nothing moving, the
        // menus and the buttons as well as the splits and the cursor
        frame.ApplyEffects(shared, host.Settings.Animations ? uiAnimation : 0.0f);

        // The bars on glass over the field of light, or solid, see DuskScene.Materials
        DressChrome();
        HexMotion.Apply(panel, host.Settings, uiAnimation);
    }

    /// <summary>Every tick, sparks off the cursor as it moves along a prompt, and a burst for a long command that went well.</summary>
    private void UpdateEffects()
    {
        // The light, the cursor's glow while a terminal has the keyboard and the pointer's, whenever the effects are on
        if (EffectsOn)
        {
            Vector2? cursor = panel.FocusedView is { } focused && compositor.Focus == focused ? effectsModule.ToLocal(chrome.ToWorld(focused.CursorPoint)) : null;
            frame.UpdateLights(cursor);
        }

        if (!EffectsOn || !effectSparks || panel.FocusedView is not { } view)
        {
            sparkedIn = finishedIn = null;
            return;
        }

        TerminalScreen screen = view.Screen;
        Vector4 colour = compositor.Skin?.AccentColor ?? Vector4.One;

        // Typing moves the cursor along the line it's on, that's what is typed at a prompt and nothing else is
        if (effects == "all" && screen.AtPrompt && compositor.Focus == view)
        {
            if (sparkedIn == view && screen.CursorY == sparkedRow && screen.CursorX != sparkedColumn)
                sparks.Emit(effectsModule.ToLocal(chrome.ToWorld(view.CursorPoint)), 3, colour);

            (sparkedIn, sparkedColumn, sparkedRow) = (view, screen.CursorX, screen.CursorY);
        }
        else
        {
            sparkedIn = null;
        }

        if (finishedIn != view)
        {
            (finishedIn, finishedSeen) = (view, screen.Finished);
        }
        else if (screen.Finished != finishedSeen)
        {
            finishedSeen = screen.Finished;
            if (screen.LastExit == 0 && screen.LastTook >= BURST_AFTER)
                sparks.Burst(effectsModule.ToLocal(chrome.ToWorld(view.CursorPoint)), 36, colour);
        }
    }
}
