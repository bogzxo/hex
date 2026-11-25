using Hex.Core;
using Hex.Dawn;
using Xunit;

namespace Hex.Tests;

/// <summary>AI Edit with several selections, @ context and the language server's problems in the prompt.</summary>
public sealed class AiEditMultiTests
{
    private const string TEXT = "a\nfoo(1);\nb\nbar(2);\nc\n";

    private static InstructRequest Req(int start, int end, IReadOnlyList<string>? problems = null, IReadOnlyList<InstructContext>? context = null) =>
        new("src/a.cs", "csharp", TEXT, start, end, "make it bigger", [], problems ?? [], [], context);

    private static (int Start, int End) Line(string text) =>
        (TEXT.IndexOf(text, StringComparison.Ordinal), TEXT.IndexOf(text, StringComparison.Ordinal) + text.Length);

    private static string User(InstructRequest request) => InstructEditFormat.Build(request, null)[^1].Content;

    // ---- targets ----

    [Fact]
    public void Two_selections_make_two_targets_in_the_order_of_the_text()
    {
        (int fs, int fe) = Line("foo(1);");
        (int bs, int be) = Line("bar(2);");
        // Given out of order, the second one primary
        (List<(int Start, int End)> targets, int primary) = HexAiEditGlue.Targets(TEXT, [(bs, be, be), (fs, fe, fe)], 0);
        Assert.Equal(2, targets.Count);
        Assert.Equal(Line("foo(1);"), targets[0]);
        Assert.Equal(Line("bar(2);"), targets[1]);
        Assert.Equal(1, primary);
    }

    [Fact]
    public void Selections_on_the_same_lines_are_one_target()
    {
        (int fs, int fe) = Line("foo(1);");
        (List<(int Start, int End)> targets, int primary) = HexAiEditGlue.Targets(TEXT, [(fs, fs + 2, fs + 2), (fs + 3, fe, fe)], 1);
        Assert.Single(targets);
        Assert.Equal(0, primary);
    }

    // ---- one-word selections, comments added above ----

    private const string HELLO = "def add(a, b):\n    return a + b\n\nif __name__ == \"__main__\":\n    print(add(2, 3))\n";

    private static List<InstructRequest> HelloRequests()
    {
        int one = HELLO.IndexOf("add", StringComparison.Ordinal), five = HELLO.LastIndexOf("add", StringComparison.Ordinal);
        (List<(int Start, int End)> targets, _) = HexAiEditGlue.Targets(HELLO, [(five, five + 3, five + 3), (one, one + 3, one + 3)], 0);
        return [.. targets.Select(t => new InstructRequest("hello.py", "python", HELLO, t.Start, t.End, "add a comment above", [], [], []))];
    }

    [Fact]
    public void One_word_selections_grow_to_whole_lines_and_both_get_a_comment_above()
    {
        List<InstructRequest> requests = HelloRequests();
        Assert.Equal(2, requests.Count);
        Assert.Equal("def add(a, b):", requests[0].Target);
        Assert.Equal("    print(add(2, 3))", requests[1].Target);

        string[] answers = ["```python\n# add\ndef add(a, b):\n```", "```python\n    # call\n    print(add(2, 3))\n```"];
        var parts = new List<IReadOnlyList<PredictionEdit>>();
        for (int i = 0; i < requests.Count; i++)
        {
            InstructParsed parsed = InstructEditFormat.Parse(requests[i], answers[i], complete: true);
            Assert.Null(parsed.Problem);
            parts.Add(parsed.Edits);
        }
        List<PredictionEdit>? combined = HexAiEditGlue.Combine(parts);
        Assert.NotNull(combined);
        Assert.Equal("# add\ndef add(a, b):\n    return a + b\n\nif __name__ == \"__main__\":\n    # call\n    print(add(2, 3))\n", HexAiEditGlue.Apply(HELLO, combined));
    }

