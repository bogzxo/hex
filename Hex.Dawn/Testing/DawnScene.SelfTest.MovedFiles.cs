namespace Hex.Dawn;

// A file that moves while it's open: the language servers are told, the old path closed and the new one opened. Save As
// and a rename in the explorer both only change the document's path, so that is what's done here.
internal sealed partial class DawnScene
{
    private void TestMovedFiles(HexPointerScript test)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"hex-moved-{System.Environment.ProcessId}");
        string first = Path.Combine(folder, "first.cs"), second = Path.Combine(folder, "second.cs");
        HexDocument? opened = null;

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(first, ["namespace Moved;", "", "public static class First", "{", "}"]);
            Open(first);
            opened = document;
        });
        test.Settle(0.5f);
        test.Check("moved: an open file is known to the languages by its path", () =>
            opened is { } file && string.Equals(languages.KnownPath(file), file.Path, StringComparison.Ordinal));

        object? before = null;
        test.Run(() =>
        {
            before = languages.LinterOf(opened!);
            File.Move(first, second);
            opened!.Path = second;
        });
        test.Settle(0.5f);
        test.Check("moved: moved, it's known by the new path the tick after", () =>
            string.Equals(languages.KnownPath(opened!), second, StringComparison.Ordinal));
        test.Check("moved: and a file with a server still has one, opened again under the new path", () =>
            before is null || languages.LinterOf(opened!) is not null);

        test.Run(() => opened!.Path = null);
        test.Settle(0.3f);
        test.Check("moved: its file gone, no server has it any more", () =>
            languages.KnownPath(opened!) is null && languages.LinterOf(opened!) is null);

        test.Run(() =>
        {
            opened!.Modified = false;
            Show(opened);
            CloseNow();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        });
        test.Settle(0.3f);
    }
}
