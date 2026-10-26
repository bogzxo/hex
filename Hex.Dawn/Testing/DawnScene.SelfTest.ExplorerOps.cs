using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, shuffling files in the explorer the way VS Code does, copy, cut, paste, duplicate and a rename that moves
// a file down into a folder, with what's open following. And telling files apart by their icons, a new file's extension
// guessed from its neighbours, and a missing server's card that can be told to keep quiet.
internal sealed partial class DawnScene
{
    private void TestExplorerOps(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "explorer_ops"), sub = Path.Combine(folder, "sub");
        string x = Path.Combine(folder, "x.go"), y = Path.Combine(folder, "y.go"), z = Path.Combine(folder, "z.go");
        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(sub);
            System.IO.File.WriteAllText(x, "package main\n");
            System.IO.File.WriteAllText(y, "package main\n\nfunc y() {}\n");
            System.IO.File.WriteAllText(z, "package main\n\nfunc z() {}\n");
            explorer.AddFolder(folder);
        });
        test.Wait();

        test.Check("explorer ops: a file's menu has Copy Relative Path, Open in Terminal, Find in Folder, Duplicate, Copy, Cut and Paste", () =>
            explorer.ExplorerMenu(new ExplorerEntry(x, false, 1, false)).Items.Select(item => item.Label).ToHashSet() is var labels
            && new[] { "copy_relative_path", "open_in_terminal", "find_in_folder", "duplicate", "copy", "cut", "paste" }.All(key => labels.Contains(Said("explorer_files." + key))));
        test.Check("explorer ops: the relative path is from the workspace folder, with forward slashes", () =>
            explorer.RelativeOf(Path.Combine(sub, "a.go")) == "sub/a.go" && explorer.ScopeOf(sub) == "sub" && explorer.ScopeOf(folder) == string.Empty);

        /* Copy and paste into the same folder, a name that's taken gets " copy" */

        List<string> pasted = [];
        test.Run(() =>
        {
            explorer.CopyPaths([x]);
            pasted = explorer.PasteInto(folder);
        });
        test.Check("explorer ops: x.go copied and pasted beside itself is x copy.go", () =>
            pasted is [var copy] && Path.GetFileName(copy) == "x copy.go" && System.IO.File.Exists(copy) && System.IO.File.Exists(x));

        string? duplicate = null;
        test.Run(() => duplicate = explorer.Duplicate(x));
        test.Check("explorer ops: Duplicate makes the next free name, x copy 2.go", () =>
            duplicate is { } made && Path.GetFileName(made) == "x copy 2.go" && System.IO.File.Exists(made));

        /* Cut and paste moves it, the open tab going along */

        string moved = Path.Combine(sub, "y.go");
        test.Run(() =>
        {
            Open(y);
            explorer.CutPaths([y]);
            pasted = explorer.PasteInto(sub);
        });
        test.Wait();
        test.Check("explorer ops: y.go cut and pasted into sub moves it, and its open tab follows it there", () =>
            pasted is [var to] && PlatformPaths.Same(to, moved) && System.IO.File.Exists(moved) && !System.IO.File.Exists(y)
            && documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, moved)) && !documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, y))
            && !explorer.CanPaste);

        /* A rename with a / in it moves the file inside the workspace, never out of it */

        bool renamed = false, climbed = true;
        test.Run(() =>
        {
            renamed = explorer.RenameTo(z, "sub/z.go");
            climbed = explorer.RenameTo(x, "../x.go");
        });
        test.Check("explorer ops: renamed to sub/z.go it moves into sub, and ../x.go is refused", () =>
            renamed && System.IO.File.Exists(Path.Combine(sub, "z.go")) && !System.IO.File.Exists(z)
            && !climbed && System.IO.File.Exists(x));

        /* The new file's extension, from the files beside it */

        string layouts = Path.Combine(folder, "layouts"), empty = Path.Combine(folder, "empty");
        test.Run(() =>
        {
            Directory.CreateDirectory(layouts);
            Directory.CreateDirectory(empty);
            System.IO.File.WriteAllText(Path.Combine(layouts, "menu.hor"), "#define layout\n");
            System.IO.File.WriteAllText(Path.Combine(layouts, "hud.hor"), "#define layout\n");
        });
        test.Check("explorer ops: a new file among .go files is guessed new.go, among layouts .hor, and with nothing beside it never .hor", () =>
            explorer.GuessExtension(sub) == ".go" && explorer.GuessExtension(layouts) == ".hor" && explorer.GuessExtension(empty) != ".hor"
            && !Said("explorer_files.new_file_text", ("folder", sub), ("extension", ".go")).Contains(".hor", StringComparison.Ordinal));

        /* Icons, every kind its own, and a leftover dim */

        string[] kinds = ["a.yml", "a.toml", "a.xml", "a.json", "Dockerfile", "Makefile", ".env", ".gitignore", "main.tf", "q.sql", "App.vue", "App.svelte", "app.log", "fix.rej"];
        test.Check("explorer ops: yml, toml, xml, json, Dockerfile, Makefile, .env, .gitignore, tf, sql, vue, svelte, log and rej each have an icon of their own", () =>
            kinds.Select(HexExplorer.FileIcon).Distinct().Count() == kinds.Length && kinds.All(kind => HexExplorer.FileIcon(kind) != "file")
            && HexExplorer.FileIcon("docker-compose.yml") == HexExplorer.FileIcon("Dockerfile") && HexExplorer.FileIcon(".env.local") == HexExplorer.FileIcon(".env"));
        test.Check("explorer ops: a .ps1 and a .sh are told apart by their colour", () =>
            explorer.FileIconColor("build.ps1") is { } ps1 && explorer.FileIconColor("build.sh") is { } sh && ps1 != sh);
        test.Check("explorer ops: deploy.sh.orig keeps the shell's icon but goes the dim colour", () =>
            HexExplorer.FileIcon("deploy.sh.orig") == HexExplorer.FileIcon("deploy.sh") && compositor.Skin?.Syntax is { } syntax
            && explorer.FileIconColor("deploy.sh.orig") == syntax.Comment && explorer.FileIconColor("deploy.sh") != syntax.Comment);

        test.Run(() =>
        {
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && PlatformPaths.IsUnder(path, folder)).ToList())
                CloseDocument(open);
            explorer.RemoveFolder(folder);
        });
        test.Wait();

        TestQuietServerCards(test, folder);
    }

    // A missing server's card has Don't Show Again, pressed it's quiet the next session too, and the setting quiets them all
    private void TestQuietServerCards(HexPointerScript test, string folder)
    {
        string nowhere = Path.Combine(folder, "nowhere", OperatingSystem.IsWindows() ? "gopls.exe" : "gopls"), goFile = Path.Combine(folder, "quiet.go");
        int before = 0;
        test.Run(() => OpenSettings(HexLanguages.SERVER_SECTION));
        test.Run(() =>
        {
            SettingsPage?.Enter(HexLanguages.SERVER_KEY + "go", new StringValue(nowhere));
            CloseSettings();
            languages.SayMissing = true;
            languages.ForgetMissingSaid();
            before = languages.MissingNotices.Count;
            languages.SayIfMissing(goFile);
        });
        test.Check("explorer ops: a missing gopls's card has Don't Show Again beside the other two", () =>
            languages.MissingNotices.Count == before + 1
            && toasts.Cards.Any(card => card.Text.Contains("gopls", StringComparison.Ordinal) && card.Actions.Count == 3
                && card.Actions.Any(action => action.Label == Said("code_server.dont_show_again"))));
        test.Run(() =>
        {
            foreach (var card in toasts.Cards.Where(card => card.Text.Contains("gopls", StringComparison.Ordinal)).ToList())
            {
                foreach (var action in card.Actions.Where(action => action.Label == Said("code_server.dont_show_again")).ToList())
                    action.Pressed();
                card.Close();
            }

            // The next session, nothing said yet
            languages.ForgetMissingSaid();
            before = languages.MissingNotices.Count;
            languages.SayIfMissing(goFile);
        });
        test.Check("explorer ops: pressed, the next session says nothing of gopls", () =>
            languages.MissingNotices.Count == before && languages.MissingQuiet("go"));

        test.Run(() =>
        {
            settings.ServerSkipped.Remove(HexLanguages.MISSING_QUIET + "go");
            languages.MissingCards = false;
            languages.ForgetMissingSaid();
            before = languages.MissingNotices.Count;
            languages.SayIfMissing(goFile);
        });
        test.Check("explorer ops: and with missing servers turned off in the settings nothing is said of any", () =>
            languages.MissingNotices.Count == before && !languages.MissingCards);

        test.Run(() =>
        {
            languages.MissingCards = true;
            languages.SayMissing = false;
            OpenSettings(HexLanguages.SERVER_SECTION);
        });
        test.Run(() =>
        {
            SettingsPage?.Reset(HexLanguages.SERVER_KEY + "go");
            CloseSettings();
        });
        test.Wait();
    }
}
