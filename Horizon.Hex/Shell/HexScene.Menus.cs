using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// The toolbar, the keys and the menu bar, which is everything there is to do that isn't done to something on screen
// directly. Only what means something for what is open is on the bar, the Layout menu is the designer's. The palette of
// components is Designer/HexScene.Palette, every item of every menu in a list is CommandPalette.
internal sealed partial class HexScene
{
    private Menu fileMenu = null!;

    private void WireToolbar()
    {
        layout.Get<Button>("play_button").OnPressed = RunOrPlay;
    }

    /// <summary>F5, the file run, or the entrances of a layout played in its designer.</summary>
    private void RunOrPlay()
    {
        if (LayoutTools) PlayIntros();
        else RunCode();
    }

    /// <summary>
    /// Helper method to set up the keys of the editor. The ones with control work whatever is going on,
    /// the others keep out of the way while something is being typed.
    /// </summary>
    private void BuildShortcuts()
    {
        shortcuts.Add(Key.Z, Undo, control: true, whileTyping: true);
        shortcuts.Add(Key.Y, Redo, control: true, whileTyping: true);
        shortcuts.Add(Key.S, Save, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.S, SaveAll, control: true, whileTyping: true, shift: true);
        shortcuts.Add(Key.O, OpenPressed, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.N, () => Show(NewDocument()), control: true, whileTyping: true);
        shortcuts.Add(Key.W, Close, control: true, whileTyping: true);

        shortcuts.Add(Key.Delete, DeleteAll);

        // The clipboard, which a text box keeps for itself while it is being typed into
        shortcuts.Add(Key.C, CopySelected, control: true);
        shortcuts.Add(Key.X, CutSelected, control: true);
        shortcuts.Add(Key.V, Paste, control: true);
        shortcuts.Add(Key.Comma, OpenSettings, control: true);

        // Nothing types an F5, so it plays the entrances from a text box as well. A file of code is run instead, and with
        // control a function of it is called tick after tick
        shortcuts.Add(Key.F5, RunOrPlay, whileTyping: true);
        shortcuts.Add(Key.F5, () => { if (!LayoutTools) OpenRunner(); }, control: true, whileTyping: true);

        // F7 flips a layout between its designer and its code, the way Visual Studio's designers go
        shortcuts.Add(Key.F7, ToggleView, whileTyping: true);

        // The window, VS Code's keys for the side bar, the panel and the command palette
        shortcuts.Add(Key.B, ToggleSideBar, control: true, whileTyping: true);
        shortcuts.Add(Key.B, ToggleProperties, control: true, whileTyping: true, alt: true);
        shortcuts.Add(Key.J, TogglePanel, control: true, whileTyping: true);
        shortcuts.Add(Key.P, CommandPalette, control: true, whileTyping: true, shift: true);

        // The code, in any file. These go while typing, the editor of code is where they are wanted most
        shortcuts.Add(Key.Enter, ApplyCode, control: true, whileTyping: true);
        shortcuts.Add(Key.F, FindInFile, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.F, FindInFiles, control: true, whileTyping: true, shift: true);
        shortcuts.Add(Key.H, ReplaceInFile, control: true, whileTyping: true);
        // Ctrl+G groups what is selected in a layout, it only goes to a line where there is code to go about in
        shortcuts.Add(Key.G, () => { if (!LayoutTools || compositor.Focus is CodeEditor) GoToLine(); }, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.P, QuickOpen, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.F3, () => FindAgain(forward: true), whileTyping: true, shift: false);
        shortcuts.Add(Key.F3, () => FindAgain(forward: false), whileTyping: true, shift: true);
        shortcuts.Add(Key.F8, CheckAllFiles, whileTyping: true);
        shortcuts.Add(Key.F12, GoToDefinition, whileTyping: true, shift: false);
        shortcuts.Add(Key.F12, FindReferences, whileTyping: true, shift: true);
        // F2 renames, a name in the code, or what is selected in the designer by its name in the properties
        shortcuts.Add(Key.F2, () => { if (compositor.Focus is CodeEditor) RenameSymbol(); else if (LayoutTools) RenameSelected(); }, whileTyping: true);

        // Ctrl+Tab goes through the files that are open, the way every editor does, with shift the other way
        shortcuts.Add(Key.Tab, () => CycleDocuments(1), control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.Tab, () => CycleDocuments(-1), control: true, whileTyping: true, shift: true);
        shortcuts.Add(Key.E, ShowExplorer, control: true, whileTyping: true, shift: true);
        shortcuts.Add(Key.O, ShowOutline, control: true, whileTyping: true, shift: true);

        // With control the arrows move it about the tree instead of about the canvas
        shortcuts.Add(Key.Up, () => MoveSelected(-1), control: true);
        shortcuts.Add(Key.Down, () => MoveSelected(1), control: true);
        shortcuts.Add(Key.Left, () => MoveSelectedAcross(inwards: false), control: true);
        shortcuts.Add(Key.Right, () => MoveSelectedAcross(inwards: true), control: true);

        // The arrows move whatever is selected a pixel at a time, for lining things up by eye
        shortcuts.Add(Key.Left, () => Nudge(-1, 0));
        shortcuts.Add(Key.Right, () => Nudge(1, 0));
        shortcuts.Add(Key.Up, () => Nudge(0, 1));
        shortcuts.Add(Key.Down, () => Nudge(0, -1));
    }

