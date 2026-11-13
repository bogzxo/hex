using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// One setting of the editor's: the key it's kept under, how it's read out of a section of a settings file, how it's
/// written into one, and the ends it's held between when it has any. See <see cref="HexSettings.Rows"/>.
/// </summary>
internal sealed class HexSettingRow(string key, Action<HexSettings, Dictionary<string, IRuntimeValue>> read, Action<HexSettings, HIDLObject> write,
    float least = float.NaN, float most = float.NaN)
{
    /// <summary>What it's kept under in the file.</summary>
    public string Key => key;

    /// <summary>Whether it's a number held between two ends, <see cref="Least"/> and <see cref="Most"/>.</summary>
    public bool Ranged => !float.IsNaN(least);

    public float Least => least;

    public float Most => most;

    /// <summary>Taken out of a section of a file, left as it is when the section hasn't got it or has something else under its key.</summary>
    public void Read(HexSettings settings, Dictionary<string, IRuntimeValue> values) => read(settings, values);

    /// <summary>Written into a section as it is now.</summary>
    public void Write(HexSettings settings, HIDLObject written) => write(settings, written);
}

// The editor's settings as one table. A setting was written down in a line that read it, a line that wrote it and (for a
// number with ends) a line of the settings window's own table of ends, three places that had to say the same key and the
// same ends and were kept in step by a self test. Now each is one row here: reading a file goes down the rows, writing
// one goes down the rows, and the ends are the rows'. A new setting is its field and its row.
internal sealed partial class HexSettings
{
    /// <summary>
    /// Every setting of the editor's section, in the order the file has them. Most are a kind there are many of (a flag,
    /// a number between two ends, one of a few words), the rest say how they are read and written themselves.
    /// </summary>
    internal static readonly HexSettingRow[] Rows =
    [
        Text("editor_skin", settings => settings.EditorSkin, (settings, value) => settings.EditorSkin = value),
        Text("editor_theme", settings => settings.EditorTheme, (settings, value) => settings.EditorTheme = value),
        Free("left_width", settings => settings.LeftWidth, (settings, value) => settings.LeftWidth = value),
        Free("right_width", settings => settings.RightWidth, (settings, value) => settings.RightWidth = value),
        Free("layers_height", settings => settings.LayersHeight, (settings, value) => settings.LayersHeight = value),
        Free("panel_height", settings => settings.PanelHeight, (settings, value) => settings.PanelHeight = value),
        Flag("side_bar_hidden", settings => settings.SideBarHidden, (settings, value) => settings.SideBarHidden = value),
        Flag("panel_hidden", settings => settings.PanelHidden, (settings, value) => settings.PanelHidden = value),
        Flag("properties_hidden", settings => settings.PropertiesHidden, (settings, value) => settings.PropertiesHidden = value),
        Flag("panel_collapsed", settings => settings.PanelCollapsed, (settings, value) => settings.PanelCollapsed = value),
        Free("snap", settings => settings.Snap, (settings, value) => settings.Snap = value),
        Number("code_zoom", settings => settings.CodeZoom, (settings, value) => settings.CodeZoom = value, 0.5f, 3.0f),
        Speed("scroll_speed", settings => settings.ScrollSpeed, (settings, value) => settings.ScrollSpeed = value),
        Speed("middle_scroll_speed", settings => settings.MiddleScrollSpeed, (settings, value) => settings.MiddleScrollSpeed = value),
        Flag("canvas_handles", settings => settings.CanvasHandles, (settings, value) => settings.CanvasHandles = value),
        Flag("reopen_last", settings => settings.ReopenLast, (settings, value) => settings.ReopenLast = value),
        Flag("backdrop", settings => settings.BackdropOn, (settings, value) => settings.BackdropOn = value),
        Flag("backdrop_wrap", settings => settings.BackdropWrap, (settings, value) => settings.BackdropWrap = value),
        Flag("backdrop_terminal", settings => settings.BackdropTerminal, (settings, value) => settings.BackdropTerminal = value),
        Number("terminal_backdrop_scale", settings => settings.TerminalBackdropScale, (settings, value) => settings.TerminalBackdropScale = value, 0.05f, 1.5f),
        Number("terminal_backdrop_across", settings => settings.TerminalBackdropAcross, (settings, value) => settings.TerminalBackdropAcross = value, -0.5f, 1.0f),
        Number("terminal_backdrop_up", settings => settings.TerminalBackdropUp, (settings, value) => settings.TerminalBackdropUp = value, -0.5f, 1.0f),
        Number("terminal_backdrop_stretch", settings => settings.TerminalBackdropStretch, (settings, value) => settings.TerminalBackdropStretch = value, LEAST_STRETCH, MOST_STRETCH),
        Number("backdrop_wrap_margin", settings => settings.BackdropWrapMargin, (settings, value) => settings.BackdropWrapMargin = value, 0.0f, 80.0f),
        Number("wrap_flow", settings => settings.WrapFlow, (settings, value) => settings.WrapFlow = value, 0.0f, 60.0f),
        Flag("wrap_code", settings => settings.WrapCode, (settings, value) => settings.WrapCode = value),
        Flag("git_blame", settings => settings.GitBlame, (settings, value) => settings.GitBlame = value),
        Flag("minimap", settings => settings.Minimap, (settings, value) => settings.Minimap = value),
        Flag("inlay_hints", settings => settings.InlayHints, (settings, value) => settings.InlayHints = value),
        Flag("code_lens_references", settings => settings.CodeLensReferences, (settings, value) => settings.CodeLensReferences = value),
        Flag("semantic_highlighting", settings => settings.SemanticHighlighting, (settings, value) => settings.SemanticHighlighting = value),
        Special("visual_studio_keys",
            (settings, values) =>
            {
                // A file from before there were Visual Studio's keys, in the Visual Studio look, has them as the look has now
                bool lookOfVisualStudio = values.TryGetValue("look", out var lookNamed) && lookNamed is StringValue { Value: "Visual Studio" };
                settings.VisualStudioKeys = Flag(values, "visual_studio_keys", settings.VisualStudioKeys || lookOfVisualStudio);
            },
            (settings, written) => written.Add("visual_studio_keys", settings.VisualStudioKeys)),
        Flag("visual_studio_keys_chosen", settings => settings.VisualStudioKeysChosen, (settings, value) => settings.VisualStudioKeysChosen = value),
        Choice("csharp_colors", settings => settings.CSharpColors, (settings, value) => settings.CSharpColors = value, "resharper", "visual_studio", "skin"),
        Flag("mcp_server", settings => settings.McpServer, (settings, value) => settings.McpServer = value),
        Flag("git_autofetch", settings => settings.GitAutoFetch, (settings, value) => settings.GitAutoFetch = value),
        Number("git_autofetch_interval", settings => settings.GitAutoFetchInterval, (settings, value) => settings.GitAutoFetchInterval = value, 1.0f, 60.0f),
        Choice("git_pull_mode", settings => settings.GitPullMode, (settings, value) => settings.GitPullMode = value, "default", "merge", "rebase", "ff_only"),
        Special("git_account",
            (settings, values) =>
            {
                if (values.TryGetValue("git_account", out var account) && account is StringValue accountNamed) settings.GitAccount = accountNamed.Value.Trim();
            },
            (settings, written) => written.Add("git_account", settings.GitAccount)),
        Flag("git_confirm_sync", settings => settings.GitConfirmSync, (settings, value) => settings.GitConfirmSync = value),
        Choice("git_smart_commit", settings => settings.GitSmartCommit, (settings, value) => settings.GitSmartCommit = value, "ask", "always", "never"),
        Whole("git_subject_length", settings => settings.GitSubjectLength, (settings, value) => settings.GitSubjectLength = value, 20.0f, 100.0f),
        Whole("git_line_length", settings => settings.GitLineLength, (settings, value) => settings.GitLineLength = value, 40.0f, 120.0f),
        Choice("render_scale", settings => settings.RenderScale, (settings, value) => settings.RenderScale = value, "auto", "native", "manual"),
        Number("render_scale_percent", settings => settings.RenderScalePercent, (settings, value) => settings.RenderScalePercent = value, 25.0f, 100.0f),
        Choice("render_upscaler", settings => settings.RenderUpscaler, (settings, value) => settings.RenderUpscaler = value, "cas", "fsr1", "gsr1", "bilinear", "nearest"),
        Number("render_sharpness", settings => settings.RenderSharpness, (settings, value) => settings.RenderSharpness = value, 0.0f, 1.0f),
        Choice("render_composition", settings => settings.RenderComposition, (settings, value) => settings.RenderComposition = value, "native_ui", "whole_frame"),
        Choice("effects", settings => settings.Effects, (settings, value) => settings.Effects = value, "none", "subtle", "all"),
        Flag("effect_sparks", settings => settings.EffectSparks, (settings, value) => settings.EffectSparks = value),
        Flag("effect_motion", settings => settings.EffectMotion, (settings, value) => settings.EffectMotion = value),
        Flag("effect_glass", settings => settings.EffectGlass, (settings, value) => settings.EffectGlass = value),
        Flag("effect_caret_glow", settings => settings.EffectCaretGlow, (settings, value) => settings.EffectCaretGlow = value),
        Flag("effect_spotlight", settings => settings.EffectSpotlight, (settings, value) => settings.EffectSpotlight = value),
        Flag("effect_ripples", settings => settings.EffectRipples, (settings, value) => settings.EffectRipples = value),
        Flag("effect_trail", settings => settings.EffectTrail, (settings, value) => settings.EffectTrail = value),
        Number("glass_blur", settings => settings.GlassBlur, (settings, value) => settings.GlassBlur = value, 0.0f, 64.0f),
        Number("glass_clarity", settings => settings.GlassClarity, (settings, value) => settings.GlassClarity = value, 0.0f, 1.0f),
        Number("glass_rim", settings => settings.GlassRim, (settings, value) => settings.GlassRim = value, 0.0f, 1.0f),
        Number("glass_bend", settings => settings.GlassBend, (settings, value) => settings.GlassBend = value, 0.0f, 24.0f),
        Number("ui_animation", settings => settings.UiAnimation, (settings, value) => settings.UiAnimation = value, 0.0f, 3.0f),
        Number("caret_glide", settings => settings.CaretGlide, (settings, value) => settings.CaretGlide = value, 0.0f, 0.3f),
        Text("look", settings => settings.Look, (settings, value) => settings.Look = value),
        Special("looks",
            (settings, values) =>
            {
                if (values.TryGetValue("looks", out var looks) && looks is ObjectValue saved)
                {
                    foreach (var (name, its) in saved.Properties)
                    {
                        if (its is ObjectValue set)
                            settings.Looks.Add((name, new Dictionary<string, IRuntimeValue>(set.Properties, StringComparer.Ordinal)));
                    }
                }
            },
            (settings, written) =>
            {
                // the user's looks in the order they were made, each the values it sets
                var looks = new HIDLObject();
                foreach (var (name, values) in settings.Looks)
                {
                    var its = new HIDLObject();
                    foreach (var (key, value) in values)
                        its.Add(key, value);
                    looks.Add(name, its);
                }

                written.Add("looks", looks);
            }),
        Whole("tab_size", settings => settings.TabSize, (settings, value) => settings.TabSize = value, 1.0f, 16.0f),
        Flag("insert_spaces", settings => settings.InsertSpaces, (settings, value) => settings.InsertSpaces = value),
        Flag("detect_indentation", settings => settings.DetectIndentation, (settings, value) => settings.DetectIndentation = value),
        Flag("line_numbers", settings => settings.LineNumbers, (settings, value) => settings.LineNumbers = value),
        Flag("indent_guides", settings => settings.IndentGuides, (settings, value) => settings.IndentGuides = value),
        Flag("editorconfig", settings => settings.UseEditorConfig, (settings, value) => settings.UseEditorConfig = value),
        Special("rulers",
            (settings, values) =>
            {
                if (values.TryGetValue("rulers", out var rulers) && rulers is StringValue rulersSaid) settings.Rulers = ParseRulers(rulersSaid.Value);
            },
            (settings, written) => written.Add("rulers", RulersText(settings.Rulers))),
        Choice("render_whitespace", settings => settings.RenderWhitespace, (settings, value) => settings.RenderWhitespace = value, "none", "selection", "boundary", "all"),
        Choice("cursor_style", settings => settings.CursorStyle, (settings, value) => settings.CursorStyle = value, "line", "block", "underline"),
        Number("caret_fast_after", settings => settings.CaretFastAfter, (settings, value) => settings.CaretFastAfter = value, 0.0f, 3.0f),
        Number("caret_fast_rate", settings => settings.CaretFastRate, (settings, value) => settings.CaretFastRate = value, 30.0f, 200.0f),
        Flag("sticky_scroll", settings => settings.StickyScroll, (settings, value) => settings.StickyScroll = value),
        Flag("format_on_save", settings => settings.FormatOnSave, (settings, value) => settings.FormatOnSave = value),
        Flag("format_on_type", settings => settings.FormatOnType, (settings, value) => settings.FormatOnType = value),
        Flag("trim_whitespace", settings => settings.TrimWhitespace, (settings, value) => settings.TrimWhitespace = value),
        Flag("final_newline", settings => settings.FinalNewline, (settings, value) => settings.FinalNewline = value),
        Flag("auto_save", settings => settings.AutoSave, (settings, value) => settings.AutoSave = value),
        Number("auto_save_delay", settings => settings.AutoSaveDelay, (settings, value) => settings.AutoSaveDelay = value, 0.25f, 60.0f),
        Flag("run_to_terminal", settings => settings.RunToTerminal, (settings, value) => settings.RunToTerminal = value),
        OnDisk("debugger_path", settings => settings.DebuggerPath, (settings, value) => settings.DebuggerPath = value),
        Flag("debug_just_my_code", settings => settings.DebugJustMyCode, (settings, value) => settings.DebugJustMyCode = value),
        Special("exception_breakpoints",
            (settings, values) =>
            {
                if (values.GetValueOrDefault("exception_breakpoints") is ObjectValue exceptionFilters)
                {
                    settings.ExceptionBreakpoints.Clear();
                    foreach (var (name, value) in exceptionFilters.Properties)
                        if (value is BooleanValue enabled) settings.ExceptionBreakpoints[name] = enabled.Value;
                }
            },
            (settings, written) => written.Add("exception_breakpoints", settings.ExceptionBreakpointsObject())),
        Special("language_server_paths",
            (settings, values) => Names(values, "language_server_paths", settings.ServerPaths),
            (settings, written) => written.Add("language_server_paths", Named(settings.ServerPaths, paths: true))),
        Special("files_associations",
            (settings, values) =>
            {
                if (values.ContainsKey("files_associations"))
                {
                    settings.FileAssociations.Clear();
                    Names(values, "files_associations", settings.FileAssociations);
                }
            },
            (settings, written) => written.Add("files_associations", Named(settings.FileAssociations))),
        Special("files_exclude",
            (settings, values) =>
            {
                if (values.GetValueOrDefault("files_exclude") is ListValue filesExclude) settings.FilesExclude = Patterns(filesExclude);
            },
            (settings, written) => written.Add("files_exclude", Horizon.HIDL.Library.Natives.List(settings.FilesExclude))),
        Special("search_exclude",
            (settings, values) =>
            {
                if (values.GetValueOrDefault("search_exclude") is ListValue searchExclude) settings.SearchExclude = Patterns(searchExclude);
            },
            (settings, written) => written.Add("search_exclude", Horizon.HIDL.Library.Natives.List(settings.SearchExclude))),
        Flag("search_use_ignore_files", settings => settings.SearchUseIgnoreFiles, (settings, value) => settings.SearchUseIgnoreFiles = value),
        Special(LANGUAGE_OVERRIDES,
            (settings, values) => settings.ReadLanguageOverrides(values),
            (settings, written) => written.Add(LANGUAGE_OVERRIDES, settings.LanguageOverridesObject())),
        Special("language_servers_added",
            (settings, values) => settings.ReadAddedServers(values),
            (settings, written) => written.Add("language_servers_added", settings.AddedServersObject())),
        Flag("language_server_updates", settings => settings.ServerUpdates, (settings, value) => settings.ServerUpdates = value),
        Text("language_servers_checked", settings => settings.ServersChecked, (settings, value) => settings.ServersChecked = value),
        Special("language_server_latest",
            (settings, values) => Names(values, "language_server_latest", settings.ServerLatest),
            (settings, written) => written.Add("language_server_latest", Named(settings.ServerLatest))),
        Special("language_server_skipped",
            (settings, values) => Names(values, "language_server_skipped", settings.ServerSkipped),
            (settings, written) => written.Add("language_server_skipped", Named(settings.ServerSkipped))),
        Text("code_font", settings => settings.CodeFont, (settings, value) => settings.CodeFont = value),
        OnDisk("backdrop_image", settings => settings.BackdropImage, (settings, value) => settings.BackdropImage = value),
        Number("backdrop_scale", settings => settings.BackdropScale, (settings, value) => settings.BackdropScale = value, 0.05f, 1.5f),
        Number("backdrop_across", settings => settings.BackdropAcross, (settings, value) => settings.BackdropAcross = value, -0.5f, 1.0f),
        Number("backdrop_up", settings => settings.BackdropUp, (settings, value) => settings.BackdropUp = value, -0.5f, 1.0f),
        Number("backdrop_stretch", settings => settings.BackdropStretch, (settings, value) => settings.BackdropStretch = value, LEAST_STRETCH, MOST_STRETCH),
        Number("backdrop_opacity", settings => settings.BackdropOpacity, (settings, value) => settings.BackdropOpacity = value, 0.0f, 1.0f),
        OnDisk("last_workspace", settings => settings.LastWorkspace, (settings, value) => settings.LastWorkspace = value),
        Special("disabled_extensions",
            (settings, values) =>
            {
                if (values.TryGetValue("disabled_extensions", out var disabled) && disabled is ListValue off)
                {
                    foreach (IRuntimeValue name in off.Items)
                        if (name is StringValue { Value: { } named }) settings.DisabledExtensions.Add(named);
                }
            },
            (settings, written) => written.Add("disabled_extensions", Horizon.HIDL.Library.Natives.List(settings.DisabledExtensions.Order(StringComparer.OrdinalIgnoreCase)))),
        Special("extension_catalog",
            (settings, values) =>
            {
                if (values.TryGetValue("extension_catalog", out var catalog) && catalog is StringValue { Value.Length: > 0 } catalogNamed) settings.ExtensionCatalog = catalogNamed.Value;
            },
            (settings, written) => written.Add("extension_catalog", settings.ExtensionCatalog.Replace('\\', '/'))),
        Flag("extension_updates", settings => settings.ExtensionUpdates, (settings, value) => settings.ExtensionUpdates = value),
        Text("extensions_checked", settings => settings.ExtensionsChecked, (settings, value) => settings.ExtensionsChecked = value),
        Special("catalog_extensions",
            (settings, values) =>
            {
                if (values.TryGetValue("catalog_extensions", out var installed) && installed is ListValue fromCatalog)
                {
                    foreach (IRuntimeValue name in fromCatalog.Items)
                        if (name is StringValue { Value: { } named }) settings.CatalogExtensions.Add(named);
                }
            },
            (settings, written) => written.Add("catalog_extensions", Horizon.HIDL.Library.Natives.List(settings.CatalogExtensions.Order(StringComparer.OrdinalIgnoreCase))))
    ];

