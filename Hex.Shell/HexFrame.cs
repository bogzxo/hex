using System.Numerics;

using Hex.Core;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>What a window of Hex's is shaped like in a skin, worked out once for both modes (<see cref="HexFrame.Dress"/>).</summary>
/// <param name="Square">A theme with no round to its corners (Visual Studio's): flush, and the window is the panes' own tone.</param>
/// <param name="Flush">The panes against each other with no gaps and no round, a square theme or one of the Solstice ones.</param>
/// <param name="Window">The tone of the window itself, the title bar, the status bar and what shows between the panes.</param>
/// <param name="Card">The tone of a pane.</param>
internal readonly record struct HexFrameShape(bool Square, bool Flush, Vector4 Window, Vector4 Card);

/// <summary>
/// What a window of Hex's is made of round its own middle, the same in Dawn and in Dusk: the field of light under
/// everything, the lights and sparks over everything, the glass the popups are on, how the bars along the edges are
/// dressed for a skin, the line under the title bar, and the sizes the window can be drawn at. Each scene had all of it
/// written out for itself, the two copies a little different wherever one had been changed and the other hadn't. A scene
/// has one of these and keeps only what is its own: Dawn its cards and its code, Dusk its panes.
/// <para>
/// Made in two steps, because of where its layers sit among the scene's: the field of light as the scene starts, under
/// the scene's own module, and the effects (<see cref="BuildEffects"/>) once the scene has made everything of its own,
/// over all of it.
/// </para>
/// </summary>
internal sealed class HexFrame
{
    /// <summary>How big a window can draw itself. Text is at its sharpest at the whole ones.</summary>
    public static readonly float[] Scales = [0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f];

    /// <summary>The picture behind the code and the terminals when nobody chose another.</summary>
    public const string USUAL_BACKDROP = "Assets/hex/images/background_icon.png";

    // How much of a button's bump under the pointer and its squeeze when pressed Hex keeps. The engine's own are for a
    // game's few big buttons, on a window of hundreds of small ones every pass of the pointer set a row of them bobbing
    private const float BUTTON_GIVE = 0.35f;

    private readonly UICompositor compositor;
    private readonly HexLightEffects lightEffects = new();
    private HexHorizon? horizon;

    /// <summary>The module under the scene's own that the field of light is in, and the field.</summary>
    public UIModule GlowModule { get; }

    public GlowField Glow { get; }

    /// <summary>The module over everything that the lights and the sparks are in, the pointer goes through it. Null until <see cref="BuildEffects"/>.</summary>
    public UIModule EffectsModule { get; private set; } = null!;

    public Lights Lights { get; private set; } = null!;

    public Sparks Sparks { get; private set; } = null!;

    /// <summary>The line under the title bar, null until it's first set (<see cref="UpdateHorizon"/>).</summary>
    public HexHorizon? Horizon => horizon;

    /// <summary>The field of light, in a module of its own made now, so under whatever the scene makes after.</summary>
    /// <param name="hole">Where the field is left open for something of the scene's to show through (Dawn's canvas), in the module it's handed, null for nowhere.</param>
    public HexFrame(UICompositor compositor, Func<UIModule, UIRect?>? hole = null)
    {
        this.compositor = compositor;
        compositor.ButtonGive = BUTTON_GIVE;
        GlowModule = compositor.CreateModule();
        GlowModule.ShowInLayoutDebugger = false;
        GlowModule.Interactive = false;
        Glow = GlowModule.Root.Add(new GlowField { Fill = UIFill.Both });
        if (hole is not null)
            Glow.Hole = () => hole(GlowModule);
    }

    /// <summary>The size nearest one asked for that a window is drawn at.</summary>
    public static float NearestScale(float scale) => Scales.MinBy(known => MathF.Abs(known - scale));

    /// <summary>Whether the lights are on at all, the effects at subtle or all.</summary>
    public static bool EffectsOn(HexSettings settings) => settings.Effects is "subtle" or "all";

    /// <summary>The glass of the popups as the four sliders of the settings make it.</summary>
    public static UIGlass PopupGlass(HexSettings settings) => HexGlass.Popup(settings.GlassBlur, settings.GlassClarity, settings.GlassRim, settings.GlassBend);

    /// <summary>The quieter glass of the sides out of the same four.</summary>
    public static UIGlass SideGlass(HexSettings settings) => HexGlass.Side(settings.GlassBlur, settings.GlassClarity, settings.GlassRim, settings.GlassBend);

