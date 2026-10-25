using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The editor's settings that are a plain value of its section, described once for both modes, Dawn putting them in
/// force as they change and Dusk the ones that are its too (the effects, the backdrop) and writing the rest for Dawn to
/// take in. Dawn adds its theme, its size and the designer's skin, which only it knows the choices of. What they come as
/// is what Dawn's HexSettings starts with.
/// <para>
/// They're in the order the settings window shows them, by what they're about rather than which mode has them. How Hex
/// looks first (the themes, the effects, the picture behind the code and the terminals), then the editor's code, its
/// files and its designer, then the terminal's own, then the extensions.
/// </para>
/// </summary>
public static class EditorSettingsCatalog
{
    /// <summary>The sections, the part of the window before the dot and the section after it.</summary>
    public const string PROFILES = "Appearance · Profiles", THEME = "Appearance · Theme", GLASS = "Appearance · Glass", EFFECTS = "Appearance · Effects",
        MOTION = "Appearance · Motion", BACKDROP = "Appearance · Backdrop", CODE = "Editor · Code", FILES = "Editor · Files", DESIGNER = "Editor · Designer";

    /// <summary>How Hex looks in the order it's shown, the profiles first, they're the whole of the rest in one go.</summary>
    public static readonly string[] Sections = [PROFILES, THEME, GLASS, EFFECTS, MOTION, BACKDROP];

    /// <summary>
    /// The parts of the settings window in the order they're shown, whichever mode a setting is (Dusk's theme is the
    /// terminal's and goes with Dawn's under how Hex looks).
    /// </summary>
    public static readonly string[] Parts = ["Appearance", "Editor", "Terminal", "Extensions"];

    /// <summary>The buttons of the picture's row, which one is which.</summary>
    public const int CHOOSE_PICTURE = 0, USUAL_PICTURE = 1;

    /// <summary>How long the caret takes to glide about two thirds of the way to where it went, as it comes.</summary>
    public const float CARET_GLIDE = 0.045f;

