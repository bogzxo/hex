using Hex.Core;

namespace Hex.Tests;

/// <summary>The prompts each model family is sent, the budgets they're kept to, and what their answers come to.</summary>
public sealed class PredictionFormatTests
{
    private static readonly Zeta2Format zeta = new();

    [Fact]
    public void Zeta_2s_prompt_for_the_model_cards_example_is_the_cards_to_the_byte()
    {
        var (input, regionStart, regionEnd, expected) = PredictionTestKit.ZetaSample();

        PredictionPrompt? prompt = zeta.Build(input, 256, regionStart, regionEnd, 0, input.Text.Length);

        Assert.NotNull(prompt);
        Assert.Equal(2, input.Events.Count);
        Assert.Equal(expected, prompt.Request.Prompt);
        Assert.Equal("zeta2", prompt.Request.Format);
        Assert.Empty(prompt.Request.Stop);
        Assert.Equal(input.Text[regionStart..regionEnd], prompt.Region);
    }

    [Fact]
    public void The_model_cards_answer_is_the_one_insertion_at_the_caret_it_is()
    {
        var (input, regionStart, regionEnd, _) = PredictionTestKit.ZetaSample();
        PredictionPrompt prompt = zeta.Build(input, 256, regionStart, regionEnd, 0, input.Text.Length)!;

        PredictionParsed parsed = zeta.Parse(prompt, PredictionTestKit.Golden("zeta2.output"), true);

        Assert.True(parsed.Done);
        PredictionEdit edit = Assert.Single(parsed.Edits);
        Assert.Equal(input.Caret, edit.Start);
        Assert.Equal(input.Caret, edit.End);
        Assert.Equal("uct LanguageEntry {\n    path: PathBuf,\n    language: OnceCell<Language>,\n    external_files: Option<Vec<PathBuf>>,\n}\n", edit.Text);
        Assert.True(PredictionDiff.IsGhost(parsed.Edits, input.Text, input.Caret));
    }

    [Fact]
    public void The_simple_rule_puts_whole_lines_round_the_caret_in_the_region_and_context_round_that()
    {
        var (input, _, _, _) = PredictionTestKit.ZetaSample();

        var (regionStart, regionEnd, contextStart, contextEnd) = Zeta2Format.Ranges(input.Text, input.Caret);

        Assert.True(contextStart <= regionStart && regionStart <= input.Caret && input.Caret <= regionEnd && regionEnd <= contextEnd);
        Assert.True(regionStart == 0 || input.Text[regionStart - 1] == '\n');
        Assert.True(regionEnd == input.Text.Length || input.Text[regionEnd] == '\n');
        Assert.InRange(PredictionTokens.Of(input.Text[regionStart..regionEnd]), 1, 350);
        Assert.NotNull(zeta.Build(input, 256));
    }

