using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The status bar along the bottom, a native StatusBar. On the left how many problems there are and the last thing Hex
// said. On the right where the caret is, the indentation, the line endings and what the
// file is in code, and in the designer where the pointer is, how many components there are, the zoom, the skin and
// the screen the layout is made for. Most of it does something when clicked.
internal sealed partial class DawnScene
{
    private StatusBar statusBar = null!;
    private StatusItem problemsItem = null!, messageItem = null!;
    private StatusItem caretItem = null!, selectionItem = null!, indentItem = null!, endingsItem = null!, languageItem = null!;
    private StatusItem coordsItem = null!, componentsItem = null!, zoomItem = null!, skinItem = null!, screenItem = null!;
    private StatusItem codeZoomItem = null!, runningItem = null!;
    private string runningShown = string.Empty;
    private float codeZoomShown = -1.0f;

    // The indentation the status bar says, spaces or a tab and how wide, written again only when it changes
    private (bool Tabs, int Size) indentShown;

    // What the skin and the kind of file were last written for. Every tick wrote them anew, garbage a tick for nothing
    private (UISkinChoice? Skin, string? Theme) skinShown;
    private HexDocument? languageFor;

    // What the document was coloured with when the language item was written, a language picked changes it
    private ICodeHighlighter? languageShownWith;

    // How many times the language servers had started or stopped when the language item was written, it says so as it happens
    private int languagesShown;

    // How many components the layout has, counted when its tree is put together, not walked again every tick

    // What the items were last written from, so a number is only made into text when it changes
    private (int Line, int Column, int Selected, int Version)? caretShown;
    private (int Errors, int Warnings)? problemsShown;
    private (int Count, int Selected)? componentsShown;
    private float zoomShown = -1.0f;

    // The screen the layout's made for when the screen item was written, a string made every tick before
    private (System.Numerics.Vector2 Size, bool Any)? screenShown;

    private void BuildStatusBar()
    {
        statusBar = layout.Get<StatusBar>("status_bar");
        BuildBranchItem();

        // The plain welcome is over an empty layout nobody opened, it isn't HIDL at Ln 1, Col 1 to anybody looking at it
        bool Code() => !LayoutTools && document.Picture is null && !plainShown;
        bool Design() => LayoutTools;

        problemsItem = statusBar.Add("$(error) 0  $(warning) 0", pressed: () => ShowPanel(PROBLEMS_PAGE), tooltip: Said("status_bar.problems_tip"));
        messageItem = statusBar.Add(string.Empty, pressed: () => ShowPanel(OUTPUT_PAGE), tooltip: Said("status_bar.message_tip"));

        // How much is selected, first of the right side so it grows out into the gap. After Ln and Col it pushed them and
        // everything before them a hundred pixels to the left as a selection was made and back again as it went
        selectionItem = statusBar.Add(string.Empty, right: true);
        selectionItem.IsVisible = Code;

        // A game started from here (Fight!) reads what's saved as it changes, Defold's hot reload, said while it runs
        runningItem = statusBar.Add(string.Empty, right: true, tooltip: Said("status_bar.running_tip"));
        runningItem.IsVisible = () => Volatile.Read(ref extensions.programsRunning) > 0;

        // The work going in the background, on the right before the rest (DawnScene.Background)
        BuildWorkItem();

        caretItem = statusBar.Add(Said("status_bar.caret", ("line", "1"), ("column", "1")), right: true, pressed: GoToLine, tooltip: Said("status_bar.caret_tip"));
        caretItem.IsVisible = Code;
        indentItem = statusBar.Add(Said("status_bar.indent", ("count", "4")), right: true, pressed: () => OpenSettings(Hex.Shell.EditorSettingsCatalog.TEXT), tooltip: Said("settings_text.status_tip"));
        indentItem.IsVisible = Code;
        endingsItem = statusBar.Add("CRLF", right: true, pressed: SwapLineEndings, tooltip: Said("status_bar.endings_tip"));
        endingsItem.IsVisible = Code;
        languageItem = statusBar.Add("HIDL", right: true, pressed: ChangeLanguageMode, tooltip: Said("language_mode.status_tip"));
        languageItem.IsVisible = Code;
        codeZoomItem = statusBar.Add(string.Empty, right: true, pressed: () => ZoomCode(0.0f), tooltip: Said("status_bar.code_zoom_tip"));
        codeZoomItem.IsVisible = () => !LayoutTools && settings.CodeZoom != 1.0f;

        coordsItem = statusBar.Add(string.Empty, right: true, tooltip: Said("status_bar.coords_tip"));
        coordsItem.IsVisible = Design;
        componentsItem = statusBar.Add(string.Empty, right: true, pressed: () => ShowSide(HexSide.Design), tooltip: Said("status_bar.components_tip"));
        componentsItem.IsVisible = Design;
        zoomItem = statusBar.Add("$(zoom-in) 100%", right: true, pressed: designer.ZoomToFit, tooltip: Said("status_bar.zoom_tip"));
        zoomItem.IsVisible = Design;
        skinItem = statusBar.Add(string.Empty, right: true, pressed: ShowSkinMenu, tooltip: Said("status_bar.skin_tip"));
        skinItem.IsVisible = Design;
        screenItem = statusBar.Add(string.Empty, right: true, pressed: () => designer.ShowScreenMenu(chrome, compositor.Pointer.Position), tooltip: Said("status_bar.screen_tip"));
        screenItem.IsVisible = Design;

        // Wide enough that the items after it don't shuffle as the numbers grow a digit, no wider, "Ln 1, Col 1" sat in a
        // gap twice the others
        caretItem.MinWidth = 96.0f;
    }

