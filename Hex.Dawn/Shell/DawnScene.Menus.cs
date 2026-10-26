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
internal sealed partial class DawnScene
{
    private void WireToolbar()
    {
        layout.Get<Button>("play_button").OnPressed = RunOrPlay;
        WireExplorerMenu();
    }

    /// <summary>
    /// What the explorer's menu does with the rest of the editor, Open in Terminal a terminal tab in the folder and Find in
    /// Folder the search view looking only in it.
    /// </summary>
    private void WireExplorerMenu()
    {
        explorer.OpenTerminalIn = folder =>
        {
            ShowPanel(CONSOLE_PAGE);
            terminalPanel.NewTab(terminals.UsualProfile, folder);
        };

        explorer.FindInFolder = (_, scope) =>
        {
            FindInFiles();
            if (searchView is null)
                return;

            ShowSearchScope(true);
            searchInclude.Text = scope;
            if (searchQuery.Text.Length > 0)
                SearchNow();
        };
    }

    /// <summary>
    /// F5, the file run, or the entrances of a layout played in its designer. A file that isn't HIDL runs the run task,
    /// and with no run task Set Up F5 comes up for it. It was run as HIDL, which no D or Python ever was.
    /// </summary>
    private void RunOrPlay()
    {
        // C# and D are debugged when there's a debugger to do it with (netcoredbg, lldb-dap), breakpoints or not, the way F5
        // is in VS Code, and Ctrl+F5 runs without it. While something is being debugged F5 goes on from where it stopped.
        // D was only debugged with a breakpoint and C# always, F5 meant two things. Without netcoredbg F5 builds and runs
        // C# the way any other program is, and says once that it isn't being debugged
        bool debuggable = debugger.CanDebug(document.Path);
        if (!LayoutTools && HexDebugging.IsCSharp(document.Path) && !debuggable && !debugger.DebugStopped)
            debugger.SayRunWithoutDebugger();

        if (debugger.DebugStopped || (!LayoutTools && debuggable)) debugger.DebugOrContinue();
        else if (LayoutTools) designer.PlayIntros();
        else if (document.IsText && document.Path is { } path && !IsHidl(path))
        {
            if (tasks.RunTaskFor(path) is { } task) tasks.Run(task);
            else tasks.SetUpRun(path);
        }
        else running.RunCode();
    }

    /// <summary>
    /// Ctrl+F5, the file run without the debugger, its run task in the terminal (Set Up F5 when there isn't one), VS Code's
    /// key for it. A HIDL file has its behaviour run, the runner, as Ctrl+F5 always did.
    /// </summary>
    private void RunWithoutDebugging()
    {
        if (LayoutTools)
            return;

        if (document.IsText && document.Path is { } path && !IsHidl(path))
        {
            if (tasks.RunTaskFor(path) is { } task) tasks.Run(task);
            else tasks.SetUpRun(path);
        }
        else
        {
            running.OpenRunner();
        }
    }

    // Whether a file is HIDL, which F5 runs itself, by its extension
    private static bool IsHidl(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hor" or ".jab" or ".hex-workspace";

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
        commands.Declare("Git", "changes", "branches", "stash", "sync", "history");
        commands.Declare("Layout", "add", "group", "move", "select", "screen");
        commands.Declare("Help", "help", "about");
    }

