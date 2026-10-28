using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The editor's settings that are a plain value of its section, described once for both modes, Dawn putting them in
/// force as they change and Dusk the ones that are its too (the effects, the backdrop) and writing the rest for Dawn to
/// take in. Dawn adds its theme, its size and the designer's skin, which only it knows the choices of. What they come as
/// is what Dawn's HexSettings starts with.
/// <para>
/// They're in the order the settings window shows them, by what they're about rather than which mode has them. The
/// editor first, since it's what a developer opens the settings for (its text, tabs and indentation at the very top,
/// then its code, files, git, running and debugging and its designer), then the terminal's own, then how Hex looks (the
/// profiles, the themes, the glass, the effects, the picture behind the code and the terminals), then the extensions.
/// </para>
/// </summary>
public static class EditorSettingsCatalog
{
    /// <summary>
    /// The sections, the part of the window before the dot and the section after it. Git and running a file have sections
    /// of their own under the editor, they were rows at the end of its code and the zoom and the debugger sat together.
    /// </summary>
    public const string PROFILES = "Appearance · Profiles", THEME = "Appearance · Theme", GLASS = "Appearance · Glass", EFFECTS = "Appearance · Effects",
        MOTION = "Appearance · Motion", BACKDROP = "Appearance · Backdrop", TEXT = "Editor · Text", CODE = "Editor · Code", FILES = "Editor · Files",
        GIT = "Editor · Git", RUN = "Editor · Run and debug", DESIGNER = "Editor · Designer", SEARCH = "Editor · Search";

    /// <summary>
    /// The sections in the order they're shown inside their parts, the editor's text first and the profiles first of how
    /// Hex looks, they're the whole of the rest of it in one go.
    /// </summary>
    public static readonly string[] Sections = [TEXT, CODE, FILES, SEARCH, GIT, RUN, DESIGNER, WindowsSource.GROUP, PROFILES, THEME, GLASS, EFFECTS, MOTION, BACKDROP];

    /// <summary>
    /// The parts of the settings window in the order they're shown, whichever mode a setting is (Dusk's theme is the
    /// terminal's and goes with Dawn's under how Hex looks). The editor comes first, the window opens on it.
    /// </summary>
    public static readonly string[] Parts = ["Editor", "Terminal", "Appearance", "Extensions"];

    /// <summary>
    /// How the code is typed and drawn, tabs, indentation, line numbers and guides, and whether a project's .editorconfig
    /// has the last word, the rulers, the whitespace drawn, the caret's look and sticky scroll, and formatting on save (a
    /// row of Editor, Files). Dawn's TextSettingsSource has them and puts them in force on every open file, they're not in
    /// <see cref="Settings"/> since those are the editor's own source's.
    /// </summary>
    public static readonly UISetting[] Text = Linked(
    [
        new("tab_size", HexText.Get("settings_text.tab_size_title"), UIFormKind.Whole, HexText.Get("settings_text.tab_size_help"), TEXT, Least: 1.0f, Most: 16.0f),
        new("insert_spaces", HexText.Get("settings_text.insert_spaces_title"), UIFormKind.Flag, HexText.Get("settings_text.insert_spaces_help"), TEXT),
        new("detect_indentation", HexText.Get("settings_text.detect_indentation_title"), UIFormKind.Flag, HexText.Get("settings_text.detect_indentation_help"), TEXT),
        new("line_numbers", HexText.Get("settings_text.line_numbers_title"), UIFormKind.Flag, HexText.Get("settings_text.line_numbers_help"), TEXT),
        new("indent_guides", HexText.Get("settings_text.indent_guides_title"), UIFormKind.Flag, HexText.Get("settings_text.indent_guides_help"), TEXT),
        new("editorconfig", HexText.Get("settings_text.editorconfig_title"), UIFormKind.Flag, HexText.Get("settings_text.editorconfig_help"), TEXT),
        new("rulers", HexText.Get("settings_text.rulers_title"), UIFormKind.Text, HexText.Get("settings_text.rulers_help"), TEXT, Placeholder: HexText.Get("settings_text.rulers_placeholder")),
        TextChoice("render_whitespace", ["none", "selection", "boundary", "all"]),
        TextChoice("cursor_style", ["line", "block", "underline"]),
        new("caret_fast_after", HexText.Get("settings_text.caret_fast_after_title"), UIFormKind.Number, HexText.Get("settings_text.caret_fast_after_help"), TEXT, Least: 0.0f, Most: 3.0f, Step: 0.1f, Slider: true, Unit: "s"),
        new("caret_fast_rate", HexText.Get("settings_text.caret_fast_rate_title"), UIFormKind.Number, HexText.Get("settings_text.caret_fast_rate_help"), TEXT, Least: 30.0f, Most: 200.0f, Step: 5.0f, Slider: true, Unit: "/s"),
        new("sticky_scroll", HexText.Get("settings_text.sticky_scroll_title"), UIFormKind.Flag, HexText.Get("settings_text.sticky_scroll_help"), TEXT),
        new("format_on_save", HexText.Get("settings_text.format_on_save_title"), UIFormKind.Flag, HexText.Get("settings_text.format_on_save_help"), FILES),
        Patterns("files_exclude"),
        Patterns("search_exclude"),
        new("search_use_ignore_files", HexText.Get("settings_text.search_use_ignore_files_title"), UIFormKind.Flag, HexText.Get("settings_text.search_use_ignore_files_help"), SEARCH)
    ]);

