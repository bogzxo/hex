using Hex.Core;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;

namespace Hex.Shell;

/// <summary>
/// A look of Hex's, a name and the visual settings it sets by their keys in the editor's section of settings.hor (the
/// glass, the effects and the motion), and the themes as well for one the user saved. Picking one sets every value it
/// has and leaves the rest alone, so the glass ones Hex comes with never touch the colours somebody chose. Visual Studio
/// does, it's the whole of that editor's look in one go, its theme, its font and its colours of C# as well.
/// </summary>
internal sealed record HexLook(string Name, IReadOnlyDictionary<string, IRuntimeValue> Values, bool BuiltIn = false);

/// <summary>
/// Hex's looks, the profiles of how it's drawn. Four come with it, Fluid Glass (clear glass that bends what is behind
/// it at its rim), Frosted Glass (thicker, blurrier, quieter behind a lot of text), Flat (the default, solid
/// panels, nothing glows or glides) and Visual Studio (that editor's dark theme, square and flat, see
/// HexLooks.VisualStudio), and the user saves their own from whatever the settings are at. The user's are kept in the
/// editor's section as looks and the one picked last as look (HexSettings reads and writes both), so Dawn and Dusk pick
/// from the same list and a look saved in either is there in the other.
/// <para>
/// A look is matched against the settings as they are, so picking one and then dragging a slider leaves no look picked,
/// the settings say Custom, until it's saved as one of the user's or another is picked.
/// </para>
/// </summary>
internal static partial class HexLooks
{
    /// <summary>The ones that come with Hex, by the names they're shown and kept under.</summary>
    public const string FLUID = "Fluid Glass", FROSTED = "Frosted Glass", FLAT = "Flat", VISUAL_STUDIO = "Visual Studio";

    /// <summary>Every key of the editor's section a look sets, the glass, the effects and the motion.</summary>
    public static readonly string[] Keys =
    [
        "effect_glass", "glass_blur", "glass_clarity", "glass_rim", "glass_bend",
        "effects", "effect_sparks", "effect_caret_glow", "effect_spotlight", "effect_ripples", "effect_trail",
        "effect_motion", "ui_animation", "caret_glide"
    ];

    /// <summary>The themes, which a look the user saves takes along and the glass ones of Hex's leave be. Dusk's is the terminal section's theme.</summary>
    public const string EDITOR_SKIN = "editor_skin", EDITOR_THEME = "editor_theme", TERMINAL_THEME = "terminal_theme";

    // How near two numbers have to be to be the same setting, a slider lands on whatever the pointer made of it
    private const float NEAR = 0.002f;

