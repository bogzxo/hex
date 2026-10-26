using Hex.Core;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>D's names told apart by Hex itself, for while no server says what they are.</summary>
public sealed class DSemanticsTests
{
    /// <summary>A D file with a bit of everything in it, a line a thing.</summary>
    public const string SAMPLE = """
        module shapes.geometry;

        import std.stdio : writeln;
        import std.math;

        enum Kind { round, square }
        enum double tau = 6.283;
        immutable int limit = 10;
        alias Real = double;

        interface Shape
        {
            Real area() const;
        }

        struct Point
        {
            int x;
            int y;

            Point offset(int by) const { return Point(x + by, y + by); }
        }

        class Circle : Shape
        {
            private Real radius;
            static int made;

            this(Real radius) { this.radius = radius; made++; }

            @property Real diameter() const { return radius * 2; }
            Real area() const { return radius * radius * tau / 2; }

            deprecated("use area") Real oldArea() { return area(); }
        }

        T largest(T)(T[] items, T fallback = T.init)
        {
            T best = fallback;
            foreach (item; items)
                if (item > best)
                    best = item;
            return best;
        }

        int twice(int value) { return value * 2; }

        void main()
        {
            auto circle = new Circle(3);
            const name = "circle";
            int total = 0;
            total += 5.twice;
            Kind kind = Kind.round;
            Point p = Point(1, 2).offset(limit);
            writeln(name, circle.diameter, largest([1, 2, total]), p.x, kind, circle.oldArea());
        }

        """;

    private static readonly List<SemanticSpan> Found = DSemantics.Of(SAMPLE.ReplaceLineEndings("\n"));

    // Helper method for what the name at a line and column is, by the name and which of it on the line
    private static SemanticSpan At(int line, string name, int which = 0)
    {
        string text = SAMPLE.ReplaceLineEndings("\n").Split('\n')[line];
        int column = -1;
        for (int i = 0; i <= which; i++)
        {
            do
                column = text.IndexOf(name, column + 1, StringComparison.Ordinal);
            while (column > 0 && (char.IsLetterOrDigit(text[column - 1]) || text[column - 1] == '_'));
        }

        return Found.Find(span => span.Line == line && span.Start == column && span.Length == name.Length);
    }

