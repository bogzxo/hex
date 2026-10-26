using System.Globalization;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;
using Horizon.UI.Drawing;

namespace Hex.Core;

/// <summary>
/// What the editor is set to and keeps from one run to the next, the look of itself, how its panels were dragged
/// and how it helps with lining things up. Kept as the editor section of settings.hor in the settings folder next to the
/// executable, a file anybody can read (hex_settings.hor next to the exe is the old file of its own, read when that has none).
/// The settings dialog (File, Settings) is where they are changed, the panels save their own sizes as they are let
/// go of. A file that is missing or makes no sense is everything at what it comes as. Only what isn't what it comes as
/// is written, so the file says what somebody changed. The folder that's open can have settings of its own over these,
/// see HexSettings.Workspace.
/// </summary>
internal sealed partial class HexSettings
{
    public const string FILE = "hex_settings.hor";

    /// <summary>The grids there are to snap to, 0 being none.</summary>
    public static readonly float[] Grids = [0.0f, 2.0f, 4.0f, 8.0f, 16.0f];

    /// <summary>Which skin the editor itself is drawn in (the flat one unless somebody fancies otherwise) and in which of its themes.</summary>
    public string EditorSkin = "flat", EditorTheme = "horizon";

    /// <summary>How wide the lists on the left and the properties on the right were dragged, 0 for as their layout has them.</summary>
    public float LeftWidth, RightWidth;

    /// <summary>How tall the list of layers and the panel under the editor were dragged, 0 for what the editor works out by itself.</summary>
    public float LayersHeight, PanelHeight;

    /// <summary>Whether the side bar, the panel under the editor and the properties were put away.</summary>
    public bool SideBarHidden, PanelHidden, PropertiesHidden;

    /// <summary>Whether the panel under the editor was folded down to its tabs by its chevron.</summary>
    public bool PanelCollapsed;

    /// <summary>What positions and sizes land on a multiple of when something is dragged in the canvas, 0 for anywhere.</summary>
    public float Snap;

    /// <summary>How big the code is drawn against the rest of the editor, 1 for the size it comes at.</summary>
    public float CodeZoom = 1.0f;

    /// <summary>Whether what is selected in the canvas can be dragged bigger by its edges.</summary>
    public bool CanvasHandles = true;

    /// <summary>Whether the editor starts with the layout that was worked on last.</summary>
    public bool ReopenLast;

    /// <summary>
    /// The picture behind the code (see DawnScene.Backdrop), whether it's there, which (empty for the girl that comes with
    /// Hex), how big against its pixels, how far from the bottom right corner it is, along and up, and how much of it shows.
    /// </summary>
    public bool BackdropOn = true;

    /// <summary>Whether wrapped code runs round what is drawn in the picture instead of over it (see CodeEditor.BackdropShape).</summary>
    public bool BackdropWrap = true;

    /// <summary>Whether the picture is behind Dusk's terminals as well, see Dusk's Backdrop.</summary>
    public bool BackdropTerminal;

    /// <summary>Where the picture is behind Dusk's terminals and how big, its own place and size and not the code's.</summary>
    public float TerminalBackdropScale = 0.5f, TerminalBackdropAcross, TerminalBackdropUp;

    /// <summary>How far the wrapped code keeps off what is drawn in the picture, in the editor's pixels, round every edge.</summary>
    public float BackdropWrapMargin = 8.0f;

    /// <summary>How quickly a word the wrap moves glides to its new line (CodeEditor.FlowRate), 0 to snap.</summary>
    public float WrapFlow = Horizon.UI.Components.CodeEditor.FLOW_RATE;

    /// <summary>
    /// The effects, none, subtle (the lights, and a burst of sparks on a save when the sparks are on) or all (sparks as it
    /// types too), see DawnScene.Effects. The glass and the caret's glide have switches of their own and don't wait on it.
    /// </summary>
    public string Effects = "none";

    /// <summary>
    /// Whether the effects that are on throw sparks, whether the caret glides, and whether the dialogs, menus, palette and
    /// side panels sit over a blur of what is behind them. What it all comes as is the Fluid Glass look (Hex.Shell's HexLooks).
    /// </summary>
    public bool EffectSparks, EffectMotion = true, EffectGlass = true;

