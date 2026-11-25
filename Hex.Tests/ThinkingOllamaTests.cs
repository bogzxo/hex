using System.Diagnostics;
using System.Text;
using Hex.Core;
using Hex.Dawn;
using Xunit.Abstractions;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// A real thinking model through a real Ollama: only with HEX_TEST_OLLAMA=1, the model HEX_TEST_THINKING_MODEL or
/// qwen3:14b. Its chat with thinking on and off, a prediction it thinks before, and Link Chat's question and answer
/// across a link in memory. Otherwise they pass without asking anything.
/// </summary>
public sealed class ThinkingOllamaTests(ITestOutputHelper output)
{
    private const string URL = "http://localhost:11434";
    private static string Model => Environment.GetEnvironmentVariable("HEX_TEST_THINKING_MODEL") is { Length: > 0 } named ? named : "qwen3:14b";
    private static bool Live => Environment.GetEnvironmentVariable("HEX_TEST_OLLAMA") == "1";

    [Fact]
    public async Task The_chat_keeps_its_thinking_apart_when_on_and_has_none_when_off()
    {
        if (!Live)
            return;

        var on = new OllamaChat(PredictionProviders.Shared, OllamaChat.Url(URL)!, Model) { Tuning = PredictionTuning.Usual with { Thinking = "on", Temperature = 0.6f } };
        Assert.True(await on.CanThink(default));

        var clock = Stopwatch.StartNew();
        ChatAnswer thought = await on.Ask([new ChatTurn("system", LinkAssistant.USUAL_SYSTEM), new ChatTurn("user", "Ann: What is 17 * 23? Just the number.")], default);
        output.WriteLine($"on, {clock.ElapsedMilliseconds} ms\nthinking: {thought.Thinking?.Length} chars\n{thought.Thinking}\nanswer: {thought.Text}");
        Assert.Contains("391", thought.Text);
        Assert.False(string.IsNullOrWhiteSpace(thought.Thinking));
        Assert.DoesNotContain("<think>", thought.Text);

        var off = new OllamaChat(PredictionProviders.Shared, OllamaChat.Url(URL)!, Model) { Tuning = PredictionTuning.Usual with { Thinking = "off" } };
        clock.Restart();
        ChatAnswer straight = await off.Ask([new ChatTurn("user", "Ann: What is 17 * 23? Just the number.")], default);
        output.WriteLine($"off, {clock.ElapsedMilliseconds} ms, answer: {straight.Text}, thinking: {straight.Thinking ?? "(none)"}");
        Assert.Contains("391", straight.Text);
        Assert.Null(straight.Thinking);
    }

    [Fact]
    public async Task A_prediction_it_thinks_before_has_the_thinking_taken_out()
    {
        if (!Live)
            return;

        // A raw prompt in the model's own chat template: the model writes <think>...</think> into the answer itself, which
        // the provider has to take out, and "\n\n" ends it only once the thinking is over
        const string prompt = "<|im_start|>user\nComplete this C# method body, code only, one line:\nint Add(int a, int b) => <|im_end|>\n<|im_start|>assistant\n";
        var request = new PredictionWireRequest(prompt, null, 64, ["<|im_end|>", "\n\n"], "qwen");
        var provider = new OllamaProvider(PredictionProviders.Shared, PredictionProviders.OllamaUrl(URL)!, Model)
        {
            Tuning = PredictionTuning.Usual with { Thinking = "on" },
            Loading = TimeSpan.FromMinutes(2),
            FirstPiece = TimeSpan.FromSeconds(30),
        };

        var clock = Stopwatch.StartNew();
        var answer = new StringBuilder();
        await foreach (string piece in provider.Complete(request, default))
            answer.Append(piece);
        output.WriteLine($"{clock.ElapsedMilliseconds} ms: [{answer}]");

        Assert.DoesNotContain("<think>", answer.ToString());
        Assert.DoesNotContain("</think>", answer.ToString());
        Assert.Contains("a + b", answer.ToString().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("a+b", "a + b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Link_Chat_asks_the_model_and_the_other_device_gets_its_answer_and_thinking()
    {
        if (!Live)
            return;

        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var options = new LinkAssistantOptions(URL, Model, string.Empty, PredictionTuning.Usual with { Thinking = "on", Temperature = 0.6f });
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The model is A's; B's person asks it
        using var assistant = new LinkAssistant(() => options, (text, model, thinking) => pair.LinkA.SayTalk(text, model, thinking), () => DeviceA.Id)
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

        var clock = Stopwatch.StartNew();
        Assert.Equal(HexLinkTalkResult.Sent, pair.LinkB.SayTalk("@ai In one sentence, what is a C# record?"));
        Task done = await Task.WhenAny(answered.Task, failed.Task, Task.Delay(TimeSpan.FromMinutes(3)));
        Assert.True(done == answered.Task, failed.Task.IsCompleted ? await failed.Task : "no answer in time");

        HexLinkTalk reply = await answered.Task;
        output.WriteLine($"{clock.ElapsedMilliseconds} ms from {reply.Sender.Name} as {reply.Model}\nthinking ({reply.Thinking?.Length} chars): {reply.Thinking}\nanswer: {reply.Text}");
        Assert.Equal(DeviceA.Id, reply.Sender.Id);
        Assert.Equal(Model, reply.Model);
        Assert.False(string.IsNullOrWhiteSpace(reply.Thinking));
        Assert.Contains("record", reply.Text, StringComparison.OrdinalIgnoreCase);
    }
}
