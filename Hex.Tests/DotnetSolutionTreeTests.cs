using Hex.Core;
using Hex.Core.Projects;

namespace Hex.Tests;

/// <summary>
/// The Solution view's reading of a .sln and an .slnx in a temp folder, its folders, its projects and what they depend
/// on, and a project that wasn't written since kept as it was read.
/// </summary>
public sealed class DotnetSolutionTreeTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-solution-").FullName;

    private string Write(string path, string text)
    {
        string full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private void Projects()
    {
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
                <PackageReference Include="Serilog" Version="4.0.1" />
                <PackageReference Include="Humanizer">
                  <Version>2.14.1</Version>
                </PackageReference>
                <PackageReference Include="Central" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("Directory.Packages.props", """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Central" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
    }

    [Fact]
    public void SlnHasItsFoldersProjectsAndDependencies()
    {
        Projects();
        string sln = Write("Both.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Libraries", "Libraries", "{22222222-2222-2222-2222-222222222222}"
            	ProjectSection(SolutionItems) = preProject
            		Directory.Packages.props = Directory.Packages.props
            	EndProjectSection
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Gone", "Gone\Gone.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Profile|Any CPU = Profile|Any CPU
            	EndGlobalSection
            	GlobalSection(NestedProjects) = preSolution
            		{33333333-3333-3333-3333-333333333333} = {22222222-2222-2222-2222-222222222222}
            	EndGlobalSection
            EndGlobal
            """);

        DotnetSolutionTree tree = DotnetSolutionTree.Read(sln)!;
        Assert.Equal(["Debug", "Profile"], tree.Configurations);
        Assert.Equal(["App"], tree.Root.Projects.Select(project => project.Name));
        SolutionFolder libraries = Assert.Single(tree.Root.Folders);
        Assert.Equal("Libraries", libraries.Name);
        Assert.Equal(["Lib"], libraries.Projects.Select(project => project.Name));
        Assert.Equal([Path.Combine(folder, "Directory.Packages.props")], libraries.Files);

        SolutionProject app = tree.Root.Projects[0];
        Assert.True(app.IsProgram);
        Assert.False(libraries.Projects[0].IsProgram);
        Assert.Equal([Path.Combine(folder, "Lib", "Lib.csproj")], app.References);
        Assert.Equal([new SolutionPackage("Central", "1.2.3"), new SolutionPackage("Humanizer", "2.14.1"), new SolutionPackage("Serilog", "4.0.1")], app.Packages);
    }

    [Fact]
    public void SlnxNestsItsFoldersByName()
    {
        Projects();
        string slnx = Write("Both.slnx", """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
              </Configurations>
              <Folder Name="/src/">
                <Project Path="App/App.csproj" />
              </Folder>
              <Folder Name="/src/libs/">
                <Project Path="Lib/Lib.csproj" />
                <File Path="Directory.Packages.props" />
              </Folder>
            </Solution>
            """);

        DotnetSolutionTree tree = DotnetSolutionTree.Read(slnx)!;
        SolutionFolder src = Assert.Single(tree.Root.Folders);
        Assert.Equal("src", src.Name);
        Assert.Equal(["App"], src.Projects.Select(project => project.Name));
        SolutionFolder libs = Assert.Single(src.Folders);
        Assert.Equal(["Lib"], libs.Projects.Select(project => project.Name));
        Assert.Single(libs.Files);
        Assert.Equal(["Debug", "Release"], tree.Configurations);
        Assert.Equal(2, tree.Projects.Count());
    }

    [Fact]
    public void AProjectNotWrittenSinceIsTheOneReadBefore()
    {
        Projects();
        string slnx = Write("Both.slnx", """<Solution><Project Path="App/App.csproj" /><Project Path="Lib/Lib.csproj" /></Solution>""");
        DotnetSolutionTree first = DotnetSolutionTree.Read(slnx)!;

        string lib = Path.Combine(folder, "Lib", "Lib.csproj");
        File.WriteAllText(lib, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""");
        File.SetLastWriteTimeUtc(lib, DateTime.UtcNow.AddMinutes(1));

        DotnetSolutionTree second = DotnetSolutionTree.Read(slnx, first)!;
        Assert.Same(first.Projects.First(project => project.Name == "App"), second.Projects.First(project => project.Name == "App"));
        Assert.True(second.Projects.First(project => project.Name == "Lib").IsProgram);
    }

    [Fact]
    public void TheOnlySolutionTakesTheSlnxOverTheSlnOfItsName()
    {
        Write("Both.sln", string.Empty);
        Write("Both.slnx", "<Solution />");
        Assert.Equal(Path.Combine(folder, "Both.slnx"), DotnetSolutionTree.OnlySolutionIn([folder]));

        Write("Other.sln", string.Empty);
        Assert.Null(DotnetSolutionTree.OnlySolutionIn([folder]));
        Assert.Equal(2, DotnetSolutionTree.SolutionsIn([folder]).Count);
    }

    [Fact]
    public void AWorkspaceKeepsItsSolutionViewAndConfiguration()
    {
        var workspace = new HexWorkspace { SolutionView = false, Solution = Path.Combine(folder, "Both.sln"), Configuration = "Release" };
        workspace.Folders.Add(folder);
        var read = new HexWorkspace();
        read.Read(workspace.Write(folder), folder);
        Assert.False(read.SolutionView);
        Assert.Equal(Path.Combine(folder, "Both.sln"), read.Solution);
        Assert.Equal("Release", read.Configuration);

        var plain = new HexWorkspace();
        plain.Read(new HexWorkspace().Write(folder), folder);
        Assert.Null(plain.SolutionView);
        Assert.Equal(HexWorkspace.DEBUG, plain.Configuration);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DependenciesUseImportedPropertiesConditionsAndCentralVersions()
    {
        string library = Write("Engine/Lib.csproj", "<Project />");
        Write("Dependency.props", """
            <Project>
              <PropertyGroup><EngineRoot>$(MSBuildThisFileDirectory)Engine/</EngineRoot></PropertyGroup>
              <ItemGroup><PackageVersion Include="Central" Version="2.0" /></ItemGroup>
            </Project>
            """);
        string project = Write("App/App.csproj", """
            <Project>
              <Import Project="../Dependency.props" />
              <ItemGroup Condition="'$(Configuration)' == 'Release'">
                <ProjectReference Include="$(EngineRoot)Lib.csproj" />
                <PackageReference Include="Central" />
                <PackageReference Include="Override" Version="1.0" VersionOverride="3.0" />
              </ItemGroup>
            </Project>
            """);
        var release = await DotnetSolutionTree.EvaluateDependencies(project, "Release");
        Assert.Equal(Path.GetFullPath(library), Path.GetFullPath(Assert.Single(release.References)));
        Assert.Equal([new SolutionPackage("Central", "2.0"), new SolutionPackage("Override", "3.0")], release.Packages);
        var debug = await DotnetSolutionTree.EvaluateDependencies(project, "Debug");
        Assert.Empty(debug.References);
        Assert.Empty(debug.Packages);
        Assert.Empty(DotnetSolutionTree.ReadProject(project, []).References);
    }

    [Fact]
    public void DirectoryBuildPropsInvalidatesSolutionProjectCache()
    {
        Projects();
        string props = Write("Directory.Build.props", "<Project />");
        string solution = Write("Both.slnx", """<Solution><Project Path="App/App.csproj" /></Solution>""");
        var before = DotnetSolutionTree.Read(solution)!;
        File.SetLastWriteTimeUtc(props, DateTime.UtcNow.AddMinutes(2));
        var after = DotnetSolutionTree.Read(solution, before)!;
        Assert.NotSame(before.Projects.Single(), after.Projects.Single());
        Assert.True(DotnetSolutionTree.Matters(props));
    }
    [Fact]
    public async Task EvaluatedItemsRespectLinksConditionsRemovalsAndVisibility()
    {
        string linked = Write("Shared/Logic.cs", "class Logic {}");
        Write("App/Removed.cs", "class Removed {}");
        Write("App/Hidden.cs", "class Hidden {}");
        string releaseFile = Write("App/Release.cs", "class Release {}");
        string project = Write("App/Items.csproj", """
            <Project>
              <ItemGroup>
                <Compile Include="../Shared/Logic.cs" Link="Engine/Logic.cs" />
                <Compile Include="Removed.cs" /><Compile Remove="Removed.cs" />
                <Compile Include="Hidden.cs" Visible="false" />
                <Compile Include="Release.cs" Condition="'$(Configuration)' == 'Release'" />
              </ItemGroup>
            </Project>
            """);
        var debug = await DotnetSolutionTree.EvaluateDependencies(project, "Debug");
        var only = Assert.Single(debug.Files);
        Assert.Equal(Path.GetFullPath(linked), only.Path);
        Assert.Equal("Engine/Logic.cs", only.DisplayPath);
        Assert.Equal("Compile", only.ItemTypes);
        Assert.True(only.Linked);
        var release = await DotnetSolutionTree.EvaluateDependencies(project, "Release");
        Assert.Equal(2, release.Files.Count);
        Assert.Contains(release.Files, file => file.Path == Path.GetFullPath(releaseFile) && !file.Linked);
        Assert.DoesNotContain(release.Files, file => file.DisplayPath.Contains("Removed") || file.DisplayPath.Contains("Hidden"));
    }
}
