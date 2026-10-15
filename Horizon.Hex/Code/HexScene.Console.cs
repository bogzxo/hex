using System.Numerics;

using Silk.NET.Input;

using Horizon.HIDL;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The console, the third page of the panel under the editor. A line of HIDL typed in is run in what the file on screen
// came to the last time it was run (its values), the way a browser's console runs in the page, so `moves.jab.phases`
// shows the phases and `wander({ tick: 3 })` calls a behaviour. What the console declares stays for the lines after it,
// and is declared again in a newer run of the file. Up and Down go through what was typed before.
internal sealed partial class HexScene
{
    private const int CONSOLE_PAGE = 2;
    private const int MAX_CONSOLE_LINES = 300;

    private StackPanel consoleLines = null!, consolePage = null!;
    private ScrollPanel consoleScroll = null!;
    private TextBox consoleInput = null!;

    // What F5 left behind, used by the console when the values haven't got the file on screen
    private HIDLRuntime? consoleRuntime;

    // The runtime the console last ran a line in, and what it declared in its lines so far
    private HIDLRuntime? consoleRanIn;
    private readonly List<string> consoleDeclared = [];

    // What was typed, the oldest first, and where Up and Down have got to in it (its count for the line being typed)
    private readonly List<string> consoleHistory = [];
    private int consoleBack;

    // Lines to go into the console, from the thread that runs them, and the line that is waiting for its answer
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, Vector4? Color)> consoleOut = new();
    private string? consoleAsked;

    private void BuildConsole()
    {
        consoleLines = layout.Get<StackPanel>("console_lines");
        consolePage = layout.Get<StackPanel>("console_page");
        consoleScroll = layout.Get<ScrollPanel>("console_scroll");
        consoleInput = layout.Get<TextBox>("console_input");
        consoleInput.OnSubmitted = AskConsole;

        layout.Populate(consoleLines, 0);
        ConsoleLine(Said("console.welcome"), DimColor);

        shortcuts.Add(Key.Up, () => ConsoleHistory(-1), whileTyping: true);
        shortcuts.Add(Key.Down, () => ConsoleHistory(1), whileTyping: true);
    }

    /// <summary>Runs a line in the console, bringing it up if it isn't, the right click on a value does this with its path.</summary>
    private void AskConsole(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;

        if (bottom.Selected != CONSOLE_PAGE || !bottom.Shown)
            ShowPanel(CONSOLE_PAGE);

        HIDLRuntime runtime = values is { } shown && valuesOf == document ? shown.Runtime : consoleRuntime ??= new HIDLRuntime { BaseDirectory = FolderOf(document) };
        IReadOnlyList<string> before = runtime == consoleRanIn ? [] : consoleDeclared;

        if (!evaluation.Ask(runtime, before, line, printed => consoleOut.Enqueue((Literal(printed), null))))
        {
            Say(Said("console.busy"), error: true);
            return;
        }

        consoleRanIn = runtime;
        consoleAsked = line;
        consoleHistory.Remove(line);
        consoleHistory.Add(line);
        consoleBack = consoleHistory.Count;

        ConsoleLine($"[color:accent]>[/color] {Literal(line)}", null);
        consoleInput.Text = string.Empty;
        consoleInput.Focus();
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
            ConsoleLine($"$(error) {Literal(answer[1..])}", ErrorColor);
        }
        else
        {
            ConsoleLine(Literal(answer), DimColor);

            // A line that declares something is declared again in a newer run of the file
            if (consoleAsked is { } asked && (asked.StartsWith("let ", StringComparison.Ordinal) || asked.StartsWith("const ", StringComparison.Ordinal) || asked.StartsWith("func ", StringComparison.Ordinal)))
                consoleDeclared.Add(asked);
        }

        consoleAsked = null;

        // It ran in what the values show, which it may have changed
        if (!LayoutTools)
            inspectorDirty = true;
    }

    private void ConsoleLine(string text, Vector4? color)
    {
        var label = layout.Instantiate(consoleLines.Template, consoleLines).Get<Label>("line");
        label.Text = text;
        label.Color = color;
        label.Wrap = true;
        label.Size = new Vector2(ConsoleWidth, 0);

        while (consoleLines.Children.Count > MAX_CONSOLE_LINES)
            consoleLines.Remove(consoleLines.Children[0]);

        // To the bottom once the new line is laid out, it isn't yet, and the scroll stopped short of it
        consoleToEnd = 2;
    }

    // How many more ticks the console is scrolled to its newest line, see FitConsole
    private int consoleToEnd;

    /// <summary>Up and Down in the console, back through what was typed and forward again to an empty line.</summary>
    private void ConsoleHistory(int by)
    {
        if (!consoleInput.IsFocused || consoleHistory.Count == 0)
            return;

        consoleBack = Math.Clamp(consoleBack + by, 0, consoleHistory.Count);
        consoleInput.Text = consoleBack < consoleHistory.Count ? consoleHistory[consoleBack] : string.Empty;
    }

    // How wide a line of the console is, the page as it was last laid out less the scroll bar. The page is filled into
    // the panel and has no size of its own to ask, the lines were sized off that and wrapped at the least there is
    private float ConsoleWidth => MathF.Max(MIN_FLEXIBLE, consolePage.Bounds.Width - consolePage.Padding.Total.X - ROW_MARGIN);

    /// <summary>
    /// Helper method to fit the console to the page of the panel, from FitChrome. The line typed on along the bottom, what
    /// was said above it as tall as the rest, every line of that as wide as the page.
    /// </summary>
    private void FitConsole()
    {
        UIRect page = consolePage.Bounds;
        if (page.IsEmpty)
            return;

        float inside = page.Width - consolePage.Padding.Total.X;
        Vector2 scroll = new(inside, MathF.Max(24.0f, page.Height - consolePage.Padding.Total.Y - consoleInput.Size.Y - consolePage.Spacing));
        if (consoleScroll.Size != scroll)
            consoleScroll.Size = scroll;

        if (consoleInput.Size.X != inside)
            consoleInput.Size = consoleInput.Size with { X = inside };

        float width = ConsoleWidth;

        if (consoleToEnd > 0)
        {
            consoleToEnd--;
            consoleScroll.Offset = float.MaxValue;
        }

        for (int i = 0; i < consoleLines.Children.Count; i++)
        {
            UIComponent line = consoleLines.Children[i];
            if (line.Size.X != width)
                line.Size = line.Size with { X = width };
        }
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
}
