using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The menu bar and the toolbar. The bar's menus are declared here and filled by the parts of the editor, each adding its
// own commands and keys (UICommands), the window's own are here. Only what means something for what is open is on the
// bar, the Layout menu is the designer's. Every item of every menu in a list is CommandPalette, their keys put where
// somebody wants them is Keys.
internal sealed partial class HexScene
{
    private void WireToolbar()
    {
        layout.Get<Button>("play_button").OnPressed = RunOrPlay;
    }

    /// <summary>F5, the file run, or the entrances of a layout played in its designer. A file that isn't HIDL runs the run task.</summary>
    private void RunOrPlay()
    {
        // C# is debugged, and while something is being debugged F5 goes on from where it stopped
        if (debugger.DebugStopped || (!LayoutTools && HexDebugging.IsCSharp(document.Path))) debugger.DebugOrContinue();
        else if (LayoutTools) designer.PlayIntros();
        else if (document.IsText && document.Path is { } path && !path.EndsWith(".hor", StringComparison.OrdinalIgnoreCase) && tasks.DefaultTask("run") is { } task) tasks.Run(task);
        else running.RunCode();
    }

    /// <summary>
    /// The menus of the bar and their groups, before anything adds its commands to them. Everything the editor does that
    /// isn't done to something on screen directly is a command of one of these, added by the part that does it.
    /// </summary>
    private void DeclareMenus()
    {
        commands.Declare("File", "new", "save", "close", "path", "workspace", "settings", "exit");
        commands.Declare("Edit", "history", "clipboard", "find", "files", "comment");
        commands.Declare("Selection", "select", "lines", "carets", "folding");
        commands.Declare("View", "palette", "sides", "toggles", "zoom", "look");
        commands.Declare("Go", "places", "open", "symbols", "problems");
        commands.Declare("Run", "debug", "run", "tasks", "check", "apply");
        commands.Declare("Layout", "add", "group", "move", "select", "screen");
        commands.Declare("Help", "help", "about");
    }

    /// <summary>
    /// The commands of the window itself, the palette, the sides and the panel, what's shown and how big, and leaving.
    /// The ones with control work whatever is going on, the others keep out of the way while something is being typed.
    /// </summary>
    private void AddWindowCommands()
    {
        commands.Add("View/palette", "Command HexDesigner.Palette...", CommandPalette, "Ctrl+Shift+P", "symbol-misc", UIKeyUse.BoundWhileTyping);
        commands.Bind(Key.F1, CommandPalette, whileTyping: true);
        commands.Add("View/palette", "Dusk, the Terminal", SwapMode, "Ctrl+Alt+D", "terminal", UIKeyUse.BoundWhileTyping);

        // VS Code's keys for the side bar, the panel and the command palette
        commands.Add("View/sides", "Explorer", ShowExplorer, "Ctrl+Shift+E", "files", UIKeyUse.BoundWhileTyping);
        commands.Add("View/sides", "Search", () => ShowSide(HexSide.Search), icon: "search");
        commands.Add("View/sides", "Debug", () => ShowSide(HexSide.Debug), icon: "debug-alt");
        commands.Add("View/sides", "Outline", ShowOutline, "Ctrl+Shift+O", "list-tree", UIKeyUse.BoundWhileTyping).IsVisible = () => !LayoutTools;
        commands.Add("View/sides", "Toolbox", ShowOutline, "Ctrl+Shift+O", "layout").IsVisible = () => LayoutTools;
        commands.Add("View/sides", "Problems", () => ShowPanel(PROBLEMS_PAGE), icon: "error");
        commands.Add("View/sides", "Output", () => ShowPanel(OUTPUT_PAGE), icon: "output");

        commands.Add("View/toggles", "Side Bar", ToggleSideBar, "Ctrl+B", "layout-sidebar-left", UIKeyUse.BoundWhileTyping).IsChecked = () => left.Shown;
        commands.Add("View/toggles", "Panel", TogglePanel, "Ctrl+J", "layout-panel", UIKeyUse.BoundWhileTyping).IsChecked = () => bottom.Shown;
        commands.Add("View/toggles", "Properties", ToggleProperties, "Ctrl+Alt+B", "layout-sidebar-right", UIKeyUse.BoundWhileTyping).IsChecked = () => right.Shown;
        commands.Add("View/toggles", "Zen Mode", ToggleQuiet, "F11", "screen-full").IsChecked = () => Quiet;
        commands.Add("View/toggles", "Word Wrap", ToggleWrap, "Alt+Z", "word-wrap", UIKeyUse.BoundWhileTyping).IsChecked = () => settings.WrapCode;
        commands.Add("View/toggles", "Code in Hack", codeEditing.ToggleCodeFont, string.Empty, "text-size").IsChecked = () => settings.CodeFont.Length > 0;
        commands.Add("View/toggles", "Split Editor", ToggleSplit, "Ctrl+\\", "split-horizontal", UIKeyUse.BoundWhileTyping).IsChecked = () => Split;

        // F7 flips a layout between its designer and its code, the way Visual Studio's designers go
        commands.Add("View/zoom", "Toggle Designer and Code", ToggleView, "F7", "layout", UIKeyUse.BoundWhileTyping).IsEnabled = () => !document.IsText;

        layoutDebugger.AddTo(commands, "View/look", () => Say(layoutDebugger.IsOn ? "layout debugger on, padding is green, gaps are orange" : "layout debugger off"));
        Menu size = commands.AddMenu("View/look", "Editor Scale", "zoom-in");
        foreach (float scale in Scales)
            size.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;

        // The skin the layouts are shown in, every one there is under Assets/uix and every theme of each. It is the
        // game's setting (UISkins) and not the editor's own, a layout looks here the way it will in a game set to
        // the same, and the editor itself stays in its flat one
        AddSkins(commands.AddMenu("View/look", "Layout Skin", "paintcan"));

        // The canvas in the designer, the code in code, each with a zoom of its own. These go while typing, the code is typed into
        commands.Bind(Key.Number0, () => { if (LayoutTools) designer.ZoomToFit(); else ZoomCode(0.0f); }, control: true, whileTyping: true);
        commands.Bind(Key.Equal, () => { if (LayoutTools) designer.ZoomCanvasIn(); else ZoomCode(CODE_ZOOM_STEP); }, control: true, whileTyping: true);
        commands.Bind(Key.Minus, () => { if (LayoutTools) designer.ZoomCanvasOut(); else ZoomCode(1.0f / CODE_ZOOM_STEP); }, control: true, whileTyping: true);

        // Nothing types an F5, so it plays the entrances from a text box as well, see RunOrPlay
        commands.Bind(Key.F5, RunOrPlay, whileTyping: true, shift: false);

        // Through the window manager, on the window's thread, asking first like its x does
        commands.Add("File/exit", "Exit", () => Engine.WindowManager.Close(), "Alt+F4", "close");
    }
}
