using Hex.Core;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>HIDL's semantic highlighting, every name by what it is, worked out of the file alone.</summary>
public sealed class HidlSemanticsTests
{
    // Helper method for what the name at a line and column was found to be, null for nothing
    private static SemanticSpan? At(List<SemanticSpan> found, int line, int column) =>
        found.Find(span => span.Line == line && span.Start == column) is { Length: > 0 } span ? span : null;

    private static SyntaxKind? KindAt(List<SemanticSpan> found, int line, int column) => At(found, line, column)?.Kind;

    [Fact]
    public void ParametersLocalsAndTopLevelNamesAreToldApart()
    {
        const string code = "let speed = 3;\nconst LIMIT = 10;\nfunc go(step) {\n    let moved = step * speed;\n    return moved + LIMIT;\n}\n";
        List<SemanticSpan> found = HidlSemantics.Of(code);

        Assert.Equal(SyntaxKind.Field, KindAt(found, 0, 4));
        Assert.True(At(found, 0, 4)!.Value.Modifiers.HasFlag(SyntaxModifiers.Declaration));
        Assert.Equal(SyntaxKind.NamedConstant, KindAt(found, 1, 6));
        Assert.True(At(found, 1, 6)!.Value.Modifiers.HasFlag(SyntaxModifiers.Readonly));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 2, 5));
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 2, 8));

        // let moved = step * speed;
        Assert.Equal(SyntaxKind.Local, KindAt(found, 3, 8));
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 3, 16));
        Assert.False(At(found, 3, 16)!.Value.Modifiers.HasFlag(SyntaxModifiers.Declaration));
        Assert.Equal(SyntaxKind.Field, KindAt(found, 3, 23));

        // return moved + LIMIT;
        Assert.Equal(SyntaxKind.Local, KindAt(found, 4, 11));
        Assert.Equal(SyntaxKind.NamedConstant, KindAt(found, 4, 19));
    }

    [Fact]
    public void FunctionsArrowsAndCallsAreMethods()
    {
        const string code = "let twice = (x) => x * 2;\nlet half = n => n / 2;\nlet add = func(a, b) { return a + b; };\nprint(twice(half(add(1, 2))));\nlater();\nfunc later() { }\n";
        List<SemanticSpan> found = HidlSemantics.Of(code);

        Assert.Equal(SyntaxKind.Method, KindAt(found, 0, 4));
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 0, 13));
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 0, 19));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 1, 4));
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 1, 11));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 2, 4));

        // print(twice(half(add(1, 2)))), what the language comes with and what the file declares
        Assert.Equal(SyntaxKind.Builtin, KindAt(found, 3, 0));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 3, 6));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 3, 12));
        Assert.Equal(SyntaxKind.Method, KindAt(found, 3, 17));

        // A func can be called above where it's written
        Assert.Equal(SyntaxKind.Method, KindAt(found, 4, 0));
    }

    [Fact]
    public void KeysMembersDestructuredAndLoopNames()
    {
        const string code = "func show(list) {\n    let { first, rest } = list;\n    for (item in rest) {\n        print(item.name, { size: first });\n    }\n}\n";
        List<SemanticSpan> found = HidlSemantics.Of(code);

        Assert.Equal(SyntaxKind.Local, KindAt(found, 1, 10));
        Assert.Equal(SyntaxKind.Local, KindAt(found, 1, 17));
        Assert.Equal(SyntaxKind.Local, KindAt(found, 2, 9));
        Assert.Equal(SyntaxKind.Local, KindAt(found, 2, 17));

        // print(item.name, { size: first });
        Assert.Equal(SyntaxKind.Local, KindAt(found, 3, 14));
        Assert.Equal(SyntaxKind.Property, KindAt(found, 3, 19));
        Assert.Equal(SyntaxKind.Property, KindAt(found, 3, 27));
        Assert.Equal(SyntaxKind.Local, KindAt(found, 3, 33));
    }

    [Fact]
    public void ALocalWrittenToAgainIsMutableEverywhere()
    {
        const string code = "func count(list) {\n    let total = 0;\n    let once = 1;\n    for (n in list) { total += n; }\n    return total + once;\n}\n";
        List<SemanticSpan> found = HidlSemantics.Of(code);

        Assert.True(At(found, 1, 8)!.Value.Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.True(At(found, 3, 22)!.Value.Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.True(At(found, 4, 11)!.Value.Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.False(At(found, 2, 8)!.Value.Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.False(At(found, 4, 19)!.Value.Modifiers.HasFlag(SyntaxModifiers.Mutable));
    }

    [Fact]
    public void AnInnerNameHidesAnOuterOneOnlyInsideItsBlock()
    {
        const string code = "let size = 1;\nfunc grow() {\n    let size = 2;\n    return size;\n}\nprint(size);\n";
        List<SemanticSpan> found = HidlSemantics.Of(code);

        Assert.Equal(SyntaxKind.Local, KindAt(found, 3, 11));
        Assert.Equal(SyntaxKind.Field, KindAt(found, 5, 6));
    }

    [Fact]
    public void NothingInsideTextsOrCommentsAndHalfTypedCodeStillGoes()
    {
        List<SemanticSpan> found = HidlSemantics.Of("let name = \"name\"; // name\nfunc broken(a, b\n    let c = a +\n");

        Assert.Single(found, span => span.Line == 0);
        Assert.Equal(SyntaxKind.Parameter, KindAt(found, 1, 12));
        Assert.Equal(SyntaxKind.Local, KindAt(found, 2, 8));
    }

    [Fact]
    public void ABigFileIsQuick()
    {
        var code = new System.Text.StringBuilder("let things = {\n");
        for (int i = 0; i < 5000; i++)
            code.Append($"    thing_{i}: {{ name: \"thing {i}\", size: {i % 97}, tags: [\"a\", \"b\"] }},\n");
        code.Append("};\n");
        for (int i = 0; i < 2000; i++)
            code.Append($"func f{i}(a, b) {{ let c = a + b; return c * {i}; }}\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<SemanticSpan> found = HidlSemantics.Of(code.ToString());
        watch.Stop();

        Assert.True(found.Count > 20000);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
