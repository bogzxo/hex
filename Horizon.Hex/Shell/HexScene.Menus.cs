using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// The palette, the toolbar, the keys and the menu bar, which is everything there is to do that isn't done to something
// on screen directly. Only what means something for what is open is on the bar, the Layout menu is the designer's, and
// the command palette (Ctrl+Shift+P) is every item of every menu in a list that narrows as it is typed into.
internal sealed partial class HexScene
{
    private void BuildPalette()
    {
        // One button per kind of component, each made from the template the palette names
        var items = layout.Populate("palette", Palette.Length);

        for (int i = 0; i < items.Count; i++)
        {
            string kind = Palette[i];
            var button = items[i].Get<Button>("button");

            button.Label = kind.Replace('_', ' ');
            button.Icon = KindIcon(kind);
            button.Tooltip = Describe(kind);
            button.OnPressed = () => AddComponentOf(kind);
            paletteButtons.Add(button);
        }

        // The buttons over the tree, the things done to it most without going up to a menu for them
        void Tool(string name, string tip, Action pressed)
        {
            var button = layout.Get<Button>(name);
            button.Tooltip = tip;
            button.OnPressed = pressed;
        }

        Tool("move_up", "Move up the tree, earlier among its siblings (Ctrl+Up)", () => MoveSelected(-1));
        Tool("move_down", "Move down the tree, later among its siblings (Ctrl+Down)", () => MoveSelected(1));
        Tool("move_out", "Move out of the container it is in (Ctrl+Left)", () => MoveSelectedAcross(inwards: false));
        Tool("move_in", "Move into the container before it (Ctrl+Right)", () => MoveSelectedAcross(inwards: true));
        Tool("duplicate", "Make a copy next to it (Ctrl+D)", DuplicateSelected);
        Tool("remove", "Delete it and everything in it (Del)", DeleteAll);
    }

    /// <summary>The buttons along the top under the menus.</summary>
    private void WireToolbar()
    {
        layout.Get<Button>("new_file").OnPressed = () => Show(NewDocument());
        layout.Get<Button>("open_file").OnPressed = OpenPressed;
        layout.Get<Button>("save_file").OnPressed = Save;
        layout.Get<Button>("save_all").OnPressed = SaveAll;
        layout.Get<Button>("undo_button").OnPressed = Undo;
        layout.Get<Button>("redo_button").OnPressed = Redo;
        layout.Get<Button>("play_button").OnPressed = RunOrPlay;
        layout.Get<Button>("find_button").OnPressed = FindInFiles;
    }

    /// <summary>F5, the file run, or the entrances of a layout played in its designer.</summary>
    private void RunOrPlay()
    {
        if (LayoutTools) PlayIntros();
        else RunCode();
    }

    /// <summary>
    /// Helper method for what a kind of component is for, the line the palette shows when the pointer rests on its button.
    /// </summary>
    private static string Describe(string kind) => kind switch
    {
        "stack" => "A row or a column of things, with a gap between them",
        "grid" => "Cells in rows and columns, so many across",
        "collapsible" => "A stack under a bar with a title, which folds it away",
        "panel" => "A box to put things in, or a backdrop",
        "scroll" => "A panel that scrolls when there is more than fits",
        "label" => "A piece of text, which can wrap",
        "button" => "A button with a label, pressed by a click or the navigator",
        "toggle" => "A box that is ticked or isn't",
        "textbox" => "A line somebody types into",
        "number_box" => "A number, dragged across or typed",
        "selector" => "One of a few options, stepped through with arrows",
        "dropdown" => "One of a list, which drops down",
        "list" => "A list of texts in a box, one chosen",
        "slider" => "A number between two ends, slid along",
        "color_picker" => "A colour, picked by hue, shade and how solid it is",
        "progress_bar" => "How full something is, a health bar say",
        "image" => "A picture, a sprite of the skin or a file",
        "tabs" => "Pages with a strip of tabs along the top",
        "divider" => "A line between things",
        "spacer" => "Nothing, of a size, for a gap",
        "readout" => "A number that changes all the time, and what it is counted in",
        "meter" => "A thin line that says how full something is",
        "graph" => "The last so many of something as bars",
        "segments" => "One bar cut up by how much of a whole things are",
        "symbol" => "One of the icons every skin has, on its own",
        "status_bar" => "The strip along the bottom of an editor, pieces on the left and the right",
        "tab_strip" => "A row of tabs for documents, with an x on each",
        "breadcrumbs" => "Where something is, as a path of pieces",
        _ => string.Empty
    };