    private void BuildMenus()
    {
        Func<bool> code = () => !LayoutTools;
        Func<bool> design = () => LayoutTools;
        Func<bool> some = () => LayoutTools && document.Selected is not null;

        fileMenu = menu.AddMenu("File");
        RebuildFileMenu();

        Menu edit = menu.AddMenu("Edit");
        edit.Add("Undo", Undo, "Ctrl+Z", "discard").IsEnabled = () => !LayoutTools || document.CanUndo || recordPending;
        edit.Add("Redo", Redo, "Ctrl+Y", "redo").IsEnabled = () => !LayoutTools || document.CanRedo;
        edit.AddSeparator();
        edit.Add("Cut", CutSelected, "Ctrl+X").IsVisible = design;
        edit.Add("Copy", CopySelected, "Ctrl+C", "copy").IsVisible = design;
        edit.Add("Paste", Paste, "Ctrl+V").IsVisible = design;
        edit.Add("Duplicate", DuplicateSelected, "Ctrl+D").IsVisible = design;
        edit.Add("Delete", DeleteAll, "Del", "trash").IsVisible = design;
        edit.AddSeparator();
        edit.Add("Find...", FindInFile, "Ctrl+F", "search");
        edit.Add("Find Next", () => FindAgain(forward: true), "F3");
        edit.Add("Find Previous", () => FindAgain(forward: false), "Shift+F3");
        edit.Add("Replace...", ReplaceInFile, "Ctrl+H", "replace").IsVisible = code;
        edit.AddSeparator();
        edit.Add("Find in Files...", FindInFiles, "Ctrl+Shift+F", "search");
        edit.AddSeparator();
        edit.Add("Toggle Line Comment", () => this.code.Document.ToggleComment(), "Ctrl+/", "comment").IsVisible = code;

        Menu selection = menu.AddMenu("Selection");
        selection.IsVisible = code;
        selection.Add("Select All", () => this.code.Document.SelectAll(), "Ctrl+A");
        selection.Add("Select Line", SelectCaretLine);
        selection.Add("Select Word", () => this.code.Document.SelectWordAt(this.code.Document.Caret));
        selection.AddSeparator();
        selection.Add("Move Line Up", () => this.code.Document.MoveLines(-1), "Alt+Up", "arrow-up");
        selection.Add("Move Line Down", () => this.code.Document.MoveLines(1), "Alt+Down", "arrow-down");
        selection.Add("Duplicate Line", () => this.code.Document.Duplicate(), "Ctrl+Shift+D", "copy");
        selection.AddSeparator();
        selection.Add("Add Next Occurrence", () => this.code.Document.AddNextOccurrence(), "Ctrl+D", "selection");
        selection.Add("Add Cursor Above", () => this.code.Document.AddCaretVertically(-1), "Ctrl+Alt+Up");
        selection.Add("Add Cursor Below", () => this.code.Document.AddCaretVertically(1), "Ctrl+Alt+Down");
        selection.Add("Add Cursors to Line Ends", () => this.code.Document.CaretsAtLineEnds(), "Ctrl+Shift+L");
        selection.Add("Single Cursor", () => this.code.Document.CollapseCarets(), "Esc").IsEnabled = () => this.code.Document.HasExtraCarets;
        selection.AddSeparator();
        Menu folding = selection.AddMenu("Folding", "fold");
        folding.Add("Fold", () => this.code.FoldAtCaret(), "Ctrl+Shift+[", "fold-up");
        folding.Add("Unfold", () => this.code.UnfoldAtCaret(), "Ctrl+Shift+]", "fold-down");
        folding.AddSeparator();
        folding.Add("Fold All", () => this.code.FoldAll(1));
        folding.Add("Fold Level 2", () => this.code.FoldAll(2));
        folding.Add("Fold Level 3", () => this.code.FoldAll(3));
        folding.Add("Unfold All", () => this.code.UnfoldAll());

        Menu view = menu.AddMenu("View");
        view.Add("Command Palette...", CommandPalette, "Ctrl+Shift+P", "symbol-misc");
        view.AddSeparator();
        view.Add("Explorer", ShowExplorer, "Ctrl+Shift+E", "files");
        view.Add("Search", () => ShowSide(HexSide.Search), icon: "search");
        view.Add("Outline", ShowOutline, "Ctrl+Shift+O", "list-tree").IsVisible = code;
        view.Add("Toolbox", ShowOutline, "Ctrl+Shift+O", "layout").IsVisible = design;
        view.Add("Problems", () => ShowPanel(PROBLEMS_PAGE), icon: "error");
        view.Add("Output", () => ShowPanel(OUTPUT_PAGE), icon: "output");
        view.AddSeparator();
        view.Add("Side Bar", ToggleSideBar, "Ctrl+B", "layout-sidebar-left").IsChecked = () => left.Shown;
        view.Add("Panel", TogglePanel, "Ctrl+J", "layout-panel").IsChecked = () => bottom.Shown;
        view.Add("Properties", ToggleProperties, "Ctrl+Alt+B", "layout-sidebar-right").IsChecked = () => right.Shown;
        view.Add("Zen Mode", ToggleQuiet, "F11", "screen-full").IsChecked = () => Quiet;
        view.Add("Word Wrap", ToggleWrap, "Alt+Z", "word-wrap").IsChecked = () => settings.WrapCode;
        view.Add("Split Editor", ToggleSplit, "Ctrl+\\", "split-horizontal").IsChecked = () => Split;
        view.AddSeparator();
        view.Add("Toggle Designer and Code", ToggleView, "F7", "layout").IsEnabled = () => !document.IsText;
        view.Add("Zoom In", () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), "Ctrl+=", "zoom-in").IsVisible = design;
        view.Add("Zoom Out", () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), "Ctrl+-", "zoom-out").IsVisible = design;
        view.Add("Zoom to Fit", ZoomToFit, "Ctrl+0", "screen-full").IsVisible = design;
        view.Add("Zoom In", () => ZoomCode(CODE_ZOOM_STEP), "Ctrl+=", "zoom-in").IsVisible = code;
        view.Add("Zoom Out", () => ZoomCode(1.0f / CODE_ZOOM_STEP), "Ctrl+-", "zoom-out").IsVisible = code;
        view.Add("Reset Zoom", () => ZoomCode(0.0f), "Ctrl+0").IsVisible = code;
        view.Add("Actual Size", ZoomToActualSize, "Ctrl+1").IsVisible = design;
        view.AddSeparator();
        layoutDebugger.AddTo(view, () => Say(layoutDebugger.IsOn ? "layout debugger on, padding is green, gaps are orange" : "layout debugger off"));
        Menu size = view.AddMenu("Editor Scale", "zoom-in");
        foreach (float scale in Scales)
            size.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;

        // The skin the layouts are shown in, every one there is under Assets/uix and every theme of each. It is the
        // game's setting (UISkins) and not the editor's own, a layout looks here the way it will in a game set to
        // the same, and the editor itself stays in its flat one
        AddSkins(view.AddMenu("Layout Skin", "paintcan"));

        Menu go = menu.AddMenu("Go");
        go.Add("Back", () => GoPlace(back: true), "Alt+Left", "arrow-left");
        go.Add("Forward", () => GoPlace(back: false), "Alt+Right", "arrow-right");
        go.AddSeparator();
        go.Add("Go to File...", QuickOpen, "Ctrl+P", "go-to-file");
        go.Add("Go to Line...", GoToLine, "Ctrl+G").IsVisible = code;
        go.Add("Go to Symbol...", GoToSymbol, icon: "symbol-variable").IsVisible = code;
        go.AddSeparator();
        go.Add("Go to Definition", GoToDefinition, "F12", "references").IsVisible = code;
        go.Add("Find All References", FindReferences, "Shift+F12", "references").IsVisible = code;
        go.AddSeparator();
        go.Add("Next Problem", () => StepProblem(1), icon: "arrow-down");
        go.Add("Previous Problem", () => StepProblem(-1), icon: "arrow-up");

        Menu run = menu.AddMenu("Run");
        run.Add("Run File", RunCode, "F5", "play").IsVisible = code;
        run.Add("Run Behaviour...", OpenRunner, "Ctrl+F5", "debug-alt").IsVisible = code;
        run.Add("Play Entrances", PlayIntros, "F5", "play").IsVisible = design;
        run.AddSeparator();
        run.Add("Check All Files", CheckAllFiles, "F8", "check-all");
        run.Add("Trigger Completion", () => { this.code.Focus(); this.code.Complete(); }, "Ctrl+Space").IsVisible = code;
        run.AddSeparator();
        run.Add("Apply Code to Layout", ApplyCode, "Ctrl+Enter").IsEnabled = () => document.CodeEdited;
        run.Add("Discard Code Changes", RevertCode, icon: "discard").IsEnabled = () => document.CodeEdited;
        run.Add("Convert to Layout...", MakeLayout, icon: "layout").IsEnabled = () => document.IsText && IsLayoutCode(this.code.Text);

        Menu built = menu.AddMenu("Layout");
        built.IsVisible = design;
        Menu add = built.AddMenu("Add", "add");
        foreach (string kind in Palette)
            add.Add(kind.Replace('_', ' '), () => AddComponentOf(kind), icon: KindIcon(kind));
        built.AddSeparator();
        built.Add("Group", GroupSelected, "Ctrl+G", "group-by-ref-type").IsEnabled = some;
        built.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);
        AddConvertMenu(built.AddMenu("Convert To", "arrow-swap"));
        built.AddSeparator();
        AddMoveMenu(built.AddMenu("Move", "arrow-swap"), some);
        AddArrangeMenu(built.AddMenu("Arrange", "layers"), some);
        AddAlignMenu(built.AddMenu("Align", "symbol-ruler"), some);
        built.AddSeparator();
        built.Add("Select All", SelectAll, "Ctrl+A");
        built.Add("Deselect All", () => Select(null), "Esc").IsEnabled = some;
        built.Add("Show All Layers", ShowAllLayers, icon: "eye").IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        built.Add("Show Code", ShowSelectedCode, icon: "code").IsEnabled = some;
        built.AddSeparator();
        FillScreenMenu(built.AddMenu("Screen", "device-desktop"));

        Menu help = menu.AddMenu("Help");
        help.Add("Keyboard Shortcuts...", OpenKeysWindow, icon: "keyboard");
        help.Add("HIDL Reference...", ShowLanguageHelp, icon: "book");
        help.Add("Release Notes...", ShowChangelog, icon: "history");
        help.Add("Open Hex's Own Layout", () => Open(EDITOR_LAYOUT), icon: "layout");
        help.AddSeparator();
        help.Add("About Hex...", ShowAbout, icon: "info");
    }

    /// <summary>The ways to move what is selected about the tree, for the Layout menu and the menu of a component.</summary>
    private void AddMoveMenu(Menu into, Func<bool> some)
    {
        into.Add("Move Up", () => MoveSelected(-1), "Ctrl+Up", "arrow-up").IsEnabled = some;
        into.Add("Move Down", () => MoveSelected(1), "Ctrl+Down", "arrow-down").IsEnabled = some;
        into.Add("Move Out", () => MoveSelectedAcross(inwards: false), "Ctrl+Left", "arrow-left").IsEnabled = some;
        into.Add("Move In", () => MoveSelectedAcross(inwards: true), "Ctrl+Right", "arrow-right").IsEnabled = some;
    }

    private void AddArrangeMenu(Menu into, Func<bool> some)
    {
        into.Add("Bring to Front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = some;
        into.Add("Bring Forward", () => ReorderSelected(HexDocument.Order.Forward), "]").IsEnabled = some;
        into.Add("Send Backward", () => ReorderSelected(HexDocument.Order.Backward), "[").IsEnabled = some;
        into.Add("Send to Back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = some;
    }

    /// <summary>Lining up is against each other with several things selected, against the container with one.</summary>
    private void AddAlignMenu(Menu into, Func<bool> some)
    {
        into.Add("Align Left", () => Align(Edge.Left)).IsEnabled = some;
        into.Add("Align Centre", () => Align(Edge.Center)).IsEnabled = some;
        into.Add("Align Right", () => Align(Edge.Right)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Align Top", () => Align(Edge.Top)).IsEnabled = some;
        into.Add("Align Middle", () => Align(Edge.Middle)).IsEnabled = some;
        into.Add("Align Bottom", () => Align(Edge.Bottom)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Distribute Horizontally", () => Distribute(horizontally: true)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
        into.Add("Distribute Vertically", () => Distribute(horizontally: false)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
    }

    /// <summary>Helper method to show the layouts in another skin, the one a game would be set to.</summary>
    private void UseSkin(UISkinChoice skin, string? theme)
    {
        UISkins.Use(skin, theme);
        Say(theme is null ? $"layouts are shown in {skin.Title}" : $"layouts are shown in {skin.Title}, {theme}");
    }

    /// <summary>
    /// Puts the file menu together. What it always has, and the files that were worked on last in a menu of their own.
    /// Again whenever those change.
    /// </summary>
    private void RebuildFileMenu()
    {
        fileMenu.Clear();

        fileMenu.Add("New Layout", () => Show(NewDocument()), "Ctrl+N", "new-file");
        fileMenu.Add("New File...", () => NewFileIn(workspace.IsEmpty ? browseDirectory : workspace.Folders[0]), icon: "file-add");
        fileMenu.Add("Open...", OpenPressed, "Ctrl+O", "folder-opened");

        Menu recentMenu = fileMenu.AddMenu("Open Recent", "history");
        for (int i = 0; i < recent.Count; i++)
        {
            // Numbered, two of them can well have the same name in different folders
            string path = recent[i];
            recentMenu.Add($"{i + 1}  {Path.GetFileName(path)}", () => Open(path));
        }
        recentMenu.AddSeparator();
        recentMenu.Add("Clear Recently Opened", ForgetRecent).IsEnabled = () => recent.Count > 0;

        fileMenu.AddSeparator();
        fileMenu.Add("Save", Save, "Ctrl+S", "save");
        fileMenu.Add("Save As...", SaveAs);
        fileMenu.Add("Save All", SaveAll, "Ctrl+Shift+S", "save-all");
        fileMenu.Add("Revert File", RevertFile, icon: "discard").IsEnabled = () => document.Path is not null && (document.Modified || document.CodeEdited);
        fileMenu.AddSeparator();
        fileMenu.Add("Close", Close, "Ctrl+W", "close");
        fileMenu.Add("Close All", () => CloseAll(), icon: "close-all");
        fileMenu.AddSeparator();
        fileMenu.Add("Open Folder...", () => AskFolder(OpenFolder), icon: "folder-opened");
        fileMenu.Add("Add Folder to Workspace...", () => AskFolder(AddFolder), icon: "new-folder");
        fileMenu.Add("Open Workspace...", OpenWorkspacePressed, icon: "folder-library");
        fileMenu.Add("Save Workspace", () => SaveWorkspacePressed(asNew: false)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Save Workspace As...", () => SaveWorkspacePressed(asNew: true)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Close Workspace", CloseWorkspace).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.AddSeparator();
        fileMenu.Add("Settings...", OpenSettings, "Ctrl+,", "settings-gear");
        fileMenu.AddSeparator();
        // Through the window manager, on the window's thread, asking first like its x does
        fileMenu.Add("Exit", () => Engine.WindowManager.Close(), "Alt+F4", "close");
        MarkReboundKeys();
    }
}
