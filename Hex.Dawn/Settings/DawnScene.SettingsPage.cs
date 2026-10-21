using Hex.Shell;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Hex's settings in Dawn (Ctrl+, and File, Settings), the one window Dusk shows too, every setting there is in it by
// what it's about. How Hex looks (both modes' themes, Dawn's size, the effects, the picture behind the code and the
// terminals), the editor's code, files, panels and designer, the terminal's, and the extensions, the ones that are on and
// a section for each one's own settings (its layout of them, or a row a setting it described). The editor's are put in
// force the moment one changes (EditorSource), the terminal's through the terminal page's host. There used to be a page
// of some of them and a window of the rest, and what was where was anybody's guess.
internal sealed partial class DawnScene
{
    // The section the extensions that are on are in, and the one in front of each extension's own
    private const string EXTENSIONS = HexExtensions.SETTINGS, EXTENSION_SECTION = "Extensions · ";

    // What the editor's panels and Dawn's size are in
    private const string PANELS = "Editor · Panels";

    private SettingsWindow? settingsView;

    // What sets an extension's own settings, by extension_<id>_<key>, for the self test to find
    private readonly Dictionary<string, UIComponent> settingControls = [];

    /// <summary>The page while it is up, for the self test.</summary>
    internal SettingsPage? SettingsPage => settingsView?.Window.Parent is not null ? settingsView.Page : null;

    /// <summary>The window of the settings while it is up.</summary>
    private WindowPanel? SettingsWindow => settingsView?.Window.Parent is not null ? settingsView.Window : null;

    /// <summary>The settings, brought to the front when they're up, at a section when one is said.</summary>
    private void OpenSettings() => OpenSettings(null);

    private void OpenSettings(string? section)
    {
        if (SettingsWindow is { } open)
        {
            open.BringToFront();
            if (section is not null)
                settingsView!.Page.GoTo(section);
            settingsView!.Page.Search.Focus();
            return;
        }

        var sources = new List<ISettingsSource> { new EditorSource(this) };
        sources.AddRange(settingSources.Select(added => added.Source()));
        sources.Add(new TerminalSettingsSource(terminals, EditorSettingsCatalog.THEME));
        settingsView = new SettingsWindow(sources, CloseSettings, EditorSettingsCatalog.Parts);
        settingsView.Window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
        chrome.Root.Add(settingsView.Window);
        foreach (var added in settingSources)
        {
            if (added.Section is { } under && added.Panel is { } panel)
                settingsView.SetPanel(under, panel());
        }

        AddExtensionSections(settingsView);

        // The glass under its sliders, as they make it while they're dragged
        settingsView.SetPanel(EditorSettingsCatalog.EFFECTS, new GlassPreview(640.0f) { Look = () => (PopupGlass, SideGlass, Glassy) });
        foreach (var added in settingSources)
            added.Opened?.Invoke();

        if (section is not null)
            settingsView.Page.GoTo(section);
        settingsView.Page.Search.Focus();
    }

    /// <summary>Takes the settings down, the x of the window or Escape.</summary>
    private void CloseSettings()
    {
        if (settingsView?.Window is { Parent: { } parent } window)
            parent.Remove(window);
        settingsView = null;
        settingControls.Clear();
        foreach (var added in settingSources)
            added.Closed?.Invoke();
        ForgetExtensionTabs();
    }

    /// <summary>A section of the window for every extension that is on and has something to set, its own layout of it or a row a setting.</summary>
    private void AddExtensionSections(SettingsWindow view)
    {
        foreach (HexExtension extension in extensions.All)
        {
            if (!extension.Enabled || extension.Failed is not null || (extension.SettingsLayout is null && extension.Settings.Count == 0))
                continue;

            var body = new StackPanel { Spacing = 8.0f, Padding = new UIEdges(14.0f, 6.0f, 10.0f, 14.0f) };
            body.Add(new Label { Text = AboutExtension(extension), Markdown = true, Wrap = true, Size = new System.Numerics.Vector2(640.0f, 0.0f), Align = Horizon.Rendering.Origin.TopLeft });

            if (extension.SettingsLayout is { } own && MakeUI(extension, "settings page", own, body, null, null) is { } page)
            {
                settingsMade.Add(page);
                if (!extension.Raise("on_settings", extensions.HexOf(extension), page))
                    extensions.Failed(extension);
            }
            else
            {
                foreach (ExtensionSetting setting in extension.Settings)
                    AddExtensionSetting(extension, setting, body);
            }

            view.SetPanel(EXTENSION_SECTION + extension.Name, body);
        }
    }

