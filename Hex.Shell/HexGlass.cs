using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;

namespace Hex.Shell;

/// <summary>
/// How thick the glass is, out of the four sliders of the effects (glass_blur, glass_clarity, glass_rim and glass_bend in
/// the editor's section, one setting for both modes). Every surface is the one glass: the popups, the side panels and the
/// windows all smear as far (the blur slider, to the pixel) and have the same rim. The windows and the sides are the
/// broad surfaces, a pane that big keeps no more of a lens than <see cref="BROAD_BEND"/> (a lens that deep on a whole side
/// looks like the window's melting), and a window, a page of sentences, is the one that is also a little less clear. That
/// is all the difference there is between them, and it is made here and nowhere else.
/// <para>
/// The blur used to be worked out per kind of surface, a window twice the popups' and at least 20, a side a fifth more,
/// so the settings, the menus and the side bars were never the same frost however the slider was set.
/// </para>
/// </summary>
public static class HexGlass
{
    /// <summary>What the sliders come as, clear glass with a bright rim and a strong lens.</summary>
    public static readonly float BLUR = UIGlass.Clear.Blur, CLARITY = UIGlass.Clear.Clarity, RIM = UIGlass.Clear.Rim, BEND = UIGlass.Clear.Bend;

    /// <summary>The glass of the dialogs, menus, dropdowns, tooltips and the palette, the sliders as they are.</summary>
    public static UIGlass Popup(float blur, float clarity, float rim, float bend) => UIGlass.Lens(blur, clarity, rim, bend);

    // The deepest lens a broad surface (a side, a window) gets, in the UI's pixels
    private const float BROAD_BEND = 6.0f;

    // How much of the popups' clarity a window keeps up to the slider's usual, and the most clarity a window gets with
    // the slider all the way up
    private const float WINDOW_CLARITY = 0.6f, CLEAREST_WINDOW = 1.0f;
    private static readonly float USUAL_CLARITY = UIGlass.Clear.Clarity;

    /// <summary>
    /// The glass of a broad surface out of the popups': the same blur, rim and clarity, only the lens held to what a surface
    /// that big can take.
    /// </summary>
    private static UIGlass Broad(UIGlass popup) => popup with { Bend = MathF.Min(popup.Bend, BROAD_BEND) };

    /// <summary>
    /// The glass of a window that stays up over everything (the settings, the keys, a page of help). The popups' blur, rim
    /// and lens (held to <see cref="BROAD_BEND"/>), less clear: a page of sentences over clear glass is a page nobody can
    /// read, and a panel of the layout under it came through as a ghost of a box behind the profiles' cards and the theme rows.
    /// </summary>
    public static UIGlass Window(UIGlass popup) => Broad(popup) with { Clarity = WindowClarity(popup.Clarity) };

    /// <summary>
    /// A window's clarity out of the slider's. Up to the usual it's a share of the popups', the way the looks were made,
    /// and past that it goes on up with the slider to as clear as the slider says. It was held to a quarter whatever the
    /// slider said, and glass turned right up still had a solid window over it.
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
    /// The glass of the side panels, out of the same sliders: the popups' blur, clarity and rim, the lens held to
    /// <see cref="BROAD_BEND"/> so the side reads as the same stuff as what pops up over it.
    /// </summary>
    public static UIGlass Side(float blur, float clarity, float rim, float bend = 0.0f) => Broad(Popup(blur, clarity, rim, bend));

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
