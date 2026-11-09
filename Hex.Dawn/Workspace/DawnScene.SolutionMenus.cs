using System.Numerics;

using Hex.Core.Projects;

namespace Hex.Dawn;

// What a right click on the Solution view's solution or one of its projects offers, Solution Explorer's Build, Rebuild,
// Clean, Add, Manage NuGet Packages, Run, Debug, Set as Startup Project and Properties, and the configuration they build
// in, picked beside the Run button. A build runs as a task in the panel's terminal like any other, Debug goes the way F5
// does.
internal sealed partial class DawnScene
{
    private void WireConfiguration() => layout.Get<Button>("configuration_button").OnPressed = ShowConfigurations;

    private string Configuration => explorer.workspace.Configuration;

    /// <summary>The menu of a project's row.</summary>
    private void ShowProjectMenu(SolutionProject project)
    {
        var menu = new Menu("project");
        menu.Add(Said("solution.build"), () => tasks.Run(HexTasks.DotnetTask("build", project.Path, Configuration, Said("solution.build_task", ("name", project.Name)))), icon: "tools");
        menu.Add(Said("solution.rebuild"), () => tasks.Run(HexTasks.DotnetTask("build", project.Path, Configuration, Said("solution.rebuild_task", ("name", project.Name)), rebuild: true)), icon: "debug-rerun");
        menu.Add(Said("solution.clean"), () => tasks.Run(HexTasks.DotnetTask("clean", project.Path, Configuration, Said("solution.clean_task", ("name", project.Name)))), icon: "clear-all");
        menu.AddSeparator();
        AddAddMenu(menu, project, project.Folder);
        menu.Add(Said("nuget.manage"), () => ManageNuget(project), icon: "package");
        menu.AddSeparator();
        menu.Add(Said("solution.run"), () => tasks.RunProject(project.Path), "", "play").IsEnabled = () => project.IsProgram;
        menu.Add(Said("solution.debug"), () => debugger.DebugProject(project.Path), "", "debug-alt").IsEnabled = () => project.IsProgram && project.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        menu.Add(Said("solution.startup"), () => SetStartupProject(project), icon: "star-full");
        menu.AddSeparator();
        menu.Add(Said("solution.open_project"), () => Open(project.Path), icon: "go-to-file");
        menu.Add(Said("explorer_files.open_in_terminal"), () => explorer.OpenTerminalIn?.Invoke(project.Folder), icon: "terminal");
        menu.AddSeparator();
        menu.Add(Said("file_properties.menu"), () => ShowPropertiesOf([project.Path]), "Alt+Enter", "info");
        Context.Windows.Menu(menu);
    }

    /// <summary>The menu of a project's Dependencies, its Projects or its Packages, Add Project Reference and Manage NuGet Packages.</summary>
    private void ShowDependenciesMenu(SolutionProject project)
    {
        var menu = new Menu("dependencies");
        menu.Add(Said("solution.project_reference"), () => AddProjectReference(project), icon: "references");
        menu.Add(Said("nuget.manage"), () => ManageNuget(project), icon: "package");
        Context.Windows.Menu(menu);
    }

    /// <summary>The menu of a project another one references, Remove takes the reference out.</summary>
    private void ShowReferenceMenu(SolutionProject project, string reference)
    {
        var menu = new Menu("reference");
        menu.Add(Said("solution.open_project"), () => Open(reference), icon: "go-to-file");
        menu.AddSeparator();
        menu.Add(Said("solution.remove"), () => RemoveReference(project, reference), "Del", "trash");
        Context.Windows.Menu(menu);
    }

    /// <summary>The menu of a package a project takes, its versions to move to, and Remove.</summary>
    private void ShowPackageMenu(SolutionProject project, SolutionPackage package)
    {
        var menu = new Menu("package");
        menu.Add(Said("nuget.versions_menu"), () => LookUpPackage(project, package.Name), icon: "versions");
        menu.Add(Said("nuget.manage"), () => ManageNuget(project), icon: "package");
        menu.AddSeparator();
        menu.Add(Said("solution.remove"), () => RemovePackage(project, package), "Del", "trash");
        Context.Windows.Menu(menu);
    }

    /// <summary>The menu of the solution's row.</summary>
    private void ShowSolutionMenu(DotnetSolutionTree read)
    {
        var menu = new Menu("solution");
        menu.Add(Said("solution.build_solution"), () => tasks.Run(HexTasks.DotnetTask("build", read.Path, Configuration, Said("solution.build_task", ("name", read.Name)))), icon: "tools");
        menu.Add(Said("solution.rebuild_solution"), () => tasks.Run(HexTasks.DotnetTask("build", read.Path, Configuration, Said("solution.rebuild_task", ("name", read.Name)), rebuild: true)), icon: "debug-rerun");
        menu.Add(Said("solution.clean_solution"), () => tasks.Run(HexTasks.DotnetTask("clean", read.Path, Configuration, Said("solution.clean_task", ("name", read.Name)))), icon: "clear-all");
        menu.AddSeparator();
        menu.Add(Said("solution.open_text"), () => OpenSolutionText(read.Path), icon: "go-to-file");
        menu.Add(Said("explorer_files.open_in_terminal"), () => explorer.OpenTerminalIn?.Invoke(read.Folder), icon: "terminal");
        menu.AddSeparator();
        menu.Add(Said("solution.folder_view"), ShowFolderView, icon: "folder");
        Context.Windows.Menu(menu);
    }

    /// <summary>A project made what F5 and Ctrl+F5 run, the same as picking it beside the Run button, kept with the workspace.</summary>
    internal void SetStartupProject(SolutionProject project)
    {
        Context.Workspace.RunTarget = new HexRunTarget(HexRunTarget.PROJECT, project.Path, project.Name);
        ShowRunTarget();
        treeDirty = true;
        Say(Said("run_target.chosen", ("name", project.Name)));
    }

    /// <summary>The configurations under the button beside Run, the solution's when one is shown, the one in use ticked.</summary>
    private void ShowConfigurations()
    {
        List<string> names = solution is { } read && ShownSolution() is { } shown && PlatformPaths.Same(read.Path, shown)
            ? [.. read.Configurations]
            : [HexWorkspace.DEBUG, "Release"];
        if (!names.Contains(Configuration, StringComparer.OrdinalIgnoreCase))
            names.Add(Configuration);

        var menu = new Menu(Said("solution.configuration_title"));
        foreach (string configuration in names)
        {
            string name = configuration;
            menu.Add(name, () => ChooseConfiguration(name)).IsChecked = () => Configuration.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        UIComponent button = layout.Get<Button>("configuration_button");
        Context.Windows.MenuAt(menu, chrome.ToWorld(new Vector2(button.Bounds.Min.X, button.Bounds.Min.Y)));
    }

    private void ChooseConfiguration(string name)
    {
        if (Configuration.Equals(name, StringComparison.OrdinalIgnoreCase))
            return;
        explorer.workspace.Configuration = name;
        debugger.WorkspaceChanged();
        testExplorer.WorkspaceChanged();
        treeDirty = true;
        explorer.KeepWorkspace();
        ShowRunTarget();
        Say(Said("solution.configuration_chosen", ("name", name)));
    }
}
