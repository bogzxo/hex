using Horizon.HIDL.Runtime;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test of semantic highlighting. A HIDL file whose every name is drawn as what it is (a parameter, a local, a top
// level let, a const, a function, what the language comes with, a key), a line typed on going back to the grammar's
// colours until the typing stops, and the setting turning it off and on. Then C# through csharp-ls when it's on this
// machine, its names from the server's semantic tokens in ReSharper's colours, and the colours switched to Visual
// Studio's and back. With HEX_SELFTEST_SHOTS set it leaves a picture of each.
internal sealed partial class DawnScene
{
    private const string SEMANTIC_HIDL = """
        // semantic highlighting
        const LIMIT = 10;
        let speed = 3;
        func grow(size, step = 2) {
            let total = size;
            for (item in [1, 2]) { total += item * step; }
            return math.min(total, LIMIT) + speed;
        }
        let twice = (x) => x * 2;
        print(grow(1), { label: twice(speed) });

        """;

    private void TestSemanticHidl(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "semantic.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, SEMANTIC_HIDL.ReplaceLineEndings("\n"));
            Open(file);
        });
        test.WaitFor(() => code.SemanticSpans.Count > 0, 5.0f);
        test.Settle(0.3f);
        SemanticShot(test, "semantic_hidl.png");

        bool Is(int line, int column, SyntaxKind kind, SyntaxModifiers with = SyntaxModifiers.None)
        {
            var (drawn, modifiers) = code.DrawnAt(line, column);
            bool right = drawn == kind && (modifiers & with) == with;
            if (!right)
                Console.WriteLine($"[Hex self-test] semantic: {line}:{column} is {drawn} {modifiers}, wanted {kind} {with}");
            return right;
        }

        test.Check("semantic: HIDL's consts, top level lets and functions are drawn as what they are", () =>
            Is(1, 6, SyntaxKind.NamedConstant, SyntaxModifiers.Declaration) && Is(2, 4, SyntaxKind.Field, SyntaxModifiers.Declaration)
            && Is(3, 5, SyntaxKind.Method, SyntaxModifiers.Declaration) && Is(8, 4, SyntaxKind.Method) && Is(9, 6, SyntaxKind.Method)
            && Is(6, 27, SyntaxKind.NamedConstant) && Is(6, 36, SyntaxKind.Field) && Is(9, 30, SyntaxKind.Field));
        test.Check("semantic: parameters, locals and loop names, a local written to again underlined", () =>
            Is(3, 10, SyntaxKind.Parameter, SyntaxModifiers.Declaration) && Is(3, 16, SyntaxKind.Parameter) && Is(4, 16, SyntaxKind.Parameter)
            && Is(4, 8, SyntaxKind.Local, SyntaxModifiers.Mutable) && Is(5, 27, SyntaxKind.Local, SyntaxModifiers.Mutable)
            && Is(5, 9, SyntaxKind.Local) && Is(5, 43, SyntaxKind.Parameter) && Is(8, 13, SyntaxKind.Parameter));
        test.Check("semantic: what the language comes with, members and keys", () =>
            Is(6, 11, SyntaxKind.Builtin) && Is(6, 16, SyntaxKind.Method) && Is(9, 0, SyntaxKind.Builtin) && Is(9, 17, SyntaxKind.Property));

        // A line typed on is the grammar's until the names come again
        test.Run(() =>
        {
            code.GoTo(4, code.Document.LineAt(4).Length);
            code.Document.Insert(" ");
        });
        test.Check("semantic: a line typed on goes back to the grammar's colours", () => code.DrawnAt(4, 16).Kind != SyntaxKind.Parameter && Is(3, 10, SyntaxKind.Parameter));
        test.Settle(1.0f);
        test.Check("semantic: and has its names again once the typing stops", () => Is(4, 16, SyntaxKind.Parameter));

        // Off, and on again
        test.Run(() => new EditorSource(this).Set("semantic_highlighting", Values.Bool(false)));
        test.Check("semantic: turned off, the names are the grammar's", () => code.SemanticSpans.Count == 0 && code.DrawnAt(3, 16).Kind != SyntaxKind.Parameter);
        test.Run(() => new EditorSource(this).Set("semantic_highlighting", Values.Bool(true)));
        test.WaitFor(() => code.SemanticSpans.Count > 0, 5.0f);
        test.Check("semantic: and on again they're back", () => Is(3, 16, SyntaxKind.Parameter));

        test.Run(() =>
        {
            code.Document.Undo();
            if (document.Path is { } open && PlatformPaths.Same(open, file))
            {
                code.Document.Replace(File.ReadAllText(file));
                CloseNow();
            }
        });
        test.Wait();
    }

    private const string SEMANTIC_CSHARP = """
        using System;

        namespace Shapes;

        public interface IShape { double Area { get; } }
        public enum Kind { Round, Square }
        public struct Point { public int X; }
        public record Size(int Width);
        public delegate void Changed(int by);
        public static class Extensions { public static int Twice(this int value) => value * 2; }

        public class Circle : IShape
        {
            public const double Tau = 6.28;
            private static int made;
            public event Changed? Moved;
            public double Radius { get; set; }
            public double Area => Radius * Radius * Tau / 2;

            [Obsolete("use Grow")]
            public void Old() { }

            public int Grow<T>(T thing, int by)
            {
                int total = by.Twice();
                total += made;
                Kind kind = Kind.Round;
                Moved?.Invoke(total);
                Old();
                return total;
            }
        }

        """;

    private void TestSemanticCSharp(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_semantic"), file = Path.Combine(folder, "Shapes.cs");
        bool there = HexServerCatalog.Locate("csharp-ls") is not null;
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "selftest_semantic.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, SEMANTIC_CSHARP.ReplaceLineEndings("\n"));
            Open(file);
        });

        // C# is ReSharper's colours as it comes, and switching to Visual Studio's is at once
        UIPalette Palette() => compositor.Skin!.Palette;
        System.Numerics.Vector4 ReSharper() => Palette().LightField ? UISyntaxColors.ReSharperLight().Method : UISyntaxColors.ReSharperDark().Method;
        test.Check("semantic: C# is in ReSharper's colours", () => CodeLanguages.ColorsFor(code.Highlighter, Palette()).Of(SyntaxKind.Method) == ReSharper());
        test.Run(() => new EditorSource(this).Set("csharp_colors", new StringValue("visual_studio")));
        test.Check("semantic: and in Visual Studio's the moment they're picked", () =>
            CodeLanguages.ColorsFor(code.Highlighter, Palette()).Of(SyntaxKind.Keyword) == (Palette().LightField ? UISyntaxColors.VisualStudioLight() : UISyntaxColors.VisualStudioDark()).Keyword);
        test.Run(() => new EditorSource(this).Set("csharp_colors", new StringValue("resharper")));
        test.Check("semantic: and back to ReSharper's", () => CodeLanguages.ColorsFor(code.Highlighter, Palette()).Of(SyntaxKind.Method) == ReSharper());

        if (!there)
        {
            test.Run(() => Console.WriteLine("[Hex self-test] csharp-ls isn't on this machine, its semantic tokens are skipped"));
            SemanticShot(test, "semantic_csharp.png");
            CloseSemanticFile(test, file);
            return;
        }

        // Loading the project is the server's slow part, it says nothing of the names until it has
        test.WaitFor(() => code.SemanticSpans.Any(span => span.Kind == SyntaxKind.Class && span.Line == 11), 90.0f);
        test.Settle(0.5f);
        SemanticShot(test, "semantic_csharp.png");
        test.Run(() =>
        {
            var kinds = code.SemanticSpans.GroupBy(span => (span.Kind, span.Modifiers)).Select(group => $"{group.Key.Kind}{(group.Key.Modifiers == SyntaxModifiers.None ? "" : "+" + group.Key.Modifiers)} {group.Count()}");
            Console.WriteLine($"[Hex self-test] semantic: csharp-ls said {code.SemanticSpans.Count} names, {string.Join(", ", kinds)}");
            if (LanguageServers.Running.FirstOrDefault(server => server.Name == "csharp") is { } csharp)
                Console.WriteLine($"[Hex self-test] semantic: csharp-ls's legend is {string.Join(", ", csharp.SemanticLegend.Types)}, modifiers {string.Join(", ", csharp.SemanticLegend.Modifiers)}");
        });
        test.Check("semantic: csharp-ls's names are drawn as what they are, every kind of them", () => DrawnAs(
        [
            (4, 17, SyntaxKind.Interface), (4, 33, SyntaxKind.Property), (5, 12, SyntaxKind.Enum), (5, 19, SyntaxKind.EnumMember),
            (6, 14, SyntaxKind.Struct), (6, 33, SyntaxKind.Field), (11, 13, SyntaxKind.Class), (13, 24, SyntaxKind.NamedConstant),
            (14, 23, SyntaxKind.Field), (15, 17, SyntaxKind.Delegate), (15, 26, SyntaxKind.Event), (16, 18, SyntaxKind.Property),
            (17, 26, SyntaxKind.Property), (22, 15, SyntaxKind.Method), (22, 20, SyntaxKind.TypeParameter), (22, 25, SyntaxKind.Parameter),
            (24, 12, SyntaxKind.Local), (24, 20, SyntaxKind.Parameter), (25, 17, SyntaxKind.Field), (26, 25, SyntaxKind.EnumMember),
            (27, 8, SyntaxKind.Event), (28, 8, SyntaxKind.Method), (7, 14, SyntaxKind.Record), (8, 21, SyntaxKind.Delegate),
            (9, 51, SyntaxKind.ExtensionMethod), (24, 23, SyntaxKind.ExtensionMethod)
        ]));

        // csharp-ls's legend has no delegate, record or extension method, they're made out from how they're declared
        test.Check("semantic: records, delegates and extension methods are coloured, nothing that should be is left the colour of plain text", () =>
            Coloured([(7, 14), (8, 21), (9, 20), (9, 51), (24, 23), (4, 17), (5, 12), (5, 19), (6, 14), (13, 24), (14, 23), (15, 26), (16, 18), (22, 15), (22, 20), (28, 8)]));
        test.Check("semantic: the local written to again is underlined, the one that isn't isn't", () =>
            code.DrawnAt(24, 12).Modifiers.HasFlag(SyntaxModifiers.Mutable) && code.DrawnAt(25, 8).Modifiers.HasFlag(SyntaxModifiers.Mutable)
            && !code.DrawnAt(26, 13).Modifiers.HasFlag(SyntaxModifiers.Mutable));
        test.WaitFor(() => code.Diagnostics.Any(found => found.Deprecated), 20.0f);
        test.Check("semantic: the call of the obsolete method is struck through, from the server's diagnostic", () =>
            code.Diagnostics.Any(found => found.Deprecated && found.Line == 28 && found.Column == 8));
        CloseSemanticFile(test, file);
    }

    private const string SEMANTIC_D = """
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

    private void TestSemanticD(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_semantic_d"), file = Path.Combine(folder, "geometry.d");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, SEMANTIC_D.ReplaceLineEndings("\n"));
            Open(file);
        });

        // serve-d says nothing of what names are, Hex works D's out itself, at once whether serve-d is there or not
        test.WaitFor(() => code.SemanticSpans.Count > 0, 10.0f);
        test.Settle(0.5f);
        SemanticShot(test, "semantic_d.png");
        test.Run(() => Console.WriteLine($"[Hex self-test] semantic: D is linted by {languages.LinterOf(document) ?? "nothing"}, its names worked out here, {code.SemanticSpans.Count} of them"));
        test.Check("semantic: D's names are drawn as what they are, every kind of them", () => DrawnAs(
        [
            (0, 7, SyntaxKind.Namespace), (5, 5, SyntaxKind.Enum), (5, 12, SyntaxKind.EnumMember), (6, 12, SyntaxKind.NamedConstant),
            (8, 6, SyntaxKind.Class), (10, 10, SyntaxKind.Interface), (15, 7, SyntaxKind.Struct), (17, 8, SyntaxKind.Field),
            (20, 10, SyntaxKind.Method), (20, 21, SyntaxKind.Parameter), (23, 6, SyntaxKind.Class), (26, 15, SyntaxKind.Field),
            (36, 0, SyntaxKind.TypeParameter), (36, 2, SyntaxKind.Method), (36, 17, SyntaxKind.Parameter), (38, 6, SyntaxKind.Local),
            (52, 15, SyntaxKind.Method), (53, 21, SyntaxKind.EnumMember), (54, 33, SyntaxKind.NamedConstant), (55, 4, SyntaxKind.Method)
        ]));
        test.Check("semantic: D's local written to again is underlined and its deprecated method struck through", () =>
            code.DrawnAt(51, 8).Modifiers.HasFlag(SyntaxModifiers.Mutable) && code.DrawnAt(33, 32).Modifiers.HasFlag(SyntaxModifiers.Deprecated));
        test.Check("semantic: D's names are coloured, nothing that should be is left the colour of plain text", () =>
            Coloured([(5, 5), (5, 12), (6, 12), (8, 6), (10, 10), (15, 7), (17, 8), (20, 10), (23, 6), (36, 2), (52, 15), (55, 4)]));
        CloseSemanticFile(test, file);
    }

    // Whether every place is drawn as the kind it should be, saying which aren't
    private bool DrawnAs((int Line, int Column, SyntaxKind Kind)[] wanted)
    {
        bool all = true;
        foreach (var (line, column, kind) in wanted)
        {
            var (drawn, modifiers) = code.DrawnAt(line, column);
            if (drawn == kind)
                continue;
            Console.WriteLine($"[Hex self-test] semantic: {line}:{column} is {drawn} {modifiers}, wanted {kind}");
            all = false;
        }

        return all;
    }

    // Whether every place is drawn in a colour of its own, not the colour plain text is
    private bool Coloured((int Line, int Column)[] places)
    {
        UISyntaxColors colors = CodeLanguages.ColorsFor(code.Highlighter, compositor.Skin!.Palette);
        bool all = true;
        foreach (var (line, column) in places)
        {
            var (kind, modifiers) = code.DrawnAt(line, column);
            if (colors.Of(kind, modifiers) != colors.Name)
                continue;
            Console.WriteLine($"[Hex self-test] semantic: {line}:{column} is {kind} {modifiers}, drawn in the colour of plain text");
            all = false;
        }

        return all;
    }

    private void CloseSemanticFile(HexPointerScript test, string file)
    {
        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }

    // A picture of the screen into HEX_SELFTEST_SHOTS, when it's set
    private void SemanticShot(HexPointerScript test, string name)
    {
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });
        // The picture is written on a thread of its own, a run that ends straight after left it empty
        test.Settle(1.5f);
    }
}
