using Hex.Dawn;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// C# got past netcoredbg the way the file it's stopped in would read it, against a netcoredbg played by a table of what
/// it answers, the answers the real one gave. A short name found through the usings, a const local put in, a cast worked
/// out, whole numbers widened for a call, and a call given an enum's member refused rather than answered wrong.
/// </summary>
public sealed class HexDebugEvaluatorTests
{
    private static readonly HexDebugScope Tweening = HexDebugScope.Of("""
        using Horizon.Core.Tweening;
        using static Horizon.Core.Tweening.Ease;
        using Pad = Horizon.Input.Gamepad;

        namespace Horizon.Testing.Examples;

        internal sealed class Example
        {
            private const float Speed = 2;
            void Go()
            {
                const int Limit = 7;
            }
        }
        """, ["System"]);

    // What netcoredbg says to each expression, anything not in the table a name it doesn't know
    private static Func<string, int, string, Task<DebugEvaluation>> Netcoredbg(Dictionary<string, object> answers, List<string>? asked = null) =>
        (expression, _, _) =>
        {
            asked?.Add(expression);
            if (!answers.TryGetValue(expression, out object? answer))
            {
                // It names the first chain of names it couldn't find, the way the real one does
                string name = HexDebugExpressions.Chains(expression) is [var first, ..] ? expression[first.Start..first.End] : expression;
                return Task.FromResult(new DebugEvaluation(null, expression.Contains('(') ? "error: 0x80004005" : $"error: The name '{name}' does not exist in the current context"));
            }
            return Task.FromResult(answer is DebugVariable value ? new DebugEvaluation(value, null) : new DebugEvaluation(null, (string)answer));
        };

    private static DebugVariable Value(string value, string type, int reference = 0) => new("x", value, type, reference);

    private static HexDebugEvaluator Evaluator(Dictionary<string, object> answers, List<string>? asked = null, Dictionary<int, DebugVariable[]>? inside = null) =>
        new(Netcoredbg(answers, asked), Said, inside is null ? null : reference => Task.FromResult<IReadOnlyList<DebugVariable>>(inside.GetValueOrDefault(reference, [])));

    // A key with what goes in it after bars, the key alone for nothing
    private static string Said(string key, (string Name, string Value)[] values) =>
        string.Join("|", values.Select(value => value.Value).Where(value => value.Length > 0).Prepend(key));

    [Fact]
    public void A_call_given_an_enums_member_is_refused_rather_than_answered_wrong()
    {
        var asked = new List<string>();
        var answers = new Dictionary<string, object>
        {
            ["Horizon.Core.Tweening.Easing.OutQuad"] = Value("OutQuad", "Horizon.Core.Tweening.Easing", 1),
            // What the real one said, 0.5 where it's 0.75
            ["Horizon.Core.Tweening.Ease.Apply(Horizon.Core.Tweening.Easing.OutQuad, 0.5f)"] = Value("0.5", "float")
        };

        DebugEvaluation said = Evaluator(answers, asked).Evaluate("Ease.Apply(Easing.OutQuad, 0.5f)", 0, Tweening).Result;
        Assert.Null(said.Value);
        Assert.Equal("debugging.eval_enum_argument|Easing.OutQuad", said.Error);
        Assert.DoesNotContain(asked, expression => expression.Contains("Apply"));

        // Written out whole it's refused the same
        said = Evaluator(answers).Evaluate("Horizon.Core.Tweening.Ease.Apply(Horizon.Core.Tweening.Easing.OutQuad, 0.5f)", 0, Tweening).Result;
        Assert.Equal("debugging.eval_enum_argument|Horizon.Core.Tweening.Easing.OutQuad", said.Error);
    }

    [Fact]
    public void A_variable_holding_an_enum_goes_through_and_the_call_is_found_through_the_usings()
    {
        var answers = new Dictionary<string, object>
        {
            ["mode"] = Value("OutQuad", "Horizon.Core.Tweening.Easing", 1),
            ["Horizon.Core.Tweening.Ease.Apply(mode, 0.5f)"] = Value("0.75", "float")
        };

        DebugEvaluation said = Evaluator(answers).Evaluate("Ease.Apply(mode, 0.5f)", 0, Tweening).Result;
        Assert.Equal(("Ease.Apply(mode, 0.5f)", "0.75"), (said.Value!.Value.Name, said.Value!.Value.Value));
    }

    [Fact]
    public void Whole_numbers_passed_to_a_call_are_widened_the_way_CSharp_would()
    {
        var answers = new Dictionary<string, object>
        {
            ["System.MathF.Pow(2, 3)"] = "error: 0x80070057",
            ["System.MathF.Pow(2L, 3L)"] = "error: 0x80070057",
            ["System.MathF.Pow(2f, 3f)"] = Value("8", "float")
        };

        DebugEvaluation said = Evaluator(answers).Evaluate("MathF.Pow(2, 3)", 0, Tweening).Result;
        Assert.Equal(("8", "float"), (said.Value!.Value.Value, said.Value!.Value.Type));
    }

