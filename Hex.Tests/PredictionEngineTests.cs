using System.Runtime.CompilerServices;
using Hex.Core;

namespace Hex.Tests;

/// <summary>The life of a prediction, on a clock the test moves and with a provider that answers from a script.</summary>
public sealed class PredictionEngineTests
{
    private readonly object document = new();
    private readonly HandClock clock = new();
    private string[] lines = ["int x = "];

    private PredictionEngine Engine(IPredictionProvider provider, string format = "qwen")
    {
        var engine = new PredictionEngine(clock)
        {
            ProviderOverride = provider,
            Options = () => PredictionTestKit.Options(format),
        };
        engine.Snapshot = asked => new PredictionSnapshot(asked, version, "src/a.cs", "csharp", lines, lines.Length - 1, lines[^1].Length);
        return engine;
    }

    private int version = 1;

    // An edit at a new version, as the glue says it
    private void Type(PredictionEngine engine, bool typedThrough = false) => engine.Edited(document, ++version, typedThrough);

    // The debounce over and the tick that starts the request
    private void Pause(PredictionEngine engine)
    {
        clock.Advance(200);
        engine.Tick();
    }

    private static async IAsyncEnumerable<string> Stall([EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Delay(Timeout.Infinite, cancel);
        yield break;
    }

    [Fact]
    public async Task A_burst_of_typing_is_one_request()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);

        for (int i = 0; i < 5; i++)
        {
            Type(engine);
            clock.Advance(50);
            engine.Tick();
        }
        Assert.Equal(0, provider.Asked);

        Pause(engine);
        await engine.Running;

        Assert.Equal(1, provider.Asked);
        PredictionResult result = engine.Take()!;
        Assert.Equal(new PredictionEdit(8, 8, "42;"), Assert.Single(result.Edits));
        Assert.True(result.Ghost);
        Assert.False(result.FromCache);
        Assert.Equal(version, result.Version);
        Assert.Equal(PredictionStatus.Ok, engine.Status);
        Assert.Null(engine.Take());
    }

    [Fact]
    public async Task A_newer_edit_cancels_the_request_in_flight()
    {
        var provider = new ScriptedProvider((_, cancel) => Stall(cancel));
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        Assert.Equal(PredictionStatus.Waiting, engine.Status);
        Task flight = engine.Running;

        Type(engine);
        await flight;

        Assert.Equal(1, engine.Cancelled);
        Assert.Equal(1, provider.Cancelled);
        Assert.Null(engine.Take());
    }

    [Fact]
    public async Task An_answer_for_a_version_the_document_moved_past_is_never_handed_over()
    {
        var answer = new TaskCompletionSource();
        var provider = new ScriptedProvider((_, cancel) => After(answer.Task, cancel));
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);

        Type(engine, typedThrough: true);
        answer.SetResult();
        await engine.Running;

        Assert.Equal(0, engine.Cancelled);
        Assert.Null(engine.Take());
    }

    private static async IAsyncEnumerable<string> After(Task gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        yield return "42;";
    }

    [Fact]
    public async Task Typing_through_a_prediction_leaves_it_be()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        await engine.Running;
        Assert.NotNull(engine.Take());

        Type(engine, typedThrough: true);
        Pause(engine);

        Assert.Equal(1, provider.Asked);
        Assert.Equal(0, engine.Cancelled);
    }

    [Fact]
    public async Task The_cache_answers_the_same_text_again_with_no_request()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        await engine.Running;
        engine.Take();

        Type(engine);
        Pause(engine);
        await engine.Running;

        PredictionResult result = engine.Take()!;
        Assert.True(result.FromCache);
        Assert.Equal(1, provider.Asked);
        Assert.Equal(1, engine.Requests);
    }

    [Fact]
    public async Task Three_failures_in_a_row_wait_half_a_minute_before_the_next_try()
    {
        var provider = new ScriptedProvider((_, _) => throw new PredictionException(PredictionStatus.Unreachable, "Can't reach localhost:11434."));
        PredictionEngine engine = Engine(provider);

        for (int i = 0; i < 3; i++)
        {
            Type(engine);
            Pause(engine);
            await engine.Running;
        }
        Assert.Equal(3, provider.Asked);
        Assert.Equal(PredictionStatus.Unreachable, engine.Status);
        Assert.Contains("localhost", engine.StatusText);

        Type(engine);
        Pause(engine);
        await engine.Running;
        Assert.Equal(3, provider.Asked);

        clock.Advance(30_000);
        engine.Tick();
        await engine.Running;
        Assert.Equal(4, provider.Asked);
    }

    [Fact]
    public async Task Taking_a_prediction_asks_for_the_next_at_once()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        await engine.Running;
        engine.Take();

        lines = ["int x = 42;", ""];
        Type(engine);
        engine.Accepted(document);
        engine.Tick();
        await engine.Running;

        Assert.Equal(2, provider.Asked);
    }

    [Fact]
    public async Task A_dismissed_prediction_isnt_asked_again_until_something_is_typed()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        await engine.Running;

        engine.Dismissed(document);
        Assert.Null(engine.Take());
        Pause(engine);
        Assert.Equal(1, provider.Asked);

        engine.PredictNow(document);
        engine.Tick();
        await engine.Running;
        Assert.Equal(PredictionStatus.Ok, engine.Status);
    }

    [Fact]
    public async Task A_settled_answer_stops_the_stream_and_closes_it()
    {
        lines = [.. Enumerable.Range(0, 12).Select(i => "line" + i)];
        string answer = "line0\nLINE1\nline2\nline3\nline4\nline5\n";
        var provider = new ScriptedProvider((_, cancel) => ScriptedProvider.Pieces([.. answer.Split('\n').Select(line => line + "\n")], true, cancel));
        PredictionEngine engine = Engine(provider, "zeta2");
        Type(engine);
        Pause(engine);
        await engine.Running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, provider.Closed);
        PredictionResult result = engine.Take()!;
        Assert.Equal(new PredictionEdit(6, 10, "LINE"), Assert.Single(result.Edits));
        Assert.False(result.Ghost);
    }

    [Fact]
    public void Nothing_is_asked_while_predictions_are_off_or_the_gates_are_closed()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        engine.Options = () => null;
        Type(engine);
        Pause(engine);
        Assert.Equal(PredictionStatus.Off, engine.Status);

        engine.Options = () => PredictionTestKit.Options();
        engine.Snapshot = _ => null;
        Type(engine);
        Pause(engine);

        Assert.Equal(0, provider.Asked);
    }

    [Fact]
    public async Task A_forgotten_document_drops_what_was_waiting_for_it()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider);
        Type(engine);
        Pause(engine);
        await engine.Running;

        engine.Forget(document);

        Assert.Null(engine.Take());
    }

    [Fact]
    public async Task The_edits_made_go_with_the_next_prompt()
    {
        var provider = new ScriptedProvider("42;");
        PredictionEngine engine = Engine(provider, "zeta2");
        lines = ["int x;", "int y;"];
        Type(engine);
        Pause(engine);
        await engine.Running;

        lines = ["int x = 1;", "int y;"];
        Type(engine);
        Pause(engine);
        await engine.Running;

        Assert.Contains("<filename>edit_history\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1,2 +1,2 @@\n-int x;\n+int x = 1;\n int y;\n", provider.Last!.Prompt);
    }
}
