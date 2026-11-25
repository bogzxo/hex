using Hex.Core;

namespace Hex.Tests;

/// <summary>A prediction at a caret after the line's indentation doesn't write that indentation twice.</summary>
public sealed class PredictionIndentTests
{
    private const string Text = "def f():\n    ";

    private static PredictionInput Input() => new("a.py", "python", Text, Text.Length, [], []);

    private static string Apply(IReadOnlyList<PredictionEdit> edits)
    {
        string result = Text;
        foreach (PredictionEdit edit in edits.OrderByDescending(e => e.Start))
            result = result[..edit.Start] + edit.Text + result[edit.End..];
        return result;
    }

    [Theory]
    [InlineData("    return 1")]
    [InlineData("        return 1")]
    public void Zeta_gives_one_indentation(string line)
    {
        var zeta = new Zeta2Format();
        PredictionPrompt prompt = zeta.Build(Input(), 256)!;

        PredictionParsed parsed = zeta.Parse(prompt, "def f():\n" + line + "\n" + Zeta2Format.UPDATED, true);

        Assert.Equal("def f():\n    return 1", Apply(parsed.Edits));
    }

    [Theory]
    [InlineData("    return 1")]
    [InlineData("        return 1")]
    public void Chat_gives_one_indentation(string line)
    {
        PredictionPrompt prompt = ChatEditFormat.Near.Build(Input(), 256)!;

        PredictionParsed parsed = ChatEditFormat.Near.Parse(prompt, "<region>\ndef f():\n" + line + "\n</region>", true);

        Assert.Equal("def f():\n    return 1", Apply(parsed.Edits));
    }

    [Theory]
    [InlineData("return 1")]
    [InlineData("    return 1")]
    public void Fim_gives_one_indentation(string answer)
    {
        PredictionPrompt prompt = FimFormat.Qwen.Build(Input(), 256)!;

        PredictionParsed parsed = FimFormat.Qwen.Parse(prompt, answer, true);

        Assert.Equal("def f():\n    return 1", Apply(parsed.Edits));
        Assert.Equal(Text.Length + "return 1".Length, parsed.CaretAfter);
    }

    [Fact]
    public void A_caret_after_code_is_left_alone()
    {
        var edits = new List<PredictionEdit> { new(5, 5, "  x") };
        int? caret = null;

        Assert.Same(edits, PredictionDiff.WithoutRepeatedIndent(edits, "ab cd", 0, 5, ref caret));
    }
}
