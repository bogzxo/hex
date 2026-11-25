using Hex.Core;
using Xunit;

namespace Hex.Tests;

public sealed class InstructEditTests
{
    private const string SIMPLE = "a\nfoo(1);\nb\n";

    // A request whose target is the first occurrence of the given text, or the lines from it up to the end of the given last line
    private static InstructRequest Req(
        string text = SIMPLE,
        string target = "foo(1);",
        bool withNewline = false,
        string instruction = "make it two",
        IReadOnlyList<PredictionEvent>? events = null,
        IReadOnlyList<string>? problems = null,
        IReadOnlyList<ChatTurn>? earlier = null)
    {
        int start = text.IndexOf(target, StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = start + target.Length + (withNewline ? 1 : 0);
        return new InstructRequest("src/a.cs", "csharp", text, start, end, instruction, events ?? [], problems ?? [], earlier ?? []);
    }

    private static string Apply(string text, IReadOnlyList<PredictionEdit> edits)
    {
        foreach (PredictionEdit edit in edits.OrderByDescending(e => e.Start))
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        return text;
    }

    private static string User(InstructRequest request, string? system = null) => InstructEditFormat.Build(request, system)[^1].Content;

    private const string HELLO = "def add(a, b):\n    return a + b\n\nif __name__ == \"__main__\":\n    print(add(2, 3))\n";

    [Fact]
    public void Parse_Strips_A_Run_Of_Copied_Lines_After_The_Target()
    {
        string target = "def add(a, b):\n    return a + b";
        InstructRequest request = new("hello.py", "python", HELLO, 0, target.Length, "add type hints and a docstring", [], [], []);
        string answer = "```python\ndef add(a: int, b: int) -> int:\n    \"\"\"Add two numbers.\"\"\"\n    return a + b\n\n\nif __name__ == \"__main__\":\n    print(add(2, 3))\n```";
        InstructParsed parsed = InstructEditFormat.Parse(request, answer, complete: true);
        Assert.Null(parsed.Problem);
        Assert.Equal("def add(a: int, b: int) -> int:\n    \"\"\"Add two numbers.\"\"\"\n    return a + b", parsed.Replacement);
        Assert.Equal("def add(a: int, b: int) -> int:\n    \"\"\"Add two numbers.\"\"\"\n    return a + b\n\nif __name__ == \"__main__\":\n    print(add(2, 3))\n", Apply(HELLO, parsed.Edits));
    }

    [Fact]
    public void Parse_Strips_A_Run_Of_Copied_Lines_Before_The_Target()
    {
        const string text = "import os\n\nx = 1\ny = 2\nz = 3\n";
        int start = text.IndexOf("y = 2", StringComparison.Ordinal);
        var request = new InstructRequest("a.py", "python", text, start, text.Length - 1, "double", [], [], []);
        InstructParsed parsed = InstructEditFormat.Parse(request, "```python\nimport os\n\nx = 1\ny = 4\nz = 6\n```", complete: true);
        Assert.Equal("y = 4\nz = 6", parsed.Replacement);
    }

    [Fact]
    public void Prompt_Tells_The_Model_Not_To_Repeat_Context()
    {
        Assert.Contains("never repeat", InstructEditFormat.Build(Req(), null)[0].Content);
    }

    // ---- InstructTarget.Around ----

    [Fact]
    public void Around_Empty_Text_Is_Empty()
    {
        Assert.Equal((0, 0), InstructTarget.Around("", 0, 0, 0));
        Assert.Equal((0, 0), InstructTarget.Around("", 3, 9, 5));
    }

    [Fact]
    public void Around_Selection_Mid_Line_Grows_To_Whole_Lines()
    {
        const string text = "aaa\nbbb\nccc\n";
        Assert.Equal((4, 7), InstructTarget.Around(text, 5, 6, 6));
        Assert.Equal((0, 7), InstructTarget.Around(text, 1, 6, 6));
        Assert.Equal((0, 11), InstructTarget.Around(text, 2, 10, 2));
    }

    [Fact]
    public void Around_Selection_Ending_At_A_Line_Start_Leaves_That_Line_Out()
    {
        const string text = "aaa\nbbb\nccc\n";
        Assert.Equal((0, 3), InstructTarget.Around(text, 0, 4, 4));
        Assert.Equal((0, 7), InstructTarget.Around(text, 0, 8, 8));
        Assert.Equal((4, 7), InstructTarget.Around(text, 4, 8, 8));
    }

    [Fact]
    public void Around_Reversed_Selection_Is_The_Same()
    {
        const string text = "aaa\nbbb\nccc";
        Assert.Equal(InstructTarget.Around(text, 1, 6, 1), InstructTarget.Around(text, 6, 1, 6));
    }

    [Fact]
    public void Around_Selection_Is_Clamped()
    {
        const string text = "aaa\nbbb";
        Assert.Equal((0, 7), InstructTarget.Around(text, -5, 100, 0));
    }

    [Fact]
    public void Around_Caret_On_A_Flat_Line_Is_That_Line()
    {
        const string text = "aaa\nbbb\nccc";
        Assert.Equal((4, 7), InstructTarget.Around(text, 5, 5, 5));
        Assert.Equal((0, 3), InstructTarget.Around(text, 0, 0, 0));
        Assert.Equal((8, 11), InstructTarget.Around(text, 11, 11, 11));
    }

    [Fact]
    public void Around_Caret_At_The_End_Of_The_Text_Is_The_Last_Line()
    {
        Assert.Equal((4, 7), InstructTarget.Around("aaa\nbbb", 7, 7, 7));
        Assert.Equal((4, 7), InstructTarget.Around("aaa\nbbb\n", 8, 8, 8));
    }

    [Fact]
    public void Around_Caret_On_A_Blank_Line_Takes_The_Nearest_Line_Above()
    {
        Assert.Equal((0, 3), InstructTarget.Around("aaa\n\nbbb", 4, 4, 4));
        Assert.Equal((2, 5), InstructTarget.Around("\n\nbbb", 0, 0, 0));
    }

    [Fact]
    public void Around_Caret_On_A_Block_Header_Takes_The_Python_Block()
    {
        const string text = "x = 1\ndef f():\n    a = 1\n\n    b = 2\ny = 3\n";
        int start = text.IndexOf("def", StringComparison.Ordinal);
        int end = text.IndexOf("\ny = 3", StringComparison.Ordinal);
        Assert.Equal((start, end), InstructTarget.Around(text, start, start, start));
    }

    [Fact]
    public void Around_Caret_In_A_Body_Starts_At_The_Opener()
    {
        const string text = "x = 1\ndef f():\n    a = 1\n\n    b = 2\ny = 3\n";
        int start = text.IndexOf("def", StringComparison.Ordinal);
        int end = text.IndexOf("\ny = 3", StringComparison.Ordinal);
        int caret = text.IndexOf("a = 1", StringComparison.Ordinal) + 2;
        Assert.Equal((start, end), InstructTarget.Around(text, caret, caret, caret));
    }

    [Fact]
    public void Around_Caret_On_A_Later_Line_Of_A_Body_Takes_The_Block()
    {
        const string text = "def f():\n    a = 1\n    b = 2\ny = 3\n";
        int caret = text.IndexOf("    b", StringComparison.Ordinal) + 6;
        Assert.Equal((0, text.IndexOf("\ny = 3", StringComparison.Ordinal)), InstructTarget.Around(text, caret, caret, caret));
    }

    // A class with so many fields that it is more than sixty lines, and a method after them
    private const string BIG_CLASS = "class C\n{\n" + FIELDS + "    void F()\n    {\n        a;\n        b;\n    }\n}\n";
    private const string FIELDS = "    int f01;\n    int f02;\n    int f03;\n    int f04;\n    int f05;\n    int f06;\n    int f07;\n    int f08;\n    int f09;\n    int f10;\n"
        + "    int f11;\n    int f12;\n    int f13;\n    int f14;\n    int f15;\n    int f16;\n    int f17;\n    int f18;\n    int f19;\n    int f20;\n"
        + "    int f21;\n    int f22;\n    int f23;\n    int f24;\n    int f25;\n    int f26;\n    int f27;\n    int f28;\n    int f29;\n    int f30;\n"
        + "    int f31;\n    int f32;\n    int f33;\n    int f34;\n    int f35;\n    int f36;\n    int f37;\n    int f38;\n    int f39;\n    int f40;\n"
        + "    int f41;\n    int f42;\n    int f43;\n    int f44;\n    int f45;\n    int f46;\n    int f47;\n    int f48;\n    int f49;\n    int f50;\n"
        + "    int f51;\n    int f52;\n    int f53;\n    int f54;\n    int f55;\n    int f56;\n    int f57;\n    int f58;\n    int f59;\n    int f60;\n";

    [Fact]
    public void Around_First_Member_Of_A_Big_Class_Is_Just_Its_Line()
    {
        int start = BIG_CLASS.IndexOf("    int f01;", StringComparison.Ordinal);
        int end = start + "    int f01;".Length;
        Assert.Equal((start, end), InstructTarget.Around(BIG_CLASS, start + 8, start + 8, start + 8));
    }

    [Fact]
    public void Around_Field_In_A_Small_Class_Takes_The_Class()
    {
        const string text = "class C\n{\n    int a;\n    int b;\n}\n";
        int caret = text.IndexOf("int a", StringComparison.Ordinal);
        Assert.Equal((0, text.IndexOf("}", StringComparison.Ordinal) + 1), InstructTarget.Around(text, caret, caret, caret));
    }

    [Fact]
    public void Around_Statement_In_A_Method_Of_A_Big_Class_Gives_The_Method()
    {
        int start = BIG_CLASS.IndexOf("    void F()", StringComparison.Ordinal);
        int end = BIG_CLASS.IndexOf("    }\n", StringComparison.Ordinal) + "    }".Length;
        int caret = BIG_CLASS.IndexOf("b;", StringComparison.Ordinal);
        Assert.Equal((start, end), InstructTarget.Around(BIG_CLASS, caret, caret, caret));
    }

    [Fact]
    public void Around_Method_Header_Of_A_Big_Class_Gives_The_Method()
    {
        int start = BIG_CLASS.IndexOf("    void F()", StringComparison.Ordinal);
        int end = BIG_CLASS.IndexOf("    }\n", StringComparison.Ordinal) + "    }".Length;
        Assert.Equal((start, end), InstructTarget.Around(BIG_CLASS, start + 6, start + 6, start + 6));
    }

    [Fact]
    public void Around_Closing_Brace_Gives_The_Method()
    {
        int start = BIG_CLASS.IndexOf("    void F()", StringComparison.Ordinal);
        int brace = BIG_CLASS.IndexOf("    }\n", StringComparison.Ordinal);
        Assert.Equal((start, brace + "    }".Length), InstructTarget.Around(BIG_CLASS, brace + 4, brace + 4, brace + 4));
    }

    [Fact]
    public void Around_Block_Drops_The_Blank_Lines_At_Its_End()
    {
        const string text = "def f():\n    a\n\n\nx\n";
        Assert.Equal((0, text.IndexOf("\n\n\nx", StringComparison.Ordinal)), InstructTarget.Around(text, 0, 0, 0));
    }

    [Fact]
    public void Around_Block_Stops_At_The_First_Line_As_Shallow_As_The_Header()
    {
        const string text = "if a:\n    b\nelse:\n    c\n";
        Assert.Equal((0, text.IndexOf("\nelse", StringComparison.Ordinal)), InstructTarget.Around(text, 0, 0, 0));
    }

    [Fact]
    public void Around_Tabs_Count_As_Four_Columns()
    {
        const string text = "if a:\n\tb\n    c\nd\n";
        Assert.Equal((0, text.IndexOf("\nd", StringComparison.Ordinal)), InstructTarget.Around(text, 0, 0, 0));
    }

    [Fact]
    public void Around_Braces_Take_The_Whole_Method_From_Inside_It()
    {
        const string text = "class C\n{\n    void F()\n    {\n        a;\n        b;\n    }\n\n    int x;\n}\n";
        int start = text.IndexOf("    void F()", StringComparison.Ordinal);
        int end = text.IndexOf("    }\n", StringComparison.Ordinal) + "    }".Length;
        int caret = text.IndexOf("a;", StringComparison.Ordinal);
        Assert.Equal((start, end), InstructTarget.Around(text, caret, caret, caret));
    }

    [Fact]
    public void Around_Braces_Take_The_Whole_Method_From_Its_Header()
    {
        const string text = "void F()\n{\n    a;\n    b;\n}\n\nint x;\n";
        Assert.Equal((0, text.IndexOf("}", StringComparison.Ordinal) + 1), InstructTarget.Around(text, 0, 0, 0));
    }

    [Fact]
    public void Around_Never_Gives_An_Empty_Target_For_Text_With_Lines()
    {
        const string text = "a\n\n  b\n    c\n\n";
        for (int caret = 0; caret <= text.Length; caret++)
        {
            (int start, int end) = InstructTarget.Around(text, caret, caret, caret);
            Assert.True(start <= end);
            Assert.InRange(end, 0, text.Length);
            Assert.True(end > start);
        }
    }

    // ---- InstructEditFormat.Build ----

    [Fact]
    public void Build_Turns_Are_System_Earlier_Then_User()
    {
        var earlier = new List<ChatTurn> { new("user", "first"), new("assistant", "```\nold\n```") };
        List<ChatTurn> turns = InstructEditFormat.Build(Req(earlier: earlier), null);

        Assert.Equal(4, turns.Count);
        Assert.Equal("system", turns[0].Role);
        Assert.Equal(InstructEditFormat.SYSTEM, turns[0].Content);
        Assert.Equal(earlier[0], turns[1]);
        Assert.Equal(earlier[1], turns[2]);
        Assert.Equal("user", turns[3].Role);
    }

    [Fact]
    public void Build_Appends_The_Persons_System_Prompt()
    {
        Assert.Equal(InstructEditFormat.SYSTEM + "\n\nbe terse", InstructEditFormat.Build(Req(), "be terse")[0].Content);
        Assert.Equal(InstructEditFormat.SYSTEM, InstructEditFormat.Build(Req(), "")[0].Content);
    }

    [Fact]
    public void Build_User_Turn_Has_The_File_The_Code_And_The_Instruction()
    {
        string user = User(Req());
        Assert.Equal("File: src/a.cs (csharp)\nCode:\n```csharp\na\n<edit>\nfoo(1);\n</edit>\nb\n```\nInstruction: make it two", user);
    }

    [Fact]
    public void Build_Target_Ending_With_A_Newline_Gets_No_Blank_Line()
    {
        string user = User(Req(withNewline: true));
        Assert.Contains("<edit>\nfoo(1);\n</edit>\nb\n```", user);
    }

    [Fact]
    public void Build_Target_At_The_End_Of_The_Text_Has_No_Lines_After()
    {
        string user = User(Req(text: "a\nfoo(1);", target: "foo(1);"));
        Assert.Contains("<edit>\nfoo(1);\n</edit>\n```\nInstruction", user);
    }

    [Fact]
    public void Build_Problems_Section_Only_When_There_Are_Problems()
    {
        Assert.DoesNotContain("Problems on these lines", User(Req()));

        string user = User(Req(problems: ["CS1002: ; expected", "CS0103: no x"]));
        Assert.Contains("Problems on these lines:\n- CS1002: ; expected\n- CS0103: no x\nCode:", user);
    }

    [Fact]
    public void Build_Has_At_Most_Ten_Problems()
    {
        string user = User(Req(problems: Enumerable.Range(1, 12).Select(i => $"problem {i:00}").ToList()));
        Assert.Contains("- problem 10\n", user);
        Assert.DoesNotContain("problem 11", user);
    }

    [Fact]
    public void Build_Edits_Section_Only_When_There_Are_Events_Newest_Last()
    {
        Assert.DoesNotContain("Recent edits", User(Req()));

        var older = new PredictionEvent("src/a.cs", "@@ -1 +1 @@\n-one\n+two\n", false);
        var newer = new PredictionEvent("src/b.cs", "@@ -1 +1 @@\n-three\n+four\n", true);
        string user = User(Req(events: [older, newer]));

        Assert.Contains("Recent edits:\n", user);
        int first = user.IndexOf(Zeta2Format.Event(older), StringComparison.Ordinal);
        int second = user.IndexOf(Zeta2Format.Event(newer), StringComparison.Ordinal);
        Assert.True(first > 0);
        Assert.True(second > first);
        Assert.True(second < user.IndexOf("Code:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_Edits_Drop_The_Oldest_First_When_They_Are_Too_Many_Tokens()
    {
        string big = "@@ -1 +1 @@\n-" + new string('x', 6000) + "\n+" + new string('y', 6000) + "\n";
        var old = new PredictionEvent("src/old.cs", big, false);
        var recent = new PredictionEvent("src/recent.cs", "@@ -1 +1 @@\n-a\n+b\n", false);
        string user = User(Req(events: [old, recent]));

        Assert.Contains("src/recent.cs", user);
        Assert.DoesNotContain("src/old.cs", user);
    }

    [Fact]
    public void Build_Context_Is_Sixty_Lines_Before_And_Thirty_After()
    {
        string text = string.Concat(Enumerable.Range(1, 200).Select(i => $"line {i}\n"));
        string user = User(Req(text: text, target: "line 100"));

        Assert.Contains("line 40\nline 41\n", user);
        Assert.DoesNotContain("line 39\n", user);
        Assert.Contains("line 99\n<edit>\nline 100\n</edit>\nline 101\n", user);
        Assert.Contains("line 130\n```", user);
        Assert.DoesNotContain("line 131\n", user);
    }

    [Fact]
    public void Build_Fence_Is_Longer_Than_Any_Run_Of_Backticks_In_The_Code()
    {
        string user = User(Req(text: "a\nfoo(1); // ````\nb\n", target: "foo(1); // ````"));
        Assert.Contains("`````csharp\na\n<edit>\n", user);
        Assert.Contains("b\n`````\nInstruction", user);

        Assert.Contains("```csharp\n", User(Req()));
        Assert.DoesNotContain("````", User(Req()));
    }

    // ---- InstructEditFormat.Parse ----

    private static void AssertReplaced(InstructRequest request, InstructParsed parsed, string replacement)
    {
        Assert.Null(parsed.Problem);
        Assert.True(parsed.Done);
        Assert.Equal(replacement, parsed.Replacement);
        Assert.Equal(request.Text[..request.TargetStart] + replacement + request.Text[request.TargetEnd..], Apply(request.Text, parsed.Edits));
    }

    [Fact]
    public void Parse_Fenced_Block_With_A_Language()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "```csharp\nfoo(2);\n```", true), "foo(2);");
    }

    [Fact]
    public void Parse_Fenced_Block_Without_A_Language_And_With_Prose_Around()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "Here you go:\n```\nfoo(2);\n```\nHope that helps.", true), "foo(2);");
    }

    [Fact]
    public void Parse_Tilde_Fence()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "~~~cs\nfoo(2);\n~~~\n", false), "foo(2);");
    }

    [Fact]
    public void Parse_A_Closed_Block_Is_Done_Before_The_Stream_Ends()
    {
        InstructRequest request = Req();
        InstructParsed parsed = InstructEditFormat.Parse(request, "```\nfoo(2);\n```\nand then", false);
        Assert.True(parsed.Done);
        Assert.Equal("foo(2);", parsed.Replacement);
    }

    [Fact]
    public void Parse_Longer_Fence_Holds_Shorter_Ones()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "````md\nfoo(2);\n```\n````", true), "foo(2);\n```");
    }

    [Fact]
    public void Parse_Closing_Fence_Must_Be_As_Long_And_The_Same_Mark()
    {
        InstructRequest request = Req();
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "````\nfoo(2);\n```\n~~~~", false));
    }

    [Fact]
    public void Parse_Unterminated_Block_Waits_While_Streaming()
    {
        InstructRequest request = Req();
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "```csharp\nfoo(2", false));
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "```csharp\nfoo(2);\n", false));
    }

    [Fact]
    public void Parse_Unterminated_Block_Is_Cut_Short_When_Complete()
    {
        InstructRequest request = Req();
        InstructParsed parsed = InstructEditFormat.Parse(request, "```csharp\nfoo(2);\n", true);
        Assert.Equal(InstructProblem.CUT_SHORT, parsed.Problem);
        Assert.True(parsed.Done);
        Assert.Null(parsed.Replacement);
        Assert.Empty(parsed.Edits);
    }

    [Fact]
    public void Parse_Thinking_Is_Dropped()
    {
        InstructRequest request = Req();
        string answer = "<think>I should write ```\nbar();\n``` maybe</think>\n```csharp\nfoo(2);\n```";
        AssertReplaced(request, InstructEditFormat.Parse(request, answer, true), "foo(2);");
    }

    [Fact]
    public void Parse_Waits_While_The_Model_Is_Still_Thinking()
    {
        InstructRequest request = Req();
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "<think>hmm\n```\nfoo(2);\n```\nmore", false));
        Assert.Equal(InstructProblem.NO_CODE, InstructEditFormat.Parse(request, "<think>hmm", true).Problem);
    }

    [Fact]
    public void Parse_Short_Answer_Without_A_Fence_Waits_While_Streaming()
    {
        InstructRequest request = Req();
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "Sure, let me", false));
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, "", false));
        Assert.Equal(InstructParsed.Waiting, InstructEditFormat.Parse(request, new string('x', 300), false));
    }

    [Fact]
    public void Parse_Edit_Tags_Are_Used_When_There_Is_No_Fence()
    {
        InstructRequest request = Req();
        string answer = "Sure, here is the rewritten code, with the change you asked for made.\n<edit>\nfoo(2);\n</edit>\nThat is all.";
        AssertReplaced(request, InstructEditFormat.Parse(request, answer, true), "foo(2);");
    }

    [Fact]
    public void Parse_Edit_Tags_Are_Used_While_Streaming_When_The_Answer_Is_Long()
    {
        InstructRequest request = Req();
        string answer = new string('x', 200) + "\n<edit>\nfoo(2);\n</edit>\nmore";
        AssertReplaced(request, InstructEditFormat.Parse(request, answer, false), "foo(2);");
    }

    [Fact]
    public void Parse_Whole_Answer_Is_The_Code_When_It_Is_Mostly_The_Targets_Lines()
    {
        InstructRequest request = Req(text: "a\nx = 1;\ny = 2;\nz = 3;\nb\n", target: "x = 1;\ny = 2;\nz = 3;");
        AssertReplaced(request, InstructEditFormat.Parse(request, "x = 1;\ny = 5;\nz = 3;\n", true), "x = 1;\ny = 5;\nz = 3;");
    }

    [Fact]
    public void Parse_Prose_Without_Code_Is_No_Code()
    {
        InstructRequest request = Req(text: "a\nx = 1;\ny = 2;\nz = 3;\nb\n", target: "x = 1;\ny = 2;\nz = 3;");
        InstructParsed parsed = InstructEditFormat.Parse(request, "I am sorry,\nI cannot do that.", true);
        Assert.Equal(InstructProblem.NO_CODE, parsed.Problem);
        Assert.True(parsed.Done);
        Assert.Null(parsed.Replacement);
        Assert.Empty(parsed.Edits);
    }

    [Fact]
    public void Parse_Copied_Context_Lines_Are_Dropped()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\na\nfoo(2);\nb\n```", true), "foo(2);");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\na\nfoo(2);\n```", true), "foo(2);");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nfoo(2);\nb\n```", true), "foo(2);");
    }

    [Fact]
    public void Parse_Copied_Edit_Tags_Inside_The_Block_Are_Dropped()
    {
        InstructRequest request = Req();
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\n<edit>\nfoo(2);\n</edit>\n```", true), "foo(2);");
    }

    [Fact]
    public void Parse_Context_Line_Equal_To_The_Targets_Own_Line_Is_Kept()
    {
        // The line before the target is the same as its first line: it can't tell a copy from the code
        InstructRequest request = Req(text: "x();\nx();\nb\n", target: "x();\nb");
        string text = request.Text;
        InstructRequest second = request with { TargetStart = 5, TargetEnd = 11 };
        Assert.Equal("x();\nb", second.Target);
        InstructParsed parsed = InstructEditFormat.Parse(second, "```\nx();\ny();\nb\n```", true);
        Assert.Equal("x();\ny();\nb", parsed.Replacement);
        Assert.Equal("x();\nx();\ny();\nb\n", Apply(text, parsed.Edits));
    }

    [Fact]
    public void Parse_Indentation_The_Target_Has_Is_Put_Back()
    {
        const string text = "class C {\n    void F() {\n        foo(1);\n        bar();\n    }\n}\n";
        InstructRequest request = Req(text: text, target: "        foo(1);\n        bar();");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nfoo(2);\n\nbar();\n```", true), "        foo(2);\n\n        bar();");
    }

    [Fact]
    public void Parse_Indentation_Already_There_Is_Left_As_It_Is()
    {
        const string text = "class C {\n    void F() {\n        foo(1);\n        bar();\n    }\n}\n";
        InstructRequest request = Req(text: text, target: "        foo(1);\n        bar();");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\n        foo(2);\n        bar();\n```", true), "        foo(2);\n        bar();");
    }

    [Fact]
    public void Parse_Unchanged_Answer_Is_A_Problem()
    {
        InstructRequest request = Req();
        InstructParsed parsed = InstructEditFormat.Parse(request, "```\nfoo(1);\n```", true);
        Assert.Equal(InstructProblem.UNCHANGED, parsed.Problem);
        Assert.True(parsed.Done);
        Assert.Empty(parsed.Edits);
        Assert.Equal("foo(1);", parsed.Replacement);
    }

    [Fact]
    public void Parse_Answer_Differing_Only_In_Trailing_Spaces_Is_Unchanged()
    {
        InstructRequest request = Req(text: "a\nx = 1;\ny = 2;\nb\n", target: "x = 1;\ny = 2;");
        InstructParsed parsed = InstructEditFormat.Parse(request, "```\nx = 1;   \ny = 2;\t\n```", true);
        Assert.Equal(InstructProblem.UNCHANGED, parsed.Problem);
        Assert.Equal("x = 1;\ny = 2;", parsed.Replacement);
    }

    [Fact]
    public void Parse_Target_With_A_Trailing_Newline_Gets_One_Back()
    {
        InstructRequest request = Req(withNewline: true);
        Assert.Equal("foo(1);\n", request.Target);
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nfoo(2);\n```", true), "foo(2);\n");
    }

    [Fact]
    public void Parse_Target_Without_A_Trailing_Newline_Gets_None()
    {
        InstructRequest request = Req(text: "a\nfoo(1);", target: "foo(1);");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nfoo(2);\n\n```", true), "foo(2);\n");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nfoo(2);\n```", true), "foo(2);");
    }

    [Fact]
    public void Parse_Edits_Are_Offsets_Into_The_Whole_Text()
    {
        const string text = "line 1\nline 2\nfoo(1);\nbar(2);\nbaz(3);\nline 6\nline 7\n";
        InstructRequest request = Req(text: text, target: "foo(1);\nbar(2);\nbaz(3);");
        InstructParsed parsed = InstructEditFormat.Parse(request, "```\nfoo(1);\nqux(2);\nextra();\nbaz(3);\n```", true);

        AssertReplaced(request, parsed, "foo(1);\nqux(2);\nextra();\nbaz(3);");
        Assert.All(parsed.Edits, e => Assert.InRange(e.Start, request.TargetStart, request.TargetEnd));
    }

    [Fact]
    public void Parse_Deleting_Lines()
    {
        const string text = "a\nx\ny\nz\nb\n";
        InstructRequest request = Req(text: text, target: "x\ny\nz", withNewline: true);
        AssertReplaced(request, InstructEditFormat.Parse(request, "```\nx\nz\n```", true), "x\nz\n");
    }

    [Fact]
    public void Parse_Crlf_Answers_Are_Read()
    {
        InstructRequest request = Req(text: "a\nx\ny\nb\n", target: "x\ny");
        AssertReplaced(request, InstructEditFormat.Parse(request, "```cs\r\nx\r\nz\r\n```\r\n", true), "x\nz");
    }

    // ---- Line structure of a pure insertion ----

    private static InstructRequest CaretRequest(string text, int caretLine)
    {
        int at = 0;
        for (int i = 1; i < caretLine; i++)
            at = text.IndexOf('\n', at) + 1;
        // Line 4 opens a block, which Around takes whole; line 5 is taken as its own line, as when it is selected
        int lineEnd = text.IndexOf('\n', at) is var nl and >= 0 ? nl : text.Length;
        (int start, int end) = caretLine == 5 ? (at, lineEnd) : InstructTarget.Around(text, at, at, at);
        return new InstructRequest("hello.py", "python", text, start, end, "insert a comment line above", [], [], []);
    }

    [Theory]
    [InlineData(5, "    # note", "    print(add(2, 3))")]
    [InlineData(4, "# Main script", "if __name__ == \"__main__\":\n    print(add(2, 3))")]
    public void Parse_Insertion_Above_Caret_Keeps_The_Line_Structure(int line, string added, string kept)
    {
        InstructRequest request = CaretRequest(HELLO, line);
        string expected = HELLO.Replace(kept, added + "\n" + kept);
        foreach (string answer in new[]
        {
            $"```python\n{added}\n{kept}\n```",
            $"```python\n{added}\n{kept}\n```\n",
        })
        {
            InstructParsed parsed = InstructEditFormat.Parse(request, answer, complete: true);
            Assert.Null(parsed.Problem);
            Assert.Equal(expected, Apply(HELLO, parsed.Edits));
        }
    }

    [Fact]
    public void Parse_Insertion_Before_An_Indented_Line_Is_Put_In_At_The_Start_Of_The_Line()
    {
        InstructRequest request = CaretRequest(HELLO, 5);
        InstructParsed parsed = InstructEditFormat.Parse(request, "```python\n    # note\n    print(add(2, 3))\n```", complete: true);
        PredictionEdit edit = Assert.Single(parsed.Edits);
        Assert.Equal(request.TargetStart, edit.Start);
        Assert.Equal(request.TargetStart, edit.End);
        Assert.Equal("    # note\n", edit.Text);
    }

    [Fact]
    public void Parse_Insertion_With_Target_Ending_In_Newline_Keeps_It()
    {
        int start = HELLO.IndexOf("    print", StringComparison.Ordinal);
        var request = new InstructRequest("hello.py", "python", HELLO, start, HELLO.Length, "add a comment above", [], [], []);
        InstructParsed parsed = InstructEditFormat.Parse(request, "```python\n    # note\n    print(add(2, 3))\n```", complete: true);
        Assert.Equal(HELLO.Replace("    print", "    # note\n    print"), Apply(HELLO, parsed.Edits));
    }

    [Fact]
    public void Parse_Answer_Of_Only_The_New_Line_Is_Not_Glued_To_The_Target()
    {
        // The model answers with just the comment: it is a pure insertion, put on a line of its own above
        InstructRequest request = CaretRequest(HELLO, 5);
        InstructParsed parsed = InstructEditFormat.Parse(request, "```python\n    # note\n```", complete: true);
        Assert.Null(parsed.Problem);
        Assert.Equal(HELLO.Replace("    print", "    # note\n    print"), Apply(HELLO, parsed.Edits));
    }

    [Fact]
    public void Parse_Answer_Of_Only_The_New_Line_Without_Fence_Is_Accepted()
    {
        InstructRequest request = CaretRequest(HELLO, 5);
        InstructParsed parsed = InstructEditFormat.Parse(request, "    # note", complete: true);
        Assert.Null(parsed.Problem);
        Assert.Equal(HELLO.Replace("    print", "    # note\n    print"), Apply(HELLO, parsed.Edits));
    }

    // ---- InstructEditFormat.MaxTokens ----

    [Fact]
    public void MaxTokens_Is_Twice_The_Target_Plus_512()
    {
        InstructRequest request = Req(text: new string('x', 300), target: new string('x', 300));
        Assert.Equal(100 * 2 + 512, InstructEditFormat.MaxTokens(request, false));
    }

    [Fact]
    public void MaxTokens_Adds_The_Thinking_Budget()
    {
        InstructRequest request = Req(text: new string('x', 300), target: new string('x', 300));
        Assert.Equal(100 * 2 + 512 + 2048, InstructEditFormat.MaxTokens(request, true));
    }

    [Fact]
    public void MaxTokens_Is_At_Most_16384()
    {
        InstructRequest request = Req(text: new string('x', 100_000), target: new string('x', 100_000));
        Assert.Equal(16384, InstructEditFormat.MaxTokens(request, true));
        Assert.Equal(16384, InstructEditFormat.MaxTokens(request, false));
    }
}
