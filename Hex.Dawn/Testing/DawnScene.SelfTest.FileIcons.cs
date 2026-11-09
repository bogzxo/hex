using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestFileIcons(HexPointerScript test)
    {
        string[] files = ["Program.cs", "main.c", "main.cpp", "app.py", "index.js", "index.ts", "Component.jsx", "Component.tsx",
            "main.go", "lib.rs", "Main.java", "main.kt", "App.swift", "script.lua", "script.rb", "index.php",
            "index.html", "style.css", "style.scss", "App.vue", "App.svelte", "main.dart", "build.ps1", "build.sh",
            "Dockerfile", "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "settings.yaml", "data.json", "layout.hor",
            "README.md", "image.png", "font.ttf", "archive.zip", "deploy.sh.orig", "unknown.xyzunknown"];
        WindowPanel window = null!;
        test.Run(() =>
        {
            window = chrome.AddComponent(new WindowPanel("File-type icons")
            {
                Anchor = Origin.Center, Pivot = Origin.Center, Size = new Vector2(920, 660), ResizeGrip = false
            });
            for (int column = 0; column < 2; column++)
            {
                window.Add(new TreeView
                {
                    Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(12 + column * 450, -8), Size = new Vector2(430, 590),
                    Items = files.Skip(column * 19).Take(19).Select(file => new UITreeItem
                    {
                        Text = file, Icon = HexExplorer.FileIcon(file), IconColor = explorer.FileIconColor(file)
                    }).ToList()
                });
            }
        });
        test.Settle(0.3f);
        test.Check("file icons: every displayed file kind resolves to a loaded glyph", () =>
            files.All(file => UISymbols.TryGet(HexExplorer.FileIcon(file), out _)));
        test.Check("file icons: common languages have distinctive glyphs", () =>
            files.Take(16).Select(HexExplorer.FileIcon).Distinct().Count() >= 14);
        test.Check("file icons: backups keep their source icon and unknown extensions keep the generic file", () =>
            HexExplorer.FileIcon("deploy.sh.orig") == HexExplorer.FileIcon("deploy.sh") && HexExplorer.FileIcon("unknown.xyzunknown") == "file");
        test.Run(() => Shot("file-type-icons"));
        test.Settle(0.3f);
        test.Run(() => chrome.RemoveComponent(window));
    }
}
