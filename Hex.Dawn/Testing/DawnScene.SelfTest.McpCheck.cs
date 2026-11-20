using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The check after hex_edit through the real csharp-ls, timed, left out when csharp-ls isn't on this machine. An edit's
// answer says what the server said of the change, and it comes in a few seconds rather than the ten an agent sat through
// per edit: three edits one after another (a comment, a mistake, the mistake taken out), then three at once to different
// files, each timed on its own, and what each said is checked against what was done (clean, the error, clean).
internal sealed partial class DawnScene
{
    // The longest an edit and its check may take once the server has the project loaded
    private const float MCP_CHECK_MOST_SECONDS = 5.0f;

    private void TestMcpCheck(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_mcpcheck");
        string[] files = [.. new[] { "Program.cs", "Second.cs", "Third.cs" }.Select(name => Path.Combine(folder, name))];
        bool there = HexServerCatalog.Locate("csharp-ls") is not null;
        test.Run(() =>
        {
            if (!there)
            {
                Console.WriteLine("[Hex self-test] csharp-ls isn't on this machine, the check after an edit is skipped");
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "selftest_mcpcheck.csproj"), CSHARP_PROJECT);
            File.WriteAllText(files[0], "class Program\n{\n    static void Main()\n    {\n        int count = 3;\n        System.Console.WriteLine(count);\n    }\n}\n");
            File.WriteAllText(files[1], "class Second\n{\n    public int Value => 2;\n}\n");
            File.WriteAllText(files[2], "class Third\n{\n    public int Value => 3;\n}\n");
        });
        if (!there)
            return;

        // The server started and the project loaded by asking what's wrong with all three, its slow part
        Task<McpToolResult>? asked = null;
        test.Run(() => asked = McpTools.Call("hex_diagnostics", new JsonObject { ["paths"] = string.Join(",", files) }, CancellationToken.None));
        test.WaitFor(() => asked?.IsCompleted == true, 150.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] mcp-check: diagnostics said {(asked is { IsCompletedSuccessfully: true } ? asked.Result.AllText() : asked?.Exception?.ToString())}"));
        test.Check("mcp-check: the project's loaded, what's wrong with its files said", () =>
            asked is { IsCompletedSuccessfully: true } && asked.Result.AllText().Contains("0 errors", StringComparison.Ordinal));

        var times = new List<(string What, double Seconds, string Said)>();
        Task<McpToolResult>? going = null;
        var clock = new Stopwatch();
        void Edit(string what, string file, string old, string with)
        {
            test.Run(() =>
            {
                clock.Restart();
                going = McpTools.Call("hex_edit", new JsonObject { ["path"] = file, ["old_string"] = old, ["new_string"] = with }, CancellationToken.None);
            });
            test.WaitFor(() => going?.IsCompleted == true, 40.0f);
            test.Run(() =>
            {
                string said = going is { IsCompletedSuccessfully: true } ? going.Result.AllText() : "(no answer)";
                times.Add((what, clock.Elapsed.TotalSeconds, said));
                Console.WriteLine($"[Hex self-test] mcp-check: {what} took {clock.Elapsed.TotalSeconds:0.00} s: {said.ReplaceLineEndings(" | ")}");
            });
        }

        Edit("a comment", files[0], "        int count = 3;", "        // counted\n        int count = 3;");
        test.Check("mcp-check: an edit that's fine says clean, in a few seconds", () =>
            times.Count == 1 && times[0].Said.Contains("clean", StringComparison.Ordinal) && times[0].Seconds < MCP_CHECK_MOST_SECONDS);
        Edit("a mistake", files[0], "int count = 3;", "int count = \"three\";");
        test.Check("mcp-check: an edit with a mistake says the error at its line, in a few seconds", () =>
            times.Count == 2 && times[1].Said.Contains("1 error", StringComparison.Ordinal) && times[1].Said.Contains(":6:", StringComparison.Ordinal) && times[1].Seconds < MCP_CHECK_MOST_SECONDS);
        Edit("the mistake out", files[0], "int count = \"three\";", "int count = 3;");
        test.Check("mcp-check: the mistake taken out says clean again", () =>
            times.Count == 3 && times[2].Said.Contains("clean", StringComparison.Ordinal) && times[2].Seconds < MCP_CHECK_MOST_SECONDS);

        // Three at once, each on its own clock: one waiting on its server mustn't hold the others up
        var together = new Task<McpToolResult>?[3];
        var took = new double[3];
        test.Run(() =>
        {
            clock.Restart();
            for (int i = 0; i < 3; i++)
            {
                int each = i;
                string file = files[i];
                together[i] = McpTools.Call("hex_edit", new JsonObject { ["path"] = file, ["old_string"] = "class ", ["new_string"] = "sealed class " }, CancellationToken.None);
                together[i]!.ContinueWith(_ => took[each] = clock.Elapsed.TotalSeconds, TaskScheduler.Default);
            }
        });
        test.WaitFor(() => together.All(task => task?.IsCompleted == true), 40.0f);
        test.Settle(0.1f);
        test.Run(() => Console.WriteLine($"[Hex self-test] mcp-check: three at once took {string.Join(", ", took.Select(seconds => $"{seconds:0.00} s"))}: {string.Join(" || ", together.Select(task => task?.Result.AllText().ReplaceLineEndings(" | ")))}"));
        test.Check("mcp-check: three edits at once each answer in a few seconds, none queued behind another", () =>
            together.All(task => task is { IsCompletedSuccessfully: true } && task.Result.AllText().Contains("clean", StringComparison.Ordinal))
            && took.All(seconds => seconds > 0.0 && seconds < MCP_CHECK_MOST_SECONDS));

        // The server's copy of the text is the disk's: what it says after them all is about the file as it is
        test.Run(() => asked = McpTools.Call("hex_diagnostics", new JsonObject { ["paths"] = string.Join(",", files) }, CancellationToken.None));
        test.WaitFor(() => asked?.IsCompleted == true, 60.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] mcp-check: diagnostics after said {asked?.Result.AllText().ReplaceLineEndings(" | ")}"));
        test.Check("mcp-check: afterwards the server's text is the disk's, nothing wrong with any of them", () =>
            asked is { IsCompletedSuccessfully: true } && asked.Result.AllText().Contains("0 errors", StringComparison.Ordinal));

        test.Run(LanguageServers.StopAll);
    }
}
