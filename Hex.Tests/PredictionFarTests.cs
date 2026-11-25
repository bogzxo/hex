using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>Far (jump) predictions from the model (docs/next-edit-plan.md, phase 5): where to ask, when, what becomes of the answers.</summary>
public sealed class PredictionFarTests
{
    private const string PATH = "src/a.cs";

    // A file of thirty lines: the first line was "int count = 0;" before the user renamed it, and the old name is still
    // written on the lines given
    private static string[] FileAfter(params int[] uses)
    {
        var lines = new string[30];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = "// filler " + i;
        lines[0] = "int total = 0;";
        foreach (int use in uses)
            lines[use] = "Print(count);";
        return lines;
    }

    private static string FileBefore(string[] after) => string.Join('\n', after).Replace("int total = 0;", "int count = 0;", StringComparison.Ordinal);

    private static IReadOnlyList<PredictionEvent> Events(string before, string after)
    {
        var history = new PredictionHistory();
        history.Note(PATH, before);
        history.Note(PATH, after);
        return history.Events(PATH);
    }

    private static string Apply(string text, IReadOnlyList<PredictionEdit> edits)
    {
        foreach (PredictionEdit edit in edits.OrderByDescending(each => each.Start))
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        return text;
    }

    // ---- where to ask

    [Fact]
    public void Candidates_are_the_lines_that_still_write_what_the_latest_event_removed()
    {
        string[] after = FileAfter(20);
        string text = string.Join('\n', after);

        IReadOnlyList<NextEditCandidate> found = NextEditLocator.Candidates(text, Events(FileBefore(after), text), 0, PATH);

        NextEditCandidate candidate = Assert.Single(found);
        Assert.Equal(20, candidate.Line);
        Assert.Equal(text.IndexOf("Print(count);", StringComparison.Ordinal), candidate.Start);
        Assert.Equal(candidate.Start + "Print(count);".Length, candidate.End);
        Assert.Contains("count", candidate.Reason);
    }

    [Fact]
    public void Candidates_leave_out_the_caret_line_and_the_lines_the_event_wrote()
    {
        string[] after = FileAfter(20);
        after[1] = "int other = count;";
        string text = string.Join('\n', after);
        IReadOnlyList<PredictionEvent> events = Events(FileBefore(after), text);

        // Line 1 is context of the event, not written by it, so it still counts; the caret's own line never does
        int caret = text.IndexOf("Print(count);", StringComparison.Ordinal) + 3;
        IReadOnlyList<NextEditCandidate> found = NextEditLocator.Candidates(text, events, caret, PATH);

        Assert.Equal([1], found.Select(each => each.Line));
        Assert.DoesNotContain(found, each => each.Line == 0);
    }

    [Fact]
    public void Candidates_include_the_callers_of_a_name_whose_parameters_changed()
    {
        var before = new List<string> { "void Save(string path)", "{", "}" };
        var after = new List<string> { "void Save(string path, bool force)", "{", "}" };
        for (int i = 0; i < 20; i++)
        {
            before.Add("// filler " + i);
            after.Add("// filler " + i);
        }
        before.Add("Save(p);");
        after.Add("Save(p);");
        string text = string.Join('\n', after);

        IReadOnlyList<NextEditCandidate> found = NextEditLocator.Candidates(text, Events(string.Join('\n', before), text), 0, PATH);

        NextEditCandidate candidate = Assert.Single(found);
        Assert.Equal(after.Count - 1, candidate.Line);
        Assert.Contains("Save", candidate.Reason);
    }

    [Fact]
    public void Candidates_are_the_nearest_three_to_the_caret()
    {
        string[] after = FileAfter(26, 10, 14, 18, 22);
        string text = string.Join('\n', after);

        IReadOnlyList<NextEditCandidate> found = NextEditLocator.Candidates(text, Events(FileBefore(after), text), 0, PATH);
        Assert.Equal([10, 14, 18], found.Select(each => each.Line));

        int caret = text.IndexOf("// filler 25", StringComparison.Ordinal);
        found = NextEditLocator.Candidates(text, Events(FileBefore(after), text), caret, PATH);
        Assert.Equal([26, 22, 18], found.Select(each => each.Line));
    }

