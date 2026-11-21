using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// What agents tripped on in the MCP tools: a call tree of a busy method folded to fit its budget with every overload
/// said, hex_refs Container.Name by the place rather than the file, a declaration deleted with the spacing kept, hex_projects
/// telling projects of one name apart, argument names a model guesses put right, and a check after an edit waiting for
/// what a server says after it.
/// </summary>
public sealed class HexMcpRoundThreeTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-mcp-three").FullName;

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    private HexCodeIndex Index() => new(() => [folder]) { StaleAfter = 0 };

    // Four overloads, each calling 20 methods that each call 6 more, far past 2000 characters written out
    private static List<HexTreeNode> BusyTree()
    {
        var roots = new List<HexTreeNode>();
        for (int overload = 0; overload < 4; overload++)
        {
            var root = new HexTreeNode("Text", $"Text(overload {overload})", "method", "/w/UIDrawList.cs", 100 + overload * 10);
            for (int callee = 0; callee < 20; callee++)
            {
                var child = new HexTreeNode($"Callee{callee}", string.Empty, "method", "/w/Helpers.cs", callee * 3);
                for (int leaf = 0; leaf < 6; leaf++)
                    child.Children.Add(new HexTreeNode($"Leaf{callee}_{leaf}", string.Empty, "method", "/w/Leaves.cs", leaf) { Cut = true });
                root.Children.Add(child);
            }
            roots.Add(root);
        }

        return roots;
    }

    [Fact]
    public void A_busy_call_tree_is_folded_to_fit_with_every_overload_said()
    {
        List<HexTreeNode> roots = BusyTree();
        HexHierarchy.MarkShared(roots);
        string said = HexHierarchy.Describe("Calls from Text (4 overloads together), 2 deep.", roots, path => path.Replace("/w/", string.Empty), 4000, "ask again", "also calls");

        Assert.True(said.Length <= 4000, $"{said.Length} characters");
        Assert.DoesNotContain("left out to stay under", said);
        for (int overload = 0; overload < 4; overload++)
            Assert.Contains($"UIDrawList.cs:{101 + overload * 10} method Text", said);

        // What the later overloads share with the first is said as above, not again
        Assert.Contains("(above)", said);
    }

    [Fact]
    public void Leaves_with_nothing_under_them_go_on_one_line()
    {
        var root = new HexTreeNode("Run", string.Empty, "method", "/w/A.cs", 0);
        var busy = new HexTreeNode("Busy", string.Empty, "method", "/w/A.cs", 5);
        busy.Children.Add(new HexTreeNode("Deep", string.Empty, "method", "/w/A.cs", 9));
        root.Children.Add(busy);
        for (int i = 0; i < 40; i++)
            root.Children.Add(new HexTreeNode($"Tiny{i}", "a helper with a long signature that says a lot of things", "method", $"/w/Helpers{i}.cs", i));

        string whole = HexHierarchy.Describe("t", [root], path => path, 100_000, "more", "also calls");
        string folded = HexHierarchy.Describe("t", [root], path => path, 1500, "more", "also calls");

        Assert.Contains("Tiny39", whole);
        Assert.True(whole.Length > 1500);
        Assert.Contains("also calls: Tiny0, Tiny1", folded);
        Assert.Contains("method Busy", folded);
        Assert.DoesNotContain("left out to stay under", folded);
    }

    [Fact]
    public void Container_dot_name_keeps_the_uses_on_that_type_only()
    {
        File.WriteAllText(Path.Combine(folder, "DrawList.cs"), """
            namespace Demo;

            public sealed class DrawList
            {
                public void Text(string text) { }

                public void Twice(string text)
                {
                    Text(text);
                    this.Text(text);
                }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "Label.cs"), """
            namespace Demo;

            public sealed class Label
            {
                public string Text = "";

                public void Draw(DrawList list, Label other)
                {
                    list.Text(Text);
                    other.Text = "x";
                    Text = other.Text;
                }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "Panel.cs"), """
            namespace Demo;

            public sealed class Panel
            {
                private readonly DrawList draw = new();
                private Label label = new();

                public void Paint()
                {
                    draw.Text("a");
                    label.Text = "b";
                }
            }
            """);

        string said = Index().References("DrawList.Text", null, 80, 8000).Text;

        Assert.Contains("by text, approximate", said);
        Assert.Contains("DrawList.cs (3)", said);
        Assert.Contains("Label.cs (1)", said);
        Assert.Contains("  9: ", said);
        Assert.Contains("Panel.cs (1)", said);
        Assert.Contains("  10: ", said);
        Assert.DoesNotContain("  5*: ", said.Split("Label.cs")[1].Split("Panel.cs")[0]);
        Assert.DoesNotContain("label.Text", said);
        Assert.DoesNotContain("other.Text", said);
    }

    [Theory]
    // Two blank lines either side of a Python function deleted, two left, not four
    [InlineData("a\n\n\ndef b():\n    pass\n\n\nc\n", 3, 4, "a\n\n\nc\n")]
    // One either side, one left
    [InlineData("a\n\nb\n\nc", 2, 2, "a\n\nc")]
    // None either side, none
    [InlineData("a\nb\nc", 1, 1, "a\nc")]
    // The last thing in the file, the blank lines before it go and it still ends with a line break
    [InlineData("a\n\n\ndef b():\n    pass\n", 3, 4, "a\n")]
    // The first thing in the file, the blank lines after it go
    [InlineData("def b():\n    pass\n\n\nc\n", 0, 1, "c\n")]
    public void A_declaration_deleted_keeps_the_spacing_between_its_neighbours(string text, int first, int last, string left) =>
        Assert.Equal(left, string.Join('\n', DawnScene.WithoutLines(text.Split('\n'), first, last)));

    [Fact]
    public void Projects_of_one_name_are_told_apart_by_their_folders_and_editor_packages_said_once()
    {
        foreach (string where in (string[])["hidl", "horizon/Horizon.HIDL", "app", "editors/vscode", "tools/site"])
            Directory.CreateDirectory(Path.Combine(folder, where));
        File.WriteAllText(Path.Combine(folder, "hidl", "Horizon.HIDL.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        File.WriteAllText(Path.Combine(folder, "horizon", "Horizon.HIDL", "Horizon.HIDL.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        File.WriteAllText(Path.Combine(folder, "app", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\hidl\Horizon.HIDL.csproj" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(folder, "editors", "vscode", "package.json"), """{ "name": "hex-vscode", "version": "1.0.0", "devDependencies": { "@types/vscode": "^1" } }""");
        File.WriteAllText(Path.Combine(folder, "tools", "site", "package.json"), """{ "name": "site-tool", "private": true }""");

        string said = Index().Projects(null, packages: false, 8000).Text;

        Assert.Contains("  -> Horizon.HIDL (hidl)", said);
        Assert.Contains("  <- App", said);
        Assert.DoesNotContain("editors/vscode/package.json  hex-vscode", said);
        Assert.Contains("other packages (2", said);
        Assert.Contains("hex-vscode (editors/vscode)", said);
        Assert.Contains("site-tool (tools/site)", said);
    }

    private static JsonObject Schema(params string[] names) =>
        McpSchema.Object(names.Select(name => (name, name switch
        {
            "depth" or "line" => McpSchema.Integer(name),
            "check" => McpSchema.Flag(name),
            _ => McpSchema.String(name)
        })), names[0]);

    [Fact]
    public void Argument_names_a_model_guesses_are_put_right()
    {
        // hex_implementations with name, hex_read with file_path, hex_diagnostics with path, camelCase
        JsonObject symbolTool = Schema("symbol", "path", "line", "depth");
        Assert.Null(McpSchema.Check(symbolTool, McpSchema.Normalize(symbolTool, new JsonObject { ["name"] = "UIDrawList" })));
        Assert.Equal("UIDrawList", McpSchema.Normalize(symbolTool, new JsonObject { ["name"] = "UIDrawList" })["symbol"]!.GetValue<string>());

        JsonObject read = Schema("path", "symbol");
        JsonObject fixedRead = McpSchema.Normalize(read, new JsonObject { ["file_path"] = "a.cs", ["symbol"] = "X" });
        Assert.Equal("a.cs", fixedRead["path"]!.GetValue<string>());
        Assert.Null(McpSchema.Check(read, fixedRead));
        Assert.Equal("a.cs", McpSchema.Normalize(read, new JsonObject { ["filePath"] = "a.cs" })["path"]!.GetValue<string>());

        JsonObject diagnostics = Schema("paths", "check");
        Assert.Equal("a.cs", McpSchema.Normalize(diagnostics, new JsonObject { ["path"] = "a.cs" })["paths"]!.GetValue<string>());
        Assert.Equal("a.cs,b.cs", McpSchema.Normalize(diagnostics, new JsonObject { ["paths"] = new JsonArray("a.cs", "b.cs") })["paths"]!.GetValue<string>());
        Assert.True(McpSchema.Normalize(diagnostics, new JsonObject { ["paths"] = "a", ["check"] = "true" })["check"]!.GetValue<bool>());

        // A whole number said as a string, and a name the tool does take left alone
        JsonObject depth = McpSchema.Normalize(symbolTool, new JsonObject { ["symbol"] = "X", ["depth"] = "2" });
        Assert.Null(McpSchema.Check(symbolTool, depth));
        JsonObject named = Schema("name", "symbol");
        Assert.Equal("n", McpSchema.Normalize(named, new JsonObject { ["name"] = "n" })["name"]!.GetValue<string>());
    }

    [Fact]
    public void A_required_argument_missing_says_every_argument_the_tool_takes()
    {
        JsonObject schema = Schema("symbol", "path", "line");
        string? wrong = McpSchema.Check(schema, McpSchema.Normalize(schema, new JsonObject { ["line"] = 3 }));
        Assert.Equal("'symbol' is required. It takes symbol (required), path, line.", wrong);
    }

    [Fact]
    public void A_check_after_an_edit_waits_for_what_is_said_after_it()
    {
        var log = new HexHeardLog();
        Assert.False(log.SaidSince("a.py", 100));

        // Said of the text before the edit, not taken for the edit's answer
        log.Record("a.py", [], "pyright", 90);
        Assert.False(log.SaidSince("a.py", 100));
        Assert.Equal(1, log.Of("a.py").Count);

        // Said after it, empty, which is clean
        log.Record("a.py", [], "pyright", 130);
        Assert.True(log.SaidSince("a.py", 100));
        Assert.Equal(2, log.Of("a.py").Count);
        Assert.Empty(log.Of("a.py").Found!);
    }
}