    [Fact]
    public void A_name_found_once_is_written_that_way_from_then_on()
    {
        var asked = new List<string>();
        var answers = new Dictionary<string, object>
        {
            ["Horizon.Core.Tweening.Easing.OutQuad"] = Value("OutQuad", "Horizon.Core.Tweening.Easing", 1)
        };

        HexDebugEvaluator evaluator = Evaluator(answers, asked);
        Assert.Equal("OutQuad", evaluator.Evaluate("Easing.OutQuad", 0, Tweening).Result.Value!.Value.Value);
        asked.Clear();
        Assert.Equal("OutQuad", evaluator.Evaluate("Easing.OutQuad", 0, Tweening).Result.Value!.Value.Value);
        Assert.Equal(["Easing.OutQuad", "Horizon.Core.Tweening.Easing.OutQuad"], asked);
    }

    [Fact]
    public void A_const_local_is_what_its_set_to_with_its_types_suffix()
    {
        var answers = new Dictionary<string, object> { ["7"] = Value("7", "int"), ["2f * 3"] = Value("6", "float") };
        Assert.Equal("7", Evaluator(answers).Evaluate("Limit", 0, Tweening).Result.Value!.Value.Value);
        Assert.Equal("6", Evaluator(answers).Evaluate("Speed * 3", 0, Tweening).Result.Value!.Value.Value);
    }

    [Fact]
    public void A_cast_in_front_is_worked_out_from_what_was_cast()
    {
        var answers = new Dictionary<string, object>
        {
            ["total"] = Value("300", "int"),
            ["ratio"] = Value("-3.75", "float"),
            ["huge"] = Value("1E+30", "double"),
            ["k"] = Value("OutQuad", "Horizon.Core.Tweening.Easing", 1)
        };

        HexDebugEvaluator evaluator = Evaluator(answers);
        Assert.Equal(("300", "long"), Said(evaluator.Evaluate("(long)total", 0, Tweening).Result));
        Assert.Equal(("44", "byte"), Said(evaluator.Evaluate("(byte)total", 0, Tweening).Result));
        Assert.Equal(("-3", "int"), Said(evaluator.Evaluate("(int)ratio", 0, Tweening).Result));
        Assert.Equal(("300", "float"), Said(evaluator.Evaluate("(float)total", 0, Tweening).Result));
        Assert.Equal("debugging.eval_cast_range|int", evaluator.Evaluate("(int)huge", 0, Tweening).Result.Error);
        Assert.Equal("debugging.eval_cast_enum|int", evaluator.Evaluate("(int)k", 0, Tweening).Result.Error);
    }

    private static (string, string?) Said(DebugEvaluation evaluated) => (evaluated.Value!.Value.Value, evaluated.Value!.Value.Type);

    [Fact]
    public void What_netcoredbg_cant_do_is_said_plainly()
    {
        var answers = new Dictionary<string, object>
        {
            ["(long)total + 1"] = "NetCoreDbg.Evaluation+SyntaxKindNotImplementedException: CastExpression not implemented!",
            ["Nothing.Here(1)"] = "error: 0x80004005"
        };

        HexDebugEvaluator evaluator = Evaluator(answers);
        Assert.Equal("debugging.eval_not_done|debugging.eval_kind_cast", evaluator.Evaluate("(long)total + 1", 0, Tweening).Result.Error);
        Assert.Equal("debugging.eval_no_method", evaluator.Evaluate("Nothing.Here(1)", 0, HexDebugScope.Empty).Result.Error);
    }

    [Fact]
    public void An_arrays_Length_is_put_in_as_what_it_comes_to_inside_a_bigger_expression()
    {
        var answers = new Dictionary<string, object>
        {
            ["args"] = Value("{string[0]}", "string[]", 3),
            ["name"] = Value("\"abc\"", "string"),
            ["0 == 0"] = Value("true", "bool"),
            ["3 > 2"] = Value("true", "bool")
        };

        HexDebugEvaluator evaluator = Evaluator(answers);
        Assert.Equal(("true", "bool"), Said(evaluator.Evaluate("args.Length == 0", 0, Tweening).Result));
        Assert.Equal(("0", "int"), Said(evaluator.Evaluate("args.Length", 0, Tweening).Result));
        Assert.Equal(("true", "bool"), Said(evaluator.Evaluate("name.Length > 2 && args.Length == 0", 0, Tweening).Result));
    }

