using System.Numerics;

using Horizon.HIDL;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The console, a profile of the terminal page (the third of the panel under the editor). A line of HIDL typed in is run
// in what the file on screen came to the last time it was run (its values), the way a browser's console runs in the
// page, so `moves.jab.phases` shows the phases and `wander({ tick: 3 })` calls a behaviour. What the console declares
// stays for the lines after it, and is declared again in a newer run of the file. It is the engine's REPL
// (TerminalHidlRepl) with Hex's evaluation behind it, which runs a line off the editor's thread and answers later, so
// it reads the line, colours it, completes the file's names and keeps its history like any other, and looks the same
// as a git status in the tab next to it.
internal sealed partial class HexScene
{
    private const int CONSOLE_PAGE = 2;

    /// <summary>The id of the console's profile.</summary>
    private const string CONSOLE_PROFILE = "hex_console";

    // What F5 left behind, used by the console when the values haven't got the file on screen
    private HIDLRuntime? consoleRuntime;

    // The runtime the console last ran a line in, and what it declared in its lines so far
    private HIDLRuntime? consoleRanIn;
    private readonly List<string> consoleDeclared = [];

    // Lines to go into the console, from the thread that runs them, and the line that is waiting for its answer
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, Vector4? Color)> consoleOut = new();
    private string? consoleAsked;

    // The REPL of the console's tab, null while there is none
    private TerminalHidlRepl? consoleRepl;

    private void BuildConsole()
    {
    }

    /// <summary>The console's program, for the terminal host when a tab of its profile is opened.</summary>
    private TerminalHidlRepl MakeConsole()
    {
        var repl = new TerminalHidlRepl(Path.Combine(TerminalFiles.UserFolder, "hex_console_history.txt"))
        {
            Name = "HIDL",
            Where = () => document.Path is { } path ? Path.GetFileName(path) : document.Name,
            RuntimeFor = ConsoleRuntime,
            Greeting = "\u001b[2m" + Said("console.welcome") + "\u001b[0m"
        };
        repl.OnLine = ConsoleLineGiven;
        consoleRepl = repl;
        return repl;
    }

    /// <summary>The runtime the console runs in, the values of the file on screen when they're in, what F5 left otherwise.</summary>
    private HIDLRuntime ConsoleRuntime() =>
        values is { } shown && valuesOf == document ? shown.Runtime : consoleRuntime ??= new HIDLRuntime { BaseDirectory = FolderOf(document) };

    /// <summary>The console's tab brought up, made when there isn't one, the panel on its page.</summary>
    private TerminalHidlRepl EnsureConsole()
    {
        if (bottom.Selected != CONSOLE_PAGE || !bottom.Shown)
            ShowPanel(CONSOLE_PAGE);

        if (ConsoleView() is { } view && consoleRepl is { IsRunning: true } running)
        {
            terminalPanel.FocusPane(view);
            return running;
        }

        terminalPanel.NewTab(terminals.ProfileCalled(CONSOLE_PROFILE));
        return consoleRepl!;
    }

    /// <summary>Runs a line in the console, bringing it up if it isn't, the right click on a value does this with its path.</summary>
    private void AskConsole(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;

        // As if it was typed, so it is on screen and in the history the same way
        EnsureConsole().Input(line + "\r");
    }

    /// <summary>A line given in the console, run in what the file came to, the answer comes in UpdateConsole.</summary>
    private void ConsoleLineGiven(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;

        HIDLRuntime runtime = ConsoleRuntime();
        IReadOnlyList<string> before = runtime == consoleRanIn ? [] : consoleDeclared;

        if (!evaluation.Ask(runtime, before, line, printed => consoleOut.Enqueue((UIRichText.Literal(printed), null))))
        {
            consoleRepl?.Write($"\u001b[33m{Said("console.busy")}\u001b[0m\n");
            return;
        }

        consoleRanIn = runtime;
        consoleAsked = line;
    }

    /// <summary>Every tick, what the line that was run printed and came to, into the console.</summary>
    private void UpdateConsole()
    {
        while (consoleOut.TryDequeue(out var printed))
            ConsoleLine(printed.Text, printed.Color);

        if (evaluation.TakeAnswer() is not { } answer)
            return;

        if (HexEvaluation.IsError(answer))
        {
            ConsoleLine(UIRichText.Literal(answer[1..]), ErrorColor);
        }
        else
        {
            consoleRepl?.Write(TerminalHidlRepl.Coloured(UIRichText.Literal(answer)) + "\n");

            // A line that declares something is declared again in a newer run of the file
            if (consoleAsked is { } asked && (asked.StartsWith("let ", StringComparison.Ordinal) || asked.StartsWith("const ", StringComparison.Ordinal) || asked.StartsWith("func ", StringComparison.Ordinal)))
                consoleDeclared.Add(asked);
        }

        consoleAsked = null;

        // It ran in what the values show, which it may have changed
        if (!LayoutTools)
            inspectorDirty = true;
    }

    /// <summary>A line written into the console, an error in red and anything dim dim, markup taken out of it.</summary>
    private void ConsoleLine(string text, Vector4? color)
    {
        TerminalHidlRepl repl = consoleRepl is { IsRunning: true } running && ConsoleView() is not null ? running : EnsureConsole();
        string plain = UIRichText.Plain(text);
        string coloured = color == ErrorColor ? $"\u001b[31m{plain}\u001b[0m" : color == DimColor ? $"\u001b[2m{plain}\u001b[0m" : plain;
        repl.Write(coloured + "\n");
    }

    private void FitConsole()
    {
    }

    // The lines --console asked for, run one at a time once the values of the file are in
    private Queue<string>? consoleAskedFor;

    /// <summary>Every tick, the next line --console asked for, once the last one is answered.</summary>
    private void RunAskedConsole()
    {
        consoleAskedFor ??= new Queue<string>(options.Console ?? []);
        if (consoleAskedFor.Count == 0 || consoleAsked is not null || values is null || valuesOf != document)
            return;

        AskConsole(consoleAskedFor.Dequeue());
    }

    /* For the self test */

    /// <summary>The last line the console wrote that isn't its prompt, empty for none.</summary>
    private string LastConsoleLine()
    {
        if (ConsoleView() is not { } view)
            return string.Empty;

        TerminalScreen screen = view.Screen;
        for (int row = screen.TotalRows - 1; row >= 0; row--)
        {
            if (row == screen.Scrollback.Count + screen.CursorY)
                continue;
            string line = screen.RowAt(row).Text().Trim();
            if (line.Length > 0)
                return line;
        }

        return string.Empty;
    }

    /// <summary>Whether the console has said something, anywhere in what it has written.</summary>
    private bool ConsoleSaid(string text)
    {
        if (ConsoleView() is not { } view)
            return false;

        TerminalScreen screen = view.Screen;
        for (int row = 0; row < screen.TotalRows; row++)
        {
            if (screen.RowAt(row).Text().Contains(text, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private TerminalView? ConsoleView()
    {
        if (consoleRepl is null)
            return null;

        foreach (TerminalTab tab in terminalPanel.Tabs)
        {
            foreach (TerminalView view in tab.Views)
            {
                if (view.Program == consoleRepl)
                    return view;
            }
        }

        return null;
    }
}
