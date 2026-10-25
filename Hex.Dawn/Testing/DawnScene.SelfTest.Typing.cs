using System.Diagnostics;
using System.Text;

namespace Hex.Dawn;

// The self test, typing fast into big files, a C# file of twenty thousand lines and a .hor of five thousand, a key every
// tick or so. Every tick of the editor is timed while it goes, and the worst and the average said, the thing to watch
// for a stutter. With HEX_PROFILE set the slow ticks are taken apart step by step as well.
internal sealed partial class DawnScene
{
    // The longest a tick of typing may take before it's called a stutter, a frame and a half at 120
    private const double TYPING_TICK_MS = 12.0;

    private void TestTypingSpeed(HexPointerScript test)
    {
        string csharp = Path.Combine(testFolder, "typing_big.cs");
        string hor = Path.Combine(testFolder, "typing_big.hor");
        test.Run(() =>
        {
            File.WriteAllText(csharp, BigCSharp(20000));
            File.WriteAllText(hor, BigHor(5000));
        });

        TypeInto(test, csharp, "csharp");
        TypeInto(test, hor, "hor");
    }

    private void TypeInto(HexPointerScript test, string file, string what)
    {
        var ticks = new List<double>();
        long last = 0;
        bool timing = false;
        void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            if (timing && last != 0)
                ticks.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
        }

        test.Run(() =>
        {
            Open(file);
            code.GoTo(code.Document.LineCount / 2, 0);
        });
        test.Settle(1.5f);
        test.Run(() => timing = true);

        const string typed = "var something = another.Call(1, 2) + 3; ";
        for (int round = 0; round < 3; round++)
        {
            foreach (char c in typed)
            {
                char key = c;
                test.Run(() =>
                {
                    Tick();
                    code.Document.Type(key);
                });
            }
        }

        test.Settle(0.5f);
        test.Run(() =>
        {
            timing = false;
            if (ticks.Count > 0)
            {
                var sorted = ticks.OrderBy(t => t).ToList();
                Console.WriteLine($"[Hex self-test] typing {what}: {ticks.Count} keys, average {ticks.Average():0.0} ms a key, median {sorted[sorted.Count / 2]:0.0}, 95th {sorted[(int)(sorted.Count * 0.95)]:0.0}, worst {sorted[^1]:0.0}");
            }
        });
        test.Check($"typing: keys into a big {what} file come a tick apart, no stutter", () =>
            ticks.Count > 50 && ticks.OrderBy(t => t).ElementAt((int)(ticks.Count * 0.95)) < TYPING_TICK_MS);

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

    private static string BigCSharp(int lines)
    {
        var text = new StringBuilder("using System;\nusing System.Collections.Generic;\n\nnamespace Big;\n\n");
        int written = 5, n = 0;
        while (written < lines)
        {
            text.Append($"public sealed class Thing{n}\n{{\n    private readonly List<int> numbers = new();\n\n");
            text.Append($"    /// <summary>Adds up what it has, {n} times over.</summary>\n    public int Sum(int times)\n    {{\n        int total = 0;\n");
            text.Append("        for (int i = 0; i < times; i++)\n        {\n            foreach (int number in numbers)\n                total += number * i;\n        }\n\n");
            text.Append($"        return total + {n}; // the end of it\n    }}\n}}\n\n");
            written += 19;
            n++;
        }

        return text.ToString();
    }

    private static string BigHor(int lines)
    {
        var text = new StringBuilder("// a big file of data\nlet things = {\n");
        for (int i = 0; i < lines - 3; i++)
            text.Append($"    thing_{i}: {{ name: \"thing {i}\", size: {i % 97}, tags: [\"a\", \"b\"] }},\n");
        text.Append("};\n");
        return text.ToString();
    }
}
