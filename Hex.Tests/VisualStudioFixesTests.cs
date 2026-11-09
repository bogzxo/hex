using Hex.Dawn;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// Ctrl+F12 finding the overrides a server left out, a fix's using put where it goes in order, what a fix would change,
/// and a build's error going once the server's had the edit that put it right.
/// </summary>
public sealed class VisualStudioFixesTests
{
    [Theory]
    [InlineData("    protected virtual Renderer2DTechnique CreateTechnique() => new(FrameBuffer);", true)]
    [InlineData("    protected override Renderer2DTechnique CreateTechnique() => new DeferredRenderer2DTechnique(this);", true)]
    [InlineData("    public string ResolvePath(string path)", true)]
    [InlineData("    string IContentFiles.ResolvePath(string path) => path;", true)]
    [InlineData("    public (int, int) ResolvePath()", true)]
    [InlineData("        Technique = CreateTechnique();", false)]
    [InlineData("        CreateTechnique();", false)]
    [InlineData("        return base.CreateTechnique();", false)]
    [InlineData("        if (ok) CreateTechnique();", false)]
    [InlineData("        var made = this.CreateTechnique();", false)]
    [InlineData("        Use(CreateTechnique());", false)]
    [InlineData("        // the CreateTechnique() of it", false)]
    public void A_member_is_declared_by_its_line(string line, bool declares)
    {
        string word = line.Contains("ResolvePath") ? "ResolvePath" : "CreateTechnique";
        Assert.Equal(declares, HexLanguages.DeclaresMember(line, line.IndexOf(word, StringComparison.Ordinal), word));
    }

    [Fact]
    public void Overrides_and_interface_members_are_told_from_what_they_implement()
    {
        string[] file =
        [
            "public interface IContentFiles",
            "{",
            "    string ResolvePath(string path);",
            "}",
            "public abstract class Renderer",
            "{",
            "    protected virtual Technique CreateTechnique() => new();",
            "    protected abstract void Draw();",
            "}",
            "public sealed class Deferred : Renderer, IContentFiles",
            "{",
            "    protected override Technique CreateTechnique() => new Deferred();",
            "    public string ResolvePath(string path) => path;",
            "    void Use() => CreateTechnique();",
            "}"
        ];

        HexLanguages.MemberKind Kind(int line, string word) => HexLanguages.KindOf(file, line, file[line].IndexOf(word, StringComparison.Ordinal), word);
        Assert.Equal(HexLanguages.MemberKind.Abstract, Kind(2, "ResolvePath"));
        Assert.Equal(HexLanguages.MemberKind.Virtual, Kind(6, "CreateTechnique"));
        Assert.Equal(HexLanguages.MemberKind.Abstract, Kind(7, "Draw"));
        Assert.Equal(HexLanguages.MemberKind.Implements, Kind(11, "CreateTechnique"));
        Assert.Equal(HexLanguages.MemberKind.Implements, Kind(12, "ResolvePath"));
        Assert.Equal(HexLanguages.MemberKind.Used, Kind(13, "CreateTechnique"));
    }

    private static string Fixed(string text, LanguageServerEdit edit) =>
        LanguageServer.Apply(text, HexLanguages.SortUsings(text, [edit]));

