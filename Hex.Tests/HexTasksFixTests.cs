using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// The fix a D build that failed to link is offered, -i put in after its compiler so the modules it imports are compiled
/// too, and left alone when it has it already or isn't a D compiler at all.
/// </summary>
public sealed class HexTasksFixTests
{
    [Theory]
    [InlineData("dmd \"app.d\" -of=app.exe", "dmd -i \"app.d\" -of=app.exe")]
    [InlineData("dmd.exe app.d", "dmd.exe -i app.d")]
    [InlineData("ldc2 app.d -g", "ldc2 -i app.d -g")]
    [InlineData("ldmd2", "ldmd2 -i")]
    [InlineData("\"C:\\D\\dmd2\\windows\\bin64\\dmd.exe\" app.d", "\"C:\\D\\dmd2\\windows\\bin64\\dmd.exe\" -i app.d")]
    [InlineData("C:\\D\\dmd2\\windows\\bin64\\dmd.exe app.d", "C:\\D\\dmd2\\windows\\bin64\\dmd.exe -i app.d")]
    [InlineData("dmd -I=source app.d", "dmd -i -I=source app.d")]
    public void ADCompileGetsDashI(string command, string expected) => Assert.Equal(expected, HexTasks.WithImports(command));

    [Theory]
    [InlineData("dmd -i app.d")]
    [InlineData("dmd app.d -i=folder_tests")]
    [InlineData("dub build")]
    [InlineData("gcc main.c -o main")]
    [InlineData(".\\app.exe")]
    public void AnythingElseIsLeftAlone(string command) => Assert.Null(HexTasks.WithImports(command));

    [Fact]
    public void TheTasksTemplateHasACommaAfterEveryKeyAndReadsBack()
    {
        string folder = Directory.CreateTempSubdirectory("hex-tasks-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "app.csproj"), "<Project />");
            string written = HexTasks.TasksTemplate(folder);

            Assert.Contains("        terminal: \"panel\",\n    },\n};\n", written);
            var runtime = new Horizon.HIDL.HIDLRuntime { Output = null };
            Assert.True(runtime.Evaluate(written).success);
            var tasks = Assert.IsType<Horizon.HIDL.Runtime.ObjectValue>(runtime.UserScope.Lookup("tasks"));
            Assert.Equal(["build", "test", "run"], tasks.Properties.Keys);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
