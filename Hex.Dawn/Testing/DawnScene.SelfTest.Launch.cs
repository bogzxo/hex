namespace Hex.Dawn;

// The self test, how Hex starts. The test's own folder of this run, a step that blows up counted and not the end of
// Hex, and a start on a folder bringing back only the tabs of that folder.
internal sealed partial class DawnScene
{
    private void TestLaunch(HexPointerScript test)
    {
        /* Two tests at once each have folders of their own, named for the process */

        string pid = "-" + System.Environment.ProcessId;
        test.Check("launch: the self test works in a folder of this run's own, named for its process", () =>
            Path.GetFileName(testFolder) == "horizon-hex-selftest" + pid
            && System.Environment.GetEnvironmentVariable("DUSK_HOME") is { } home && home.EndsWith("horizon-hex-selftest-dusk" + pid, StringComparison.Ordinal));

        /* A step that throws, on a script of its own so the tally of this one isn't what's thrown off */

        var inner = new HexPointerScript { Quiet = true };
        inner.Run(() => throw new InvalidOperationException("thrown on purpose"));
        inner.WaitFor(() => throw new InvalidOperationException("thrown on purpose"), 5.0f);
        inner.Check("after the throws", () => true);
        test.Run(() =>
        {
            for (int tick = 0; tick < 20 && !inner.IsFinished; tick++)
                inner.Update(1.0f / 60.0f);
        });
        test.Check("launch: a step and a wait that throw are a failure each, and the script plays on to its tally", () =>
            inner.IsFinished && inner.Failed == 2 && inner.Passed == 1);

        /* Started on a folder, the workspace of last time doesn't bring its other tabs along */

        string scope = Path.Combine(testFolder, "launch_scope"), other = Path.Combine(testFolder, "launch_other");
        string inside = Path.Combine(scope, "inside.hor"), outside = Path.Combine(other, "outside.hor");
        HexWorkspace? was = null;

        test.Run(() =>
        {
            Directory.CreateDirectory(scope);
            Directory.CreateDirectory(other);
            System.IO.File.WriteAllText(inside, "let inside = 1;\n");
            System.IO.File.WriteAllText(outside, "let outside = 2;\n");

            // What the workspace of last time would be, another folder with a tab of each open
            var last = new HexWorkspace();
            last.Add(other);
            last.OpenFiles.Add(outside);
            last.OpenFiles.Add(inside);

            was = explorer.workspace;
            explorer.Restore(last, scope);
            explorer.OpenFolder(scope);
        });
        test.Wait();
        test.Check("launch: on a folder, a tab of the workspace of last time outside it stays shut", () =>
            !documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, outside)));
        test.Check("launch: on a folder, its own tabs of last time come back and it is the one folder", () =>
            documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, inside))
            && explorer.workspace.Folders.Count == 1 && PlatformPaths.Same(explorer.workspace.Folders[0], scope));

        test.Run(() =>
        {
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && PlatformPaths.IsUnder(path, scope)).ToList())
                CloseDocument(open);

            explorer.workspace = was ?? new HexWorkspace();
            treeDirty = true;
        });
        test.Wait();
    }
}
