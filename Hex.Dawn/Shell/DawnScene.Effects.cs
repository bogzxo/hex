using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The effects (effects in the settings, none, subtle or all, and a switch each under it). Subtle is the lights, the
// glow under the caret and whichever others are switched on, and a burst of sparks on a save when the sparks are, all is
// sparks off the caret as it types as well, the power mode people install on purpose. The glass and the caret's glide
// are what Hex is made of rather than effects, so they have their own switches and are on as it comes (the Fluid Glass
// look). The glass is the dialogs, the menus, the palette and the side panels over a blur of whatever is behind them,
// Apple's Liquid Glass more or less, the compositor drawing in goes to have it (UIGlass in the engine). The sparks are a
// module of their own over everything that the pointer goes through, a pool of quads that makes no garbage, and with
// nothing flying nothing is drawn, so an idle window stays as idle as it was.
internal sealed partial class DawnScene
{
    // How long the caret glides for, about two thirds of the way to where it went in this long, the motion's caret_glide
    private float CaretGlide => Motion && settings.UiAnimation > 0.0f ? settings.CaretGlide : 0.0f;

    // How the glass looks, as thick as the four sliders of the effects say. The popups keep most of their colour so the
    // text on them reads, the sides a quieter glass out of the same (HexGlass), there's nothing on most of them but the tree
    private UIGlass PopupGlass => HexGlass.Popup(settings.GlassBlur, settings.GlassClarity, settings.GlassRim, settings.GlassBend);
    private UIGlass SideGlass => HexGlass.Side(settings.GlassBlur, settings.GlassClarity, settings.GlassRim, settings.GlassBend);

    private UIModule effectsModule = null!;
    private Sparks sparks = null!;
    private Lights lights = null!;
    private readonly HexLightEffects lightEffects = new();

    // the version of the code that was last thrown sparks off, so a tick that typed nothing throws none
    private int sparkedVersion = -1;
    private CodeEditor? sparkedIn;

    private bool EffectsOn => settings.Effects is "subtle" or "all";

    /// <summary>Whether the caret glides, its own switch whatever the effects are.</summary>
    private bool Motion => settings.EffectMotion;

    /// <summary>Whether a save throws a burst.</summary>
    private bool SaveSparks => EffectsOn && settings.EffectSparks;

    /// <summary>Whether the dialogs, menus, palette and sides are on glass, its own switch whatever the effects are.</summary>
    private bool Glassy => settings.EffectGlass;

    /// <summary>Whether typing throws sparks off the caret.</summary>
    private bool TypingSparks => settings.Effects == "all" && settings.EffectSparks;

    private void BuildEffects()
    {
        effectsModule = compositor.CreateModule();
        effectsModule.ShowInLayoutDebugger = false;
        effectsModule.Interactive = false;
        // the light under, the sparks over it
        lights = effectsModule.Root.Add(new Lights());
        sparks = effectsModule.Root.Add(new Sparks());

        // What is behind the editor is the stage, which only changes when the editor does (the canvas is a hole in it),
        // so the picture of it with its glass is kept the way it is without
        compositor.GlassKeepsPicture = true;
        ApplyEffects();
    }

    /// <summary>The effects as the settings have them now, on every editor that's open.</summary>
    private void ApplyEffects()
    {
        foreach (HexDocument open in documents)
            open.Editor.CaretGlide = CaretGlide;

        // Everything else of Dawn's that moves, the hovers and the drawers, as quick as the motion says. Not the stage,
        // a layout being designed moves the way the game it is for will have it
        compositor.AnimationSpeed = settings.UiAnimation;
        compositor.ScrollSpeed = settings.ScrollSpeed;
        ApplyTerminalMotion();

        if (!EffectsOn)
        {
            sparks.Clear();
            lights.Clear();
        }

        lightEffects.CaretGlow = settings.EffectCaretGlow;
        lightEffects.Spotlight = settings.EffectSpotlight;
        lightEffects.Ripples = settings.EffectRipples;
        lightEffects.Trail = settings.EffectTrail;

        compositor.PopupGlass = Glassy ? PopupGlass : null;

        // The frame on glass over its field of light, or solid, see DawnScene.Materials
        DressChrome();
    }

    /// <summary>Every tick, sparks off the caret if what is typed into changed since the last.</summary>
    private void UpdateEffects()
    {
        // The light, the caret's glow while code is typed into and the pointer's, whenever the effects are on
        if (EffectsOn)
        {
            Vector2? caret = document.IsText || document.View == HexView.Code ? compositor.Focus == code ? code.CaretPoint : null : null;
            lightEffects.Update(lights, sparks, effectsModule.ToLocal(compositor.Pointer.Position), compositor.Pointer.Down, caret,
                compositor.Skin?.AccentColor ?? Vector4.One, compositor.Skin?.TextColor ?? Vector4.One);
        }

        if (!TypingSparks || !document.IsText || compositor.Focus != code)
        {
            sparkedIn = null;
            return;
        }

        int version = code.Document.Version;
        if (sparkedIn == code && version != sparkedVersion)
            sparks.Emit(code.CaretPoint, 4, compositor.Skin?.AccentColor ?? Vector4.One);

        sparkedIn = code;
        sparkedVersion = version;
    }

    /// <summary>A burst for a file saved, where the caret is in its code or the middle of the canvas.</summary>
    private void SavedEffects()
    {
        // A save nobody asked for (the auto save) throws nothing, a burst every second of typing is the power mode again
        if (!SaveSparks || savingQuietly)
            return;

        // A file of text is code to the editor whatever its view says, the burst was in the middle of nowhere for one
        Vector2 at = (document.View == HexView.Code || document.IsText) && code.CaretPoint != Vector2.Zero ? code.CaretPoint : canvas.Bounds.Center;
        sparks.Burst(at, 36, compositor.Skin?.AccentColor ?? Vector4.One);
    }
}
