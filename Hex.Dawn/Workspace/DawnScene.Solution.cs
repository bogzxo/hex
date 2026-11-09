using Hex.Core.Projects;

namespace Hex.Dawn;

// The Solution view of the explorer, Visual Studio's Solution Explorer. The solution's folders and projects instead of
// the workspace's folders, each project with what it depends on and its files. It's what the explorer shows when the
// workspace has one solution at the top of its folders, or a solution was opened, and the button at the end of the
// explorer's title switches between it and the folders the way VS's Switch Views does. The solution is read off the
// editor's thread, once, and read again only when a solution or a project file is written (the projects that weren't
// are kept as they were read).
internal sealed partial class DawnScene
{
    private Button switchViewButton = null!;

    // The solution as it was read last, null before it has been
    private DotnetSolutionTree? solution;

    // Whether it's being read now, and whether one of its files was written since it was last read
    private bool solutionReading, solutionStale;

    // A solution that couldn't be read, so it isn't read again and again
    private string? solutionUnread;

    // The one solution at the top of the workspace's folders, looked for again only when the folders or a solution change
    private string? onlySolution;
    private bool solutionLooked;

    private void WireSolution()
    {
        switchViewButton = layout.Get<Button>("tree_switch_view");
        switchViewButton.OnPressed = ShowSwitchViews;
        // Another workspace has its own startup project and configuration, the Run button said the last one's
        explorer.FoldersChanged += () =>
        {
            solutionLooked = false;
            ShowRunTarget();
        };
        Context.Workspace.FilesChanged += SolutionFilesChanged;
        explorer.AddMenuOf = AddFolderAddMenu;
    }

    /// <summary>
    /// The solution the explorer shows, null when it shows the folders. The one picked, else the only one at the top of
    /// the workspace's folders unless the folders were picked instead.
    /// </summary>
    private string? ShownSolution()
    {
        HexWorkspace workspace = explorer.workspace;
        if (workspace.IsEmpty || workspace.SolutionView == false)
            return null;
        if (workspace.Solution is { } picked && File.Exists(picked))
            return picked;

        if (!solutionLooked)
        {
            onlySolution = DotnetSolutionTree.OnlySolutionIn(workspace.Folders);
            solutionLooked = true;
        }

        return onlySolution;
    }

    /// <summary>
    /// What's been read of a solution, read (again) in the background when it hasn't been or a file of it was written.
    /// What was read before stays on screen while it's read again, the tree is put together again when it's in.
    /// </summary>
    private DotnetSolutionTree? SolutionRead(string path)
    {
        bool same = solution is { } read && PlatformPaths.Same(read.Path, path);
        if ((!same || solutionStale) && !solutionReading && !PlatformPaths.Same(solutionUnread ?? string.Empty, path))
        {
            solutionReading = true;
            solutionStale = false;
            DotnetSolutionTree? before = same ? solution : null;
            Context.Work.InBackground(Said("solution.reading"), () => DotnetSolutionTree.Read(path, before), read =>
            {
                solutionReading = false;
                solution = read;
                solutionUnread = read is null ? path : null;
                treeDirty |= Side == HexSide.Explorer;
            }, quiet: true);
        }

        return same ? solution : null;
    }

    /// <summary>A solution, a project or a Directory.Packages.props written, made or taken away, the solution read again.</summary>
    private void SolutionFilesChanged(IReadOnlyList<(string Path, LanguageServerFileChange Change)> changes)
    {
        foreach (var (path, change) in changes)
        {
            bool definition = DotnetSolutionTree.Matters(path);
            bool buildOutput = path.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj" or ".git");
            if (buildOutput) continue;
            if (solution is not null && (definition || change != LanguageServerFileChange.Changed))
            {
                foreach (var project in solution.Projects)
                {
                    // Include linked source roots: Fighter2D.Game's glob lives in its parent.
                    bool belongs = PlatformPaths.IsUnder(path, project.Folder) || project.Files.Where(file => file.Linked).Any(file =>
                    {
                        string? root = project.Folder;
                        while (root is not null && !PlatformPaths.IsUnder(file.Path, root))
                            root = Path.GetDirectoryName(root);
                        return root is not null && PlatformPaths.IsUnder(path, root);
                    });
                    if (!definition && (!belongs || !SolutionMembershipChanged(project, path, change))) continue;
                    project.EvaluationGeneration++;
                    project.DependencyConfiguration = null;
                    project.DependencyError = null;
                    treeDirty |= Side == HexSide.Explorer;
                }
            }
            if (!definition)
                continue;

            if (DotnetSolutionTree.IsSolution(path) && change != LanguageServerFileChange.Changed)
                solutionLooked = false;
            solutionStale = true;
            solutionUnread = null;
            treeDirty |= Side == HexSide.Explorer;
        }
    }

