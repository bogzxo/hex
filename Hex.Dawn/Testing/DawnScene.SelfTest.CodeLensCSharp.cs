using System.Numerics;

namespace Hex.Dawn;

// The self test, reference counts over the declarations of a C# file, from csharp-ls's code lenses, when csharp-ls is on
// this machine. Two names declared in one go, a method's count over its attribute, and a press listing a property's uses.
internal sealed partial class DawnScene
{
    private const string LENS_CSHARP = """
        namespace Lens;

        /// <summary>A thing.</summary>
        public class Thing
        {
            private const float A = 4f, B = 12f;

            public int Count { get; set; }

            [System.Obsolete]
            public float Sum() => A + B + A + B;
        }

        public static class Program
        {
            public static void Main()
            {
                var thing = new Thing();
                thing.Count = 3;
                System.Console.WriteLine(thing.Sum() + thing.Count);
            }
        }

        """;

    private void TestCodeLensCSharp(HexPointerScript test)
    {
        if (HexServerCatalog.Locate("csharp-ls") is null)
        {
            test.Run(() => Console.WriteLine("[Hex self-test] csharp-ls isn't on this machine, its reference counts are skipped"));
            return;
        }

        string folder = Path.Combine(testFolder, "selftest_lens"), file = Path.Combine(folder, "Program.cs");
        string[] On(int line) => [.. code.LensesAbove.Where(lens => lens.Line == line).Select(lens => lens.Text)];
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "selftest_lens.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, LENS_CSHARP.ReplaceLineEndings("\n"));
            Open(file);
        });

        // The project loading is the slow part, a count asked for before it's in can say 0 until the server says it's ready
        test.WaitFor(() => On(5) is ["2 references", "2 references"] && On(7) is ["2 references"] && On(9) is ["1 reference"], 90.0f);
        test.Settle(0.5f);
        test.Run(() =>
        {
            Console.WriteLine($"[Hex self-test] lens: csharp-ls counted {string.Join(", ", code.LensesAbove.Select(lens => $"{lens.Line + 1} {lens.Text}"))}");
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "code_lens_csharp.png"));
        });
        test.Check("lens: csharp-ls's counts are over C#, two for a line declaring two constants", () => On(5) is ["2 references", "2 references"] && On(3) is ["1 reference"]);
        test.Check("lens: a method's count is over its attribute, under its doc comment", () => On(9) is ["1 reference"] && On(10).Length == 0 && On(2).Length == 0);

        test.Click(() => code.Module is { } module ? module.ToWorld(code.LensAboveBounds(7, 0).Center) : Vector2.Zero);
        test.WaitFor(() => contextMenu?.Showing is not null, 20.0f);
        test.Check("lens: a press on a property's count lists its two uses, not its own get and set", () =>
            contextMenu?.Showing is { } menu && menu.Items.Count == 3 && menu.Items[0].Label == Said("code_lens.file", ("file", "Program.cs"), ("count", "2"))
            && menu.Items[1].Label == "19   thing.Count = 3;" && menu.Items[2].Label.StartsWith("20   System.Console.WriteLine", StringComparison.Ordinal));
        test.Run(() =>
        {
            contextMenu?.Close();
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
