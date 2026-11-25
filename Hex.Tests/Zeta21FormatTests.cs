using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Zeta 2.1's prompt, as Zed's own sample has it: the region in chunks behind numbered markers, the caret marked, the
/// last marker right after the region; and its answer, the chunks it rewrote put back in their place.
/// </summary>
public sealed class Zeta21FormatTests
{
    private static readonly IPredictionFormat format = PredictionFormats.Named(PredictionFormats.ZETA21)!;

    // A file of 30 short lines, the caret on line 15 after "value"
    private static (PredictionInput Input, string Text) File30()
    {
        var lines = Enumerable.Range(1, 30).Select(i => i == 15 ? "    var value = 15;" : $"    int line{i} = {i};").ToList();
        string text = "class A\n{\n" + string.Join('\n', lines) + "\n}\n";
        int caret = text.IndexOf("value", StringComparison.Ordinal) + "value".Length;
        return (new PredictionInput("src/A.cs", "csharp", text, caret, [], []), text);
    }

    [Fact]
    public void The_region_is_cut_into_numbered_chunks_with_the_caret_marked()
    {
        var (input, _) = File30();
        PredictionPrompt prompt = format.Build(input, 256)!;
        string sent = prompt.Request.Prompt;

        Assert.StartsWith("<[fim-suffix]>", sent);
        Assert.EndsWith("<[fim-middle]>", sent);
        Assert.Contains("<filename>src/A.cs\n", sent);
        Assert.Contains("value<|user_cursor|> = 15;", sent);
        Assert.Contains("<|marker_1|>", sent);
        Assert.Equal(PredictionFormats.ZETA21, prompt.Request.Format);

        // Each marker but the last begins a line of the region, the last comes right after it, then the middle token
        int[] bounds = Zeta2Format.Chunks(prompt.Region);
        string last = Zeta2Format.Marker(bounds.Length);
        Assert.Contains(last + "\n<[fim-middle]>", sent);
        Assert.Equal(bounds.Length, System.Text.RegularExpressions.Regex.Matches(sent, @"<\|marker_\d+\|>").Count);
    }

    [Fact]
    public void The_chunks_written_back_replace_theirs_and_the_rest_stays()
    {
        var (input, text) = File30();
        PredictionPrompt prompt = format.Build(input, 256)!;
        int[] bounds = Zeta2Format.Chunks(prompt.Region);
        int chunk = Array.FindLastIndex(bounds, at => prompt.RegionStart + at <= prompt.Caret) + 1;
        string old = prompt.Region[bounds[chunk - 1]..bounds[chunk]];
        string rewritten = old.Replace("var value = 15;", "var total = 15;", StringComparison.Ordinal);

        string answer = Zeta2Format.Marker(chunk) + rewritten.Replace("total", "total<|user_cursor|>", StringComparison.Ordinal) + Zeta2Format.Marker(chunk + 1);
        PredictionParsed parsed = format.Parse(prompt, answer, complete: true);

        PredictionEdit edit = Assert.Single(parsed.Edits);
        Assert.Equal("total", edit.Text);
        Assert.Equal("value", text[edit.Start..edit.End]);
        // The caret where the model marked it, after "total", in the text once the edit is made
        Assert.Equal(prompt.RegionStart + prompt.Region.IndexOf("value", StringComparison.Ordinal) + "total".Length, parsed.CaretAfter);
        Assert.True(parsed.Done);
    }

    [Fact]
    public void An_answer_without_its_closing_marker_waits_or_is_no_prediction()
    {
        var (input, _) = File30();
        PredictionPrompt prompt = format.Build(input, 256)!;
        Assert.Same(PredictionParsed.Unsettled, format.Parse(prompt, "<|marker_1|>    int line", complete: false));
        Assert.Same(PredictionParsed.Nothing, format.Parse(prompt, "<|marker_1|>    int line", complete: true));
        Assert.Same(PredictionParsed.Nothing, format.Parse(prompt, "<|marker_2|>x<|marker_1|>", complete: true));
        Assert.Same(PredictionParsed.Nothing, format.Parse(prompt, "<|marker_1|>x<|marker_99|>", complete: true));
    }

    [Fact]
    public void Zeta_2_1_is_inferred_from_its_name_before_zeta_2()
    {
        Assert.Equal(PredictionFormats.ZETA21, PredictionFormats.Infer("zeta-2.1").Name);
        Assert.Equal(PredictionFormats.ZETA21, PredictionFormats.Infer("mradermacher/zeta-2.1-i1-GGUF:Q4_K_M").Name);
        Assert.Equal(PredictionFormats.ZETA2, PredictionFormats.Infer("hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M").Name);
    }
}
