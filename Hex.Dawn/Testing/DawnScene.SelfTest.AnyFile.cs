namespace Hex.Dawn;

// The self test of Dawn as an editor of any file, not only HIDL. A C# file, a Markdown one and a JSON one opened, each
// with an outline of its own language, an icon and a colour by what it is, and the values side folded away while
// there are no values to show, back for a file of HIDL.
internal sealed partial class DawnScene
{
    private void TestAnyFile(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "any_file");
        string cs = Path.Combine(folder, "Thing.cs"), md = Path.Combine(folder, "notes.md"), json = Path.Combine(folder, "data.json");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(cs, "namespace Things;\n\npublic sealed class Thing\n{\n    public int Count { get; set; }\n\n    public void Grow(int by)\n    {\n        if (by > 0) Count += by;\n    }\n}\n\ninternal enum Kind { One, Two }\n");
            File.WriteAllText(md, "# Notes\n\nSome text.\n\n## Under it\n\n```\n# not a heading\n```\n\n### Deeper\n");
            File.WriteAllText(json, "{\n  \"name\": \"dawn\",\n  \"version\": 3,\n  \"inner\": {\n    \"deep\": true\n  }\n}\n");
            Open(cs);
        });
        test.Settle(0.3f);
        test.Check("any file: a C# file has an outline of its classes, members and enums", () =>
            OutlineHere() is var entries && entries.Any(entry => entry is { Name: "Thing", Icon: "symbol-class" })
            && entries.Any(entry => entry is { Name: "Count", Icon: "symbol-property", Depth: 1 })
            && entries.Any(entry => entry is { Name: "Grow", Icon: "symbol-method" }) && entries.Any(entry => entry is { Name: "Kind", Icon: "symbol-enum" })
            && !entries.Any(entry => entry.Name == "if"));
        test.Check("any file: and the values side is folded away, there are no values", () => !right.Shown);
        test.Check("any file: a D file's outline is its types and functions, not what's called in them", () =>
            HexOutline.Outline("module app;\n\nstruct Point\n{\n    int lengthSquared() const { return 1; }\n}\n\nT twice(T)(T x) { return x * 2; }\n\nvoid main()\n{\n    if (true)\n        writeln(twice(2));\n}\n", "app.d") is var entries
            && entries.Any(entry => entry is { Name: "Point", Icon: "symbol-struct", Depth: 0 })
            && entries.Any(entry => entry is { Name: "lengthSquared", Icon: "symbol-method", Depth: 1 })
            && entries.Any(entry => entry is { Name: "twice", Icon: "symbol-method" }) && entries.Any(entry => entry is { Name: "main", Icon: "symbol-method" })
            && !entries.Any(entry => entry.Name is "if" or "writeln"));
        test.Check("any file: its icon and its colour say what it is", () => HexExplorer.FileIcon(cs) == "symbol-class" && explorer.FileIconColor(cs) is not null && HexExplorer.FileIcon(md) == "markdown");

        test.Run(() => Open(md));
        test.Settle(0.3f);
        test.Check("any file: Markdown's outline is its headings, none out of a fence", () =>
            OutlineHere() is var entries && entries.Select(entry => (entry.Name, entry.Depth)).SequenceEqual([("Notes", 0), ("Under it", 1), ("Deeper", 2)]));

        // The preview of Markdown, offered for a Markdown file and nothing else, following its text
        test.Check("any file: a Markdown file is offered a preview", () => ViewsFor(document).Any(kind => kind.Id == "markdown_preview.preview"));
        test.Run(() => SwitchToExtensionView("markdown_preview.preview"));
        test.Settle(0.3f);
        test.Check("any file: the preview draws it as it reads", () =>
            ShownView is { } shown && shown.Made.Get<Horizon.UI.Components.Label>("text") is { Markdown: true } label && label.Text.Contains("# Notes"));
        test.Run(() => code.Document.Insert("\n- a new line\n"));
        test.Settle(1.5f);
        test.Check("any file: and follows it as it is typed", () =>
            ShownView is { } shown && shown.Made.Get<Horizon.UI.Components.Label>("text").Text.Contains("- a new line"));
        test.Run(() => SwitchView(HexView.Code));

        test.Run(() => Open(json));
        test.Settle(0.3f);
        test.Check("any file: and nothing else is", () => !ViewsFor(document).Any(kind => kind.Id == "markdown_preview.preview"));
        test.Check("any file: JSON's is its keys", () =>
            OutlineHere() is var entries && entries.Any(entry => entry is { Name: "name", Depth: 0 }) && entries.Any(entry => entry is { Name: "deep", Depth: 1 }));

        test.Run(() => Open(dataFile));
        test.Settle(0.4f);
        test.Check("any file: a file of HIDL brings the values back", () => right.Shown == !settings.PropertiesHidden);
        // The designer is a built-in extension, switched off a layout is the HIDL it is, back on it's the canvas again
        string layoutFile = Path.Combine(folder, "menu.hor");
        HexExtension? designer = null;
        test.Run(() =>
        {
            File.WriteAllText(layoutFile, STAND_IN_LAYOUT);
            designer = extensions.All.FirstOrDefault(extension => extension.Native == HexDesignerKind.ID);
        });
        test.Check("any file: the designer is a built-in extension, on", () => designer is { BuiltIn: true, Enabled: true, Failed: null } && DesignerOn);
        test.Run(() =>
        {
            extensions.Switch(designer!, false);
            Open(layoutFile);
        });
        test.Settle(0.3f);
        test.Check("any file: switched off, a layout opens as its code", () => !DesignerOn && document.IsText && document.Path == layoutFile && !LayoutTools);
        test.Run(() =>
        {
            CloseDocument(document);
            extensions.Switch(designer!, true);
            Open(layoutFile);
        });
        test.Settle(0.3f);
        test.Check("any file: and on again, on its canvas", () => DesignerOn && !document.IsText && document.Path == layoutFile);
        test.Run(() => CloseDocument(document));
        test.Settle(0.2f);

        test.Run(() =>
        {
            foreach (string file in new[] { cs, md, json })
            {
                if (documents.Find(open => open.Path == file) is { } open)
                    CloseDocument(open);
            }
        });
        test.Settle(0.2f);
    }
}
