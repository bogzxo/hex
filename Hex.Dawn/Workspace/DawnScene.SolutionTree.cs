using Hex.Core.Projects;

using Horizon.UI;

namespace Hex.Dawn;

// The rows of the Solution view. The solution on top, its solution folders, its projects in them, and under a project
// its Dependencies (the projects it references and the packages it takes, with their versions) and then its files,
// which are the explorer's own rows doing what they do there. The startup project, what F5 runs, is in bold.
internal sealed partial class DawnScene
{
    // The rows that are the solution or a project rather than a file, by the folder a new file made on them goes in. The
    // explorer's file keys leave them alone, Delete on a project's row threw the project file in the trash
    private readonly Dictionary<UITreeItem, string> solutionHeads = [];

    // The rows of the Solution view that are the other way to how they start, a solution folder closed or a project's
    // Dependencies opened, by what they are
    private readonly HashSet<string> solutionFlipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The solution's row with everything under it, or a row saying it's being read.</summary>
    private UITreeItem SolutionItem(string path)
    {
        if (SolutionRead(path) is not { } read)
            return NoteItem(Said(solutionReading ? "solution.reading" : "solution.unread", ("name", Path.GetFileName(path))), DimColor);

        int count = read.Projects.Count();
        var root = new UITreeItem(Said("solution.row", ("name", read.Name), ("count", HexText.Count("project", count))), "folder-library")
        {
            Tooltip = read.Path,
            Expandable = true,
            Expanded = true,
            Tag = new SideTag(Menu: _ => ShowSolutionMenu(read))
        };

        solutionHeads[root] = read.Folder;
        AddSolutionFolder(root, read.Root, read.Path);
        return root;
    }

    // What a solution folder holds under its row, its folders, its projects and its loose files
    private void AddSolutionFolder(UITreeItem row, SolutionFolder folder, string key)
    {
        foreach (SolutionFolder inside in folder.Folders)
        {
            string folderKey = key + "/" + inside.Name;
            bool open = !solutionFlipped.Contains(folderKey);
            var item = new UITreeItem(inside.Name, open ? "folder-opened" : "folder")
            {
                IconColor = FolderColor,
                Expandable = true,
                Expanded = open,
                Tag = new SideTag(Toggled: toggled =>
                {
                    toggled.Icon = toggled.Expanded ? "folder-opened" : "folder";
                    if (!solutionFlipped.Remove(folderKey))
                        solutionFlipped.Add(folderKey);
                })
            };

            AddSolutionFolder(item, inside, folderKey);
            row.Add(item);
        }

        foreach (SolutionProject project in folder.Projects)
            row.Add(ProjectItem(project));

        foreach (string file in folder.Files)
            row.Add(FileRow(file, 1));
    }

    /// <summary>A project's row, bold for the startup project, what's under it filled in as it opens.</summary>
    private UITreeItem ProjectItem(SolutionProject project)
    {
        bool open = explorer.workspace.Expanded.Contains(project.Path);
        var item = new UITreeItem(project.Name, HexExplorer.FileIcon(project.Path))
        {
            IconColor = explorer.FileIconColor(project.Path),
            Tooltip = project.Path,
            Bold = Context.Workspace.RunTarget is { Kind: HexRunTarget.PROJECT } startup && PlatformPaths.Same(startup.Path, project.Path),
            Expandable = true,
            Expanded = open,
            Tag = new SideTag(
                Menu: _ => ShowProjectMenu(project),
                Toggled: toggled =>
                {
                    if (toggled.Expanded) explorer.workspace.Expanded.Add(project.Path);
                    else explorer.workspace.Expanded.Remove(project.Path);
                    explorer.KeepWorkspace();
                },
                Expanding: row => FillProject(row, project))
        };

        solutionHeads[item] = project.Folder;
        if (open)
            FillProject(item, project);
        return item;
    }

    // Delete on a package's row, the package taken out
    private bool RemovedPackage(SolutionProject project, SolutionPackage package)
    {
        RemovePackage(project, package);
        return true;
    }

    // Delete on a reference's row, the reference taken out
    private bool RemovedReference(SolutionProject project, string reference)
    {
        RemoveReference(project, reference);
        return true;
    }

    /// <summary>A project's Dependencies and then its files, put under its row as it opens.</summary>
    private void FillProject(UITreeItem item, SolutionProject project)
    {
        if (item.Children is not null)
            return;

        item.Children = [];
        if (!EnsureSolutionDependencies(project))
        {
            item.Children.Add(NoteItem(project.DependencyError is { } error
                ? Said("file_properties.not_evaluated", ("why", error))
                : Said("file_properties.evaluating"), DimColor));
            return;
        }
        string key = "dependencies:" + project.Path;
        bool open = solutionFlipped.Contains(key);
        var dependencies = item.Add(new UITreeItem(Said("solution.dependencies"), "references")
        {
            IconColor = DimColor,
            Expandable = project.References.Count + project.Packages.Count > 0,
            Expanded = open,
            Tag = new SideTag(Menu: _ => ShowDependenciesMenu(project), Toggled: _ =>
            {
                if (!solutionFlipped.Remove(key))
                    solutionFlipped.Add(key);
            })
        });

        // A reference's row is the project, Delete on it would have thrown the referenced project's file in the trash
        if (project.References.Count > 0)
        {
            var projects = dependencies.Add(new UITreeItem(Said("solution.projects"), "project") { IconColor = DimColor, Expanded = true, Tag = new SideTag(Menu: _ => ShowDependenciesMenu(project)) });
            foreach (string reference in project.References)
            {
                string path = reference;
                var row = projects.Add(new UITreeItem(Path.GetFileNameWithoutExtension(reference), "project")
                {
                    IconColor = DimColor,
                    Tooltip = reference,
                    Tag = new SideTag(Menu: _ => ShowReferenceMenu(project, path), Key: key => key == UIKeyboard.DELETE && RemovedReference(project, path))
                });
                solutionHeads[row] = project.Folder;
            }
        }

        if (project.Packages.Count > 0)
        {
            var packages = dependencies.Add(new UITreeItem(Said("solution.packages"), "package") { IconColor = DimColor, Expanded = true, Tag = new SideTag(Menu: _ => ShowDependenciesMenu(project)) });
            foreach (SolutionPackage package in project.Packages)
            {
                SolutionPackage taken = package;
                packages.Add(new UITreeItem(package.Name, "package")
                {
                    IconColor = DimColor,
                    Detail = package.Version,
                    Tag = new SideTag(Menu: _ => ShowPackageMenu(project, taken), Key: key => key == UIKeyboard.DELETE && RemovedPackage(project, taken))
                });
            }
        }

        AddProjectFiles(item, project);
    }
}
