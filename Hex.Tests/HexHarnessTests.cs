using Hex.Core;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// The agent harness's index over a little workspace in a temp folder, C#, HIDL, Python and Markdown. Files found and
/// left out, symbols ranked, search grouped and ranked, references without the comments and strings, a declaration read
/// to its end, the budget cut honestly, a change seen and logged, answers kept, the cache on disk and a language
/// server's symbols taken in.
/// </summary>
public sealed class HexHarnessTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-harness-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string cache;

    private const string SCHEMA = """
        using System.Text.Json.Nodes;

        namespace Demo;

        /// <summary>
        /// Checks arguments against a schema, the little of it tools need.
        /// </summary>
        internal static class McpSchema
        {
            /// <summary>What's wrong with some arguments, null when nothing is.</summary>
            public static string? Check(JsonObject schema, JsonObject arguments)
            {
                if (schema is null)
                {
                    return "no schema { at all";
                }

                return null;
            }

            public static int Count => 3;

            private static readonly string Name = "Check"; // Check in a comment
        }
        """;

    private const string QUEUE = """
        namespace Demo;

        internal sealed class Queue
        {
            public void QueueMcp(string call)
            {
                var wrong = McpSchema.Check(null, null);
                Log("QueueMcp");
            }

            private void Log(string text) { }
        }
        """;

    private const string SCRIPT = """
        def helper(x):
            return x + 1

        class Thing:
            def run(self):
                return helper(2)

        print(helper(1))
        """;

    private const string NOTES = """
        # Notes

        The first paragraph says what it's about.

        ## Check

        How Check is used.

        ## Other

        Something else.
        """;

    public HexHarnessTests()
    {
        cache = Path.Combine(folder, "..", Path.GetFileName(folder) + "-cache");
        Directory.CreateDirectory(Path.Combine(folder, "Core", "Mcp"));
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "Core", "Mcp", "McpSchema.cs"), SCHEMA);
        File.WriteAllText(Path.Combine(folder, "Core", "Queue.cs"), QUEUE);
        File.WriteAllText(Path.Combine(folder, "tool.py"), SCRIPT);
        File.WriteAllText(Path.Combine(folder, "notes.md"), NOTES);
        File.WriteAllText(Path.Combine(folder, "speed.hor"), "let speed = 4;\nfunc go(x) { return x * speed; }\n");
        File.WriteAllText(Path.Combine(folder, "bin", "Built.cs"), "class Built { public void Check() { } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
        try { Directory.Delete(cache, true); } catch (Exception) { }
    }

    private HexCodeIndex Index(string? cacheFolder = null) => new(() => [folder], cacheFolder) { StaleAfter = 0 };

    [Fact]
    public void The_index_has_the_text_files_and_leaves_bin_out()
    {
        HexIndexSnapshot index = Index().Current();
        string[] files = [.. index.Files.Select(file => file.Relative).Order(StringComparer.Ordinal)];
        Assert.Equal(["Core/Mcp/McpSchema.cs", "Core/Queue.cs", "notes.md", "speed.hor", "tool.py"], files);
    }

    [Fact]
    public void Symbols_know_what_they_are_inside_of()
    {
        HexIndexedFile schema = Index().Current().Files.Single(file => file.Relative.EndsWith("McpSchema.cs"));
        HexSymbol check = schema.Symbols.Single(symbol => symbol.Name == "Check");
        Assert.Equal("McpSchema.Check", check.Full);
        Assert.Equal("method", check.Kind);
        Assert.Contains(schema.Symbols, symbol => symbol.Name == "McpSchema" && symbol.IsType);
    }

    [Fact]
    public void Symbols_rank_the_exact_name_first_and_find_one_by_its_capitals()
    {
        HexCodeIndex index = Index();
        string said = index.Symbols("Check", null, null, 10, 4000).Text;
        Assert.StartsWith("2 declarations fit Check", said);
        Assert.Contains("\nCore/Mcp/McpSchema.cs:11 method McpSchema.Check", said);
        Assert.Contains("notes.md:5 string Notes.Check", said);

        string capitals = index.Symbols("QM", null, null, 10, 4000).Text;
        Assert.Contains("Core/Queue.cs:5 method Queue.QueueMcp", capitals);

        string narrowed = index.Symbols("Queue.Log", null, null, 10, 4000).Text;
        Assert.Contains("Queue.Log", narrowed);
        Assert.DoesNotContain("QueueMcp", narrowed);
    }

    [Fact]
    public void Search_puts_the_declaration_first_and_writes_each_path_once()
    {
        string said = Index().Search("Check", false, true, true, null, 0, 50, 4000).Text;
        string[] lines = said.Split('\n');
        Assert.Equal("Core/Mcp/McpSchema.cs (2)", lines[1]);
        Assert.StartsWith("  11*: public static string? Check", lines[2]);
        Assert.Single(lines, line => line.StartsWith("Core/Queue.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(folder, said);
    }

    [Fact]
    public void Search_narrows_by_glob_and_gives_context()
    {
        string said = Index().Search("helper", false, false, false, "*.py", 1, 50, 4000).Text;
        Assert.Contains("tool.py (3)", said);
        Assert.Contains("  2- ", said);
        Assert.DoesNotContain(".cs", said);
    }

    [Fact]
    public void References_leave_out_comments_and_strings()
    {
        string said = Index().References("Check", "*.cs", 50, 4000).Text;
        Assert.Contains("2 places in 2 files", said);
        Assert.Contains("  11*: ", said);
        Assert.DoesNotContain("in a comment", said);
    }

    [Fact]
    public void Read_takes_a_method_with_its_doc_comment_to_its_closing_brace()
    {
        string said = Index().Read(null, "McpSchema.Check", null, null, true, 4000).Text;
        Assert.StartsWith("Core/Mcp/McpSchema.cs:10-19 method McpSchema.Check", said);
        Assert.Contains("10      /// <summary>What's wrong", said);
        Assert.Contains("19      }", said);
        Assert.DoesNotContain("Count", said);
    }

    [Fact]
    public void Read_ends_an_expression_member_at_its_semicolon_and_python_by_indent_and_markdown_by_heading()
    {
        HexCodeIndex index = Index();
        Assert.StartsWith("Core/Mcp/McpSchema.cs:21-21 property McpSchema.Count", index.Read("McpSchema.cs", "Count", null, null, false, 4000).Text);
        Assert.StartsWith("tool.py:4-6 class Thing", index.Read("tool.py", "Thing", null, null, false, 4000).Text);
        string section = index.Read("notes.md", "Check", null, null, false, 4000).Text;
        Assert.Contains("How Check is used.", section);
        Assert.DoesNotContain("Something else", section);
    }

    [Fact]
    public void A_read_of_a_name_nobody_declares_says_so()
    {
        HexAnswer said = Index().Read(null, "Nope", null, null, true, 4000);
        Assert.True(said.Failed);
        Assert.Contains("hex_symbols", said.Text);
    }

    [Fact]
    public void Skim_says_the_summary_and_the_outline_of_several_files()
    {
        string said = Index().Skim("McpSchema.cs, tool.py", 2, 4000).Text;
        Assert.Contains("Core/Mcp/McpSchema.cs  24 lines", said);
        Assert.Contains("Checks arguments against a schema", said);
        Assert.Contains("11 method Check", said);
        Assert.Contains("tool.py  8 lines", said);
    }

    [Fact]
    public void The_map_has_the_folders_with_their_languages_and_types()
    {
        string said = Index().Map(null, 2, 4000).Text;
        Assert.Contains("./ 5 files", said);
        Assert.Contains("  Core/ 2 files (.cs 2)", said);
        Assert.Contains("Mcp/ 1 files (.cs 1)  McpSchema", said);
    }

    [Fact]
    public void Files_rank_a_name_over_a_path()
    {
        string said = Index().Files("queue", 10, 4000).Text;
        Assert.Contains("Core/Queue.cs  12 lines", said);
    }

    [Fact]
    public void The_budget_cuts_and_says_how_much_it_left_out()
    {
        File.WriteAllText(Path.Combine(folder, "many.cs"), string.Join('\n', Enumerable.Range(0, 400).Select(i => $"var check{i} = Check(); // padding padding padding")));
        string said = Index().Search("Check", false, false, false, null, 0, 1000, 1000).Text;
        Assert.True(said.Length < 1100, $"{said.Length} characters");
        Assert.Contains("more lines left out to stay under 1000 characters", said);
    }

    [Fact]
    public void A_changed_file_is_seen_logged_and_the_kept_answer_made_again()
    {
        HexCodeIndex index = Index();
        string before = index.Symbols("Fresh", null, null, 10, 4000).Text;
        long generation = index.Generation;
        Assert.Contains("Nothing declares Fresh", before);

        File.WriteAllText(Path.Combine(folder, "Core", "Queue.cs"), QUEUE.Replace("private void Log", "public void Fresh() { }\n    private void Log"));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "Core", "Queue.cs"), DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("method Queue.Fresh", index.Symbols("Fresh", null, null, 10, 4000).Text);

        File.Delete(Path.Combine(folder, "tool.py"));
        string changes = index.Changes(generation, 4000).Text;
        Assert.Contains("~ Core/Queue.cs", changes);
        Assert.Contains("- tool.py", changes);
        Assert.DoesNotContain("notes.md", changes);
    }

    [Fact]
    public void The_same_question_twice_is_kept()
    {
        var slow = new HexCodeIndex(() => [folder]) { StaleAfter = 60_000 };
        string first = slow.Symbols("Check", null, null, 10, 4000).Text;
        string second = slow.Symbols("Check", null, null, 10, 4000).Text;
        Assert.Same(first, second);
        Assert.Contains("hex_symbols  2  1", slow.Stats.Describe());
    }

    [Fact]
    public void The_cache_on_disk_brings_the_symbols_back_without_reading_the_files()
    {
        HexCodeIndex first = Index(cache);
        first.Current();
        first.SaveCache();

        HexCodeIndex second = Index(cache);
        HexIndexSnapshot index = second.Current();
        Assert.Equal(5, second.FromCache);
        Assert.Contains(index.Files.Single(file => file.Relative == "Core/Queue.cs").Symbols, symbol => symbol.Name == "QueueMcp");

        // And a file changed while Hex was closed is read again rather than taken from it
        File.AppendAllText(Path.Combine(folder, "speed.hor"), "let later = 1;\n");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "speed.hor"), DateTime.UtcNow.AddMinutes(1));
        HexCodeIndex third = Index(cache);
        Assert.Contains(third.Current().Files.Single(file => file.Relative == "speed.hor").Symbols, symbol => symbol.Name == "later");
        Assert.Equal(4, third.FromCache);
    }

    [Fact]
    public void A_language_servers_symbols_take_the_place_of_the_outlines_while_the_file_is_the_same()
    {
        HexCodeIndex index = Index();
        HexIndexSnapshot before = index.Current();
        HexIndexedFile queue = before.Files.Single(file => file.Relative == "Core/Queue.cs");
        Assert.DoesNotContain(queue.Symbols, symbol => symbol.Name == "wrong");

        index.TakeServerSymbols(queue.Path, queue.Length, queue.Written, HexCodeIndex.FromServer(
        [
            // The way csharp-ls says it, inside the file and its namespace
            new LanguageServerSymbol("Queue.cs", "File", string.Empty, 0, 0,
            [
                new LanguageServerSymbol("Demo", "Namespace", string.Empty, 0, 10,
                [
                    new LanguageServerSymbol("Queue", "Class", string.Empty, 2, 22,
                    [
                        new LanguageServerSymbol("QueueMcp(string)", "Method", string.Empty, 4, 16, []),
                        new LanguageServerSymbol("Log(string)", "Method", string.Empty, 10, 17, [])
                    ])
                ])
            ])
        ]));

        HexIndexedFile now = index.Current().Files.Single(file => file.Relative == "Core/Queue.cs");
        Assert.True(now.FromServer);
        Assert.Contains(now.Symbols, symbol => symbol.Full == "Queue.QueueMcp" && symbol.Kind == "method" && symbol.Depth == 1);
        Assert.DoesNotContain(now.Symbols, symbol => symbol.Kind is "file" or "namespace");
        Assert.True(index.Generation > before.Generation);
        Assert.Contains("symbols from its language server", index.Skim("Queue.cs", 2, 4000).Text);
    }

    [Fact]
    public void Watching_tells_when_a_file_changes_without_being_asked()
    {
        var index = new HexCodeIndex(() => [folder]) { StaleAfter = 100 };
        index.Current();
        using var told = new ManualResetEventSlim();
        index.Changed += _ => told.Set();
        index.Watch();
        try
        {
            File.WriteAllText(Path.Combine(folder, "new.md"), "# New\n");
            Assert.True(told.Wait(5000), "nothing said the workspace changed");
        }
        finally
        {
            index.Unwatch();
        }
    }
}
