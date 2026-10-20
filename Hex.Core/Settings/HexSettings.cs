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
/// go of. A file that is missing or makes no sense is everything at what it comes as.
/// </summary>
internal sealed class HexSettings
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

    /// <summary>What positions and sizes land on a multiple of when something is dragged in the canvas, 0 for anywhere.</summary>
    public float Snap;

    /// <summary>How big the code is drawn against the rest of the editor, 1 for the size it comes at.</summary>
    public float CodeZoom = 1.0f;

    /// <summary>Whether what is selected in the canvas can be dragged bigger by its edges.</summary>
    public bool CanvasHandles = true;

    /// <summary>Whether the editor starts with the layout that was worked on last.</summary>
    public bool ReopenLast;

    /// <summary>
    /// The picture behind the code (see HexScene.Backdrop), whether it's there, which (empty for the girl that comes with
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

    /// <summary>The effects, none, subtle (the caret glides, a save throws sparks) or all (sparks as it types too), see HexScene.Effects.</summary>
    public string Effects = "none";

    /// <summary>
    /// Whether the effects that are on throw sparks, whether the caret glides, and whether the dialogs, menus, palette and
    /// side panels sit over a blur of what is behind them.
    /// </summary>
    public bool EffectSparks = true, EffectMotion = true, EffectGlass = true;

    /// <summary>The effects of light, a glow under the caret, a spotlight under the pointer, rings off clicks, embers behind the pointer.</summary>
    public bool EffectCaretGlow = true, EffectSpotlight, EffectRipples = true, EffectTrail;

    /// <summary>How thick the glass is, the popups' as they are and the sides' worked out of them (the engine's UIGlass.Clear to begin with, Hex.Shell's HexGlass draws it).</summary>
    public float GlassBlur = UIGlass.Clear.Blur, GlassClarity = UIGlass.Clear.Clarity, GlassRim = UIGlass.Clear.Rim, GlassBend = UIGlass.Clear.Bend;

    /// <summary>Whether a file of code loses the spaces at the ends of its lines and gets a newline at its end when saved.</summary>
    public bool TrimWhitespace = true, FinalNewline;

    /// <summary>Whether a file of code saves itself once typing in it stops for this many seconds.</summary>
    public bool AutoSave;
    public float AutoSaveDelay = 1.0f;

    /// <summary>Whether long lines of code are wrapped at the edge of the editor (Alt+Z).</summary>
    public bool WrapCode;

    /// <summary>Whether F5 says what a file printed and declared in the console of the terminal page rather than the output.</summary>
    public bool RunToTerminal;

    /// <summary>Where netcoredbg is, empty to look for it (HexScene.Debugging).</summary>
    public string DebuggerPath = string.Empty;

    /// <summary>Where each language server is, by its name in language_servers.hor, for one that isn't the file's command on the PATH.</summary>
    public readonly Dictionary<string, string> ServerPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether Hex looks for new versions of the language servers on GitHub, once a day as it starts.</summary>
    public bool ServerUpdates = true;

    /// <summary>When it last looked (UTC, round trip), empty for never.</summary>
    public string ServersChecked = string.Empty;

    /// <summary>The newest version of each server it found, and the versions that were said not to be offered again.</summary>
    public readonly Dictionary<string, string> ServerLatest = new(StringComparer.OrdinalIgnoreCase), ServerSkipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The font the code is written in, a folder of Assets/fonts (hack), empty for the skin's (see HexScene.CodeFont).</summary>
    public string CodeFont = "hack";
    public string BackdropImage = string.Empty;
    public float BackdropScale = 0.5f, BackdropAcross, BackdropUp, BackdropOpacity = 0.22f;

    /// <summary>The workspace file that was open last, empty for one that was never saved (kept in the settings folder instead).</summary>
    public string LastWorkspace = string.Empty;

    // The extensions that are switched off, by the name of their folder
    public readonly HashSet<string> DisabledExtensions = new(StringComparer.OrdinalIgnoreCase);

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

            if (values.TryGetValue("editor_skin", out var skin) && skin is StringValue skinned) EditorSkin = skinned.Value;
            if (values.TryGetValue("editor_theme", out var theme) && theme is StringValue themed) EditorTheme = themed.Value;
            LeftWidth = Number(values, "left_width", LeftWidth);
            RightWidth = Number(values, "right_width", RightWidth);
            LayersHeight = Number(values, "layers_height", LayersHeight);
            PanelHeight = Number(values, "panel_height", PanelHeight);
            SideBarHidden = Flag(values, "side_bar_hidden", SideBarHidden);
            PanelHidden = Flag(values, "panel_hidden", PanelHidden);
            PropertiesHidden = Flag(values, "properties_hidden", PropertiesHidden);
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
            TrimWhitespace = Flag(values, "trim_whitespace", TrimWhitespace);
            FinalNewline = Flag(values, "final_newline", FinalNewline);
            AutoSave = Flag(values, "auto_save", AutoSave);
            AutoSaveDelay = Math.Clamp(Number(values, "auto_save_delay", AutoSaveDelay), 0.25f, 60.0f);
            RunToTerminal = Flag(values, "run_to_terminal", RunToTerminal);
            if (values.TryGetValue("debugger_path", out var debuggerAt) && debuggerAt is StringValue debuggerNamed) DebuggerPath = debuggerNamed.Value;
            Names(values, "language_server_paths", ServerPaths);
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

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

    private static bool Flag(Dictionary<string, IRuntimeValue> values, string key, bool otherwise) =>
        values.TryGetValue(key, out var value) && value is BooleanValue flag ? flag.Value : otherwise;

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

        var settings = new HIDLObject()
            .Add("editor_skin", EditorSkin)
            .Add("editor_theme", EditorTheme)
            .Add("left_width", LeftWidth)
            .Add("right_width", RightWidth)
            .Add("layers_height", LayersHeight)
            .Add("panel_height", PanelHeight)
            .Add("side_bar_hidden", SideBarHidden)
            .Add("panel_hidden", PanelHidden)
            .Add("properties_hidden", PropertiesHidden)
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
            .Add("trim_whitespace", TrimWhitespace)
            .Add("final_newline", FinalNewline)
            .Add("auto_save", AutoSave)
            .Add("auto_save_delay", AutoSaveDelay)
            .Add("run_to_terminal", RunToTerminal)
            .Add("debugger_path", DebuggerPath.Replace('\\', '/'))
            .Add("language_server_paths", Named(ServerPaths, paths: true))
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
            .Add("disabled_extensions", Horizon.HIDL.Library.Natives.List(DisabledExtensions.Order(StringComparer.OrdinalIgnoreCase)));

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
}