    private static Dictionary<string, HexSettingRow>? rowsByKey;
    private static Dictionary<string, (float Least, float Most)>? ranges;

    /// <summary>The row of a key, null for a key that is no setting of the editor's.</summary>
    internal static HexSettingRow? RowOf(string key) =>
        (rowsByKey ??= Rows.ToDictionary(row => row.Key, StringComparer.Ordinal)).GetValueOrDefault(key);

    /// <summary>
    /// The ends of every number that has them, by key: what a file's value is held to as it's read. A slider of the
    /// settings window offers no more than this and often less, the file is for whoever writes it by hand.
    /// </summary>
    public static IReadOnlyDictionary<string, (float Least, float Most)> Ranges =>
        ranges ??= Rows.Where(row => row.Ranged).ToDictionary(row => row.Key, row => (row.Least, row.Most), StringComparer.Ordinal);

    // Keys a file may have that are no row: where the extensions' settings were before they had a section of their own
    private static readonly string[] onceKeys = ["extension_settings"];

    /// <summary>Whether a key of the editor's section is one Hex reads, now or once. Anything else in a file is somebody else's and is kept.</summary>
    internal static bool IsKnown(string key) => RowOf(key) is not null || Array.IndexOf(onceKeys, key) >= 0;

    /* The kinds of row there are many of */