    /// <summary>
    /// A row of globs of Editor, Search, a line a pattern with its own pencil and bin. Its words are in settings_text,
    /// files_exclude_title, its sentence files_exclude_help and what a new one looks like files_exclude_placeholder. The
    /// button under them says Add Pattern (a list row's <see cref="UISetting.ValuePlaceholder"/>, it has no values).
    /// </summary>
    private static UISetting Patterns(string key) =>
        new(key, HexText.Get($"settings_text.{key}_title"), UIFormKind.List, HexText.Get($"settings_text.{key}_help"), SEARCH,
            KeyPlaceholder: HexText.Get($"settings_text.{key}_placeholder"), ValuePlaceholder: HexText.Get("settings_text.add_pattern"));

    /// <summary>The buttons of the picture's row, which one is which.</summary>
    public const int CHOOSE_PICTURE = 0, USUAL_PICTURE = 1;

    /// <summary>
    /// The row with the button that lays the picture out by hand, in Dawn behind the code and in Dusk behind the terminals,
    /// whichever window the settings are in (an ArrangeOverlay over the editor or the terminals). It keeps nothing of its own.
    /// </summary>
    public const string ARRANGE_BACKDROP = "backdrop_arrange";

    /// <summary>How long the caret takes to glide about two thirds of the way to where it went, as it comes.</summary>
    public const float CARET_GLIDE = 0.045f;

