using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the scratchpads (DawnScene.Scratchpads). One made in a language, closed without a question and kept,
// an empty one gone when it's closed and swept when it isn't open, the list naming them by their first lines, and one
// moved out into a real file.
internal sealed partial class DawnScene
{
    private void TestScratchpads(HexPointerScript test)
    {
        string scratch = Path.Combine(testFolder, SCRATCH_FOLDER), moved = Path.Combine(testFolder, "shopping-list.py");
        ListBox Chooser() => Descendants(textDialog!.Body).OfType<ListBox>().First();

        // One in Python, its extension and its colours
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
            File.Delete(moved);
            NewScratchpad(".py");
        });
        test.Wait();
        test.Check("scratchpads: one made in a language has its extension and is coloured as it", () =>
            document.Path is { } path && Path.GetFileName(path) == "scratch 1.py" && IsScratchpad(document) && CodeLanguages.LanguageIdOf(code.Highlighter) == "python");

        // Something typed and closed, no question and it's in its file
        test.Run(() =>
        {
            code.Focus();
            code.Document.Insert("# Shopping list\nmilk = 2\n");
            Close();
        });
        test.Wait();
        test.Check("scratchpads: closing one with something in it asks nothing and keeps it", () =>
            chrome.Dialog is not { IsOpen: true } && OpenScratchpadAt(Path.Combine(scratch, "scratch 1.py")) is null
            && File.ReadAllText(Path.Combine(scratch, "scratch 1.py")).StartsWith("# Shopping list", StringComparison.Ordinal));

        // An empty one closed is gone, and an empty one lying about is swept by the list
        test.Run(() =>
        {
            NewScratchpad();
            Close();
            File.WriteAllText(Path.Combine(scratch, "scratch 7.txt"), "  \n");
            OpenScratchpad();
        });
        test.Wait();
        test.Check("scratchpads: an empty one closed is deleted, numbered after the ones there", () => !File.Exists(Path.Combine(scratch, "scratch 2.txt")));
        test.Check("scratchpads: an empty one that isn't open is swept away by the list", () => !File.Exists(Path.Combine(scratch, "scratch 7.txt")));
        test.Check("scratchpads: the list names one by its first line, without the # in front", () =>
            textDialog is { IsOpen: true } && Chooser() is { Items.Length: 1 } list && list.Items[0].StartsWith("Shopping list", StringComparison.Ordinal));

        // Picked from the list it opens, then it's moved out into a real file
        test.Run(() =>
        {
            ListBox list = Chooser();
            list.Choose(0);
            list.OnActivated!(list.Items[0]);
        });
        test.Wait();
        test.Check("scratchpads: picked from the list it opens", () => document.Path is { } path && Path.GetFileName(path) == "scratch 1.py");
        test.Check("scratchpads: the name it's offered to move as comes from its first line", () => FileNameFrom(ScratchName(document.Path!)) == "shopping-list");
        test.Run(() => MoveScratchpad(document, Path.Combine(testFolder, "shopping-list")));
        test.Wait();
        test.Check("scratchpads: moved into the workspace it's a real file there, open, and gone from the scratch folder", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(moved) && !IsScratchpad(document)
            && !File.Exists(Path.Combine(scratch, "scratch 1.py")) && File.ReadAllText(moved).Contains("milk", StringComparison.Ordinal));
        test.Run(() =>
        {
            CloseDocument(document);
            File.Delete(moved);
        });
        test.Wait();
    }
}
