using Hex.Core.Projects;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Add on a project's row and on a folder of one in the Solution view, Solution Explorer's. New Item and Class make a file
// of C# in the project's namespace and its style (CSharpItems) and open it, New Folder and Existing Item are the
// explorer's. Project references retain their MSBuild expressions and are verified with the SDK after editing.
// The solution's watch sees project changes and reads Dependencies again.
internal sealed partial class DawnScene
{
    // The kinds New Item offers, in the order Visual Studio lists them, and the icon each has
    private static readonly (CSharpItemKind Kind, string Icon)[] ItemKinds =
    [
        (CSharpItemKind.Class, "symbol-class"),
        (CSharpItemKind.Interface, "symbol-interface"),
        (CSharpItemKind.Record, "symbol-structure"),
        (CSharpItemKind.Struct, "symbol-structure"),
        (CSharpItemKind.Enum, "symbol-enum")
    ];

    /// <summary>The Add menu of a project's row or a folder of it, what goes in the folder.</summary>
    private void AddAddMenu(Menu into, SolutionProject project, string folder)
    {
        Menu add = into.AddMenu(Said("solution.add"), "add");
        add.Add(Said("solution.new_item"), () => NewItemIn(project, folder), icon: "new-file");
        add.Add(Said("solution.existing_item"), () => ExistingItemIn(folder), icon: "file-add");
        add.Add(Said("solution.new_folder"), () => explorer.NewFolderIn(folder), icon: "new-folder");
        add.AddSeparator();
        add.Add(Said("solution.project_reference"), () => AddProjectReference(project), icon: "references");
        add.AddSeparator();
        add.Add(Said("solution.class"), () => NameItem(project, folder, CSharpItemKind.Class), icon: "symbol-class");
    }

    /// <summary>
    /// The Add menu for a folder's row of the explorer while it's a folder of a project of the solution shown, null
    /// otherwise, so the Folder View's rows stay as they are.
    /// </summary>
    private void AddFolderAddMenu(Menu into, string folder)
    {
        if (ShownSolution() is null || solution is not { } read)
            return;

        // The deepest project the folder is in, a project inside another's folder is its own
        SolutionProject? project = read.Projects.Where(each => PlatformPaths.IsUnder(folder, each.Folder) || PlatformPaths.Same(folder, each.Folder))
            .MaxBy(each => each.Folder.Length);
        if (project is not null)
            AddAddMenu(into, project, folder);
    }

    /// <summary>New Item, the kinds of C# to pick from and then the name, or a file of any kind by its name.</summary>
    private void NewItemIn(SolutionProject project, string folder)
    {
        var kinds = new List<UIChoice>();
        foreach (var (kind, icon) in ItemKinds)
        {
            CSharpItemKind picked = kind;
            kinds.Add(new UIChoice(Said("solution.kind_" + kind.ToString().ToLowerInvariant()), () => NameItem(project, folder, picked)) { Icon = icon, Detail = Said("solution.kind_detail") });
        }

        kinds.Add(new UIChoice(Said("solution.kind_file"), () => explorer.NewFileIn(folder)) { Icon = "new-file", Detail = Said("solution.kind_file_detail") });
        ChooseFrom(Said("solution.new_item_title"), Said("solution.new_item_text", ("folder", Relative(folder))), kinds);
    }

