using Hex.Core;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// The reference counts over declarations, HIDL's worked out of the file alone, and which line a count goes over and
/// which of the places a server says are really uses.
/// </summary>
public sealed class CodeLensTests
{
    // Helper method for the declaration of a name, which there has to be one of
    private static HidlDeclared Of(List<HidlDeclared> found, string name) => Assert.Single(found, declared => declared.Name == name);

    [Fact]
    public void TopLevelNamesAreCountedWhereverTheyreUsed()
    {
        const string code = "let speed = 3;\nconst LIMIT = 10;\nfunc go(step) {\n    return step * speed + LIMIT + speed;\n}\nlet moved = go(speed);\n";
        List<HidlDeclared> found = HidlSemantics.References(code);

        Assert.Equal(["speed", "LIMIT", "go", "moved"], found.Select(declared => declared.Name));
        Assert.Equal(3, Of(found, "speed").Uses.Count);
        Assert.Single(Of(found, "LIMIT").Uses);
        Assert.Equal((5, 12), Of(found, "go").Uses.Single());
        Assert.Empty(Of(found, "moved").Uses);
        Assert.Equal((0, 4), (Of(found, "speed").Line, Of(found, "speed").Column));
    }

    [Fact]
    public void ParametersAndLocalsGetNoCountAndDontCountForTheTopOnesTheyHide()
    {
        const string code = "let size = 4;\nfunc grow(size) {\n    let more = size * 2;\n    return more;\n}\nprint(size);\n";
        List<HidlDeclared> found = HidlSemantics.References(code);

        Assert.Equal(["size", "grow"], found.Select(declared => declared.Name));
        Assert.Equal((5, 6), Of(found, "size").Uses.Single());
    }

    [Fact]
    public void TwoNamesDeclaredOnOneLineAreCountedEach()
    {
        const string code = "const [low, high] = [4, 12];\nprint(low + high + high);\n";
        List<HidlDeclared> found = HidlSemantics.References(code);

        Assert.Equal(["low", "high"], found.Select(declared => declared.Name));
        Assert.All(found, declared => Assert.Equal(0, declared.Line));
        Assert.Single(Of(found, "low").Uses);
        Assert.Equal(2, Of(found, "high").Uses.Count);
    }

    [Fact]
    public void AFunctionCalledAboveWhereItsWrittenIsCountedAndCommentsArent()
    {
        const string code = "// later is called here\nlater();\nlater();\nfunc later() { }\n";
        Assert.Equal(2, Of(HidlSemantics.References(code), "later").Uses.Count);
    }

    [Fact]
    public void ACountGoesOverTheAttributesNotTheDocComment()
    {
        string[] lines =
        [
            "public class Thing",
            "{",
            "",
            "    /// <summary>Adds them.</summary>",
            "    [Obsolete]",
            "    [Fact(Skip = \"no\")]",
            "    public float Sum() => 0;",
            "}"
        ];

        // csharp-ls starts it at the blank line, a code lens says only the name's line
        Assert.Equal(4, HexCodeLens.LensLine(line => lines[line], lines.Length, 6, 2));
        Assert.Equal(4, HexCodeLens.LensLine(line => lines[line], lines.Length, 6, 6));
        Assert.Equal(0, HexCodeLens.LensLine(line => lines[line], lines.Length, 0, 0));
    }

    [Fact]
    public void TypesAndMembersAreCountedButNotNamespacesOrLocals()
    {
        Assert.True(HexCodeLens.Counts("class"));
        Assert.True(HexCodeLens.Counts("method"));
        Assert.True(HexCodeLens.Counts("field"));
        Assert.True(HexCodeLens.Counts("enum-member"));
        Assert.False(HexCodeLens.Counts("namespace"));
        Assert.False(HexCodeLens.Counts("variable"));
    }

    [Fact]
    public void TheDeclarationAndAPropertysOwnGetAndSetArentUses()
    {
        string path = Path.Combine(Path.GetTempPath(), "Thing.cs");
        var declaration = new LanguageServerPlace(path, 7, 15, 7, 20);
        var getter = new LanguageServerPlace(path, 7, 23, 7, 26);
        var use = new LanguageServerPlace(path, 20, 14, 20, 19);
        var elsewhere = new LanguageServerPlace(Path.Combine(Path.GetTempPath(), "Other.cs"), 7, 15, 7, 20);

        Assert.False(HexCodeLens.IsUse(declaration, path, "property", 7, 15));
        Assert.False(HexCodeLens.IsUse(getter, path, "property", 7, 15));
        Assert.True(HexCodeLens.IsUse(getter, path, "method", 7, 15));
        Assert.True(HexCodeLens.IsUse(use, path, "property", 7, 15));
        Assert.True(HexCodeLens.IsUse(elsewhere, path, "property", 7, 15));
    }
}