    /// <summary>The icon of a kind of component, in the toolbox, the tree and the breadcrumbs.</summary>
    private static string KindIcon(string kind) => kind switch
    {
        "stack" => "list-flat",
        "collapsible" => "fold",
        "grid" => "table",
        "panel" => "window",
        "scroll" => "list-selection",
        "label" => "symbol-text",
        "button" => "symbol-event",
        "toggle" => "check",
        "textbox" => "symbol-string",
        "number_box" => "symbol-number",
        "selector" => "arrow-swap",
        "dropdown" => "chevron-down",
        "list" => "list-unordered",
        "slider" => "settings",
        "color_picker" => "symbol-color",
        "progress_bar" => "loading",
        "image" => "file-media",
        "tabs" => "window",
        "divider" => "dash",
        "spacer" => "whitespace",
        "readout" => "dashboard",
        "meter" => "pulse",
        "graph" => "graph",
        "segments" => "symbol-ruler",
        "menu_bar" => "menu",
        "code" or "code_editor" => "code",
        "group" => "group-by-ref-type",
        "symbol" => "smiley",
        "status_bar" => "info",
        "tab_strip" => "files",
        "breadcrumbs" => "chevron-right",
        _ => "symbol-misc"
    };

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

        // Nothing types an F5, so it plays the entrances from a text box as well. A file of code is run instead
        shortcuts.Add(Key.F5, RunOrPlay, whileTyping: true);

        // F7 flips a layout between its designer and its code, the way Visual Studio's designers go
        shortcuts.Add(Key.F7, ToggleView, whileTyping: true);

        // The window, VS Code's keys for the side bar, the panel and the command palette
        shortcuts.Add(Key.B, ToggleSideBar, control: true, whileTyping: true);
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
        edit.Add("Find next", () => FindAgain(forward: true), "F3");
        edit.Add("Find the one before", () => FindAgain(forward: false), "Shift+F3");
        edit.Add("Replace...", ReplaceInFile, "Ctrl+H", "replace").IsVisible = code;
        edit.AddSeparator();
        edit.Add("Find in files...", FindInFiles, "Ctrl+Shift+F", "search");
        edit.AddSeparator();
        edit.Add("Comment lines in and out", () => this.code.Document.ToggleComment(), "Ctrl+/", "comment").IsVisible = code;

        Menu selection = menu.AddMenu("Selection");
        selection.IsVisible = code;
        selection.Add("Select all", () => this.code.Document.SelectAll(), "Ctrl+A");
        selection.Add("Select the line", SelectCaretLine);
        selection.Add("Select the word", () => this.code.Document.SelectWordAt(this.code.Document.Caret));
        selection.AddSeparator();
        selection.Add("Move lines up", () => this.code.Document.MoveLines(-1), "Alt+Up", "arrow-up");
        selection.Add("Move lines down", () => this.code.Document.MoveLines(1), "Alt+Down", "arrow-down");
        selection.Add("Duplicate the line", () => this.code.Document.Duplicate(), "Ctrl+D", "copy");

