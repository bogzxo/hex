using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Every .hor of the engine's Assets and of Hex's own opened and saved with nothing changed, which has to leave the file
// exactly as it was, and a layout written by hand changed in the designer, which has to keep everything the designer
// can't write. Saving scripted_panel.hor once threw all four of its handlers away.
internal sealed partial class DawnScene
{
    private void TestLossless(HexPointerScript test)
    {
        string copies = Path.Combine(testFolder, "lossless");
        var files = new List<(string Name, string Copy)>();
        foreach (string root in LosslessRoots())
        {
            // Copied with the .hor files round them, so what one includes or fills itself in from is next to it
            string into = Path.Combine(copies, files.Count.ToString());
            foreach (string file in Directory.EnumerateFiles(root, "*.hor", SearchOption.AllDirectories).Order())
            {
                string relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                    continue;

                string copy = Path.Combine(into, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: true);
                files.Add((Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/'), copy));
            }
        }

        test.Check("lossless: there are .hor files to open", () => files.Count > 0);
        foreach (var (name, copy) in files)
        {
            byte[] bytes = File.ReadAllBytes(copy);
            string text = File.ReadAllText(copy).Replace("\r\n", "\n");
            bool opened = false;

            // A layout that doesn't build (the checks have one on purpose) is open as its code, which is the file all the same
            test.Run(() =>
            {
                Open(copy);
                opened = document.Path is { } at && Path.GetFullPath(at) == Path.GetFullPath(copy);
            });
            test.Check($"lossless: {name} opens with its code what the file says", () =>
                opened && document.GenerateCode() == text && document.Editor.Text == text && document.TakeTurnedDown() is null);
            test.Run(SaveFormatted);
            test.Check($"lossless: {name} saved with nothing changed is the same bytes", () => File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes));
            test.Run(() =>
            {
                chrome.Dialog?.Close();
                if (opened)
                    CloseNow();
            });
        }

        string? scripted = files.Find(file => file.Name.EndsWith("examples/ui/scripted_panel.hor", StringComparison.Ordinal)).Copy;
        if (scripted is not null)
        {
            string original = File.ReadAllText(scripted).Replace("\r\n", "\n");
            foreach (bool mixed in new[] { false, true })
            {
                string variant = Path.Combine(Path.GetDirectoryName(scripted)!, mixed ? "scripted-mixed.hor" : "scripted-bom.hor");
                var encoding = mixed ? (System.Text.Encoding)new System.Text.UnicodeEncoding(false, true) : new System.Text.UTF8Encoding(true);
                string[] lines = original.Split('\n');
                string text = string.Concat(lines.Select((line, i) => line + (i == lines.Length - 1 ? "" : mixed && i % 2 == 0 ? "\n" : "\r\n")));
                File.WriteAllText(variant, text, encoding);
                TestScriptedPanel(test, variant);
                string expected = text.Replace("label: \"Less\"", "label: \"Fewer\"").Replace("size: vec(360, 28)", "size: vec(400, 28)");
                test.Check($"lossless: designer edits retain {(mixed ? "mixed endings and UTF-16" : "CRLF and UTF-8 BOM")}", () =>
                    File.ReadAllBytes(variant).AsSpan().SequenceEqual([.. encoding.GetPreamble(), .. encoding.GetBytes(expected)]));
            }
        }
        TestScriptedPanel(test, scripted);
    }

    // The layout of the first UI example, four handlers written as funcs and one of them set after its declaration
    private void TestScriptedPanel(HexPointerScript test, string? copy)
    {
        if (copy is null)
        {
            test.Run(() => Console.WriteLine("[Hex self-test] scripted_panel.hor left out, the engine's Assets aren't over Hex"));
            return;
        }

        string text = File.ReadAllText(copy).Replace("\r\n", "\n");
        test.Run(() => Open(copy));
        test.Check("lossless: scripted_panel opens with its four handlers in its code", () =>
            document.KeepsCode && document.Editor.Text.Split("func(").Length == 5 && !document.Editor.Text.Contains("can't be written back"));

        // A label changed in the designer is that label changed in the file, and nothing else
        test.Run(() =>
        {
            designer.Select(document.Find("less")!);
            designer.Set(document.Find("less")!, "label", new StringValue("Fewer"));
            designer.Select(document.Find("level")!);
            designer.Set(document.Find("level")!, "size", new Vector2Value(new System.Numerics.Vector2(400, 28)));
            codeDirty = true;
        });
        test.Settle(0.3f);
        test.Run(SaveFormatted);
        test.Check("lossless: changed in the designer only what was changed is changed in the file", () =>
            File.ReadAllText(copy).Replace("\r\n", "\n") == text.Replace("label: \"Less\"", "label: \"Fewer\"").Replace("size: vec(360, 28)", "size: vec(400, 28)"));
        test.Check("lossless: and the code on screen is the file", () => document.Editor.Text == File.ReadAllText(copy).Replace("\r\n", "\n"));

        // Taking out what a handler uses can't be written without breaking the handler, it's turned down and put back
        string? why = null;
        string before = string.Empty;
        test.Run(() =>
        {
            before = File.ReadAllText(copy);
            document.Remove(document.Find("charge")!);
            document.GenerateCode();
            why = document.TakeTurnedDown();
        });
        test.Check("lossless: taking out what a handler uses is turned down and put back", () =>
            why is not null && why.Contains("charge") && document.Find("charge") is ProgressBar && document.GenerateCode() == before.Replace("\r\n", "\n"));
        test.Run(SaveFormatted);
        test.Check("lossless: and saving after leaves the file as it was", () => File.ReadAllText(copy) == before);
        test.Run(CloseNow);
    }

    // The Assets of the engine Hex is built against and Hex's own, the ones that are there
    private static IEnumerable<string> LosslessRoots()
    {
        string own = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(own))
            yield return own;

        string? engine = FindUpFolder(AppContext.BaseDirectory, "Horizon.sln") ?? (FindUpFolder(AppContext.BaseDirectory, Path.Combine("horizon", "Horizon.sln")) is { } over ? Path.Combine(over, "horizon") : null);
        if (engine is null)
            yield break;

        foreach (string assets in Directory.EnumerateDirectories(engine, "Assets", SearchOption.AllDirectories).Order())
        {
            string relative = Path.GetRelativePath(engine, assets);
            if (!relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" || part.StartsWith('.')) && !IsInsideAnother(assets))
                yield return assets;
        }
    }

    // An Assets folder inside of another one is gone through with that one
    private static bool IsInsideAnother(string assets) =>
        Path.GetDirectoryName(assets) is { } parent && parent.Split(Path.DirectorySeparatorChar).Contains("Assets");
}
