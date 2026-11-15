using Hex.Core.Projects;

namespace Hex.Tests;

public sealed class ProjectTemplatesTests
{
    [Theory]
    [InlineData("MyApp", "None")]
    [InlineData("my-app_2.core", "None")]
    [InlineData("", "Empty")]
    [InlineData("2fast", "BadStart")]
    [InlineData("_hidden", "BadStart")]
    [InlineData("has space", "BadCharacter")]
    [InlineData("slash/name", "BadCharacter")]
    [InlineData("trailing.", "BadCharacter")]
    public void NamesAreCheckedTheWayEveryToolTakesThem(string name, string expected) =>
        Assert.Equal(expected, ProjectTemplates.Check(name).ToString());

    [Fact]
    public void TooLongANameIsSaidSo() => Assert.Equal(ProjectNameProblem.TooLong, ProjectTemplates.Check("a" + new string('b', 64)));

    [Fact]
    public void ModulesAndPackagesAreWhatTheirLanguagesTake()
    {
        Assert.Equal("my_cool_app", ProjectTemplates.Module("My-Cool.App"));
        Assert.Equal("mycoolapp", ProjectTemplates.Package("My-Cool.App"));
    }

    [Fact]
    public void EveryKindThatIsOnlyFilesHasTheFileItOpensOn()
    {
        foreach (ProjectTemplate template in ProjectTemplates.All.Where(each => each.Tool.Length == 0))
        {
            var files = ProjectTemplates.Files(template, "Sample").Select(file => file.Path).ToList();
            Assert.Contains(ProjectTemplates.MainFile(template, "Sample"), files);
            Assert.Empty(ProjectTemplates.Commands(template, "Sample", optional: false));
        }
    }

    [Fact]
    public void DotnetKindsAreTheSdksTemplatesWithASolution()
    {
        var commands = ProjectTemplates.Commands(ProjectTemplates.Find("csharp-console")!, "Tool", optional: false);
        Assert.Equal(new[] { "dotnet", "new", "console", "--name", "Tool", "--output", "." }, commands[0]);
        Assert.Equal(new[] { "dotnet", "sln", "add", "Tool.csproj" }, commands[^1]);
    }

    [Fact]
    public void PythonsVenvIsOnlyThereWhenItsWanted()
    {
        ProjectTemplate python = ProjectTemplates.Find("python")!;
        Assert.Empty(ProjectTemplates.Commands(python, "tool", optional: false));
        Assert.Equal(new[] { "-m", "venv", ".venv" }, ProjectTemplates.Commands(python, "tool", optional: true).Single()[1..]);
    }

    [Fact]
    public void AGoProjectIsMadeInAFolderThatIsFreeAndNotInOneThatIsnt()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-new-project-" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "Hello");
            var request = new ProjectRequest(ProjectTemplates.Find("go")!, "Hello", folder, Optional: false, Git: false);
            Assert.Null(ProjectCreation.Make(request, _ => { }));
            Assert.Equal("module hello\n\ngo 1.22\n", File.ReadAllText(Path.Combine(folder, "go.mod")));
            Assert.Contains("Hello from Hello!", File.ReadAllText(Path.Combine(folder, "main.go")));