    /// <summary>
    /// The commands of the window itself, the palette, the sides and the panel, what's shown and how big, and leaving.
    /// The ones with control work whatever is going on, the others keep out of the way while something is being typed.
    /// </summary>
    private void AddWindowCommands()
    {
        commands.Add("View/palette", Said("menus.command_palette"), CommandPalette, "Ctrl+Shift+P", "symbol-misc", UIKeyUse.BoundWhileTyping);
        commands.Bind(Key.F1, CommandPalette, whileTyping: true);
        commands.Add("View/palette", Said("menus.dusk_terminal"), SwapMode, "Ctrl+Alt+D", "terminal", UIKeyUse.BoundWhileTyping);

        // VS Code's keys for the side bar, the panel and the command palette
        commands.Add("View/sides", Said("menus.explorer"), explorer.ShowExplorer, "Ctrl+Shift+E", "files", UIKeyUse.BoundWhileTyping);
        commands.Add("View/sides", Said("menus.search"), () => ShowSide(HexSide.Search), icon: "search");
        commands.Add("View/sides", Said("menus.debug"), () => ShowSide(HexSide.Debug), icon: "debug-alt");
        commands.Add("View/sides", Said("menus.outline"), explorer.ShowOutline, "Ctrl+Shift+O", "list-tree", UIKeyUse.BoundWhileTyping).IsVisible = () => !LayoutTools;
        commands.Add("View/sides", Said("menus.toolbox"), explorer.ShowOutline, "Ctrl+Shift+O", "layout").IsVisible = () => LayoutTools;
        commands.Add("View/sides", Said("menus.problems"), () => ShowPanel(PROBLEMS_PAGE), icon: "error");
        commands.Add("View/sides", Said("menus.output"), () => ShowPanel(OUTPUT_PAGE), icon: "output");

        commands.Add("View/toggles", Said("menus.side_bar"), ToggleSideBar, "Ctrl+B", "layout-sidebar-left", UIKeyUse.BoundWhileTyping).IsChecked = () => left.Shown;
        commands.Add("View/toggles", Said("menus.panel"), TogglePanel, "Ctrl+J", "layout-panel", UIKeyUse.BoundWhileTyping).IsChecked = () => bottom.Shown;
        commands.Add("View/toggles", Said("menus.properties"), ToggleProperties, "Ctrl+Alt+B", "layout-sidebar-right", UIKeyUse.BoundWhileTyping).IsChecked = () => right.Shown;
        // VS Code's chord for Zen Mode, F11 was Step Into's too and the keys window had the two in conflict out of the box.
        // F11 still goes quiet when nothing is being debugged, see BuildQuiet
        commands.Add("View/toggles", Said("menus.zen_mode"), ToggleQuiet, "Ctrl+K Z", "screen-full", UIKeyUse.BoundWhileTyping).IsChecked = () => Quiet;
        commands.Add("View/toggles", Said("menus.word_wrap"), ToggleWrap, "Alt+Z", "word-wrap", UIKeyUse.BoundWhileTyping).IsChecked = () => settings.WrapCode;
        commands.Add("View/toggles", Said("menus.code_in_hack"), codeEditing.ToggleCodeFont, string.Empty, "text-size").IsChecked = () => settings.CodeFont.Length > 0;
        commands.Add("View/toggles", Said("menus.split_editor"), ToggleSplit, "Ctrl+\\", "split-horizontal", UIKeyUse.BoundWhileTyping).IsChecked = () => Split;

        // F7 flips a layout between its designer and its code, the way Visual Studio's designers go
        commands.Add("View/zoom", Said("menus.toggle_designer_code"), ToggleView, "F7", "layout", UIKeyUse.BoundWhileTyping).IsEnabled = () => !document.IsText;

        layoutDebugger.AddTo(commands, "View/look", () => Say(layoutDebugger.IsOn ? Said("menus.layout_debugger_on") : Said("menus.layout_debugger_off")));
        Menu size = commands.AddMenu("View/look", Said("menus.editor_scale"), "zoom-in");
        foreach (float scale in Scales)
            size.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;

        // The skin the layouts are shown in, every one there is under Assets/uix and every theme of each. It is the
        // game's setting (UISkins) and not the editor's own, a layout looks here the way it will in a game set to
        // the same, and the editor itself stays in its flat one
        AddSkins(commands.AddMenu("View/look", Said("menus.layout_skin"), "paintcan"));
        AddLooks(commands.AddMenu("View/look", Said("looks.menu"), "color-mode"));

        // The canvas in the designer, the code in code, each with a zoom of its own. These go while typing, the code is typed into
        commands.Bind(Key.Number0, () => { if (LayoutTools) designer.ZoomToFit(); else ZoomCode(0.0f); }, control: true, whileTyping: true);
        commands.Bind(Key.Equal, () => { if (LayoutTools) designer.ZoomCanvasIn(); else ZoomCode(CODE_ZOOM_STEP); }, control: true, whileTyping: true);
        commands.Bind(Key.Minus, () => { if (LayoutTools) designer.ZoomCanvasOut(); else ZoomCode(1.0f / CODE_ZOOM_STEP); }, control: true, whileTyping: true);

        // Nothing types an F5, so it plays the entrances from a text box as well, see RunOrPlay
        commands.Bind(Key.F5, RunOrPlay, whileTyping: true, shift: false);

        // Through the window manager, on the window's thread, asking first like its x does
        commands.Add("File/exit", Said("menus.exit"), () => Engine.WindowManager.Close(), "Alt+F4", "close");
    }
}
