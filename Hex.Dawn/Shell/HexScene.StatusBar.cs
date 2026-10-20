using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The status bar along the bottom, a native StatusBar. On the left how many problems there are and the last thing Hex
// said. On the right where the caret is, the indentation, the line endings and what the
// file is in code, and in the designer where the pointer is, how many components there are, the zoom, the skin and
// the screen the layout is made for. Most of it does something when clicked.
internal sealed partial class HexScene
{
    private StatusBar statusBar = null!;
    private StatusItem problemsItem = null!, messageItem = null!;
    private StatusItem caretItem = null!, selectionItem = null!, indentItem = null!, endingsItem = null!, languageItem = null!;
    private StatusItem coordsItem = null!, componentsItem = null!, zoomItem = null!, skinItem = null!, screenItem = null!;
    private StatusItem codeZoomItem = null!, runningItem = null!;
    private string runningShown = string.Empty;
    private float codeZoomShown = -1.0f;

    // What the skin and the kind of file were last written for. Every tick wrote them anew, garbage a tick for nothing
    private (UISkinChoice? Skin, string? Theme) skinShown;
    private HexDocument? languageFor;

    // How many components the layout has, counted when its tree is put together, not walked again every tick
    private int componentCount;

    // What the items were last written from, so a number is only made into text when it changes
    private (int Line, int Column, int Selected, int Version)? caretShown;
    private (int Errors, int Warnings)? problemsShown;
    private (int Count, int Selected)? componentsShown;
    private float zoomShown = -1.0f;

    private void BuildStatusBar()
    {
        statusBar = layout.Get<StatusBar>("status_bar");

        bool Code() => !LayoutTools;
        bool Design() => LayoutTools;

        problemsItem = statusBar.Add("$(error) 0  $(warning) 0", pressed: () => ShowPanel(PROBLEMS_PAGE), tooltip: "What is wrong, click for the list (F8 checks every file)");
        messageItem = statusBar.Add(string.Empty, pressed: () => ShowPanel(OUTPUT_PAGE), tooltip: "What Hex said last, click for all of it");

        // A game started from here (Fight!) reads what's saved as it changes, Defold's hot reload, said while it runs
        runningItem = statusBar.Add(string.Empty, right: true, tooltip: "A game started from Hex is running, and reads every file it uses again when it's saved here, Ctrl+S and look");
        runningItem.IsVisible = () => Volatile.Read(ref programsRunning) > 0;

        // The work going in the background, on the right before the rest (HexScene.Background)
        BuildWorkItem();

        caretItem = statusBar.Add("Ln 1, Col 1", right: true, pressed: GoToLine, tooltip: "Go to a line (Ctrl+G)");
        caretItem.IsVisible = Code;
        selectionItem = statusBar.Add(string.Empty, right: true);
        selectionItem.IsVisible = Code;
        indentItem = statusBar.Add("Spaces: 4", right: true, tooltip: "Tab puts four spaces in, a tab in the file is drawn as wide as four");
        indentItem.IsVisible = Code;
        endingsItem = statusBar.Add("CRLF", right: true, pressed: SwapLineEndings, tooltip: "How the lines end in the file, click to swap them");
        endingsItem.IsVisible = Code;
        languageItem = statusBar.Add("HIDL", right: true, tooltip: "What the file is to Hex");
        languageItem.IsVisible = Code;
        codeZoomItem = statusBar.Add(string.Empty, right: true, pressed: () => ZoomCode(0.0f), tooltip: "How big the code is drawn (Ctrl+= and Ctrl+-, or Ctrl and the wheel), click for the size it comes at");
        codeZoomItem.IsVisible = () => !LayoutTools && settings.CodeZoom != 1.0f;

        coordsItem = statusBar.Add(string.Empty, right: true, tooltip: "Where the pointer is in the layout, in its units");
        coordsItem.IsVisible = Design;
        componentsItem = statusBar.Add(string.Empty, right: true, pressed: () => ShowSide(HexSide.Design), tooltip: "How many components the layout has, and how many are selected");
        componentsItem.IsVisible = Design;
        zoomItem = statusBar.Add("$(zoom-in) 100%", right: true, pressed: ZoomToFit, tooltip: "How big the layout is drawn, click to fit it to the canvas (Ctrl+0)");
        zoomItem.IsVisible = Design;
        skinItem = statusBar.Add(string.Empty, right: true, pressed: ShowSkinMenu, tooltip: "The skin the layout is shown in, a game's setting, click for the others");
        skinItem.IsVisible = Design;
        screenItem = statusBar.Add($"$(device-desktop) {DesignSize.X:0} x {DesignSize.Y:0}", right: true, tooltip: "The screen the layout is laid out for");
        screenItem.IsVisible = Design;

        caretItem.MinWidth = 120.0f;
    }

