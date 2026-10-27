using Horizon.HIDL.Runtime;

namespace Hex.Shell;

// Visual Studio in its dark theme, the one look of Hex's that is more than glass and lights. It puts the flat skin's
// Visual Studio theme on Dawn and Dusk both (square, flush and near black, see Assets/uix/flat/themes/visual_studio.hor),
// takes the glass and every light away, has the caret jump the way it does there, writes the code in Cascadia Mono and
// has C# coloured by the theme, which is Visual Studio's colours, rather than ReSharper's.
internal static partial class HexLooks
{
    /// <summary>The theme of the flat skin the Visual Studio look puts on, Dawn's and Dusk's.</summary>
    public const string VISUAL_STUDIO_THEME = "visual_studio";

    /// <summary>The keys of the editor's section only the Visual Studio look sets, the font of the code and whose colours C# is in.</summary>
    public const string CODE_FONT = "code_font", CSHARP_COLORS = "csharp_colors";

    // The look itself, made by a method so it's there before BuiltIn lists it whatever order the two files are read in
    private static HexLook MakeVisualStudio() => new(VISUAL_STUDIO, new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effect_glass"] = Values.Bool(false),
        ["effects"] = new StringValue("none"),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_caret_glow"] = Values.Bool(false),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(false),
        ["ui_animation"] = new NumberValue(1.0f),
        ["caret_glide"] = new NumberValue(0.0f),
        [EDITOR_SKIN] = new StringValue("flat"),
        [EDITOR_THEME] = new StringValue(VISUAL_STUDIO_THEME),
        [TERMINAL_THEME] = new StringValue(VISUAL_STUDIO_THEME),
        [CODE_FONT] = new StringValue("cascadia_mono"),
        [CSHARP_COLORS] = new StringValue("skin")
    }, BuiltIn: true);

    /// <summary>Whether a look sets the code's font or the colours of C#, which the modes put in force after it's picked.</summary>
    public static bool HasCodeLook(HexLook look) => look.Values.ContainsKey(CODE_FONT) || look.Values.ContainsKey(CSHARP_COLORS);
}
