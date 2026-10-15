using System.Globalization;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;

namespace Horizon.Hex;

/// <summary>
/// What the editor is set to and keeps from one run to the next, the look of itself, how its panels were dragged
/// and how it helps with lining things up. Kept next to the executable as a file anybody can read, hex_settings.hor.
/// The settings dialog (File, Settings) is where they are changed, the panels save their own sizes as they are let
/// go of. A file that is missing or makes no sense is everything at what it comes as.
/// </summary>
internal sealed class HexSettings
{
    public const string FILE = "hex_settings.hor";

    /// <summary>The grids there are to snap to, 0 being none.</summary>
    public static readonly float[] Grids = [0.0f, 2.0f, 4.0f, 8.0f, 16.0f];

    /// <summary>Which skin the editor itself is drawn in (the flat one unless somebody fancies otherwise) and in which of its themes.</summary>
    public string EditorSkin = "flat", EditorTheme = "dark";

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

    /// <summary>Whether long lines of code are wrapped at the edge of the editor (Alt+Z).</summary>
    public bool WrapCode;
    public string BackdropImage = string.Empty;
    public float BackdropScale = 0.55f, BackdropAcross, BackdropUp, BackdropOpacity = 0.22f;

    /// <summary>The workspace file that was open last, empty for one that was never saved (kept next to the editor instead).</summary>
    public string LastWorkspace = string.Empty;

    // The extensions that are switched off, by the name of their folder
    public readonly HashSet<string> DisabledExtensions = new(StringComparer.OrdinalIgnoreCase);

    // What every extension was told on its page of the settings, by the name of its folder and then of the setting.
    // Kept for one that isn't there any more as well, it might come back
    public readonly Dictionary<string, Dictionary<string, IRuntimeValue>> ExtensionSettings = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? path;

    /// <param name="path">Where it is kept, null for settings that are never read or written (the editor testing itself).</param>
    public HexSettings(string? path)
    {
        this.path = path;
        if (path is null || !File.Exists(path))
            return;

        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, message) = runtime.Evaluate(File.ReadAllText(path));
            if (!success || runtime.UserScope.Lookup("settings") is not ObjectValue settings)
                throw new Exception(success ? "it has to declare an object called 'settings'" : message);

            var values = settings.Properties;
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
            WrapCode = Flag(values, "wrap_code", WrapCode);
            if (values.TryGetValue("backdrop_image", out var picture) && picture is StringValue pictured) BackdropImage = pictured.Value;
            BackdropScale = Math.Clamp(Number(values, "backdrop_scale", BackdropScale), 0.05f, 4.0f);
            BackdropAcross = Number(values, "backdrop_across", BackdropAcross);
            BackdropUp = Number(values, "backdrop_up", BackdropUp);
            BackdropOpacity = Math.Clamp(Number(values, "backdrop_opacity", BackdropOpacity), 0.0f, 1.0f);
            if (values.TryGetValue("last_workspace", out var last) && last is StringValue lastWorkspace) LastWorkspace = lastWorkspace.Value;
            if (values.TryGetValue("disabled_extensions", out var disabled) && disabled is ListValue off)
            {
                foreach (IRuntimeValue name in off.Items)
                    if (name is StringValue { Value: { } named }) DisabledExtensions.Add(named);
            }

            if (values.TryGetValue("extension_settings", out var told) && told is ObjectValue byExtension)
            {
                foreach (var (id, its) in byExtension.Properties)
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

    /// <summary>Writes the settings to their file. Says what went wrong if they couldn't be, null if they could.</summary>
    public string? Save()
    {
        if (path is null)
            return null;

        static string Write(float value) => value.ToString(CultureInfo.InvariantCulture);
        static string Flag(bool value) => value ? "true" : "false";

        var text = new StringBuilder();
        text.AppendLine("// Written by the editor, change these in its settings (File, Settings) instead.");
        text.AppendLine("let settings = {");
        text.AppendLine($"    editor_skin: \"{EditorSkin}\",");
        text.AppendLine($"    editor_theme: \"{EditorTheme}\",");
        text.AppendLine($"    left_width: {Write(LeftWidth)},");
        text.AppendLine($"    right_width: {Write(RightWidth)},");
        text.AppendLine($"    layers_height: {Write(LayersHeight)},");
        text.AppendLine($"    panel_height: {Write(PanelHeight)},");
        text.AppendLine($"    side_bar_hidden: {Flag(SideBarHidden)},");
        text.AppendLine($"    panel_hidden: {Flag(PanelHidden)},");
        text.AppendLine($"    properties_hidden: {Flag(PropertiesHidden)},");
        text.AppendLine($"    snap: {Write(Snap)},");
        text.AppendLine($"    code_zoom: {Write(CodeZoom)},");
        text.AppendLine($"    canvas_handles: {Flag(CanvasHandles)},");
        text.AppendLine($"    reopen_last: {Flag(ReopenLast)},");
        text.AppendLine($"    backdrop: {Flag(BackdropOn)},");
        text.AppendLine($"    wrap_code: {Flag(WrapCode)},");
        text.AppendLine($"    backdrop_image: {HIDLWriter.Write(new StringValue(BackdropImage.Replace('\\', '/')))},");
        text.AppendLine($"    backdrop_scale: {Write(BackdropScale)},");
        text.AppendLine($"    backdrop_across: {Write(BackdropAcross)},");
        text.AppendLine($"    backdrop_up: {Write(BackdropUp)},");
        text.AppendLine($"    backdrop_opacity: {Write(BackdropOpacity)},");
        text.AppendLine($"    last_workspace: {HIDLWriter.Write(new StringValue(LastWorkspace.Replace('\\', '/')))},");
        text.AppendLine($"    disabled_extensions: {HIDLWriter.Write(Horizon.HIDL.Library.Natives.List(DisabledExtensions.Order(StringComparer.OrdinalIgnoreCase)))},");
        text.AppendLine("    extension_settings: {");
        var ids = ExtensionSettings.Where(told => told.Value.Count > 0).Select(told => told.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < ids.Count; i++)
        {
            var its = ExtensionSettings[ids[i]];
            string each = string.Join(", ", its.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(key => $"{key}: {HIDLWriter.Write(its[key])}"));
            text.AppendLine($"        {ids[i]}: {{ {each} }}{(i < ids.Count - 1 ? "," : string.Empty)}");
        }

        text.AppendLine("    }");
        text.AppendLine("};");

        try
        {
            File.WriteAllText(path, text.ToString());
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the settings for next time: {e.Message}";
        }
    }
}
