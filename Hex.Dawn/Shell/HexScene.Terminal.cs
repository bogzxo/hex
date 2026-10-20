using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The terminal page of the panel under the editor, Dusk's terminal in Hex. It is the engine's TerminalPanel on the
// engine's TerminalHost, the same host Dusk is a window round, so the plugins, the keys, the settings and the history are
// Dusk's (the user's folder, %AppData%\Dusk) and a command written for one is in the other's palette too. The console
// is a profile of it (HexScene.Console). The first tab opens the first time the page is looked at, in the workspace's
// folder or the file's (start_in in settings.hor). While a terminal has the keyboard Hex's own keys stay out of the
// shell's way, the few that have to work from a terminal (the panel away, F6, Ctrl+`) are terminal commands of Hex's.
internal sealed partial class HexScene : ITerminalHostUi
{
    private TerminalPanel terminalPanel = null!;
    private TerminalHost terminals = null!;

    // Hex's own commands on the host, owned by this so a plugin can't take them away
    private static readonly object HexOwns = new();

    private void BuildTerminal()
    {
        terminalPanel = layout.Get<TerminalPanel>("terminal");
        terminals = new TerminalHost(terminalPanel, new TerminalHostOptions
        {
            Name = "hex",
            Version = HexVersion,
            ObjectNames = ["terminal"],
            PluginFolders = [(TerminalHost.EnginePlugins, true), (Path.Combine(TerminalFiles.UserFolder, TerminalFiles.PLUGINS), false)],
            KeysFiles = [TerminalHost.EngineKeys, TerminalFiles.Keys()],
            SettingsFile = TerminalFiles.Settings(),
            HistoryFile = TerminalFiles.History(),
            Say = (message, error) => Say(message, error),
            Notify = (message, error) => Notify(message, error),
            Ui = this,
            Api = HexTerminalApi,
            ApiFunctions = HexApiFunctions,
            ApiFile = Path.Combine(TerminalFiles.UserFolder, TerminalApiDescription.FILE),
            ProgramFor = profile => profile.Id == CONSOLE_PROFILE ? MakeConsole() : null,

            // What a compiler in a terminal says is wrong goes in the Problems panel, until a line is run again
            Problem = (problem, _) => AddTerminalProblem(problem.File, problem.Line, Math.Max(1, problem.Column), problem.Message, problem.Warning),
            ProblemsCleared = _ => ClearTerminalProblems()
        });

        terminalPanel.OnTabMenu = (tab, point) =>
        {
            var shown = new Menu("terminal tab");
            shown.Add("Close", () => terminalPanel.CloseTab(tab), terminals.Commands.KeyOf("close_tab"), icon: "close");
            shown.Add("Rename", () => terminals.AskRename(tab), icon: "edit");
            shown.Add("Split right", () => terminals.Run("split_right"), terminals.Commands.KeyOf("split_right"), icon: "split-horizontal");
            ContextMenu.Show(chrome, point, shown);
        };

        terminals.Blocked = () => chrome.Dialog is not null;
        terminals.Start();

        // The console after the machine's shells, a new terminal is a shell and the console is asked for by name
        terminalPanel.Profiles.Add(new TerminalProfile("HIDL in this file", string.Empty, "symbol-namespace", CONSOLE_PROFILE) { Kind = TerminalProfileKind.Embedded });
        terminals.Watch();

        // The keys of Hex's that have to work while a terminal has the keyboard, as commands of the terminal so the shell
        // never sees them. A keys.hor can move them like any other
        terminals.Commands.Register("hex_panel", "Hex: the panel away", TogglePanel, "Ctrl+J", HexOwns);
        terminals.Commands.Register("hex_terminal", "Hex: the terminal page and back", ToggleTerminalPage, "F6", HexOwns);
        terminals.Commands.Register("hex_terminal_focus", "Hex: the terminal or the editor", ToggleTerminalFocus, "Ctrl+`", HexOwns);
        terminals.Commands.Register("hex_new_terminal", "Hex: a new terminal", NewTerminal, "Ctrl+Shift+`", HexOwns);
        terminals.Commands.Register("hex_run", "Hex: run the file", RunOrPlay, "F5", HexOwns);
        terminals.Commands.Register("hex_run_to_terminal", "Hex: F5 says what it ran in the terminal, or in the output", () =>
        {
            settings.RunToTerminal = !settings.RunToTerminal;
            SaveSettings();
            Say(settings.RunToTerminal ? "F5 says what a file did in the terminal" : "F5 says what a file did in the output");
        }, string.Empty, HexOwns);

        // And the same keys from the editor
        commands.Bind(Key.F6, ToggleTerminalPage, whileTyping: true);
        commands.Bind(Key.GraveAccent, ToggleTerminalFocus, control: true, whileTyping: true, shift: false);
        commands.Bind(Key.GraveAccent, NewTerminal, control: true, whileTyping: true, shift: true);
    }

