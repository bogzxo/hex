using Hex.Core;
using Hex.Dawn;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// The automations of a tasks.hor read out of it, what each runs and what the palette calls it, the shells they name,
/// the file's tasks written again by Set Up F5 without the automations and their functions going with them, and the
/// workspace's own tasks.hor, the push of every repository, reading as the automations it means to be.
/// </summary>
public sealed class HexAutomationTests
{
    private static List<HexAutomation> Read(string code, string root = "C:/work")
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, message) = runtime.Evaluate(code);
        Assert.True(success, message);
        return HexAutomation.Read(Assert.IsType<ObjectValue>(runtime.UserScope.Lookup("automations")), root, root + "/tasks.hor");
    }

    [Fact]
    public void EachKindReadsWithWhatItRuns()
    {
        List<HexAutomation> read = Read("""
            let automations = {
                push_all: { title: "Push all", key: "Ctrl+Alt+P", run: hex => go(hex) },
                status: { command: "git status -sb", shell: "bash", folder: "hex" },
                tidy: { title: "Tidy", script: "scripts/tidy.hor" },
                nothing: { title: "Does nothing" },
                not_one: 3,
            };
            func go(hex) { return 1; }
            """);

        Assert.Equal(["push_all", "status", "tidy"], read.Select(automation => automation.Name));
        Assert.True(read[0] is { Title: "Push all", Key: "Ctrl+Alt+P", HasRun: true, IsHidl: true });
        Assert.True(read[1] is { Title: "Status", Command: "git status -sb", Shell: "bash", IsHidl: false, HasRun: false });
        Assert.Equal(Path.GetFullPath("C:/work/hex"), read[1].WorkingFolder());
        Assert.True(read[2] is { Title: "Tidy", IsHidl: true });
        Assert.Equal(Path.GetFullPath("C:/work/scripts/tidy.hor"), read[2].ScriptPath());
        Assert.Equal(Path.GetFullPath("C:/work"), read[0].WorkingFolder());
    }

    [Theory]
    [InlineData("push_all", "Push all")]
    [InlineData("status", "Status")]
    [InlineData("_", "_")]
    public void AKeyWithoutATitleIsCalledAfterItself(string name, string title) => Assert.Equal(title, HexAutomation.TitleOf(name));

    [Fact]
    public void BashIsGitBashOnWindowsAndTheRestAreTheirOwnIds()
    {
        Assert.Null(HexTasks.ShellProfile(""));
        Assert.Equal(OperatingSystem.IsWindows() ? "git_bash" : "bash", HexTasks.ShellProfile("bash"));
        Assert.Equal(OperatingSystem.IsWindows() ? "git_bash" : "bash", HexTasks.ShellProfile("Bash"));
        Assert.Equal("pwsh", HexTasks.ShellProfile("pwsh"));
        Assert.Equal("wsl:Ubuntu", HexTasks.ShellProfile(" wsl:Ubuntu "));
    }

    private const string FILE = """
        // The tasks, with a comment
        let tasks = {
            build: { command: "make", group: "build" }, // and a brace } in a comment
        };

        // The automations, with a brace in a text
        let automations = {
            push: { title: "Push {it}", run: hex => go(hex, "}") },
        };

        func go(hex, brace) { return `{brace}`; }
        """;

    [Fact]
    public void TheTasksAreFoundWholeBracesInTextsAndCommentsAside()
    {
        var span = Assert.NotNull(TasksFileText.Span(FILE, "tasks"));
        string declared = FILE[span.Start..span.End];
        Assert.StartsWith("let tasks = {", declared);
        Assert.EndsWith("// and a brace } in a comment\n};", declared.ReplaceLineEndings("\n"));

        var automations = Assert.NotNull(TasksFileText.Span(FILE, "automations"));
        Assert.EndsWith("};", FILE[automations.Start..automations.End]);
        Assert.Null(TasksFileText.Span(FILE, "task"));
        Assert.Null(TasksFileText.Span("let tasksmore = {};", "tasks"));
    }

    [Fact]
    public void WritingTheTasksAgainKeepsTheRestOfTheFile()
    {
        string written = TasksFileText.Replace(FILE, "tasks", "let tasks = {\n    run: { command: \"go\" },\n};\n");

        Assert.Contains("// The tasks, with a comment", written);
        Assert.Contains("run: { command: \"go\" }", written);
        Assert.DoesNotContain("make", written);
        Assert.Contains("func go(hex, brace)", written);

        var runtime = new HIDLRuntime { Output = null };
        Assert.True(runtime.Evaluate(written).success);
        Assert.Single(HexAutomation.Read(Assert.IsType<ObjectValue>(runtime.UserScope.Lookup("automations")), "C:/work", "C:/work/tasks.hor"));
    }

    [Fact]
    public void AFileWithoutTasksHasThemAddedAtTheEnd()
    {
        string written = TasksFileText.Replace("let automations = { a: { command: \"x\" } };\n", "tasks", "let tasks = {};");
        Assert.Equal("let automations = { a: { command: \"x\" } };\n\nlet tasks = {};\n", written);
        Assert.Equal("let x = 1;\nlet tasks = 2;\nlet y = 3;", TasksFileText.Replace("let x = 1;\nlet tasks = 5;\nlet y = 3;", "tasks", "let tasks = 2;"));
        Assert.Equal("let tasks = 2\nlet y = 3", TasksFileText.Replace("let tasks = 5\nlet y = 3", "tasks", "let tasks = 2"));
    }

    [Fact]
    public void TheWorkspacesPushOfEveryRepositoryReadsAsItsAutomations()
    {
        // The workspace's folder, over the hex repository, when the tests run inside one
        string? at = AppContext.BaseDirectory;
        while (at is not null && !File.Exists(Path.Combine(at, "push-all.ps1")))
            at = Path.GetDirectoryName(at);
        if (at is null || !File.Exists(Path.Combine(at, "tasks.hor")))
            return;

        var runtime = new HIDLRuntime { Output = null };
        var (success, message) = runtime.Evaluate(File.ReadAllText(Path.Combine(at, "tasks.hor")));
        Assert.True(success, message);
        List<HexAutomation> read = HexAutomation.Read(Assert.IsType<ObjectValue>(runtime.UserScope.Lookup("automations")), at, Path.Combine(at, "tasks.hor"));
        Assert.Equal(["Push all", "Push all (dry run)", "Status of every repository"], read.Select(automation => automation.Title));
        Assert.True(read[0].HasRun && read[1].HasRun && read[2] is { Shell: "bash", IsHidl: false });
    }
}
