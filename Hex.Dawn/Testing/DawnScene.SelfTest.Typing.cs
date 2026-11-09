using System.Diagnostics;
using System.Text;

namespace Hex.Dawn;

// The self test, typing fast into big files, a C# file of twenty thousand lines and a .hor of five thousand, a key every
// tick or so. Every tick of the editor is timed while it goes, and the worst and the average said, the thing to watch
// for a stutter. With HEX_PROFILE set the slow ticks are taken apart step by step as well.
internal sealed partial class DawnScene
{
    // The p95 scripted key-interval budget. Passing does not rule out tail-latency stalls.
    private const double TYPING_TICK_MS = 12.0;

    private void TestTypingSpeed(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "typing-project");
        string csharp = Path.Combine(folder, "typing_big.cs");
        string hor = Path.Combine(folder, "typing_big.hor");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Typing.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(csharp, BigCSharp(20000));
            File.WriteAllText(hor, BigHor(5000));
            // An unrelated project above Temp must not become this fixture's language-server root.
            explorer.OpenFolder(folder);
        });

        TypeInto(test, csharp, "csharp");
        TypeInto(test, hor, "hor");
    }

    private void TypeInto(HexPointerScript test, string file, string what)
    {
        var ticks = new List<double>();
        bool diagnostics = Environment.GetEnvironmentVariable("HEX_TYPING_DIAGNOSTICS") == "1";
        var pauses = new List<(int Key, double Interval, double Pause, int Gen0, int Gen1, int Gen2)>();
        var edits = new List<double>();
        double previousPause = 0;
        int previousGen0 = 0, previousGen1 = 0, previousGen2 = 0;
        long last = 0;
        bool timing = false;
        void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            if (timing && last != 0)
                ticks.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            if (diagnostics)
            {
                double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                if (timing && last != 0)
                    pauses.Add((ticks.Count, ticks[^1], pause - previousPause,
                        gen0 - previousGen0, gen1 - previousGen1, gen2 - previousGen2));
                (previousPause, previousGen0, previousGen1, previousGen2) = (pause, gen0, gen1, gen2);
            }
            last = now;
        }

        test.Run(() =>
        {
            Open(file);
            code.GoTo(code.Document.LineCount / 2, 0);
        });
        test.Settle(1.5f);
        if (what == "csharp")
        {
            test.WaitFor(() => languages.CanServe(document, "documentSymbolProvider") && code.SemanticSpans.Count > 0, 90f);
            test.Check("typing: real C# server is ready in the isolated project", () =>
                languages.TestServerFor(document) is { IsReady: true } server
                && PlatformPaths.Same(server.RootDirectory, Path.GetDirectoryName(file)!)
                && code.SemanticSpans.Count > 0);
            test.Settle(1.0f);
        }
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
                    long editStarted = diagnostics ? Stopwatch.GetTimestamp() : 0;
                    code.Document.Type(key);
                    if (diagnostics)
                        edits.Add(Stopwatch.GetElapsedTime(editStarted).TotalMilliseconds);
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
                if (diagnostics)
                {
                    foreach (var sample in pauses.Where(sample => sample.Interval >= TYPING_TICK_MS))
                        Console.WriteLine(FormattableString.Invariant($"[Hex self-test] typing-tail {what} key={sample.Key} intervalMs={sample.Interval:F3} editMs={edits[sample.Key - 1]:F3} gcPauseDeltaMs={sample.Pause:F3} gen0={sample.Gen0} gen1={sample.Gen1} gen2={sample.Gen2}"));
                    Console.WriteLine(FormattableString.Invariant($"[Hex self-test] typing-edits {what} firstMs={edits[0]:F3} maxMs={edits.Max():F3}"));
                }
            }
        });
        test.Check($"typing: scripted key intervals in a big {what} file meet the p95 budget", () =>
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
