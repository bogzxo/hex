using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// What the language-server tools stand on that isn't a server: a name found to the column a server is asked at, found
/// again in a file changed since it was indexed, a declaration's lines for hex_edit_symbol, the trees hex_calls and
/// hex_types write, and hex_projects reading every build system's files.
/// </summary>
public sealed class HexCodeToolsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-code-tools-" + Guid.NewGuid().ToString("N")[..8]);

    private const string QUEUE = """
        namespace Demo;

        internal sealed class Queue
        {
            /// <summary>Puts a call in the queue.</summary>
            public void QueueMcp(string call)
            {
                Log("QueueMcp");
            }

            private void Log(string text) { }
        }
        """;

    public HexCodeToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        Directory.CreateDirectory(Path.Combine(folder, "Lib"));
        File.WriteAllText(Path.Combine(folder, "Lib", "Queue.cs"), QUEUE);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    private HexCodeIndex Index() => new(() => [folder], null) { StaleAfter = 0 };

    [Fact]
    public void A_name_is_located_at_its_own_column_with_its_extent()
    {
        HexLocated? found = Index().Locate("Queue.QueueMcp", null, out string? wrong);
        Assert.Null(wrong);
        Assert.NotNull(found);
        Assert.Equal("Lib/Queue.cs", found.File.Relative);
        Assert.Equal(5, found.Line);
        Assert.Equal("    public void QueueMcp(string call)".IndexOf("QueueMcp", StringComparison.Ordinal), found.Column);
        Assert.Equal(4, found.First);
        Assert.Equal(8, found.Last);
    }

    [Fact]
    public void A_name_nowhere_says_so_and_a_wrong_path_says_what_it_might_be()
    {
        HexCodeIndex index = Index();
        Assert.Null(index.Locate("Nothing", null, out string? wrong));
        Assert.Contains("Nothing called Nothing", wrong);
        Assert.Null(index.Locate("QueueMcp", "Other/Queue.cs", out wrong));
        Assert.Contains("Queue.cs", wrong);
    }

    [Fact]
    public void A_file_changed_since_it_was_indexed_is_located_in_as_it_is_now()
    {
        HexCodeIndex index = Index();
        Assert.NotNull(index.Locate("QueueMcp", null, out _));
        string path = Path.Combine(folder, "Lib", "Queue.cs");
        File.WriteAllText(path, "// moved down\n// and again\n" + QUEUE);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        // The index itself may not have looked again yet, the file read from the disk has
        HexLocated? found = index.Locate("QueueMcp", "Lib/Queue.cs", out _);
        Assert.NotNull(found);
        Assert.Equal(7, found.Line);
        Assert.Contains("QueueMcp", found.File.Lines[found.Line]);
    }

    [Fact]
    public void Two_declarations_of_a_name_are_both_said()
    {
        File.WriteAllText(Path.Combine(folder, "App", "Other.cs"), "namespace Demo;\n\ninternal sealed class Other\n{\n    public void QueueMcp(string call)\n    {\n    }\n}\n");
        HexLocated? found = Index().Locate("QueueMcp", null, out _);
        Assert.NotNull(found);
        Assert.Single(found.Others);
        Assert.Contains("say path to pick one", found.AlsoSaid);
    }

    [Fact]
    public void An_interface_member_the_outline_leaves_out_is_found_inside_its_interface()
    {
        File.WriteAllText(Path.Combine(folder, "Lib", "Shape.cs"), "namespace Demo;\n\npublic interface IShape\n{\n    // Area of it\n    double Area();\n}\n");
        HexLocated? found = Index().Locate("IShape.Area", null, out string? wrong);
        Assert.Null(wrong);
        Assert.NotNull(found);
        Assert.Equal(5, found.Line);
        Assert.Equal(11, found.Column);
        Assert.Equal(5, found.Last);
    }

    [Theory]
    [InlineData("    public void QueueMcp(string call)", "QueueMcp", 0, 16)]
    [InlineData("    QueueMcpX(); QueueMcp();", "QueueMcp", 0, 17)]
    [InlineData("    def run(self):", "missing", 0, 4)]
    [InlineData("a.b(b)", "b", 5, 4)]
    public void A_name_is_found_on_its_line_as_a_whole_word_nearest_where_it_was_said(string line, string name, int near, int column) =>
        Assert.Equal(column, HexCodeIndex.NameColumn(line, name, near));

    [Fact]
    public void A_tree_is_written_indented_with_its_sites_and_what_was_cut()
    {
        var root = new HexTreeNode("QueueMcp", "void Queue.QueueMcp(string)", "method", "/w/Queue.cs", 5);
        var caller = new HexTreeNode("Run", string.Empty, "method", "/w/Run.cs", 9, Sites: 3) { Cut = true };
        var again = new HexTreeNode("QueueMcp", string.Empty, "method", "/w/Queue.cs", 5) { Seen = true };
        root.Children.Add(caller);
        root.Children.Add(again);
        root.Outside.AddRange(["print", "sum"]);

        string said = HexHierarchy.Describe("Callers of QueueMcp.", [root], path => path.Replace("/w/", string.Empty), 4000, "ask again");
        string[] lines = said.Split('\n');
        Assert.Equal("Callers of QueueMcp.", lines[0]);
        Assert.Equal("Queue.cs:6 method QueueMcp  void Queue.QueueMcp(string)", lines[1]);
        Assert.Equal("  Run.cs:10 method Run  x3  +", lines[2]);
        Assert.Equal("  Queue.cs:6 method QueueMcp  (above)", lines[3]);
        Assert.Equal("  outside the workspace: print, sum", lines[4]);
        Assert.Contains("marked + have more under them, ask again", said);
    }

    [Fact]
    public void Kinds_are_said_as_words() =>
        Assert.Equal(["class", "method", "interface", "function", "struct", "symbol"], new[] { 5, 6, 11, 12, 23, 99 }.Select(HexHierarchy.KindName));

    [Fact]
    public void Projects_of_every_build_system_are_read_with_what_uses_what()
    {
        File.WriteAllText(Path.Combine(folder, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(folder, "web", "ui"));
        File.WriteAllText(Path.Combine(folder, "web", "package.json"), """{ "name": "web", "private": true, "dependencies": { "ui": "workspace:*", "react": "^19.0.0" }, "devDependencies": { "vitest": "^3" } }""");
        File.WriteAllText(Path.Combine(folder, "web", "ui", "package.json"), """{ "name": "ui", "version": "1.0.0" }""");
        Directory.CreateDirectory(Path.Combine(folder, "rust", "core"));
        File.WriteAllText(Path.Combine(folder, "rust", "Cargo.toml"), "[package]\nname = \"tool\"\nedition = \"2024\"\n\n[dependencies]\nserde = { version = \"1.0\", features = [\"derive\"] }\ncore = { path = \"core\" }\n");
        File.WriteAllText(Path.Combine(folder, "rust", "core", "Cargo.toml"), "[package]\nname = \"core\"\n");
        File.WriteAllText(Path.Combine(folder, "go.mod"), "module example.com/thing\n\ngo 1.24\n\nrequire (\n\tgithub.com/pkg/errors v0.9.1\n)\n");
        File.WriteAllText(Path.Combine(folder, "pyproject.toml"), "[project]\nname = \"thing\"\nrequires-python = \">=3.12\"\ndependencies = [\n  \"requests>=2\",\n  \"rich\",\n]\n");
        Directory.CreateDirectory(Path.Combine(folder, "native", "math"));
        File.WriteAllText(Path.Combine(folder, "native", "CMakeLists.txt"), "project(Native)\nadd_subdirectory(math)\nadd_executable(app main.cpp)\ntarget_link_libraries(app PRIVATE mathlib fmt::fmt)\nfind_package(fmt)\n");
        File.WriteAllText(Path.Combine(folder, "native", "math", "CMakeLists.txt"), "add_library(mathlib math.cpp)\n");

        string said = Index().Projects(null, packages: true, 8000).Text;
        Assert.Contains("App/App.csproj  App (MSBuild exe net11.0)", said);
        Assert.Contains("  -> Lib", said);
        Assert.Contains("Lib/Lib.csproj  Lib (MSBuild library net11.0)", said);
        Assert.Contains("  <- App", said);
        Assert.Contains("Newtonsoft.Json 13.0.3", said);
        Assert.Contains("web/package.json  web (npm", said);
        Assert.Contains("vitest ^3 (dev)", said);
        Assert.Contains("rust/Cargo.toml  tool (Cargo", said);
        Assert.Contains("serde 1.0", said);
        Assert.Contains("go.mod  example.com/thing (Go module go 1.24)", said);
        Assert.Contains("github.com/pkg/errors v0.9.1", said);
        Assert.Contains("pyproject.toml  thing (Python package python >=3.12)", said);
        Assert.Contains("requests>=2, rich", said);
        Assert.Contains("native/CMakeLists.txt  Native (CMake app (exe))", said);
        Assert.Contains("fmt::fmt", said);

        // web uses ui, tool uses core, Native uses its math folder, App uses Lib
        string[] lines = said.Split('\n');
        int web = Array.FindIndex(lines, line => line.StartsWith("web/package.json", StringComparison.Ordinal));
        Assert.Equal("  -> ui", lines[web + 1]);
        int rust = Array.FindIndex(lines, line => line.StartsWith("rust/Cargo.toml", StringComparison.Ordinal));
        Assert.Equal("  -> core", lines[rust + 1]);
        int native = Array.FindIndex(lines, line => line.StartsWith("native/CMakeLists.txt", StringComparison.Ordinal));
        Assert.StartsWith("  -> math", lines[native + 1]);
    }

    [Fact]
    public void Projects_can_be_narrowed_and_their_packages_only_counted()
    {
        File.WriteAllText(Path.Combine(folder, "Lib", "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="A" Version="1" /><PackageReference Include="B" Version="2" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        string said = Index().Projects("Lib", packages: false, 8000).Text;
        Assert.StartsWith("1 projects of 2", said);
        Assert.Contains("  2 packages (packages true lists them)", said);
        Assert.DoesNotContain("App.csproj", said);
    }

    [Fact]
    public void Worktrees_and_snapshot_copies_inside_the_workspace_are_left_out_and_submodules_kept()
    {
        // hexwt-tools: a git worktree, its .git a file pointing into another clone's worktrees
        Directory.CreateDirectory(Path.Combine(folder, "hexwt-tools"));
        File.WriteAllText(Path.Combine(folder, "hexwt-tools", ".git"), "gitdir: C:/repos/hex/.git/worktrees/hexwt-tools\n");
        File.WriteAllText(Path.Combine(folder, "hexwt-tools", "Queue.cs"), QUEUE);
        // .hexbase: a snapshot of a clone, a dot folder with a .git of its own
        Directory.CreateDirectory(Path.Combine(folder, ".hexbase", ".git"));
        File.WriteAllText(Path.Combine(folder, ".hexbase", "Queue.cs"), QUEUE);
        // A submodule is the workspace's own
        Directory.CreateDirectory(Path.Combine(folder, "extensions"));
        File.WriteAllText(Path.Combine(folder, "extensions", ".git"), "gitdir: ../.git/modules/extensions\n");
        File.WriteAllText(Path.Combine(folder, "extensions", "Ext.cs"), "namespace Demo;\n\ninternal sealed class Ext\n{\n}\n");

        HexCodeIndex index = Index();
        HexLocated? found = index.Locate("QueueMcp", null, out _);
        Assert.NotNull(found);
        Assert.Empty(found.Others);
        Assert.Equal("Lib/Queue.cs", found.File.Relative);
        Assert.NotNull(index.Locate("Ext", null, out _));
        Assert.DoesNotContain(index.Current().Files, file => file.Relative.Contains("hexwt", StringComparison.Ordinal) || file.Relative.Contains(".hexbase", StringComparison.Ordinal));
        Assert.True(HexSearch.IsRepositoryCopy(Path.Combine(folder, "hexwt-tools")));
        Assert.False(HexSearch.IsRepositoryCopy(Path.Combine(folder, "extensions")));
    }

    [Fact]
    public void A_declaration_in_a_project_is_tried_before_a_loose_copy_and_every_one_is_a_candidate()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Copy"));
        File.WriteAllText(Path.Combine(folder, "App", "Queue.cs"), QUEUE);
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        List<HexLocated> all = Index().LocateAll("QueueMcp", null, out string? wrong);
        Assert.Null(wrong);
        Assert.Equal(2, all.Count);
        Assert.Equal("App/Queue.cs", all[0].File.Relative);
        Assert.Equal("Lib/Queue.cs", all[1].File.Relative);
        Assert.Contains("App/Queue.cs:6", all[1].Others);
    }

    [Theory]
    [InlineData("    public static bool TryGet(char c, out Glyph glyph)", "TryGet")]
    [InlineData("    public static bool TryGet<T>(T key) where T : notnull", "TryGet")]
    [InlineData("    internal sealed partial class CodeEditor : UIComponent, IDisposable", "CodeEditor")]
    [InlineData("    public sealed record struct Place(int Line)", "Place")]
    [InlineData("    async def run(self, x):", "run")]
    [InlineData("pub fn parse(text: &str) -> Ast {", "parse")]
    [InlineData("    public int Count { get; private set; }", "Count")]
    [InlineData("    private readonly List<string> names = [];", "names")]
    [InlineData("    public Queue(int size) : base(size)", "Queue")]
    public void A_declaration_line_is_asked_at_its_name_not_its_modifiers(string line, string name) =>
        Assert.Equal(System.Text.RegularExpressions.Regex.Match(line, $@"\b{name}\b").Index, HexCodeIndex.DeclarationColumn(line));

    [Fact]
    public void A_partial_type_is_said_once_with_the_file_holding_its_bases()
    {
        (string Name, string Line)[] items =
        [
            ("CodeEditor", "internal sealed partial class CodeEditor"),
            ("CodeEditor", "internal sealed partial class CodeEditor : UIComponent"),
            ("Other", "internal sealed class Other : CodeEditor"),
            ("CodeEditor", "partial class CodeEditor // more"),
        ];
        var folded = HexHierarchy.FoldPartials(items, item => item.Name, item => item.Line);
        Assert.Equal(2, folded.Count);
        Assert.Equal("internal sealed partial class CodeEditor : UIComponent", folded[0].Item.Line);
        Assert.Equal(2, folded[0].Partials);
        Assert.Equal(0, folded[1].Partials);

        var node = new HexTreeNode("v CodeEditor", string.Empty, "class", "/w/CodeEditor.cs", 3) { Partials = 2 };
        Assert.Equal("CodeEditor.cs:4 class v CodeEditor  +2 partial files", HexHierarchy.LineOf(node, 0, path => path.Replace("/w/", string.Empty)));
    }

    [Theory]
    [InlineData("class Square(Shape):", "Square", "Shape")]
    [InlineData("class Square(shapes.Shape, Generic[T], metaclass=ABCMeta):", "Square", "Shape,Generic")]
    [InlineData("export class Button extends Component<Props> implements Clickable, Focusable {", "Button", "Component,Clickable,Focusable")]
    [InlineData("internal sealed partial class CodeEditor : UIComponent, IDisposable", "CodeEditor", "UIComponent,IDisposable")]
    [InlineData("public class Box<T> : List<T> where T : class", "Box", "List")]
    [InlineData("class Plain:", "Plain", "")]
    [InlineData("class Thing(object):", "Thing", "")]
    public void Bases_are_read_off_a_declaration_line(string line, string name, string bases) =>
        Assert.Equal(bases, string.Join(',', HexCodeIndex.BasesOf(line, name)));

    [Fact]
    public void A_python_hierarchy_comes_from_the_index_where_the_server_has_none()
    {
        Directory.CreateDirectory(Path.Combine(folder, "py"));
        File.WriteAllText(Path.Combine(folder, "py", "shapes.py"), "from abc import ABC\n\n\nclass Shape(ABC):\n    def area(self):\n        return 0\n\n\nclass Square(Shape):\n    def area(self):\n        return 4\n\n\nclass Tiny(Square):\n    pass\n");
        File.WriteAllText(Path.Combine(folder, "py", "round.py"), "from shapes import Shape\n\n\nclass Circle(Shape):\n    def area(self):\n        return 3\n");
        HexCodeIndex index = Index();

        HexLocated shape = index.Locate("Shape", null, out _)!;
        List<HexTreeNode> tree = index.IndexTypeTree(shape, up: true, down: true, depth: 2);
        string said = HexHierarchy.Describe("Shape", tree, path => Path.GetRelativePath(folder, path).Replace('\\', '/'), 4000, "more");
        Assert.Contains("outside the workspace: ABC", said);
        Assert.Contains("py/shapes.py:9 class v Square", said);
        Assert.Contains("py/round.py:4 class v Circle", said);
        Assert.Contains("    py/shapes.py:14 class v Tiny", said);

        List<string> types = [.. index.IndexImplementations(shape).Select(each => each.Symbol.Name)];
        Assert.Equal(["Circle", "Square", "Tiny"], types.Order(StringComparer.Ordinal));

        HexLocated area = index.Locate("Shape.area", null, out _)!;
        List<(HexIndexedFile File, HexSymbol Symbol)> overriding = index.IndexImplementations(area);
        Assert.Equal(2, overriding.Count);
        Assert.All(overriding, each => Assert.Equal("area", each.Symbol.Name));
    }

    [Fact]
    public void Diagnostics_say_the_files_not_looked_at_in_the_count()
    {
        Assert.Equal("0 errors and 0 warnings in 3 files, by their language servers.", Hex.Dawn.DawnScene.DiagnosticsTitle(0, 0, 3, 0, 0));
        Assert.Equal("0 errors and 2 warnings in 3 files (1 file not ready, not counted), by their language servers.", Hex.Dawn.DawnScene.DiagnosticsTitle(0, 2, 3, 1, 0));
        Assert.Equal("1 errors and 0 warnings in 4 files (2 files not ready, 1 no server reads, not counted), by their language servers.", Hex.Dawn.DawnScene.DiagnosticsTitle(1, 0, 4, 2, 1));
    }

    [Fact]
    public void Package_json_that_is_only_data_or_under_docs_is_not_a_project()
    {
        Directory.CreateDirectory(Path.Combine(folder, "docs", "benchmarks", "2026-11-04"));
        File.WriteAllText(Path.Combine(folder, "docs", "benchmarks", "2026-11-04", "package.json"), """{ "name": "bench", "Files": 232 }""");
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "data", "package.json"), """{ "Files": 232, "ZipBytes": 47144371 }""");
        File.WriteAllText(Path.Combine(folder, "App", "package.json"), """{ "name": "app", "dependencies": { "react": "^19" } }""");
        string said = Index().Projects(null, false, 8000).Text;
        Assert.StartsWith("1 projects", said);
        Assert.Contains("App/package.json  app", said);
    }

    [Fact]
    public void A_broken_project_file_is_said_unreadable_rather_than_failing()
    {
        File.WriteAllText(Path.Combine(folder, "App", "package.json"), "{ not json");
        Assert.Contains("unreadable", Index().Projects(null, false, 8000).Text);
    }
}
