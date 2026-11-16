using System.Numerics;

namespace Hex.Dawn;

// Two presses on a word in the code pick the word and a third straight after picks its line, the way every editor has it.
internal sealed partial class DawnScene
{
    private void TestTripleClick(HexPointerScript test)
    {
        string file = Path.Combine(Path.GetTempPath(), $"hex-triple-{System.Environment.ProcessId}.txt");
        Vector2 OnWord() => code.Module is { } module ? module.ToWorld(code.PlaceOf(new UITextPosition(1, 8)) + new Vector2(2.0f, -4.0f)) : Vector2.Zero;

        test.Run(() =>
        {
            File.WriteAllLines(file, ["the first line", "second line of three", "and the last"]);
            Open(file);
        });
        test.Settle(0.5f);

        test.Click(OnWord);
        test.Click(OnWord);
        test.Check("triple click: two presses pick the word under the pointer", () =>
            code.Document.SelectedText == "line" && code.Document.Caret.Line == 1);
        test.Click(OnWord);
        test.Check("triple click: a third straight after picks the whole line, to the start of the next", () =>
            code.Document.SelectedText == "second line of three\n"
            && code.Document.Anchor == new UITextPosition(1, 0) && code.Document.Caret == new UITextPosition(2, 0));

        // A press somewhere else, once, is a caret again and nothing of the three is left over
        test.Settle(0.6f);
        test.Click(OnWord);
        test.Check("triple click: and one press on its own later is only a caret", () => !code.Document.HasSelection);

        test.Run(() =>
        {
            CloseNow();
            try { File.Delete(file); } catch (IOException) { }
        });
        test.Settle(0.3f);
    }
}