    public static readonly UISetting[] Settings =
    [
        new("effect_glass", HexText.Get("settings_catalog.effect_glass_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_glass_help"), GLASS),
        new("glass_blur", HexText.Get("settings_catalog.glass_blur_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_blur_help"), GLASS, Least: 0.0f, Most: 48.0f, Step: 1.0f, Slider: true, Unit: "px"),
        new("glass_clarity", HexText.Get("settings_catalog.glass_clarity_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_clarity_help"), GLASS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_rim", HexText.Get("settings_catalog.glass_rim_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_rim_help"), GLASS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_bend", HexText.Get("settings_catalog.glass_bend_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_bend_help"), GLASS, Least: 0.0f, Most: 16.0f, Step: 0.5f, Slider: true, Unit: "px"),

        new("effects", HexText.Get("settings_catalog.effects_title"), UIFormKind.Choice, HexText.Get("settings_catalog.effects_help"), EFFECTS, ["none", "subtle", "all"]),
        new("effect_sparks", HexText.Get("settings_catalog.effect_sparks_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_sparks_help"), EFFECTS),
        new("effect_caret_glow", HexText.Get("settings_catalog.effect_caret_glow_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_caret_glow_help"), EFFECTS),
        new("effect_spotlight", HexText.Get("settings_catalog.effect_spotlight_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_spotlight_help"), EFFECTS),
        new("effect_ripples", HexText.Get("settings_catalog.effect_ripples_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_ripples_help"), EFFECTS),
        new("effect_trail", HexText.Get("settings_catalog.effect_trail_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_trail_help"), EFFECTS),

        new("effect_motion", HexText.Get("settings_catalog.effect_motion_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_motion_help"), MOTION),
        new("ui_animation", HexText.Get("settings_catalog.ui_animation_title"), UIFormKind.Number, HexText.Get("settings_catalog.ui_animation_help"), MOTION, Least: 0.0f, Most: 3.0f, Step: 0.1f, Slider: true, Unit: "×"),
        new("caret_glide", HexText.Get("settings_catalog.caret_glide_title"), UIFormKind.Number, HexText.Get("settings_catalog.caret_glide_help"), MOTION, Least: 0.0f, Most: 0.3f, Step: 0.005f, Slider: true, Unit: "s"),

        new("backdrop", HexText.Get("settings_catalog.backdrop_title"), UIFormKind.Flag, HexText.Get("settings_catalog.backdrop_help"), BACKDROP),
        new("backdrop_terminal", HexText.Get("settings_catalog.backdrop_terminal_title"), UIFormKind.Flag, HexText.Get("settings_catalog.backdrop_terminal_help"), BACKDROP),
        new("backdrop_image", HexText.Get("settings_catalog.backdrop_image_title"), UIFormKind.Text, HexText.Get("settings_catalog.backdrop_image_help"), BACKDROP, Buttons: [HexText.Get("settings_catalog.backdrop_image_button_0"), HexText.Get("settings_catalog.backdrop_image_button_1")]),
        new("backdrop_opacity", HexText.Get("settings_catalog.backdrop_opacity_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_opacity_help"), BACKDROP, Least: 0.05f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_scale", HexText.Get("settings_catalog.backdrop_scale_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_scale_help"), BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("backdrop_across", HexText.Get("settings_catalog.backdrop_across_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_across_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_up", HexText.Get("settings_catalog.backdrop_up_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_up_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_scale", HexText.Get("settings_catalog.terminal_backdrop_scale_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_scale_help"), BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_across", HexText.Get("settings_catalog.terminal_backdrop_across_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_across_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_up", HexText.Get("settings_catalog.terminal_backdrop_up_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_up_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_wrap", HexText.Get("settings_catalog.backdrop_wrap_title"), UIFormKind.Flag, HexText.Get("settings_catalog.backdrop_wrap_help"), BACKDROP),
        new("backdrop_wrap_margin", HexText.Get("settings_catalog.backdrop_wrap_margin_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_wrap_margin_help"), BACKDROP, Least: 0.0f, Most: 60.0f, Slider: true, Unit: "px"),
        new("wrap_flow", HexText.Get("settings_catalog.wrap_flow_title"), UIFormKind.Number, HexText.Get("settings_catalog.wrap_flow_help"), BACKDROP, Least: 0.0f, Most: 40.0f, Slider: true, Unit: "px"),

        new("code_zoom", HexText.Get("settings_catalog.code_zoom_title"), UIFormKind.Number, HexText.Get("settings_catalog.code_zoom_help"), CODE, Least: 0.5f, Most: 3.0f, Step: 0.05f),
        new("code_font", HexText.Get("settings_catalog.code_font_title"), UIFormKind.Choice, HexText.Get("settings_catalog.code_font_help"), CODE, CodeFonts()),
        new("wrap_code", HexText.Get("settings_catalog.wrap_code_title"), UIFormKind.Flag, HexText.Get("settings_catalog.wrap_code_help"), CODE),
        new("git_blame", HexText.Get("settings_catalog.git_blame_title"), UIFormKind.Flag, HexText.Get("settings_catalog.git_blame_help"), CODE),
        new("minimap", HexText.Get("settings_catalog.minimap_title"), UIFormKind.Flag, HexText.Get("settings_catalog.minimap_help"), CODE),
        new("inlay_hints", HexText.Get("settings_catalog.inlay_hints_title"), UIFormKind.Flag, HexText.Get("settings_catalog.inlay_hints_help"), CODE),
        new("git_autofetch", HexText.Get("settings_catalog.git_autofetch_title"), UIFormKind.Flag, HexText.Get("settings_catalog.git_autofetch_help"), CODE),
        new("debugger_path", HexText.Get("settings_catalog.debugger_path_title"), UIFormKind.Text, HexText.Get("settings_catalog.debugger_path_help"), CODE),
        new("run_to_terminal", HexText.Get("settings_catalog.run_to_terminal_title"), UIFormKind.Flag, HexText.Get("settings_catalog.run_to_terminal_help"), CODE),

        new("reopen_last", HexText.Get("settings_catalog.reopen_last_title"), UIFormKind.Flag, HexText.Get("settings_catalog.reopen_last_help"), FILES),
        new("trim_whitespace", HexText.Get("settings_catalog.trim_whitespace_title"), UIFormKind.Flag, HexText.Get("settings_catalog.trim_whitespace_help"), FILES),
        new("final_newline", HexText.Get("settings_catalog.final_newline_title"), UIFormKind.Flag, HexText.Get("settings_catalog.final_newline_help"), FILES),
        new("auto_save", HexText.Get("settings_catalog.auto_save_title"), UIFormKind.Flag, HexText.Get("settings_catalog.auto_save_help"), FILES),
        new("auto_save_delay", HexText.Get("settings_catalog.auto_save_delay_title"), UIFormKind.Number, HexText.Get("settings_catalog.auto_save_delay_help"), FILES, Least: 0.5f, Most: 10.0f, Step: 0.5f, Slider: true, Unit: "s"),

        new("snap", HexText.Get("settings_catalog.snap_title"), UIFormKind.Whole, HexText.Get("settings_catalog.snap_help"), DESIGNER, Least: 0.0f, Most: 16.0f),
        new("canvas_handles", HexText.Get("settings_catalog.canvas_handles_title"), UIFormKind.Flag, HexText.Get("settings_catalog.canvas_handles_help"), DESIGNER)
    ];

    /// <summary>
    /// The fonts code can be written in, every folder of Assets/fonts with a .ttf of its own name in it, Hack first. The
    /// row was a line of text to type a folder's name into, and a name typed wrong was no font at all.
    /// </summary>
    private static string[] CodeFonts()
    {
        string fonts = Path.Combine(Horizon.Core.Platform.PlatformPaths.Program, "Assets", "fonts");
        if (!Directory.Exists(fonts))
            return ["hack"];

        return [.. Directory.GetDirectories(fonts).Select(Path.GetFileName).OfType<string>()
            .Where(name => File.Exists(Path.Combine(fonts, name, name + ".ttf")))
            .OrderBy(name => name == "hack" ? 0 : 1).ThenBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What every one of them comes as, the glass, the effects and the motion as the Fluid Glass look has them.</summary>
    public static readonly IReadOnlyDictionary<string, IRuntimeValue> Usual = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effects"] = new StringValue("none"),
        ["effect_glass"] = Values.Bool(true),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(true),
        ["effect_caret_glow"] = Values.Bool(true),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["glass_blur"] = new NumberValue(HexGlass.BLUR),
        ["glass_clarity"] = new NumberValue(HexGlass.CLARITY),
        ["glass_rim"] = new NumberValue(HexGlass.RIM),
        ["glass_bend"] = new NumberValue(HexGlass.BEND),
        ["ui_animation"] = new NumberValue(1.0f),
        ["caret_glide"] = new NumberValue(CARET_GLIDE),
        ["backdrop"] = Values.Bool(true),
        ["backdrop_terminal"] = Values.Bool(false),
        ["backdrop_image"] = new StringValue(string.Empty),
        ["backdrop_opacity"] = new NumberValue(0.22f),
        ["backdrop_scale"] = new NumberValue(0.5f),
        ["backdrop_across"] = new NumberValue(0.0f),
        ["backdrop_up"] = new NumberValue(0.0f),
        ["terminal_backdrop_scale"] = new NumberValue(0.5f),
        ["terminal_backdrop_across"] = new NumberValue(0.0f),
        ["terminal_backdrop_up"] = new NumberValue(0.0f),
        ["backdrop_wrap"] = Values.Bool(true),
        ["backdrop_wrap_margin"] = new NumberValue(8.0f),
        ["wrap_flow"] = new NumberValue(16.0f),
        ["code_zoom"] = new NumberValue(1.0f),
        ["code_font"] = new StringValue("hack"),
        ["wrap_code"] = Values.Bool(false),
        ["git_blame"] = Values.Bool(true),
        ["minimap"] = Values.Bool(true),
        ["inlay_hints"] = Values.Bool(true),
        ["git_autofetch"] = Values.Bool(true),
        ["debugger_path"] = new StringValue(string.Empty),
        ["run_to_terminal"] = Values.Bool(false),
        ["reopen_last"] = Values.Bool(false),
        ["trim_whitespace"] = Values.Bool(true),
        ["final_newline"] = Values.Bool(false),
        ["auto_save"] = Values.Bool(false),
        ["auto_save_delay"] = new NumberValue(1.0f),
        ["snap"] = new NumberValue(0.0f),
        ["canvas_handles"] = Values.Bool(true)
    };
}
