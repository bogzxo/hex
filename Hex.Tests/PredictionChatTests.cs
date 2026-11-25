using System.Net;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Phase 4 of docs/next-edit-plan.md: the chat edit format (prompt and answer), the history comment and the rename-aware
/// word in fill in the middle prompts, and the providers' streamed chat requests.
/// </summary>
public sealed class PredictionChatTests
{
    private const string SIX = "int a = 1;\nint b = 2;\nint c = 3;\nint d = 4;\nint e = 5;\nint f = 6;";

    private static PredictionInput Six(int caret = 0) => new("Foo.cs", "csharp", SIX, caret, [], []);

    private static string Apply(string text, IReadOnlyList<PredictionEdit> edits)
    {
        foreach (PredictionEdit edit in edits.OrderByDescending(each => each.Start))
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        return text;
    }

    private static PredictionParsed Parse(string answer, bool complete = true, PredictionInput? input = null)
    {
        PredictionPrompt prompt = ChatEditFormat.Near.Build(input ?? Six(), 256)!;
        return ChatEditFormat.Near.Parse(prompt, answer, complete);
    }

    private static string Region(params string[] lines) => "<region>\n" + string.Join('\n', lines) + "\n</region>";

    // ---- prompts

    [Fact]
    public void The_chat_prompt_is_a_fixed_system_message_and_one_user_message_in_order()
    {
        var events = new[] { new PredictionEvent("Foo.cs", "@@ -1,1 +1,1 @@\n-x\n+a\n", false) };
        var input = new PredictionInput("Foo.cs", "csharp", "a\nb\nc", 3, events, []);

        PredictionPrompt prompt = ChatEditFormat.Near.Build(input, 256)!;

        Assert.Equal(["system", "user"], prompt.Request.Turns!.Select(turn => turn.Role));
        Assert.Equal(ChatEditFormat.SYSTEM, prompt.Request.Turns![0].Content);
        Assert.Equal(
            "Recent edits (oldest first):\n(typed)\n--- a/Foo.cs\n+++ b/Foo.cs\n@@ -1,1 +1,1 @@\n-x\n+a\n\n"
            + "File: Foo.cs (csharp)\n\n<region>\na\nb<|cursor|>\nc\n</region>\n",
            prompt.Request.Turns![1].Content);
        Assert.Equal("chat", prompt.Request.Format);
        Assert.Empty(prompt.Request.Stop);
        Assert.Equal(0, prompt.RegionStart);
        Assert.Equal(5, prompt.RegionEnd);
        Assert.Equal("a\nb\nc", prompt.Region);
    }

    [Fact]
    public void The_chat_prompt_holds_related_files_edits_rename_and_the_lines_round_the_region()
    {
        string text = string.Join('\n', Enumerable.Range(0, 100).Select(line => "line " + line));
        int caret = text.IndexOf("line 50", StringComparison.Ordinal);
        var events = new[]
        {
            new PredictionEvent("Foo.cs", "@@ -1,1 +1,1 @@\n-x\n+y\n", true),
            new PredictionEvent("Foo.cs", "@@ -5,1 +5,1 @@\n-p\n+q\n", false),
        };
        var input = new PredictionInput("Foo.cs", "csharp", text, caret, events, [new PredictionExcerpt("Bar.cs", "class Bar {}")],
            Rename: new PredictionRenameHint("count", "total", 4));

        string user = ChatEditFormat.Near.Build(input, 256)!.Request.Turns![1].Content;

        int related = user.IndexOf("Related files:\n### Bar.cs\nclass Bar {}\n", StringComparison.Ordinal);
        int edits = user.IndexOf("Recent edits (oldest first):\n(accepted suggestion)", StringComparison.Ordinal);
        int typed = user.IndexOf("(typed)", StringComparison.Ordinal);
        int rename = user.IndexOf("Renamed count → total in Foo.cs; 4 occurrences remain.", StringComparison.Ordinal);
        int file = user.IndexOf("File: Foo.cs (csharp)", StringComparison.Ordinal);
        int region = user.IndexOf("<region>", StringComparison.Ordinal);
        Assert.True(0 <= related && related < edits && edits < typed && typed < rename && rename < file && file < region, user);

        // The region is lines 48 to 55 (it starts two lines above the caret's); thirty lines above it, fifteen below
        Assert.Contains("line 18\nline 19\n", user);
        Assert.DoesNotContain("line 17\n", user);
        Assert.Contains("</region>\nline 56\n", user);
        Assert.EndsWith("line 70\n", user);
    }