    [Fact]
    public void Answers_of_only_the_comment_still_insert_it_above_each_target()
    {
        List<InstructRequest> requests = HelloRequests();
        string[] answers = ["```python\n# add\n```", "# call"];
        var parts = new List<IReadOnlyList<PredictionEdit>>();
        for (int i = 0; i < requests.Count; i++)
        {
            InstructParsed parsed = InstructEditFormat.Parse(requests[i], answers[i], complete: true);
            Assert.Null(parsed.Problem);
            parts.Add(parsed.Edits);
        }
        List<PredictionEdit>? combined = HexAiEditGlue.Combine(parts);
        Assert.NotNull(combined);
        Assert.Equal("# add\ndef add(a, b):\n    return a + b\n\nif __name__ == \"__main__\":\n    # call\n    print(add(2, 3))\n", HexAiEditGlue.Apply(HELLO, combined));
    }

    [Fact]
    public void The_prompt_tells_the_model_to_keep_the_original_lines_when_adding()
    {
        Assert.Contains("original lines that stay", InstructEditFormat.Build(HelloRequests()[0], null)[0].Content);
    }

    // ---- one answer, one undo step ----

    [Fact]
    public void The_answers_of_all_targets_combine_in_order_and_apply_together()
    {
        (int fs, int fe) = Line("foo(1);");
        (int bs, int be) = Line("bar(2);");
        var first = new List<PredictionEdit> { new(fs, fe, "foo(10);") };
        var second = new List<PredictionEdit> { new(bs, be, "bar(20);") };

        // Whatever order they were answered in, the editor is given them in the order of the text
        List<PredictionEdit>? combined = HexAiEditGlue.Combine([second, first]);
        Assert.NotNull(combined);
        Assert.Equal([first[0], second[0]], combined);
        Assert.Equal("a\nfoo(10);\nb\nbar(20);\nc\n", HexAiEditGlue.Apply(TEXT, combined));
    }

    [Fact]
    public void Answers_that_overlap_do_not_combine()
    {
        var a = new List<PredictionEdit> { new(2, 8, "x") };
        var b = new List<PredictionEdit> { new(5, 9, "y") };
        Assert.Null(HexAiEditGlue.Combine([a, b]));
    }

    [Fact]
    public void A_target_left_unchanged_adds_no_edits()
    {
        (int fs, int fe) = Line("foo(1);");
        var changed = new List<PredictionEdit> { new(fs, fe, "foo(2);") };
        List<PredictionEdit>? combined = HexAiEditGlue.Combine([changed, []]);
        Assert.Equal(changed, combined);
    }

    // ---- the prompt for each target ----

    [Fact]
    public void Each_target_has_its_own_code_and_the_same_start_of_the_prompt()
    {
        InstructContext[] context = [new("src/b.cs", "class B {}")];
        (int fs, int fe) = Line("foo(1);");
        (int bs, int be) = Line("bar(2);");
        string one = User(Req(fs, fe, context: context)), two = User(Req(bs, be, context: context));

        Assert.Contains("<edit>\nfoo(1);\n</edit>", one);
        Assert.Contains("<edit>\nbar(2);\n</edit>", two);
        Assert.DoesNotContain("bar(2);\n</edit>", one);

        // Everything before the code is the same, which is what the server's prompt cache is given
        string startOne = one[..one.IndexOf("Code:", StringComparison.Ordinal)], startTwo = two[..two.IndexOf("Code:", StringComparison.Ordinal)];
        Assert.Equal(startOne, startTwo);
        Assert.Contains("Context from src/b.cs", startOne);
    }

    // ---- diagnostics ----

    [Fact]
    public void The_problems_on_the_target_lines_are_in_the_prompt()
    {
        (int fs, int fe) = Line("foo(1);");
        string user = User(Req(fs, fe, [HexAiEditGlue.ProblemLine(1, "CS0103: The name 'foo' does not exist\nin this context")]));
        Assert.Contains("Problems on these lines:\n- line 2: CS0103: The name 'foo' does not exist in this context\n", user);
        Assert.True(user.IndexOf("Problems on these lines", StringComparison.Ordinal) < user.IndexOf("Code:", StringComparison.Ordinal));
    }