    /// <summary>The skins and themes the designer can show a layout in, as the window lists them.</summary>
    private static List<(UISkinChoice Skin, string? Theme, string Text)> LayoutSkins()
    {
        var all = new List<(UISkinChoice, string?, string)>();
        foreach (UISkinChoice skin in UISkins.Available)
        {
            if (skin.Themes.Count == 0)
                all.Add((skin, null, skin.Title));
            else
                foreach (string theme in skin.Themes)
                    all.Add((skin, theme, $"{skin.Title}, {theme}"));
        }

        return all;
    }

    /// <summary>The editor's section, read from and put in force on the editor itself.</summary>
    private sealed class EditorSource(DawnScene hex) : ISettingsSource
    {
        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings { get; } =
        [
            new("editor_skin", Said("settings_page.dawn_theme"), UIFormKind.Choice, Said("settings_page.dawn_theme_help"), EditorSettingsCatalog.THEME,
                [.. EditorSkins().Select(choice => choice.Text)]),
            .. EditorSettingsCatalog.Settings,
            new("size", Said("settings_page.size"), UIFormKind.Choice, Said("settings_page.size_help"), PANELS, [.. Scales.Select(DescribeScale)]),
            new("panels", Said("settings.editor.panels"), UIFormKind.Text, Said("settings_page.panels_help"), PANELS, Buttons: [Said("settings_page.put_back")], ButtonsOnly: true),
            new("layout_skin", Said("settings_page.layout_skin"), UIFormKind.Choice, Said("settings_page.layout_skin_help"), EditorSettingsCatalog.DESIGNER,
                [.. LayoutSkins().Select(choice => choice.Text)]),
            new("disabled_extensions", Said("settings_page.switched_off"), UIFormKind.Flags, Said("settings_page.switched_off_help"), EXTENSIONS,
                [.. hex.extensions.All.Select(extension => extension.Id)])
        ];

        public IRuntimeValue? Get(string key) => key switch
        {
            "editor_skin" => new StringValue(EditorSkins().Find(choice => choice.Skin.Name == hex.settings.EditorSkin && (choice.Theme ?? string.Empty) == hex.settings.EditorTheme).Text ?? string.Empty),
            "size" => new StringValue(DescribeScale(hex.compositor.Scale)),
            "layout_skin" => new StringValue(LayoutSkins().Find(choice => choice.Skin.Name == UISkins.Current.Name && choice.Theme == (UISkins.Theme ?? UISkins.Current.UsualTheme)).Text ?? UISkins.Current.Title),
            "code_zoom" => new NumberValue(hex.settings.CodeZoom),
            "code_font" => new StringValue(hex.settings.CodeFont),
            "wrap_code" => Values.Bool(hex.settings.WrapCode),
            "run_to_terminal" => Values.Bool(hex.settings.RunToTerminal),
            "debugger_path" => new StringValue(hex.settings.DebuggerPath),
            "backdrop" => Values.Bool(hex.settings.BackdropOn),
            "backdrop_terminal" => Values.Bool(hex.settings.BackdropTerminal),
            "backdrop_image" => new StringValue(hex.settings.BackdropImage),
            "backdrop_opacity" => new NumberValue(hex.settings.BackdropOpacity),
            "backdrop_scale" => new NumberValue(hex.settings.BackdropScale),
            "backdrop_across" => new NumberValue(hex.settings.BackdropAcross),
            "backdrop_up" => new NumberValue(hex.settings.BackdropUp),
            "terminal_backdrop_scale" => new NumberValue(hex.settings.TerminalBackdropScale),
            "terminal_backdrop_across" => new NumberValue(hex.settings.TerminalBackdropAcross),
            "terminal_backdrop_up" => new NumberValue(hex.settings.TerminalBackdropUp),
            "backdrop_wrap" => Values.Bool(hex.settings.BackdropWrap),
            "backdrop_wrap_margin" => new NumberValue(hex.settings.BackdropWrapMargin),
            "wrap_flow" => new NumberValue(hex.settings.WrapFlow),
            "reopen_last" => Values.Bool(hex.settings.ReopenLast),
            "effects" => new StringValue(hex.settings.Effects),
            "effect_sparks" => Values.Bool(hex.settings.EffectSparks),
            "effect_motion" => Values.Bool(hex.settings.EffectMotion),
            "effect_glass" => Values.Bool(hex.settings.EffectGlass),
            "effect_caret_glow" => Values.Bool(hex.settings.EffectCaretGlow),
            "effect_spotlight" => Values.Bool(hex.settings.EffectSpotlight),
            "effect_ripples" => Values.Bool(hex.settings.EffectRipples),
            "effect_trail" => Values.Bool(hex.settings.EffectTrail),
            "glass_blur" => new NumberValue(hex.settings.GlassBlur),
            "glass_clarity" => new NumberValue(hex.settings.GlassClarity),
            "glass_rim" => new NumberValue(hex.settings.GlassRim),
            "glass_bend" => new NumberValue(hex.settings.GlassBend),
            "ui_animation" => new NumberValue(hex.settings.UiAnimation),
            "caret_glide" => new NumberValue(hex.settings.CaretGlide),
            "trim_whitespace" => Values.Bool(hex.settings.TrimWhitespace),
            "final_newline" => Values.Bool(hex.settings.FinalNewline),
            "auto_save" => Values.Bool(hex.settings.AutoSave),
            "auto_save_delay" => new NumberValue(hex.settings.AutoSaveDelay),
            "canvas_handles" => Values.Bool(hex.settings.CanvasHandles),
            "snap" => new NumberValue(hex.settings.Snap),
            "disabled_extensions" => Natives.List(hex.extensions.All.Where(extension => !extension.Enabled).Select(extension => extension.Id)),
            _ => null
        };