    [Fact]
    public void A_using_a_fix_adds_goes_in_order_with_System_first()
    {
        // Renderer2D's usings, not quite in order, the fix put System.Text on the end under PostProcessing
        const string RENDERER = "using Horizon.Logging;\nusing System.Numerics;\n\nusing Horizon.Core.Threading;\nusing Horizon.Rendering.PostProcessing;\n\nnamespace Horizon.Rendering;\n";
        Assert.Equal("using Horizon.Logging;\nusing System.Numerics;\nusing System.Text;\n\nusing Horizon.Core.Threading;\nusing Horizon.Rendering.PostProcessing;\n\nnamespace Horizon.Rendering;\n",
            Fixed(RENDERER, new LanguageServerEdit(5, 0, 5, 0, "using System.Text;\n")));

        // In order already, System's first and a group of the project's under them
        const string SORTED = "using System;\nusing System.IO;\n\nusing Horizon.Audio;\nusing Horizon.Graphics;\n\nclass A { }\n";
        Assert.Equal("using System;\nusing System.IO;\nusing System.Text;\n\nusing Horizon.Audio;\nusing Horizon.Graphics;\n\nclass A { }\n",
            Fixed(SORTED, new LanguageServerEdit(5, 0, 5, 0, "using System.Text;\n")));
        Assert.Equal("using System;\nusing System.IO;\n\nusing Horizon.Audio;\nusing Horizon.Core;\nusing Horizon.Graphics;\n\nclass A { }\n",
            Fixed(SORTED, new LanguageServerEdit(5, 0, 5, 0, "using Horizon.Core;\n")));
        Assert.Equal("using System;\nusing System.IO;\n\nusing Horizon.Audio;\nusing Horizon.Graphics;\nusing Microsoft.Win32;\n\nclass A { }\n",
            Fixed(SORTED, new LanguageServerEdit(4, 23, 4, 23, "\nusing Microsoft.Win32;")));

        // Put at the end of a line rather than on one of its own, with Windows' line ends
        Assert.Equal("using System;\r\nusing System.Text;\r\nusing Zed;\r\n",
            Fixed("using System;\r\nusing Zed;\r\n", new LanguageServerEdit(1, 10, 1, 10, "\r\nusing System.Text;")));

        // Anything that isn't one using is left alone
        LanguageServerEdit[] rename = [new LanguageServerEdit(0, 6, 0, 12, "Other")];
        Assert.Same(rename, HexLanguages.SortUsings("using System;\n", rename));
    }

    [Fact]
    public void A_fix_shows_the_lines_it_takes_out_and_puts_in()
    {
        var shown = new List<string>();
        HexLanguages.ChangedLines("a\n    var x = Max();\nb\n", "a\n    var x = Math.Max(1, 2);\nb\n", shown);
        Assert.Equal(["- var x = Max();", "+ var x = Math.Max(1, 2);"], shown);

        shown.Clear();
        HexLanguages.ChangedLines("using A;\nclass B { }\n", "using A;\nusing System.Text;\nclass B { }\n", shown);
        Assert.Equal(["+ using System.Text;"], shown);

        shown.Clear();
        HexLanguages.ChangedLines("same\n", "same\n", shown);
        Assert.Empty(shown);
    }

    [Fact]
    public void A_build_error_goes_once_the_server_has_had_the_edit_and_no_longer_says_it()
    {
        var built = new HexProblem("R.cs", 149, 59, "Argument 1: cannot convert from 'uint' to 'byte' (CS1503)", false);
        UICodeDiagnostic[] stillThere = [new(149, 59, 4, "Argument 1: cannot convert (CS1503)", false), new(152, 12, 2, "Already defined (CS0128)", false)];
        UICodeDiagnostic[] lineFixed = [new(152, 12, 2, "Already defined (CS0128)", false)];

        // Not edited since the build, the server may simply not know of it, so it stays
        Assert.False(DawnScene.BuildErrorGone(built, lineFixed, clean: false, edited: false));

        // Edited and still said on its line it stays, edited and gone from its line it goes, other errors left or not
        Assert.False(DawnScene.BuildErrorGone(built, stillThere, clean: false, edited: true));
        Assert.True(DawnScene.BuildErrorGone(built, lineFixed, clean: false, edited: true));

        // Nothing wrong at all takes every error, never a warning
        Assert.True(DawnScene.BuildErrorGone(built, [], clean: true, edited: false));
        Assert.False(DawnScene.BuildErrorGone(built with { Warning = true }, lineFixed, clean: true, edited: true));
    }
}