    /// <summary>The name of the running game said once, when it changes.</summary>
    private void UpdateRunningItem()
    {
        if (ReferenceEquals(runningShown, extensions.runningName))
            return;

        runningShown = extensions.runningName;
        runningItem.Text = $"$(run) {Said("status_bar.running", ("name", extensions.runningName))}";
    }

    /// <summary>Every tick, what the status bar says brought up to date. Nothing is written into text that hasn't changed.</summary>
    private void UpdateStatusBar()
    {
        UpdateBranchItem();
        FollowExtensions();
        UpdateLanguageModes();
        UpdateImageView();
        UpdateConflicts();
        UpdateInlays();
        UpdateReferenceLenses();
        UpdateSemantics();

        if (messageItem.Text.Length > 0 && System.Environment.TickCount64 - saidAt > SAID_FOR)
        {
            messageItem.Text = string.Empty;
            messageItem.Color = null;
        }

        // The screen the layout is made for, or the one it's previewed on, which follows Hex onto another screen. It said
        // 1600 x 900 whatever the layout was made for and whatever screen it was on
        if (LayoutTools)
        {
            var screen = (document.Design ?? HexDesigner.DesignSize, document.Design is null);
            if (screenShown != screen)
            {
                screenShown = screen;
                screenItem.Text = $"$(device-desktop) {designer.ScreenText()}";
            }
        }

        if (problemsShown != (errorCount, warningCount))
        {
            problemsShown = (errorCount, warningCount);
            problemsItem.Text = $"$(error) {errorCount}  $(warning) {warningCount}";
            problemsItem.Background = errorCount > 0 ? ErrorColor with { W = 0.35f } : null;
        }

        if (!LayoutTools)
        {
            UITextPosition caret = code.Document.Caret;
            int selected = code.Document.HasSelection ? code.Document.SelectedLength : 0;
            if (caretShown != (caret.Line, caret.Column, selected, code.Document.Version))
            {
                caretShown = (caret.Line, caret.Column, selected, code.Document.Version);
                caretItem.Text = Said("status_bar.caret", ("line", (caret.Line + 1).ToString()), ("column", (caret.Column + 1).ToString()));
                selectionItem.Text = selected > 0 ? Said("status_bar.selection", ("count", selected.ToString())) : string.Empty;
            }

            endingsItem.Text = document.CrLf ? "CRLF" : "LF";

            // Spaces 2 or Tab Size 4, the file's own as the settings, its .editorconfig or how it was indented made it
            (bool Tabs, int Size) indent = (code.Document.Indent.StartsWith('\t'), code.TabSize);
            if (indentShown != indent)
            {
                indentShown = indent;
                indentItem.Text = Said(indent.Tabs ? "settings_text.status_tabs" : "status_bar.indent", ("count", indent.Size.ToString()));
            }

            if (languageFor != document || languagesShown != languages.ServersChanged || !ReferenceEquals(languageShownWith, document.Editor.Highlighter))
            {
                languageFor = document;
                languageShownWith = document.Editor.Highlighter;
                languagesShown = languages.ServersChanged;
                var (icon, language) = KindOf(document);
                languageItem.Text = $"$({icon}) {language}";
            }
            if (settings.CodeZoom != codeZoomShown)
            {
                codeZoomShown = settings.CodeZoom;
                codeZoomItem.Text = $"$(zoom-in) {settings.CodeZoom * 100.0f:0}%";
            }
            return;
        }

        int count = designer.componentCount;
        if (componentsShown != (count, document.Selection.Count))
        {
            componentsShown = (count, document.Selection.Count);
            componentsItem.Text = document.Selection.Count > 0
                ? $"$(symbol-class) {Said("status_bar.components_selected", ("count", HexText.Count("component", count)), ("selected", document.Selection.Count.ToString()))}"
                : $"$(symbol-class) {Said("status_bar.components", ("count", HexText.Count("component", count)))}";
        }

        // How big the layout really is drawn against its own pixels, what zooming says too. It said its zoom against fitting
        // the canvas, 100% for a layout drawn at half its size
        float actual = document.Module.Scale.X / MathF.Max(0.01f, compositor.Scale) * 100.0f;
        if (MathF.Abs(actual - zoomShown) >= 0.5f)
        {
            zoomShown = actual;
            zoomItem.Text = $"$(zoom-in) {actual:0}%";
        }

        if (skinShown != (UISkins.Current, UISkins.Theme))
        {
            skinShown = (UISkins.Current, UISkins.Theme);
            skinItem.Text = UISkins.Theme is { } theme ? $"$(paintcan) {UISkins.Current.Title}, {theme}" : $"$(paintcan) {UISkins.Current.Title}";
        }
    }