    [Fact]
    public void No_problems_say_nothing_and_ten_at_most_are_listed()
    {
        (int fs, int fe) = Line("foo(1);");
        Assert.DoesNotContain("Problems", User(Req(fs, fe)));
        string many = User(Req(fs, fe, [.. Enumerable.Range(1, 15).Select(i => $"line {i}: problem {i}")]));
        Assert.Contains("problem 10", many);
        Assert.DoesNotContain("problem 11", many);
    }

    // ---- @ mentions ----

    [Fact]
    public void Mentions_are_taken_out_of_the_instruction()
    {
        (string instruction, List<string> files, bool diagnostics) = InstructMentions.Parse("use @src/Util.cs and @diagnostics, to fix this @src/Util.cs");
        Assert.Equal(["src/Util.cs"], files);
        Assert.True(diagnostics);
        Assert.DoesNotContain("@", instruction);
        Assert.StartsWith("use", instruction);
    }

    [Fact]
    public void An_at_inside_a_word_is_no_mention()
    {
        (string instruction, List<string> files, bool diagnostics) = InstructMentions.Parse("mail a@b.com about it");
        Assert.Equal("mail a@b.com about it", instruction);
        Assert.Empty(files);
        Assert.False(diagnostics);
    }

    [Theory]
    [InlineData("fix it @", "")]
    [InlineData("@src/Fo", "src/Fo")]
    [InlineData("fix it", null)]
    [InlineData("fix @a b", null)]
    [InlineData("a@b", null)]
    public void The_word_being_typed_is_found(string text, string? typing) => Assert.Equal(typing, InstructMentions.Typing(text));

    [Fact]
    public void The_mention_being_typed_comes_off_the_end()
    {
        Assert.Equal("fix it ", InstructMentions.WithoutTyping("fix it @src"));
        Assert.Equal("fix it", InstructMentions.WithoutTyping("fix it"));
    }

    [Fact]
    public void A_short_file_is_kept_whole()
    {
        const string text = "line one\nline two\n";
        Assert.Equal(text, InstructMentions.Cap(text, ["two"]));
    }

    [Fact]
    public void A_long_file_is_cut_to_its_tokens_round_the_names_the_target_uses()
    {
        var lines = Enumerable.Range(0, 3000).Select(i => $"var filler{i} = {i}; // padding to make the line a little longer").ToList();
        lines[2500] = "int NeedleMethod() => 42;";
        string text = string.Join('\n', lines);
        Assert.True(PredictionTokens.Of(text) > InstructMentions.FILE_TOKENS * 4);

        string capped = InstructMentions.Cap(text, ["NeedleMethod"]);
        Assert.True(PredictionTokens.Of(capped) <= InstructMentions.FILE_TOKENS + 50, $"{PredictionTokens.Of(capped)} tokens");
        Assert.Contains("NeedleMethod", capped);
        Assert.Contains("…", capped);
        Assert.DoesNotContain("filler0 ", capped);
    }

    [Fact]
    public void A_long_file_with_no_matches_is_cut_from_its_start()
    {
        string text = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"var filler{i} = {i}; // padding to make the line a little longer"));
        string capped = InstructMentions.Cap(text, ["Nothing"]);
        Assert.True(PredictionTokens.Of(capped) <= InstructMentions.FILE_TOKENS + 50);
        Assert.StartsWith("var filler0 ", capped);
    }

    [Fact]
    public void The_terms_of_a_target_are_its_longer_identifiers_most_used_first()
    {
        List<string> terms = InstructMentions.Terms("int a = Widget.Make(); Widget.Reset(); var x = Gadget;");
        Assert.Equal("Widget", terms[0]);
        Assert.DoesNotContain("int", terms);
        Assert.Contains("Gadget", terms);
    }
}