    public static readonly UISetting[] Settings = Linked(
    [
        new("effect_glass", HexText.Get("settings_catalog.effect_glass_title"), UIFormKind.Flag, HexText.Get("settings_catalog.effect_glass_help"), GLASS),
        new("glass_blur", HexText.Get("settings_catalog.glass_blur_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_blur_help"), GLASS, Least: 0.0f, Most: 48.0f, Step: 1.0f, Slider: true, Unit: "px"),
        new("glass_clarity", HexText.Get("settings_catalog.glass_clarity_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_clarity_help"), GLASS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_rim", HexText.Get("settings_catalog.glass_rim_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_rim_help"), GLASS, Least: 0.0f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("glass_bend", HexText.Get("settings_catalog.glass_bend_title"), UIFormKind.Number, HexText.Get("settings_catalog.glass_bend_help"), GLASS, Least: 0.0f, Most: 16.0f, Step: 0.5f, Slider: true, Unit: "px"),

        Choice("effects", EFFECTS, ["none", "subtle", "all"]),
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
        new("backdrop_image", HexText.Get("settings_catalog.backdrop_image_title"), UIFormKind.Text, HexText.Get("settings_catalog.backdrop_image_help"), BACKDROP, Buttons: [HexText.Get("settings_catalog.backdrop_image_button_0"), HexText.Get("settings_catalog.backdrop_image_button_1")], Placeholder: HexText.Get("settings_catalog.backdrop_image_placeholder")),
        new(ARRANGE_BACKDROP, HexText.Get("settings_catalog.backdrop_arrange_title"), UIFormKind.Text, HexText.Get("settings_catalog.backdrop_arrange_help"), BACKDROP, Buttons: [HexText.Get("settings_catalog.backdrop_arrange_button")], ButtonsOnly: true),
        new("backdrop_opacity", HexText.Get("settings_catalog.backdrop_opacity_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_opacity_help"), BACKDROP, Least: 0.05f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_scale", HexText.Get("settings_catalog.backdrop_scale_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_scale_help"), BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("backdrop_across", HexText.Get("settings_catalog.backdrop_across_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_across_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_up", HexText.Get("settings_catalog.backdrop_up_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_up_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("backdrop_stretch", HexText.Get("settings_catalog.backdrop_stretch_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_stretch_help"), BACKDROP, Least: Hex.Core.HexSettings.LEAST_STRETCH, Most: Hex.Core.HexSettings.MOST_STRETCH, Step: 0.01f, Slider: true, Unit: "×"),
        new("terminal_backdrop_scale", HexText.Get("settings_catalog.terminal_backdrop_scale_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_scale_help"), BACKDROP, Least: 0.05f, Most: 1.5f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_across", HexText.Get("settings_catalog.terminal_backdrop_across_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_across_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_up", HexText.Get("settings_catalog.terminal_backdrop_up_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_up_help"), BACKDROP, Least: -0.5f, Most: 1.0f, Step: 0.01f, Slider: true),
        new("terminal_backdrop_stretch", HexText.Get("settings_catalog.terminal_backdrop_stretch_title"), UIFormKind.Number, HexText.Get("settings_catalog.terminal_backdrop_stretch_help"), BACKDROP, Least: Hex.Core.HexSettings.LEAST_STRETCH, Most: Hex.Core.HexSettings.MOST_STRETCH, Step: 0.01f, Slider: true, Unit: "×"),
        new("backdrop_wrap", HexText.Get("settings_catalog.backdrop_wrap_title"), UIFormKind.Flag, HexText.Get("settings_catalog.backdrop_wrap_help"), BACKDROP),
        new("backdrop_wrap_margin", HexText.Get("settings_catalog.backdrop_wrap_margin_title"), UIFormKind.Number, HexText.Get("settings_catalog.backdrop_wrap_margin_help"), BACKDROP, Least: 0.0f, Most: 60.0f, Slider: true, Unit: "px"),
        new("wrap_flow", HexText.Get("settings_catalog.wrap_flow_title"), UIFormKind.Number, HexText.Get("settings_catalog.wrap_flow_help"), BACKDROP, Least: 0.0f, Most: 40.0f, Slider: true, Unit: "px"),

        new("code_zoom", HexText.Get("settings_catalog.code_zoom_title"), UIFormKind.Number, HexText.Get("settings_catalog.code_zoom_help"), CODE, Least: 0.5f, Most: 3.0f, Step: 0.05f, Slider: true, Unit: "×"),
        new("code_font", HexText.Get("settings_catalog.code_font_title"), UIFormKind.Choice, HexText.Get("settings_catalog.code_font_help"), CODE, CodeFonts()),
        new("wrap_code", HexText.Get("settings_catalog.wrap_code_title"), UIFormKind.Flag, HexText.Get("settings_catalog.wrap_code_help"), CODE),
        new("minimap", HexText.Get("settings_catalog.minimap_title"), UIFormKind.Flag, HexText.Get("settings_catalog.minimap_help"), CODE),
        new("inlay_hints", HexText.Get("settings_catalog.inlay_hints_title"), UIFormKind.Flag, HexText.Get("settings_catalog.inlay_hints_help"), CODE),
        new("code_lens_references", HexText.Get("settings_catalog.code_lens_references_title"), UIFormKind.Flag, HexText.Get("settings_catalog.code_lens_references_help"), CODE),
        new("semantic_highlighting", HexText.Get("settings_catalog.semantic_highlighting_title"), UIFormKind.Flag, HexText.Get("settings_catalog.semantic_highlighting_help"), CODE),
        Choice("csharp_colors", CODE, ["resharper", "visual_studio", "skin"]),

        new("reopen_last", HexText.Get("settings_catalog.reopen_last_title"), UIFormKind.Flag, HexText.Get("settings_catalog.reopen_last_help"), FILES),
        new("trim_whitespace", HexText.Get("settings_catalog.trim_whitespace_title"), UIFormKind.Flag, HexText.Get("settings_catalog.trim_whitespace_help"), FILES),
        new("final_newline", HexText.Get("settings_catalog.final_newline_title"), UIFormKind.Flag, HexText.Get("settings_catalog.final_newline_help"), FILES),
        new("auto_save", HexText.Get("settings_catalog.auto_save_title"), UIFormKind.Flag, HexText.Get("settings_catalog.auto_save_help"), FILES),
        new("auto_save_delay", HexText.Get("settings_catalog.auto_save_delay_title"), UIFormKind.Number, HexText.Get("settings_catalog.auto_save_delay_help"), FILES, Least: 0.5f, Most: 10.0f, Step: 0.5f, Slider: true, Unit: "s"),
        new("files_associations", HexText.Get("settings_catalog.files_associations_title"), UIFormKind.Pairs, HexText.Get("settings_catalog.files_associations_help"), FILES,
            KeyPlaceholder: HexText.Get("settings_catalog.files_associations_key_placeholder"), ValuePlaceholder: HexText.Get("settings_catalog.files_associations_value_placeholder")),

        new("git_blame", HexText.Get("settings_catalog.git_blame_title"), UIFormKind.Flag, HexText.Get("settings_catalog.git_blame_help"), GIT),
        new("git_autofetch", HexText.Get("settings_catalog.git_autofetch_title"), UIFormKind.Flag, HexText.Get("settings_catalog.git_autofetch_help"), GIT),
        new("git_autofetch_interval", HexText.Get("settings_catalog.git_autofetch_interval_title"), UIFormKind.Number, HexText.Get("settings_catalog.git_autofetch_interval_help"), GIT, Least: 1.0f, Most: 60.0f, Step: 1.0f, Slider: true, Unit: "min"),
        Choice("git_pull_mode", GIT, ["default", "merge", "rebase", "ff_only"]),
        new("git_account", HexText.Get("settings_catalog.git_account_title"), UIFormKind.Text, HexText.Get("settings_catalog.git_account_help"), GIT, Placeholder: HexText.Get("settings_catalog.git_account_placeholder")),
        new("git_confirm_sync", HexText.Get("settings_catalog.git_confirm_sync_title"), UIFormKind.Flag, HexText.Get("settings_catalog.git_confirm_sync_help"), GIT),
        Choice("git_smart_commit", GIT, ["ask", "always", "never"]),
        new("git_subject_length", HexText.Get("settings_catalog.git_subject_length_title"), UIFormKind.Whole, HexText.Get("settings_catalog.git_subject_length_help"), GIT, Least: 20.0f, Most: 100.0f),
        new("git_line_length", HexText.Get("settings_catalog.git_line_length_title"), UIFormKind.Whole, HexText.Get("settings_catalog.git_line_length_help"), GIT, Least: 40.0f, Most: 120.0f),

        new("debugger_path", HexText.Get("settings_catalog.debugger_path_title"), UIFormKind.Text, HexText.Get("settings_catalog.debugger_path_help"), RUN, Placeholder: HexText.Get("settings_catalog.debugger_path_placeholder")),
        new("run_to_terminal", HexText.Get("settings_catalog.run_to_terminal_title"), UIFormKind.Flag, HexText.Get("settings_catalog.run_to_terminal_help"), RUN),
        new("mcp_server", HexText.Get("settings_catalog.mcp_server_title"), UIFormKind.Flag, HexText.Get("settings_catalog.mcp_server_help"), RUN),

        new("snap", HexText.Get("settings_catalog.snap_title"), UIFormKind.Whole, HexText.Get("settings_catalog.snap_help"), DESIGNER, Least: 0.0f, Most: 16.0f),
        new("canvas_handles", HexText.Get("settings_catalog.canvas_handles_title"), UIFormKind.Flag, HexText.Get("settings_catalog.canvas_handles_help"), DESIGNER)
    ]);

    /// <summary>
    /// Every row given the other words it's searched by, out of settings_keywords by its key (word wrap and wordwrap for
    /// wrap_code), and the switch it hangs off when it's one of the rows that only mean something while that's on.
    /// </summary>
    private static UISetting[] Linked(UISetting[] rows) =>
        [.. rows.Select(row => row with { Keywords = KeywordsOf(row.Key), DependsOn = ParentOf(row.Key) })];

    // The words a row is found by besides its own, nothing for a row with none. HexText.Get gives a missing key back
    // with an @ in front, which the search would have found
    private static string KeywordsOf(string key) => HexText.Strings.GetValueOrDefault($"settings_keywords.{key}", string.Empty);

    // The switch a row hangs off, the delay off saving by itself, the picture's sizes and places off the picture being
    // on, the glass's blur and rim off the glass. Empty for a row that stands on its own. The picture's file isn't the
    // code's alone, the terminals draw it too, so it stays full strength with the code's picture off
    private static string ParentOf(string key) => key switch
    {
        "auto_save_delay" => "auto_save",
        "git_autofetch_interval" => "git_autofetch",
        "backdrop_wrap_margin" or "wrap_flow" => "backdrop_wrap",
        "backdrop_opacity" or "backdrop_scale" or "backdrop_across" or "backdrop_up" or "backdrop_stretch" or "backdrop_wrap" => "backdrop",
        _ when key.StartsWith("terminal_backdrop_", StringComparison.Ordinal) => "backdrop_terminal",
        _ when key.StartsWith("glass_", StringComparison.Ordinal) => "effect_glass",
        _ => string.Empty
    };

    /// <summary>
    /// A row of choices that says each of them in words with a line under it of what the one picked does, VS Code's
    /// descriptions of a setting's values. Its sentence, every choice's label and every choice's line are in
    /// settings_choices by its key, effects_row, effects_subtle and effects_subtle_help. Its name is the catalog's.
    /// </summary>
    private static UISetting Choice(string key, string group, string[] choices) =>
        new(key, HexText.Get($"settings_catalog.{key}_title"), UIFormKind.Choice, HexText.Get($"settings_choices.{key}_row"), group, choices,
            ChoiceLabels: [.. choices.Select(choice => HexText.Get($"settings_choices.{key}_{choice}"))],
            ChoiceHelp: [.. choices.Select(choice => HexText.Get($"settings_choices.{key}_{choice}_help"))]);

    /// <summary>
    /// The same for a row of Editor, Text, whose words are all in settings_text, render_whitespace_title, its sentence
    /// render_whitespace_help, and every choice's label and line, render_whitespace_all and render_whitespace_all_help.
    /// </summary>
    private static UISetting TextChoice(string key, string[] choices) =>
        new(key, HexText.Get($"settings_text.{key}_title"), UIFormKind.Choice, HexText.Get($"settings_text.{key}_help"), TEXT, choices,
            ChoiceLabels: [.. choices.Select(choice => HexText.Get($"settings_text.{key}_{choice}"))],
            ChoiceHelp: [.. choices.Select(choice => HexText.Get($"settings_text.{key}_{choice}_help"))]);

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

    /// <summary>The number a setting comes as, 0 for one that isn't a number.</summary>
    public static float UsualNumber(string key) => Usual.GetValueOrDefault(key) is NumberValue number ? number.Value : 0.0f;

    /// <summary>New-install and reset values. Flat surfaces and no decorative motion.</summary>
    public static readonly IReadOnlyDictionary<string, IRuntimeValue> Usual = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
    {
        ["effects"] = new StringValue("none"),
        ["effect_glass"] = Values.Bool(false),
        ["effect_sparks"] = Values.Bool(false),
        ["effect_motion"] = Values.Bool(false),
        ["effect_caret_glow"] = Values.Bool(false),
        ["effect_spotlight"] = Values.Bool(false),
        ["effect_ripples"] = Values.Bool(false),
        ["effect_trail"] = Values.Bool(false),
        ["glass_blur"] = new NumberValue(HexGlass.BLUR),
        ["glass_clarity"] = new NumberValue(HexGlass.CLARITY),
        ["glass_rim"] = new NumberValue(HexGlass.RIM),
        ["glass_bend"] = new NumberValue(HexGlass.BEND),
        ["ui_animation"] = new NumberValue(0.0f),
        ["caret_glide"] = new NumberValue(0.0f),
        ["backdrop"] = Values.Bool(false),
        ["backdrop_terminal"] = Values.Bool(false),
        ["backdrop_image"] = new StringValue(string.Empty),
        ["backdrop_opacity"] = new NumberValue(0.22f),
        ["backdrop_scale"] = new NumberValue(0.5f),
        ["backdrop_across"] = new NumberValue(0.0f),
        ["backdrop_up"] = new NumberValue(0.0f),
        ["backdrop_stretch"] = new NumberValue(1.0f),
        ["terminal_backdrop_scale"] = new NumberValue(0.5f),
        ["terminal_backdrop_across"] = new NumberValue(0.0f),
        ["terminal_backdrop_up"] = new NumberValue(0.0f),
        ["terminal_backdrop_stretch"] = new NumberValue(1.0f),
        ["backdrop_wrap"] = Values.Bool(true),
        ["backdrop_wrap_margin"] = new NumberValue(8.0f),
        ["wrap_flow"] = new NumberValue(16.0f),
        ["tab_size"] = new NumberValue(4.0f),
        ["insert_spaces"] = Values.Bool(true),
        ["detect_indentation"] = Values.Bool(true),
        ["line_numbers"] = Values.Bool(true),
        ["indent_guides"] = Values.Bool(true),
        ["editorconfig"] = Values.Bool(true),
        ["rulers"] = new StringValue(string.Empty),
        ["render_whitespace"] = new StringValue("selection"),
        ["cursor_style"] = new StringValue("line"),
        ["caret_fast_after"] = new NumberValue(1.0f),
        ["caret_fast_rate"] = new NumberValue(80.0f),
        ["sticky_scroll"] = Values.Bool(true),
        ["format_on_save"] = Values.Bool(false),
        ["code_zoom"] = new NumberValue(1.0f),
        ["code_font"] = new StringValue("hack"),
        ["wrap_code"] = Values.Bool(false),
        ["git_blame"] = Values.Bool(true),
        ["minimap"] = Values.Bool(true),
        ["inlay_hints"] = Values.Bool(true),
        ["code_lens_references"] = Values.Bool(true),
        ["semantic_highlighting"] = Values.Bool(true),
        ["csharp_colors"] = new StringValue("resharper"),
        ["git_autofetch"] = Values.Bool(true),
        ["git_autofetch_interval"] = new NumberValue(3.0f),
        ["git_pull_mode"] = new StringValue("default"),
        ["git_account"] = new StringValue(string.Empty),
        ["git_confirm_sync"] = Values.Bool(true),
        ["git_smart_commit"] = new StringValue("ask"),
        ["git_subject_length"] = new NumberValue(50.0f),
        ["git_line_length"] = new NumberValue(72.0f),
        ["debugger_path"] = new StringValue(string.Empty),
        ["run_to_terminal"] = Values.Bool(false),
        ["mcp_server"] = Values.Bool(false),
        ["reopen_last"] = Values.Bool(false),
        ["trim_whitespace"] = Values.Bool(true),
        ["final_newline"] = Values.Bool(false),
        ["auto_save"] = Values.Bool(false),
        ["auto_save_delay"] = new NumberValue(1.0f),
        ["files_associations"] = new ObjectValue(),
        ["files_exclude"] = Horizon.HIDL.Library.Natives.List(Hex.Core.HexSettings.UsualFilesExclude),
        ["search_exclude"] = Horizon.HIDL.Library.Natives.List(Hex.Core.HexSettings.UsualSearchExclude),
        ["search_use_ignore_files"] = Values.Bool(true),
        ["snap"] = new NumberValue(0.0f),
        ["canvas_handles"] = Values.Bool(true)
    };
}
