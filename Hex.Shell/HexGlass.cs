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
    public const float BLUR = Dawn.HexGlassDefaults.BLUR, CLARITY = Dawn.HexGlassDefaults.CLARITY, RIM = Dawn.HexGlassDefaults.RIM, BEND = Dawn.HexGlassDefaults.BEND;

    /// <summary>The glass of the dialogs, menus and the palette.</summary>
    public static UIGlass Popup(float blur, float clarity, float rim, float bend) =>
        new(Blur: Math.Clamp(blur, 0.0f, 64.0f), Clarity: Math.Clamp(clarity, 0.0f, 1.0f), Rim: Math.Clamp(rim, 0.0f, 1.0f), Bend: Math.Clamp(bend, 0.0f, 24.0f));

    /// <summary>The glass of the side panels, out of the same sliders.</summary>
    public static UIGlass Side(float blur, float clarity, float rim) =>
        new(Blur: Math.Clamp(blur * 1.2f, 0.0f, 64.0f), Clarity: Math.Clamp(clarity + 0.05f, 0.0f, 1.0f), Rim: Math.Clamp(rim * 0.35f, 0.0f, 1.0f), Bend: 0.0f);

    /// <summary>The popups' glass as the editor's section of the settings has it, for Dusk, which reads the file.</summary>
    public static UIGlass Popup(IReadOnlyDictionary<string, IRuntimeValue> editor) =>
        Popup(Number(editor, "glass_blur", BLUR), Number(editor, "glass_clarity", CLARITY), Number(editor, "glass_rim", RIM), Number(editor, "glass_bend", BEND));

    private static float Number(IReadOnlyDictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;
}
