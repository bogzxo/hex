namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // Opt-in, read-only qualification against a real workspace, not the self-test fixture.
    private void TestRepository(HexPointerScript test)
    {
        string? path = Environment.GetEnvironmentVariable("HEX_TEST_SOLUTION");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            test.Check("repository: HEX_TEST_SOLUTION must name an existing solution", () => false);
            return;
        }

        string full = Path.GetFullPath(path);
        test.Run(() =>
        {
            explorer.OpenFolder(Path.GetDirectoryName(full)!);
            OpenSolution(full);
        });
        test.WaitFor(() => solution is not null && PlatformPaths.Same(solution.Path, full), 30f);
        test.Check("repository: the actual solution is loaded in the explorer", () =>
            solution is not null && PlatformPaths.Same(solution.Path, full) && Side == HexSide.Explorer);
        test.Check("repository: every listed project exists on disk", () =>
            solution is not null && solution.Projects.Any() && solution.Projects.All(project => File.Exists(project.Path)));
        test.Check("repository: engine, editor and game projects are available", () =>
            solution is not null && new[] { "Horizon", "Horizon.UI", "Hex", "Hex.Dawn", "Fighter2D", "Fighter2D.Game" }
                .All(name => solution.Projects.Any(project => project.Name == name)));
        test.Check("repository: Debug and Release configurations are available", () =>
            solution is not null && solution.Configurations.Contains("Debug") && solution.Configurations.Contains("Release"));
        test.Run(() =>
        {
            if (solution?.Projects.FirstOrDefault(project => project.Name == "Hex") is { } hex)
                EnsureSolutionDependencies(hex);
        });
        test.WaitFor(() => solution?.Projects.FirstOrDefault(project => project.Name == "Hex")?.DependencyConfiguration == Configuration, 90f);
        test.Check("repository: Hex dependencies resolve to real projects", () =>
            solution?.Projects.FirstOrDefault(project => project.Name == "Hex") is { References.Count: > 0, DependencyError: null } hex
            && hex.References.All(File.Exists)
            && hex.References.Any(path => PlatformPaths.Same(path, Path.Combine(Path.GetDirectoryName(full)!, "horizon", "Horizon", "Horizon.csproj"))));
        test.Run(() =>
        {
            foreach (string name in new[] { "Fighter2D.Game", "Hex.Dawn" })
                if (solution?.Projects.FirstOrDefault(project => project.Name == name) is { } project)
                    EnsureSolutionDependencies(project);
        });
        test.WaitFor(() => new[] { "Fighter2D.Game", "Hex.Dawn" }.All(name =>
            solution?.Projects.FirstOrDefault(project => project.Name == name)?.DependencyConfiguration == Configuration), 90f);
        test.Check("repository: Fighter game contains shared source under its linked path", () =>
            solution?.Projects.First(project => project.Name == "Fighter2D.Game").Files.Any(file =>
                file.DisplayPath == "App/Startup.cs" && file.Linked && file.ItemTypes.Contains("Compile") && File.Exists(file.Path)) == true);
        test.Check("repository: game excludes desktop entry point and test sources", () =>
            solution?.Projects.First(project => project.Name == "Fighter2D.Game").Files.All(file =>
                file.DisplayPath != "App/Program.cs" && !file.DisplayPath.StartsWith("Fighter2D.Tests/")) == true);
        test.Check("repository: Hex notebook asset has its evaluated linked location", () =>
            solution?.Projects.First(project => project.Name == "Hex.Dawn").Files.Any(file =>
                file.DisplayPath == "Assets/hex/notebooks/engineering.ipynb" && file.Linked && File.Exists(file.Path)) == true);
        test.Settle(0.3f);
        UITreeItem? linkedRow = null;
        test.Run(() =>
        {
            var game = solution!.Projects.First(project => project.Name == "Fighter2D.Game");
            var row = solutionHeads.Keys.FirstOrDefault(item => item.Tooltip is { } path && PlatformPaths.Same(path, game.Path));
            if (row is null) return;
            if (!row.Expanded) sideTree.Toggle(row);
            var app = row.Children?.FirstOrDefault(item => item.Text == "App");
            if (app is null) return;
            if (!app.Expanded) sideTree.Toggle(app);
            linkedRow = app.Children?.FirstOrDefault(item => item.Text == "Startup.cs");
        });
        test.Check("repository: linked file row is guarded from filesystem deletion", () =>
            linkedRow is not null && solutionHeads.ContainsKey(linkedRow) && (linkedRow.Tag as SideTag)?.Menu is null);
        test.Run(() => (linkedRow?.Tag as SideTag)?.Pressed?.Invoke());
        test.Check("repository: linked row opens the real Fighter source", () =>
            document.Path is { } path && PlatformPaths.Same(path, Path.Combine(Path.GetDirectoryName(full)!, "Fighter2D", "App", "Startup.cs")));
        test.Run(() => SolutionFilesChanged([(Path.Combine(Path.GetDirectoryName(full)!, "Fighter2D", "App", ".hex-save-" + Guid.NewGuid().ToString("N") + ".tmp"), LanguageServerFileChange.Created)]));
        test.Check("repository: transient save events in linked roots preserve evaluated membership", () =>
            solution?.Projects.First(project => project.Name == "Fighter2D.Game").DependencyConfiguration == Configuration);
        test.Run(() =>
        {
            var game = solution!.Projects.First(project => project.Name == "Fighter2D.Game");
            int before = game.EvaluationGeneration;
            SolutionFilesChanged([(Path.Combine(game.Folder, "obj", "generated.props"), LanguageServerFileChange.Created)]);
            if (game.EvaluationGeneration != before) throw new InvalidOperationException("Build output invalidated project membership.");
            SolutionFilesChanged([(game.Path, LanguageServerFileChange.Changed)]);
            EnsureSolutionDependencies(game);
        });
        test.WaitFor(() => solution?.Projects.First(project => project.Name == "Fighter2D.Game").DependencyConfiguration == Configuration, 90f);
        test.Check("repository: membership refresh completes after invalidation", () =>
            solution?.Projects.First(project => project.Name == "Fighter2D.Game") is { DependencyError: null, Files.Count: > 0 });
        test.Run(() => Shot("repository-solution"));
        // Screenshot encoding is asynchronous; keep rendering before the suite can exit.
        test.Settle(1f);
        test.Run(() => Console.WriteLine($"[Hex repository] {full}: {solution?.Projects.Count()} projects"));
        testExplorer.TestSolutionScope(test, full);
        if (Environment.GetEnvironmentVariable("HEX_TEST_REPOSITORY_RUN_TESTS") == "1")
            testExplorer.TestRepositorySelection(test, Path.Combine(Path.GetDirectoryName(full)!, "hex", "Hex.Tests", "Hex.Tests.csproj"));
        if (Environment.GetEnvironmentVariable("HEX_TEST_REPOSITORY_NAVIGATION") == "1")
            TestRepositoryNavigation(test, Path.GetDirectoryName(full)!);
    }

    private void TestRepositoryNavigation(HexPointerScript test, string root)
    {
        string program = Path.Combine(root, "hex", "Hex", "Program.cs");
        string expected = Path.Combine(root, "horizon", "Horizon.Platform.Desktop", "DesktopPlatform.cs");
        string[] lines = File.ReadAllLines(program);
        int line = Array.FindIndex(lines, text => text.Contains("DesktopPlatform.Install()", StringComparison.Ordinal));
        test.Run(() => Open(program));
        test.WaitFor(() => languages.CanServe(document, "definitionProvider"), 90f);
        test.Check("repository navigation: real C# language server offers definitions", () => languages.CanServe(document, "definitionProvider"));
        test.Run(() =>
        {
            if (line < 0)
                return;
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(line, lines[line].IndexOf("Install", StringComparison.Ordinal) + 2));
            codeEditing.GoToDefinition();
        });
        test.WaitFor(() => document.Path is { } path && PlatformPaths.Same(path, expected), 120f);
        test.Check("repository navigation: F12 resolves from Hex into the desktop engine project", () =>
            document.Path is { } path && PlatformPaths.Same(path, expected)
                && code.Document.LineAt(code.Document.Caret.Line).Contains("Install", StringComparison.Ordinal));
        test.Run(() => Shot("repository-cross-project-definition"));
        test.Settle(1f);
    }
}
