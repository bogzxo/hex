using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// The palette and the menu bar, which is everything there is to do that isn't done to something on screen directly.
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
        _ => string.Empty
    };

    /* The menu bar: everything there is to do that isn't done to something on screen directly */

    /// <summary>
    /// Helper method to set up the keys of the editor. The ones with control work whatever is going on,
    /// the others keep out of the way while something is being typed.
    /// </summary>
    private void BuildShortcuts()
    {
        shortcuts.Add(Key.Z, Undo, control: true, whileTyping: true);
        shortcuts.Add(Key.Y, Redo, control: true, whileTyping: true);
        shortcuts.Add(Key.S, Save, control: true, whileTyping: true);
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
        shortcuts.Add(Key.F5, () => { if (LayoutTools) PlayIntros(); else RunCode(); }, whileTyping: true);

        // F7 flips a layout between its designer and its code, the way Visual Studio's designers go
        shortcuts.Add(Key.F7, ToggleView, whileTyping: true);

        // The code, in any file. These go while typing, the editor of code is where they are wanted most
        shortcuts.Add(Key.Enter, ApplyCode, control: true, whileTyping: true);
        shortcuts.Add(Key.F, FindInFile, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.F, FindInFiles, control: true, whileTyping: true, shift: true);
        shortcuts.Add(Key.H, ReplaceInFile, control: true, whileTyping: true);
        // Ctrl+G groups what is selected in a layout, it only goes to a line where there is code to go about in
        shortcuts.Add(Key.G, () => { if (!LayoutTools || compositor.Focus is CodeEditor) GoToLine(); }, control: true, whileTyping: true, shift: false);
        shortcuts.Add(Key.P, QuickOpen, control: true, whileTyping: true);
        shortcuts.Add(Key.F3, () => FindAgain(forward: true), whileTyping: true, shift: false);
        shortcuts.Add(Key.F3, () => FindAgain(forward: false), whileTyping: true, shift: true);
        shortcuts.Add(Key.F8, CheckAllFiles, whileTyping: true);
        shortcuts.Add(Key.F12, GoToDefinition, whileTyping: true, shift: false);
        shortcuts.Add(Key.F12, FindReferences, whileTyping: true, shift: true);
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
        fileMenu = menu.AddMenu("File");
        RebuildFileMenu();

        Menu edit = menu.AddMenu("Edit");
        edit.Add("Undo", Undo, "Ctrl+Z").IsEnabled = () => document.CanUndo || recordPending;
        edit.Add("Redo", Redo, "Ctrl+Y").IsEnabled = () => document.CanRedo;
        edit.AddSeparator();
        edit.Add("Cut", CutSelected, "Ctrl+X").IsEnabled = () => document.Selected is not null;
        edit.Add("Copy", CopySelected, "Ctrl+C").IsEnabled = () => document.Selected is not null;
        edit.Add("Paste", Paste, "Ctrl+V").IsEnabled = () => clipboard.Length > 0;
        edit.Add("Duplicate", DuplicateSelected, "Ctrl+D").IsEnabled = () => document.Selected is not null;
        edit.Add("Delete", DeleteAll, "Del").IsEnabled = () => document.Selected is not null;
        edit.AddSeparator();

        // Where it is in the tree, which in a stack is where it is laid out
        edit.Add("Move up", () => MoveSelected(-1), "Ctrl+Up").IsEnabled = () => document.Selected is not null;
        edit.Add("Move down", () => MoveSelected(1), "Ctrl+Down").IsEnabled = () => document.Selected is not null;
        edit.Add("Move out", () => MoveSelectedAcross(inwards: false), "Ctrl+Left").IsEnabled = () => document.Selected is not null;
        edit.Add("Move into", () => MoveSelectedAcross(inwards: true), "Ctrl+Right").IsEnabled = () => document.Selected is not null;
        edit.AddSeparator();
        edit.Add("Group", GroupSelected, "Ctrl+G").IsEnabled = () => document.Selected is not null;
        edit.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);
        edit.AddSeparator();
        edit.Add("Bring to front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = () => document.Selected is not null;
        edit.Add("Send to back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = () => document.Selected is not null;
        edit.AddSeparator();
        edit.Add("Select all", SelectAll, "Ctrl+A");
        edit.Add("Select nothing", () => Select(null), "Esc").IsEnabled = () => document.Selected is not null;

        // Lining up is against each other with several things selected, against the container with one
        // The code of whatever is open, a layout's or a file's
        Menu codeMenu = menu.AddMenu("Code");
        codeMenu.Add("Find...", FindInFile, "Ctrl+F");
        codeMenu.Add("Find next", () => FindAgain(forward: true), "F3");
        codeMenu.Add("Find the one before", () => FindAgain(forward: false), "Shift+F3");
        codeMenu.Add("Replace...", ReplaceInFile, "Ctrl+H");
        codeMenu.Add("Go to line...", GoToLine, "Ctrl+G");
        codeMenu.AddSeparator();
        codeMenu.Add("Put the code into the layout", ApplyCode, "Ctrl+Enter").IsEnabled = () => document.CodeEdited;
        codeMenu.Add("Throw the typed code away", RevertCode).IsEnabled = () => document.CodeEdited;
        codeMenu.Add("Run the file", RunCode, "F5").IsEnabled = () => !LayoutTools;
        codeMenu.Add("Designer or code", ToggleView, "F7").IsEnabled = () => !document.IsText;
        codeMenu.Add("Make this file a layout (#define layout)", MakeLayout).IsEnabled = () => document.IsText && IsLayoutCode(code.Text);
        codeMenu.AddSeparator();
        codeMenu.Add("Open a file of the workspace...", QuickOpen, "Ctrl+P");
        codeMenu.Add("Find in files...", FindInFiles, "Ctrl+Shift+F");
        codeMenu.Add("Check every file", CheckAllFiles, "F8");
        codeMenu.Add("Put the list of what was found away", CloseResults).IsEnabled = () => results is not null;
        codeMenu.AddSeparator();
        codeMenu.Add("Go to where it is declared", GoToDefinition, "F12");
        codeMenu.Add("Find everywhere it is", FindReferences, "Shift+F12");
        codeMenu.Add("Complete the word", () => { code.Focus(); code.Complete(); }, "Ctrl+Space");
        codeMenu.AddSeparator();
        codeMenu.Add("Comment lines in and out", () => code.Document.ToggleComment(), "Ctrl+/");
        codeMenu.Add("Duplicate the line", () => code.Document.Duplicate(), "Ctrl+D");
        codeMenu.Add("Move lines up", () => code.Document.MoveLines(-1), "Alt+Up");
        codeMenu.Add("Move lines down", () => code.Document.MoveLines(1), "Alt+Down");

        Menu arrange = menu.AddMenu("Arrange");
        Func<bool> some = () => document.Selected is not null;
        arrange.Add("Left edges", () => Align(Edge.Left)).IsEnabled = some;
        arrange.Add("Centres across", () => Align(Edge.Center)).IsEnabled = some;
        arrange.Add("Right edges", () => Align(Edge.Right)).IsEnabled = some;
        arrange.AddSeparator();
        arrange.Add("Top edges", () => Align(Edge.Top)).IsEnabled = some;
        arrange.Add("Middles", () => Align(Edge.Middle)).IsEnabled = some;
        arrange.Add("Bottom edges", () => Align(Edge.Bottom)).IsEnabled = some;
        arrange.AddSeparator();
        arrange.Add("Space out across", () => Distribute(horizontally: true)).IsEnabled = () => document.Selection.Count >= 3;
        arrange.Add("Space out up and down", () => Distribute(horizontally: false)).IsEnabled = () => document.Selection.Count >= 3;

        Menu view = menu.AddMenu("View");
        view.Add("Play intros", PlayIntros, "F5");
        view.AddSeparator();
        view.Add("Zoom in", () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), "Ctrl+=");
        view.Add("Zoom out", () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), "Ctrl+-");
        view.Add("Zoom to fit", ZoomToFit, "Ctrl+0");
        view.Add("Actual size", ZoomToActualSize, "Ctrl+1");
        view.AddSeparator();
        layoutDebugger.AddTo(view, () => Say(layoutDebugger.IsOn ? "layout debugger on: padding is green, gaps are orange" : "layout debugger off"));
        view.AddSeparator();
        view.Add("Show all layers", ShowAllLayers).IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        view.AddSeparator();

        // The sizes it can be are the ones the editor knows how to lay itself out at
        foreach (float scale in Scales)
            view.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;

        // The skin the layouts are shown in, every one there is under Assets/uix and every theme of each. It is the
        // game's setting (UISkins) and not the editor's own, a layout looks here the way it will in a game set to
        // the same, and the editor itself stays in its flat one
        Menu skins = menu.AddMenu("Skin");
        foreach (UISkinChoice skin in UISkins.Available)
        {
            if (skin.Themes.Count == 0)
            {
                skins.Add(skin.Title, () => UseSkin(skin, null)).IsChecked = () => UISkins.Current == skin;
                continue;
            }

            foreach (string theme in skin.Themes)
            {
                skins.Add($"{skin.Title}, {theme}", () => UseSkin(skin, theme)).IsChecked =
                    () => UISkins.Current == skin && (UISkins.Theme ?? skin.UsualTheme) == theme;
            }

            skins.AddSeparator();
        }
    }

    /// <summary>Helper method to show the layouts in another skin, the one a game would be set to.</summary>
    private void UseSkin(UISkinChoice skin, string? theme)
    {
        UISkins.Use(skin, theme);
        Say(theme is null ? $"layouts are shown in {skin.Title}" : $"layouts are shown in {skin.Title}, {theme}");
    }

    /// <summary>
    /// Puts the file menu together. What it always has, and under that the layouts that were worked on last.
    /// Again whenever those change.
    /// </summary>
    private void RebuildFileMenu()
    {
        fileMenu.Clear();

        fileMenu.Add("New", () => Show(NewDocument()), "Ctrl+N");
        fileMenu.Add("Open...", OpenPressed, "Ctrl+O");
        fileMenu.AddSeparator();
        fileMenu.Add("Save", Save, "Ctrl+S");
        fileMenu.Add("Save as...", SaveAs);
        fileMenu.Add("Close", Close, "Ctrl+W");
        fileMenu.AddSeparator();
        fileMenu.Add("Settings...", OpenSettings, "Ctrl+,");
        fileMenu.AddSeparator();
        fileMenu.Add("Open folder...", () => AskFolder(OpenFolder));
        fileMenu.Add("Add folder to the workspace...", () => AskFolder(AddFolder));
        fileMenu.Add("Open workspace...", OpenWorkspacePressed);
        fileMenu.Add("Save workspace", () => SaveWorkspacePressed(asNew: false)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Save workspace as...", () => SaveWorkspacePressed(asNew: true)).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Close workspace", CloseWorkspace).IsEnabled = () => !workspace.IsEmpty;
        fileMenu.Add("Explorer", ShowExplorer, "Ctrl+Shift+E");
        fileMenu.Add("Outline, or the layout's components", ShowOutline, "Ctrl+Shift+O");

        if (recent.Count > 0)
        {
            fileMenu.AddSeparator();

            for (int i = 0; i < recent.Count; i++)
            {
                // Numbered, two of them can well have the same name in different folders
                string path = recent[i];
                fileMenu.Add($"{i + 1}  {Path.GetFileName(path)}", () => Open(path));
            }

            fileMenu.Add("Forget these", ForgetRecent);
        }

        fileMenu.AddSeparator();
        fileMenu.Add("The editor's own UI", () => Open(EDITOR_LAYOUT));
    }
}
