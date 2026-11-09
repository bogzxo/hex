using System.Text;
using Hex.Core.Projects;

namespace Hex.Tests;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void Removing_one_expression_preserves_metadata_comments_and_other_specs()
    {
        const string before = "<Project>\r\n  <!-- keep -->\r\n  <ItemGroup Condition=\"'$(Configuration)' == 'Debug'\">\r\n    <ProjectReference Include='$(Root)A.csproj;$(Root)B.csproj' Private='false' />\r\n  </ItemGroup>\r\n</Project>";
        Assert.Equal(before.Replace("$(Root)A.csproj;", ""), ProjectFileEdits.RemoveProjectReference(before, spec => spec == "$(Root)A.csproj"));
    }

    [Fact]
    public async Task Reference_edits_preserve_root_switching_and_utf8_bom()
    {
        string folder = Directory.CreateTempSubdirectory("hex-ref-edit-").FullName;
        try
        {
            foreach (string root in new[] { "shared", "vendored" })
            {
                Directory.CreateDirectory(Path.Combine(folder, root));
                foreach (string name in new[] { "A", "B" })
                    File.WriteAllText(Path.Combine(folder, root, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            }
            string project = Path.Combine(folder, "App.csproj"), target = Path.Combine(folder, "shared", "B.csproj");
            const string original = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <!-- keep this comment -->\r\n  <PropertyGroup>\r\n    <TargetFramework>net11.0</TargetFramework>\r\n    <HorizonRoot Condition=\"'$(UseSubmodules)' != 'true'\">shared/</HorizonRoot>\r\n    <HorizonRoot Condition=\"'$(UseSubmodules)' == 'true'\">vendored/</HorizonRoot>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n    <ProjectReference Include=\"$(HorizonRoot)A.csproj\" />\r\n  </ItemGroup>\r\n</Project>\r\n";
            File.WriteAllText(project, original, new UTF8Encoding(true));
            await ProjectReferences.Set(project, target, true, "Debug");
            string added = File.ReadAllText(project);
            Assert.Contains("Include=\"$(HorizonRoot)B.csproj\"", added);
            Assert.True(File.ReadAllBytes(project).AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
            await ProjectReferences.Set(project, target, true, "Debug");
            Assert.Equal(added, File.ReadAllText(project));
            var (exit, output) = await DotnetCli.Run(folder, ["msbuild", project, "-nologo", "-p:UseSubmodules=true", "-getItem:ProjectReference"], TimeSpan.FromSeconds(60));
            Assert.Equal(0, exit);
            Assert.Contains(DotnetEvaluation.Parse(project, output).Items, item => Path.GetFullPath(item.FullPath) == Path.Combine(folder, "vendored", "B.csproj"));
            await ProjectReferences.Set(project, target, false, "Debug");
            Assert.Equal(original, File.ReadAllText(project));

            // An explicit exclusion later in evaluation defeats the requested add.
            // It must be reported as failure with the original file restored.
            string excluded = original.Replace("</Project>", "  <ItemGroup><ProjectReference Remove=\"$(HorizonRoot)B.csproj\" /></ItemGroup>\r\n</Project>");
            File.WriteAllText(project, excluded, new UTF8Encoding(true));
            byte[] excludedBytes = File.ReadAllBytes(project);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectReferences.Set(project, target, true, "Debug"));
            Assert.Equal(excludedBytes, File.ReadAllBytes(project));

            string imported = original.Replace("    <ProjectReference Include=\"$(HorizonRoot)A.csproj\" />\r\n", "")
                .Replace("</Project>", "  <Import Project=\"refs.props\" />\r\n</Project>");
            File.WriteAllText(Path.Combine(folder, "refs.props"), "<Project><ItemGroup><ProjectReference Include=\"$(HorizonRoot)A.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(project, imported, new UTF8Encoding(true));
            byte[] importedBytes = File.ReadAllBytes(project);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectReferences.Set(project, Path.Combine(folder, "shared", "A.csproj"), false, "Debug"));
            Assert.Contains("defining project", error.Message);
            Assert.Equal(importedBytes, File.ReadAllBytes(project));

            string multi = original.Replace("<TargetFramework>net11.0</TargetFramework>", "<TargetFrameworks>net10.0;net11.0</TargetFrameworks>")
                .Replace("  </ItemGroup>", "    <ProjectReference Include=\"$(HorizonRoot)B.csproj\" Condition=\"'$(TargetFramework)' == 'net11.0'\" />\r\n  </ItemGroup>");
            File.WriteAllText(project, multi, Encoding.Unicode);
            await ProjectReferences.Set(project, target, false, "Debug");
            Assert.DoesNotContain("B.csproj", File.ReadAllText(project));
            Assert.Contains("$(HorizonRoot)A.csproj", File.ReadAllText(project));
            Assert.True(File.ReadAllBytes(project).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
        }
        finally { Directory.Delete(folder, true); }
    }
}