    // On or off
    private static HexSettingRow Flag(string key, Func<HexSettings, bool> get, Action<HexSettings, bool> set) => new(key,
        (settings, values) => set(settings, Flag(values, key, get(settings))),
        (settings, written) => written.Add(key, get(settings)));

    // A number held between two ends, as it's read and when the file hasn't got it as well (what it is already is held too)
    private static HexSettingRow Number(string key, Func<HexSettings, float> get, Action<HexSettings, float> set, float least, float most) => new(key,
        (settings, values) => set(settings, Math.Clamp(Number(values, key, get(settings)), least, most)),
        (settings, written) => written.Add(key, get(settings)), least, most);

    // A number taken as it is, a width somebody dragged a panel to
    private static HexSettingRow Free(string key, Func<HexSettings, float> get, Action<HexSettings, float> set) => new(key,
        (settings, values) => set(settings, Number(values, key, get(settings))),
        (settings, written) => written.Add(key, get(settings)));

    // How fast something scrolls, held to its ends and 1 for what is no number at all (ScrollSpeedOf)
    private static HexSettingRow Speed(string key, Func<HexSettings, float> get, Action<HexSettings, float> set) => new(key,
        (settings, values) => set(settings, ScrollSpeedOf(Number(values, key, get(settings)))),
        (settings, written) => written.Add(key, get(settings)), 0.25f, 5.0f);

