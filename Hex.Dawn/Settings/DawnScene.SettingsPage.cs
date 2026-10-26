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

    // How wide the words of a section that's a panel run (the keyboard's, an extension's about), at the page's zoom
    private const float PANEL_PROSE = 640.0f;

    // What the editor's panels and Dawn's size are in
    private const string PANELS = "Editor · Panels";

    private SettingsWindow? settingsView;

    /// <summary>The page while it is up, for the self test.</summary>
    internal SettingsPage? SettingsPage => settingsView?.Window.Parent is not null ? settingsView.Page : null;

    /// <summary>The window of the settings while it is up.</summary>
    private WindowPanel? SettingsWindow => settingsView?.Window.Parent is not null ? settingsView.Window : null;

    // What the keyboard's section is called, a panel that sends you to the window of the keys
    private const string KEYS_SECTION = "Editor · Keyboard shortcuts";

    // Whether Dawn's size was picked on its row this run, the self test keeps no file of it to go by
    private bool scalePicked;

    /// <summary>
    /// The settings, brought to the front when they're up. Ctrl+, in the settings file goes to the setting on the caret's
    /// line, the way back from the window's { }.
    /// </summary>
    private void OpenSettings()
    {
        string? key = SettingAtCaret();
        OpenSettings(null);
        if (key is not null)
            settingsView?.Reveal(key);
    }

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
        sources.Add(new WindowsSource());
        sources.Add(new DefaultTerminalSource(said => Notify(said, error: true)));
        sources.Add(new ExtensionSettingsSource(this));
        settingsView = new SettingsWindow(sources, CloseSettings, EditorSettingsCatalog.Parts, sections: EditorSettingsCatalog.Sections, texts: SettingsPageTexts.Make());

        // Dawn's window is the editor's, the other parts fold away the first time it opens and a search puts the
        // editor's rows a little over the rest. Reset all settings asks first
        settingsView.Page.HomePart = EDITOR_PART;
        settingsView.Page.Common = CommonSettings;

        // Drawn smaller than the screen would have it (a size picked small on a big screen, the self test at 1 on 4K), the
        // page zooms its text and its controls up to what the screen's usual size would draw them at
        settingsView.Page.Zoom = SettingsZoom;
        settingsView.Page.OnResetAllAsked = AskResetAllSettings;
        settingsView.Window.Color = compositor.Skin?.Palette.Raised;
        settingsView.Window.Glass = Glassy ? HexGlass.Window(PopupGlass) : null;
        settingsView.AddTitleAction("json", Said("settings_window.open_file"), OpenSettingsFileFromWindow);
        settingsView.AddTitleAction("keyboard", Said("settings_window.keyboard"), OpenKeysWindow);
        settingsPanels.Clear();
        AddScopeBar(settingsView);
        chrome.Root.Add(settingsView.Window);
        foreach (var added in settingSources)
        {
            if (added.Section is { } under && added.Panel is { } panel)
                SetSettingsPanel(settingsView, under, panel());
        }

        AddExtensionSections(settingsView);

        // The glass under its sliders, as they make it while they're dragged
        SetSettingsPanel(settingsView, EditorSettingsCatalog.GLASS, new GlassPreview(PANEL_PROSE * settingsView.Page.Zoom) { Look = () => (PopupGlass, SideGlass, Glassy) });

        // The keys aren't rows of these, they've a window of their own, but whoever searches here for them finds the way
        SetSettingsPanel(settingsView, KEYS_SECTION, KeysPanel());

        // The profiles of how Hex looks over all of it, see DawnScene.Looks
        AddLookPicker(settingsView);
        if (lookPicker is not null)
            settingsPanels[EditorSettingsCatalog.PROFILES] = lookPicker;
        foreach (var added in settingSources)
            added.Opened?.Invoke();

        // A section asked for goes there, otherwise the window opens where it was left this run, its section, its search
        // and its scroll. A setting gone to after this (Ctrl+, in the settings file) wins over that too
        if (section is not null)
            settingsView.Page.GoTo(section);
        else if (settingsLeft is { } left && (!options.SelfTest || reopenWhereLeftInTest))
            settingsView.Page.Restore(left.Group, left.Query, left.Offset);
        settingsView.Page.Search.Focus();
    }

    // Where the settings were left the last time the window closed this run, opened there again. Not kept in a file,
    // the next run starts at the top the way it always has
    private (string? Group, string Query, float Offset)? settingsLeft;

    // The self test opens the window afresh every time, what its parts go by, but for its own check of opening it where
    // it was left
    private bool reopenWhereLeftInTest;

    // The settings people change the most, at the top of the window as Commonly used in this order, VS Code's list for
    // the editor and the terminal's look and shell after it. One the window hasn't got a row for is passed over
    private static readonly string[] CommonSettings =
    [
        "tab_size", "insert_spaces", "code_font", "code_zoom", "wrap_code", "auto_save", "format_on_save", "render_whitespace", "rulers", "minimap",
        "editor_skin", "files_associations", "font", "text_scale", "profile"
    ];

    // The part of the settings that's the editor's, what Dawn's window is for
    private const string EDITOR_PART = "Editor";

    /// <summary>
    /// Reset all settings off the window's funnel or a part's right click, asked first with how many it puts back. On the
    /// folder's settings it's only what the folder's file sets, a reset there goes through the scope like any other.
    /// </summary>
    private void AskResetAllSettings()
    {
        if (settingsView is not { } view || view.Page.ModifiedCount is not (> 0 and var count))
            return;

        SettingsPage page = view.Page;
        string text = InWorkspaceScope && WorkspaceSettingsFolder() is { } folder
            ? SettingsTexts.Counted(Said("settings_page.reset_all_text_workspace_one"), Said("settings_page.reset_all_text_workspace_many"), count, ("name", FolderName(folder)))
            : SettingsTexts.Counted(Said("settings_page.reset_all_text_one"), Said("settings_page.reset_all_text_many"), count);

        // Risky, so Enter is Cancel and not the reset
        UIDialog.Show(chrome, Said("settings_page.reset_all_title"), text,
            new DialogChoice(Said("settings_page.reset_all_yes"), () => page.ResetAll(), Risky: true),
            new DialogChoice(Said("settings_page.reset_all_no")));
    }

    /// <summary>Takes the settings down, the x of the window or Escape.</summary>
    private void CloseSettings()
    {
        if (settingsView?.Window is { Parent: { } parent } window)
        {
            settingsLeft = settingsView.Page.Place;
            parent.Remove(window);
        }
        settingsView = null;
        lookPicker = null;
        userScopeTab = workspaceScopeTab = null;
        scopeLine = null;
        workspaceScope = false;
        everySetting = null;
        settingsPanels.Clear();
        foreach (var added in settingSources)
            added.Closed?.Invoke();
        ForgetExtensionTabs();
    }

    /// <summary>
    /// What every extension that is on and has something to set is, over its section of the window, and its own layout of
    /// them when it has one. The rows of the settings it described are ExtensionSettingsSource's.
    /// </summary>
    private void AddExtensionSections(SettingsWindow view)
    {
        extensionSections.Clear();
        foreach (HexExtension extension in extensions.All)
        {
            if (!extension.Enabled || extension.Failed is not null || (extension.SettingsLayout is null && extension.Settings.Count == 0))
                continue;

            var body = new StackPanel { Spacing = 8.0f, Padding = new UIEdges(14.0f, 6.0f, 10.0f, 14.0f) };
            body.Add(new Label { Text = AboutExtension(extension), Markdown = true, Wrap = true, Size = new System.Numerics.Vector2(PANEL_PROSE * view.Page.Zoom, 0.0f), Align = Horizon.Rendering.Origin.TopLeft });

            if (extension.SettingsLayout is { } own && MakeUI(extension, "settings page", own, body, null, null) is { } page)
            {
                settingsMade.Add(page);
                if (!extension.Raise("on_settings", extensions.HexOf(extension), page))
                    extensions.Failed(extension);
            }

            SetSettingsPanel(view, EXTENSION_SECTION + extension.Name, body);
            extensionSections.Add(EXTENSION_SECTION + extension.Name);
        }
    }

    // The sections the extensions that are on have in the window, taken away again when one is switched off
    private readonly List<string> extensionSections = [];

    /// <summary>
    /// The extensions' sections made again, after one was switched on or off with the window up. One switched off left
    /// its section behind, one switched on had none until the window was opened again.
    /// </summary>
    private void RebuildExtensionSections()
    {
        if (settingsView is not { } view)
            return;

        foreach (string group in extensionSections)
            SetSettingsPanel(view, group, null);
        ForgetExtensionTabs();
        AddExtensionSections(view);
    }

    /// <summary>The keyboard's section, what it's about and a button to the window of the keys.</summary>
    private StackPanel KeysPanel()
    {
        var body = new StackPanel { Spacing = 10.0f, Padding = new UIEdges(14.0f, 6.0f, 10.0f, 14.0f) };
        float zoom = settingsView?.Page.Zoom ?? 1.0f;
        float prose = PANEL_PROSE * zoom;
        body.Add(new Label { Text = Said("settings_window.keys_about"), Wrap = true, Size = new System.Numerics.Vector2(prose, 0.0f), Align = Horizon.Rendering.Origin.TopLeft });
        body.Add(new Button { Label = Said("settings_window.keys_button"), Icon = "keyboard", Size = new System.Numerics.Vector2(240.0f * zoom, 30.0f * zoom), OnPressed = OpenKeysWindow });
        return body;
    }

    /// <summary>The size Dawn is drawn at when nobody picked one, what the window's height says.</summary>
    private float UsualScale => ScaleFor(Engine.WindowManager.ViewportSize);

    /// <summary>How much the settings page zooms, what the usual size would draw Dawn at over what it's drawn at, never smaller.</summary>
    private float SettingsZoom => MathF.Max(1.0f, UsualScale / MathF.Max(0.25f, compositor.Scale));

    /// <summary>Dawn back at the size the window says, the one that was picked forgotten so the next run goes by the window too.</summary>
    private void UseUsualScale()
    {
        SetScale(UsualScale, remember: false);
        scalePicked = false;
        if (options.SelfTest)
            return;

        try
        {
            File.Delete(ScalePath);
        }
        catch (Exception e)
        {
            Say(Said("chrome.scale_not_kept", ("reason", e.Message)), error: true);
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

    /// <summary>
    /// What a skin and theme the designer can show a layout in says in the settings, its theme in words with a capital,
    /// "Horizon, Solarized dark". The value stays the way the View menu writes it, the theme as the skin names it.
    /// </summary>
    private static string LayoutSkinLabel((UISkinChoice Skin, string? Theme, string Text) choice)
    {
        if (choice.Theme is not { Length: > 0 } theme)
            return choice.Text;

        string named = theme.Replace('_', ' ');
        return $"{choice.Skin.Title}, {char.ToUpperInvariant(named[0])}{named[1..]}";
    }

    /// <summary>The editor's section, read from and put in force on the editor itself.</summary>
    private sealed class EditorSource(DawnScene hex) : ISettingsSource
    {
        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings { get; } =
        [
            new("editor_skin", Said("settings_page.dawn_theme"), UIFormKind.Choice, Said("settings_page.dawn_theme_help"), EditorSettingsCatalog.THEME,
                [.. EditorSkins().Select(choice => choice.Text)], Buttons: [Said("settings_window.preview_themes")]),
            .. EditorSettingsCatalog.Settings,
            new("size", Said("settings_page.size"), UIFormKind.Choice, Said("settings_page.size_help"), PANELS, [.. Scales.Select(DescribeScale)]),
            new("panels", Said("settings.editor.panels"), UIFormKind.Text, Said("settings_page.panels_help"), PANELS, Buttons: [Said("settings_page.put_back")], ButtonsOnly: true),
            new("layout_skin", Said("settings_page.layout_skin"), UIFormKind.Choice, Said("settings_page.layout_skin_help"), EditorSettingsCatalog.DESIGNER,
                [.. LayoutSkins().Select(choice => choice.Text)], ChoiceLabels: [.. LayoutSkins().Select(LayoutSkinLabel)]),
            new("disabled_extensions", Said("settings_page.switched_off"), UIFormKind.Flags, Said("settings_page.switched_off_help"), EXTENSIONS,
                [.. hex.ExtensionNames()])
        ];

        /// <summary>What a setting comes as, for the row to say.</summary>
        public IRuntimeValue? Default(string key) => EditorSettingsCatalog.Usual.GetValueOrDefault(key);

        // What a row shows, the folder's or the user's by the window's switch (DawnScene.SettingsScope)
        public IRuntimeValue? Get(string key) => hex.ScopedValue(key, InForce(key));

        // What a setting is in force at, the folder's over the user's
        private IRuntimeValue? InForce(string key) => key switch
        {
            "editor_skin" => new StringValue(EditorSkins().Find(choice => choice.Skin.Name == hex.settings.EditorSkin && (choice.Theme ?? string.Empty) == hex.settings.EditorTheme).Text ?? string.Empty),
            "size" => new StringValue(DescribeScale(hex.compositor.Scale)),
            "layout_skin" => new StringValue(LayoutSkins().Find(choice => choice.Skin.Name == UISkins.Current.Name && choice.Theme == (UISkins.Theme ?? UISkins.Current.UsualTheme)).Text ?? UISkins.Current.Title),
            "code_zoom" => new NumberValue(hex.settings.CodeZoom),
            "code_font" => new StringValue(hex.settings.CodeFont),
            "wrap_code" => Values.Bool(hex.settings.WrapCode),
            "git_blame" => Values.Bool(hex.settings.GitBlame),
            "minimap" => Values.Bool(hex.settings.Minimap),
            "inlay_hints" => Values.Bool(hex.settings.InlayHints),
            "semantic_highlighting" => Values.Bool(hex.settings.SemanticHighlighting),
            "csharp_colors" => new StringValue(hex.settings.CSharpColors),
            "git_autofetch" => Values.Bool(hex.settings.GitAutoFetch),
            "git_autofetch_interval" => new NumberValue(hex.settings.GitAutoFetchInterval),
            "git_pull_mode" => new StringValue(hex.settings.GitPullMode),
            "git_account" => new StringValue(hex.settings.GitAccount),
            "git_confirm_sync" => Values.Bool(hex.settings.GitConfirmSync),
            "git_smart_commit" => new StringValue(hex.settings.GitSmartCommit),
            "git_subject_length" => new NumberValue(hex.settings.GitSubjectLength),
            "git_line_length" => new NumberValue(hex.settings.GitLineLength),
            "run_to_terminal" => Values.Bool(hex.settings.RunToTerminal),
            "mcp_server" => Values.Bool(hex.settings.McpServer),
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
            "files_associations" => UIFormPairs.Value(hex.settings.FileAssociations.Select(pair => (pair.Key, pair.Value))),
            "disabled_extensions" => Natives.List(hex.ExtensionNames().Where((_, at) => !hex.extensions.All[at].Enabled)),
            _ => null
        };

        public bool IsModified(string key) => hex.ScopedModified(key) ?? key switch
        {
            "editor_skin" => hex.settings.EditorSkin != "flat" || hex.settings.EditorTheme != "horizon",
            // Only a size somebody picked and that's kept for next time, a window that's another size than the last run's
            // isn't a change of anybody's
            "size" => hex.scalePicked || (!hex.options.SelfTest && File.Exists(ScalePath)),
            "panels" or "layout_skin" => false,
            "disabled_extensions" => hex.extensions.All.Any(extension => !extension.Enabled),
            _ => !SettingValues.Same(Get(key), EditorSettingsCatalog.Usual.GetValueOrDefault(key))
        };

        public void Set(string key, IRuntimeValue value)
        {
            if (!hex.SetInScope(key, value, Apply))
                Apply(key, value);
        }

        // A setting put in force and kept, the way its row changing it does
        private void Apply(string key, IRuntimeValue value)
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
                    {
                        hex.SetScale(Scales[at], remember: true);
                        hex.scalePicked = true;
                    }
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
                case ("git_blame", BooleanValue blame):
                    settings.GitBlame = blame.Value;
                    break;
                case ("git_autofetch", BooleanValue fetch):
                    settings.GitAutoFetch = fetch.Value;
                    break;
                case ("git_autofetch_interval", NumberValue minutes):
                    settings.GitAutoFetchInterval = Math.Clamp(MathF.Round(minutes.Value), 1.0f, 60.0f);
                    break;
                case ("git_pull_mode", StringValue { Value: "default" or "merge" or "rebase" or "ff_only" } pull):
                    settings.GitPullMode = pull.Value;
                    break;
                case ("git_account", StringValue account):
                    settings.GitAccount = account.Value.Trim();
                    break;
                case ("git_confirm_sync", BooleanValue confirm):
                    settings.GitConfirmSync = confirm.Value;
                    break;
                case ("git_smart_commit", StringValue { Value: "ask" or "always" or "never" } smart):
                    settings.GitSmartCommit = smart.Value;
                    ((IPanels)hex).RefreshSide(HexSide.SourceControl);
                    break;
                case ("git_subject_length", NumberValue subject):
                    settings.GitSubjectLength = (int)Math.Clamp(MathF.Round(subject.Value), 20.0f, 100.0f);
                    break;
                case ("git_line_length", NumberValue line):
                    settings.GitLineLength = (int)Math.Clamp(MathF.Round(line.Value), 40.0f, 120.0f);
                    break;
                case ("minimap", BooleanValue minimap):
                    settings.Minimap = minimap.Value;
                    foreach (HexDocument open in hex.documents)
                        open.Editor.Minimap = minimap.Value;
                    break;
                case ("inlay_hints", BooleanValue inlays):
                    settings.InlayHints = inlays.Value;
                    hex.ForgetInlays();
                    break;
                case ("semantic_highlighting", BooleanValue semantic):
                    settings.SemanticHighlighting = semantic.Value;
                    hex.ForgetSemantics();
                    break;
                case ("csharp_colors", StringValue { Value: "resharper" or "visual_studio" or "skin" } csharp):
                    settings.CSharpColors = csharp.Value;
                    hex.languages.ApplyCSharpColors();
                    break;
                case ("run_to_terminal", BooleanValue run):
                    settings.RunToTerminal = run.Value;
                    break;
                case ("mcp_server", BooleanValue agents):
                    settings.McpServer = agents.Value;
                    hex.ApplyMcp();
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
                case ("files_associations", ObjectValue associations):
                    // the open files pick the new languages up next tick, see UpdateLanguageModes
                    settings.FileAssociations.Clear();
                    foreach (var (pattern, language) in UIFormPairs.Of(associations))
                    {
                        if (pattern.Length > 0 && language.Length > 0)
                            settings.FileAssociations[pattern] = language;
                    }
                    break;
                case ("disabled_extensions", ListValue off):
                    var named = off.Items.OfType<StringValue>().Select(item => hex.ExtensionIdOf(item.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (HexExtension extension in hex.extensions.All)
                    {
                        if (extension.Enabled == named.Contains(extension.Id))
                            hex.extensions.Switch(extension, !named.Contains(extension.Id));
                    }
                    hex.RebuildExtensionSections();
                    return;
                default:
                    return;
            }

            hex.SaveSettings();
        }

        public void Reset(string key)
        {
            if (hex.ResetInScope(key, Apply))
                return;

            switch (key)
            {
                case "editor_skin":
                    hex.UseEditorSkin("flat", "horizon", save: true);
                    return;
                case "disabled_extensions":
                    Set(key, new ListValue([]));
                    return;
                case "size":
                    hex.UseUsualScale();
                    return;
            }

            if (EditorSettingsCatalog.Usual.GetValueOrDefault(key) is { } usual)
                Set(key, usual);
        }

        public void Press(string key, int which)
        {
            switch (key, which)
            {
                case ("editor_skin", _):
                    // Color Theme's list, each theme shown as it's gone past and the old one back on Escape, so both
                    // ways to a theme behave the same
                    hex.ChooseColorTheme();
                    break;
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