    [Fact]
    public void The_Length_of_what_a_call_comes_to_is_worked_out_too()
    {
        var answers = new Dictionary<string, object>
        {
            // What the real one said, it names the Length alone
            ["t.ToString().Length > 0"] = "error: The name 'Length' does not exist in the current context",
            ["t.ToString()"] = Value("\"0.5\"", "string"),
            ["3 > 0"] = Value("true", "bool")
        };

        Assert.Equal(("true", "bool"), Said(Evaluator(answers).Evaluate("t.ToString().Length > 0", 0, Tweening).Result));
        Assert.Equal((0, 9, 16), HexDebugExpressions.MemberOfCall("a.b(c)[0].Length", "Length") is var (start, dot, end) ? (start, dot, end) : default);
    }

    [Fact]
    public void A_member_a_value_hasnt_got_is_said_plainly_with_the_nearest_it_has()
    {
        var answers = new Dictionary<string, object>
        {
            ["Horizon.Testing.TestCatalog.Tests"] = Value("{Horizon.Testing.TestEntry[25]}", "Horizon.Testing.TestEntry[]", 4),
            ["box"] = Value("{Horizon.Testing.Box}", "Horizon.Testing.Box", 5),
            ["items"] = Value("Count = 2", "System.Collections.Generic.List<int>", 6)
        };
        var inside = new Dictionary<int, DebugVariable[]>
        {
            [5] = [new("Width", "4", "int", 0), new("Height", "2", "int", 0)],
            [6] = [new("Count", "2", "int", 0), new("Capacity", "4", "int", 0)]
        };

        HexDebugEvaluator evaluator = Evaluator(answers, inside: inside);
        Assert.Equal("debugging.eval_no_member_has|Count|TestEntry[]|Length", evaluator.Evaluate("TestCatalog.Tests.Count", 0, Tweening).Result.Error);
        Assert.Equal("debugging.eval_no_member_has|Widht|Box|Width", evaluator.Evaluate("box.Widht", 0, Tweening).Result.Error);
        Assert.Equal("debugging.eval_no_member|Depth|Box", evaluator.Evaluate("box.Depth", 0, Tweening).Result.Error);
        Assert.Equal("debugging.eval_no_member_has|Length|List<int>|Count", evaluator.Evaluate("items.Length", 0, Tweening).Result.Error);

        // A member it has that netcoredbg couldn't reach keeps netcoredbg's own words, nothing is said to be missing that isn't
        Assert.DoesNotContain("eval_no_member", evaluator.Evaluate("TestCatalog.Tests.Rank", 0, Tweening).Result.Error);
    }

    [Fact]
    public void And_and_or_stop_at_the_operand_that_decides_them_the_way_CSharp_does()
    {
        var asked = new List<string>();
        var answers = new Dictionary<string, object>
        {
            ["args"] = Value("{string[0]}", "string[]", 3),
            ["0 == 1"] = Value("false", "bool"),
            ["0 == 0"] = Value("true", "bool"),
            ["i"] = Value("4", "int")
        };

        HexDebugEvaluator evaluator = Evaluator(answers, asked);
        Assert.Equal(("false", "bool"), Said(evaluator.Evaluate("args.Length == 1 && args[0] == \"quickstart\"", 0, Tweening).Result));
        Assert.DoesNotContain(asked, expression => expression.Contains("quickstart"));
        Assert.Equal(("true", "bool"), Said(evaluator.Evaluate("args.Length == 1 || args.Length == 0", 0, Tweening).Result));
        Assert.Equal("debugging.eval_not_bool|i", evaluator.Evaluate("i && args.Length == 0", 0, Tweening).Result.Error);
        Assert.Null(HexDebugExpressions.LogicalParts("a ? b && c : d"));
        Assert.Equal([["a", "b"], ["c"]], HexDebugExpressions.LogicalParts("a && b || c"));
    }

    [Theory]
    [InlineData("args.Length == 0", null)]
    [InlineData("i is not null && name == \"a=b\"", null)]
    [InlineData("i >= 4 || i <= -1 || i != 2", null)]
    [InlineData("(int)ratio > 2", null)]
    [InlineData("i = 4", "debugging.condition_assignment")]
    [InlineData("(i == 4", "debugging.condition_brackets")]
    [InlineData("list[0)]", "debugging.condition_brackets")]
    [InlineData("name == \"abc", "debugging.condition_quote")]
    [InlineData("i ==", "debugging.condition_incomplete")]
    [InlineData("i 4", "debugging.condition_operator_missing")]
    public void A_condition_is_looked_over_when_its_set(string condition, string? problem) =>
        Assert.Equal(problem, HexDebugExpressions.ConditionProblem(condition));

