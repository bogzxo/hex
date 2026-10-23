using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The self test of D through the real language server (serve-d, see language_servers.hor), left out when it isn't on
// this machine. A dub project with a struct in it is opened, the file is coloured as D and opened in serve-d, and the
// list after a dot is the struct's own members, after a few letters the functions of the file they start, the way the
// editor asks. serve-d asks to download DCD the first time, which is answered the way the dialog's button does.
internal sealed partial class DawnScene
{
    private const string D_PROGRAM =
        "module app;\n\nstruct Point\n{\n    int x, y;\n    int lengthSquared() const { return x * x + y * y; }\n}\n\n"
        + "int countThings(int[] things) { return cast(int) things.length; }\n\nvoid main()\n{\n    Point p = Point(3, 4);\n    \n}\n";

    private void TestD(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_d"), program = Path.Combine(folder, "source", "app.d");
        bool there = HexServerCatalog.Locate("serve-d") is not null;
        test.Run(() =>
        {
            if (!there)
            {
                Console.WriteLine("[Hex self-test] serve-d isn't on this machine, the D part is skipped");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(program)!);
            File.WriteAllText(Path.Combine(folder, "dub.json"), "{ \"name\": \"selftest_d\" }\n");
            File.WriteAllText(program, D_PROGRAM);
            Open(program);
        });
        if (!there)
            return;

        test.Check("d: a .d file is coloured as D", () => code.Highlighter is TextMateHighlighter { ScopeName: "source.d" });
        test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
        test.Check("d: and opened in serve-d", () => languages.LinterOf(document) == "d");

        // DCD, which serve-d asks for the first time, downloaded the way the dialog's first button has it
        test.WaitFor(() => languages.QuestionUp is not null, 6.0f);
        test.Run(() =>
        {
            if (languages.QuestionUp is { Actions.Count: > 0 } question)
                question.Answer(question.Actions[0]);
        });

        // Typed on the empty line in main as a key would, the dot asking for what can follow it. DCD reads the project
        // as it starts, a minute at the most on a cold start
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(13, 4));
            code.Document.Insert("p.");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("lengthSquared", StringComparison.Ordinal)), 90.0f);
        test.Check("d: after p. the list is the struct's own, its fields and what it does, from serve-d", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "x") && code.Completions.Any(item => item.Text.StartsWith("lengthSquared", StringComparison.Ordinal)));
        test.Run(() => Shot("d-completion"));
        test.Settle(0.2f);

        test.Run(() =>
        {
            code.CloseCompletion();
            code.Document.Backspace();
            code.Document.Backspace();
            code.Document.Insert("cou");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("countThings", StringComparison.Ordinal)), 30.0f);
        test.Check("d: a few letters of a function of the file offer it, with what it takes", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("countThings", StringComparison.Ordinal)));

        // What's wrong, as it's typed, a syntax error where it is and a variable never used said quietly, and still said
        // a while after, not for a moment and gone
        test.Run(() =>
        {
            code.CloseCompletion();
            code.Document.Backspace();
            code.Document.Backspace();
            code.Document.Backspace();
            code.Document.Insert("int unused = 3;\n    int y = 5 +;");
        });
        test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("Primary expression")), 30.0f);
        test.Check("d: a syntax error typed is underlined on its own line", () =>
            code.Diagnostics.Any(found => found.Line == 14 && !found.Warning && found.Message.Contains("Primary expression")));
        test.Settle(3.0f);
        test.Check("d: and still is a while after, with the variable never used as a warning", () =>
            code.Diagnostics.Any(found => found.Message.Contains("Primary expression"))
            && code.Diagnostics.Any(found => found.Line == 13 && found.Warning && found.Message.Contains("never used")));
        // Counted in this file, the Problems panel has every open file's
        test.Check("d: which is only a hint, quieter than a warning and counted with neither", () =>
            code.Diagnostics.Any(found => found.Line == 13 && found.Hint)
            && !problemList.Any(problem => problem.Path == document.Path && problem.Warning && !problem.Hint) && problemList.Any(problem => problem.Path == document.Path && !problem.Warning));

        // A file opened with something wrong in it says so as it opens, not once it's typed into (serve-d is told
        // lintOnFileOpen "always"), and saved it still does
        string broken = Path.Combine(folder, "source", "broken.d");
        test.Run(() =>
        {
            File.WriteAllText(broken, "module broken;\n\nvoid main()\n{\n    int unused = 3;\n    int x = ;\n}\n");
            Open(broken);
        });
        test.WaitFor(() => document.Path == broken && code.Diagnostics.Any(found => found.Message.Contains("Primary expression")), 30.0f);
        test.Check("d: a file opened with something wrong in it says so straight away", () =>
            document.Path == broken && code.Diagnostics.Any(found => found.Line == 5 && !found.Warning && !found.Message.EndsWith("()", StringComparison.Ordinal)));
        test.Run(Save);
        test.Settle(2.0f);
        // With a compiler on the PATH serve-d says what dub's build says on save, D-Scanner's hints go then
        test.Check("d: and saved it still says so", () => code.Diagnostics.Any(found => found.Line == 5 && !found.Warning));

        // Text Hex puts into the file itself (what wasn't saved brought back after a crash, the file read again after it
        // changed on the disk) goes to serve-d as a change, what it says is about the text on the screen
        test.Run(() => SetCode(document, "module broken;\n\nvoid main()\n{\n    int fine = 1;\n    fine++;\n    int z = 2 *;\n}\n", fresh: false));
        // dmd's check of the text says line 6 sooner than serve-d does, the server's word on the old text has to go too
        test.WaitFor(() => code.Diagnostics.Any(found => found.Line == 6 && !found.Warning) && !code.Diagnostics.Any(found => found.Message.Contains("unused")), 30.0f);
        test.Check("d: text Hex puts in itself is what serve-d checks, not what's on the disk", () =>
            code.Diagnostics.Any(found => found.Line == 6 && !found.Warning) && !code.Diagnostics.Any(found => found.Message.Contains("unused")));

        test.Run(() =>
        {
            code.CloseCompletion();
            CloseNow();
            LanguageServers.StopAll();
        });
    }
}