    /// <summary>The effects of light, a glow under the caret, a spotlight under the pointer, rings off clicks, embers behind the pointer.</summary>
    public bool EffectCaretGlow = true, EffectSpotlight, EffectRipples, EffectTrail;

    /// <summary>The look picked last in the settings by its name (one of Hex's three or one of the user's), empty for none.</summary>
    public string Look = "Fluid Glass";

    /// <summary>
    /// The looks the user saved, in the order they were made, each the visual settings it sets by their keys in this section
    /// (and terminal_theme for Dusk's). Hex.Shell's HexLooks is what reads and applies them.
    /// </summary>
    public readonly List<(string Name, Dictionary<string, IRuntimeValue> Values)> Looks = [];

    /// <summary>How thick the glass is, the popups' as they are and the sides' worked out of them (the engine's UIGlass.Clear to begin with, Hex.Shell's HexGlass draws it).</summary>
    public float GlassBlur = UIGlass.Clear.Blur, GlassClarity = UIGlass.Clear.Clarity, GlassRim = UIGlass.Clear.Rim, GlassBend = UIGlass.Clear.Bend;

    /// <summary>How quickly the UI moves (UICompositor.AnimationSpeed), 1 as it comes and 0 for not at all, and how long the caret glides for, 0 for jumping.</summary>
    public float UiAnimation = 1.0f, CaretGlide = 0.045f;

    /// <summary>How many columns wide a tab is and a level of indentation, 1 to 16.</summary>
    public int TabSize = 4;

    /// <summary>Whether Tab and indenting put in spaces rather than a tab.</summary>
    public bool InsertSpaces = true;

    /// <summary>Whether a file that is opened is indented the way it already is, tabs or so many spaces, rather than as set here.</summary>
    public bool DetectIndentation = true;

    /// <summary>Whether the code has its lines numbered down the left and guides down its indentation.</summary>
    public bool LineNumbers = true, IndentGuides = true;

    /// <summary>Whether a project's .editorconfig files have the last word on how its files are indented.</summary>
    public bool UseEditorConfig = true;

    /// <summary>The columns a faint line is drawn down the code at, VS Code's rulers, none to begin with.</summary>
    public int[] Rulers = [];

    /// <summary>Where the spaces and tabs are drawn, none, selection, boundary (leading and trailing) or all.</summary>
    public string RenderWhitespace = "selection";

    /// <summary>How the caret is drawn, line, block or underline.</summary>
    public string CursorStyle = "line";

    /// <summary>Whether the lines the top of the code is inside of stay held at the top as it scrolls.</summary>
    public bool StickyScroll = true;

    /// <summary>Whether Ctrl+S has the file's language server format it before it's written, when the server can.</summary>
    public bool FormatOnSave;

    /// <summary>Whether a file of code loses the spaces at the ends of its lines and gets a newline at its end when saved.</summary>
    public bool TrimWhitespace = true, FinalNewline;

    /// <summary>Whether a file of code saves itself once typing in it stops for this many seconds.</summary>
    public bool AutoSave;
    public float AutoSaveDelay = 1.0f;

    /// <summary>Whether long lines of code are wrapped at the edge of the editor (Alt+Z).</summary>
    public bool WrapCode;

    /// <summary>Whether the line the caret is on says faintly at its end who last changed it and when, git blame.</summary>
    public bool GitBlame = true;

    /// <summary>Whether the code is drawn small down the right of the editor, a slider over what's in sight.</summary>
    public bool Minimap = true;

    /// <summary>Whether the names of parameters and the like are written faintly in among the code, inlay hints.</summary>
    public bool InlayHints = true;

    /// <summary>Whether the workspace's repositories are fetched from their remotes every few minutes, for behind to be known.</summary>
    public bool GitAutoFetch = true;

    /// <summary>How many minutes go between those fetches.</summary>
    public float GitAutoFetchInterval = 3.0f;

    /// <summary>
    /// How a pull brings the branch up to date, default for however git config has it, merge, rebase, or ff_only for only
    /// when it can just move along.
    /// </summary>
    public string GitPullMode = "default";

    /// <summary>Whether a sync asks before it pulls and pushes, VS Code's git.confirmSync.</summary>
    public bool GitConfirmSync = true;

