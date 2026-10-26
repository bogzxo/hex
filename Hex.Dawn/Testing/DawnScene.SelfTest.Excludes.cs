using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of Editor, Search in the settings, part of the text settings' (see TestTextSettings). What the walk of a
// folder's files leaves out, venv, node_modules and bin by search_exclude and vendor by the folder's .gitignore, with
// .github/workflows found the way a VS Code developer expects. Use .gitignore off bringing vendor back, the explorer
// hiding only what files_exclude says, Find in Files saying when it stopped at its most, and the excludes a folder's to set
internal sealed partial class DawnScene
{
    private void TestExcludeSettings(HexPointerScript test)
    {
        string root = string.Empty;
        bool browsingWas = false;
        List<string> Listed() => [.. HexSearch.OpenableFilesUnder([root], _ => true).Select(file => HexSearch.RelativeTo([root], file))];
        bool InExplorer(string name) => explorer.explorerRows.Any(row => row.Path == Path.Combine(root, name));
        void Write(string relative, string text = "needle\n")
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        test.Run(() =>
        {
            root = Path.Combine(testFolder, "excludes");
            if (Directory.Exists(root))
                Directory.Delete(root, true);

            Write("src/main.cs");
            Write("src/build.txt");
            Write("build.txt");
            Write(".github/workflows/ci.yml");
            Write(".env", "KEY=1\n");
            Write("venv/lib/x.py");
            Write("vendor/v.txt");
            Write("node_modules/a.js");
            Write("bin/Debug/x.cs");
            Write(".git/config");
            Write("logs/app.log");
            Write("root.log");
            Write(".gitignore", "# what git leaves alone\nvendor/\n/build.txt\n");
            Write("sub/.gitignore", "*.tmp\n!keep.tmp\n");
            Write("sub/a.tmp");
            Write("sub/keep.tmp");
        });

        test.Check("excludes: quick open's walk leaves out venv, node_modules, bin, .git and vendor/ by the .gitignore, and finds .github/workflows/ci.yml", () =>
            Listed() is var listed && listed.Contains(".github/workflows/ci.yml") && listed.Contains(".env") && listed.Contains("src/main.cs")
            && !listed.Any(file => file.StartsWith("venv/") || file.StartsWith("vendor/") || file.StartsWith("node_modules/") || file.StartsWith("bin/") || file.StartsWith(".git/")));
        test.Check("excludes: /build.txt is only the top's, and a .gitignore further down goes for its own folder, *.tmp out and !keep.tmp back", () =>
            Listed() is var listed && !listed.Contains("build.txt") && listed.Contains("src/build.txt") && !listed.Contains("sub/a.tmp") && listed.Contains("sub/keep.tmp"));
        test.Check("excludes: Find in Files looks through the same files, nothing from venv or vendor", () =>
            HexSearch.FindIn([root], "needle", new UIFindOptions()).Select(found => HexSearch.RelativeTo([root], found.Path)).ToList() is var found
            && found.Contains(".github/workflows/ci.yml") && !found.Any(file => file.StartsWith("venv/") || file.StartsWith("vendor/")));
        test.Check("excludes: the globs go by VS Code's rules, ** any folders, * a name, ? a letter, {a,b} and a slash at the end only folders", () =>
            new HexExcludes(["**/*.{log,tmp}", "out/", "a?c"], [], false) is var globs
            && globs.Hidden("deep/x.log", false) && globs.Hidden("x.tmp", false) && !globs.Hidden("x.cs", false)
            && globs.Hidden("out", true) && globs.Hidden("deep/out", true) && !globs.Hidden("out", false) && globs.Hidden("abc", false) && !globs.Hidden("abbc", false)
            && globs.InSkippedFolder("deep/out/file.cs") && !globs.InSkippedFolder("deep/outside/file.cs"));

        // Use .gitignore off, what it says is back in quick open, search.exclude still isn't
        test.Run(() => SettingsPage!.Enter("search_use_ignore_files", Values.Bool(false)));
        test.Check("excludes: Use .gitignore off brings vendor, build.txt and sub/a.tmp back, venv stays out", () =>
            !settings.SearchUseIgnoreFiles && Listed() is var listed && listed.Contains("vendor/v.txt") && listed.Contains("build.txt") && listed.Contains("sub/a.tmp")
            && !listed.Any(file => file.StartsWith("venv/")));
        test.Run(() => SettingsPage!.Reset("search_use_ignore_files"));
        test.Check("excludes: and back on with its reset", () => settings.SearchUseIgnoreFiles && !Listed().Contains("vendor/v.txt"));

        /* The explorer, only files_exclude */

        test.Run(() =>
        {
            browsingWas = browsing;
            explorer.AddFolder(root);
        });
        test.WaitFor(() => InExplorer("src"), 3.0f);
        test.Check("excludes: the explorer still shows .github, .env, vendor, node_modules and bin, only .git is gone", () =>
            InExplorer(".github") && InExplorer(".env") && InExplorer("vendor") && InExplorer("node_modules") && InExplorer("bin") && InExplorer("root.log")
            && !InExplorer(".git"));
        test.Run(() => SettingsPage!.Enter("files_exclude", UIFormList.Value([.. HexSettings.UsualFilesExclude, "**/*.log"])));
        test.WaitFor(() => !InExplorer("root.log"), 3.0f);
        test.Check("excludes: **/*.log added to files_exclude hides the logs from the explorer and logs/app.log from search", () =>
            !InExplorer("root.log") && InExplorer("logs") && Listed() is var listed && !listed.Contains("logs/app.log") && !listed.Contains("root.log")
            && SettingsPage!.IsModified!("files_exclude"));
        test.Run(() => SettingsPage!.Reset("files_exclude"));
        test.WaitFor(() => InExplorer("root.log"), 3.0f);
        test.Check("excludes: and its reset brings them back", () => InExplorer("root.log") && Listed().Contains("logs/app.log"));
        test.Run(() =>
        {
            explorer.RemoveFolder(root);
            browsing = browsingWas;
        });
        test.Wait();

        /* Find in Files at its most */

        List<HexResult> many = [];
        test.Run(() =>
        {
            Write("many.txt", string.Concat(Enumerable.Repeat("needle\n", HexSearch.MOST_FOUND + 100)));
            many = HexSearch.FindIn([root], "needle", new UIFindOptions());
            ShowResults("needle", many);
        });
        test.Wait();
        test.Check("excludes: Find in Files stopped at 2000 says so over the results, narrow the search", () =>
            many.Count == HexSearch.MOST_FOUND && sideTree.Items is [{ } first, ..] && first.Text == ResultsMostText && first.Text.Contains("2000", StringComparison.Ordinal));
        test.Run(CloseResults);
        test.Wait();

        /* A folder's own */

        test.Check("excludes: the three are a folder's to set, search_exclude kept in its .hex/settings.hor and read back from there", () =>
        {
            if (!new[] { "files_exclude", "search_exclude", "search_use_ignore_files" }.All(HexSettings.IsWorkspaceKey))
                return false;

            string user = Path.Combine(root, "user_settings.hor");
            var kept = new HexSettings(user);
            if (kept.LoadWorkspace(root) is not null || kept.SetWorkspace("search_exclude", UIFormList.Value(["**/fixtures"])) is not null)
                return false;

            var read = new HexSettings(user);
            return File.ReadAllText(HexSettings.WorkspacePathOf(root)).Contains("\"**/fixtures\"", StringComparison.Ordinal)
                && read.LoadWorkspace(root) is null && read.SearchExclude.SequenceEqual(["**/fixtures"]) && read.IsOverridden("search_exclude");
        });
        test.Check("excludes: every row of Editor, Search is found by its VS Code id", () =>
            EditorSettingsCatalog.Text.Where(setting => setting.Group == EditorSettingsCatalog.SEARCH).ToList() is { Count: 3 } rows
            && rows.First(setting => setting.Key == "files_exclude").Keywords.Contains("files.exclude", StringComparison.Ordinal)
            && rows.First(setting => setting.Key == "search_exclude").Keywords.Contains("search.exclude", StringComparison.Ordinal)
            && rows.First(setting => setting.Key == "search_use_ignore_files").Keywords.Contains("search.useIgnoreFiles", StringComparison.Ordinal));
    }
}