    [Fact]
    public void A_file_with_the_format_s_own_markers_is_not_asked()
    {
        Assert.Null(ChatEditFormat.Near.Build(new PredictionInput("a.cs", "csharp", "x <region> y", 0, [], []), 256));
        Assert.Null(ChatEditFormat.Near.Build(new PredictionInput("a.cs", "csharp", "x <|cursor|> y", 0, [], []), 256));
    }

    [Fact]
    public void An_instruct_model_gets_the_chat_format_near_the_caret_unless_it_is_reached_over_link()
    {
        PredictionOptions options = PredictionTestKit.Options("infer") with { Model = "some-model", Kind = "instruct" };
        Assert.Equal("chat", PredictionStrategy.For(options).Near.Name);
        Assert.Equal("qwen", PredictionStrategy.For(options with { Kind = "fim_instruct" }).Near.Name);
        Assert.Equal("qwen", PredictionStrategy.For(options with { Provider = "link" }).Near.Name);
        Assert.Equal("codestral", PredictionStrategy.For(options with { Format = "codestral" }).Near.Name);
    }

    [Fact]
    public void A_fill_in_the_middle_prompt_has_the_recent_edits_and_the_rename_as_comments()
    {
        var events = new[]
        {
            new PredictionEvent("Foo.cs", "@@ -1,1 +1,1 @@\n-int count = 0;\n+int total = 0;\n", false),
        };
        var input = new PredictionInput("Foo.cs", "csharp", "int total = 0;\n", 15, events, [], Rename: new PredictionRenameHint("count", "total", 4));

        PredictionPrompt prompt = FimFormat.Qwen.Build(input, 64)!;

        Assert.Equal(
            "<|fim_prefix|>// Recent edits (oldest first):\n// Foo.cs @@ -1,1 +1,1 @@\n// -int count = 0;\n// +int total = 0;\n"
            + "// Renamed count → total in Foo.cs; 4 occurrences remain.\n// Foo.cs\nint total = 0;\n<|fim_suffix|><|fim_middle|>",
            prompt.Request.Prompt);
    }

    [Fact]
    public void An_accepted_suggestion_is_marked_in_the_history_comment_and_a_language_without_comments_has_none()
    {
        var events = new[] { new PredictionEvent("a.py", "@@ -1,1 +1,1 @@\n-x\n+y\n", true) };
        string python = FimFormat.Qwen.Build(new PredictionInput("a.py", "python", "y\n", 2, events, []), 64)!.Request.Prompt;
        Assert.StartsWith("<|fim_prefix|># Recent edits (oldest first):\n# (accepted suggestion)\n# a.py @@ -1,1 +1,1 @@\n# -x\n# +y\n# a.py\n", python);

        string plain = FimFormat.Qwen.Build(new PredictionInput("a.txt", "plaintext", "y\n", 2, events, []), 64)!.Request.Prompt;
        Assert.Equal("<|fim_prefix|>y\n<|fim_suffix|><|fim_middle|>", plain);
    }

    [Fact]
    public void The_history_comment_keeps_the_newest_edits_within_its_budget()
    {
        string big = string.Concat(Enumerable.Range(0, 40).Select(line => "+" + new string('x', 60) + line + "\n"));
        PredictionEvent[] events = [.. Enumerable.Range(0, 10).Select(n => new PredictionEvent("Foo.cs", $"@@ -{n},1 +{n},40 @@\n" + big, false))];
        string comments = PredictionEventText.Comments(events, null, "Foo.cs", "//", PredictionEventText.FIM_TOKENS);

        Assert.True(PredictionTokens.Of(comments) <= PredictionEventText.FIM_TOKENS, comments.Length.ToString());
        Assert.Contains("@@ -9,1 +9,40 @@", comments);
        Assert.DoesNotContain("@@ -0,1 +0,40 @@", comments);
    }

    [Fact]
    public void A_caret_inside_the_renamed_word_is_asked_with_the_word_taken_out_and_the_new_name_is_the_answer()
    {
        const string text = "int count = 0;\nreturn count;";
        var input = new PredictionInput("Foo.cs", "csharp", text, 24, [], [], Rename: new PredictionRenameHint("count", "total", 1));

        PredictionPrompt prompt = FimFormat.Qwen.Build(input, 64)!;

        Assert.EndsWith("return <|fim_suffix|>;<|fim_middle|>", prompt.Request.Prompt);
        Assert.Equal((22, 27, "count"), (prompt.RegionStart, prompt.RegionEnd, prompt.Region));

        PredictionParsed parsed = FimFormat.Qwen.Parse(prompt, "total", true);
        PredictionEdit edit = Assert.Single(parsed.Edits);
        Assert.Equal(new PredictionEdit(22, 27, "total"), edit);
        Assert.Equal("int count = 0;\nreturn total;", Apply(text, parsed.Edits));

        // Another word, or no end to the answer's first word yet, is not an edit
        Assert.Empty(FimFormat.Qwen.Parse(prompt, "totally", true).Edits);
        Assert.False(FimFormat.Qwen.Parse(prompt, "tot", false).Done);
        Assert.True(FimFormat.Qwen.Parse(prompt, "total;", false).Done);
    }