    /// <summary>What a commit with nothing staged does, ask whether to stage everything first, always just do it, or never.</summary>
    public string GitSmartCommit = "ask";

    /// <summary>How long a commit's summary is best kept to, and past how many characters a line is too long to show whole.</summary>
    public int GitSubjectLength = 50, GitLineLength = 72;

    /// <summary>Whether F5 says what a file printed and declared in the console of the terminal page rather than the output.</summary>
    public bool RunToTerminal;

    /// <summary>Where netcoredbg is, empty to look for it (DawnScene.Debugging).</summary>
    public string DebuggerPath = string.Empty;

    /// <summary>Where each language server is, by its name in language_servers.hor, for one that isn't the file's command on the PATH.</summary>
    public readonly Dictionary<string, string> ServerPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which language the files of a glob are, VS Code's files.associations, "*.weird" to python. The most specific
    /// glob wins (CodeLanguages.SetAssociations), plaintext for none.
    /// </summary>
    public readonly Dictionary<string, string> FileAssociations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The language servers added in the settings, one a language, in the order they were added. One for a language
    /// language_servers.hor has a server for as well is used instead of that one (HexLanguages).
    /// </summary>
    public readonly List<HexAddedServer> AddedServers = [];

    /// <summary>Whether Hex looks for new versions of the language servers on GitHub, once a day as it starts.</summary>
    public bool ServerUpdates = true;

    /// <summary>When it last looked (UTC, round trip), empty for never.</summary>
    public string ServersChecked = string.Empty;

    /// <summary>The newest version of each server it found, and the versions that were said not to be offered again.</summary>
    public readonly Dictionary<string, string> ServerLatest = new(StringComparer.OrdinalIgnoreCase), ServerSkipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The font the code is written in, a folder of Assets/fonts (hack), empty for the skin's (see DawnScene.CodeFont).</summary>
    public string CodeFont = "hack";
    public string BackdropImage = string.Empty;
    public float BackdropScale = 0.5f, BackdropAcross, BackdropUp, BackdropOpacity = 0.22f;

    /// <summary>The workspace file that was open last, empty for one that was never saved (kept in the settings folder instead).</summary>
    public string LastWorkspace = string.Empty;

    // The extensions that are switched off, by the name of their folder
    public readonly HashSet<string> DisabledExtensions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the extensions that can be installed come from, a GitHub repository (owner/name, its main branch) or a folder
    /// on this machine laid out the same, a folder an extension.
    /// </summary>
    public string ExtensionCatalog = DEFAULT_CATALOG;

    /// <summary>The catalog Hex comes set to, the extensions of its own repository on GitHub.</summary>
    public const string DEFAULT_CATALOG = "bogzxo/hex-extensions";

    /// <summary>Whether Hex looks at the catalog for new versions of the extensions installed out of it, once a day as it starts.</summary>
    public bool ExtensionUpdates = true;

    /// <summary>When it last looked (UTC, round trip), empty for never.</summary>
    public string ExtensionsChecked = string.Empty;

    // The extensions that were installed out of the catalog, by the name of their folder, the ones offered updates
    public readonly HashSet<string> CatalogExtensions = new(StringComparer.OrdinalIgnoreCase);

    // What every extension was told on its page of the settings, by the name of its folder and then of the setting.
    // Kept for one that isn't there any more as well, it might come back
    public readonly Dictionary<string, Dictionary<string, IRuntimeValue>> ExtensionSettings = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? path;

