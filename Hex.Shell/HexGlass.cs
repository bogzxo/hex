using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;

namespace Hex.Shell;

/// <summary>
/// How thick the glass is, out of the three sliders of the effects (glass_blur, glass_clarity and glass_bend in
/// the editor's section, one setting for both modes). Every surface is the one glass: the popups, the side panels and the
/// windows all smear as far (the blur slider, to the pixel). The windows and the sides are the broad surfaces, keeping
/// no more of a lens than <see cref="BROAD_BEND"/>, and a window is a little less clear so pages of sentences stay readable.
/// </summary>
public static class HexGlass
{
    /// <summary>What the sliders come as, clear liquid glass with a strong meniscus lens.</summary>
    public static readonly float BLUR = UIGlass.Clear.Blur, CLARITY = UIGlass.Clear.Clarity, BEND = UIGlass.Clear.Bend;

    /// <summary>The glass of the dialogs, menus, dropdowns, tooltips and the palette, the sliders as they are.</summary>
    public static UIGlass Popup(float blur, float clarity, float bend) => UIGlass.Lens(blur, clarity, bend);

    // The deepest lens a broad surface (a side, a window) gets, in the UI's pixels
    private const float BROAD_BEND = 10.0f;

    private const float WINDOW_CLARITY = 0.8f, CLEAREST_WINDOW = 1.0f;
    private static readonly float USUAL_CLARITY = UIGlass.Clear.Clarity;

    private static UIGlass Broad(UIGlass popup) => popup with { Bend = MathF.Min(popup.Bend, BROAD_BEND) };

    public static UIGlass Window(UIGlass popup) => Broad(popup) with { Clarity = WindowClarity(popup.Clarity) };

    public static float WindowClarity(float clarity)
    {
        float usual = USUAL_CLARITY * WINDOW_CLARITY;
        if (clarity <= USUAL_CLARITY)
            return clarity * WINDOW_CLARITY;

        float past = Math.Clamp((clarity - USUAL_CLARITY) / (1.0f - USUAL_CLARITY), 0.0f, 1.0f);
        return usual + (CLEAREST_WINDOW - usual) * past;
    }

    public static UIGlass Side(float blur, float clarity, float bend = 0.0f) => Broad(Popup(blur, clarity, bend));

    public static float Veil(float clarity) => Math.Clamp(0.95f - clarity * 0.85f, 0.1f, 0.7f);

    /// <summary>The popups' glass as the editor's section of the settings has it, for Dusk, which reads the file.</summary>
    public static UIGlass Popup(IReadOnlyDictionary<string, IRuntimeValue> editor) =>
        Popup(Number(editor, "glass_blur", BLUR), Number(editor, "glass_clarity", CLARITY), Number(editor, "glass_bend", BEND));

    private static float Number(IReadOnlyDictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;
}