    [Fact]
    public void A_caret_inside_any_other_word_is_still_not_asked()
    {
        const string text = "int count = 0;\nreturn count;";
        Assert.Null(FimFormat.Qwen.Build(new PredictionInput("Foo.cs", "csharp", text, 24, [], []), 64));
        var other = new PredictionInput("Foo.cs", "csharp", text, 7, [], [], Rename: new PredictionRenameHint("size", "total", 1));
        Assert.Null(FimFormat.Qwen.Build(other, 64));
    }

    // ---- answers

    [Fact]
    public void An_answer_in_the_region_tags_is_diffed_against_the_old_region()
    {
        PredictionParsed parsed = Parse(Region("int a = 1;", "int b = 2;", "int c = 3;", "int d = 4;", "int e = 5;", "int f = 60;"));

        Assert.True(parsed.Done);
        Assert.Equal(SIX.Replace("f = 6;", "f = 60;"), Apply(SIX, parsed.Edits));
    }

    [Fact]
    public void Fences_inside_the_region_are_taken_off()
    {
        string answer = "<region>\n```csharp\nint a = 1;\nint b = 2;\nint c = 3;\nint d = 4;\nint e = 5;\nint f = 60;\n```\n</region>";
        Assert.Equal(SIX.Replace("f = 6;", "f = 60;"), Apply(SIX, Parse(answer).Edits));
    }

    [Fact]
    public void Text_round_the_region_and_a_think_block_are_ignored()
    {
        string answer = "<think>I will write <region> around it</think>\nSure!\n"
            + Region("int a = 1;", "int b = 2;", "int c = 3;", "int d = 4;", "int e = 5;", "int f = 60;") + "\nHope that helps.";
        Assert.Equal(SIX.Replace("f = 6;", "f = 60;"), Apply(SIX, Parse(answer).Edits));
    }

    [Fact]
    public void An_answer_with_no_closing_tag_was_cut_off_and_is_no_edit()
    {
        string cut = "<region>\nint a = 1;\nint b = 20;\nint c = 3;";

        PredictionParsed streaming = Parse(cut, complete: false);
        Assert.False(streaming.Done);

        PredictionParsed ended = Parse(cut, complete: true);
        Assert.True(ended.Done);
        Assert.Empty(ended.Edits);
        Assert.Empty(Parse("I think nothing changes.", complete: true).Edits);
    }

    [Fact]
    public void An_answer_settles_as_soon_as_the_region_is_closed()
    {
        PredictionParsed parsed = Parse(Region("int a = 10;", "int b = 2;", "int c = 3;", "int d = 4;", "int e = 5;", "int f = 6;") + "\nand then more", complete: false);
        Assert.True(parsed.Done);
        Assert.Single(parsed.Edits);
    }

    [Fact]
    public void An_answer_that_rewrites_most_of_the_region_is_rejected_and_says_so()
    {
        PredictionParsed rewrote = Parse(Region("int a = 1;", "int b = 2;", "int w = 3;", "int x = 4;", "int y = 5;", "int z = 6;"));
        Assert.True(rewrote.Done);
        Assert.Empty(rewrote.Edits);
        Assert.NotNull(rewrote.Note);

        // Half of the lines is an edit
        PredictionParsed half = Parse(Region("int a = 1;", "int b = 2;", "int c = 3;", "int x = 4;", "int y = 5;", "int z = 6;"));
        Assert.Equal(3, Assert.Single(half.Edits.Select(edit => edit.Text.Count(c => c == 'x' || c == 'y' || c == 'z'))) );
        Assert.Null(half.Note);

        // Lines added are not lines changed
        PredictionParsed added = Parse(Region("int a = 1;", "int b = 2;", "int c = 3;", "int d = 4;", "int e = 5;", "int f = 6;", "int g = 7;", "int h = 8;", "int i = 9;"));
        Assert.Equal(SIX + "\nint g = 7;\nint h = 8;\nint i = 9;", Apply(SIX, added.Edits));
    }

