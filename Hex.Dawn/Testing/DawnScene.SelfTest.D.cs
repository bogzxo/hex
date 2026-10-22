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

        test.Run(() =>
        {
            code.CloseCompletion();
            CloseNow();
            LanguageServers.StopAll();
        });
    }
}