        Menu view = menu.AddMenu("View");
        view.Add("Command palette...", CommandPalette, "Ctrl+Shift+P", "symbol-misc");
        view.AddSeparator();
        view.Add("Explorer", ShowExplorer, "Ctrl+Shift+E", "files");
        view.Add("Search", () => ShowSide(HexSide.Search), icon: "search");
        view.Add("Outline", ShowOutline, "Ctrl+Shift+O", "list-tree").IsVisible = code;
        view.Add("Toolbox, components and layers", ShowOutline, "Ctrl+Shift+O", "layout").IsVisible = design;
        view.Add("Problems", () => ShowPanel(PROBLEMS_PAGE), icon: "error");
        view.Add("Output", () => ShowPanel(OUTPUT_PAGE), icon: "output");
        view.AddSeparator();
        view.Add("Side bar", ToggleSideBar, "Ctrl+B", "layout-sidebar-left").IsChecked = () => left.Visible;
        view.Add("Panel", TogglePanel, "Ctrl+J", "layout-panel").IsChecked = () => bottom.Visible;
        view.Add("Properties", ToggleProperties, icon: "layout-sidebar-right").IsChecked = () => right.Visible;
        view.AddSeparator();
        view.Add("Designer or code", ToggleView, "F7", "layout").IsEnabled = () => !document.IsText;
        view.Add("Zoom in", () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), "Ctrl+=", "zoom-in").IsVisible = design;
        view.Add("Zoom out", () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), "Ctrl+-", "zoom-out").IsVisible = design;
        view.Add("Zoom to fit", ZoomToFit, "Ctrl+0", "screen-full").IsVisible = design;
        view.Add("Code bigger", () => ZoomCode(CODE_ZOOM_STEP), "Ctrl+=", "zoom-in").IsVisible = code;
        view.Add("Code smaller", () => ZoomCode(1.0f / CODE_ZOOM_STEP), "Ctrl+-", "zoom-out").IsVisible = code;
        view.Add("Code at the size it comes", () => ZoomCode(0.0f), "Ctrl+0").IsVisible = code;
        view.Add("Actual size", ZoomToActualSize, "Ctrl+1").IsVisible = design;
        view.AddSeparator();
        layoutDebugger.AddTo(view, () => Say(layoutDebugger.IsOn ? "layout debugger on, padding is green, gaps are orange" : "layout debugger off"));
        Menu size = view.AddMenu("How big Hex is", "zoom-in");
        foreach (float scale in Scales)
            size.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;

        // The skin the layouts are shown in, every one there is under Assets/uix and every theme of each. It is the
        // game's setting (UISkins) and not the editor's own, a layout looks here the way it will in a game set to
        // the same, and the editor itself stays in its flat one
        AddSkins(view.AddMenu("Skin of the layouts", "paintcan"));

        Menu go = menu.AddMenu("Go");
        go.Add("Go to a file...", QuickOpen, "Ctrl+P", "go-to-file");
        go.Add("Go to a line...", GoToLine, "Ctrl+G").IsVisible = code;
        go.Add("Go to something in the file...", GoToSymbol, icon: "symbol-variable").IsVisible = code;
        go.AddSeparator();
        go.Add("Go to where it is declared", GoToDefinition, "F12", "references").IsVisible = code;
        go.Add("Find everywhere it is", FindReferences, "Shift+F12", "references").IsVisible = code;
        go.AddSeparator();
        go.Add("The next problem", () => StepProblem(1), icon: "arrow-down");
        go.Add("The problem before", () => StepProblem(-1), icon: "arrow-up");

        Menu run = menu.AddMenu("Run");
        run.Add("Run the file", RunCode, "F5", "play").IsVisible = code;
        run.Add("Play the entrances", PlayIntros, "F5", "play").IsVisible = design;
        run.AddSeparator();
        run.Add("Check every file", CheckAllFiles, "F8", "check-all");
        run.Add("Complete the word", () => { this.code.Focus(); this.code.Complete(); }, "Ctrl+Space").IsVisible = code;
        run.AddSeparator();
        run.Add("Put the code into the layout", ApplyCode, "Ctrl+Enter").IsEnabled = () => document.CodeEdited;
        run.Add("Throw the typed code away", RevertCode, icon: "discard").IsEnabled = () => document.CodeEdited;
        run.Add("Make this file a layout (#define layout)...", MakeLayout, icon: "layout").IsEnabled = () => document.IsText && IsLayoutCode(this.code.Text);

        Menu built = menu.AddMenu("Layout");
        built.IsVisible = design;
        Menu add = built.AddMenu("Add", "add");
        foreach (string kind in Palette)
            add.Add(kind.Replace('_', ' '), () => AddComponentOf(kind), icon: KindIcon(kind));
        built.AddSeparator();
        built.Add("Group", GroupSelected, "Ctrl+G", "group-by-ref-type").IsEnabled = some;
        built.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);
        AddConvertMenu(built.AddMenu("Change into", "arrow-swap"));
        built.AddSeparator();
        AddMoveMenu(built.AddMenu("Move", "arrow-swap"), some);
        AddArrangeMenu(built.AddMenu("Arrange", "layers"), some);
        AddAlignMenu(built.AddMenu("Line up", "symbol-ruler"), some);
        built.AddSeparator();
        built.Add("Select all", SelectAll, "Ctrl+A");
        built.Add("Select nothing", () => Select(null), "Esc").IsEnabled = some;
        built.Add("Show every layer", ShowAllLayers, icon: "eye").IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        built.Add("Its code", ShowSelectedCode, icon: "code").IsEnabled = some;
        built.AddSeparator();
        FillScreenMenu(built.AddMenu("Screen", "device-desktop"));

        Menu help = menu.AddMenu("Help");
        help.Add("The keys...", ShowKeys, icon: "keyboard");
        help.Add("The language...", ShowLanguageHelp, icon: "book");
        help.Add("What changed...", ShowChangelog, icon: "history");
        help.Add("The editor's own UI", () => Open(EDITOR_LAYOUT), icon: "layout");
        help.AddSeparator();
        help.Add("About Hex...", ShowAbout, icon: "info");
    }

    /// <summary>The ways to move what is selected about the tree, for the Layout menu and the menu of a component.</summary>
    private void AddMoveMenu(Menu into, Func<bool> some)
    {
        into.Add("Up", () => MoveSelected(-1), "Ctrl+Up", "arrow-up").IsEnabled = some;
        into.Add("Down", () => MoveSelected(1), "Ctrl+Down", "arrow-down").IsEnabled = some;
        into.Add("Out of its container", () => MoveSelectedAcross(inwards: false), "Ctrl+Left", "arrow-left").IsEnabled = some;
        into.Add("Into the one before it", () => MoveSelectedAcross(inwards: true), "Ctrl+Right", "arrow-right").IsEnabled = some;
    }

    private void AddArrangeMenu(Menu into, Func<bool> some)
    {
        into.Add("Bring to front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = some;
        into.Add("Bring forward", () => ReorderSelected(HexDocument.Order.Forward), "]").IsEnabled = some;
        into.Add("Send backward", () => ReorderSelected(HexDocument.Order.Backward), "[").IsEnabled = some;
        into.Add("Send to back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = some;
    }

    /// <summary>Lining up is against each other with several things selected, against the container with one.</summary>
    private void AddAlignMenu(Menu into, Func<bool> some)
    {
        into.Add("Left edges", () => Align(Edge.Left)).IsEnabled = some;
        into.Add("Centres across", () => Align(Edge.Center)).IsEnabled = some;
        into.Add("Right edges", () => Align(Edge.Right)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Top edges", () => Align(Edge.Top)).IsEnabled = some;
        into.Add("Middles", () => Align(Edge.Middle)).IsEnabled = some;
        into.Add("Bottom edges", () => Align(Edge.Bottom)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Space out across", () => Distribute(horizontally: true)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
        into.Add("Space out up and down", () => Distribute(horizontally: false)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
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

        fileMenu.Add("New layout", () => Show(NewDocument()), "Ctrl+N", "new-file");
        fileMenu.Add("New file...", () => NewFileIn(workspace.IsEmpty ? browseDirectory : workspace.Folders[0]), icon: "file-add");
        fileMenu.Add("Open...", OpenPressed, "Ctrl+O", "folder-opened");

        Menu recentMenu = fileMenu.AddMenu("Open recent", "history");
        for (int i = 0; i < recent.Count; i++)
        {
            // Numbered, two of them can well have the same name in different folders
            string path = recent[i];
            recentMenu.Add($"{i + 1}  {Path.GetFileName(path)}", () => Open(path));
        }
        recentMenu.AddSeparator();
        recentMenu.Add("Forget these", ForgetRecent).IsEnabled = () => recent.Count > 0;

        fileMenu.AddSeparator();
        fileMenu.Add("Save", Save, "Ctrl+S", "save");
        fileMenu.Add("Save as...", SaveAs);
        fileMenu.Add("Save all", SaveAll, "Ctrl+Shift+S", "save-all");
        fileMenu.Add("Revert the file", RevertFile, icon: "discard").IsEnabled = () => document.Path is not null && (document.Modified || document.CodeEdited);
        fileMenu.AddSeparator();
        fileMenu.Add("Close", Close, "Ctrl+W", "close");
        fileMenu.Add("Close all", () => CloseAll(), icon: "close-all");
        fileMenu.AddSeparator();
        fileMenu.Add("Open folder...", () => AskFolder(OpenFolder), icon: "folder-opened");
        fileMenu.Add("Add folder to the workspace...", () => AskFolder(AddFolder), icon: "new-folder");
        fileMenu.Add("Open workspace...", OpenWorkspacePressed, icon: "folder-library");
        fileMenu.Add("Save workspace", () => SaveWorkspacePressed(asNew: false)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Save workspace as...", () => SaveWorkspacePressed(asNew: true)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Close workspace", CloseWorkspace).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.AddSeparator();
        fileMenu.Add("Settings...", OpenSettings, "Ctrl+,", "settings-gear");
        fileMenu.AddSeparator();
        // Through the window manager, on the window's thread, asking first like its x does
        fileMenu.Add("Exit", () => Engine.WindowManager.Close(), "Alt+F4", "close");
    }

    /* The command palette */

    /// <summary>
    /// Ctrl+Shift+P, every item of every menu there is right now in a list that narrows as it is typed into, with the
    /// menus it is in and its keys, the way VS Code's does.
    /// </summary>
    private void CommandPalette()
    {
        var commands = new List<(string Text, Action Picked)>();
        foreach (Menu bar in menu.Menus)
        {
            if (bar.IsVisible is { } shown && !shown())
                continue;

            foreach (var (item, path) in bar.Everything())
            {
                if ((item.IsVisible?.Invoke() ?? true) && (item.IsEnabled?.Invoke() ?? true))
                    commands.Add(($"{path}: {item.Label}{(item.Shortcut.Length > 0 ? $"   ({item.Shortcut})" : string.Empty)}", item.OnPicked!));
            }
        }

        ChooseFrom("Command palette", "Everything Hex can do right now, a few letters narrow it down", commands);
    }

    /// <summary>
    /// Helper method to ask for one of a list of things in a dialog (choose.hor), narrowed down by what is typed (the
    /// letters in order, the way quick open goes), enter, the first button or a double click picks.
    /// </summary>
    /// <param name="shortestFirst">Whether what is left is put shortest first, the nearest to what was typed for a file's name.</param>
    private void ChooseFrom(string title, string message, List<(string Text, Action Picked)> choices, bool shortestFirst = false)
    {
        string[] texts = [.. choices.Select(choice => choice.Text)];
        var byText = new Dictionary<string, Action>();
        foreach (var (text, picked) in choices)
            byText.TryAdd(text, picked);

        if (textDialog is { IsOpen: true })
            textDialog.Close();

        ListBox list = null!;
        void PickChosen()
        {
            if (list.Index >= 0 && list.Index < list.Items.Length && byText.TryGetValue(list.Items[list.Index], out Action? picked))
                picked();
        }

        UIDialog shown = textDialog = UIDialog.Show(chrome, title, message, new DialogChoice("Go", PickChosen), new DialogChoice("Cancel"));
        UILayout made = layout.Instantiate(CHOOSE_TEMPLATE, shown.Body);
        TextBox box = made.Get<TextBox>("answer");
        list = made.Get<ListBox>("choices");

        list.Items = texts;
        if (texts.Length > 0) list.Choose(0);

        box.OnChanged = typed =>
        {
            var left = texts.Where(text => Matches(text, typed));
            if (shortestFirst)
                left = left.OrderBy(text => Path.GetFileName(text).Length);

            list.Items = [.. left.Take(300)];
            if (list.Items.Length > 0) list.Choose(0);
        };
        box.OnSubmitted = _ =>
        {
            shown.Close();
            PickChosen();
        };
        list.OnActivated = _ =>
        {
            shown.Close();
            PickChosen();
        };

        box.Focus();
    }
}
