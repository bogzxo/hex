using Hex.Core.Projects;

namespace Hex.Tests;

/// <summary>
/// The project model on real projects in a temp folder, evaluated by the SDK, changed the way the properties panel
/// changes them and built with dotnet, so a file set to Copy if newer is proved to land next to the program.
/// </summary>
public sealed class DotnetProjectsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-projects-").FullName;
    private readonly DotnetProjects projects = new();

    private static string Framework => $"net{Environment.Version.Major}.0";

    private string Project(string name = "app")
    {
        string project = Path.Combine(folder, name + ".csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{Framework}</TargetFramework>
              </PropertyGroup>

            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"hi\");\n");
        File.WriteAllText(Path.Combine(folder, "data.txt"), "some data\n");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.json"), "{}\n");
        return project;
    }

    [Fact]
    public async Task CopyIfNewerLandsInBinAndDoNotCopyPutsTheProjectBack()
    {
        string project = Project();
        string original = File.ReadAllText(project);
        string data = Path.Combine(folder, "data.txt");

        DotnetEvaluation before = await projects.Evaluate(project);
        Assert.Equal("None", before.BuildItemOf(data)!.Type);
        Assert.Equal(DotnetEvaluation.NEVER, before.BuildItemOf(data)!.CopyToOutput);
        Assert.Equal("Compile", before.BuildItemOf(Path.Combine(folder, "Program.cs"))!.Type);
        Assert.Equal(Path.Combine(folder, "bin", "Debug", Framework) + Path.DirectorySeparatorChar, before.OutputFolder);
        Assert.True(before.IsProgram);
        Assert.Contains("EmbeddedResource", before.ItemTypes);
        Assert.Same(before, projects.Known(project));

        DotnetSettingsResult copied = await projects.Apply(project, [data], null, DotnetEvaluation.IF_NEWER);
        Assert.True(copied.Written);
        Assert.Empty(copied.NotTaken);
        Assert.Equal(DotnetEvaluation.IF_NEWER, copied.Evaluation.BuildItemOf(data)!.CopyToOutput);
        Assert.Contains("<None Update=\"data.txt\">", File.ReadAllText(project));

        var (built, said) = await DotnetCli.Run(folder, ["build", project, "-nologo", "-v", "q"], TimeSpan.FromMinutes(3));
        Assert.True(built == 0, said);
        Assert.Equal("some data\n", File.ReadAllText(Path.Combine(before.OutputFolder, "data.txt")));

        DotnetSettingsResult back = await projects.Apply(project, [data], null, DotnetEvaluation.NEVER);
        Assert.Equal(DotnetEvaluation.NEVER, back.Evaluation.BuildItemOf(data)!.CopyToOutput);
        Assert.Equal(original, File.ReadAllText(project));
    }

    [Fact]
    public async Task BuildActionMovesAFileAndAFolderOfFilesGoesAtOnce()
    {
        string project = Project();
        string data = Path.Combine(folder, "data.txt"), json = Path.Combine(folder, "sub", "b.json");

        DotnetSettingsResult content = await projects.Apply(project, [data, json], "Content", DotnetEvaluation.ALWAYS);
        Assert.Empty(content.NotTaken);
        foreach (string file in (string[])[data, json])
        {
            Assert.Equal("Content", content.Evaluation.BuildItemOf(file)!.Type);
            Assert.Equal(DotnetEvaluation.ALWAYS, content.Evaluation.BuildItemOf(file)!.CopyToOutput);
            Assert.DoesNotContain(content.Evaluation.ItemsOf(file), item => item.Type == "None");
        }

        DotnetSettingsResult none = await projects.Apply(project, [data, json], "None", DotnetEvaluation.NEVER);
        Assert.Equal("None", none.Evaluation.BuildItemOf(json)!.Type);
        Assert.DoesNotContain("Content", File.ReadAllText(project));
    }

    [Fact]
    public async Task ASettingOfAnImportIsSaidAndNeverWrittenOver()
    {
        string project = Project();
        string original = File.ReadAllText(project);
        string json = Path.Combine(folder, "sub", "b.json");
        string targets = Path.Combine(folder, "Directory.Build.targets");
        string imported = "<Project>\n  <ItemGroup>\n    <None Update=\"sub\\b.json\" CopyToOutputDirectory=\"Always\" />\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(targets, imported);

        DotnetEvaluation evaluation = await projects.Evaluate(project);
        Assert.Equal(DotnetEvaluation.ALWAYS, evaluation.BuildItemOf(json)!.CopyToOutput);
        Assert.Equal(targets, DotnetProjects.ImportedFrom(evaluation, json, ProjectFileEdits.COPY_TO_OUTPUT));

        // The targets come after the project, nothing the project says wins over them, so it's said and the project left be
        DotnetSettingsResult never = await projects.Apply(project, [json], null, DotnetEvaluation.NEVER);
        Assert.Equal([json], never.NotTaken);
        Assert.Equal(original, File.ReadAllText(project));
        Assert.Equal(imported, File.ReadAllText(targets));

        // What the project can say over an import, a file of its own made Copy always over Directory.Build.props
        string props = Path.Combine(folder, "Directory.Build.props");
        File.Move(targets, props);
        string data = Path.Combine(folder, "data.txt");
        DotnetSettingsResult always = await projects.Apply(project, [data, json], null, DotnetEvaluation.ALWAYS);
        Assert.Empty(always.NotTaken);
        Assert.Equal(imported, File.ReadAllText(props));
    }

    [Fact]
    public async Task WhatsKeptIsForgottenWhenTheFolderOrTheImportsChange()
    {
        string project = Project();
        DotnetEvaluation first = await projects.Evaluate(project);
        Assert.Same(first, projects.Known(project));

        // A new file in the folder, which a glob takes in
        string added = Path.Combine(folder, "more.txt");
        File.WriteAllText(added, "x");
        Assert.True(SpinWait.SpinUntil(() => projects.Known(project) is null, TimeSpan.FromSeconds(5)));
        Assert.NotNull((await projects.Evaluate(project)).BuildItemOf(added));

        // A Directory.Build.props written over it
        DotnetEvaluation second = await projects.Evaluate(project);
        File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />\n");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "Directory.Build.props"), DateTime.UtcNow.AddSeconds(5));
        SpinWait.SpinUntil(() => projects.Known(project) is null, TimeSpan.FromSeconds(5));
        Assert.Null(projects.Known(project));
        Assert.NotSame(second, await projects.Evaluate(project));
    }

    [Fact]
    public void AFilesProjectIsTheNearestSdkProjectOverIt()
    {
        string project = Project();
        Assert.Equal(project, DotnetProjects.ProjectOf(Path.Combine(folder, "sub", "b.json")));
        Assert.Equal(project, DotnetProjects.ProjectOf(Path.Combine(folder, "sub")));

        string inner = Path.Combine(folder, "sub", "Inner.csproj");
        File.WriteAllText(inner, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal(inner, DotnetProjects.ProjectOf(Path.Combine(folder, "sub", "b.json")));

        File.WriteAllText(inner, "<Project ToolsVersion=\"15.0\"><Import Project=\"old.targets\" /></Project>");
        Assert.Equal(project, DotnetProjects.ProjectOf(Path.Combine(folder, "sub", "b.json")));
    }

    public void Dispose()
    {
        projects.Dispose();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }
}