    [Fact]
    public void An_event_that_removed_nothing_gives_no_candidates()
    {
        string[] after = FileAfter(20);
        string text = string.Join('\n', after);
        string before = text.Replace("int total = 0;", "", StringComparison.Ordinal);

        Assert.Empty(NextEditLocator.Candidates(text, Events(before, text), 0, PATH));
        Assert.Empty(NextEditLocator.Candidates(text, [], 0, PATH));
    }

    // ---- merging

    private static NextEdit Suggestion(NextEditSource source, int start, int end, string with = "x") =>
        new(PATH, start, end, 1, [new PredictionEdit(start, end, with)], source, "r", 0.5, new string('a', end - start));

    [Fact]
    public void A_model_edit_over_a_rename_suggestion_loses_to_it()
    {
        var queue = new NextEditQueue();
        queue.Add(Suggestion(NextEditSource.Rename, 10, 15));

        Assert.False(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 12, 20)));
        Assert.False(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 10, 15)));
        Assert.Equal(NextEditSource.Rename, Assert.Single(queue.All).Source);

        Assert.True(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 20, 25)));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void A_rename_suggestion_replaces_the_model_edits_it_overlaps()
    {
        var queue = new NextEditQueue();
        Assert.True(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 10, 20)));
        Assert.True(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 40, 45)));

        NextEditMerge.AddRename(queue, Suggestion(NextEditSource.Rename, 15, 18));

        Assert.Equal([NextEditSource.Rename, NextEditSource.Model], queue.All.Select(each => each.Source));
        Assert.Equal([15, 40], queue.All.Select(each => each.Start));
    }

    [Fact]
    public void A_newer_model_edit_replaces_an_older_one_it_overlaps()
    {
        var queue = new NextEditQueue();
        NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 10, 20, "old"));
        Assert.True(NextEditMerge.Add(queue, Suggestion(NextEditSource.Model, 15, 25, "new")));

        NextEdit left = Assert.Single(queue.All);
        Assert.Equal("new", left.Edits[0].Text);
    }

    [Fact]
    public void A_model_answer_becomes_one_suggestion_spanning_its_edits_and_none_when_it_changes_nothing()
    {
        string text = "abc def ghi";
        NextEdit made = NextEditMerge.FromModel(PATH, text, 3, [new PredictionEdit(0, 3, "x"), new PredictionEdit(8, 11, "y")], "why")!;

        Assert.Equal(NextEditSource.Model, made.Source);
        Assert.Equal((0, 11, 3), (made.Start, made.End, made.Version));
        Assert.Equal(text, made.Expected);
        Assert.Null(NextEditMerge.FromModel(PATH, text, 3, [], "why"));
        Assert.Null(NextEditMerge.FromModel(PATH, text, 3, [new PredictionEdit(0, 99, "x")], "why"));
    }

    [Fact]
    public void A_suggestion_made_for_an_older_text_is_rebased_or_dropped()
    {
        string made = "0123456789 count end";
        NextEdit edit = NextEditMerge.FromModel(PATH, made, 1, [new PredictionEdit(11, 16, "total")], "r")!;

        NextEdit moved = NextEditMerge.Rebased(edit, made, "// new\n" + made)!;
        Assert.Equal((18, 23), (moved.Start, moved.End));
        Assert.Equal(18, moved.Edits[0].Start);

        NextEdit same = NextEditMerge.Rebased(edit, made, made)!;
        Assert.Equal(edit, same);

        // Typed before it, after it: it stays; typed in it: it goes
        Assert.NotNull(NextEditMerge.Rebased(edit, made, made + "!"));
        Assert.Null(NextEditMerge.Rebased(edit, made, made.Replace("count", "cnt", StringComparison.Ordinal)));
    }

    // ---- the cache and the history hash

    [Fact]
    public void The_far_cache_misses_when_the_history_differs()
    {
        string first = PredictionHistory.Hash(Events("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nc\nX\ne\nf\ng\n"));
        string second = PredictionHistory.Hash(Events("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nc\nY\ne\nf\ng\n"));
        string again = PredictionHistory.Hash(Events("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nc\nX\ne\nf\ng\n"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, again);
        Assert.Equal(string.Empty, PredictionHistory.Hash([]));

        var cache = new PredictionCache();
        string key = PredictionCache.FarKey("region", "m", "chat", first);
        cache.Put(key, 100, [new PredictionEdit(104, 106, "zz")], null);

        Assert.True(cache.TryGet(PredictionCache.FarKey("region", "m", "chat", first), 500, out IReadOnlyList<PredictionEdit> hit, out _));
        Assert.Equal(new PredictionEdit(504, 506, "zz"), Assert.Single(hit));
        Assert.False(cache.TryGet(PredictionCache.FarKey("region", "m", "chat", second), 100, out _, out _));
        Assert.False(cache.TryGet(PredictionCache.FarKey("other", "m", "chat", first), 100, out _, out _));
        Assert.False(cache.TryGet(PredictionCache.FarKey("region", "m", "zeta2", first), 100, out _, out _));
    }

    // ---- llama.cpp

    private static async Task<JsonElement> Sent(OpenAiProvider provider, FakeServer server, PredictionWireRequest request)
    {
        await foreach (string _ in provider.Complete(request, default))
        {
        }
        var (_, body) = Assert.Single(server.Sent);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static FakeServer Answers() => FakeServer.Streaming(new PieceStream([
        "data: {\"choices\":[{\"text\":\"x\",\"delta\":{\"content\":\"x\"}}]}\n\n",
        "data: [DONE]\n\n",
    ]));

    [Fact]
    public async Task Llama_cpp_gets_cache_prompt_and_the_slot_only_when_one_is_set()
    {
        Uri url = PredictionProviders.OpenAiUrl("http://localhost:1/v1")!;
        var plain = new PredictionWireRequest("p", null, 10, [], "qwen");
        var chat = new PredictionWireRequest("p", null, 10, [], "chat", [new ChatTurn("user", "q")]);

        foreach (PredictionWireRequest request in new[] { plain, chat })
        {
            var server = Answers();
            JsonElement body = await Sent(new OpenAiProvider(new HttpClient(server), url, "m", null) { CachePrompt = true }, server, request);
            Assert.True(body.GetProperty("cache_prompt").GetBoolean());
            Assert.False(body.TryGetProperty("id_slot", out _));

            server = Answers();
            body = await Sent(new OpenAiProvider(new HttpClient(server), url, "m", null) { CachePrompt = true, Slot = 0 }, server, request);
            Assert.True(body.GetProperty("cache_prompt").GetBoolean());
            Assert.Equal(0, body.GetProperty("id_slot").GetInt32());

            // Another server may refuse a field it doesn't know
            server = Answers();
            body = await Sent(new OpenAiProvider(new HttpClient(server), url, "m", "key") { Slot = 0 }, server, request);
            Assert.False(body.TryGetProperty("cache_prompt", out _));
            Assert.False(body.TryGetProperty("id_slot", out _));
        }
    }

    [Fact]
    public void The_llama_cpp_provider_asks_for_cache_prompt_and_the_other_openai_servers_do_not()
    {
        var llama = PredictionProviders.Make(PredictionTestKit.Options() with { Provider = PredictionProviders.LLAMACPP, ApiUrl = "http://localhost:8080", Slot = 0 });
        var other = PredictionProviders.Make(PredictionTestKit.Options() with { Provider = PredictionProviders.OPENAI, ApiUrl = "http://localhost:8080" });

        OpenAiProvider asked = Assert.IsType<OpenAiProvider>(llama);
        Assert.True(asked.CachePrompt);
        Assert.Equal(0, asked.Slot);
        Assert.False(Assert.IsType<OpenAiProvider>(other).CachePrompt);
    }

    // ---- the engine

    private sealed class Rig
    {
        public readonly object Document = new();
        public readonly HandClock Clock = new();
        public readonly ConcurrentQueue<string> Prompts = new();
        public readonly PredictionEngine Engine;
        public ScriptedProvider Provider;
        public string[] Lines;
        public int Version = 1;
        public bool StallFar;
        private int inflight, mostInflight;

        public Rig(string[] after, string kind = PredictionModels.INSTRUCT, string format = "infer", Func<PredictionWireRequest, CancellationToken, IAsyncEnumerable<string>>? script = null)
        {
            Lines = after;
            Provider = new ScriptedProvider(script ?? Echo);
            Engine = new PredictionEngine(Clock)
            {
                ProviderOverride = Provider,
                Options = () => PredictionTestKit.Options(format) with { Kind = kind },
            };
            Engine.Snapshot = asked => new PredictionSnapshot(asked, Version, PATH, "csharp", Lines, 0, Lines[0].Length);
            Engine.History.Note(PATH, FileBefore(after));
        }

        public int MostInflight => Volatile.Read(ref mostInflight);

        public string Text => string.Join('\n', Lines);

        // An edit at a new version, as the glue says it
        public void Type() => Engine.Edited(Document, ++Version);

        // The debounce over and the tick that starts the near request, then the near request finished
        public async Task Near()
        {
            Type();
            Clock.Advance(250);
            Engine.Tick();
            await Engine.Running;
        }

        // The quiet a far question waits for, the tick that starts it, and the request finished
        public async Task Far(int quiet = 700)
        {
            Clock.Advance(quiet);
            Engine.Tick();
            await Engine.Running;
        }

        // A chat answer: the region again, with the old name made the new one wherever it's written (the far questions) and
        // nothing changed near the caret
        private async IAsyncEnumerable<string> Echo(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
        {
            int now = Interlocked.Increment(ref inflight);
            int most;
            while (now > (most = Volatile.Read(ref mostInflight)) && Interlocked.CompareExchange(ref mostInflight, now, most) != most)
            {
            }
            try
            {
                string user = request.Turns![1].Content;
                Prompts.Enqueue(user);
                int from = user.IndexOf("<region>\n", StringComparison.Ordinal) + "<region>\n".Length;
                string region = user[from..user.IndexOf("\n</region>", from, StringComparison.Ordinal)].Replace("<|cursor|>", "", StringComparison.Ordinal);
                bool far = region.Contains("count", StringComparison.Ordinal);
                await Task.Yield();
                if (far && StallFar)
                    await Task.Delay(Timeout.Infinite, cancel);
                yield return "<region>\n" + (far ? region.Replace("count", "total", StringComparison.Ordinal) : region) + "\n</region>";
            }
            finally
            {
                Interlocked.Decrement(ref inflight);
            }
        }
    }

    [Fact]
    public async Task A_far_question_is_asked_only_once_the_typing_is_quiet_and_its_answer_becomes_a_model_suggestion()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();

        // Near request done, the far one waits for the quiet
        Assert.Equal(1, rig.Engine.FarPending);
        rig.Clock.Advance(100);
        rig.Engine.Tick();
        await rig.Engine.Running;
        Assert.Equal(0, rig.Engine.FarRequests);
        Assert.Equal(1, rig.Provider.Asked);

        await rig.Far();
        Assert.Equal(1, rig.Engine.FarRequests);
        Assert.Equal(2, rig.Provider.Asked);
        Assert.Equal(0, rig.Engine.FarPending);

        // The question was about the candidate line: the caret marker is on it
        string far = rig.Prompts.Last();
        Assert.Contains("<|cursor|>Print(count);", far);

        Assert.True(rig.Engine.HasFar);
        NextEdit edit = Assert.Single(rig.Engine.TakeFar(rig.Document, rig.Version, rig.Text));
        Assert.Equal(NextEditSource.Model, edit.Source);
        Assert.Equal(rig.Version, edit.Version);
        Assert.Equal(rig.Text.Replace("Print(count);", "Print(total);", StringComparison.Ordinal), Apply(rig.Text, edit.Edits));
        Assert.False(rig.Engine.HasFar);
        Assert.Empty(rig.Engine.TakeFar(rig.Document, rig.Version, rig.Text));
        Assert.Equal(1, rig.MostInflight);
    }

    [Fact]
    public async Task No_far_question_is_asked_after_the_typing_was_left_for_seconds_or_in_an_unfocused_window()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        Assert.Equal(1, rig.Engine.FarPending);

        rig.Clock.Advance(9000);
        rig.Engine.Tick();
        Assert.Equal(0, rig.Engine.FarPending);
        Assert.Equal(0, rig.Engine.FarRequests);

        rig = new Rig(FileAfter(20));
        await rig.Near();
        rig.Engine.Active = () => false;
        rig.Clock.Advance(1000);
        rig.Engine.Tick();
        Assert.Equal(0, rig.Engine.FarPending);
        Assert.Equal(0, rig.Engine.FarRequests);
    }

    [Fact]
    public async Task At_most_three_far_questions_are_asked_one_at_a_time_nearest_first()
    {
        var rig = new Rig(FileAfter(10, 14, 18, 22, 26));
        await rig.Near();
        Assert.Equal(3, rig.Engine.FarPending);

        await rig.Far();
        await rig.Far(0);
        await rig.Far(0);
        await rig.Far(0);

        Assert.Equal(3, rig.Engine.FarRequests);
        Assert.Equal(4, rig.Provider.Asked);
        Assert.Equal(0, rig.Engine.FarPending);
        Assert.Equal(1, rig.MostInflight);

        string[] asked = [.. rig.Prompts.Skip(1)];
        Assert.Equal(3, asked.Length);
        Assert.Contains("<|cursor|>Print(count);", asked[0]);
        Assert.Equal([10, 14, 18], rig.Prompts.Skip(1).Select(each => LineOfCursor(each, rig.Lines)));
    }

    // The line of the file the caret marker is on, from the lines of the file the region is in
    private static int LineOfCursor(string prompt, string[] lines)
    {
        // The file's lines from its first (the files here are shorter than the lines shown above a region), the region's
        // opening tag a line of its own
        int cursor = prompt.IndexOf("<|cursor|>", StringComparison.Ordinal);
        int from = prompt.IndexOf("\n\n", prompt.IndexOf("File: ", StringComparison.Ordinal), StringComparison.Ordinal) + 2;
        return prompt[from..cursor].Count(c => c == '\n') - 1;
    }

    [Fact]
    public async Task A_far_question_never_runs_beside_a_near_one_or_an_instruction_edit()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        Assert.Equal(1, rig.Engine.FarPending);

        // An instruction edit comes first, then near requests, far last
        rig.Engine.InstructActive = true;
        rig.Clock.Advance(1000);
        rig.Engine.Tick();
        await rig.Engine.Running;
        Assert.Equal(0, rig.Engine.FarRequests);
        rig.Engine.InstructActive = false;

        // A near request wanted while a far one waits goes first: the far question is asked again after it
        rig.Engine.PredictNow(rig.Document);
        rig.Engine.Tick();
        await rig.Engine.Running;
        Assert.Equal(0, rig.Engine.FarRequests);
        // (the near answer came from the cache: the text is the same)
        Assert.Equal(1, rig.Provider.Asked);
        Assert.Equal(1, rig.Engine.FarPending);

        await rig.Far();
        Assert.Equal(1, rig.Engine.FarRequests);
        Assert.Equal(1, rig.MostInflight);
    }

    [Fact]
    public async Task A_far_question_waits_for_nothing_after_a_prediction_was_taken()
    {
        var rig = new Rig(FileAfter(20));
        rig.Type();
        rig.Engine.Accepted(rig.Document);
        rig.Engine.Tick();
        await rig.Engine.Running;
        Assert.Equal(1, rig.Engine.FarPending);

        rig.Engine.Tick();
        await rig.Engine.Running;
        Assert.Equal(1, rig.Engine.FarRequests);
    }

    [Fact]
    public async Task A_keystroke_cancels_a_far_question_and_the_ones_still_to_ask()
    {
        var rig = new Rig(FileAfter(10, 20)) { StallFar = true };
        await rig.Near();
        await rig.Engine.Running;
        Assert.Equal(2, rig.Engine.FarPending);

        rig.Clock.Advance(700);
        rig.Engine.Tick();
        Task far = rig.Engine.Running;
        Assert.Equal(1, rig.Engine.FarPending);

        rig.Type();
        await far;

        Assert.Equal(0, rig.Engine.FarPending);
        Assert.False(rig.Engine.HasFar);
        Assert.Equal(1, rig.Provider.Cancelled);
        Assert.Empty(rig.Engine.TakeFar(rig.Document, rig.Version, rig.Text));
    }

    [Fact]
    public async Task An_answer_that_changes_nothing_is_no_edit_here()
    {
        var rig = new Rig(FileAfter(20), script: Unchanged);
        await rig.Near();
        await rig.Far();

        Assert.Equal(1, rig.Engine.FarRequests);
        Assert.False(rig.Engine.HasFar);
        Assert.Empty(rig.Engine.TakeFar(rig.Document, rig.Version, rig.Text));
        Assert.Equal(PredictionStatus.Ok, rig.Engine.Status);

        static async IAsyncEnumerable<string> Unchanged(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
        {
            await Task.Yield();
            string user = request.Turns![1].Content;
            int from = user.IndexOf("<region>\n", StringComparison.Ordinal) + "<region>\n".Length;
            yield return "<region>\n" + user[from..user.IndexOf("\n</region>", from, StringComparison.Ordinal)].Replace("<|cursor|>", "", StringComparison.Ordinal) + "\n</region>";
        }
    }

    [Fact]
    public async Task The_same_far_question_with_the_same_history_is_answered_from_the_cache()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        await rig.Far();
        rig.Engine.TakeFar(rig.Document, rig.Version, rig.Text);
        Assert.Equal(1, rig.Engine.FarRequests);

        // Asked for again by hand: the near answer and the far one both come from the cache
        rig.Engine.PredictNow(rig.Document);
        rig.Engine.Tick();
        await rig.Engine.Running;
        await rig.Far();

        Assert.Equal(1, rig.Engine.FarRequests);
        Assert.Equal(2, rig.Provider.Asked);
        Assert.True(rig.Engine.HasFar);
    }

    [Fact]
    public async Task A_stale_far_result_is_rebased_when_its_span_is_untouched_and_dropped_when_it_is_not()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        await rig.Far();
        Assert.True(rig.Engine.HasFar);
        string made = rig.Text;

        // The user typed above it before it was taken
        rig.Type();
        string moved = "// header\n" + made;
        NextEdit edit = Assert.Single(rig.Engine.TakeFar(rig.Document, rig.Version, moved));
        Assert.Equal(moved.Replace("Print(count);", "Print(total);", StringComparison.Ordinal), Apply(moved, edit.Edits));
        Assert.Equal(rig.Version, edit.Version);

        // Typed in the span itself
        var again = new Rig(FileAfter(20));
        await again.Near();
        await again.Far();
        again.Type();
        Assert.Empty(again.Engine.TakeFar(again.Document, again.Version, made.Replace("Print(count);", "Print(cnt);", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_far_result_for_another_document_is_dropped()
    {
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        await rig.Far();

        Assert.Empty(rig.Engine.TakeFar(new object(), rig.Version, rig.Text));
        Assert.False(rig.Engine.HasFar);
    }

    [Fact]
    public async Task Models_that_cannot_chat_or_edit_ask_no_far_questions()
    {
        // A fill in the middle model: renames only
        var rig = new Rig(FileAfter(20), PredictionModels.FIM, "qwen", (_, cancel) => ScriptedProvider.Pieces(["x"], false, cancel));
        await rig.Near();

        Assert.Equal(0, rig.Engine.FarPending);
        await rig.Far();
        Assert.Equal(0, rig.Engine.FarRequests);
    }

    [Fact]
    public async Task An_edit_model_is_asked_with_a_zeta_region_at_the_candidate()
    {
        var prompts = new ConcurrentQueue<string>();
        var rig = new Rig(FileAfter(25), PredictionModels.EDIT_MODEL, "zeta21", (request, cancel) =>
        {
            prompts.Enqueue(request.Prompt);
            return ScriptedProvider.Pieces([""], false, cancel);
        });
        await rig.Near();
        Assert.Equal(1, rig.Engine.FarPending);
        await rig.Far();

        Assert.Equal(1, rig.Engine.FarRequests);
        Assert.Equal(2, prompts.Count);
        string far = prompts.Last();
        Assert.Contains(Zeta2Format.CURSOR + "Print(count);", far);
        Assert.DoesNotContain(Zeta2Format.CURSOR + "int total", far);
        Assert.Contains("int total = 0;" + Zeta2Format.CURSOR, prompts.First());
    }

    // ---- phase 7: the switches and the counters

    [Fact]
    public async Task With_far_predictions_off_no_far_question_is_scheduled_or_asked()
    {
        var rig = new Rig(FileAfter(20));
        rig.Engine.Options = () => PredictionTestKit.Options("infer") with { Kind = PredictionModels.INSTRUCT, Far = false };
        await rig.Near();
        await rig.Far();

        Assert.Equal(0, rig.Engine.FarPending);
        Assert.Equal(0, rig.Engine.FarRequests);
        Assert.Equal(1, rig.Provider.Asked);
        Assert.False(rig.Engine.HasFar);
    }

    [Fact]
    public async Task Far_requests_and_the_edits_they_find_are_counted()
    {
        PredictionCounters before = PredictionLog.Counters;
        var rig = new Rig(FileAfter(20));
        await rig.Near();
        await rig.Far();
        Assert.True(rig.Engine.HasFar);

        PredictionCounters after = PredictionLog.Counters;
        Assert.True(after.FarAsked >= before.FarAsked + 1);
        Assert.True(after.FarFound >= before.FarFound + 1);
    }

    [Fact]
    public void The_counters_count_what_is_shown_taken_and_let_go_of()
    {
        PredictionCounters before = PredictionLog.Counters;
        PredictionLog.Shown();
        PredictionLog.JumpShown();
        PredictionLog.JumpShown();
        PredictionLog.JumpAccepted();
        PredictionLog.JumpDismissed();

        PredictionCounters after = PredictionLog.Counters;
        Assert.True(after.Shown >= before.Shown + 1);
        Assert.True(after.JumpsShown >= before.JumpsShown + 2);
        Assert.True(after.JumpsAccepted >= before.JumpsAccepted + 1);
        Assert.True(after.JumpsDismissed >= before.JumpsDismissed + 1);
        Assert.Contains("far asked", new PredictionCounters(1, 2, 3, 4, 5, 6).ToString());
    }

    [Fact]
    public void With_renames_off_the_tracker_notes_none_and_offers_no_rename_places()
    {
        string before = "int count = 0;\nPrint(count);";
        string after = "int total = 0;\nPrint(count);";

        var on = new RenameTracker();
        on.Observe(PATH, before, after);
        Assert.Single(on.Active(PATH));

        var off = new RenameTracker { Enabled = false };
        off.Observe(PATH, before, after);
        Assert.Empty(off.Active(PATH));
        Assert.Null(off.Hint(PATH, after));

        // Switched off after one was followed: it is let go of, and switching on again doesn't bring it back
        on.Enabled = false;
        Assert.Empty(on.Active(PATH));
        on.Enabled = true;
        Assert.Empty(on.Active(PATH));
    }

    [Fact]
    public void The_two_switches_are_on_as_they_come()
    {
        var settings = new HexSettings(Path.Combine(Path.GetTempPath(), "hex-nonexistent-" + Guid.NewGuid().ToString("N"), "settings.hor"));
        Assert.True(settings.EditPredictionsRenames);
        Assert.True(settings.EditPredictionsFar);
        Assert.True(new PredictionOptions(true, "ollama", "http://x", null, "m", "infer", 256, 100).Far);
    }

    [Fact]
    public async Task A_stopped_engine_forgets_its_far_questions_and_answers()
    {
        var rig = new Rig(FileAfter(10, 20));
        await rig.Near();
        await rig.Far();
        Assert.True(rig.Engine.HasFar);

        rig.Engine.ShutDown();

        Assert.False(rig.Engine.HasFar);
        Assert.Equal(0, rig.Engine.FarPending);
    }
}