    /// <summary>The lights and the sparks, in a module of their own made now, so over whatever the scene has made.</summary>
    public void BuildEffects()
    {
        EffectsModule = compositor.CreateModule();
        EffectsModule.ShowInLayoutDebugger = false;
        EffectsModule.Interactive = false;

        // the light under, the sparks over it
        Lights = EffectsModule.Root.Add(new Lights());
        Sparks = EffectsModule.Root.Add(new Sparks());

        // What is behind the scene's UI only changes when the UI does, so the picture of it under its glass is kept
        compositor.GlassKeepsPicture = true;
    }

    /// <summary>
    /// What the two modes share of the effects put in force from the editor's settings: the popups' glass, how quickly the
    /// UI moves and scrolls, which of the lights are on, and nothing left flying or lit that is switched off.
    /// </summary>
    /// <param name="animationSpeed">How quickly the scene's UI moves, the settings' unless the scene has a say of its own (Dusk's terminal animations off is nothing moving).</param>
    public void ApplyEffects(HexSettings settings, float animationSpeed)
    {
        compositor.PopupGlass = settings.EffectGlass ? PopupGlass(settings) : null;
        compositor.AnimationSpeed = animationSpeed;
        compositor.ScrollSpeed = settings.ScrollSpeed;
        compositor.MiddleScrollSpeed = settings.MiddleScrollSpeed;

        lightEffects.CaretGlow = settings.EffectCaretGlow;
        lightEffects.Spotlight = settings.EffectSpotlight;
        lightEffects.Ripples = settings.EffectRipples;
        lightEffects.Trail = settings.EffectTrail;

        if (Sparks is null)
            return;

        bool on = EffectsOn(settings);
        if (!on || !settings.EffectSparks)
            Sparks.Clear();
        if (!on)
            Lights.Clear();
    }

    /// <summary>Every tick with the effects on, the glow under the caret (or the cursor) and whatever follows the pointer.</summary>
    /// <param name="caret">Where the caret is in the effects' module, null while nothing is being typed into.</param>
    public void UpdateLights(Vector2? caret) =>
        lightEffects.Update(Lights, Sparks, EffectsModule.ToLocal(compositor.Pointer.Position), compositor.Pointer.Down, caret,
            compositor.Skin?.AccentColor ?? Vector4.One, compositor.Skin?.TextColor ?? Vector4.One);

    /// <summary>
    /// The window dressed for a skin, what both modes do the same way: its tone, the field of light under a veil of that
    /// tone on glass or the tone alone without, and the bars along its edges nothing of their own on it. Says what shape
    /// the skin makes the window, for the scene to dress its own middle by.
    /// </summary>
    /// <param name="glassy">Whether the glass is on.</param>
    /// <param name="clarity">How clear the glass is, less of the window's tone is laid over the light for clearer glass.</param>
    /// <param name="modeToggle">The Dawn and Dusk selector in the title bar, the mode that's on filled in a flush look.</param>
    /// <param name="bars">The bars that are the window's own surface, the title bar and Dawn's activity bar.</param>
    public HexFrameShape Dress(UISkin skin, bool glassy, float clarity, StatusBar statusBar, Button? modeToggle, params ReadOnlySpan<StackPanel> bars)
    {
        Vector4 card = skin.PanelColor with { W = 1.0f };
        bool square = skin.CornerRadius <= 0.0f;
        bool flush = HexLooks.Flush(skin);
        Vector4 window = square ? card : skin.FieldColor with { W = 1.0f };

        Glow.Visible = true;
        Glow.UseSkin(skin);
        Glow.Veil = glassy ? window with { W = HexGlass.Veil(clarity) } : window;

        // The bars are the window and nothing of their own, and the status bar has no line between it and what's over it
        Vector4 none = window with { W = 0.0f };
        foreach (StackPanel bar in bars)
        {
            bar.Color = none;
            bar.Glass = null;
        }

        statusBar.Color = none;
        statusBar.Glass = null;
        statusBar.Hairline = false;

        HexModeSwitch.Dress(modeToggle, skin);
        return new HexFrameShape(square, flush, window, card);
    }

    /// <summary>The line under the title bar set for the skin and what's going, see <see cref="HexHorizon"/>. Made over <paramref name="root"/> the first time.</summary>
    public void UpdateHorizon(UIComponent root, UISkin skin, float under, float across, HexHorizonState state, float? progress) =>
        (horizon ??= new HexHorizon(root)).Update(skin, under, across, state, progress);
}
