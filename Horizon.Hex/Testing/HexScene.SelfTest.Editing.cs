using System.Numerics;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The self test, the editing round of on-par.md. More than one caret, folding by brackets (a caret going inside opens
// it), and long lines wrapped at the edge.
internal sealed partial class HexScene
{
    private void TestEditingRound(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "fold_me.hor");
        string longComment = "// " + string.Concat(Enumerable.Repeat("a long comment that goes on and on ", 12));
        test.Run(() =>
        {
            File.WriteAllText(file, $"let moves = {{\n    jab: {{\n        damage: 5\n    }},\n    kick: {{\n        damage: 7\n    }}\n}};\n{longComment}\n");
            Open(file);
        });
        test.WaitFor(() => code.CanFold(1) && code.CanFold(4), 3.0f);
        test.Check("editing: a line that opens a bracket closed further down can fold", () => code.CanFold(0) && code.CanFold(1) && code.CanFold(4) && !code.CanFold(2));

        test.Run(() => code.FoldAll(2));
        test.Wait();
        test.Check("editing: the keys of the top object folded, their insides gone from the rows", () =>
            code.FoldedLines.Count == 2 && code.FoldedLines.Contains(1) && code.FoldedLines.Contains(4) && code.RowCount == code.Document.LineCount - 4);

        test.Run(() => code.GoTo(5, 8));
        test.Wait();
        test.Check("editing: a caret going inside something folded opens it", () => !code.FoldedLines.Contains(4) && code.FoldedLines.Contains(1));

        test.Run(() => code.UnfoldAll());
        test.Wait();
        test.Run(ToggleWrap);
        test.Wait();
        test.Check("editing: Alt+Z wraps the long line over more than one row, kept in the settings", () =>
            settings.WrapCode && code.WordWrap && code.RowCount > code.Document.LineCount);
        test.Run(ToggleWrap);
        test.Wait();
        test.Check("editing: and again doesn't", () => !code.WordWrap && code.RowCount == code.Document.LineCount);

        // Ctrl+D on a word and twice more, then typed over at every one of them
        test.Run(() =>
        {
            code.GoTo(2, 9);
            code.Document.AddNextOccurrence();
            code.Document.AddNextOccurrence();
            foreach (char typed in "hit")
                code.Document.Type(typed);
        });
        test.Wait();
        test.Check("editing: Ctrl+D takes the word and the next place it is, and both are typed over", () =>
            code.Text.Contains("hit: 5", StringComparison.Ordinal) && code.Text.Contains("hit: 7", StringComparison.Ordinal) && code.Document.CaretCount == 2);

        // Inside a call, how it's called, the argument the caret is on picked out
        test.Run(() =>
        {
            code.Document.CollapseCarets();
            code.GoTo(code.Document.LineCount - 1, 0);
            code.Document.Insert("let reach = math.clamp(4, ");
        });
        test.Wait();
        test.Check("editing: signature help says how the call is called, the argument being typed picked out", () =>
            code.Signature is { Text: "math.clamp(x, low, high)" } said && said.Text.Substring(said.Start, said.Length) == "low");
        test.Run(() =>
        {
            code.Document.Insert("1)");
        });
        test.Wait();
        test.Check("editing: and goes once the call is closed", () => code.Signature is null);

        test.Run(() => CloseNow());
        test.Wait();
        TestInlineValues(test);
        TestSplit(test);
        TestBackdropUnderCode(test);
        TestRunningProgram(test);
        TestRunner(test);
    }

    // A behaviour called tick after tick with a view, what it hands back every tick and the state of the file after it said
    private void TestRunner(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "behaviour.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, "let pressed_for = 0;\nfunc wander(view) {\n    pressed_for = pressed_for + 1;\n    return view.tick * 2;\n}\n");
            Open(file);
        });
        test.WaitFor(() => values is { } shown && valuesOf == document && shown.Declared.Any(pair => pair.Name == "wander"), 3.0f);
        test.Run(() =>
        {
            OpenRunner();
            runnerFunction.Value = "wander";
            runnerView.Text = "{ tick: 0 }";
            RunBehaviour(3);
        });
        test.WaitFor(() => ConsoleSaid("state   pressed_for = 3"), 3.0f);
        test.Check("runner: a function called three ticks, what it handed back every tick and the state after in the console", () =>
            ConsoleSaid("tick 1   0") && ConsoleSaid("tick 3   4") && ConsoleSaid("state   pressed_for = 3") && runnerTick == 3);

        test.Run(() => RunBehaviour(1));
        test.WaitFor(() => ConsoleSaid("tick 4   6"), 3.0f);
        test.Check("runner: one tick more goes on from where it got to, its state kept", () => ConsoleSaid("tick 4   6") && ConsoleSaid("pressed_for = 4"));

        test.Run(() =>
        {
            runnerWindow?.Parent?.Remove(runnerWindow);
            runnerWindow = null;
            CloseNow();
        });
        test.Wait();
    }

    // Whether a line of the console says something
    private bool ConsoleSaid(string text) =>
        Descendants(consoleLines).OfType<Label>().Any(label => label.Text.Contains(text, StringComparison.Ordinal));

    // The canvas's dark stays under what the middle shows, the panel dragged up with the code on screen didn't have it
    // follow and it showed through the panel where the canvas had been
    private void TestBackdropUnderCode(HexPointerScript test)
    {
        float was = 0.0f;
        test.Run(() =>
        {
            ShowPanel(OUTPUT_PAGE);
            was = bottom.Size.Y;
            bottom.Size = bottom.Size with { Y = was + 200.0f };
        });
        test.Settle(0.2f);
        test.Check("canvas: its dark is under the code and no further, the panel grown with a file of code up", () =>
            !canvas.Visible && codeScroll.Visible && Vector2.Distance(backdrop.Position, codeScroll.Bounds.Center) < 1.0f
            && backdrop.Viewport is { } shown && Vector2.Distance(shown.Size, codeScroll.Bounds.Size) < 1.0f);
        test.Run(() => bottom.Size = bottom.Size with { Y = was });
        test.Wait();
    }

    // A program an extension started (Fight!) is said in the status bar while it runs, saves reloading in it
    private void TestRunningProgram(HexPointerScript test)
    {
        string shell = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(shell) || extensions.Count == 0)
            return;

        test.Run(() => StartProgram(extensions[0], shell, ["/c", "ping", "-n", "3", "127.0.0.1"], null));
        test.Wait();
        test.Check("running: a game started from Hex is said in the status bar, what's saved reloading in it", () =>
            runningItem.IsVisible?.Invoke() == true && runningItem.Text.Contains("cmd running", StringComparison.Ordinal));
        test.WaitFor(() => Volatile.Read(ref programsRunning) == 0, 8.0f);
        test.Check("running: and isn't once it's gone", () => runningItem.IsVisible?.Invoke() == false);
    }

    // A let worked out says what it came to at the end of its line, one written as its value doesn't
    private void TestInlineValues(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "worked_out.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, "let base = 64;\nlet reach = base * 1.5;\n");
            Open(file);
        });
        test.WaitFor(() => code.InlineHints.Count > 0, 3.0f);
        test.Check("values: a let that was worked out says what it came to at the end of its line, a literal doesn't", () =>
            code.InlineHints is [{ Line: 1, Text: "= 96" }]);

        // A function called by a name a letter off, the error says which it most likely meant
        test.Run(() => code.Document.Replace("func strike() { return 3; }\nlet base = 64;\nlet reach = sitrke();\n"));
        test.WaitFor(() => code.Diagnostics.Any(problem => problem.Message.Contains("Did you mean", StringComparison.Ordinal)), 3.0f);
        test.Check("values: an error that names something a letter or two off a name there is says the likely one", () =>
            code.Diagnostics.Any(problem => problem.Message.Contains("Did you mean strike?", StringComparison.Ordinal)));
        test.Run(() => CloseNow());
        test.Wait();
    }

    // Two files side by side, Ctrl+\, the one with the keyboard the one Hex is about
    private void TestSplit(HexPointerScript test)
    {
        string one = Path.Combine(testFolder, "side_one.hor"), two = Path.Combine(testFolder, "side_two.hor");
        HexDocument? first = null, second = null;
        test.Run(() =>
        {
            File.WriteAllText(one, "let one = 1;\n");
            File.WriteAllText(two, "let two = 2;\n");
            Open(one);
            first = document;
            Open(two);
            second = document;
            ToggleSplit();
        });
        test.Settle(0.2f);
        test.Check("split: Ctrl+\\ puts the file on screen on the right of the one before it, both in the middle", () =>
            Split && splitLeft == first && splitRight == second && first!.Editor.Parent == codeScroll && second!.Editor.Parent == codeScroll
            && first.Editor.Bounds.Max.X <= second.Editor.Bounds.Min.X + 1 && first.Editor.Bounds.Width > 80);

        test.Run(() => first!.Editor.Focus());
        test.Wait();
        test.Check("split: the side with the keyboard is the file Hex is about", () => document == first && Split);

        test.Run(ToggleSplit);
        test.Wait();
        test.Check("split: and Ctrl+\\ again is one file in the middle", () => !Split && document.Editor.Parent == codeScroll && codeScroll.Children.OfType<Horizon.UI.Components.CodeEditor>().Count() == 1);

        test.Run(() =>
        {
            OpenToSide(second!);
        });
        test.Wait();
        test.Check("split: open to the side on a tab splits for it", () => Split && document == second && splitLeft == first);

        test.Run(() =>
        {
            CloseNow();
            if (documents.Contains(first!))
            {
                Show(first!);
                CloseNow();
            }
        });
        test.Wait();
        test.Check("split: and closing a side ends it", () => !Split);
    }
}
