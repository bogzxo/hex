using System.Diagnostics;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Go to All (Ctrl+T) over the harness's index of a little workspace in a temp folder. The exact name first, a type over
/// a member as good, the capitals and the letters in pieces found, Container.Name narrowing, nothing but code listed,
/// and a workspace the size of the engine's searched in well under a frame or two.
/// </summary>
public sealed class HexGoToAllTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-gotoall-" + Guid.NewGuid().ToString("N")[..8]);

    private const string LOOP = """
        namespace Demo;

        public sealed class SimulationLoop
        {
            public void WaitUntil(double moment) { }

            public void Simulate() { }
        }
        """;

    private const string NAVIGATION = """
        namespace Demo;

        internal sealed class Navigation
        {
            public void GoToDefinition() { }

            public void SimulationLoopStarted() { }
        }
        """;

    private const string NOTES = """
        # SimulationLoop

        A heading named like the class, which Go to All has no business listing.
        """;

    public HexGoToAllTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SimulationLoop.cs"), LOOP);
        File.WriteAllText(Path.Combine(folder, "Navigation.cs"), NAVIGATION);
        File.WriteAllText(Path.Combine(folder, "notes.md"), NOTES);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    private HexIndexSnapshot Index() => new HexCodeIndex(() => [folder]) { StaleAfter = 0 }.Current();

    private static string[] Names(List<(HexIndexedFile File, HexSymbol Symbol, int Score)> found) => [.. found.Select(hit => hit.Symbol.Full)];

    [Fact]
    public void The_exact_name_comes_first_and_markdown_headings_are_left_out()
    {
        string[] found = Names(HexCodeIndex.GoToAll(Index(), "SimulationLoop", 20));
        Assert.Equal("SimulationLoop", found[0]);
        Assert.Contains("Navigation.SimulationLoopStarted", found);
        Assert.Single(found, name => name == "SimulationLoop");
    }

    [Fact]
    public void The_capitals_and_the_letters_in_pieces_find_a_name()
    {
        Assert.Equal(["Navigation.GoToDefinition"], Names(HexCodeIndex.GoToAll(Index(), "gtd", 20)));
        Assert.Contains("SimulationLoop", Names(HexCodeIndex.GoToAll(Index(), "simloop", 20)));
        Assert.Empty(HexCodeIndex.GoToAll(Index(), "xyzzy", 20));
    }

    [Fact]
    public void Container_dot_name_narrows_to_what_is_inside()
    {
        string[] found = Names(HexCodeIndex.GoToAll(Index(), "SimulationLoop.Wait", 20));
        Assert.Equal(["SimulationLoop.WaitUntil"], found);
    }

    [Fact]
    public void Loose_scores_rank_below_the_name_itself()
    {
        Assert.True(HexCodeIndex.LooseScore("GoToDefinition", "GoTo") > HexCodeIndex.LooseScore("GoToDefinition", "gtd"));
        Assert.True(HexCodeIndex.LooseScore("GoToDefinition", "gtd") > HexCodeIndex.LooseScore("GoToDefinition", "godef"));
        Assert.True(HexCodeIndex.LooseScore("GoToDefinition", "godef") > 0);
        Assert.Equal(0, HexCodeIndex.LooseScore("GoToDefinition", "gdx"));
    }

    [Fact]
    public void A_cancelled_search_comes_back_empty()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Empty(HexCodeIndex.GoToAll(Index(), "SimulationLoop", 20, cancel.Token));
    }

    [Fact]
    public void A_workspace_the_size_of_the_engine_is_searched_quickly()
    {
        // 1,100 files of 30 members each, about what the engine's repository declares
        string big = Path.Combine(folder, "big");
        Directory.CreateDirectory(big);
        for (int i = 0; i < 1100; i++)
        {
            var text = new System.Text.StringBuilder($"namespace Demo;\n\npublic sealed class Thing{i}\n{{\n");
            for (int m = 0; m < 30; m++)
                text.Append($"    public void DoSomethingNumber{m}With{i}() {{ }}\n\n");
            File.WriteAllText(Path.Combine(big, $"Thing{i}.cs"), text.Append("}\n").ToString());
        }

        HexIndexSnapshot index = Index();
        HexCodeIndex.GoToAll(index, "warm", 200);

        var clock = Stopwatch.StartNew();
        foreach (string query in new[] { "d", "dsn", "DoSomethingNumber12", "thing99", "dsnw", "Thing5.Do" })
            HexCodeIndex.GoToAll(index, query, 200);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1500, $"six searches took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("Thing99", HexCodeIndex.GoToAll(index, "thing99", 200)[0].Symbol.Name);
    }
}
