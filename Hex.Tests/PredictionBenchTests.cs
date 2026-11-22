using System.Diagnostics;
using System.Text;
using Hex.Core;
using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>
/// Real local models through a real Ollama over a few small files in different languages: only with HEX_TEST_OLLAMA_URL
/// set (HEX_TEST_MODELS names the models, comma separated), and then it prints the time to the first piece, the total time
/// and what the model would change. Otherwise it passes without asking anything.
/// </summary>
public sealed class PredictionBenchTests(ITestOutputHelper output)
{
    private const string ZETA = "hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M";

    private static readonly (string Name, string Path, string Language, string Text, string Marker)[] cases =
    [
        ("csharp-mid-line", "src/Account.cs", "csharp",
            "namespace Bank;\n\npublic sealed class Account\n{\n    private decimal balance;\n\n    public decimal Balance => balance;\n\n    public void Deposit(decimal amount)\n    {\n        if (amount <= 0)\n            throw new ArgumentOutOfRangeException(nameof(amount));\n        balance += amount;\n    }\n\n    public void Withdraw(decimal amount)\n    {\n        if (amount <= 0)\n            throw new ArgumentOutOfRangeException(nameof(amount));\n        if (amount > balance)\n            throw new <CARET>\n    }\n}\n", "<CARET>"),
        ("csharp-end-of-file", "src/Util.cs", "csharp",
            "static class Util\n{\n    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;\n\n    public static int Max(int a, int b) => <CARET>", "<CARET>"),
        ("csharp-multi-line", "src/Shape.cs", "csharp",
            "abstract class Shape { public abstract double Area(); }\n\nclass Circle(double r) : Shape\n{\n    public override double Area() => Math.PI * r * r;\n}\n\nclass Square(double s) : Shape\n{\n    <CARET>\n}\n", "<CARET>"),
        ("hor-extension", "scripts/spawn.hor", "hor",
            "// the .hor extension language\nfn spawn_wave(count: int) {\n    for i in 0..count {\n        let e = entity.create(\"enemy\");\n        e.position = vec3(i * 2.0, 0.0, 0.0);\n        e.<CARET>\n    }\n}\n", "<CARET>"),
        ("markdown", "docs/notes.md", "markdown",
            "# Notes\n\n## Install\n\n1. Download the release.\n2. Unzip it.\n3. <CARET>\n\n## Usage\n", "<CARET>"),
        ("json", "package.json", "json",
            "{\n  \"name\": \"demo\",\n  \"version\": \"1.0.0\",\n  \"scripts\": {\n    \"build\": \"tsc\",\n    \"test\": \"vitest\",\n    <CARET>\n  }\n}\n", "<CARET>"),
    ];

