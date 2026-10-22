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
    /// The glass of a window that stays up over everything (the settings), the popups' with most of the panel kept and
    /// hardly a lens. A page of sentences over clear glass is a page nobody can read, and a lens on that much edge looks
    /// like a fishbowl.
    /// </summary>
    public static UIGlass Window(UIGlass popup) => popup with { Clarity = MathF.Min(popup.Clarity, 0.3f), Bend = MathF.Min(popup.Bend, 4.0f) };

    /// <summary>The glass of the side panels, out of the same sliders.</summary>
    public static UIGlass Side(float blur, float clarity, float rim) => UIGlass.Pane(blur, clarity, rim);

    /// <summary>The popups' glass as the editor's section of the settings has it, for Dusk, which reads the file.</summary>
    public static UIGlass Popup(IReadOnlyDictionary<string, IRuntimeValue> editor) =>
        Popup(Number(editor, "glass_blur", BLUR), Number(editor, "glass_clarity", CLARITY), Number(editor, "glass_rim", RIM), Number(editor, "glass_bend", BEND));

    private static float Number(IReadOnlyDictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;
}