    // A whole number between two ends
    private static HexSettingRow Whole(string key, Func<HexSettings, int> get, Action<HexSettings, int> set, float least, float most) => new(key,
        (settings, values) => set(settings, (int)Math.Clamp(Number(values, key, get(settings)), least, most)),
        (settings, written) => written.Add(key, get(settings)), least, most);

    // Any text
    private static HexSettingRow Text(string key, Func<HexSettings, string> get, Action<HexSettings, string> set) => new(key,
        (settings, values) =>
        {
            if (values.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text)
                set(settings, text.Value);
        },
        (settings, written) => written.Add(key, get(settings)));

    // Where something is on the disk, written with the slashes every system reads
    private static HexSettingRow OnDisk(string key, Func<HexSettings, string> get, Action<HexSettings, string> set) => new(key,
        (settings, values) =>
        {
            if (values.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text)
                set(settings, text.Value);
        },
        (settings, written) => written.Add(key, get(settings).Replace('\\', '/')));

    // One of a few words, anything else in the file left as it was
    private static HexSettingRow Choice(string key, Func<HexSettings, string> get, Action<HexSettings, string> set, params string[] allowed) => new(key,
        (settings, values) =>
        {
            if (values.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text && Array.IndexOf(allowed, text.Value) >= 0)
                set(settings, text.Value);
        },
        (settings, written) => written.Add(key, get(settings)));

    // One that says how it's read and written itself
    private static HexSettingRow Special(string key, Action<HexSettings, Dictionary<string, IRuntimeValue>> read, Action<HexSettings, HIDLObject> write) =>
        new(key, read, write);
}
