using System.Diagnostics;
using System.Text;
using Hex.Core;
using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>
/// The real Zeta 2 through a real Ollama, asked the model card's example: only with HEX_TEST_OLLAMA=1, and then it
/// prints how long it took. Otherwise it passes without asking anything.
/// </summary>
public sealed class PredictionOllamaTests(ITestOutputHelper output)
{
    private const string MODEL = "hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M";

    [Fact]
    public async Task Zeta_2_predicts_the_model_cards_example()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_OLLAMA") != "1")
            return;

        var (input, regionStart, regionEnd, _) = PredictionTestKit.ZetaSample();
        var format = new Zeta2Format();
        PredictionPrompt prompt = format.Build(input, 256, regionStart, regionEnd, 0, input.Text.Length)!;
        var provider = new OllamaProvider(PredictionProviders.Shared, PredictionProviders.OllamaUrl("http://localhost:11434")!, MODEL);

        for (int run = 0; run < 2; run++)
        {
            var clock = Stopwatch.StartNew();
            double first = 0;
            int pieces = 0;
            var answer = new StringBuilder();
            PredictionParsed? parsed = null;
            await foreach (string piece in provider.Complete(prompt.Request, default))
            {
                if (pieces++ == 0)
                    first = clock.Elapsed.TotalMilliseconds;
                answer.Append(piece);
                parsed = format.Parse(prompt, answer.ToString(), false);
                if (parsed.Done)
                    break;
            }
            if (parsed is not { Done: true })
                parsed = format.Parse(prompt, answer.ToString(), true);

            output.WriteLine($"run {run}: first {first:0} ms, total {clock.Elapsed.TotalMilliseconds:0} ms, " +
                $"{pieces} pieces, prompt ~{PredictionTokens.Of(prompt.Request.Prompt)} tokens, max {prompt.Request.MaxTokens}, " +
                $"stopped early {parsed.Done && answer.ToString().IndexOf(Zeta2Format.UPDATED, StringComparison.Ordinal) < 0}, " +
                $"edits {parsed.Edits.Count}, ghost {PredictionDiff.IsGhost(parsed.Edits, input.Text, input.Caret)}");
            foreach (PredictionEdit edit in parsed.Edits)
                output.WriteLine($"  {edit.Start}-{edit.End}: {edit.Text.Replace("\n", "\\n")}");

            Assert.NotEmpty(parsed.Edits);
        }
    }
}