    [Fact]
    public void An_unchanged_region_is_no_prediction_and_the_cursor_marker_never_reaches_the_text()
    {
        Assert.Empty(Parse(Region(SIX.Split('\n'))).Edits);

        string[] lines = SIX.Split('\n');
        lines[0] = "int a<|cursor|> = 1;";
        Assert.Empty(Parse(Region(lines)).Edits);

        // A line that is only the marker is dropped
        string[] spurious = [.. lines[..1].Select(_ => "int a = 1;"), "<|cursor|>", .. SIX.Split('\n')[1..]];
        Assert.Empty(Parse(Region(spurious)).Edits);

        lines[0] = "int a = 1;";
        lines[5] = "int f = 60;<|cursor|>";
        PredictionParsed parsed = Parse(Region(lines));
        Assert.DoesNotContain("<|cursor|>", Apply(SIX, parsed.Edits));
        Assert.Equal(SIX.IndexOf("int f = 6;", StringComparison.Ordinal) + "int f = 60;".Length, parsed.CaretAfter);
    }

    [Fact]
    public void A_blank_line_the_caret_is_on_survives_the_marker()
    {
        const string text = "if (a)\n{\n    \n}\n";
        var input = new PredictionInput("Foo.cs", "csharp", text, text.IndexOf("    \n", StringComparison.Ordinal) + 4, [], []);
        PredictionPrompt prompt = ChatEditFormat.Near.Build(input, 256)!;

        PredictionParsed parsed = ChatEditFormat.Near.Parse(prompt, "<region>\nif (a)\n{\n    <|cursor|>\n}\n\n</region>", true);
        Assert.Empty(parsed.Edits);
    }

    // ---- the providers' chat requests

    private static PredictionWireRequest Chat() =>
        new("prompt", null, 77, [], "chat", [new ChatTurn("system", "Be exact."), new ChatTurn("user", "Region?")]);

    private static async Task<string> Read(IPredictionProvider provider, PredictionWireRequest request)
    {
        var text = new System.Text.StringBuilder();
        await foreach (string piece in provider.Complete(request, default))
            text.Append(piece);
        return text.ToString();
    }

    [Fact]
    public async Task OpenAI_chat_is_streamed_with_thinking_off_and_what_the_model_thinks_is_not_the_answer()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>x</think><reg\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"ion>\\nint a;\\n</region>\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n",
        ]));
        var provider = new OpenAiProvider(new HttpClient(server), PredictionProviders.OpenAiUrl("http://localhost:1/v1")!, "m", "secret");

        Assert.Equal("<region>\nint a;\n</region>", await Read(provider, Chat()));

        var (sent, body) = Assert.Single(server.Sent);
        Assert.Equal("/v1/chat/completions", sent.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(77, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray().Select(each => each.GetProperty("role").GetString()));
        Assert.False(root.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task Ollama_chat_is_streamed_from_api_chat_with_think_false()
    {
        var server = new FakeServer(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/show", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"capabilities\":[\"completion\",\"thinking\"]}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PieceStream([
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"hm\"},\"done\":false}\n",
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"<region>\\nint \"},\"done\":false}\n",
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"a;\\n</region>\"},\"done\":false}\n",
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n",
                ])),
            });
        });
        var provider = new OllamaProvider(new HttpClient(server), PredictionProviders.OllamaUrl("http://localhost:11434")!, "qwen3:" + Guid.NewGuid());

        Assert.Equal("<region>\nint a;\n</region>", await Read(provider, Chat()));

        var (sent, body) = server.Sent.Single(each => each.Request.RequestUri!.AbsolutePath == "/api/chat");
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal(77, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray().Select(each => each.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Cancelling_a_chat_stream_closes_the_connection()
    {
        var body = new PieceStream(["data: {\"choices\":[{\"delta\":{\"content\":\"<region>\"}}]}\n\n"], stall: true);
        var provider = new OpenAiProvider(new HttpClient(FakeServer.Streaming(body)), PredictionProviders.OpenAiUrl("http://localhost:1/v1")!, "m", null);
        using var cancel = new CancellationTokenSource();

        await using IAsyncEnumerator<string> pieces = provider.Complete(Chat(), cancel.Token).GetAsyncEnumerator(cancel.Token);
        Assert.True(await pieces.MoveNextAsync());
        Assert.Equal("<region>", pieces.Current);

        Task<bool> next = pieces.MoveNextAsync().AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Leaving_a_chat_stream_early_closes_the_connection()
    {
        var body = new PieceStream([
            "data: {\"choices\":[{\"delta\":{\"content\":\"<region>\\nint a;\\n</region>\"}}]}\n\n",
        ], stall: true);
        var provider = new OpenAiProvider(new HttpClient(FakeServer.Streaming(body)), PredictionProviders.OpenAiUrl("http://localhost:1/v1")!, "m", null);

        await foreach (string piece in provider.Complete(Chat(), default))
        {
            Assert.Contains("</region>", piece);
            break;
        }
        Assert.True(body.Disposed);
    }
}
