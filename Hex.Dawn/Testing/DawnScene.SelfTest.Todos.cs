namespace Hex.Dawn;

// The self test, TODO and FIXME lit up in comments, and nowhere else.
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

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