    /// <param name="path">Where it is kept, null for settings that are never read or written (the editor testing itself).</param>
    /// <param name="old">The file the editor's settings were in before they were a section of Hex's one, read when the section isn't there yet.</param>
    public HexSettings(string? path, string? old = null)
    {
        this.path = path;
        if (path is null)
            return;

        try
        {
            // The editor's section of Hex's settings file, the extensions' beside it, or the old file of its own
            Dictionary<string, IRuntimeValue> values;
            Dictionary<string, IRuntimeValue>? byExtension = null;
            var all = Horizon.UI.Components.SettingsSections.Read(path);
            if (all is not null && all.ContainsKey(Horizon.UI.Components.SettingsSections.EDITOR))
            {
                values = Horizon.UI.Components.SettingsSections.Section(all, Horizon.UI.Components.SettingsSections.EDITOR);
                byExtension = Horizon.UI.Components.SettingsSections.Section(all, Horizon.UI.Components.SettingsSections.EXTENSIONS);
            }
            else if (old is not null && File.Exists(old))
            {
                values = Horizon.UI.Components.SettingsSections.ReadText(File.ReadAllText(old));
            }
            else
            {
                return;
            }

            userValues = new Dictionary<string, IRuntimeValue>(values, StringComparer.Ordinal);
            Apply(values);

            if (byExtension is null && values.TryGetValue("extension_settings", out var told) && told is ObjectValue inOld)
                byExtension = inOld.Properties;
            if (byExtension is not null)
            {
                foreach (var (id, its) in byExtension)
                {
                    if (its is ObjectValue settingsOf)
                        ExtensionSettings[id] = new Dictionary<string, IRuntimeValue>(settingsOf.Properties, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[Hex] {path} makes no sense and is left alone, the editor comes up as it is made: {e.Message}");
        }
    }

    /// <summary>
    /// The editor's settings put in force from a section of them, the keys it has and nothing else. Once for the user's
    /// file, then again for the folder's own over it.
    /// </summary>
    private void Apply(Dictionary<string, IRuntimeValue> values)
    {
        if (values.TryGetValue("editor_skin", out var skin) && skin is StringValue skinned) EditorSkin = skinned.Value;
        if (values.TryGetValue("editor_theme", out var theme) && theme is StringValue themed) EditorTheme = themed.Value;
        LeftWidth = Number(values, "left_width", LeftWidth);
        RightWidth = Number(values, "right_width", RightWidth);
        LayersHeight = Number(values, "layers_height", LayersHeight);
        PanelHeight = Number(values, "panel_height", PanelHeight);
        SideBarHidden = Flag(values, "side_bar_hidden", SideBarHidden);
        PanelHidden = Flag(values, "panel_hidden", PanelHidden);
        PropertiesHidden = Flag(values, "properties_hidden", PropertiesHidden);
        PanelCollapsed = Flag(values, "panel_collapsed", PanelCollapsed);
        Snap = Number(values, "snap", Snap);
        CodeZoom = Math.Clamp(Number(values, "code_zoom", CodeZoom), 0.5f, 3.0f);
        if (values.TryGetValue("canvas_handles", out var handles) && handles is BooleanValue handled) CanvasHandles = handled.Value;
        if (values.TryGetValue("reopen_last", out var reopen) && reopen is BooleanValue reopened) ReopenLast = reopened.Value;
        BackdropOn = Flag(values, "backdrop", BackdropOn);
        BackdropWrap = Flag(values, "backdrop_wrap", BackdropWrap);
        BackdropTerminal = Flag(values, "backdrop_terminal", BackdropTerminal);
        TerminalBackdropScale = Math.Clamp(Number(values, "terminal_backdrop_scale", TerminalBackdropScale), 0.05f, 1.5f);
        TerminalBackdropAcross = Math.Clamp(Number(values, "terminal_backdrop_across", TerminalBackdropAcross), -0.5f, 1.0f);
        TerminalBackdropUp = Math.Clamp(Number(values, "terminal_backdrop_up", TerminalBackdropUp), -0.5f, 1.0f);
        BackdropWrapMargin = Math.Clamp(Number(values, "backdrop_wrap_margin", BackdropWrapMargin), 0.0f, 80.0f);
        WrapFlow = Math.Clamp(Number(values, "wrap_flow", WrapFlow), 0.0f, 60.0f);
        WrapCode = Flag(values, "wrap_code", WrapCode);
        GitBlame = Flag(values, "git_blame", GitBlame);
        Minimap = Flag(values, "minimap", Minimap);
        InlayHints = Flag(values, "inlay_hints", InlayHints);
        GitAutoFetch = Flag(values, "git_autofetch", GitAutoFetch);
        GitAutoFetchInterval = Math.Clamp(Number(values, "git_autofetch_interval", GitAutoFetchInterval), 1.0f, 60.0f);
        if (values.TryGetValue("git_pull_mode", out var pull) && pull is StringValue { Value: "default" or "merge" or "rebase" or "ff_only" } pulled) GitPullMode = pulled.Value;
        GitConfirmSync = Flag(values, "git_confirm_sync", GitConfirmSync);
        if (values.TryGetValue("git_smart_commit", out var smart) && smart is StringValue { Value: "ask" or "always" or "never" } smartly) GitSmartCommit = smartly.Value;
        GitSubjectLength = (int)Math.Clamp(Number(values, "git_subject_length", GitSubjectLength), 20.0f, 100.0f);
        GitLineLength = (int)Math.Clamp(Number(values, "git_line_length", GitLineLength), 40.0f, 120.0f);
        if (values.TryGetValue("effects", out var effects) && effects is StringValue { Value: "none" or "subtle" or "all" } effected) Effects = effected.Value;
        EffectSparks = Flag(values, "effect_sparks", EffectSparks);
        EffectMotion = Flag(values, "effect_motion", EffectMotion);
        EffectGlass = Flag(values, "effect_glass", EffectGlass);
        EffectCaretGlow = Flag(values, "effect_caret_glow", EffectCaretGlow);
        EffectSpotlight = Flag(values, "effect_spotlight", EffectSpotlight);
        EffectRipples = Flag(values, "effect_ripples", EffectRipples);
        EffectTrail = Flag(values, "effect_trail", EffectTrail);
        GlassBlur = Math.Clamp(Number(values, "glass_blur", GlassBlur), 0.0f, 64.0f);
        GlassClarity = Math.Clamp(Number(values, "glass_clarity", GlassClarity), 0.0f, 1.0f);
        GlassRim = Math.Clamp(Number(values, "glass_rim", GlassRim), 0.0f, 1.0f);
        GlassBend = Math.Clamp(Number(values, "glass_bend", GlassBend), 0.0f, 24.0f);
        UiAnimation = Math.Clamp(Number(values, "ui_animation", UiAnimation), 0.0f, 3.0f);
        CaretGlide = Math.Clamp(Number(values, "caret_glide", CaretGlide), 0.0f, 0.3f);
        if (values.TryGetValue("look", out var look) && look is StringValue looked) Look = looked.Value;
        if (values.TryGetValue("looks", out var looks) && looks is ObjectValue saved)
        {
            foreach (var (name, its) in saved.Properties)
            {
                if (its is ObjectValue set)
                    Looks.Add((name, new Dictionary<string, IRuntimeValue>(set.Properties, StringComparer.Ordinal)));
            }
        }
        TabSize = (int)Math.Clamp(Number(values, "tab_size", TabSize), 1.0f, 16.0f);
        InsertSpaces = Flag(values, "insert_spaces", InsertSpaces);
        DetectIndentation = Flag(values, "detect_indentation", DetectIndentation);
        LineNumbers = Flag(values, "line_numbers", LineNumbers);
        IndentGuides = Flag(values, "indent_guides", IndentGuides);
        UseEditorConfig = Flag(values, "editorconfig", UseEditorConfig);
        if (values.TryGetValue("rulers", out var rulers) && rulers is StringValue rulersSaid) Rulers = ParseRulers(rulersSaid.Value);
        if (values.TryGetValue("render_whitespace", out var whitespace) && whitespace is StringValue { Value: "none" or "selection" or "boundary" or "all" } whitespaced) RenderWhitespace = whitespaced.Value;
        if (values.TryGetValue("cursor_style", out var cursor) && cursor is StringValue { Value: "line" or "block" or "underline" } cursored) CursorStyle = cursored.Value;
        StickyScroll = Flag(values, "sticky_scroll", StickyScroll);
        FormatOnSave = Flag(values, "format_on_save", FormatOnSave);
        TrimWhitespace = Flag(values, "trim_whitespace", TrimWhitespace);
        FinalNewline = Flag(values, "final_newline", FinalNewline);
        AutoSave = Flag(values, "auto_save", AutoSave);
        AutoSaveDelay = Math.Clamp(Number(values, "auto_save_delay", AutoSaveDelay), 0.25f, 60.0f);
        RunToTerminal = Flag(values, "run_to_terminal", RunToTerminal);
        if (values.TryGetValue("debugger_path", out var debuggerAt) && debuggerAt is StringValue debuggerNamed) DebuggerPath = debuggerNamed.Value;
        Names(values, "language_server_paths", ServerPaths);
        if (values.ContainsKey("files_associations"))
        {
            FileAssociations.Clear();
            Names(values, "files_associations", FileAssociations);
        }
        ReadLanguageOverrides(values);
        ReadAddedServers(values);
        Names(values, "language_server_latest", ServerLatest);
        Names(values, "language_server_skipped", ServerSkipped);
        ServerUpdates = Flag(values, "language_server_updates", ServerUpdates);
        if (values.TryGetValue("language_servers_checked", out var checkedAt) && checkedAt is StringValue checkedWhen) ServersChecked = checkedWhen.Value;
        if (values.TryGetValue("code_font", out var font) && font is StringValue fontNamed) CodeFont = fontNamed.Value;
        if (values.TryGetValue("backdrop_image", out var picture) && picture is StringValue pictured) BackdropImage = pictured.Value;
        BackdropScale = Math.Clamp(Number(values, "backdrop_scale", BackdropScale), 0.05f, 1.5f);
        BackdropAcross = Math.Clamp(Number(values, "backdrop_across", BackdropAcross), -0.5f, 1.0f);
        BackdropUp = Math.Clamp(Number(values, "backdrop_up", BackdropUp), -0.5f, 1.0f);
        BackdropOpacity = Math.Clamp(Number(values, "backdrop_opacity", BackdropOpacity), 0.0f, 1.0f);
        if (values.TryGetValue("last_workspace", out var last) && last is StringValue lastWorkspace) LastWorkspace = lastWorkspace.Value;
        if (values.TryGetValue("disabled_extensions", out var disabled) && disabled is ListValue off)
        {
            foreach (IRuntimeValue name in off.Items)
                if (name is StringValue { Value: { } named }) DisabledExtensions.Add(named);
        }

        if (values.TryGetValue("extension_catalog", out var catalog) && catalog is StringValue { Value.Length: > 0 } catalogNamed) ExtensionCatalog = catalogNamed.Value;
        ExtensionUpdates = Flag(values, "extension_updates", ExtensionUpdates);
        if (values.TryGetValue("extensions_checked", out var catalogLooked) && catalogLooked is StringValue catalogLookedWhen) ExtensionsChecked = catalogLookedWhen.Value;
        if (values.TryGetValue("catalog_extensions", out var installed) && installed is ListValue fromCatalog)
        {
            foreach (IRuntimeValue name in fromCatalog.Items)
                if (name is StringValue { Value: { } named }) CatalogExtensions.Add(named);
        }
    }

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

    private static bool Flag(Dictionary<string, IRuntimeValue> values, string key, bool otherwise) =>
        values.TryGetValue(key, out var value) && value is BooleanValue flag ? flag.Value : otherwise;

    /// <summary>
    /// The columns of the rulers out of what was typed, "80, 120" being 80 and 120. Anything that isn't a whole number
    /// from 1 to 500 is dropped, so "abc, 90" is just 90, and the same column twice is once.
    /// </summary>
    public static int[] ParseRulers(string said)
    {
        var columns = new List<int>();
        foreach (string part in said.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int column) && column is >= 1 and <= 500 && !columns.Contains(column))
                columns.Add(column);
        }

        return [.. columns];
    }

    /// <summary>The rulers written the way the row shows them and the file keeps them, "80, 120".</summary>
    public static string RulersText(int[] columns) => string.Join(", ", columns);

    // Helper method to read an object of texts by name, what isn't text left out
    private static void Names(Dictionary<string, IRuntimeValue> values, string key, Dictionary<string, string> into)
    {
        if (!values.TryGetValue(key, out var value) || value is not ObjectValue named)
            return;

        foreach (var (name, said) in named.Properties)
        {
            if (said is StringValue { Value.Length: > 0 } text)
                into[name] = text.Value;
        }
    }

    // Helper method to read the language servers added in the settings, an object of them by their language
    private void ReadAddedServers(Dictionary<string, IRuntimeValue> values)
    {
        if (!values.TryGetValue("language_servers_added", out var value) || value is not ObjectValue added)
            return;

        static string[] Texts(ObjectValue of, string key) =>
            of.Properties.GetValueOrDefault(key) is ListValue list ? [.. list.Items.OfType<StringValue>().Select(item => item.Value)] : [];

        foreach (var (language, its) in added.Properties)
        {
            if (its is not ObjectValue server || server.Properties.GetValueOrDefault("command") is not StringValue { Value.Length: > 0 } command)
                continue;

            string name = server.Properties.GetValueOrDefault("name") is StringValue { Value.Length: > 0 } named ? named.Value : language;
            AddedServers.Add(new HexAddedServer(language, name, Texts(server, "extensions"), command.Value, Texts(server, "args")));
        }
    }

    // Helper method to write the added language servers, each its name, files, program and what it's started with
    private HIDLObject AddedServersObject()
    {
        static ListValue List(IEnumerable<string> texts) => new([.. texts.Select(text => (IRuntimeValue)new StringValue(text))]);

        var added = new HIDLObject();
        foreach (HexAddedServer server in AddedServers)
        {
            added.Add(server.Language, new HIDLObject()
                .Add("name", server.Name)
                .Add("extensions", List(server.Extensions))
                .Add("command", server.Command.Replace('\\', '/'))
                .Add("args", List(server.Arguments)));
        }

        return added;
    }

    // Helper method to write texts by name, in the order of their names
    private static HIDLObject Named(Dictionary<string, string> values, bool paths = false)
    {
        var named = new HIDLObject();
        foreach (var (name, value) in values.Where(pair => pair.Value.Length > 0).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            named.Add(name, paths ? value.Replace('\\', '/') : value);
        return named;
    }

    /// <summary>Writes the settings to their file. Says what went wrong if they couldn't be, null if they could.</summary>
    public string? Save()
    {
        if (path is null)
            return null;

        var extensions = new HIDLObject();
        foreach (string id in ExtensionSettings.Where(told => told.Value.Count > 0).Select(told => told.Key).Order(StringComparer.OrdinalIgnoreCase))
        {
            var its = new HIDLObject();
            foreach (var (key, value) in ExtensionSettings[id].OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                its.Add(key, value);
            extensions.Add(id, its);
        }

        // Only what isn't what it comes as. A setting the folder's own file covers is written as the user had it, the
        // folder's value is the folder's and never ends up in here
        var settings = new HIDLObject();
        foreach (var (key, value) in EditorValues())
        {
            // The languages files were set to by hand are a folder's, never the user's
            if (key == LANGUAGE_OVERRIDES)
                continue;

            IRuntimeValue? own = WorkspaceValues.ContainsKey(key) ? userValues.GetValueOrDefault(key) : value;
            if (own is not null && !IsUsual(key, own))
                settings.Add(key, own);
        }

        // the editor's and the extensions' sections of Hex's one settings file, the terminal's kept as it is
        string text = Horizon.UI.Components.SettingsSections.Write(path, (Horizon.UI.Components.SettingsSections.EDITOR, settings),
            (Horizon.UI.Components.SettingsSections.EXTENSIONS, extensions));

        try
        {
            File.WriteAllText(path, text);
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the settings for next time: {e.Message}";
        }
    }

    /// <summary>Every one of the editor's settings as it is now by its key, in the order the file has them.</summary>
    public Dictionary<string, IRuntimeValue> EditorValues()
    {
        // the user's looks in the order they were made, each the values it sets
        var looks = new HIDLObject();
        foreach (var (name, values) in Looks)
        {
            var its = new HIDLObject();
            foreach (var (key, value) in values)
                its.Add(key, value);
            looks.Add(name, its);
        }

        return new HIDLObject()
            .Add("editor_skin", EditorSkin)
            .Add("editor_theme", EditorTheme)
            .Add("left_width", LeftWidth)
            .Add("right_width", RightWidth)
            .Add("layers_height", LayersHeight)
            .Add("panel_height", PanelHeight)
            .Add("side_bar_hidden", SideBarHidden)
            .Add("panel_hidden", PanelHidden)
            .Add("properties_hidden", PropertiesHidden)
            .Add("panel_collapsed", PanelCollapsed)
            .Add("snap", Snap)
            .Add("code_zoom", CodeZoom)
            .Add("canvas_handles", CanvasHandles)
            .Add("reopen_last", ReopenLast)
            .Add("backdrop", BackdropOn)
            .Add("backdrop_wrap", BackdropWrap)
            .Add("backdrop_terminal", BackdropTerminal)
            .Add("terminal_backdrop_scale", TerminalBackdropScale)
            .Add("terminal_backdrop_across", TerminalBackdropAcross)
            .Add("terminal_backdrop_up", TerminalBackdropUp)
            .Add("backdrop_wrap_margin", BackdropWrapMargin)
            .Add("wrap_flow", WrapFlow)
            .Add("wrap_code", WrapCode)
            .Add("git_blame", GitBlame)
            .Add("minimap", Minimap)
            .Add("inlay_hints", InlayHints)
            .Add("git_autofetch", GitAutoFetch)
            .Add("git_autofetch_interval", GitAutoFetchInterval)
            .Add("git_pull_mode", GitPullMode)
            .Add("git_confirm_sync", GitConfirmSync)
            .Add("git_smart_commit", GitSmartCommit)
            .Add("git_subject_length", GitSubjectLength)
            .Add("git_line_length", GitLineLength)
            .Add("effects", Effects)
            .Add("effect_sparks", EffectSparks)
            .Add("effect_motion", EffectMotion)
            .Add("effect_glass", EffectGlass)
            .Add("effect_caret_glow", EffectCaretGlow)
            .Add("effect_spotlight", EffectSpotlight)
            .Add("effect_ripples", EffectRipples)
            .Add("effect_trail", EffectTrail)
            .Add("glass_blur", GlassBlur)
            .Add("glass_clarity", GlassClarity)
            .Add("glass_rim", GlassRim)
            .Add("glass_bend", GlassBend)
            .Add("ui_animation", UiAnimation)
            .Add("caret_glide", CaretGlide)
            .Add("look", Look)
            .Add("looks", looks)
            .Add("tab_size", TabSize)
            .Add("insert_spaces", InsertSpaces)
            .Add("detect_indentation", DetectIndentation)
            .Add("line_numbers", LineNumbers)
            .Add("indent_guides", IndentGuides)
            .Add("editorconfig", UseEditorConfig)
            .Add("rulers", RulersText(Rulers))
            .Add("render_whitespace", RenderWhitespace)
            .Add("cursor_style", CursorStyle)
            .Add("sticky_scroll", StickyScroll)
            .Add("format_on_save", FormatOnSave)
            .Add("trim_whitespace", TrimWhitespace)
            .Add("final_newline", FinalNewline)
            .Add("auto_save", AutoSave)
            .Add("auto_save_delay", AutoSaveDelay)
            .Add("run_to_terminal", RunToTerminal)
            .Add("debugger_path", DebuggerPath.Replace('\\', '/'))
            .Add("language_server_paths", Named(ServerPaths, paths: true))
            .Add("files_associations", Named(FileAssociations))
            .Add(LANGUAGE_OVERRIDES, LanguageOverridesObject())
            .Add("language_servers_added", AddedServersObject())
            .Add("language_server_updates", ServerUpdates)
            .Add("language_servers_checked", ServersChecked)
            .Add("language_server_latest", Named(ServerLatest))
            .Add("language_server_skipped", Named(ServerSkipped))
            .Add("code_font", CodeFont)
            .Add("backdrop_image", BackdropImage.Replace('\\', '/'))
            .Add("backdrop_scale", BackdropScale)
            .Add("backdrop_across", BackdropAcross)
            .Add("backdrop_up", BackdropUp)
            .Add("backdrop_opacity", BackdropOpacity)
            .Add("last_workspace", LastWorkspace.Replace('\\', '/'))
            .Add("disabled_extensions", Horizon.HIDL.Library.Natives.List(DisabledExtensions.Order(StringComparer.OrdinalIgnoreCase)))
            .Add("extension_catalog", ExtensionCatalog.Replace('\\', '/'))
            .Add("extension_updates", ExtensionUpdates)
            .Add("extensions_checked", ExtensionsChecked)
            .Add("catalog_extensions", Horizon.HIDL.Library.Natives.List(CatalogExtensions.Order(StringComparer.OrdinalIgnoreCase)))
            .ToValue().Properties;
    }
}