    /// <summary>
    /// Clear glass with a bright rim and a strong lens, the light caught at the edges, the caret gliding, and none of the
    /// effects, a glow following the caret about was the first thing anybody asked to turn off.
    /// </summary>
    public static readonly HexLook Fluid = new(FLUID, new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effect_glass"] = Values.Bool(true),
        ["glass_blur"] = new NumberValue(UIGlass.Clear.Blur),
        ["glass_clarity"] = new NumberValue(UIGlass.Clear.Clarity),
        ["glass_rim"] = new NumberValue(UIGlass.Clear.Rim),
        ["glass_bend"] = new NumberValue(UIGlass.Clear.Bend),
        ["effects"] = new StringValue("none"),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_caret_glow"] = Values.Bool(true),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(true),
        ["ui_animation"] = new NumberValue(1.0f),
        ["caret_glide"] = new NumberValue(EditorSettingsCatalog.CARET_GLIDE)
    }, BuiltIn: true);

    /// <summary>
    /// Thick frosted glass, a long blur and little of it coming through, a soft rim and no bend, which is the glass's
    /// frosted kind. Easier behind a wall of text. Fluid's bend is what makes its glass liquid, see glass.slang.
    /// </summary>
    public static readonly HexLook Frosted = new(FROSTED, new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effect_glass"] = Values.Bool(true),
        ["glass_blur"] = new NumberValue(40.0f),
        ["glass_clarity"] = new NumberValue(0.38f),
        ["glass_rim"] = new NumberValue(0.2f),
        ["glass_bend"] = new NumberValue(0.0f),
        ["effects"] = new StringValue("none"),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_caret_glow"] = Values.Bool(true),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(true),
        ["ui_animation"] = new NumberValue(1.0f),
        ["caret_glide"] = new NumberValue(EditorSettingsCatalog.CARET_GLIDE)
    }, BuiltIn: true);

    /// <summary>
    /// Solid panels, no lights and no sparks, the caret jumping. The glass's sliders are left where they are, with the glass
    /// off they make nothing and they're there for when it goes back on.
    /// </summary>
    public static readonly HexLook Flat = new(FLAT, new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effect_glass"] = Values.Bool(false),
        ["effects"] = new StringValue("none"),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_caret_glow"] = Values.Bool(false),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(false),
        ["ui_animation"] = new NumberValue(0.0f),
        ["caret_glide"] = new NumberValue(0.0f)
    }, BuiltIn: true);

    /// <summary>Visual Studio dark, see HexLooks.VisualStudio.</summary>
    public static readonly HexLook VisualStudio = MakeVisualStudio();

    /// <summary>The ones that come with Hex, in the order they're shown.</summary>
    public static readonly HexLook[] BuiltIn = [Fluid, Frosted, Flat, VisualStudio];

    /// <summary>Hex's own and then the user's, in the order they were saved.</summary>
    public static List<HexLook> All(HexSettings settings) =>
        [.. BuiltIn, .. settings.Looks.Select(saved => new HexLook(saved.Name, saved.Values))];

    /// <summary>A look by its name, null for one there isn't.</summary>
    public static HexLook? Find(HexSettings settings, string name) =>
        All(settings).Find(look => look.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The look the settings are in, the one picked last while it still says what they are, otherwise the first that does,
    /// null for settings no look has (Custom).
    /// </summary>
    public static HexLook? Current(HexSettings settings, string terminalTheme)
    {
        List<HexLook> all = All(settings);
        if (all.Find(look => look.Name == settings.Look) is { } picked && Matches(picked, settings, terminalTheme))
            return picked;

        return all.Find(look => Matches(look, settings, terminalTheme));
    }

    /// <summary>Whether every value a look sets is what the settings have now.</summary>
    public static bool Matches(HexLook look, HexSettings settings, string terminalTheme)
    {
        foreach (var (key, value) in look.Values)
        {
            if (!Same(Get(settings, key, terminalTheme), value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A look's values put into the settings, its themes as well (the editor's skin and theme into the settings, Dusk's
    /// is the mode's to put in force, see <see cref="TerminalTheme"/>). Nothing is kept or put on screen, that's the mode's.
    /// </summary>
    public static void Apply(HexLook look, HexSettings settings)
    {
        foreach (var (key, value) in look.Values)
            Set(settings, key, value);
        settings.Look = look.Name;
    }

    /// <summary>The theme a look has for Dusk, null for one that leaves it be.</summary>
    public static string? TerminalTheme(HexLook look) =>
        look.Values.TryGetValue(TERMINAL_THEME, out IRuntimeValue? theme) && theme is StringValue { Value.Length: > 0 } named ? named.Value : null;

    /// <summary>Whether a look changes the editor's skin or theme.</summary>
    public static bool HasEditorTheme(HexLook look) => look.Values.ContainsKey(EDITOR_SKIN) || look.Values.ContainsKey(EDITOR_THEME);

    /// <summary>Everything a look saved by the user keeps, as the settings and Dusk's theme have it now.</summary>
    public static Dictionary<string, IRuntimeValue> Capture(HexSettings settings, string terminalTheme)
    {
        var values = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (string key in Keys)
        {
            if (Get(settings, key, terminalTheme) is { } value)
                values[key] = value;
        }

        values[EDITOR_SKIN] = new StringValue(settings.EditorSkin);
        values[EDITOR_THEME] = new StringValue(settings.EditorTheme);
        if (terminalTheme.Length > 0)
            values[TERMINAL_THEME] = new StringValue(terminalTheme);
        return values;
    }

    /// <summary>
    /// Keeps the settings as a look of the user's by a name, over the one of theirs by that name if there is one, and picks
    /// it. False for a name that's empty or one of Hex's own, which aren't the user's to change.
    /// </summary>
    public static bool Save(HexSettings settings, string name, string terminalTheme)
    {
        name = name.Trim();
        if (name.Length == 0 || BuiltIn.Any(look => look.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return false;

        Dictionary<string, IRuntimeValue> values = Capture(settings, terminalTheme);
        int at = settings.Looks.FindIndex(saved => saved.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            settings.Looks[at] = (settings.Looks[at].Name, values);
        else
            settings.Looks.Add((name, values));

        settings.Look = at >= 0 ? settings.Looks[at].Name : name;
        return true;
    }

    /// <summary>Forgets a look of the user's. False for one of Hex's own or one there isn't.</summary>
    public static bool Delete(HexSettings settings, string name)
    {
        int at = settings.Looks.FindIndex(saved => saved.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return false;

        settings.Looks.RemoveAt(at);
        if (settings.Look.Equals(name, StringComparison.OrdinalIgnoreCase))
            settings.Look = string.Empty;
        return true;
    }

    /// <summary>A name for a new look that none of them has yet, My look, My look 2 and on.</summary>
    public static string FreshName(HexSettings settings, string stem)
    {
        List<HexLook> all = All(settings);
        string name = stem;
        for (int n = 2; all.Any(look => look.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{stem} {n}";
        return name;
    }

    /// <summary>The glass a look makes for the popups, as its values say and the settings fill in, null for a look with the glass off.</summary>
    public static UIGlass? GlassOf(HexLook look, HexSettings settings)
    {
        float Number(string key, float otherwise) => look.Values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;

        bool on = look.Values.TryGetValue("effect_glass", out IRuntimeValue? glass) ? glass is BooleanValue { Value: true } : settings.EffectGlass;
        if (!on)
            return null;

        return HexGlass.Popup(Number("glass_blur", settings.GlassBlur), Number("glass_clarity", settings.GlassClarity),
            Number("glass_rim", settings.GlassRim), Number("glass_bend", settings.GlassBend));
    }

    /// <summary>What a key of a look is in the settings now, Dusk's theme from what the mode says it is.</summary>
    public static IRuntimeValue? Get(HexSettings settings, string key, string terminalTheme) => key switch
    {
        "effect_glass" => Values.Bool(settings.EffectGlass),
        "glass_blur" => new NumberValue(settings.GlassBlur),
        "glass_clarity" => new NumberValue(settings.GlassClarity),
        "glass_rim" => new NumberValue(settings.GlassRim),
        "glass_bend" => new NumberValue(settings.GlassBend),
        "effects" => new StringValue(settings.Effects),
        "effect_sparks" => Values.Bool(settings.EffectSparks),
        "effect_caret_glow" => Values.Bool(settings.EffectCaretGlow),
        "effect_spotlight" => Values.Bool(settings.EffectSpotlight),
        "effect_ripples" => Values.Bool(settings.EffectRipples),
        "effect_trail" => Values.Bool(settings.EffectTrail),
        "effect_motion" => Values.Bool(settings.EffectMotion),
        "ui_animation" => new NumberValue(settings.UiAnimation),
        "caret_glide" => new NumberValue(settings.CaretGlide),
        CODE_FONT => new StringValue(settings.CodeFont),
        CSHARP_COLORS => new StringValue(settings.CSharpColors),
        EDITOR_SKIN => new StringValue(settings.EditorSkin),
        EDITOR_THEME => new StringValue(settings.EditorTheme),
        TERMINAL_THEME => new StringValue(terminalTheme),
        _ => null
    };

    // A key of a look put into the settings, held to what each setting takes. Dusk's theme isn't the editor's section's
    private static void Set(HexSettings settings, string key, IRuntimeValue value)
    {
        switch (key, value)
        {
            case ("effect_glass", BooleanValue on): settings.EffectGlass = on.Value; break;
            case ("glass_blur", NumberValue blur): settings.GlassBlur = Math.Clamp(blur.Value, 0.0f, 64.0f); break;
            case ("glass_clarity", NumberValue clarity): settings.GlassClarity = Math.Clamp(clarity.Value, 0.0f, 1.0f); break;
            case ("glass_rim", NumberValue rim): settings.GlassRim = Math.Clamp(rim.Value, 0.0f, 1.0f); break;
            case ("glass_bend", NumberValue bend): settings.GlassBend = Math.Clamp(bend.Value, 0.0f, 24.0f); break;
            case ("effects", StringValue { Value: "none" or "subtle" or "all" } effects): settings.Effects = effects.Value; break;
            case ("effect_sparks", BooleanValue on): settings.EffectSparks = on.Value; break;
            case ("effect_caret_glow", BooleanValue on): settings.EffectCaretGlow = on.Value; break;
            case ("effect_spotlight", BooleanValue on): settings.EffectSpotlight = on.Value; break;
            case ("effect_ripples", BooleanValue on): settings.EffectRipples = on.Value; break;
            case ("effect_trail", BooleanValue on): settings.EffectTrail = on.Value; break;
            case ("effect_motion", BooleanValue on): settings.EffectMotion = on.Value; break;
            case ("ui_animation", NumberValue speed): settings.UiAnimation = Math.Clamp(speed.Value, 0.0f, 3.0f); break;
            case ("caret_glide", NumberValue glide): settings.CaretGlide = Math.Clamp(glide.Value, 0.0f, 0.3f); break;
            case (CODE_FONT, StringValue font): settings.CodeFont = font.Value; break;
            case (CSHARP_COLORS, StringValue { Value: "resharper" or "visual_studio" or "skin" } colors): settings.CSharpColors = colors.Value; break;
            case (EDITOR_SKIN, StringValue { Value.Length: > 0 } skin): settings.EditorSkin = skin.Value; break;
            case (EDITOR_THEME, StringValue theme): settings.EditorTheme = theme.Value; break;
        }
    }

    // Whether two values of a setting are the same one, numbers as near as a slider lands
    private static bool Same(IRuntimeValue? a, IRuntimeValue? b) => (a, b) switch
    {
        (NumberValue x, NumberValue y) => MathF.Abs(x.Value - y.Value) <= NEAR,
        (StringValue x, StringValue y) => x.Value == y.Value,
        (BooleanValue x, BooleanValue y) => x.Value == y.Value,
        _ => false
    };
}
