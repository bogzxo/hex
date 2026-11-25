using System.Diagnostics;
using System.Text;
using Hex.Core;
using Hex.Dawn;
using Xunit.Abstractions;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// A real llama-server and model through Hex's own code: only with HEX_TEST_LLAMACPP_SERVER (llama-server's path) and
/// HEX_TEST_LLAMACPP_MODEL (a .gguf) set, HEX_TEST_LLAMACPP_OFFLOAD optionally. Started, timed, asked for a prediction
/// the way the editor asks, asked a question the way Link Chat does across a link, and stopped. Otherwise it passes
/// without starting anything.
/// </summary>
public sealed class LlamaCppLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_real_llama_server_starts_predicts_answers_in_link_chat_and_stops()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_LLAMACPP_SERVER") is not { Length: > 0 } server
            || Environment.GetEnvironmentVariable("HEX_TEST_LLAMACPP_MODEL") is not { Length: > 0 } model)
            return;

        const int port = 8093;
        string offload = Environment.GetEnvironmentVariable("HEX_TEST_LLAMACPP_OFFLOAD") is { Length: > 0 } said ? said : LlamaCppServerArgs.AUTO;
        var config = new LlamaCppServerConfig(server, model, "hex-live", port, 32768, offload, string.Empty);
        using var llama = new LlamaCppServer();
        var clock = Stopwatch.StartNew();
        bool ready = await llama.Start(config, TimeSpan.FromMinutes(10));
        output.WriteLine($"start: {ready} in {clock.Elapsed.TotalSeconds:0.0} s, {llama.Problem}");
        output.WriteLine(string.Join("\n", llama.Log.Where(line => line.Contains("offload", StringComparison.OrdinalIgnoreCase) || line.Contains("CUDA", StringComparison.Ordinal)
            || line.Contains("fit", StringComparison.OrdinalIgnoreCase) || line.StartsWith('>')).Take(25)));
        Assert.True(ready, llama.Problem);
        Assert.Equal(LlamaCppServerState.Ready, llama.State);

        try
        {
            // Timed, as the setup's Time It does
            LlamaCppSpeed speed = await llama.Measure(port, "hex-live", default);
            output.WriteLine($"speed: read {speed.PromptPerSecond:0} t/s, wrote {speed.AnswerPerSecond:0.0} t/s ({speed.Tokens} tokens)\n{speed.Text}");
            Assert.True(speed.AnswerPerSecond > 0);

            // A prediction as the editor asks for one: Qwen's fill in the middle through the llama.cpp provider
            var options = new PredictionOptions(true, PredictionProviders.LLAMACPP, LlamaCppServerArgs.CompletionsUrl(port), null, "hex-live", "qwen", 64, 100);
            IPredictionProvider provider = PredictionProviders.Make(options)!;
            string prompt = "<|fim_prefix|>static int Add(int a, int b)\n{\n    return <|fim_suffix|>;\n}\n<|fim_middle|>";
            clock.Restart();
            var predicted = new StringBuilder();
            await foreach (string piece in provider.Complete(new PredictionWireRequest(prompt, null, 32, ["<|fim_pad|>", "<|endoftext|>", "\n"], "qwen"), default))
                predicted.Append(piece);
            output.WriteLine($"prediction in {clock.ElapsedMilliseconds} ms: [{predicted}]");
            Assert.Contains("a", predicted.ToString());
            Assert.Contains("b", predicted.ToString());

            // A question in Link Chat, answered from this device's server, heard on the other
            using var pair = await LinkedPair.Up(HexLinkRights.View);
            var asked = new LinkAssistantOptions(LlamaCppServerArgs.CompletionsUrl(port), "hex-live", string.Empty, PredictionTuning.Usual with { Temperature = 0.7f })
            {
                Provider = PredictionProviders.OPENAI,
            };
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var assistant = new LinkAssistant(() => asked, (text, name, thinking) => pair.LinkA.SayTalk(text, name, thinking), () => DeviceA.Id)
            {
                Failed = why => failed.TrySetResult(why),
            };
            pair.LinkA.TalkHeard += assistant.Heard;
            var answered = new TaskCompletionSource<HexLinkTalk>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.LinkB.TalkHeard += message =>
            {
                if (message.Model is not null)
                    answered.TrySetResult(message);
            };
            clock.Restart();
            Assert.Equal(HexLinkTalkResult.Sent, pair.LinkB.SayTalk("@ai In one sentence, what does C#'s `record` keyword make?"));
            Task done = await Task.WhenAny(answered.Task, failed.Task, Task.Delay(TimeSpan.FromMinutes(4)));
            Assert.True(done == answered.Task, failed.Task.IsCompleted ? await failed.Task : "no answer in time");
            HexLinkTalk reply = await answered.Task;
            output.WriteLine($"Link Chat answer in {clock.Elapsed.TotalSeconds:0.0} s as {reply.Model}: {reply.Text}");
            Assert.Equal("hex-live", reply.Model);
        }
        finally
        {
            llama.Stop();
        }

        Assert.Equal(LlamaCppServerState.Stopped, llama.State);
        Assert.Null(await llama.Answers(LlamaCppServerArgs.Root(port), default));
    }
}