    [Theory]
    [InlineData(0, "shapes", 0, SyntaxKind.Namespace)]
    [InlineData(0, "geometry", 0, SyntaxKind.Namespace)]
    [InlineData(2, "stdio", 0, SyntaxKind.Namespace)]
    [InlineData(5, "Kind", 0, SyntaxKind.Enum)]
    [InlineData(5, "round", 0, SyntaxKind.EnumMember)]
    [InlineData(5, "square", 0, SyntaxKind.EnumMember)]
    [InlineData(6, "tau", 0, SyntaxKind.NamedConstant)]
    [InlineData(7, "limit", 0, SyntaxKind.NamedConstant)]
    [InlineData(8, "Real", 0, SyntaxKind.Class)]
    [InlineData(10, "Shape", 0, SyntaxKind.Interface)]
    [InlineData(12, "Real", 0, SyntaxKind.Class)]
    [InlineData(12, "area", 0, SyntaxKind.Method)]
    [InlineData(15, "Point", 0, SyntaxKind.Struct)]
    [InlineData(17, "x", 0, SyntaxKind.Field)]
    [InlineData(20, "Point", 0, SyntaxKind.Struct)]
    [InlineData(20, "offset", 0, SyntaxKind.Method)]
    [InlineData(20, "by", 0, SyntaxKind.Parameter)]
    [InlineData(20, "Point", 1, SyntaxKind.Struct)]
    [InlineData(20, "x", 0, SyntaxKind.Field)]
    [InlineData(20, "by", 1, SyntaxKind.Parameter)]
    [InlineData(23, "Circle", 0, SyntaxKind.Class)]
    [InlineData(23, "Shape", 0, SyntaxKind.Interface)]
    [InlineData(25, "radius", 0, SyntaxKind.Field)]
    [InlineData(26, "made", 0, SyntaxKind.Field)]
    [InlineData(28, "radius", 0, SyntaxKind.Parameter)]
    [InlineData(28, "radius", 1, SyntaxKind.Field)]
    [InlineData(28, "radius", 2, SyntaxKind.Parameter)]
    [InlineData(28, "made", 0, SyntaxKind.Field)]
    [InlineData(30, "diameter", 0, SyntaxKind.Method)]
    [InlineData(31, "tau", 0, SyntaxKind.NamedConstant)]
    [InlineData(33, "oldArea", 0, SyntaxKind.Method)]
    [InlineData(36, "T", 0, SyntaxKind.TypeParameter)]
    [InlineData(36, "largest", 0, SyntaxKind.Method)]
    [InlineData(36, "items", 0, SyntaxKind.Parameter)]
    [InlineData(36, "fallback", 0, SyntaxKind.Parameter)]
    [InlineData(36, "init", 0, SyntaxKind.Property)]
    [InlineData(38, "best", 0, SyntaxKind.Local)]
    [InlineData(38, "fallback", 0, SyntaxKind.Parameter)]
    [InlineData(39, "item", 0, SyntaxKind.Local)]
    [InlineData(39, "items", 0, SyntaxKind.Parameter)]
    [InlineData(45, "twice", 0, SyntaxKind.Method)]
    [InlineData(45, "value", 1, SyntaxKind.Parameter)]
    [InlineData(49, "circle", 0, SyntaxKind.Local)]
    [InlineData(49, "Circle", 0, SyntaxKind.Class)]
    [InlineData(50, "name", 0, SyntaxKind.Local)]
    [InlineData(52, "twice", 0, SyntaxKind.Method)]
    [InlineData(53, "Kind", 1, SyntaxKind.Enum)]
    [InlineData(53, "round", 0, SyntaxKind.EnumMember)]
    [InlineData(54, "offset", 0, SyntaxKind.Method)]
    [InlineData(54, "limit", 0, SyntaxKind.NamedConstant)]
    [InlineData(55, "writeln", 0, SyntaxKind.Method)]
    [InlineData(55, "diameter", 0, SyntaxKind.Method)]
    [InlineData(55, "largest", 0, SyntaxKind.Method)]
    [InlineData(55, "x", 0, SyntaxKind.Field)]
    [InlineData(55, "oldArea", 0, SyntaxKind.Method)]
    public void Every_kind_of_name_is_told_apart(int line, string name, int which, SyntaxKind kind)
    {
        SemanticSpan found = At(line, name, which);
        Assert.True(found.Length > 0, $"{name} on line {line} wasn't found to be anything");
        Assert.Equal(kind, found.Kind);
    }

    [Fact]
    public void Modifiers_say_static_readonly_mutable_deprecated_and_where_it_is_made()
    {
        Assert.True(At(26, "made").Modifiers.HasFlag(SyntaxModifiers.Static));
        Assert.True(At(7, "limit").Modifiers.HasFlag(SyntaxModifiers.Readonly));
        Assert.True(At(50, "name").Modifiers.HasFlag(SyntaxModifiers.Readonly));
        Assert.True(At(51, "total").Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.True(At(52, "total").Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.True(At(38, "best").Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.False(At(49, "circle").Modifiers.HasFlag(SyntaxModifiers.Mutable));
        Assert.True(At(33, "oldArea").Modifiers.HasFlag(SyntaxModifiers.Deprecated));
        Assert.True(At(55, "oldArea").Modifiers.HasFlag(SyntaxModifiers.Deprecated));
        Assert.True(At(36, "largest").Modifiers.HasFlag(SyntaxModifiers.Declaration));
        Assert.False(At(55, "largest").Modifiers.HasFlag(SyntaxModifiers.Declaration));
    }

    [Fact]
    public void Nothing_in_texts_or_comments_and_keywords_are_left_to_the_grammar()
    {
        List<SemanticSpan> found = DSemantics.Of("// int x = 1;\n/+ nested /+ void f() {} +/ +/\nstring s = \"Point p\";\nint y = cast(int) 2;\n");

        Assert.DoesNotContain(found, span => span.Line < 2);
        Assert.Single(found, span => span.Line == 2);
        Assert.Single(found, span => span.Line == 3);
    }

    [Fact]
    public void A_big_file_is_quick()
    {
        var code = new System.Text.StringBuilder();
        for (int i = 0; i < 3000; i++)
            code.Append(SAMPLE.Replace("module shapes.geometry;", string.Empty).Replace("void main()", $"void main{i}()"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<SemanticSpan> found = DSemantics.Of(code.ToString().ReplaceLineEndings("\n"));
        watch.Stop();

        Assert.True(found.Count > 100000);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
