using Hex.Core.Projects;

namespace Hex.Dawn;

// The self test, New Project. The window up from its command, a kind picked from its list, the name checked as it's
// typed, a folder that's taken refused, and then a Go module made the way Create makes it, its folder the workspace and
// main.go open. Go and Java are only files, so none of it needs a tool on the machine.
internal sealed partial class DawnScene
{
    private void TestNewProject(HexPointerScript test)
    {
        string place = Path.Combine(testFolder, "new projects");
        string made = Path.Combine(place, "Hello");

        test.Run(() =>
        {
            if (Directory.Exists(place))
                Directory.Delete(place, recursive: true);
            Directory.CreateDirectory(Path.Combine(place, "Taken"));
            File.WriteAllText(Path.Combine(place, "Taken", "keep.txt"), "already here");
            AllCommands().First(command => command.Text.Contains(Said("new_project.command"), StringComparison.Ordinal)).Picked();
        });
        test.Wait();

        test.Check("new project: the command puts the window up, every kind a row and the first picked", () =>
            NewProjectWindow is not null && newProjectRows.Count == ProjectTemplates.All.Count && newProjectRows[0].Row.Selected);
        test.Check("new project: a free name is put in so Create works straight away", () =>
            newProjectName!.Text.StartsWith("MyProject", StringComparison.Ordinal) && newProjectCreate is { Enabled: true });

        test.Run(() => FilterProjectKinds("rust"));
        test.Check("new project: the search narrows the kinds and picks the first left", () =>
            newProjectRows.Count(row => row.Row.Visible) == 1 && NewProjectKind.Id == "rust");
        test.Run(() => FilterProjectKinds(string.Empty));

        test.Check("new project: every kind's server is one language_servers.hor has, Java's jdtls among them", () =>
            ProjectTemplates.BuiltIn.All(template => languages.ServerEntry(template.Server) is not null)
            && languages.ServerEntry("java") is { Command: "jdtls", Extensions: [".java"] });

        test.Run(() =>
        {
            PickProjectKind(ProjectTemplates.Find("go")!);
            newProjectPlace!.Text = place;
            newProjectName!.Text = "2bad";
            CheckNewProject();
        });
        test.Check("new project: Go picked lights its row alone and a name starting with a digit leaves Create off", () =>
            newProjectRows.Count(row => row.Row.Selected) == 1 && NewProjectKind.Id == "go"
            && newProjectCreate is { Enabled: false } && newProjectLine == Said("new_project.name_start"));

        test.Run(() =>
        {
            newProjectName!.Text = "Taken";
            CheckNewProject();
        });
        test.Check("new project: and a folder with something in it is refused too", () =>
            newProjectCreate is { Enabled: false } && newProjectLine.Contains("Taken", StringComparison.Ordinal));

        test.Run(() =>
        {
            newProjectName!.Text = "Hello";
            newProjectGit!.State = false;
            CheckNewProject();
        });
        test.Check("new project: a good name says the folder it makes and Create is on", () =>
            newProjectCreate is { Enabled: true } && newProjectLine == Said("new_project.creates", ("folder", made)));

        test.Run(CreateProject);
        test.WaitFor(() => document.Path is { } open && PlatformPaths.Same(open, Path.Combine(made, "main.go")), 20);
        test.Check("new project: Create puts the window down and makes the module", () =>
            NewProjectWindow is null && File.Exists(Path.Combine(made, "go.mod")));
        test.Check("new project: its folder is the workspace, main.go open in it", () =>
            explorer.workspace.Folders is [{ } folder] && PlatformPaths.Same(folder, made)
            && document.Path is { } path && PlatformPaths.Same(path, Path.Combine(made, "main.go")));

        test.Run(() =>
        {
            ShowNewProject();
            PickProjectKind(ProjectTemplates.Find("java-maven")!);
        });
        test.Check("new project: opened again it's beside the last one, and Java says whether jdtls is here", () =>
            PlatformPaths.Same(newProjectPlace!.Text, place) && newProjectServer!.Text.Contains("jdtls", StringComparison.Ordinal));
        test.Run(() => newProjectWindow?.Close());
        test.Wait();
    }
}
