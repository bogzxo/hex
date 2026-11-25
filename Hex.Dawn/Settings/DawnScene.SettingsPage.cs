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

    // What the editor's panels and its interface size are in
    private const string PANELS = EditorSettingsCatalog.INTERFACE;

    // The row of the interface size, the key it's kept under in the settings file too
    private const string INTERFACE_SIZE = "interface_size";

    private SettingsWindow? settingsView;
    private OllamaModelPicker? ollamaModels;

    /// <summary>The page while it is up, for the self test.</summary>
    internal SettingsPage? SettingsPage => settingsView?.Window.Parent is not null ? settingsView.Page : null;

    /// <summary>The window of the settings while it is up.</summary>
    private WindowPanel? SettingsWindow => settingsView?.Window.Parent is not null ? settingsView.Window : null;

    // What the keyboard's section is called, a panel that sends you to the window of the keys
    private const string KEYS_SECTION = EditorSettingsCatalog.KEYS;

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

        // The text rows come before the editor's own in a section they share, tab size is what most open the settings for
        var sources = new List<ISettingsSource>(settingSources.Select(added => added.Source())) { new EditorSource(this) };
        sources.Add(new TerminalSettingsSource(terminals, EditorSettingsCatalog.THEME));
        sources.Add(new WindowsSource());
        sources.Add(new DefaultTerminalSource(said => Notify(said, error: true)));
        sources.Add(new ExtensionSettingsSource(this));
        if (!HexAi.Off)
            ollamaModels = new();
        settingsView = new SettingsWindow(sources, CloseSettings, EditorSettingsCatalog.Parts, sections: EditorSettingsCatalog.Sections, texts: SettingsPageTexts.Make());

        // Dawn's window is the editor's, the other parts fold away the first time it opens and a search puts the
        // editor's rows a little over the rest. Reset all settings asks first
        settingsView.Page.HomePart = EDITOR_PART;
        settingsView.Page.Common = CommonSettings;
        // The row to the Local AI window goes with it while AI is off for this run, the window doesn't open then
        settingsView.Page.HiddenOf = key => key == EditorSettingsCatalog.LOCAL_AI_WINDOW && HexAi.Off;
        settingsView.Page.DisabledReasonOf = key => !settings.GitEnabled && EditorSettingsCatalog.RequiresGit(key)
            ? Said("settings_catalog.git_required")
            : key == "edit_predictions_api_url" && settings.EditPredictionsProvider is HexSettings.LLAMACPP or "link"
                ? Said("settings_catalog.edit_predictions_api_url_only") : null;

        // The page is drawn at the interface size like everything else. It zoomed itself back up when a size under the one
        // the window's height suggested was picked, so the setting that made every word smaller made these bigger
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
            settingsPanels[EditorSettingsCatalog.LOOKS] = lookPicker;
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
    /// Reset all settings off the window's funnel or a part's right click, asked first with how many it resets. On
    /// Workspace it's only the workspace overrides, each reset going through the scope like any other.
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
        ollamaModels?.Update(null);
        ollamaModels = null;
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

    /// <summary>The skins and themes the designer can show a layout in, as the window lists them.</summary>
    private static List<(UISkinChoice Skin, string? Theme, string Text)> LayoutSkins()
    {
        List<(UISkinChoice, string?, string)> all = [];
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

    /// <summary>
    /// The editor section, read from and applied to the editor itself. The settings window's has Local AI's rows as the
    /// one row that opens the Local AI window (<see cref="EditorSettingsCatalog.Shown"/>); MCP's (<paramref name="all"/>)
    /// has every one of them, hex_settings_get and set read and write them as they always did.
    /// </summary>
    private sealed class EditorSource(DawnScene hex, bool all = false) : ISettingsSource
    {
        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings { get; } =
        [
            new("editor_skin", Said("settings_page.dawn_theme"), UIFormKind.Choice, Said("settings_page.dawn_theme_help"), EditorSettingsCatalog.THEME,
                [.. EditorSkins().Select(choice => choice.Text)], Buttons: [Said("settings_window.preview_themes")]),
            .. all ? EditorSettingsCatalog.Settings : EditorSettingsCatalog.Shown,
            EditorSettingsCatalog.RenderNow,
            new(INTERFACE_SIZE, Said("settings_page.interface_size"), UIFormKind.Choice, Said("settings_page.interface_size_help"), PANELS, [.. Scales.Select(DescribeSize)]),
            new("panels", Said("settings.editor.panels"), UIFormKind.Text, Said("settings_page.panels_help"), PANELS, Buttons: [Said("settings_page.put_back")], ButtonsOnly: true),
            new("layout_skin", Said("settings_page.layout_skin"), UIFormKind.Choice, Said("settings_page.layout_skin_help"), EditorSettingsCatalog.DESIGNER,
                [.. LayoutSkins().Select(choice => choice.Text)], ChoiceLabels: [.. LayoutSkins().Select(LayoutSkinLabel)]),
            new("disabled_extensions", Said("settings_page.switched_off"), UIFormKind.Flags, Said("settings_page.switched_off_help"), EXTENSIONS,
                [.. hex.ExtensionNames()])
        ];

        /// <summary>A setting's default value, for the row to show.</summary>
        public IRuntimeValue? Default(string key) => EditorSettingsCatalog.Usual.GetValueOrDefault(key);

        // What a row shows, the workspace or user value by the window's switch (DawnScene.SettingsScope)
        public IRuntimeValue? Get(string key) => hex.ScopedValue(key, InForce(key));

        // A setting's effective value, the workspace value over the user value
        private IRuntimeValue? InForce(string key) => key switch
        {
            "editor_skin" => new StringValue(EditorSkins().Find(choice => choice.Skin.Name == hex.settings.EditorSkin && (choice.Theme ?? string.Empty) == hex.settings.EditorTheme).Text ?? string.Empty),
            INTERFACE_SIZE => new StringValue(DescribeSize(hex.compositor.Scale)),
            "layout_skin" => new StringValue(LayoutSkins().Find(choice => choice.Skin.Name == UISkins.Current.Name && choice.Theme == (UISkins.Theme ?? UISkins.Current.UsualTheme)).Text ?? UISkins.Current.Title),
            "code_zoom" => new NumberValue(hex.settings.CodeZoom),
            "code_font" => new StringValue(hex.settings.CodeFont),
            "wrap_code" => Values.Bool(hex.settings.WrapCode),
            EditorSettingsCatalog.GIT_ENABLED => Values.Bool(hex.settings.GitEnabled),
            "git_blame" => Values.Bool(hex.settings.GitBlame),
            "minimap" => Values.Bool(hex.settings.Minimap),
            "spell_check" => Values.Bool(hex.settings.SpellCheck),
            "spell_check_language" => new StringValue(hex.settings.SpellCheckLanguage),
            "inlay_hints" => Values.Bool(hex.settings.InlayHints),
            "code_lens_references" => Values.Bool(hex.settings.CodeLensReferences),
            "semantic_highlighting" => Values.Bool(hex.settings.SemanticHighlighting),
            "visual_studio_keys" => Values.Bool(hex.settings.VisualStudioKeys),
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
            "link" => Values.Bool(hex.settings.Link),
            "link_port" => new NumberValue(hex.settings.LinkPort),
            "link_name" => new StringValue(hex.settings.LinkName),
            "link_discoverable" => Values.Bool(hex.settings.LinkDiscoverable),
        "link_talk_open_on_message" => Values.Bool(hex.settings.LinkTalkOpenOnMessage),
            "link_invite_rights" => new StringValue(hex.settings.LinkInviteRights),
            "no_ai" => Values.Bool(hex.settings.NoAi),
            "agents_resume_after_limit" => Values.Bool(hex.settings.AgentsResumeAfterLimit),
            "agents_auto_settle_seconds" => new NumberValue(hex.settings.AgentsAutoSettleSeconds),
            "debugger_path" => new StringValue(hex.settings.DebuggerPath),
            "agent_data_folder" => new StringValue(hex.settings.AgentDataFolder),
            "backdrop" => Values.Bool(hex.settings.BackdropOn),
            "backdrop_terminal" => Values.Bool(hex.settings.BackdropTerminal),
            "backdrop_image" => new StringValue(hex.settings.BackdropImage),
            "backdrop_opacity" => new NumberValue(hex.settings.BackdropOpacity),
            "backdrop_scale" => new NumberValue(hex.settings.BackdropScale),
            "backdrop_across" => new NumberValue(hex.settings.BackdropAcross),
            "backdrop_up" => new NumberValue(hex.settings.BackdropUp),
            "backdrop_stretch" => new NumberValue(hex.settings.BackdropStretch),
            "terminal_backdrop_scale" => new NumberValue(hex.settings.TerminalBackdropScale),
            "terminal_backdrop_across" => new NumberValue(hex.settings.TerminalBackdropAcross),
            "terminal_backdrop_up" => new NumberValue(hex.settings.TerminalBackdropUp),
            "terminal_backdrop_stretch" => new NumberValue(hex.settings.TerminalBackdropStretch),
            "backdrop_wrap" => Values.Bool(hex.settings.BackdropWrap),
            "backdrop_wrap_margin" => new NumberValue(hex.settings.BackdropWrapMargin),
            "wrap_flow" => new NumberValue(hex.settings.WrapFlow),
            "reopen_last" => Values.Bool(hex.settings.ReopenLast),
            "macos_native_menu" => Values.Bool(hex.settings.MacNativeMenu),
            "theme_follow_system" => Values.Bool(hex.settings.ThemeFollowSystem),
            "persistent_process" => Values.Bool(hex.settings.PersistentProcess),
            "persistent_at_login" => Values.Bool(hex.settings.PersistentAtLogin),
            "effects" => new StringValue(hex.settings.Effects),
            "effect_sparks" => Values.Bool(hex.settings.EffectSparks),
            "effect_motion" => Values.Bool(hex.settings.EffectMotion),
            "effect_glass" => Values.Bool(hex.settings.EffectGlass),
            "effect_caret_glow" => Values.Bool(hex.settings.EffectCaretGlow),
            "effect_spotlight" => Values.Bool(hex.settings.EffectSpotlight),
            "effect_ripples" => Values.Bool(hex.settings.EffectRipples),
            "effect_trail" => Values.Bool(hex.settings.EffectTrail),
            "glass_blur" => new NumberValue(hex.settings.GlassBlur),
            "render_scale" => new StringValue(hex.settings.RenderScale),
            "render_scale_percent" => new NumberValue(hex.settings.RenderScalePercent),
            "render_upscaler" => new StringValue(hex.settings.RenderUpscaler),
            "render_sharpness" => new NumberValue(hex.settings.RenderSharpness),
            "text_smoothing" => new StringValue(hex.settings.TextSmoothing),
            "render_composition" => new StringValue(hex.settings.RenderComposition),
            "render_scale_now" => new StringValue(hex.DescribeRenderScale()),
            "glass_clarity" => new NumberValue(hex.settings.GlassClarity),
            "glass_rim" => new NumberValue(hex.settings.GlassRim),
            "glass_bend" => new NumberValue(hex.settings.GlassBend),
            "ui_animation" => new NumberValue(hex.settings.UiAnimation),
            "scroll_speed" => new NumberValue(hex.settings.ScrollSpeed),
            "middle_scroll_speed" => new NumberValue(hex.settings.MiddleScrollSpeed),
            "caret_glide" => new NumberValue(hex.settings.CaretGlide),
            "trim_whitespace" => Values.Bool(hex.settings.TrimWhitespace),
            "final_newline" => Values.Bool(hex.settings.FinalNewline),
            "auto_save" => Values.Bool(hex.settings.AutoSave),
            "auto_save_delay" => new NumberValue(hex.settings.AutoSaveDelay),
            "canvas_handles" => Values.Bool(hex.settings.CanvasHandles),
            "snap" => new NumberValue(hex.settings.Snap),
            "edit_predictions" => Values.Bool(hex.settings.EditPredictions),
            "edit_predictions_provider" => new StringValue(hex.settings.EditPredictionsProvider),
            "edit_predictions_api_url" => new StringValue(hex.settings.EditPredictionsApiUrl),
            EditorSettingsCatalog.PREDICTION_KEY => new StringValue(hex.settings.EditPredictionsApiKey),
            "edit_predictions_model" => new StringValue(hex.settings.EditPredictionsModel),
            "edit_predictions_prompt_format" => new StringValue(hex.settings.EditPredictionsPromptFormat),
            "edit_predictions_kind" => new StringValue(hex.settings.EditPredictionsKind),
        "edit_predictions_max_tokens" => new NumberValue(hex.settings.EditPredictionsMaxTokens),
            "edit_predictions_invocation" => new StringValue(hex.settings.EditPredictionsInvocation),
            "edit_predictions_mode" => new StringValue(hex.settings.EditPredictionsMode),
            "edit_predictions_debounce" => new NumberValue(hex.settings.EditPredictionsDebounce),
            "edit_predictions_disabled_languages" => Natives.List(hex.settings.EditPredictionsDisabledLanguages),
            HexSettings.EDIT_PREDICTIONS_EXCLUDE => Natives.List(hex.settings.EditPredictionsExclude),
            "edit_predictions_workspace" => Values.Bool(hex.settings.EditPredictionsWorkspace),
            "edit_predictions_share" => Values.Bool(hex.settings.EditPredictionsShare),
            "edit_predictions_terminal" => Values.Bool(hex.settings.EditPredictionsTerminal),
            "edit_predictions_renames" => Values.Bool(hex.settings.EditPredictionsRenames),
            "edit_predictions_far" => Values.Bool(hex.settings.EditPredictionsFar),
            "edit_predictions_system_prompt" => new StringValue(hex.settings.EditPredictionsSystemPrompt),
            "edit_predictions_thinking" => new StringValue(hex.settings.EditPredictionsThinking),
            "edit_predictions_temperature" => new NumberValue(hex.settings.EditPredictionsTemperature),
            "edit_predictions_top_p" => new NumberValue(hex.settings.EditPredictionsTopP),
            "edit_predictions_top_k" => new NumberValue(hex.settings.EditPredictionsTopK),
            "edit_predictions_repeat_penalty" => new NumberValue(hex.settings.EditPredictionsRepeatPenalty),
            "edit_predictions_context" => new NumberValue(hex.settings.EditPredictionsContext),
            "edit_predictions_link_chat" => Values.Bool(hex.settings.EditPredictionsLinkChat),
            "llamacpp_folder" => new StringValue(hex.settings.LlamaCppFolder),
            "llamacpp_models" => new StringValue(hex.settings.LlamaCppModels),
            "llamacpp_server" => new StringValue(hex.settings.LlamaCppServer),
            "llamacpp_model" => new StringValue(hex.settings.LlamaCppModel),
            "llamacpp_alias" => new StringValue(hex.settings.LlamaCppAlias),
            "llamacpp_port" => new NumberValue(hex.settings.LlamaCppPort),
            "llamacpp_context" => new NumberValue(hex.settings.LlamaCppContext),
            "llamacpp_offload" => new StringValue(hex.settings.LlamaCppOffload),
            "llamacpp_extra_args" => new StringValue(hex.settings.LlamaCppExtraArgs),
            "llamacpp_autostart" => Values.Bool(hex.settings.LlamaCppAutostart),
        "local_ai_predict_model" => new StringValue(hex.ModelRowText(hex.settings.LocalAiPredictModel)),
        "local_ai_chat_model" => new StringValue(hex.ModelRowText(hex.settings.LocalAiChatModel)),
        "local_ai_engine" => new StringValue(hex.settings.LocalAiEngine),
        "local_ai_model_folders" => new StringValue(hex.settings.LocalAiModelFolders),
        "local_ai_presets" => new StringValue(hex.settings.LocalAiPresets),
        "local_ai_favourites" => new StringValue(hex.settings.LocalAiFavourites),
        "local_ai_ui_mode" => new StringValue(hex.settings.LocalAiUiMode),
        "local_ai_setup" => new StringValue(hex.settings.LocalAiSetup),
            "ai_edit_model" => new StringValue(hex.settings.AiEditModel),
            "ai_edit_thinking" => Values.Bool(hex.settings.AiEditThinking),
            "ai_edit_max_tokens" => new NumberValue(hex.settings.AiEditMaxTokens),
            EditorSettingsCatalog.LLAMACPP_SETUP => new StringValue(hex.llamaServer is { } server ? hex.LlamaServerStateText(server) : Said("llamacpp.state_stopped")),
            HexSettings.DISCORD_PRESENCE => Values.Bool(hex.settings.DiscordPresence),
            "discord_show_file" => Values.Bool(hex.settings.DiscordShowFile),
            "discord_show_workspace" => Values.Bool(hex.settings.DiscordShowWorkspace),
            "discord_show_language_icon" => Values.Bool(hex.settings.DiscordShowLanguageIcon),
            "discord_idle_minutes" => new NumberValue(hex.settings.DiscordIdleMinutes),
            "discord_hide_globs" => Natives.List(hex.settings.DiscordHideGlobs),
            EditorSettingsCatalog.LINK_SYSTEM_FILE_FOLDERS => Natives.List(hex.settings.LinkSystemFileFolders),
            "files_associations" => UIFormPairs.Value(hex.settings.FileAssociations.Select(pair => (pair.Key, pair.Value))),
            "disabled_extensions" => Natives.List(hex.ExtensionNames().Where((_, at) => !hex.extensions.All[at].Enabled)),
            _ => null
        };

        public bool IsModified(string key) => hex.ScopedModified(key) ?? key switch
        {
            "editor_skin" => hex.settings.EditorSkin != HexSettings.USUAL_SKIN || hex.settings.EditorTheme != HexSettings.USUAL_THEME,
            INTERFACE_SIZE => hex.compositor.Scale != 1.0f,
            "panels" or "layout_skin" or EditorSettingsCatalog.ARRANGE_BACKDROP or EditorSettingsCatalog.LLAMACPP_SETUP or EditorSettingsCatalog.LOCAL_AI_WINDOW or "render_scale_now" => false,
            "disabled_extensions" => hex.extensions.All.Any(extension => !extension.Enabled),
            _ => !SettingValues.Same(Get(key), EditorSettingsCatalog.Usual.GetValueOrDefault(key))
        };

        public void Set(string key, IRuntimeValue value)
        {
            if (!hex.SetInScope(key, value, Apply))
                Apply(key, value);
        }

        // A setting applied and saved, the way changing its row does
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
                case (INTERFACE_SIZE, StringValue sized):
                    int at = Array.FindIndex(Scales, size => DescribeSize(size) == sized.Value);
                    if (at >= 0)
                        hex.SetInterfaceSize(Scales[at], remember: true);
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
                case (EditorSettingsCatalog.GIT_ENABLED, BooleanValue enabled):
                    settings.GitEnabled = enabled.Value;
                    hex.git.ApplyEnabled();
                    hex.SettingsPage?.Refresh();
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
                case ("spell_check", BooleanValue spelling):
                    settings.SpellCheck = spelling.Value;
                    break;
                case ("spell_check_language", StringValue spellingLanguage):
                    settings.SpellCheckLanguage = spellingLanguage.Value.Trim();
                    break;
                case ("inlay_hints", BooleanValue inlays):
                    settings.InlayHints = inlays.Value;
                    hex.ForgetInlays();
                    break;
                case ("code_lens_references", BooleanValue lenses):
                    settings.CodeLensReferences = lenses.Value;
                    hex.ForgetReferenceLenses();
                    break;
                case ("semantic_highlighting", BooleanValue semantic):
                    settings.SemanticHighlighting = semantic.Value;
                    hex.ForgetSemantics();
                    break;
                case ("visual_studio_keys", BooleanValue visualStudio):
                    settings.VisualStudioKeys = visualStudio.Value;
                    hex.ApplyKeyLayer();
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
                case ("link", BooleanValue link):
                    settings.Link = link.Value;
                    hex.ApplyLink();
                    break;
                case ("link_port", NumberValue port):
                    settings.LinkPort = (int)Math.Clamp(MathF.Round(port.Value), 1024.0f, 65535.0f);
                    hex.ApplyLink(restart: true);
                    break;
                case ("link_name", StringValue name):
                    settings.LinkName = name.Value.Trim();
                    hex.ApplyLink();
                    break;
                case ("link_discoverable", BooleanValue discoverable):
                    settings.LinkDiscoverable = discoverable.Value;
                    hex.ApplyLink();
                    break;
                case ("link_talk_open_on_message", BooleanValue openChat):
                    settings.LinkTalkOpenOnMessage = openChat.Value;
                    break;
                case ("link_invite_rights", StringValue { Value: "work_together" or "use_agents" } inviteRights):
                    settings.LinkInviteRights = inviteRights.Value;
                    break;
                case ("agents_resume_after_limit", BooleanValue resume):
                settings.AgentsResumeAfterLimit = resume.Value;
                break;
                case ("agents_auto_settle_seconds", NumberValue settle):
                    settings.AgentsAutoSettleSeconds = (int)Math.Clamp(MathF.Round(settle.Value), 0.0f, 3600.0f);
                    break;
            case ("no_ai", BooleanValue noAi):
                    settings.NoAi = noAi.Value;
                    hex.NoAiChanged();
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
                case ("backdrop_stretch", NumberValue stretch):
                    settings.BackdropStretch = Math.Clamp(stretch.Value, HexSettings.LEAST_STRETCH, HexSettings.MOST_STRETCH);
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
                case ("terminal_backdrop_stretch", NumberValue terminalStretch):
                    settings.TerminalBackdropStretch = Math.Clamp(terminalStretch.Value, HexSettings.LEAST_STRETCH, HexSettings.MOST_STRETCH);
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
                case ("render_scale", StringValue { Value: "auto" or "native" or "manual" } scale):
                    settings.RenderScale = scale.Value;
                    hex.ApplyRenderScale();
                    break;
                case ("render_scale_percent", NumberValue percent):
                    settings.RenderScalePercent = Math.Clamp(percent.Value, 25.0f, 100.0f);
                    hex.ApplyRenderScale();
                    break;
                case ("text_smoothing", StringValue { Value: "auto" or "subpixel" or "grayscale" } smoothing):
                settings.TextSmoothing = smoothing.Value;
                hex.ApplyRenderScale();
                break;
            case ("render_upscaler", StringValue { Value: "cas" or "fsr1" or "gsr1" or "bilinear" or "nearest" } upscaler):
                    settings.RenderUpscaler = upscaler.Value;
                    hex.ApplyRenderScale();
                    break;
                case ("render_sharpness", NumberValue sharpness):
                    settings.RenderSharpness = Math.Clamp(sharpness.Value, 0.0f, 1.0f);
                    hex.ApplyRenderScale();
                    break;
                case ("render_composition", StringValue { Value: "native_ui" or "whole_frame" } composition):
                    settings.RenderComposition = composition.Value;
                    hex.ApplyRenderScale();
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
                case ("scroll_speed", NumberValue scroll):
                    settings.ScrollSpeed = HexSettings.ScrollSpeedOf(scroll.Value);
                    hex.compositor.ScrollSpeed = settings.ScrollSpeed;
                    break;
                case ("middle_scroll_speed", NumberValue middleScroll):
                    settings.MiddleScrollSpeed = HexSettings.ScrollSpeedOf(middleScroll.Value);
                    hex.compositor.MiddleScrollSpeed = settings.MiddleScrollSpeed;
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
                case ("agent_data_folder", StringValue agentFolder):
                    settings.AgentDataFolder = agentFolder.Value.Trim().Trim('"');
                    hex.AgentFolderTyped();
                    break;
                case ("debugger_path", StringValue debuggerPath):
                    settings.DebuggerPath = debuggerPath.Value.Trim().Trim('"');
                    break;
                case ("reopen_last", BooleanValue reopen):
                    settings.ReopenLast = reopen.Value;
                    break;
                case ("macos_native_menu", BooleanValue nativeMenu):
                    settings.MacNativeMenu = nativeMenu.Value;
                    hex.MacMenuSettingChanged();
                    break;
                case ("theme_follow_system", BooleanValue follow):
                    settings.ThemeFollowSystem = follow.Value;
                    hex.AppearanceSettingChanged();
                    break;
                case ("persistent_process", BooleanValue persistent):
                    settings.PersistentProcess = persistent.Value;
                    // Starting with the system is the persistent process's, it goes when that does
                    if (!persistent.Value && settings.PersistentAtLogin)
                        settings.PersistentAtLogin = false;
                    HexStartup.Apply(settings.PersistentProcess && settings.PersistentAtLogin);
                    break;
                case ("persistent_at_login", BooleanValue atLogin):
                    settings.PersistentAtLogin = atLogin.Value;
                    if (atLogin.Value)
                        settings.PersistentProcess = true;
                    HexStartup.Apply(settings.PersistentProcess && settings.PersistentAtLogin);
                    break;
                case ("canvas_handles", BooleanValue handles):
                    settings.CanvasHandles = handles.Value;
                    break;
                case ("snap", NumberValue snap):
                    settings.Snap = Math.Clamp(MathF.Round(snap.Value), 0.0f, 16.0f);
                    break;
                case ("edit_predictions", BooleanValue predicts):
                    settings.EditPredictions = predicts.Value;
                    break;
                case ("edit_predictions_provider", StringValue { Value: "ollama" or "openai" or HexSettings.LLAMACPP or "link" } provider):
                    // Ollama's own address follows the provider to its OpenAI completions and back, any other URL stays
                    settings.EditPredictionsProvider = provider.Value;
                    string url = Hex.Core.PredictionSettings.UrlForProvider(provider.Value, settings.EditPredictionsApiUrl);
                    if (url != settings.EditPredictionsApiUrl)
                    {
                        settings.EditPredictionsApiUrl = url;
                        hex.SettingsPage?.Refresh("edit_predictions_api_url");
                    }
                    break;
                case ("edit_predictions_api_url", StringValue predictionUrl):
                    settings.EditPredictionsApiUrl = predictionUrl.Value.Trim();
                    break;
                case (EditorSettingsCatalog.PREDICTION_KEY, StringValue predictionKey):
                    settings.EditPredictionsApiKey = predictionKey.Value.Trim();
                    break;
                case ("edit_predictions_model", StringValue predictionModel):
                    settings.EditPredictionsModel = predictionModel.Value.Trim();
                    break;
                case ("edit_predictions_prompt_format", StringValue { Value: "infer" or "zeta21" or "zeta2" or "qwen" or "code_llama" or "star_coder" or "deepseek_coder" or "code_gemma" or "codestral" } format):
                    settings.EditPredictionsPromptFormat = format.Value;
                    break;
                case ("edit_predictions_kind", StringValue { Value: "auto" or "edit_model" or "fim" or "instruct" or "fim_instruct" } kind):
                    settings.EditPredictionsKind = kind.Value;
                    break;
                case ("edit_predictions_max_tokens", NumberValue predictionTokens):
                    settings.EditPredictionsMaxTokens = (int)Math.Clamp(MathF.Round(predictionTokens.Value), 16.0f, 2048.0f);
                    break;
                case ("edit_predictions_invocation", StringValue { Value: "after_typing" or "shortcut" } invocation):
                    settings.EditPredictionsInvocation = invocation.Value;
                    break;
                case ("edit_predictions_mode", StringValue { Value: "eager" or "subtle" } predictionMode):
                    settings.EditPredictionsMode = predictionMode.Value;
                    break;
                case ("edit_predictions_debounce", NumberValue predictionPause):
                    settings.EditPredictionsDebounce = (int)Math.Clamp(MathF.Round(predictionPause.Value), 0.0f, 2000.0f);
                    break;
                case ("edit_predictions_disabled_languages", ListValue offLanguages):
                    settings.EditPredictionsDisabledLanguages = HexSettings.Patterns(offLanguages);
                    break;
                case (HexSettings.EDIT_PREDICTIONS_EXCLUDE, ListValue neverSent):
                    settings.EditPredictionsExclude = HexSettings.Patterns(neverSent);
                    break;
                case ("edit_predictions_workspace", BooleanValue predictsHere):
                    settings.EditPredictionsWorkspace = predictsHere.Value;
                    break;
                case (EditorSettingsCatalog.LINK_SYSTEM_FILE_FOLDERS, ListValue systemFolders):
                    // Only whole local paths name a folder, the way the file is read
                    settings.LinkSystemFileFolders = [.. HexSettings.Patterns(systemFolders).Where(Path.IsPathFullyQualified)];
                    break;
                case ("edit_predictions_share", BooleanValue shares):
                    settings.EditPredictionsShare = shares.Value;
                    break;
                case ("edit_predictions_terminal", BooleanValue predictsInTerminal):
                    settings.EditPredictionsTerminal = predictsInTerminal.Value;
                    break;
                case ("edit_predictions_renames", BooleanValue followsRenames):
                    settings.EditPredictionsRenames = followsRenames.Value;
                    break;
                case ("edit_predictions_far", BooleanValue predictsFar):
                    settings.EditPredictionsFar = predictsFar.Value;
                    break;
                case ("edit_predictions_system_prompt", StringValue systemPrompt):
                    settings.EditPredictionsSystemPrompt = systemPrompt.Value.Trim();
                    break;
                case ("edit_predictions_thinking", StringValue thinking) when Array.IndexOf(PredictionTuning.Thinkings, thinking.Value) >= 0:
                    settings.EditPredictionsThinking = thinking.Value;
                    break;
                case ("edit_predictions_temperature", NumberValue temperature):
                    settings.EditPredictionsTemperature = Math.Clamp(temperature.Value, 0.0f, 2.0f);
                    break;
                case ("edit_predictions_top_p", NumberValue topP):
                    settings.EditPredictionsTopP = Math.Clamp(topP.Value, 0.0f, 1.0f);
                    break;
                case ("edit_predictions_top_k", NumberValue topK):
                    settings.EditPredictionsTopK = (int)Math.Clamp(MathF.Round(topK.Value), 0.0f, 200.0f);
                    break;
                case ("edit_predictions_repeat_penalty", NumberValue repeatPenalty):
                    settings.EditPredictionsRepeatPenalty = Math.Clamp(repeatPenalty.Value, 0.0f, 2.0f);
                    break;
                case ("edit_predictions_context", NumberValue context):
                    settings.EditPredictionsContext = (int)Math.Clamp(MathF.Round(context.Value), 0.0f, 262144.0f);
                    break;
                case ("edit_predictions_link_chat", BooleanValue answersInChat):
                    settings.EditPredictionsLinkChat = answersInChat.Value;
                    break;
                case ("llamacpp_folder", StringValue llamaFolder):
                    settings.LlamaCppFolder = llamaFolder.Value.Trim();
                    break;
                case ("llamacpp_models", StringValue llamaModels):
                    settings.LlamaCppModels = llamaModels.Value.Trim();
                    break;
                case ("llamacpp_server", StringValue llamaServerPath):
                    settings.LlamaCppServer = llamaServerPath.Value.Trim();
                    break;
                case ("llamacpp_model", StringValue llamaModel):
                    settings.LlamaCppModel = llamaModel.Value.Trim();
                    break;
                case ("llamacpp_alias", StringValue llamaAlias):
                    settings.LlamaCppAlias = llamaAlias.Value.Trim();
                    break;
                case ("llamacpp_port", NumberValue llamaPort):
                    settings.LlamaCppPort = (int)Math.Clamp(MathF.Round(llamaPort.Value), 1024.0f, 65535.0f);
                    break;
                case ("llamacpp_context", NumberValue llamaContext):
                    settings.LlamaCppContext = (int)Math.Clamp(MathF.Round(llamaContext.Value), 2048.0f, 262144.0f);
                    break;
                case ("llamacpp_offload", StringValue offload) when Array.IndexOf(LlamaCppServerArgs.Offloads, offload.Value) >= 0:
                    settings.LlamaCppOffload = offload.Value;
                    break;
                case ("llamacpp_extra_args", StringValue llamaExtra):
                    settings.LlamaCppExtraArgs = llamaExtra.Value.Trim();
                    break;
                case ("local_ai_predict_model", StringValue localPredict):
            LocalAiRoles.Set(settings, LocalAiRole.Predict, localPredict.Value);
            break;
        case ("local_ai_chat_model", StringValue localChat):
            LocalAiRoles.Set(settings, LocalAiRole.Chat, localChat.Value);
            break;
        case ("local_ai_engine", StringValue localEngine) when Array.IndexOf(HexSettings.LocalAiEngines, localEngine.Value) >= 0:
            settings.LocalAiEngine = localEngine.Value;
            break;
        case ("local_ai_model_folders", StringValue localFolders):
            settings.LocalAiModelFolders = localFolders.Value.Trim();
            _ = LocalModelLibrary.Shared.Refresh();
            break;
        case ("local_ai_presets", StringValue localPresets):
            settings.LocalAiPresets = localPresets.Value.Trim();
            break;
        case ("local_ai_favourites", StringValue localFavourites):
            settings.LocalAiFavourites = localFavourites.Value.Trim();
            break;
        case ("local_ai_ui_mode", StringValue localUiMode) when Array.IndexOf(HexSettings.LocalAiUiModes, localUiMode.Value) >= 0:
            settings.LocalAiUiMode = localUiMode.Value;
            break;
        case ("local_ai_setup", StringValue localSetup):
            settings.LocalAiSetup = localSetup.Value.Trim();
            break;
        case ("llamacpp_autostart", BooleanValue llamaAutostart):
                    settings.LlamaCppAutostart = llamaAutostart.Value;
                    break;
                case ("ai_edit_model", StringValue aiEditModel):
                    settings.AiEditModel = aiEditModel.Value.Trim();
                    break;
                case ("ai_edit_thinking", BooleanValue aiEditThinking):
                    settings.AiEditThinking = aiEditThinking.Value;
                    break;
                case ("ai_edit_max_tokens", NumberValue aiEditMax):
                    settings.AiEditMaxTokens = (int)Math.Clamp(MathF.Round(aiEditMax.Value), 512.0f, 16384.0f);
                    break;
                case (HexSettings.DISCORD_PRESENCE, BooleanValue discordOn):
                    settings.DiscordPresence = discordOn.Value;
                    break;
                case ("discord_show_file", BooleanValue discordFile):
                    settings.DiscordShowFile = discordFile.Value;
                    break;
                case ("discord_show_workspace", BooleanValue discordWorkspace):
                    settings.DiscordShowWorkspace = discordWorkspace.Value;
                    break;
                case ("discord_show_language_icon", BooleanValue discordIcon):
                    settings.DiscordShowLanguageIcon = discordIcon.Value;
                    break;
                case ("discord_idle_minutes", NumberValue discordIdle):
                    settings.DiscordIdleMinutes = (int)Math.Clamp(MathF.Round(discordIdle.Value), 0.0f, 240.0f);
                    break;
                case ("discord_hide_globs", ListValue discordHide):
                    settings.DiscordHideGlobs = HexSettings.Patterns(discordHide);
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
                    hex.UseEditorSkin(HexSettings.USUAL_SKIN, HexSettings.USUAL_THEME, save: true);
                    return;
                case "disabled_extensions":
                    Set(key, new ListValue([]));
                    return;
                case INTERFACE_SIZE:
                    hex.SetInterfaceSize(1.0f, remember: true);
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
                case (EditorSettingsCatalog.ARRANGE_BACKDROP, _):
                    hex.ArrangeBackdrop();
                    break;
                case (EditorSettingsCatalog.LOCAL_AI_WINDOW, _):
                    hex.OpenLocalAiWindow();
                    break;
                case ("local_ai_predict_model", EditorSettingsCatalog.MODEL_CHANGE):
                    hex.PickModelFor(LocalAiRole.Predict);
                    break;
                case ("local_ai_chat_model", EditorSettingsCatalog.MODEL_CHANGE):
                    hex.PickModelFor(LocalAiRole.Chat);
                    break;
                case (EditorSettingsCatalog.LLAMACPP_SETUP, EditorSettingsCatalog.LLAMACPP_SET_UP):
                    hex.OpenLlamaSetup();
                    break;
                case (EditorSettingsCatalog.LLAMACPP_SETUP, EditorSettingsCatalog.LLAMACPP_START):
                    hex.StartLlamaServer();
                    break;
                case (EditorSettingsCatalog.LLAMACPP_SETUP, EditorSettingsCatalog.LLAMACPP_STOP):
                    hex.StopLlamaServer();
                    break;
                case ("backdrop_image", EditorSettingsCatalog.USUAL_PICTURE):
                    Set(key, new StringValue(string.Empty));
                    hex.SettingsPage?.Refresh(key);
                    break;
            }
        }
    }
}