    /// <summary>F6, the terminal page brought up with the keyboard in it, or the editor back when it is up.</summary>
    private void ToggleTerminalPage()
    {
        if (bottom.Shown && bottom.Selected == CONSOLE_PAGE && compositor.Focus is TerminalView)
        {
            FocusEditor();
            return;
        }

        ShowPanel(CONSOLE_PAGE);
        FocusTerminal();
    }

    /// <summary>Ctrl+`, the panel up on the terminal with the keyboard in it, or away when a terminal has the keyboard.</summary>
    private void ToggleTerminalFocus()
    {
        if (compositor.Focus is TerminalView && bottom.Shown)
        {
            TogglePanel();
            FocusEditor();
            return;
        }

        ShowPanel(CONSOLE_PAGE);
        FocusTerminal();
    }

    /// <summary>Ctrl+Shift+`, another terminal in the page.</summary>
    private void NewTerminal()
    {
        ShowPanel(CONSOLE_PAGE);
        terminalPanel.NewTab(terminals.UsualProfile, TerminalFolder());
    }

    private void FocusTerminal()
    {
        if (terminalPanel.Tabs.Count == 0)
            terminalPanel.NewTab(terminals.UsualProfile, TerminalFolder());
        else if (terminalPanel.FocusedView is { } view)
            terminalPanel.FocusPane(view);
    }

    private void FocusEditor()
    {
        if (!LayoutTools)
            code.Focus();
        else
            compositor.Focus = null;
    }

    /// <summary>Where a new terminal starts, the workspace's folder or the file's, as the settings say.</summary>
    private string TerminalFolder()
    {
        string? file = document.Path is { } path ? Path.GetDirectoryName(path) : null;
        string? root = workspace.Folders.FirstOrDefault();
        return terminals.Settings.StartIn == "file" ? file ?? root ?? browseDirectory : root ?? file ?? browseDirectory;
    }

    // Whether the font has been built, which wants a tick with the device up, and the skin the palette was taken from
    private bool terminalFontSet;
    private UISkin? terminalColouredFor;

