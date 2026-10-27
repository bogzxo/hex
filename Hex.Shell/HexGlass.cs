using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;

namespace Hex.Shell;

/// <summary>
/// How thick the glass is, out of the four sliders of the effects (glass_blur, glass_clarity, glass_rim and glass_bend in
/// the editor's section, one setting for both modes). The popups get them as they are, the side panels a quieter glass
/// worked out of the same, a little more blur and a lot less rim and no bend, there's nothing on most of a side but a
/// tree and a lens on that much glass looks like the window's melting.
/// </summary>
public static class HexGlass
{
    /// <summary>What the sliders come as, clear glass with a bright rim and a strong lens.</summary>
    public static readonly float BLUR = UIGlass.Clear.Blur, CLARITY = UIGlass.Clear.Clarity, RIM = UIGlass.Clear.Rim, BEND = UIGlass.Clear.Bend;

    /// <summary>The glass of the dialogs, menus and the palette.</summary>
    public static UIGlass Popup(float blur, float clarity, float rim, float bend) => UIGlass.Lens(blur, clarity, rim, bend);

    /// <summary>
    /// The glass of a window that stays up over everything (the settings), the popups' less clear and with less of
    /// a lens. A page of sentences over clear glass is a page nobody can read, and a lens on that much edge looks like a
    /// fishbowl. It was held to one clarity for every look, and Fluid's windows came out the same solid navy as Frosted's.
    /// <para>
    /// Blurred twice as far as a popup and never less than WINDOW_BLUR, and under half as clear. A window stays up over the
    /// designer's canvas, and at the popups' blur and three quarters of their clarity a panel of the layout under it came
    /// through as a ghost of a box behind the profiles' cards and the theme rows.
    /// </para>
    /// </summary>
    public static UIGlass Window(UIGlass popup) => popup with
    {
        Blur = MathF.Min(MathF.Max(popup.Blur * 2.0f, WINDOW_BLUR), 64.0f),
        Clarity = WindowClarity(popup.Clarity),
        Bend = MathF.Min(popup.Bend, 6.0f)
    };

    // How much of the popups' clarity a window keeps, and the least it's blurred
    private const float WINDOW_CLARITY = 0.45f, WINDOW_BLUR = 20.0f;

    // The clarity the sliders come as, and the most a window gets with the slider all the way up
    private const float CLEAREST_WINDOW = 0.95f;
    private static readonly float USUAL_CLARITY = UIGlass.Clear.Clarity;

    /// <summary>
    /// A window's clarity out of the slider's. Up to the usual it's under half the popups', the way the looks were made,
    /// and past that it goes on up to nearly clear glass with the slider. It was held to a quarter whatever the slider
    /// said, and glass turned right up still had a solid window over it.
    /// </summary>
    public static float WindowClarity(float clarity)
    {
        float usual = USUAL_CLARITY * WINDOW_CLARITY;
        if (clarity <= USUAL_CLARITY)
            return clarity * WINDOW_CLARITY;

        float past = Math.Clamp((clarity - USUAL_CLARITY) / (1.0f - USUAL_CLARITY), 0.0f, 1.0f);
        return usual + (CLEAREST_WINDOW - usual) * past;
    }

    /// <summary>
    /// The glass of the side panels, out of the same sliders. Liquid glass (a bend) keeps more of its rim there, a light
    /// along the edge of the side as there is along a popup's, so the side reads as the same stuff as what pops up over it.
    /// </summary>
    public static UIGlass Side(float blur, float clarity, float rim, float bend = 0.0f)
    {
        UIGlass pane = UIGlass.Pane(blur, clarity, rim);
        return bend > 0.0f ? pane with { Rim = Math.Clamp(rim * LIQUID_SIDE_RIM, 0.0f, 1.0f) } : pane;
    }

    // How much of the rim the side of liquid glass keeps
    private const float LIQUID_SIDE_RIM = 0.6f;

    /// <summary>
    /// How much of the window's tone is laid over its field of light, by how clear the look's glass is. Clear glass lets
    /// more of the light through, so there's something under the sides for it to show, frosted keeps the window dimmer.
    /// One veil for every look had the main window the same in all three, the glass only showing on what popped up.
    /// </summary>
    // It stopped at 0.4 however clear the glass was, and the clearest glass still had most of a solid window under it
    public static float Veil(float clarity) => Math.Clamp(0.95f - clarity * 0.85f, 0.1f, 0.7f);

    /// <summary>The popups' glass as the editor's section of the settings has it, for Dusk, which reads the file.</summary>
    public static UIGlass Popup(IReadOnlyDictionary<string, IRuntimeValue> editor) =>
        Popup(Number(editor, "glass_blur", BLUR), Number(editor, "glass_clarity", CLARITY), Number(editor, "glass_rim", RIM), Number(editor, "glass_bend", BEND));

    private static float Number(IReadOnlyDictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;
}
