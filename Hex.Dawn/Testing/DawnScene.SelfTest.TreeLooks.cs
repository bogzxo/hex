using Hex.Core.Git;
using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer and the Properties of a file beside it in every look Hex comes with at 100, 150 and 200%. A repository
// made for it with a file changed, one nobody added, an ignored build, a chain of folders with one folder in each and
// a problem in a file, the changed file open and picked, its Properties up on the right. HEX_SELFTEST_SHOTS names a
// folder for a picture of each, and of several files picked, a folder picked and the explorer with no workspace.
internal sealed partial class DawnScene
{
    // Whether one thing is over another on screen, its bottom at or over the other's top
    private static bool HexAbove(UIRect upper, UIRect lower) => upper.Min.Y >= lower.Max.Y - 0.5f;

    private string TreeLooksFolder => Path.Combine(testFolder, "tree_looks");

    private void TestTreeLooks(HexPointerScript test)
    {
        string repo = TreeLooksFolder;
        string changed = Path.Combine(repo, "src", "Explorer.cs"), fresh = Path.Combine(repo, "src", "Properties.cs");
        string deep = Path.Combine(repo, "assets", "ui", "flat", "skin.hor"), readme = Path.Combine(repo, "README.md");
        string themes = Path.Combine(repo, "assets", "ui", "flat");
        bool git = GitProcess.Installed;
        float scaleBefore = 1.0f;
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;

        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;

        void Shot(string name)
        {
            // A look or a scale changed takes a few frames to be drawn at, a picture taken too soon is of an empty canvas
            test.Run(() => Engine.WindowManager.RequestFrames());
            test.Settle(1.0f);
            test.Run(() => Engine.WindowManager.RequestFrames());
            test.Settle(0.5f);
            test.Run(() =>
            {
                if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                    Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));
            });
            test.Settle(0.5f);
        }

        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            DeleteGitFolder(repo);
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            Directory.CreateDirectory(themes);
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            File.WriteAllText(changed, "namespace Sample;\n\ninternal sealed class Explorer\n{\n}\n");
            File.WriteAllText(Path.Combine(repo, "src", "Tree.cs"), "namespace Sample;\n");
            File.WriteAllText(deep, "let skin = {};\n");
            File.WriteAllText(readme, "# Sample\n\nA workspace for the explorer's look.\n");
            File.WriteAllText(Path.Combine(repo, "docs", "guide.md"), "# Guide\n");
            File.WriteAllText(Path.Combine(repo, "settings.json"), "{ \"a\": 1 }\n");
            File.WriteAllText(Path.Combine(repo, "logo.png"), "png");
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\n");
            if (git)
            {
                Git(repo, "init", "-q", "-b", "main");
                Git(repo, "config", "user.name", "Hex self test");
                Git(repo, "config", "user.email", "selftest@example.com");
                Git(repo, "add", ".");
                Git(repo, "commit", "-q", "-m", "The sample workspace");
            }

            // Then worked on, a file changed, one nobody added and a build git ignores
            File.WriteAllText(changed, "namespace Sample;\n\ninternal sealed class Explorer\n{\n    // rows\n}\n");
            File.WriteAllText(fresh, "namespace Sample;\n");
            Directory.CreateDirectory(Path.Combine(repo, "bin"));
            File.WriteAllText(Path.Combine(repo, "bin", "Sample.dll"), "built");

            explorer.OpenFolder(repo);
            explorer.workspace.Expanded.Add(Path.Combine(repo, "src"));
            explorer.workspace.Expanded.Add(Path.Combine(repo, "assets"));
            explorer.workspace.Expanded.Add(Path.Combine(repo, "assets", "ui"));
            explorer.workspace.Expanded.Add(themes);
            Open(changed);
        });
        test.Wait();
        test.Run(() =>
        {
            ShowSide(HexSide.Explorer);
            treeDirty = true;
        });
        test.WaitFor(() => Row(changed) is not null && Row(deep) is not null, 5.0f);
        if (git)
            test.WaitFor(() => Row(changed)?.Badge is { Length: > 0 }, 5.0f);
        test.Run(() => ShowPropertiesOf(null));
        test.WaitFor(() => properties.Shows == HexPropertiesShows.Files && properties.Shown.Count == 1, 10.0f);
        test.Check("tree looks: the open file is picked in the explorer and its Properties are up", () =>
            sideTree.Selected == Row(changed) && right.Shown && properties.Page.Visible);

        // A chain of folders with only a folder in each is one row, the folders of it have no rows of their own
        test.Check("tree looks: assets, ui and flat are one row, assets/ui/flat, with skin.hor under it", () =>
            Row(themes) is { Text: "assets/ui/flat" } chain && Row(Path.Combine(repo, "assets")) is null
            && Row(Path.Combine(repo, "assets", "ui")) is null && Row(deep)?.Parent == chain);
        test.Check("tree looks: a folder's name is in the text's colour and its icon the text's turned down, the workspace's folder bold", () =>
            Row(Path.Combine(repo, "docs")) is { Color: null } docs && docs.IconColor == FolderColor && Row(repo) is { Bold: true });
        if (git)
        {
            test.Check("tree looks: a folder with a change under it has a dot in the colour of the change, and its name in it", () =>
                Row(Path.Combine(repo, "src")) is { Dot: { } dot } src && src.Color == dot && Row(Path.Combine(repo, "docs"))?.Dot is null);
        }

        // An error in a file, its name in the error's colour with how many there are before its letter, a dot on every folder over it
        var problem = new HexProblem(Path.Combine(repo, "src", "Tree.cs"), 0, 0, "the tree has no rows", false);
        IReadOnlyList<HexProblem> Problems() => [problem];
        test.Run(() =>
        {
            problemSources.Add(Problems);
            problemsDirty = true;
        });
        test.WaitFor(() => Row(problem.Path)?.Badge == "1", 3.0f);
        test.Check("tree looks: a file with an error is in the error's colour with 1 at its end, and its folder has the error's dot", () =>
            Row(problem.Path) is { Badge: "1" } wrong && wrong.Color == ErrorColor && wrong.BadgeColor == ErrorColor
            && Row(Path.Combine(repo, "src"))?.Dot == ErrorColor && Row(repo)?.Dot == ErrorColor);

        // The head of the Properties, what the file is before the rows
        if (git)
            test.WaitFor(() => properties.HeadShown.Git.Length > 0, 5.0f);
        test.Check("tree looks: the Properties start with the file's name, where it is in the workspace and its buttons", () =>
            properties.HeadShown is { Name: "Explorer.cs", Where: "src", Actions: true } shown && (!git || shown.Git == Said("file_properties.git_modified"))
            && HexAbove(properties.HeadBounds, properties.Page.Bounds));


        // The first change of look of a run is the slowest to be drawn at
        test.Run(() => PickLook(HexLooks.Solstice));
        test.Settle(1.5f);

        foreach (HexLook look in HexLooks.BuiltIn)
        {
            foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
            {
                string named = $"{look.Name} at {scale * 100:0}%";
                test.Run(() =>
                {
                    PickLook(look);
                    SetInterfaceSize(scale, remember: false);
                });
                test.Settle(0.3f);
                test.Check($"tree looks: {named}, the explorer and Properties both have room and fit the window", () =>
                    sideTree.Visible && sideTree.Bounds.Width > 0 && properties.Page.Bounds.Width > 0 && Fits());
                test.Check($"tree looks: {named}, the side bar's title ends before the explorer's buttons", () =>
                    treeTitle.Parent!.Children.Where(each => each != treeTitle && each.Visible).All(button => treeTitle.Bounds.Max.X <= button.Bounds.Min.X + 0.5f));
                Shot($"tree-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}");
            }
        }

        test.Run(() =>
        {
            SetInterfaceSize(scaleBefore, remember: false);
            PickLook(HexLooks.Solstice);
        });
        test.Settle(0.3f);

        // Several files picked, then a folder
        test.Run(() => sideTree.SelectMany([.. new[] { changed, fresh }.Select(Row).OfType<UITreeItem>()]));
        test.WaitFor(() => properties.Shown.Count == 2, 5.0f);
        test.Check("tree looks: two files picked say so in the head and where they are, with no name row of their own", () =>
            properties.HeadShown is { Name: "2 files" } both && both.Where == Said("file_properties.all_in", ("folder", "src"))
            && properties.Page.RowBounds("name") is null);
        Shot("tree-several");
        test.Run(() => sideTree.SelectMany([.. new[] { Path.Combine(repo, "src") }.Select(Row).OfType<UITreeItem>()]));
        test.WaitFor(() => properties.Shown.Count == 1 && Directory.Exists(properties.Shown[0]), 5.0f);
        test.Settle(0.5f);
        Shot("tree-folder");
        test.Run(() =>
        {
            problemSources.Remove(Problems);
            problemsDirty = true;
        });

        // No workspace at all
        test.Run(explorer.CloseWorkspace);
        test.Wait();
        test.Run(() =>
        {
            ShowSide(HexSide.Explorer);
            treeDirty = true;
        });
        test.Settle(0.3f);
        test.Check("tree looks: with no workspace the explorer says what it's for and has Open Folder and Open Workspace as buttons", () =>
            sideTree.Items.Count > 2 && sideTree.Items[1] is { Button: true } folder && folder.Text == Said("side_tree.open_folder")
            && sideTree.Items[2] is { Button: true } workspace && workspace.Text == Said("side_tree.open_workspace"));
        Shot("tree-empty");

        test.Run(() =>
        {
            UseEditorSkin(skinBefore, themeBefore, save: false);
            properties.Follow(null);
            CloseAll();
        });
        test.Wait();
    }
}