    /// <summary>The name of the running game said once, when it changes.</summary>
    private void UpdateRunningItem()
    {
        if (ReferenceEquals(runningShown, runningName))
            return;

        runningShown = runningName;
        runningItem.Text = $"$(run) {runningName} running, saves reload in it";
    }

    /// <summary>Every tick, what the status bar says brought up to date. Nothing is written into text that hasn't changed.</summary>
    private void UpdateStatusBar()
    {
        if (messageItem.Text.Length > 0 && System.Environment.TickCount64 - saidAt > SAID_FOR)
        {
            messageItem.Text = string.Empty;
            messageItem.Color = null;
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
            int selected = code.Document.HasSelection ? code.Document.SelectedText.Length : 0;
            if (caretShown != (caret.Line, caret.Column, selected, code.Document.Version))
            {
                caretShown = (caret.Line, caret.Column, selected, code.Document.Version);
                caretItem.Text = $"Ln {caret.Line + 1}, Col {caret.Column + 1}";
                selectionItem.Text = selected > 0 ? $"({selected} selected)" : string.Empty;
            }

            endingsItem.Text = document.CrLf ? "CRLF" : "LF";

            if (languageFor != document)
            {
                languageFor = document;
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

        int count = componentCount;
        if (componentsShown != (count, document.Selection.Count))
        {
            componentsShown = (count, document.Selection.Count);
            componentsItem.Text = document.Selection.Count > 0
                ? $"$(symbol-class) {count} components, {document.Selection.Count} selected"
                : $"$(symbol-class) {count} components";
        }

        if (canvasZoom != zoomShown)
        {
            zoomShown = canvasZoom;
            zoomItem.Text = $"$(zoom-in) {canvasZoom * 100.0f:0}%";
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
            return ("layout", "HIDL layout");

        string name = shown.Path is { } path ? Path.GetFileName(path) : shown.Name;
        string extension = Path.GetExtension(name);

        if (extension.Equals(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase))
            return ("folder-library", "Hex workspace");
        if (!extension.Equals(".hor", StringComparison.OrdinalIgnoreCase))
            return shown.Editor.Highlighter is { } language ? ("file-code", languages.LinterOf(shown) is { } linter ? $"{language.Name}, {linter}" : language.Name) : ("file", "Plain text");
        if (name.Equals("skin.hor", StringComparison.OrdinalIgnoreCase))
            return ("paintcan", "HIDL skin");
        if (shown.Path is { } file && Path.GetFileName(Path.GetDirectoryName(file))?.Equals("localisation", StringComparison.OrdinalIgnoreCase) == true)
            return ("globe", "HIDL strings");

        return ("file-code", "HIDL");
    }

    private void SwapLineEndings()
    {
        if (LayoutTools)
            return;

        document.CrLf = !document.CrLf;
        codeDirty = tabsDirty = true;
        Say($"{document.Name} is saved with its lines ended {(document.CrLf ? "\\r\\n (CRLF)" : "\\n (LF)")} from now on, save it to keep that");
    }

    /// <summary>The skins there are, as a menu where the pointer is, from the status bar.</summary>
    private void ShowSkinMenu()
    {
        var shown = new Menu("skin");
        AddSkins(shown);
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>Helper method to put every skin and every theme of each into a menu, the one shown ticked.</summary>
    private void AddSkins(Menu into)
    {
        foreach (UISkinChoice skin in UISkins.Available)
        {
            if (skin.Themes.Count == 0)
            {
                into.Add(skin.Title, () => UseSkin(skin, null)).IsChecked = () => UISkins.Current == skin;
                continue;
            }

            foreach (string theme in skin.Themes)
            {
                into.Add($"{skin.Title}, {theme}", () => UseSkin(skin, theme)).IsChecked =
                    () => UISkins.Current == skin && (UISkins.Theme ?? skin.UsualTheme) == theme;
            }

            into.AddSeparator();
        }
    }

    /// <summary>Helper method to show the layouts in another skin, the one a game would be set to.</summary>
    private void UseSkin(UISkinChoice skin, string? theme)
    {
        UISkins.Use(skin, theme);
        Say(theme is null ? $"layouts are shown in {skin.Title}" : $"layouts are shown in {skin.Title}, {theme}");
    }
}
