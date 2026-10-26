using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of Emmet, the built-in extension that writes HTML out of an abbreviation. Tab in an .html file is handed
// to it before it indents (the code editor's OnTab, which is what Tab calls), the abbreviation goes and its HTML comes in
// as one step to undo with the caret in the first empty element, and Tab after a word that isn't an element still indents.
internal sealed partial class DawnScene
{
    private void TestEmmet(HexPointerScript test)
    {
        string page = Path.Combine(testFolder, "emmet", "page.html");
        const string WRITTEN = "<body>\n    ul>li.item$*2\n</body>\n";
        bool took = false;

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(page)!);
            File.WriteAllText(page, WRITTEN);
            Open(page);
        });
        test.Settle(0.3f);
        test.Check("emmet: it's a built-in extension, on", () =>
            extensions.All.Any(extension => extension is { Id: "emmet", BuiltIn: true, Enabled: true, Failed: null }));

        test.Run(() =>
        {
            document.Editor.Document.MoveTo(new UITextPosition(1, 17));
            took = document.Editor.OnTab?.Invoke() == true;
        });
        test.Check("emmet: Tab after an abbreviation writes out its HTML, indented like its line", () =>
            took && document.Editor.Text == "<body>\n    <ul>\n        <li class=\"item1\"></li>\n        <li class=\"item2\"></li>\n    </ul>\n</body>\n");
        test.Check("emmet: with the caret in the first empty element", () => document.Editor.Document.Caret == new UITextPosition(2, 26));
        test.Check("emmet: and the file has a change to save", () => document.Modified);

        test.Run(() => document.Editor.Document.Undo());
        test.Check("emmet: one undo puts the abbreviation back", () => document.Editor.Text == WRITTEN);

        test.Run(() =>
        {
            document.Editor.Document.Replace("<p>Hello\n");
            document.Editor.Document.MoveTo(new UITextPosition(0, 8));
            took = document.Editor.OnTab?.Invoke() == true;
        });
        test.Check("emmet: after a word that isn't an element Tab is still a Tab", () => !took && document.Editor.Text == "<p>Hello\n");

        test.Run(() =>
        {
            Save();
            CloseDocument(document);
        });
        test.Wait();
    }
}
