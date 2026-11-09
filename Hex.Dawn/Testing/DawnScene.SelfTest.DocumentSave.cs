using System.Text;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDocumentSave(HexPointerScript test)
    {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            string file = Path.Combine(testFolder, "encoded-" + encoding.CodePage + ".txt");
            string copy = Path.Combine(testFolder, "encoded-copy-" + encoding.CodePage + ".txt");
            const string before = "before\r\n", after = "after \u03B1 \U0001F680\r\n";
            test.Run(() =>
            {
                File.WriteAllText(file, before, encoding);
                Open(file);
                document.Editor.Document.Replace(after.Replace("\r\n", "\n"));
                SaveTo(copy);
            });
            test.Check($"save integrity: Save As retains {encoding.WebName} and its BOM", () =>
                File.ReadAllBytes(copy).AsSpan().SequenceEqual([.. encoding.GetPreamble(), .. encoding.GetBytes(after)]) &&
                File.ReadAllText(file) == before);
            test.Run(() =>
            {
                document.Editor.Document.Replace("changed again\n");
                SaveTo(copy);
            });
            test.Check($"save integrity: subsequent save retains {encoding.WebName}", () =>
                File.ReadAllBytes(copy).AsSpan().SequenceEqual([.. encoding.GetPreamble(), .. encoding.GetBytes("changed again\r\n")]) &&
                !document.Modified);
            test.Run(CloseNow);
        }
        if (OperatingSystem.IsWindows())
        {
            string file = Path.Combine(testFolder, "locked-save.txt");
            test.Run(() =>
            {
                File.WriteAllText(file, "original");
                Open(file);
                document.Editor.Document.Replace("unsaved edit");
                document.Modified = true;
                using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                SaveTo(file);
            });
            test.Check("save integrity: failed replace keeps disk bytes and unsaved editor text", () =>
                File.ReadAllText(file) == "original" && document.Editor.Text == "unsaved edit" && document.Modified);
            test.Run(() => { document.Modified = false; CloseNow(); });
        }
    }
}