    [Fact]
    public void A_files_scope_has_its_namespaces_innermost_first_and_its_usings()
    {
        Assert.Equal(["Horizon.Testing.Examples", "Horizon.Testing", "Horizon", "Horizon.Core.Tweening", "System"], Tweening.Namespaces);
        Assert.Equal(["Horizon.Core.Tweening.Ease"], Tweening.StaticTypes);
        Assert.Equal("Horizon.Input.Gamepad", Tweening.Aliases["Pad"]);
        Assert.Equal("7", Tweening.Constants["Limit"].Literal);
        Assert.Equal("2f", Tweening.Constants["Speed"].Literal);

        // Two declared at once, the second worked out from the first
        HexDebugScope both = HexDebugScope.Of("const float back = 1.70158f, backInOut = back * 1.525f;", []);
        Assert.Equal("1.70158f", both.Constants["back"].Literal);
        Assert.Equal("(back * 1.525f)", both.Constants["backInOut"].Literal);
        Assert.Equal(["System", "System.Linq"], HexDebugScope.GlobalUsingsIn("global using global::System;\nglobal using global::System.Linq;\nglobal using static global::System.Math;\n"));
    }

    [Fact]
    public void Expressions_come_apart_into_chains_and_calls()
    {
        List<HexDebugExpressions.Call> calls = HexDebugExpressions.Calls("Ease.Apply(Easing.OutQuad, Math.Max(a, \"x,y\".Length))");
        Assert.Equal(2, calls.Count);
        Assert.Equal(["Easing.OutQuad", "Math.Max(a, \"x,y\".Length)"], calls[0].Arguments);
        Assert.Equal(["a", "\"x,y\".Length"], calls[1].Arguments);

        Assert.Equal("Horizon.Easing.OutQuad + box.Easing", HexDebugExpressions.ReplaceHead("Easing.OutQuad + box.Easing", "Easing", "Horizon.Easing"));
        Assert.Equal("System.Int32.MaxValue - System.String.Empty.Length", HexDebugExpressions.KeywordTypes("int.MaxValue - string.Empty.Length"));
        Assert.Equal(["F(2L, -3L, x + 1)", "F(2f, -3f, x + 1)", "F(2d, -3d, x + 1)"], HexDebugExpressions.WiderNumbers("F(2, -3, x + 1)"));
        Assert.Null(HexDebugExpressions.TopCast("(int)x + 1"));
        Assert.Equal(("float", "(a + b)"), HexDebugExpressions.TopCast("(float)(a + b)"));
    }

    [Theory]
    [InlineData("Easing.OutQuad", "OutQuad", "Horizon.Core.Tweening.Easing", true)]
    [InlineData("mode", "OutQuad", "Horizon.Core.Tweening.Easing", false)]
    [InlineData("this.mode", "OutQuad", "Horizon.Core.Tweening.Easing", false)]
    [InlineData("Box.Width", "4", "int", false)]
    [InlineData("Outer.Kind.A", "A", "Outer+Kind", true)]
    public void An_enums_member_is_told_apart_from_a_variable_holding_one(string argument, string value, string type, bool member) =>
        Assert.Equal(member, HexDebugExpressions.IsEnumMember(argument, new DebugVariable(argument, value, type, 0)));

    [Theory]
    [InlineData("5", DebugHitKind.Equal, 5)]
    [InlineData("= 5", DebugHitKind.Equal, 5)]
    [InlineData(">=12", DebugHitKind.AtLeast, 12)]
    [InlineData("% 3", DebugHitKind.MultipleOf, 3)]
    [InlineData("multiple of 4", DebugHitKind.MultipleOf, 4)]
    public void A_hit_count_is_read_the_way_its_typed(string typed, DebugHitKind kind, int count) =>
        Assert.Equal(new DebugHitCondition(kind, count), HexDebugging.HitsIn(typed));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("< 4")]
    [InlineData("five")]
    public void Anything_else_isnt_a_hit_count(string typed) => Assert.Null(HexDebugging.HitsIn(typed));

    [Fact]
    public void Hit_counts_stop_on_the_hits_they_say()
    {
        Assert.Equal([3], Enumerable.Range(1, 10).Where(new DebugHitCondition(DebugHitKind.Equal, 3).Stops));
        Assert.Equal([8, 9, 10], Enumerable.Range(1, 10).Where(new DebugHitCondition(DebugHitKind.AtLeast, 8).Stops));
        Assert.Equal([3, 6, 9], Enumerable.Range(1, 10).Where(new DebugHitCondition(DebugHitKind.MultipleOf, 3).Stops));
    }

    [Fact]
    public void An_exit_code_says_its_hex_when_its_an_HRESULT()
    {
        Assert.Equal("3", HexDebugging.ExitCodeText(3));
        Assert.Equal("-532462766 (0xe0434352)", HexDebugging.ExitCodeText(unchecked((int)0xE0434352)));
    }
}