    /// <summary>Every tick. The host, the font once, the colours after a change of skin, the first tab when the page is looked at.</summary>
    private void UpdateTerminal(float dt)
    {
        terminals.Update(dt);

        if (!terminalFontSet && Horizon.Graphics.GraphicsDevice.IsAvailable)
        {
            terminalFontSet = true;
            terminalPanel.Font = UIFont.Shared(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", HACK), HACK + ".ttf", CODE_FONT_EM, terminal: true);
        }

        if (compositor.Skin is { } skin && !ReferenceEquals(skin, terminalColouredFor))
        {
            terminalColouredFor = skin;
            terminalPanel.Palette = TerminalPalette.FromPalette(skin.Palette);
            terminalPanel.ApplyPalette();
        }

        // The page looked at with nothing in it opens the tabs the workspace kept, or a shell, nothing is started before
        // somebody wants one
        if (terminalPanel.Tabs.Count == 0 && bottom.Shown && bottom.Selected == CONSOLE_PAGE && terminalFontSet && !options.SelfTest)
            OpenTerminals();

        UpdateTerminalSession(dt);
    }

    /// <summary>The tabs the workspace kept when the settings say so and there are any, a shell in the workspace's folder otherwise.</summary>
    private void OpenTerminals()
    {
        if (terminals.Settings.RestoreSessions && workspace.Terminal is { } kept && TerminalSessions.Restore(terminalPanel, kept, terminals.ProfileCalled))
        {
            return;
        }

        terminalPanel.NewTab(terminals.UsualProfile, TerminalFolder());
    }

    // The tabs and splits as they were last kept, worked out every tick without allocating, and the workspace written a
    // few seconds after they changed
    private int terminalShape;
    private float terminalKeepIn = -1.0f;

    private void UpdateTerminalSession(float dt)
    {
        int shape = terminalPanel.Tabs.Count * 7919 + terminalPanel.CurrentIndex;
        for (int i = 0; i < terminalPanel.Tabs.Count; i++)
            shape = shape * 31 + terminalPanel.Tabs[i].Tree.Count;

        if (shape != terminalShape)
        {
            terminalShape = shape;
            if (terminalPanel.Tabs.Count > 0)
                terminalKeepIn = 3.0f;
        }

        if (terminalKeepIn > 0.0f && (terminalKeepIn -= dt) <= 0.0f)
            KeepWorkspace();
    }

    /* What Hex adds to the terminal object */

    /// <summary>What Hex adds, for api.hor.</summary>
    public static readonly TerminalApiFunction[] HexApiFunctions =
    [
        new("file", "", "The path of the file on screen in Hex, \"\" for one that was never saved."),
        new("workspace", "", "The folders of Hex's workspace."),
        new("problem", "path, line, column, message, warning?", "Puts a problem in Hex's Problems panel, from a terminal's output say."),
        new("clear_problems", "", "Takes the problems the terminal put in Hex's Problems panel out again.")
    ];

    private IEnumerable<(string, IRuntimeValue)> HexTerminalApi(TerminalPlugin plugin) =>
    [
        ("file", Natives.Function("file", _ => new StringValue(document.Path ?? string.Empty))),
        ("workspace", Natives.Function("workspace", _ => Natives.List(workspace.Folders))),
        ("problem", Natives.Action("problem", args => AddTerminalProblem(args.Text(0), (int)args.Number(1), (int)args.Number(2, 1), args.Text(3), args.Bool(4, false)))),
        ("clear_problems", Natives.Action("clear_problems", _ => ClearTerminalProblems()))
    ];

    /* What the host asks of Hex */

    void ITerminalHostUi.Palette() => CommandPalette();

    void ITerminalHostUi.Choose(string title, string message, IReadOnlyList<string> items, Action<int> picked)
    {
        var choices = new List<(string Text, Action Picked)>();
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            choices.Add((items[i], () => picked(index)));
        }

        ChooseFrom(title, message, choices);
    }

    void ITerminalHostUi.AskText(string title, string message, string initial, Action<string> answered)
    {
        UIDialog dialog = UIDialog.Show(chrome, title, message, new DialogChoice("OK"), new DialogChoice("Cancel"));
        var box = dialog.Body.Add(new TextBox { Text = initial, Size = new Vector2(360, 30), TextScale = 0.14f });
        dialog.Buttons[0].OnPressed = () =>
        {
            dialog.Close();
            answered(box.Text);
        };
        box.OnSubmitted = text =>
        {
            dialog.Close();
            answered(text);
        };
        box.Focus();
    }

    /// <summary>
    /// What the terminal's dawn command asks for. A file opens here, a folder becomes the workspace, and a new window is
    /// another Dawn of its own (Hex.exe --window) on the file or the folder.
    /// </summary>
    bool ITerminalHostUi.OpenInDawn(string path, bool newWindow)
    {
        if (newWindow)
            return StartWindow(path);

        if (Directory.Exists(path))
        {
            OpenFolder(path);
            return true;
        }

        if (!File.Exists(path))
            return false;

        Open(path);
        return true;
    }

    /// <summary>Starts another Dawn of its own on a file or a folder, false when the exe couldn't be started.</summary>
    internal static bool StartWindow(string path)
    {
        if (System.Environment.ProcessPath is not { } exe)
            return false;

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add("--window");
            start.ArgumentList.Add(Directory.Exists(path) ? "--folder" : "--open");
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A file a terminal asks to have opened, in Hex itself, at its line (1 for the first).</summary>
    bool ITerminalHostUi.OpenFile(string path, int line)
    {
        if (!File.Exists(path))
            return false;

        OpenAt(path, Math.Max(0, line - 1), 0);
        return true;
    }
}
