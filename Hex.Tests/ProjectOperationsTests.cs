using Hex.Core;
using Hex.Core.Projects;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// What the Solution view's project menu does that can be checked without a window. A new class's namespace and style,
/// a property of the project set in its text, launch profiles read and written, the run task they make, nuget.org's
/// answer read, and the profile picked kept with the workspace.
/// </summary>
public sealed class ProjectOperationsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-project-ops-").FullName;

    [Fact]
    public void ANewClassIsInTheRootNamespaceAndItsFolders()
    {
        string project = Path.Combine(folder, "My Game.csproj");
        Assert.Equal("My_Game", CSharpItems.RootNamespaceOf(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        Assert.Equal("Engine.Core", CSharpItems.RootNamespaceOf(project, "<Project><PropertyGroup><RootNamespace>Engine.Core</RootNamespace></PropertyGroup></Project>"));
        Assert.Equal("Shooter", CSharpItems.RootNamespaceOf(project, "<Project><PropertyGroup><AssemblyName>Shooter</AssemblyName></PropertyGroup></Project>"));

        Assert.Equal("Game", CSharpItems.NamespaceFor("Game", folder, folder));
        Assert.Equal("Game.Scenes._2D.Menus", CSharpItems.NamespaceFor("Game", folder, Path.Combine(folder, "Scenes", "2D", "Menus")));
        Assert.Equal("Game", CSharpItems.NamespaceFor("Game", folder, Path.GetTempPath()));
        Assert.Equal("_1st_Thing", CSharpItems.Identifier("1st Thing"));
    }

    [Fact]
    public void ANewClassIsWrittenTheWayTheProjectWritesItsCode()
    {
        CSharpStyle fileScoped = CSharpItems.StyleOf(["namespace A;\r\n\r\ninternal class B\r\n{\r\n  int c;\r\n}\r\n"]);
        Assert.Equal(new CSharpStyle(true, "  ", "\r\n"), fileScoped);
        Assert.Equal("namespace A.B;\r\n\r\ninternal record Thing\r\n{\r\n}\r\n", CSharpItems.Text(CSharpItemKind.Record, "Thing", "A.B", fileScoped));

        CSharpStyle block = CSharpItems.StyleOf(["namespace A\n{\n\tclass B { }\n}\n", "namespace C {\n}\n"]);
        Assert.Equal(new CSharpStyle(false, "\t", "\n"), block);
        Assert.Equal("namespace A\n{\n\tinternal interface IThing\n\t{\n\t}\n}\n", CSharpItems.Text(CSharpItemKind.Interface, "IThing", "A", block));

        Assert.True(CSharpItems.StyleOf([]).FileScoped);
        Assert.Equal("Thing.cs", CSharpItems.FileName("Thing"));
        Assert.Equal("Thing.cs", CSharpItems.FileName("Thing.cs"));
    }

    [Fact]
    public void ANewClassReadsTheStyleOffTheProjectsFiles()
    {
        Directory.CreateDirectory(Path.Combine(folder, "obj"));
        File.WriteAllText(Path.Combine(folder, "obj", "Generated.cs"), "namespace X\n{\n}\n");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "namespace X;\r\n\r\nclass P\r\n{\r\n    static void Main() { }\r\n}\r\n");
        Assert.Equal(new CSharpStyle(true, "    ", "\r\n"), CSharpItems.StyleIn(folder, Path.Combine(folder, "Nothing")));
    }

    [Fact]
    public void APropertyIsChangedWhereTheProjectSaysIt()
    {
        string text = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>net11.0</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";
        Assert.Equal("Exe", ProjectFileEdits.Property(text, "OutputType"));
        Assert.Null(ProjectFileEdits.Property(text, "Nullable"));

        string library = ProjectFileEdits.SetProperty(text, "OutputType", "Library");
        Assert.Equal(text.Replace("<OutputType>Exe</OutputType>", "<OutputType>Library</OutputType>"), library);

        string nullable = ProjectFileEdits.SetProperty(text, "Nullable", "enable");
        Assert.Equal(text.Replace("net11.0</TargetFramework>\r\n", "net11.0</TargetFramework>\r\n    <Nullable>enable</Nullable>\r\n"), nullable);

        Assert.Equal(text.Replace("    <OutputType>Exe</OutputType>\r\n", string.Empty), ProjectFileEdits.SetProperty(text, "OutputType", null));
        Assert.Same(text, ProjectFileEdits.SetProperty(text, "AssemblyName", null));
        Assert.Same(text, ProjectFileEdits.SetProperty(text, "OutputType", "Exe"));
    }

    [Fact]
    public void APropertyOfAProjectWithNoGroupGetsOne()
    {
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>",
            ProjectFileEdits.SetProperty("<Project Sdk=\"Microsoft.NET.Sdk\" />", "Nullable", "enable"));

        string items = "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <ItemGroup>\n    <None Include=\"a\" />\n  </ItemGroup>\n\n</Project>\n";
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n\n  <ItemGroup>\n    <None Include=\"a\" />\n  </ItemGroup>\n\n</Project>\n",
            ProjectFileEdits.SetProperty(items, "Nullable", "enable"));

        // A conditioned group isn't the project's own, the property goes in the plain one
        string conditioned = "<Project>\n  <PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">\n    <Nullable>disable</Nullable>\n  </PropertyGroup>\n  <PropertyGroup>\n    <A>1</A>\n  </PropertyGroup>\n</Project>";
        Assert.Contains("<A>1</A>\n    <Nullable>enable</Nullable>", ProjectFileEdits.SetProperty(conditioned, "Nullable", "enable"));
    }

    [Fact]
    public void LaunchProfilesAreReadTheWayVisualStudioReadsThem()
    {
        List<DotnetLaunchProfile> profiles = DotnetLaunch.ProfilesIn("""
            {
              // a comment
              "profiles": {
                "web": { "commandName": "IISExpress" },
                "quick": { "commandName": "Project", "commandLineArgs": "quickstart \"a b\"", "workingDirectory": "$(ProjectDir)run", "environmentVariables": { "HORIZON_AUDIO": "off" } },
                "plain": { },
              }
            }
            """);

        Assert.Equal(["quick", "plain"], profiles.Select(profile => profile.Name));
        Assert.Equal(["quickstart", "a b"], profiles[0].Arguments);
        Assert.Equal("off", profiles[0].Environment["HORIZON_AUDIO"]);
        string project = Path.Combine(folder, "App.csproj");
        Assert.Equal(Path.Combine(folder, "run"), profiles[0].WorkingFolder(project));
        Assert.Null(profiles[1].WorkingFolder(project));
        Assert.Empty(DotnetLaunch.ProfilesIn("not json"));
    }

    [Fact]
    public void ALaunchProfileIsWrittenIntoTheFileKeepingTheRest()
    {
        string project = Path.Combine(folder, "App.csproj");
        DotnetLaunch.Save(project, new DotnetLaunchProfile("App", "quickstart", null, new Dictionary<string, string>()));
        Assert.True(File.Exists(Path.Combine(folder, "Properties", "launchSettings.json")));
        DotnetLaunchProfile made = Assert.Single(DotnetLaunch.Profiles(project));
        Assert.Equal(["quickstart"], made.Arguments);

        string kept = DotnetLaunch.WithProfile("""{ "$schema": "x", "profiles": { "web": { "commandName": "Project", "applicationUrl": "http://localhost:5000", "commandLineArgs": "old" } } }""",
            new DotnetLaunchProfile("web", "new", "out", new Dictionary<string, string> { ["A"] = "1" }));
        Assert.Contains("\"applicationUrl\": \"http://localhost:5000\"", kept);
        Assert.Contains("\"$schema\": \"x\"", kept);
        DotnetLaunchProfile changed = Assert.Single(DotnetLaunch.ProfilesIn(kept));
        Assert.Equal(new DotnetLaunchProfile("web", "new", "out", changed.Environment) { ApplicationUrl = "http://localhost:5000" }, changed);
        Assert.Equal("1", changed.Environment["A"]);

        // What's emptied comes out of the profile
        Assert.DoesNotContain("commandLineArgs", DotnetLaunch.WithProfile(kept, new DotnetLaunchProfile("web", string.Empty, null, new Dictionary<string, string>())));
    }

    [Fact]
    public void TheLaunchProfilePickedIsWhatRunsAndDebugsStartWith()
    {
        string project = Path.Combine(folder, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(folder, "Properties"));
        File.WriteAllText(DotnetLaunch.SettingsOf(project), """{ "profiles": { "first": { "commandName": "Project", "commandLineArgs": "one" }, "second": { "commandName": "Project", "commandLineArgs": "two", "workingDirectory": ".." } } }""");

        Assert.Equal("first", DotnetLaunch.For(project, null)?.Name);
        Assert.Equal("first", DotnetLaunch.For(project, "gone")?.Name);
        DotnetLaunchProfile second = DotnetLaunch.For(project, "second")!;
        Assert.Equal(["two"], DotnetLaunch.ArgumentsFor(Path.Combine(folder, "App.dll"), second, null));
        Assert.Equal(["--window", "two"], DotnetLaunch.ArgumentsFor(Path.Combine(folder, "Hex.dll"), second, Path.Combine(folder, "Hex.exe")));

        HexTask first = HexTasks.ProjectTask(project, "Release", DotnetLaunch.For(project, null));
        Assert.Equal("dotnet run --project \"App.csproj\" -c Release --launch-profile \"first\"", first.Command);
        Assert.Equal(folder, first.WorkingFolder());

        HexTask above = HexTasks.ProjectTask(project, HexWorkspace.DEBUG, second);
        Assert.Equal(Path.GetDirectoryName(folder), above.WorkingFolder());
        Assert.Equal($"dotnet run --project \"{Path.Combine(Path.GetFileName(folder), "App.csproj")}\" --launch-profile \"second\"", above.Command);
    }

    [Fact]
    public void TheProfilePickedIsKeptWithTheWorkspace()
    {
        string file = Path.Combine(folder, "game.hex-workspace");
        var workspace = new HexWorkspace();
        workspace.Add(folder);
        workspace.LaunchProfiles[Path.Combine(folder, "Game", "Game.csproj")] = "quick";
        workspace.Save(file);

        Assert.Contains("profile: \"quick\"", File.ReadAllText(file));
        Assert.Equal("quick", HexWorkspace.Load(file).LaunchProfiles[Path.Combine(folder, "Game", "Game.csproj")]);

        workspace.LaunchProfiles.Clear();
        workspace.Save(file);
        Assert.DoesNotContain("launch_profiles", File.ReadAllText(file));
    }

    [Fact]
    public void NugetsAnswerIsReadNewestVersionFirst()
    {
        List<NugetPackage> found = NugetSearch.Parse("""
            { "totalHits": 2, "data": [
              { "id": "Newtonsoft.Json", "version": "13.0.3", "description": "Json.NET", "totalDownloads": 5000000000,
                "versions": [ { "version": "12.0.1", "downloads": 1 }, { "version": "13.0.3", "downloads": 2 } ] },
              { "id": "Tiny", "version": "1.0.0" }
            ] }
            """);

        Assert.Equal(2, found.Count);
        Assert.Equal(["13.0.3", "12.0.1"], found[0].Versions);
        Assert.Equal(["1.0.0"], found[1].Versions);
        Assert.Equal("5B", NugetSearch.Short(found[0].Downloads));
        Assert.Equal("1.2M", NugetSearch.Short(1_234_567));
        Assert.Equal("999", NugetSearch.Short(999));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }
}