    [Fact]
    public async Task The_engine_typing_over_a_real_model_cancels_and_does_not_pile_up()
    {
        string? url = Environment.GetEnvironmentVariable("HEX_TEST_OLLAMA_URL");
        if (string.IsNullOrEmpty(url))
            return;
        string model = (Environment.GetEnvironmentVariable("HEX_TEST_MODELS") ?? ZETA).Split(',')[0];
        string[] lines = ["static class Util", "{", "    public static int Max(int a, int b) => "];
        int version = 1;
        var engine = new PredictionEngine(TimeProvider.System)
        {
            ProviderOverride = new OllamaProvider(PredictionProviders.Shared, PredictionProviders.OllamaUrl(url)!, model),
            Options = () => PredictionTestKit.Options("infer", 200) with { Model = model, ApiUrl = url },
        };
        engine.Snapshot = asked => new PredictionSnapshot(asked, version, "src/a.cs", "csharp", lines, lines.Length - 1, lines[^1].Length);
        var document = new object();
        string typing = "a > ";
        var clock = Stopwatch.StartNew();

        // keystrokes every 400 ms, so each pause is past the debounce and a request is in flight when the next key comes
        foreach (char c in typing)
        {
            lines[^1] += c;
            version++;
            engine.Edited(document, version);
            long until = clock.ElapsedMilliseconds + 400;
            while (clock.ElapsedMilliseconds < until)
            {
                engine.Tick();
                await Task.Delay(10);
            }
        }
        // fast typing: 10 keys 40 ms apart
        for (int i = 0; i < 10; i++)
        {
            lines[^1] += "b";
            version++;
            engine.Edited(document, version);
            long until = clock.ElapsedMilliseconds + 40;
            while (clock.ElapsedMilliseconds < until)
            {
                engine.Tick();
                await Task.Delay(5);
            }
        }
        var last = Stopwatch.StartNew();
        PredictionResult? result = null;
        while (last.ElapsedMilliseconds < 20000 && (result = engine.Take()) is null)
        {
            engine.Tick();
            await Task.Delay(10);
        }
        output.WriteLine($"{model}: requests {engine.Requests}, cancelled {engine.Cancelled}, result after last key {(result is null ? "none" : last.ElapsedMilliseconds + " ms")}, status {engine.Status} '{engine.StatusText}'");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task A_missing_model_and_a_dead_server_fail_quickly_and_in_words()
    {
        string? url = Environment.GetEnvironmentVariable("HEX_TEST_OLLAMA_URL");
        if (string.IsNullOrEmpty(url))
            return;
        var input = new PredictionInput("a.cs", "csharp", "int x = ;", 8, [], []);
        foreach ((string target, string model) in new[] { (url, "no-such-model:1b"), ("http://127.0.0.1:1", "x") })
        {
            IPredictionFormat format = PredictionFormats.For(PredictionTestKit.Options("infer") with { Model = model });
            PredictionPrompt prompt = format.Build(input, 256)!;
            var provider = new OllamaProvider(PredictionProviders.Shared, PredictionProviders.OllamaUrl(target)!, model);
            var clock = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<PredictionException>(async () =>
            {
                await foreach (string _ in provider.Complete(prompt.Request, default)) { }
            });
            output.WriteLine($"{target} {model}: {e.Status} after {clock.ElapsedMilliseconds} ms: {e.Message}");
        }
    }

    [Fact]
    public async Task Local_models_over_a_few_languages()
    {
        string? url = Environment.GetEnvironmentVariable("HEX_TEST_OLLAMA_URL");
        if (string.IsNullOrEmpty(url))
            return;
        int runs = int.TryParse(Environment.GetEnvironmentVariable("HEX_TEST_RUNS"), out int r) ? r : 4;
        string[] models = (Environment.GetEnvironmentVariable("HEX_TEST_MODELS") ?? ZETA).Split(',');

        foreach (string model in models)
        {
            PredictionOptions options = PredictionTestKit.Options("infer") with { Model = model, ApiUrl = url };
            IPredictionFormat format = PredictionFormats.For(options);
            var provider = new OllamaProvider(PredictionProviders.Shared, PredictionProviders.OllamaUrl(url)!, model);
            output.WriteLine($"== {model} (format {format.Name})");
            foreach (var c in cases)
            {
                int caret = c.Text.IndexOf(c.Marker, StringComparison.Ordinal);
                string text = c.Text.Replace(c.Marker, "", StringComparison.Ordinal);
                var input = new PredictionInput(c.Path, c.Language, text, caret, [], []);
                PredictionPrompt? prompt = format.Build(input, 256);
                if (prompt is null)
                {
                    output.WriteLine($"{c.Name}: no prompt");
                    continue;
                }
                var firsts = new List<double>();
                var totals = new List<double>();
                string shown = "";
                for (int run = 0; run < runs; run++)
                {
                    var clock = Stopwatch.StartNew();
                    double first = 0;
                    int pieces = 0;
                    var answer = new StringBuilder();
                    PredictionParsed? parsed = null;
                    try
                    {
                        await foreach (string piece in provider.Complete(prompt.Request, default))
                        {
                            if (pieces++ == 0)
                                first = clock.Elapsed.TotalMilliseconds;
                            answer.Append(piece);
                            parsed = format.Parse(prompt, answer.ToString(), false);
                            if (parsed.Done)
                                break;
                        }
                    }
                    catch (PredictionException e)
                    {
                        output.WriteLine($"{c.Name}: {e.Message}");
                        break;
                    }
                    if (parsed is not { Done: true })
                        parsed = format.Parse(prompt, answer.ToString(), true);
                    firsts.Add(first);
                    totals.Add(clock.Elapsed.TotalMilliseconds);
                    if (run == runs - 1)
                        shown = string.Join(" | ", parsed.Edits.Select(e => $"{e.Start}-{e.End}:{e.Text.Replace("\n", "\\n")}")) +
                            $"  ghost={PredictionDiff.IsGhost(parsed.Edits, text, caret)} raw={answer.ToString().Replace("\n", "\\n")[..Math.Min(answer.Length, 160)]}";
                }
                if (firsts.Count > 0)
                    output.WriteLine($"{c.Name}: prompt~{PredictionTokens.Of(prompt.Request.Prompt)}t first[{string.Join(",", firsts.Select(f => f.ToString("0")))}] total[{string.Join(",", totals.Select(f => f.ToString("0")))}]\n    {shown}");
            }
        }
    }
}