    [Fact]
    public void A_large_file_has_an_editable_region_of_about_350_tokens_and_150_of_context()
    {
        string text = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"    int value{i} = {i};"));
        int caret = text.IndexOf("value200", StringComparison.Ordinal);

        var (regionStart, regionEnd, contextStart, contextEnd) = Zeta2Format.Ranges(text, caret);

        Assert.InRange(PredictionTokens.Of(text[regionStart..regionEnd]), 300, 350);
        Assert.InRange(PredictionTokens.Of(text[contextStart..regionStart]) + PredictionTokens.Of(text[regionEnd..contextEnd]), 100, 151);
    }

    [Fact]
    public void Edit_history_is_dropped_oldest_first_and_related_files_before_it()
    {
        string text = "class A\n{\n    void Run()\n    {\n        \n    }\n}\n";
        int caret = text.IndexOf("        \n", StringComparison.Ordinal) + 8;
        string Big(int n) => "@@ -1,1 +1,1 @@\n" + string.Concat(Enumerable.Repeat($"+event{n} filler line of text that is long enough\n", 40));
        var events = Enumerable.Range(0, 6).Select(n => new PredictionEvent("src/a.cs", Big(n), false)).ToList();
        var related = new List<PredictionExcerpt> { new("src/b.cs", "RELATED\n" + new string('x', 3000)) };

        string prompt = zeta.Build(new PredictionInput("src/a.cs", "csharp", text, caret, events, related), 256)!.Request.Prompt;

        Assert.DoesNotContain("RELATED", prompt);
        Assert.DoesNotContain("+event0 ", prompt);
        Assert.Contains("+event5 ", prompt);
        Assert.InRange(PredictionTokens.Of(prompt), 1, 4096);

        string small = zeta.Build(new PredictionInput("src/a.cs", "csharp", text, caret, events.Take(1).ToList(), related), 256)!.Request.Prompt;
        Assert.Contains("<filename>src/b.cs\nRELATED", small);
        Assert.True(small.IndexOf("RELATED", StringComparison.Ordinal) < small.IndexOf("<filename>edit_history", StringComparison.Ordinal));
    }

    [Fact]
    public void At_most_six_events_are_sent_and_an_accepted_prediction_is_marked()
    {
        var events = Enumerable.Range(0, 8).Select(n => new PredictionEvent("a.cs", $"@@ -1,1 +1,1 @@\n-x{n}\n+y{n}\n", n == 7)).ToList();

        string prompt = zeta.Build(new PredictionInput("a.cs", "csharp", "int x;\n", 0, events, []), 256)!.Request.Prompt;

        Assert.DoesNotContain("+y1\n", prompt);
        Assert.Contains("+y2\n", prompt);
        Assert.Contains("// User accepted prediction:\n--- a/a.cs\n+++ b/a.cs\n@@ -1,1 +1,1 @@\n-x7\n+y7\n", prompt);
    }

    [Theory]
    [InlineData("a\n=======\nb\n")]
    [InlineData("<<<<<<< CURRENT\nb\n")]
    [InlineData("a\n>>>>>>> UPDATED")]
    [InlineData("string s = \"<|user_cursor|>\";\n")]
    [InlineData("// <[fim-middle]>\n")]
    public void A_file_holding_a_marker_is_not_predicted_for(string text)
    {
        Assert.Null(zeta.Build(new PredictionInput("a.cs", "csharp", text, 0, [], []), 256));
    }

    [Fact]
    public void A_marker_inside_a_line_is_no_marker()
    {
        Assert.NotNull(zeta.Build(new PredictionInput("a.cs", "csharp", "// a ======= b\n", 0, [], []), 256));
    }

    [Theory]
    [InlineData("qwen", "<|fim_prefix|>// a.cs\nint x = <|fim_suffix|>\nint y;<|fim_middle|>")]
    [InlineData("code_gemma", "<|fim_prefix|>// a.cs\nint x = <|fim_suffix|>\nint y;<|fim_middle|>")]
    [InlineData("star_coder", "<fim_prefix>// a.cs\nint x = <fim_suffix>\nint y;<fim_middle>")]
    [InlineData("code_llama", "<PRE> // a.cs\nint x =  <SUF>\nint y; <MID>")]
    [InlineData("deepseek_coder", "<｜fim▁begin｜>// a.cs\nint x = <｜fim▁hole｜>\nint y;<｜fim▁end｜>")]
    [InlineData("codestral", "[SUFFIX]\nint y;[PREFIX]// a.cs\nint x = ")]
    public void Every_fill_in_the_middle_family_has_its_own_tokens(string name, string expected)
    {
        IPredictionFormat format = PredictionFormats.Named(name)!;

        PredictionPrompt prompt = format.Build(new PredictionInput("a.cs", "csharp", "int x = \nint y;", 8, [], []), 256)!;

        Assert.Equal(name, format.Name);
        Assert.Equal(expected, prompt.Request.Prompt);
        Assert.DoesNotContain("\n", prompt.Request.Stop);
        Assert.Equal(256, prompt.Request.MaxTokens);
    }

    [Fact]
    public void In_the_middle_of_a_line_the_answer_stops_at_a_newline()
    {
        PredictionPrompt prompt = FimFormat.Qwen.Build(new PredictionInput("a.cs", "csharp", "Run();\n", 4, [], []), 256)!;

        Assert.Equal("\n", prompt.Request.Stop[0]);
        Assert.True(prompt.Request.MaxTokens <= 64);
        PredictionParsed parsed = FimFormat.Qwen.Parse(prompt, "x, y\nmore", false);
        Assert.True(parsed.Done);
        Assert.Equal(new PredictionEdit(4, 4, "x, y"), Assert.Single(parsed.Edits));
    }

    [Fact]
    public void Nothing_is_asked_in_the_middle_of_a_word()
    {
        Assert.Null(FimFormat.Qwen.Build(new PredictionInput("a.cs", "csharp", "Console", 3, [], []), 256));
    }

    [Theory]
    [InlineData("zed-industries/zeta-2", "zeta2")]
    [InlineData("hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M", "zeta2")]
    [InlineData("qwen2.5-coder:14b-base-q4_K_M", "qwen")]
    [InlineData("codegemma:7b-code", "code_gemma")]
    [InlineData("codellama:7b-code", "code_llama")]
    [InlineData("starcoder2:3b", "star_coder")]
    [InlineData("deepseek-coder:6.7b-base", "deepseek_coder")]
    [InlineData("codestral-latest", "codestral")]
    [InlineData("some-model", "qwen")]
    public void The_format_is_inferred_from_the_models_name(string model, string format)
    {
        Assert.Equal(format, PredictionFormats.Infer(model).Name);
        Assert.Equal(format, PredictionFormats.For(PredictionTestKit.Options("infer") with { Model = model }).Name);
    }

    [Fact]
    public void The_format_named_wins_over_the_models_name_and_infer_names_none()
    {
        Assert.Null(PredictionFormats.Named("infer"));
        Assert.Equal("zeta2", PredictionFormats.For(PredictionTestKit.Options("zeta2") with { Model = "qwen" }).Name);
        Assert.Equal(8, PredictionFormats.Names.Count);
    }

    // An answer for a small file whose whole text is the region
    private static (PredictionPrompt Prompt, string Text) Small(string text, int caret)
    {
        var input = new PredictionInput("a.cs", "csharp", text, caret, [], []);
        return (zeta.Build(input, 256, 0, text.Length, 0, text.Length)!, text);
    }

    [Fact]
    public void The_cursor_marker_is_taken_out_and_remembered()
    {
        var (prompt, _) = Small("int a;\nint b;", 7);

        PredictionParsed parsed = zeta.Parse(prompt, "int a;\nint bc;<|user_cursor|>\n>>>>>>> UPDATED\n", false);

        Assert.True(parsed.Done);
        Assert.Equal(new PredictionEdit(12, 12, "c"), Assert.Single(parsed.Edits));
        Assert.Equal(14, parsed.CaretAfter);
    }

    [Fact]
    public void An_answer_cut_short_drops_its_unfinished_line_and_keeps_the_rest_of_the_region()
    {
        var (prompt, _) = Small("one\ntwo\nthree\nfour\nfive", 0);

        PredictionParsed parsed = zeta.Parse(prompt, "one\nTWO\nthr", true);

        Assert.Equal(new PredictionEdit(4, 7, "TWO"), Assert.Single(parsed.Edits));
    }

    [Fact]
    public void An_answer_that_changes_nothing_or_only_spaces_at_line_ends_is_nothing()
    {
        var (prompt, _) = Small("int a;\nint b;", 0);

        Assert.Empty(zeta.Parse(prompt, "int a;\nint b;\n>>>>>>> UPDATED\n", true).Edits);
        Assert.Empty(zeta.Parse(prompt, "int a;   \nint b;\n>>>>>>> UPDATED\n", true).Edits);
        Assert.True(zeta.Parse(prompt, "int a;\nint b;\n>>>>>>> UPDATED\n", true).Done);
    }

    [Fact]
    public void An_answer_with_crlf_line_ends_reads_as_one_with_newlines()
    {
        var (prompt, _) = Small("int a;\nint b;", 0);

        PredictionParsed parsed = zeta.Parse(prompt, "int a;\r\nint c;\r\n>>>>>>> UPDATED\r\n", true);

        Assert.Equal(new PredictionEdit(11, 12, "c"), Assert.Single(parsed.Edits));
    }

    [Fact]
    public void A_change_then_four_lines_the_region_has_settles_the_answer_while_it_streams()
    {
        string text = string.Join('\n', Enumerable.Range(0, 20).Select(i => "line" + i));
        var (prompt, _) = Small(text, 0);

        Assert.False(zeta.Parse(prompt, "line0\nLINE1\nline2\nline3\nline4\n", false).Done);
        PredictionParsed parsed = zeta.Parse(prompt, "line0\nLINE1\nline2\nline3\nline4\nline5\n", false);

        Assert.True(parsed.Done);
        Assert.Equal(new PredictionEdit(6, 10, "LINE"), Assert.Single(parsed.Edits));
    }

    [Fact]
    public void Four_unchanged_lines_past_the_caret_settle_the_answer_as_nothing()
    {
        string text = string.Join('\n', Enumerable.Range(0, 20).Select(i => "line" + i));
        var (prompt, _) = Small(text, 1);

        Assert.False(zeta.Parse(prompt, "line0\nline1\nline2\nline3\n", false).Done);
        PredictionParsed parsed = zeta.Parse(prompt, "line0\nline1\nline2\nline3\nline4\n", false);

        Assert.True(parsed.Done);
        Assert.Empty(parsed.Edits);
    }

    [Fact]
    public void What_an_answer_repeats_of_the_text_after_the_caret_is_trimmed()
    {
        PredictionPrompt prompt = FimFormat.Qwen.Build(new PredictionInput("a.cs", "csharp", "Run();\n", 4, [], []), 256)!;

        Assert.Equal("x", Assert.Single(FimFormat.Qwen.Parse(prompt, "x);", true).Edits).Text);
        Assert.Empty(FimFormat.Qwen.Parse(prompt, ");", true).Edits);
    }

    [Fact]
    public void A_block_ends_at_a_blank_line_or_where_the_indentation_falls_back()
    {
        string text = "    if (ready)\n    \n";
        PredictionPrompt prompt = FimFormat.Qwen.Build(new PredictionInput("a.cs", "csharp", text, 14, [], []), 256)!;

        Assert.False(FimFormat.Qwen.Parse(prompt, "\n    {\n        Go();", false).Done);
        PredictionParsed ended = FimFormat.Qwen.Parse(prompt, "\n    {\n        Go();\n    }\n}\n", false);
        Assert.True(ended.Done);
        Assert.Equal("\n    {\n        Go();\n    }", Assert.Single(ended.Edits).Text);

        PredictionParsed blank = FimFormat.Qwen.Parse(prompt, "\n    {\n        Go();\n    }\n\n    Next();", false);
        Assert.Equal("\n    {\n        Go();\n    }", Assert.Single(blank.Edits).Text);
    }

    [Fact]
    public void A_familys_end_token_ends_the_answer()
    {
        PredictionPrompt prompt = FimFormat.Qwen.Build(new PredictionInput("a.cs", "csharp", "int x = ", 8, [], []), 256)!;

        PredictionParsed parsed = FimFormat.Qwen.Parse(prompt, "42;<|endoftext|>junk", false);

        Assert.True(parsed.Done);
        Assert.Equal("42;", Assert.Single(parsed.Edits).Text);
        Assert.Equal(11, parsed.CaretAfter);
    }
}