            Assert.False(ProjectCreation.FolderFree(folder));
            Assert.NotNull(ProjectCreation.Make(request, _ => { }));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AJavaProjectHasItsClassInThePackageItSays()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-new-project-" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "Shop-Front");
            Assert.Null(ProjectCreation.Make(new ProjectRequest(ProjectTemplates.Find("java-maven")!, "Shop-Front", folder, false, false), _ => { }));
            string app = Path.Combine(folder, "src", "main", "java", "shopfront", "App.java");
            Assert.StartsWith("package shopfront;", File.ReadAllText(app));
            Assert.Contains("<artifactId>Shop-Front</artifactId>", File.ReadAllText(Path.Combine(folder, "pom.xml")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    // What an extension says its project is made of, without HIDL
    private sealed class Listed(IReadOnlyList<(string Path, string Text)> files, IReadOnlyList<string[]>? commands = null) : IProjectSource
    {
        public IReadOnlyList<(string Path, string Text)> Files(string name) => [.. files.Select(file => (ProjectTemplates.Fill(file.Path, name), ProjectTemplates.Fill(file.Text, name)))];

        public IReadOnlyList<string[]> Commands(string name, bool optional) => commands ?? [];
    }

    [Fact]
    public void AnExtensionsProjectIsListedAfterHexsOwnAndGoesWithIt()
    {
        var love = new ProjectTemplate("test-love", "Lua", string.Empty, "main.lua", Title: "LÖVE", Owner: "test_ext", Source: new Listed([("main.lua", "-- {{name}}")]));
        try
        {
            Assert.Null(ProjectTemplates.Add(love));
            Assert.Same(love, ProjectTemplates.All[^1]);
            Assert.Equal(ProjectTemplates.BuiltIn.Count + 1, ProjectTemplates.All.Count);

            // Said again it's put in place of the first, not listed twice
            Assert.Null(ProjectTemplates.Add(love with { Title = "LÖVE 2" }));
            Assert.Equal("LÖVE 2", ProjectTemplates.Find("test-love")!.Title);
            Assert.Equal(ProjectTemplates.BuiltIn.Count + 1, ProjectTemplates.All.Count);

            // Hex's own and another extension's ids are refused
            Assert.NotNull(ProjectTemplates.Add(love with { Id = "go" }));
            Assert.NotNull(ProjectTemplates.Add(love with { Owner = "other_ext" }));
        }
        finally
        {
            ProjectTemplates.Forget("test_ext");
        }

        Assert.Null(ProjectTemplates.Find("test-love"));
        Assert.Equal(ProjectTemplates.BuiltIn.Count, ProjectTemplates.All.Count);
    }

    [Fact]
    public void TheNameIsFilledInEveryWayAProjectWantsIt() =>
        Assert.Equal("My-App my_app myapp my-app", ProjectTemplates.Fill("{{name}} {{module}} {{package}} {{lower}}", "My-App"));

    [Fact]
    public void AnExtensionsProjectIsMadeFromWhatItSaidAndNeverOutsideItsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-new-project-" + Guid.NewGuid());
        try
        {
            var game = new ProjectTemplate("test-game", "Lua", string.Empty, "src/{{module}}.lua", Owner: "test_ext",
                Source: new Listed([("src/{{module}}.lua", "print(\"{{name}}\")\n")]));
            string folder = Path.Combine(root, "Space-Game");

            // Not worked out first, on the editor's thread, it isn't made
            Assert.NotNull(ProjectCreation.Make(new ProjectRequest(game, "Space-Game", folder, false, false), _ => { }));

            var request = new ProjectRequest(game, "Space-Game", folder, false, false, ProjectTemplates.Contents(game, "Space-Game", false));
            Assert.Null(ProjectCreation.Make(request, _ => { }));
            Assert.Equal("print(\"Space-Game\")\n", File.ReadAllText(Path.Combine(folder, "src", "space_game.lua")));
            Assert.Equal("src/space_game.lua", ProjectTemplates.MainFile(game, "Space-Game"));

            var escaping = game with { Source = new Listed([("../outside.txt", "no")]) };
            string other = Path.Combine(root, "Other");
            Assert.NotNull(ProjectCreation.Make(new ProjectRequest(escaping, "Other", other, false, false, ProjectTemplates.Contents(escaping, "Other", false)), _ => { }));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public void AHorizonGameReferencesTheEngineAndSaysGoodbyeSunshine()
    {
        ProjectTemplate game = ProjectTemplates.Find("csharp-horizon")!;
        var files = ProjectTemplates.Files(game, "Sun-Game").ToDictionary(file => file.Path, file => file.Text);

        Assert.Contains(@"$(HorizonRoot)Horizon.UI\Horizon.UI.csproj", files["Sun-Game.csproj"]);
        Assert.Contains("<RootNamespace>Sun_Game</RootNamespace>", files["Sun-Game.csproj"]);
        Assert.Contains("new Label(\"Goodbye, sunshine!\")", files["MainScene.cs"]);
        Assert.Contains("namespace Sun_Game;", files["MainScene.cs"]);
        Assert.Contains("engine.Run<MainScene>();", files["Program.cs"]);
        Assert.Equal("MainScene.cs", ProjectTemplates.MainFile(game, "Sun-Game"));

        // The engine cloned beside it only when there's no checkout to point at
        var commands = ProjectTemplates.Commands(game, "Sun-Game", optional: false);
        Assert.Equal(ProjectTemplates.HorizonEngine() is null, commands.Any(command => command is ["git", "clone", ..]));
        Assert.Contains(commands, command => command is ["dotnet", "sln", "add", "Sun-Game.csproj"]);
    }

    [Theory]
    [InlineData("My-App", "My_App")]
    [InlineData("Game.2d", "Game._2d")]
    public void TheNameIsMadeANamespace(string name, string expected) => Assert.Equal(expected, ProjectTemplates.Namespace(name));
}