    internal static bool SolutionMembershipChanged(SolutionProject project, string path, LanguageServerFileChange change)
    {
        // Watchers report an atomic replacement as delete/create (and Windows adds a
        // transient backup name). Membership follows the resulting files, not those
        // intermediate operations. Real additions/removals still invalidate globs.
        string name = Path.GetFileName(path);
        if (name.StartsWith(".hex-save-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(10, name.Length - 14), "N", out _))
            return false;
        if (change == LanguageServerFileChange.Changed)
            return false;
        bool known = project.Files.Any(file => PlatformPaths.Same(file.Path, path));
        if (File.Exists(path))
            return !known;
        if (Directory.Exists(path))
            return change == LanguageServerFileChange.Created;
        return project.Files.Any(file => PlatformPaths.Same(file.Path, path) || PlatformPaths.IsUnder(file.Path, path));
    }

    /// <summary>
    /// Opens a solution and makes its folder the workspace root. Existing document buffers remain open.
    /// </summary>
    internal bool OpenSolution(string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full)!;
        if (explorer.workspace.Folders.Count != 1 || !PlatformPaths.Same(explorer.workspace.Folders[0], folder))
            explorer.OpenFolder(folder);

        ShowSolutionView(full);
        Context.Panels.ShowSide(HexSide.Explorer);
        Say(Said("solution.opened", ("name", Path.GetFileName(full))));
        return true;
    }

    /// <summary>A solution's own text in a tab, what opening it did before the Solution view.</summary>
    internal bool OpenSolutionText(string path)
    {
        if (documents.Find(other => other.Path is not null && PlatformPaths.Same(other.Path, path)) is { } already)
        {
            Show(already);
            return true;
        }

        try
        {
            return OpenText(path, File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Said("documents.file_not_read", ("name", Path.GetFileName(path)), ("reason", e.Message)), error: true);
            return false;
        }
    }

    /// <summary>The explorer switched to a solution, kept with the workspace.</summary>
    private void ShowSolutionView(string solutionPath)
    {
        bool changed = !PlatformPaths.Same(Context.Workspace.Solution ?? string.Empty, solutionPath);
        explorer.workspace.SolutionView = true;
        explorer.workspace.Solution = solutionPath;
        if (changed)
        {
            languages?.WorkspaceChanged();
            testExplorer?.WorkspaceChanged();
        }
        explorer.KeepWorkspace();
        treeDirty = true;
    }

    /// <summary>The explorer switched to the workspace's folders, kept with the workspace.</summary>
    private void ShowFolderView()
    {
        explorer.workspace.SolutionView = false;
        explorer.KeepWorkspace();
        treeDirty = true;
    }

    /// <summary>
    /// Switch Views, under the button at the end of the explorer's title. The solutions at the top of the workspace's
    /// folders (and the one that was opened from elsewhere) and the folders, the one showing ticked.
    /// </summary>
    private void ShowSwitchViews()
    {
        string? shown = ShownSolution();
        List<string> solutions = DotnetSolutionTree.SolutionsIn(explorer.workspace.Folders);
        if (explorer.workspace.Solution is { } picked && File.Exists(picked) && !solutions.Contains(picked, PlatformPaths.Comparer))
            solutions.Add(picked);

        var menu = new Menu(Said("solution.switch_title"));
        foreach (string solutionPath in solutions)
        {
            string path = solutionPath;
            menu.Add(Relative(path), () => ShowSolutionView(path), icon: "folder-library").IsChecked = () => shown is not null && PlatformPaths.Same(shown, path);
        }

        if (solutions.Count == 0)
            menu.Add(Said("solution.no_solution"), () => { }).IsEnabled = () => false;
        menu.AddSeparator();
        menu.Add(Said("solution.folder_view"), ShowFolderView, icon: "folder").IsChecked = () => shown is null;
        Context.Windows.Menu(menu);
    }
}
