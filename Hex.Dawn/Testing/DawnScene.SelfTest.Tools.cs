using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the catalog's editing extensions in Dawn itself, which is hex.selection, hex.select and hex.lines on
// the real editor. Text tools sorts the selected lines and keeps them selected, Snippets writes fn out in a .js file on
// Tab with its name selected and goes on to its arguments on the next Tab.
internal sealed partial class DawnScene
{
    private void TestTools(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "tools", "app.js");
        const string WRITTEN = "pear\napple\nfig\nlast\n";
        bool took = false;

        void Command(string name)
        {
            var (_, picked) = extensions.Commands().First(command => command.Text.StartsWith(name, StringComparison.Ordinal));
            picked();
        }

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, WRITTEN);
            Open(file);
        });
        test.Settle(0.3f);
        test.Check("tools: Text tools and Snippets are on", () =>
            extensions.All.Any(extension => extension is { Id: "text_tools", Enabled: true, Failed: null })
            && extensions.All.Any(extension => extension is { Id: "snippets", Enabled: true, Failed: null }));

        test.Run(() =>
        {
            document.Editor.Document.Select(new UITextPosition(0, 0), new UITextPosition(3, 0));
            Command("Text tools: Sort lines");
        });
        test.Check("tools: Sort lines sorts the selected lines and leaves the line the selection stops at", () =>
            document.Editor.Text == "apple\nfig\npear\nlast\n" && document.Editor.Document.SelectedText == "apple\nfig\npear");
        test.Check("tools: and it's one undo", () =>
        {
            document.Editor.Document.Undo();
            return document.Editor.Text == WRITTEN;
        });

        test.Run(() =>
        {
            document.Editor.Document.Replace("fn\n");
            document.Editor.Document.MoveTo(new UITextPosition(0, 2));
            took = document.Editor.OnTab?.Invoke() == true;
        });
        test.Check("tools: fn and Tab in a .js file is a function with its name selected", () =>
            took && document.Editor.Text == "function name() {\n    \n}\n" && document.Editor.Document.SelectedText == "name");
        test.Run(() =>
        {
            document.Editor.Document.Insert("go");
            took = document.Editor.OnTab?.Invoke() == true;
        });
        test.Check("tools: and the next Tab goes on to its arguments", () =>
            took && document.Editor.Text == "function go() {\n    \n}\n" && document.Editor.Document.Caret == new UITextPosition(0, 12));

        test.Run(() =>
        {
            Save();
            CloseDocument(document);
        });
        test.Wait();
    }
}
