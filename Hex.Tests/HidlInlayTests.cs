using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>HIDL's inlay hints, the parameter each argument of a call to a function of the file goes to.</summary>
public sealed class HidlInlayTests
{
    [Fact]
    public void ArgumentsSayTheirParameters()
    {
        List<UICodeInlay> found = HidlInlays.Of("func add(a, b) { return a + b; }\nlet x = add(1, add(2, 3));\n");

        Assert.Equal(
        [
            new UICodeInlay(1, 12, "a:", PadRight: true),
            new UICodeInlay(1, 15, "b:", PadRight: true),
            new UICodeInlay(1, 19, "a:", PadRight: true),
            new UICodeInlay(1, 22, "b:", PadRight: true)
        ], found.OrderBy(inlay => inlay.Column));
    }

    [Fact]
    public void ANameTheSameAsItsParameterGoesWithout()
    {
        List<UICodeInlay> found = HidlInlays.Of("let add = func(a, b) { return a + b; }\nlet a = 1;\nadd(a, \"two\");\n");

        Assert.Equal([new UICodeInlay(2, 7, "b:", PadRight: true)], found);
    }

    [Fact]
    public void MembersAndUnknownFunctionsGoWithout()
    {
        Assert.Empty(HidlInlays.Of("func go(a) { }\nhex.go(1);\nother(2);\n// go(3)\n"));
    }

    [Fact]
    public void ArrowsGivenANameHaveHintsToo()
    {
        List<UICodeInlay> found = HidlInlays.Of("let add = (a, b) => a + b;\nconst twice = x => x * 2;\nadd(1, twice(2));\n");

        Assert.Equal(
        [
            new UICodeInlay(2, 4, "a:", PadRight: true),
            new UICodeInlay(2, 7, "b:", PadRight: true),
            new UICodeInlay(2, 13, "x:", PadRight: true)
        ], found.OrderBy(inlay => inlay.Column));
    }

    [Fact]
    public void ABracketThatIsntAnArrowIsntAFunction()
    {
        Assert.Empty(HidlInlays.Of("let sum = (a + b) * 2;\nsum(1);\n"));
    }

    [Fact]
    public void ADefaultSaysTheNameAndNotTheDefault()
    {
        List<UICodeInlay> found = HidlInlays.Of("func box(at, size = vec(16, 16), colour = \"white\") { }\nbox(1, 2, 3);\nlet clip = (text, width = 40) => text;\nclip(\"a\", 5);\n");

        Assert.Equal(["at:", "size:", "colour:", "text:", "width:"], found.OrderBy(inlay => inlay.Line).ThenBy(inlay => inlay.Column).Select(inlay => inlay.Text));
    }

    [Fact]
    public void LoopsOfTwoNamesAndNamesTakenApartDontGetInTheWay()
    {
        List<UICodeInlay> found = HidlInlays.Of("func pair(a, b) { return [a, b]; }\nlet [x, y] = pair(1, 2);\nlet { from, to } = pair(3, 4);\nfor (key, value in { a: 1 }) pair(key, value);\nfor (i, item in [1]) { pair(i, item); }\n");

        Assert.Equal(["a:", "b:", "a:", "b:", "a:", "b:", "a:", "b:"], found.OrderBy(inlay => inlay.Line).ThenBy(inlay => inlay.Column).Select(inlay => inlay.Text));
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], found.OrderBy(inlay => inlay.Line).ThenBy(inlay => inlay.Column).Select(inlay => inlay.Line));
    }

    [Fact]
    public void ASignatureHasTheDefaultsAsTheyreWritten()
    {
        const string code = "func box(at, size = vec(16, 16), colour = \"white\") { }\nlet clip = (text, width = 40) => text;\nlet twice = x => x * 2;\nlet old = func(a, b) { };\n";

        Assert.Equal(["at", "size = vec(16, 16)", "colour = \"white\""], HidlFunctions.Signature(code, "box"));
        Assert.Equal(["text", "width = 40"], HidlFunctions.Signature(code, "clip"));
        Assert.Equal(["x"], HidlFunctions.Signature(code, "twice"));
        Assert.Equal(["a", "b"], HidlFunctions.Signature(code, "old"));
        Assert.Null(HidlFunctions.Signature(code, "nope"));
    }
}