    /// <summary>The name of a new type of C# asked for, its file made in the folder, written and opened.</summary>
    private void NameItem(SolutionProject project, string folder, CSharpItemKind kind)
    {
        // Class1, Interface1, the first that isn't there yet, the way Visual Studio names them
        string stem = kind.ToString();
        int number = 1;
        while (File.Exists(Path.Combine(folder, $"{stem}{number}.cs")))
            number++;
        string suggested = $"{stem}{number}.cs";

        string kindName = Said("solution.kind_" + kind.ToString().ToLowerInvariant());
        Context.Windows.Prompt(Said("solution.add_kind_title", ("kind", kindName)), Said("solution.add_kind_text", ("kind", kindName.ToLowerInvariant()), ("folder", Relative(folder))),
            suggested, Said("solution.add_button"), string.Empty, typed =>
            {
                string name = CSharpItems.FileName(typed.Trim());
                string stemTyped = Path.GetFileNameWithoutExtension(name);
                if (stemTyped.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    Say(Said("explorer_files.bad_file_name", ("name", typed)), error: true);
                    return;
                }

                string path = Path.Combine(folder, name);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Say(Said("explorer_files.already_there", ("name", name)), error: true);
                    return;
                }

                MakeItem(project, folder, path, kind, stemTyped);
            }, (0, suggested.Length - 3));
    }

    // The file written off the editor's thread (the project's style is read off a few of its files), then opened
    private void MakeItem(SolutionProject project, string folder, string path, CSharpItemKind kind, string name)
    {
        Context.Work.InBackground(Said("solution.adding", ("name", Path.GetFileName(path))), () =>
        {
            try
            {
                string space = CSharpItems.NamespaceFor(CSharpItems.RootNamespaceOf(project.Path, File.ReadAllText(project.Path)), project.Folder, folder);
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, CSharpItems.Text(kind, name, space, CSharpItems.StyleIn(project.Folder, folder)));
                return (string?)null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }, problem =>
        {
            if (problem is not null)
            {
                Say(Said("explorer_files.not_made", ("path", path), ("why", problem)), error: true);
                return;
            }

            explorer.workspace.Expanded.Add(project.Path);
            explorer.workspace.Expanded.Add(folder);
            explorer.KeepWorkspace();
            treeDirty = true;
            Open(path);
            Say(Said("explorer_files.made", ("path", Relative(path))));
        }, quiet: true);
    }

    /// <summary>Existing Item, a file picked from anywhere copied into the folder, the way Visual Studio adds one, and opened.</summary>
    private void ExistingItemIn(string folder)
    {
        Context.Windows.ChooseFile(Said("solution.existing_item_title"), Context.Windows.BrowseFolder, [(Said("solution.all_files"), "*.*")], picked =>
        {
            string into = Path.Combine(folder, Path.GetFileName(picked));
            try
            {
                if (!PlatformPaths.Same(Path.GetFullPath(picked), into))
                {
                    if (File.Exists(into))
                    {
                        Say(Said("explorer_files.already_there", ("name", Path.GetFileName(into))), error: true);
                        return;
                    }

                    File.Copy(picked, into);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Say(Said("explorer_files.not_made", ("path", into), ("why", e.Message)), error: true);
                return;
            }

            explorer.workspace.Expanded.Add(folder);
            treeDirty = true;
            Open(into);
            Say(Said("solution.item_added", ("name", Path.GetFileName(into))));
        });
    }

    /// <summary>Project Reference, the other projects of the solution this one doesn't reference yet, the one picked added.</summary>
    private void AddProjectReference(SolutionProject project)
    {
        if (!EnsureSolutionDependencies(project, () => AddProjectReference(project)))
            return;
        if (solution is not { } read)
            return;

        var choices = new List<UIChoice>();
        foreach (SolutionProject other in read.Projects.OrderBy(each => each.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (PlatformPaths.Same(other.Path, project.Path) || project.References.Contains(other.Path, PlatformPaths.Comparer))
                continue;

            string path = other.Path;
            choices.Add(new UIChoice(other.Name, () => ChangeProjectReference(project, path, add: true))
            {
                Icon = "project",
                Detail = Relative(other.Path)
            });
        }

        if (choices.Count == 0)
        {
            Say(Said("solution.no_more_references", ("name", project.Name)));
            return;
        }

        ChooseFrom(Said("solution.project_reference_title"), Said("solution.project_reference_text", ("name", project.Name)), choices);
    }

    /// <summary>Removes the original reference expression, retaining the project's path conventions.</summary>
    private void RemoveReference(SolutionProject project, string reference) =>
        ChangeProjectReference(project, reference, add: false);

    private void ChangeProjectReference(SolutionProject project, string reference, bool add)
    {
        string name = Path.GetFileNameWithoutExtension(reference), configuration = Configuration;
        Context.Work.InBackground(Said(add ? "solution.adding_reference" : "solution.removing", ("name", name)), () =>
        {
            try { ProjectReferences.Set(project.Path, reference, add, configuration).GetAwaiter().GetResult(); return (string?)null; }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { return e.Message; }
        }, error =>
        {
            solutionStale = true;
            solutionUnread = null;
            project.DependencyConfiguration = null;
            project.EvaluationGeneration++;
            treeDirty = true;
            if (error is null)
                Say(Said(add ? "solution.reference_added" : "solution.removed", ("name", name), ("project", project.Name)));
            else
                Say(Said("solution.dotnet_failed", ("command", add ? "Add Project Reference" : "Remove Project Reference"), ("why", error)), error: true);
        });
    }

    /// <summary>
    /// A dotnet command that changes a project (add or remove a reference or a package) run off the editor's thread with a
    /// card while it goes, what it said in the output, and the solution read again once it's done.
    /// </summary>
    private void RunDotnet(SolutionProject project, string doing, string done, params string[] arguments)
    {
        Context.Work.InBackground(doing, () =>
        {
            try
            {
                return DotnetCli.Run(project.Folder, arguments, TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
            {
                return (Exit: -1, Said: e.Message);
            }
        }, result =>
        {
            foreach (string line in result.Said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Context.Notices.Note(line.TrimEnd('\r'), error: result.Exit != 0);

            solutionStale = true;
            solutionUnread = null;
            treeDirty = true;
            if (result.Exit == 0)
            {
                Say(done);
                return;
            }

            string why = result.Said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                ?? result.Said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "?";
            Say(Said("solution.dotnet_failed", ("command", "dotnet " + arguments[0]), ("why", why)), error: true);
        });
    }
}
