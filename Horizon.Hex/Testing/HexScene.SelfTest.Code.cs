using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// The self test, files of code, their values, the console, docs, the table and completion.
internal sealed partial class HexScene
{
    private void TestCode(HexPointerScript test)
    {
        /* Code, the files that aren't layouts and the code of the ones that are */

        string original = string.Empty;

        test.Run(() =>
        {
            System.IO.File.WriteAllText(dataFile, TEST_DATA);
            if (System.IO.File.Exists(brokenFile)) System.IO.File.Delete(brokenFile);
            Open(dataFile);
            original = code.Text;
        });
        test.Wait();
        test.Check("code: a file that isn't a layout opens as the code it is, the middle to itself", () =>
            document.IsText && code.Text.StartsWith("let speed") && !canvas.Visible && codeScroll.Children.Contains(code) && code.Bounds.Height > canvas.Size.Y * 0.5f);
        test.Check("code: its outline lists what it declares and the keys in it", () =>
            treeTitle.Text.StartsWith("OUTLINE") && sideTree.Visible && sideTree.VisibleCount == 3);
        test.Check("code: what only a layout has is out of the way", () =>
            !layout.Get<UIComponent>("toolbox").Visible && !layout.Get<UIComponent>("layers_section").Visible);

        test.Click(At(() => code, 0.5f, 0.5f));
        test.Check("code: a click gives it the keyboard", () => code.IsFocused);

        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("let broken = ;");
        });
        test.WaitFor(() => code.SyntaxError is not null, 2.0f);
        test.Check("code: what doesn't parse is underlined, on the line it is on", () => code.SyntaxError is { Line: 4 } && document.Modified);
        test.Run(() => code.Document.Undo());
        test.WaitFor(() => code.SyntaxError is null, 2.0f);
        test.Check("code: undone it parses again and has nothing to save", () => code.Text == original && !document.Modified);

        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("let added = 2;");
            Save();
        });
        test.Wait();
        test.Check("code: saved, it has what was typed and the line endings it came with", () =>
            System.IO.File.ReadAllText(dataFile) is { } written && written.Contains("let added = 2;") && written.Contains("\r\n") && !written.Replace("\r\n", "").Contains('\n') && !document.Modified);

        test.Run(() =>
        {
            lastFind = "names";
            code.Document.MoveToStart(false);
            FindAgain(forward: true);
        });
        test.Wait();
        test.Check("code: finding selects what is found", () => code.Document.SelectedText == "names" && code.FoundCount == 1);

        test.Run(RunCode);
        test.Check("code: F5 runs it and lists what it declares", () =>
            runOutput.Any(said => said.StartsWith("speed = 3")) && runOutput.Any(said => said.StartsWith("added = 2")));
    }

    private void TestValuesAndConsole(HexPointerScript test)
    {
        /* What the file comes to, on the right, and the console under it */

        test.WaitFor(() => values is { Error: null } shown && valuesOf == document && shown.Declared.Any(pair => pair.Name == "added"), 3.0f);
        test.Check("values: the right side is what the file comes to, run off the editor's thread", () =>
            values is { Error: null } shown && shown.Declared.Any(pair => pair.Name == "speed" && pair.Value is NumberValue { Value: 3 })
            && valuesTree.Visible && valuesTree.Items.Any(item => item.Text == "names" && item.Detail == "{ 1 }"));
        test.Check("values: a key of an object is found in the code by its path", () => LineOf("names.first") is (2, _));
        test.Check("outline: says what each thing is, a number, an object and a text", () =>
            Outline(code.Text) is var entries && entries.Any(entry => entry is { Name: "speed", Icon: "symbol-number" })
            && entries.Any(entry => entry is { Name: "names", Icon: "symbol-object" }) && entries.Any(entry => entry is { Name: "first", Icon: "symbol-string" }));

        string LastConsoleLine() => consoleLines.Children.Count > 0 && consoleLines.Children[^1] is Label line ? line.Text : string.Empty;

        test.Run(() => AskConsole("speed * 2"));
        test.WaitFor(() => LastConsoleLine() == "6", 2.0f);
        test.Check("console: a line runs in what the file came to", () => LastConsoleLine() == "6" && bottom.Selected == CONSOLE_PAGE);

        test.Run(() => AskConsole("let doubled = speed * 2"));
        test.WaitFor(() => consoleAsked is null, 2.0f);
        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet more = 1;");
        });
        test.WaitFor(() => values is { } shown && shown.Declared.Any(pair => pair.Name == "more"), 3.0f);
        test.Run(() => AskConsole("doubled + more"));
        test.WaitFor(() => consoleAsked is null, 2.0f);
        test.Check("console: what it declared is still there after the file runs again", () => LastConsoleLine() == "7");

        test.Run(() => AskConsole("func down(n) { return down(n + 1); } down(0)"));
        test.WaitFor(() => consoleAsked is null, 3.0f);
        test.Check("console: a function that calls itself for ever is stopped and said so, the editor carries on", () =>
            LastConsoleLine().Contains("deep", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Wait();

        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet broken = names.first();");
        });
        test.WaitFor(() => values is { Error: not null } && code.Diagnostics.Any(problem => !problem.Warning), 3.0f);
        test.Check("values: what goes wrong running it is underlined on the line it went wrong on", () =>
            code.Diagnostics.Any(problem => !problem.Warning && code.Document.LineAt(problem.Line).Contains("broken", StringComparison.Ordinal)));
        test.Run(() => code.Document.Undo());
        test.WaitFor(() => values is { Error: null } && !code.Diagnostics.Any(problem => !problem.Warning), 3.0f);
        test.Check("values: and goes once it runs again", () => !code.Diagnostics.Any(problem => !problem.Warning));

        test.Run(() =>
        {
            code.GoTo(0, 0);
            var names = (ObjectValue)values!.Runtime.UserScope.Lookup("names");
            GoToValue("names.first", values.Sources.TryGetKey(names, "first", out var first) ? first : null);
        });
        test.Wait();
        test.Check("values: a click on a value goes to where the run says it was written", () => code.Document.Caret.Line == 2 && code.Document.Caret.Column == 4);

        // A value written as itself is edited where it is shown, and goes into the code in its place
        test.Run(() =>
        {
            if (valuesTree.Items.FirstOrDefault(item => item.Text == "speed") is { } speed)
                ActivateValue(speed);
        });
        test.Wait();
        test.Check("values: a double click on a number puts a box over it with the number as it is written", () =>
            valueBox is { Visible: true, Text: "3" } box && box.IsFocused);
        test.Run(() => valueBox!.Text = "5");
        test.Run(() => valueBox!.Submit());
        test.Wait();
        test.Check("values: and what is typed goes into the code where the number was and nowhere else", () =>
            code.Text.StartsWith("let speed = 5;", StringComparison.Ordinal) && code.Text.Contains("first: \"one\"", StringComparison.Ordinal) && valueBox is not { Visible: true });
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("values: one step to undo", () => code.Text.StartsWith("let speed = 3;", StringComparison.Ordinal));
    }

    private void TestDocs(HexPointerScript test)
    {
        /* Documentation, // @doc over a name, shown wherever the name is */

        string docsFile = Path.Combine(testFolder, "selftest_docs.hor"), usesFile = Path.Combine(testFolder, "selftest_uses.hor");
        string elsewhereFile = Path.Combine(testFolder, "selftest_elsewhere.hor");
        HexDocument? beforeDocs = null;
        test.Run(() =>
        {
            beforeDocs = document;
            System.IO.File.WriteAllText(docsFile, "// Greetings, by the look of it\n// @doc The list of greetings\nlet greetings = [\"hi\"];\n");
            System.IO.File.WriteAllText(usesFile, "let other = include(\"selftest_docs.hor\");\nlet first = other.greetings;\n");
            System.IO.File.WriteAllText(elsewhereFile, "let picked = greetings;\n");
            Open(docsFile);
        });
        test.WaitFor(() => values is { } ran && valuesOf == document && ran.Declared.Any(pair => pair.Name == "greetings"), 3.0f);
        test.Check("docs: a // @doc over a let is its documentation, under the pointer where it is made", () =>
            HoverHint(code, new UITextPosition(2, 6)) is { } said && said.Contains("The list of greetings", StringComparison.Ordinal));
        test.Check("docs: and in the tooltip of its value", () =>
            valuesTree.Items.FirstOrDefault(item => item.Text == "greetings") is { } row && row.Tooltip.Contains("The list of greetings", StringComparison.Ordinal));

        test.Run(() => Open(usesFile));
        test.WaitFor(() => values is { } ran && valuesOf == document && ran.Declared.Any(pair => pair.Name == "first"), 3.0f);
        test.Check("docs: in a file that includes it, on the name used there, saying where it was made", () =>
            HoverHint(code, new UITextPosition(1, 20)) is { } said && said.Contains("The list of greetings", StringComparison.Ordinal) && said.Contains("selftest_docs.hor", StringComparison.Ordinal));

        test.Run(() =>
        {
            ForgetVocabulary();
            Open(elsewhereFile);
            HoverHint(code, new UITextPosition(0, 15));
        });
        test.WaitFor(() => docIndex is not null, 3.0f);
        test.Check("docs: and in any file of the workspace by its name, out of the index of what every file documents", () =>
            HoverHint(code, new UITextPosition(0, 15)) is { } said && said.Contains("The list of greetings", StringComparison.Ordinal));

        test.Run(() =>
        {
            for (int closing = 0; closing < 3; closing++) CloseNow();
            foreach (string made in new[] { docsFile, usesFile, elsewhereFile })
                System.IO.File.Delete(made);
            if (beforeDocs is not null && documents.Contains(beforeDocs))
                Show(beforeDocs);
        });
        test.Wait();
    }

    private void TestTable(HexPointerScript test)
    {
        /* The table of a file, an object of objects a row each, a file of texts with its languages side by side */

        HexDocument? dataDocument = null;
        string movesFile = Path.Combine(testFolder, "selftest_moves.hor");
        test.Run(() =>
        {
            dataDocument = document;
            System.IO.File.WriteAllText(movesFile, "let moves = {\n    jab: { damage: 5, frames: 3 },\n    kick: { damage: 8, frames: 5, low: true }\n};\n");
            Open(movesFile);
        });
        test.WaitFor(() => HasTable, 3.0f);
        test.Check("table: a file of an object of objects has a table to show, next to its code", () =>
            HasTable && layout.Get<UIComponent>("views").Visible && layout.Get<Button>("table_view").Visible && !layout.Get<Button>("design_view").Visible);
        test.Run(() => SwitchView(HexView.Table));
        test.WaitFor(() => dataTable.Rows.Count == 2, 2.0f);
        test.Check("table: a row for every move, a column for every key any of them has, nothing where a move hasn't got one", () =>
            dataTable.Visible && !codeScroll.Visible && dataTable.Columns.Select(column => column.Title).SequenceEqual(["moves", "damage", "frames", "low"])
            && dataTable.Rows[0][0].Text == "jab" && dataTable.Rows[1][3].Text == "true" && dataTable.Rows[0][3].Text is null or "");

        test.Run(() => ActivateCell(0, 1));
        test.Wait();
        test.Check("table: a double click on a number puts the box over its cell", () => valueBox is { Visible: true, Text: "5" });
        test.Run(() =>
        {
            valueBox!.Text = "6";
            valueBox.Submit();
        });
        test.WaitFor(() => dataTable.Rows.Count == 2 && dataTable.Rows[0][1].Text == "6", 3.0f);
        test.Check("table: and what is typed goes into the code in its place, and the table with it", () =>
            code.Text.Contains("jab: { damage: 6, frames: 3 }", StringComparison.Ordinal) && dataTable.Rows[0][1].Text == "6");

        // A key only one move has that is a letter off one the others have, and a path that isn't there
        test.Run(() =>
        {
            code.Document.Replace("let moves = {\n    jab: { stances: \"standing\", damage: 5 },\n    kick: { stances: \"standing\", damage: 8 },\n"
                + "    sweep: { stance: \"crouching\", damage: 7 },\n    art: { stances: \"standing\", sprite: \"Assets/nothing/here.ase\", damage: 1 }\n};\n");
        });
        test.WaitFor(() => code.Diagnostics.Count(problem => problem.Warning) >= 2, 3.0f);
        test.Check("lint: a key only one of the objects has, a letter off one the others have, is a typo by the look of it", () =>
            code.Diagnostics.Any(problem => problem.Warning && problem.Line == 3 && problem.Message.Contains("stances", StringComparison.Ordinal)));
        test.Check("lint: and a path to a file that isn't there is said about, on the text", () =>
            code.Diagnostics.Any(problem => problem.Warning && problem.Line == 4 && problem.Message.Contains("Assets/nothing/here.ase", StringComparison.Ordinal)));
        test.Run(() => code.Document.Undo());
        test.Wait();

        string english = Path.Combine(testFolder, "selftest_en.hor"), french = Path.Combine(testFolder, "selftest_fr.hor");
        test.Run(() =>
        {
            CloseNow();
            System.IO.File.WriteAllText(french, "let strings = { menu: { quit: \"Quitter\" } };\n");
            System.IO.File.WriteAllText(english, "let strings = { menu: { quit: \"Quit\", play: \"Play\" } };\n");
            Open(english);
        });
        test.WaitFor(() => HasTable, 3.0f);
        test.Run(() => SwitchView(HexView.Table));
        test.WaitFor(() => dataTable.Rows.Count == 2, 2.0f);
        test.Check("table: a file of texts is a row a key and a column a language, every one next to it in its folder", () =>
            dataTable.Columns.Count == 3 && dataTable.Columns[1].Title == "selftest_en" && dataTable.Columns[2].Title == "selftest_fr"
            && dataTable.Rows.Any(row => row[0].Text == "menu.quit" && row[1].Text == "Quit" && row[2].Text == "Quitter"));
        test.Check("table: and what a language hasn't got is in red", () =>
            dataTable.Rows.First(row => row[0].Text == "menu.play")[2] is { Fill: not null } missing && missing.Color == ErrorColor);

        test.Run(() =>
        {
            CloseNow();
            System.IO.File.Delete(english);
            System.IO.File.Delete(french);
            System.IO.File.Delete(movesFile);
            if (dataDocument is not null && documents.Contains(dataDocument))
                Show(dataDocument);
        });
        test.Wait();

        test.Run(() =>
        {
            System.IO.File.WriteAllText(brokenFile, "let = 3;\n");
            workspaceProblems = null;
            CheckAllFiles();
        });
        test.WaitFor(() => workspaceProblems is not null && !Working, 5.0f);
        test.Check("code: checking every file lists the one that doesn't parse, on the left", () =>
            workspaceProblems is { } listed && listed.Any(result => result.Path == brokenFile) && bottom.Visible && bottom.Selected == PROBLEMS_PAGE
            && problemList.Any(problem => problem.Path == brokenFile));
        test.Run(() =>
        {
            CloseResults();
            System.IO.File.Delete(brokenFile);
        });
    }

    private void TestLayoutAsCode(HexPointerScript test)
    {
        /* A layout as its designer or its code, and what says it is one */

        test.Run(() => Open(savedFile));
        test.Wait();
        test.Check("views: a layout Hex saved says so at the top", () =>
            System.IO.File.ReadAllText(savedFile).Contains("#define layout") && Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(System.IO.File.ReadAllText(savedFile)));
        test.Check("views: and opens as its designer, the canvas the whole of the middle and no code", () =>
            !document.IsText && LayoutTools && canvas.Visible && !codeScroll.Visible && layout.Get<UIComponent>("views").Visible
            && layout.Get<Button>("design_view").Selected && layout.Get<UIComponent>("palette").Visible);

        test.Click(At(() => layout.Get<Button>("code_view")));
        test.Wait();
        test.Check("views: the code button shows its code instead, the middle to itself", () =>
            document.View == HexView.Code && !canvas.Visible && codeScroll.Visible && codeScroll.Children.Contains(code)
            && layout.Get<Button>("code_view").Selected && code.IsFocused);
        test.Check("views: with the explorer and what is wrong with it round it, not the tools of a layout", () =>
            !layout.Get<UIComponent>("toolbox").Visible && !layout.Get<UIComponent>("layers_section").Visible
            && layout.Get<Label>("inspector_title").Text == compositor.Resolve("@properties.file") && !treeTitle.Text.StartsWith("LAYOUT"));

        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet from_code = compositor.label({ text: \"typed\" });");
        });
        test.Wait();
        test.Check("views: typed into, a layout's code waits to be put into it", () => document.CodeEdited && document.Find("from_code") is null);

        test.Click(At(() => layout.Get<Button>("design_view")));
        test.Wait();
        test.Check("views: back to the designer it goes in, the layout has what was typed", () =>
            LayoutTools && canvas.Visible && !document.CodeEdited && document.Find("from_code") is Label && code.Text.Contains("from_code"));

        test.Run(ToggleView);
        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet = ;");
        });
        test.Wait();
        test.Run(ToggleView);
        test.Wait();
        test.Check("views: code that doesn't build keeps it on the code", () => document.View == HexView.Code && document.CodeEdited);
        test.Run(() =>
        {
            code.Document.Undo();
            ApplyCode();
            SwitchView(HexView.Code);
        });
        test.Wait();
    }

    private void TestCompletion(HexPointerScript test)
    {
        /* Completing what is typed */

        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet completed = compositor.label({ te");
            code.Complete(typed: true);
        });
        test.Wait();
        test.Check("complete: inside a component being made it offers that kind's properties", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "text") && code.Completions.All(item => item.Text.Contains("te", StringComparison.OrdinalIgnoreCase)));
        test.Run(() =>
        {
            int at = code.Completions.ToList().FindIndex(item => item.Text == "text");
            code.MoveCompletion(at);
            code.AcceptCompletion();
        });
        test.Check("complete: picked, it goes in with its colon", () => !code.CompletionOpen && code.Document.LineAt(code.Document.Caret.Line).EndsWith("compositor.label({ text: "));
        test.Run(() =>
        {
            code.Document.Insert("\"x\" });\nlet other = compositor.");
            code.Complete(typed: true);
        });
        test.Check("complete: after compositor. it offers the kinds of component", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "button") && code.Completions.Any(item => item.Text == "collapsible"));
        test.Run(() =>
        {
            code.CloseCompletion();
            code.Document.Undo();
            code.Document.Undo();
            code.Document.Undo();
            RevertCode();
        });
    }
}
