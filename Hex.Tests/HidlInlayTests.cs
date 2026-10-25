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
}