    /// <summary>What a document is to Hex, the icon of its tab and what the status bar calls it.</summary>
    private (string Icon, string Language) KindOf(HexDocument shown)
    {
        if (!shown.IsText)
            return ("layout", Said("status_bar.kind_layout"));

        // A backup is what it was a copy of, menu.hor.orig HIDL, and a file set to another language by hand is that
        string name = shown.Path is { } path ? Path.GetFileName(path) : shown.Name;
        string extension = Path.GetExtension(CodeLanguages.WithoutBackupSuffix(name));

        if (extension.Equals(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase))
            return ("folder-library", Said("status_bar.kind_workspace"));
        if (!extension.Equals(".hor", StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(shown.Editor.Highlighter, CodeLanguages.Hidl))
            return shown.Editor.Highlighter is { } language ? ("file-code", languages.ServerShownOf(shown) is { } linter ? $"{language.Name}, {linter}" : language.Name) : ("file", Said("status_bar.kind_plain_text"));
        if (name.Equals("skin.hor", StringComparison.OrdinalIgnoreCase))
            return ("paintcan", Said("status_bar.kind_skin"));
        if (shown.Path is { } file && Path.GetFileName(Path.GetDirectoryName(file))?.Equals("localisation", StringComparison.OrdinalIgnoreCase) == true)
            return ("globe", Said("status_bar.kind_strings"));

        return ("file-code", "HIDL");
    }

    private void SwapLineEndings()
    {
        if (LayoutTools)
            return;

        document.CrLf = !document.CrLf;
        codeDirty = tabsDirty = true;
        Say(Said("status_bar.line_endings_swapped", ("name", document.Name), ("ending", document.CrLf ? "\\r\\n (CRLF)" : "\\n (LF)")));
    }

    /// <summary>The skins there are, as a menu where the pointer is, from the status bar.</summary>
    private void ShowSkinMenu()
    {
        var shown = new Menu("skin");
        AddSkins(shown);
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>
    /// Helper method to put every skin into a menu, the one shown ticked, a skin with themes a menu of its own of them
    /// (all the themes of all the skins in one list ran off the bottom of the window).
    /// </summary>
    private void AddSkins(Menu into)
    {
        foreach (UISkinChoice skin in UISkins.Available)
        {
            if (skin.Themes.Count == 0)
            {
                into.Add(skin.Title, () => UseSkin(skin, null)).IsChecked = () => UISkins.Current == skin;
                continue;
            }

            Menu themes = into.AddMenu(skin.Title);
            foreach (string theme in skin.Themes)
            {
                themes.Add($"{skin.Title}, {theme}", () => UseSkin(skin, theme)).IsChecked =
                    () => UISkins.Current == skin && (UISkins.Theme ?? skin.UsualTheme) == theme;
            }
        }
    }

    /// <summary>
    /// Helper method to show the layouts in another skin, the one a game would be set to. Nothing, and nothing said, for
    /// the one they're in already, which picking a look put back and said so, "layouts are shown in Flat, horizon".
    /// </summary>
    private void UseSkin(UISkinChoice skin, string? theme)
    {
        if (UISkins.Current == skin && (UISkins.Theme ?? skin.UsualTheme) == (theme ?? skin.UsualTheme))
            return;

        UISkins.Use(skin, theme);
        Say(theme is null ? Said("status_bar.skin_shown", ("skin", skin.Title)) : Said("status_bar.skin_shown_theme", ("skin", skin.Title), ("theme", theme)));
    }
}
