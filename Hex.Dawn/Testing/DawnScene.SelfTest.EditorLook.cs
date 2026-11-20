using System.Numerics;

using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, how the editor looks in every look Hex comes with and at 100, 150 and 200%. A few files open in tabs, one
// with changes, code with what's wrong, a breakpoint and what changed in its gutter, the hover of a mistake, the list of
// completions, the box over a call, the find bar and replace, the empty editor, a diff and two files side by side. It
// checks the parts stay inside the editor at every size, and with HEX_SELFTEST_SHOTS set it leaves a picture of each.
internal sealed partial class DawnScene
{
    private void TestEditorLook(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float sizeBefore = 1.0f;
        string folder = Path.Combine(testFolder, "editor-look");
        string main = Path.Combine(folder, "Program.cs"), layoutFile = Path.Combine(folder, "look.hor"), notes = Path.Combine(folder, "notes.md");
        string written = EditorLookCSharp();

        void Shot(string name)
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "editor-" + name + ".png"));
        }

        void ShotNow(string name)
        {
            test.Settle(0.35f);
            test.Run(() => Shot(name));
            test.Settle(0.5f);
        }

        test.Run(() =>
        {
            sizeBefore = compositor.Scale;
            Directory.CreateDirectory(folder);
            File.WriteAllText(main, written);
            File.WriteAllText(layoutFile, "// The look of a panel, its sizes and colours\nlet look = {\n    title: \"Panel\",\n    text_scale: 0.14,\n    padding: vec(8, 4, 8, 4),\n    size: vec(120, 28),\n    tint: vec(0.9, 0.5, 0.2, 1),\n};\n\nlet wide = look.size.x * 2;\n\nlet square = vec(28, 28);\n");
            File.WriteAllText(notes, "# Notes\n\nThe editor, in every look.\n");
            settings.Minimap = true;
            CloseAll();
        });
        test.Settle(0.3f);

        (HexLook Look, float[] Sizes)[] runs =
        [
            (HexLooks.Solstice, [1.0f, 1.5f, 2.0f]),
            (HexLooks.SolsticeDay, [1.0f, 1.5f, 2.0f]),
            (HexLooks.VisualStudio, [1.0f, 1.5f, 2.0f]),
            (HexLooks.Graphite, [1.0f]),
            (HexLooks.Fluid, [1.0f]),
            (HexLooks.Frosted, [1.0f]),
            (HexLooks.Flat, [1.0f]),
        ];

        foreach (var (look, sizes) in runs)
        {
            string lookName = look.Name.ToLowerInvariant().Replace(' ', '-');
            test.Run(() => PickLook(look));
            test.Settle(1.5f);

            foreach (float size in sizes)
            {
                string tag = $"{lookName}-{(int)(size * 100)}";
                test.Run(() => SetInterfaceSize(size, remember: false));
                test.Settle(0.4f);

                // The empty editor, nothing open
                test.Run(() => CloseAll());
                ShotNow(tag + "-empty");

                // Three files in tabs, the C# one on screen with changes, its gutter and squiggles
                test.Run(() =>
                {
                    Open(notes);
                    Open(layoutFile);
                    Open(main);
                    PinTab(documents.First(open => open.Path is { } path && PlatformPaths.Same(path, notes)), true);
                    code.Text = written.Replace("int total = 0;", "int total = 1;", StringComparison.Ordinal);
                    code.GoTo(EDITOR_LOOK_MIDDLE, 8);
                    code.Diagnostics =
                    [
                        new(EDITOR_LOOK_MIDDLE + 2, 12, 5, "CS0103: The name 'totel' does not exist in the current context"),
                        new(EDITOR_LOOK_MIDDLE + 3, 12, 5, "CS0168: The variable 'spare' is declared but never used", Warning: true),
                        new(EDITOR_LOOK_MIDDLE + 4, 12, 4, "IDE0059: Unnecessary assignment of a value", Warning: true, Hint: true),
                    ];
                    code.Breakpoints = new HashSet<int> { EDITOR_LOOK_MIDDLE - 1 };
                    code.LineChanges = [new(EDITOR_LOOK_MIDDLE - 3, 1, UILineChangeKind.Modified), new(EDITOR_LOOK_MIDDLE + 5, 2, UILineChangeKind.Added), new(EDITOR_LOOK_MIDDLE + 8, 0, UILineChangeKind.Deleted)];
                });
                ShotNow(tag + "-code");
                test.Check($"editor look: {tag} the tabs fit their strip and the code is under them", () =>
                    docTabs.Tabs.Count == 3 && docTabs.Selected == 2 && code.CodeBounds.Width > 100.0f);
                test.Check($"editor look: {tag} the code keeps most of the editor, the minimap stepping aside in a narrow one", () =>
                    code.CodeBounds.Width >= code.Bounds.Width * 0.45f && (code.MinimapShown || code.Bounds.Width < 480.0f));
                test.Check($"editor look: {tag} a pinned tab is at the front with its pin", () =>
                    docTabs.Tabs[0].Pinned && documents[0].Pinned && !docTabs.Tabs[1].Pinned && docTabs.Tabs[0].Title == "notes.md");

                // The hover of the mistake, the pointer resting on it
                test.Hover(() => code.Module is { } module ? module.ToWorld(code.PlaceOf(new(EDITOR_LOOK_MIDDLE + 2, 14))) : default);
                test.Settle(1.2f);
                ShotNow(tag + "-hover");
                test.Check($"editor look: {tag} the hover of a mistake is a card, how bad it is in its colour, its code apart from what it says", () =>
                    code.TooltipMarkdown && code.Tooltip.StartsWith("[color:error][b]" + Said("code.diagnostic_error") + "[/b][/color]", StringComparison.Ordinal)
                    && code.Tooltip.Contains("CS0103", StringComparison.Ordinal) && code.Tooltip.Contains("\nThe name 'totel'", StringComparison.Ordinal));
                test.Hover(() => chrome.ToWorld(chrome.Frame.Max - new Vector2(4, 4)));

                // The hover of a name, what a server says of it, at the caret
                test.Run(() =>
                {
                    code.Focus();
                    code.GoTo(EDITOR_LOOK_MIDDLE + 7, 22);
                    code.ShowHoverAtCaret("```csharp\nvoid Console.WriteLine(string? value)\n```\n---\nWrites the specified string value, followed by the current line terminator, to the standard output stream.\n\n**value**: The value to write.");
                });
                ShotNow(tag + "-hover-doc");
                test.Run(() => code.GoTo(EDITOR_LOOK_MIDDLE, 8));

                // Completions, Ctrl+Space on a word started, in the look's code
                test.Run(() =>
                {
                    Open(layoutFile);
                    code.Focus();
                    code.GoTo(12, 0);
                    code.Document.Insert("let more = ve");
                    code.Complete();
                });
                test.Settle(0.6f);
                ShotNow(tag + "-completion");
                test.Run(() =>
                {
                    code.CloseCompletion();
                    code.Document.Undo();
                });

                // The box over a call
                test.Run(() =>
                {
                    int line = 4;
                    code.Focus();
                    code.GoTo(line, code.Document.LineAt(line).IndexOf("vec(", StringComparison.Ordinal) + 7);
                    code.ShowSignature();
                });
                ShotNow(tag + "-signature");

                // Find and replace over the code
                test.Run(() =>
                {
                    Open(main);
                    codeEditing.FindInFile();
                    TypeInto(codeEditing.findBox, "total");
                });
                PressKeys(test, "Ctrl+H");
                ShotNow(tag + "-find");
                test.Check($"editor look: {tag} the find bar is inside the code", () =>
                    codeEditing.FindBarOpen && codeEditing.findBar.Bounds.Max.X <= code.CodeBounds.Max.X + 1.0f
                    && codeEditing.findBar.Bounds.Min.X >= codeScroll.Bounds.Min.X - 1.0f);
                test.Run(codeEditing.CloseFindBar);

                // Side by side, then the diff of what changed
                if (size == 1.0f || look == HexLooks.Solstice)
                {
                    test.Run(() => OpenToSide(documents.First(open => open.Path is { } path && PlatformPaths.Same(path, layoutFile))));
                    ShotNow(tag + "-split");
                    test.Run(Unsplit);
                    test.Run(() =>
                    {
                        Open(main);
                        CompareWithSaved();
                    });
                    test.WaitFor(() => document.DiffOf is not null, 4.0f);
                    ShotNow(tag + "-diff");
                    test.Run(() =>
                    {
                        if (document.DiffOf is not null)
                            CloseNow();
                    });
                }

                test.Run(() =>
                {
                    foreach (HexDocument open in documents.ToList())
                    {
                        if (open.Path is { } path && PlatformPaths.Same(path, main))
                        {
                            Show(open);
                            code.Text = written;
                        }
                    }
                    CloseAll(keepPinned: true);
                });
                test.Settle(0.3f);
                test.Check($"editor look: {tag} Close All leaves the pinned tab open", () =>
                    documents.Count == 1 && documents[0].Pinned && docTabs.Tabs is [{ Pinned: true }]);
                test.Run(() =>
                {
                    PinTab(documents[0], false);
                    CloseAll();
                });
                test.Settle(0.3f);
            }
        }

        test.Run(() =>
        {
            CloseAll();
            SetInterfaceSize(sizeBefore, remember: false);
            settings.EditorSkin = skinBefore;
            UseEditorTheme(themeBefore, save: false);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        });
        test.Settle(0.5f);
    }

    private const int EDITOR_LOOK_MIDDLE = 14;

    // A short class with a method long enough to fill the screen, the sticky lines and the minimap having something to show
    private static string EditorLookCSharp()
    {
        var text = new System.Text.StringBuilder("using System;\n\nnamespace Look;\n\n/// <summary>Adds things up.</summary>\npublic sealed class Totals\n{\n    private readonly int[] values = [1, 2, 3];\n\n    public int Sum(int start, string name)\n    {\n        int total = 0;\n        foreach (int value in values)\n        {\n");
        text.Append("            total += value;\n            total += start;\n            total += totel;\n            int spare;\n            name = name;\n            total += name.Length;\n            \n            Console.WriteLine($\"{name}: {total}\");\n");
        for (int i = 0; i < 60; i++)
            text.Append($"            total += {i} * value; // step {i}\n");
        text.Append("        }\n\n        return total;\n    }\n}\n");
        return text.ToString();
    }
}
