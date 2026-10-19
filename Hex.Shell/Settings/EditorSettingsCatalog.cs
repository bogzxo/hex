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
    public const string THEME = "Appearance · Theme", EFFECTS = "Appearance · Effects", BACKDROP = "Appearance · Backdrop",
        CODE = "Editor · Code", FILES = "Editor · Files", DESIGNER = "Editor · Designer";

    /// <summary>The buttons of the picture's row, which one is which.</summary>
    public const int CHOOSE_PICTURE = 0, USUAL_PICTURE = 1;

    public static readonly UISetting[] Settings =
    [
        new("effects", "Effects", UIFormKind.Choice, "None of them, subtle (the caret glides, a save or a long command that went well throws a burst of sparks, the popups sit on glass) or all (sparks off the caret as you type as well).", EFFECTS, ["none", "subtle", "all"]),
        new("effect_glass", "Glass", UIFormKind.Flag, "Whether the dialogs, the menus, the palette and the side panels sit over a blur of what is behind them, the way Windows 11 has its own.", EFFECTS),
        new("effect_sparks", "Sparks", UIFormKind.Flag, "Whether the effects that are on throw sparks, on a save, a long command that went well and as you type.", EFFECTS),
        new("effect_motion", "Caret glides", UIFormKind.Flag, "Whether the caret glides to where it goes rather than jumping there.", EFFECTS),
        new("effect_caret_glow", "Glow under the caret", UIFormKind.Flag, "Whether a soft light follows the caret about as you type, and the cursor in Dusk.", EFFECTS),
        new("effect_spotlight", "Spotlight", UIFormKind.Flag, "Whether a faint light sits under the pointer wherever it goes, the way Windows lit what you were over.", EFFECTS),
        new("effect_ripples", "Ripples", UIFormKind.Flag, "Whether a ring spreads out of every click.", EFFECTS),
        new("effect_trail", "Pointer trail", UIFormKind.Flag, "Whether the pointer leaves a few embers behind it when it is moved quickly.", EFFECTS),
        new("glass_blur", "Glass blur", UIFormKind.Number, "How far what is behind the glass is blurred, in pixels, 0 for clear glass. The side panels take a little more.", EFFECTS, Least: 0.0f, Most: 48.0f, Step: 1.0f, Slider: true),
        new("glass_clarity", "Glass clarity", UIFormKind.Number, "How much of what is behind the glass shows through the colour of the panel on it, 0 for none of it, 1 for all of it.", EFFECTS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_rim", "Glass rim", UIFormKind.Number, "How bright the silver lining round the edge of the glass is.", EFFECTS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_bend", "Glass bend", UIFormKind.Number, "How far the edge of the popups' glass bends what is behind it, the lens, in pixels, 0 for flat glass.", EFFECTS, Least: 0.0f, Most: 16.0f, Step: 0.5f, Slider: true),

        new("backdrop", "Behind the code", UIFormKind.Flag, "Whether a picture is drawn behind the code, in the bottom right corner of the editor.", BACKDROP),
        new("backdrop_terminal", "Behind the terminals", UIFormKind.Flag, "Whether the same picture is drawn behind Dusk's terminals as well.", BACKDROP),
        new("backdrop_image", "Picture", UIFormKind.Text, "The picture's file, a .png or a .jpg, empty for the one that comes with Hex.", BACKDROP, Buttons: ["Choose...", "The usual one"]),
        new("backdrop_opacity", "Opacity", UIFormKind.Number, "How much of the picture shows through what is written over it.", BACKDROP, Least: 0.05f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_scale", "Size behind the code", UIFormKind.Number, "How tall the picture is drawn behind the code, as a share of the window's height.", BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("backdrop_across", "In from the right behind the code", UIFormKind.Number, "How far the picture behind the code is in from the editor's right edge, as a share of the window's width.", BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_up", "Up from the bottom behind the code", UIFormKind.Number, "How far the picture behind the code is up from the editor's bottom edge, as a share of the window's height.", BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_scale", "Size behind the terminals", UIFormKind.Number, "How tall the picture is drawn behind Dusk's terminals, as a share of the window's height, its own size and not the code's.", BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_across", "In from the right behind the terminals", UIFormKind.Number, "How far the picture behind the terminals is in from their right edge, as a share of the window's width.", BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_up", "Up from the bottom behind the terminals", UIFormKind.Number, "How far the picture behind the terminals is up from their bottom edge, as a share of the window's height.", BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_wrap", "Code round the picture", UIFormKind.Flag, "Whether wrapped code runs round the shape of what is drawn in the picture rather than over it.", BACKDROP),
        new("backdrop_wrap_margin", "How far round", UIFormKind.Number, "How far the code keeps off the shape of the picture, in pixels.", BACKDROP, Least: 0.0f, Most: 60.0f, Slider: true),
        new("wrap_flow", "Words glide", UIFormKind.Number, "How quickly a word the wrap moves glides to its new place, 0 for snapping there.", BACKDROP, Least: 0.0f, Most: 40.0f, Slider: true),

        new("code_zoom", "Code zoom", UIFormKind.Number, "How big the code is written, 1 as it comes. Ctrl and the wheel over the code change it too.", CODE, Least: 0.5f, Most: 3.0f, Step: 0.05f),
        new("code_font", "Code font", UIFormKind.Text, "The font code is written in, a folder of Assets/fonts (hack, cascadia), empty for the skin's.", CODE),
        new("wrap_code", "Wrap code", UIFormKind.Flag, "Whether a long line of code wraps at the edge of the editor rather than running off it (Alt+Z).", CODE),
        new("debugger_path", ".NET debugger", UIFormKind.Text, "Where netcoredbg is, for F5 on C#. Empty looks in a netcoredbg folder next to Hex.exe, the settings folder and the PATH.", CODE),
        new("run_to_terminal", "Run in a terminal", UIFormKind.Flag, "Whether a file run with Ctrl+F5 runs in a terminal of the panel rather than in the output.", CODE),

        new("reopen_last", "Reopen the last workspace", UIFormKind.Flag, "Whether the workspace and the files that were open come back when Dawn starts.", FILES),
        new("trim_whitespace", "Trim trailing spaces", UIFormKind.Flag, "Whether the spaces and tabs at the ends of the lines of a file of code go when it is saved.", FILES),
        new("final_newline", "End with a newline", UIFormKind.Flag, "Whether a file of code is saved with a newline at its end when it hasn't one.", FILES),
        new("auto_save", "Save by itself", UIFormKind.Flag, "Whether a file of code is saved by itself a moment after typing in it stops (trailing spaces are left for a save by hand, you might be about to type after them).", FILES),
        new("auto_save_delay", "After", UIFormKind.Number, "How long typing has to have stopped for, in seconds.", FILES, Least: 0.5f, Most: 10.0f, Step: 0.5f, Slider: true),

        new("snap", "Snap", UIFormKind.Whole, "How many pixels the designer snaps what is dragged to, 0 for none.", DESIGNER, Least: 0.0f, Most: 16.0f),
        new("canvas_handles", "Handles on the canvas", UIFormKind.Flag, "Whether the designer draws the handles that drag a component bigger.", DESIGNER)
    ];

    /// <summary>What every one of them comes as.</summary>
    public static readonly IReadOnlyDictionary<string, IRuntimeValue> Usual = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effects"] = new StringValue("none"),
        ["effect_glass"] = Values.Bool(true),
        ["effect_sparks"] = Values.Bool(true),
        ["effect_motion"] = Values.Bool(true),
        ["effect_caret_glow"] = Values.Bool(true),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(true),
        ["effect_trail"] = Values.Bool(false),
        ["glass_blur"] = new NumberValue(HexGlass.BLUR),
        ["glass_clarity"] = new NumberValue(HexGlass.CLARITY),
        ["glass_rim"] = new NumberValue(HexGlass.RIM),
        ["glass_bend"] = new NumberValue(HexGlass.BEND),
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
