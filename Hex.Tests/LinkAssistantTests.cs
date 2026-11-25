using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// A model answering in Link Chat: which messages are questions, what goes to the model, how its answer and thinking are
/// said and carried over the wire, and what it does when it can't answer.
/// </summary>
public sealed class LinkAssistantTests
{
    private static readonly LinkAssistantOptions options = new("http://localhost:11434", "qwen3:14b", "", PredictionTuning.Usual);
    private static readonly HexLinkDevice ann = new(new string('A', 64), "Ann"), here = new(new string('B', 64), "Desk");

    private static HexLinkTalk From(HexLinkDevice who, string text, string? model = null, string? thinking = null) =>
        new(Guid.NewGuid(), who, text, DateTimeOffset.UtcNow, model, thinking);

    [Theory]
    [InlineData("@ai what is a span?", "what is a span?")]
    [InlineData("  @AI: why", "why")]
    [InlineData("@ai, explain this", "explain this")]
    [InlineData("@aid me", null)]
    [InlineData("hello @ai", null)]
    [InlineData("@ai   ", null)]
    public void A_question_starts_with_the_mention(string text, string? question) =>
        Assert.Equal(question, LinkAssistant.Question(text));

    [Fact]
    public void A_long_answer_is_said_in_a_few_messages_each_within_the_limit()
    {
        string answer = string.Join(' ', Enumerable.Repeat("word", 2500));
        List<string> parts = LinkAssistant.Parts(answer);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, part => Assert.True(HexLinkTalk.Valid(part)));
        Assert.EndsWith("…", parts[^1]);
        Assert.Equal(["short"], LinkAssistant.Parts("short\r\n\u0007"));
    }

    [Fact]
    public async Task A_question_is_answered_with_the_earlier_questions_and_answers_and_nothing_else()
    {
        var said = new ConcurrentQueue<(string Text, string Model, string? Thinking)>();
        var asked = new ConcurrentQueue<IReadOnlyList<ChatTurn>>();
        var answered = new SemaphoreSlim(0);
        using var assistant = new LinkAssistant(() => options with { SystemPrompt = "Be a pirate." },
            (text, model, thinking) => { said.Enqueue((text, model!, thinking)); return HexLinkTalkResult.Sent; },
            () => here.Id,
            (_, conversation, _) =>
            {
                asked.Enqueue(conversation);
                answered.Release();
                return Task.FromResult(new ChatAnswer("Arr, four.", "Two and two."));
            });

        assistant.Heard(From(ann, "just chatting, not for the model"));
        assistant.Heard(From(ann, "@ai what is 2 + 2?"));
        Assert.True(await answered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Eventually(() => said.Count == 1));
        var (text, model, thinking) = Assert.Single(said);
        Assert.Equal(("Arr, four.", "qwen3:14b", "Two and two."), (text, model, thinking));

        // Its answer comes back through the chat as this device's model's, and is its own turn next time
        assistant.Heard(From(here, "Arr, four.", "qwen3:14b", "Two and two."));
        assistant.Heard(From(ann, "@ai and 3 + 3?"));
        Assert.True(await answered.WaitAsync(TimeSpan.FromSeconds(5)));

        IReadOnlyList<ChatTurn> second = asked.Last();
        Assert.Equal(new ChatTurn("system", "Be a pirate."), second[0]);
        Assert.Equal([new ChatTurn("user", "Ann: what is 2 + 2?"), new ChatTurn("assistant", "Arr, four."), new ChatTurn("user", "Ann: and 3 + 3?")], second.Skip(1));
        Assert.DoesNotContain(second, turn => turn.Content.Contains("chatting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_asked_while_it_is_off_and_a_models_answer_is_never_a_question()
    {
        bool on = false;
        int asks = 0;
        using var assistant = new LinkAssistant(() => on ? options : null, (_, _, _) => HexLinkTalkResult.Sent, () => here.Id,
            (_, _, _) => { Interlocked.Increment(ref asks); return Task.FromResult(new ChatAnswer("x")); });

        assistant.Heard(From(ann, "@ai anyone?"));
        on = true;
        assistant.Heard(From(ann, "@ai from another model", model: "other"));
        await Task.Delay(100);
        Assert.Equal(0, asks);
    }

    [Fact]
    public void Asked_with_no_model_the_device_says_so_in_the_chat_and_says_nothing_when_answering_is_off()
    {
        var said = new ConcurrentQueue<(string Text, string? Model)>();
        string? reply = "No local model is set up on Here to answer @ai.";
        using var assistant = new LinkAssistant(() => null, (text, model, _) => { said.Enqueue((text, model)); return HexLinkTalkResult.Sent; }, () => here.Id)
        {
            NoModel = () => reply
        };

        assistant.Heard(From(ann, "@ai hello"));
        assistant.Heard(From(ann, "just talking"));
        Assert.Equal((reply, null), Assert.Single(said));
        reply = null;
        assistant.Heard(From(ann, "@ai again"));
        Assert.Single(said);
    }

    [Fact]
    public void Only_the_designated_device_says_it_has_no_model()
    {
        var said = new ConcurrentQueue<string>();
        bool designated = false;
        using var assistant = new LinkAssistant(() => null, (text, _, _) => { said.Enqueue(text); return HexLinkTalkResult.Sent; }, () => here.Id)
        {
            NoModel = () => "No model here.", SpeaksForNoModel = _ => designated
        };

        assistant.Heard(From(ann, "@ai hello"));
        Assert.Empty(said);
        designated = true;
        assistant.Heard(From(ann, "@ai hello again"));
        Assert.Equal("No model here.", Assert.Single(said));
    }

    [Fact]
    public async Task A_failure_is_said_here_and_a_short_line_in_the_chat()
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new ConcurrentQueue<string>();
        using var assistant = new LinkAssistant(() => options, (text, _, _) => { said.Enqueue(text); return HexLinkTalkResult.Sent; }, () => here.Id,
            (_, _, _) => throw new PredictionException(PredictionStatus.Unreachable, "Nothing answers at localhost."))
        {
            Failed = failed.SetResult, Unanswered = "Couldn't answer.",
        };

        assistant.Heard(From(ann, "@ai hello"));
        Assert.Equal("Nothing answers at localhost.", await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Eventually(() => said.Count == 1));
        Assert.Equal("Couldn't answer.", Assert.Single(said));
        Assert.True(Eventually(() => assistant.Waiting == 0));
    }

    [Fact]
    public async Task A_models_answer_and_thinking_cross_the_link_and_a_bad_one_is_dropped()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var heard = new TaskCompletionSource<HexLinkTalk>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.LinkA.TalkHeard += message => heard.TrySetResult(message);

        Assert.Equal(HexLinkTalkResult.Sent, pair.LinkB.SayTalk("Four.", "qwen3:14b", "Two and two\nmake four."));
        HexLinkTalk message = await heard.Task.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.Equal(("Four.", "qwen3:14b", "Two and two\nmake four."), (message.Text, message.Model, message.Thinking));

        // Thinking without a model, or past its limit, is refused here and dropped there
        Assert.Equal(HexLinkTalkResult.Invalid, pair.LinkB.SayTalk("x", null, "thought"));
        Assert.Equal(HexLinkTalkResult.Invalid, pair.LinkB.SayTalk("x", "m", new string('t', HexLinkTalk.MOST_THINKING + 1)));
        JsonObject person = From(DeviceB, "A person.").ToJson();
        person["thinking"] = "a person doesn't send thinking";
        Assert.Null(HexLinkTalk.Of(person, DeviceB));
        JsonObject spoofed = From(DeviceB, "x", "m").ToJson();
        spoofed["model"] = "‮";
        Assert.Null(HexLinkTalk.Of(spoofed, DeviceB));
    }

    [Fact]
    public void Thinking_past_its_limit_is_cut_and_said_to_be()
    {
        string cut = LinkAssistant.Thought(new string('t', HexLinkTalk.MOST_THINKING + 50))!;
        Assert.Equal(HexLinkTalk.MOST_THINKING, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Null(LinkAssistant.Thought("  \u0007 "));
    }
}