        public bool IsModified(string key) => key switch
        {
            "editor_skin" => hex.settings.EditorSkin != "flat" || hex.settings.EditorTheme != "horizon",
            "size" or "panels" or "layout_skin" => false,
            "disabled_extensions" => hex.extensions.All.Any(extension => !extension.Enabled),
            _ => !SettingValues.Same(Get(key), EditorSettingsCatalog.Usual.GetValueOrDefault(key))
        };

        public void Set(string key, IRuntimeValue value)
        {
            HexSettings settings = hex.settings;
            switch (key, value)
            {
                case ("editor_skin", StringValue chosen):
                    var skins = EditorSkins();
                    int index = skins.FindIndex(choice => choice.Text == chosen.Value);
                    if (index >= 0)
                        hex.UseEditorSkin(skins[index].Skin.Name, skins[index].Theme, save: true);
                    return;
                case ("size", StringValue sized):
                    int at = Array.FindIndex(Scales, scale => DescribeScale(scale) == sized.Value);
                    if (at >= 0)
                        hex.SetScale(Scales[at], remember: true);
                    return;
                case ("layout_skin", StringValue shownIn):
                    var layoutSkins = LayoutSkins();
                    int which = layoutSkins.FindIndex(choice => choice.Text == shownIn.Value);
                    if (which >= 0)
                        hex.UseSkin(layoutSkins[which].Skin, layoutSkins[which].Theme);
                    return;
                case ("code_zoom", NumberValue zoom):
                    hex.ZoomCode(zoom.Value / MathF.Max(0.01f, settings.CodeZoom));
                    return;
                case ("code_font", StringValue font):
                    settings.CodeFont = font.Value;
                    hex.codeEditing.ApplyCodeFont();
                    break;
                case ("wrap_code", BooleanValue wrap):
                    settings.WrapCode = wrap.Value;
                    foreach (HexDocument open in hex.documents)
                        open.Editor.WordWrap = wrap.Value;
                    break;
                case ("run_to_terminal", BooleanValue run):
                    settings.RunToTerminal = run.Value;
                    break;
                case ("backdrop", BooleanValue on):
                    settings.BackdropOn = on.Value;
                    hex.ApplyBackdrop();
                    break;
                case ("backdrop_terminal", BooleanValue terminals):
                    settings.BackdropTerminal = terminals.Value;
                    break;
                case ("backdrop_image", StringValue picture):
                    settings.BackdropImage = picture.Value.Length > 0 && File.Exists(picture.Value) ? Path.GetFullPath(picture.Value) : string.Empty;
                    hex.ApplyBackdrop();
                    break;
                case ("backdrop_opacity", NumberValue opacity):
                    settings.BackdropOpacity = Math.Clamp(opacity.Value, 0.0f, 1.0f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("backdrop_scale", NumberValue scale):
                    settings.BackdropScale = Math.Clamp(scale.Value, 0.05f, 1.5f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("backdrop_across", NumberValue across):
                    settings.BackdropAcross = Math.Clamp(across.Value, -0.5f, 1.0f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("backdrop_up", NumberValue up):
                    settings.BackdropUp = Math.Clamp(up.Value, -0.5f, 1.0f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("terminal_backdrop_scale", NumberValue terminalScale):
                    settings.TerminalBackdropScale = Math.Clamp(terminalScale.Value, 0.05f, 1.5f);
                    break;
                case ("terminal_backdrop_across", NumberValue terminalAcross):
                    settings.TerminalBackdropAcross = Math.Clamp(terminalAcross.Value, -0.5f, 1.0f);
                    break;
                case ("terminal_backdrop_up", NumberValue terminalUp):
                    settings.TerminalBackdropUp = Math.Clamp(terminalUp.Value, -0.5f, 1.0f);
                    break;
                case ("backdrop_wrap", BooleanValue wrapsRound):
                    settings.BackdropWrap = wrapsRound.Value;
                    hex.ApplyBackdrop();
                    break;
                case ("backdrop_wrap_margin", NumberValue margin):
                    settings.BackdropWrapMargin = Math.Clamp(margin.Value, 0.0f, 80.0f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("wrap_flow", NumberValue flow):
                    settings.WrapFlow = Math.Clamp(flow.Value, 0.0f, 60.0f);
                    hex.ShowBackdropEverywhere();
                    break;
                case ("effects", StringValue { Value: "none" or "subtle" or "all" } effects):
                    settings.Effects = effects.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_sparks", BooleanValue sparked):
                    settings.EffectSparks = sparked.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_motion", BooleanValue moved):
                    settings.EffectMotion = moved.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_glass", BooleanValue glassy):
                    settings.EffectGlass = glassy.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_caret_glow", BooleanValue effectcaretglow):
                    settings.EffectCaretGlow = effectcaretglow.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_spotlight", BooleanValue effectspotlight):
                    settings.EffectSpotlight = effectspotlight.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_ripples", BooleanValue effectripples):
                    settings.EffectRipples = effectripples.Value;
                    hex.ApplyEffects();
                    break;
                case ("effect_trail", BooleanValue effecttrail):
                    settings.EffectTrail = effecttrail.Value;
                    hex.ApplyEffects();
                    break;
                case ("glass_blur", NumberValue blur):
                    settings.GlassBlur = Math.Clamp(blur.Value, 0.0f, 64.0f);
                    hex.ApplyEffects();
                    break;
                case ("glass_clarity", NumberValue clarity):
                    settings.GlassClarity = Math.Clamp(clarity.Value, 0.0f, 1.0f);
                    hex.ApplyEffects();
                    break;
                case ("glass_rim", NumberValue rim):
                    settings.GlassRim = Math.Clamp(rim.Value, 0.0f, 1.0f);
                    hex.ApplyEffects();
                    break;
                case ("glass_bend", NumberValue bend):
                    settings.GlassBend = Math.Clamp(bend.Value, 0.0f, 24.0f);
                    hex.ApplyEffects();
                    break;
                case ("ui_animation", NumberValue speed):
                    settings.UiAnimation = Math.Clamp(speed.Value, 0.0f, 3.0f);
                    hex.ApplyEffects();
                    break;
                case ("caret_glide", NumberValue glide):
                    settings.CaretGlide = Math.Clamp(glide.Value, 0.0f, 0.3f);
                    hex.ApplyEffects();
                    break;
                case ("trim_whitespace", BooleanValue trims):
                    settings.TrimWhitespace = trims.Value;
                    break;
                case ("final_newline", BooleanValue ends):
                    settings.FinalNewline = ends.Value;
                    break;
                case ("auto_save", BooleanValue saves):
                    settings.AutoSave = saves.Value;
                    break;
                case ("auto_save_delay", NumberValue delay):
                    settings.AutoSaveDelay = Math.Clamp(delay.Value, 0.25f, 60.0f);
                    break;
                case ("debugger_path", StringValue debuggerPath):
                    settings.DebuggerPath = debuggerPath.Value.Trim().Trim('"');
                    break;
                case ("reopen_last", BooleanValue reopen):
                    settings.ReopenLast = reopen.Value;
                    break;
                case ("canvas_handles", BooleanValue handles):
                    settings.CanvasHandles = handles.Value;
                    break;
                case ("snap", NumberValue snap):
                    settings.Snap = Math.Clamp(MathF.Round(snap.Value), 0.0f, 16.0f);
                    break;
                case ("disabled_extensions", ListValue off):
                    var named = off.Items.OfType<StringValue>().Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (HexExtension extension in hex.extensions.All)
                    {
                        if (extension.Enabled == named.Contains(extension.Id))
                            hex.extensions.Switch(extension, !named.Contains(extension.Id));
                    }
                    return;
                default:
                    return;
            }

            hex.SaveSettings();
        }

        public void Reset(string key)
        {
            switch (key)
            {
                case "editor_skin":
                    hex.UseEditorSkin("flat", "horizon", save: true);
                    return;
                case "disabled_extensions":
                    Set(key, new ListValue([]));
                    return;
            }

            if (EditorSettingsCatalog.Usual.GetValueOrDefault(key) is { } usual)
                Set(key, usual);
        }

        public void Press(string key, int which)
        {
            switch (key, which)
            {
                case ("panels", _):
                    hex.ResetPanels();
                    break;
                case ("backdrop_image", EditorSettingsCatalog.CHOOSE_PICTURE):
                    hex.ChooseBackdrop();
                    break;
                case ("backdrop_image", EditorSettingsCatalog.USUAL_PICTURE):
                    Set(key, new StringValue(string.Empty));
                    hex.SettingsPage?.Refresh(key);
                    break;
            }
        }
    }
}
