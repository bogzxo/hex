namespace Hex.Dawn;

// The self test, TODO and FIXME lit up in comments, and nowhere else. C# first, then a file of every other language Hex
// colours (DawnScene.SelfTest.TodoSamples), each opened and checked once its colours are in.
internal sealed partial class DawnScene
{
    private void TestTodos(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "todos.cs");
        test.Run(() =>
        {
            File.WriteAllText(file, "// TODO make it quicker\nint TODO = 1; // FIXME before shipping\nstring said = \"TODO not this one\";\n// TODOS NOTODO aren't words of it\n");
            Open(file);
        });
        test.WaitFor(() => code.TodosAt(1).Any(), 5.0f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "todos.png"));
        });
        test.Settle(0.3f);
        test.Check("todos: a TODO in a comment is lit, a FIXME is lit as urgent", () =>
            code.TodosAt(0).SequenceEqual([(3, 7, false)]) && code.TodosAt(1).SequenceEqual([(17, 22, true)]));
        test.Check("todos: but not a name, a string or a word with TODO in it", () =>
            !code.TodosAt(1).Any(todo => todo.From == 4) && !code.TodosAt(2).Any() && !code.TodosAt(3).Any());
        CloseTodos(test, file);

        foreach (var (name, sample) in TodoSamples)
            TestTodosIn(test, name, sample);
    }

    // Helper method to open one sample and check what's lit in it is what's marked, line comments, the lines of a block
    // comment after its first and nothing in a string or a name
    private void TestTodosIn(HexPointerScript test, string name, string sample)
    {
        var (text, wanted) = UnmarkTodos(sample);
        string file = Path.Combine(testFolder, name);
        test.Run(() =>
        {
            File.WriteAllText(file, text);
            Open(file);
        });
        test.WaitFor(() => TodosLitIn(file) is { } lit && lit.SequenceEqual(wanted), 5.0f);
        test.Check($"todos: {name} has its comments lit, line and block, and not its strings or names", () =>
        {
            if (TodosLitIn(file) is { } lit && lit.SequenceEqual(wanted))
                return true;

            string Listed(IEnumerable<(int Line, int From, int To, bool Urgent)> todos) => string.Join(" ", todos.Select(todo => $"{todo.Line}:{todo.From}"));
            Console.WriteLine($"[Hex self-test] todos: {name} wanted {Listed(wanted)} and has {(TodosLitIn(file) is { } has ? Listed(has) : "it not open")}");
            return false;
        });
        CloseTodos(test, file);
    }

    // What's lit in the open file, every line's in order, null when the file open isn't this one
    private List<(int Line, int From, int To, bool Urgent)>? TodosLitIn(string file)
    {
        if (document.Path is not { } open || !PlatformPaths.Same(open, file))
            return null;

        var lit = new List<(int Line, int From, int To, bool Urgent)>();
        for (int line = 0; line < code.Document.LineCount; line++)
            lit.AddRange(code.TodosAt(line).OrderBy(todo => todo.From).Select(todo => (line, todo.From, todo.To, todo.Urgent)));
        return lit;
    }

    private void CloseTodos(HexPointerScript test, string file)
    {
        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
