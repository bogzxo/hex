using Hex.Dawn;

using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// Edits by path (HexValueEdits, under the extensions' hex.edit and the rest) on a file of moves with comments in the
/// awkward places. The text in and the text out, and every one has to run afterwards.
/// </summary>
public sealed class HexValueEditsTests
{
    private const string SAMPLE = """
        // The moves of somebody
        let moves = {
            // a quick one
            jab: {
                input: "Y",
                damage: 5, // light
                phases: {
                    swing: { animation: "punch", frames: 6, hit: 2 },
                    back: { frames: 4 }
                }
            },
            kick: { damage: 8 }
        };
        """;

    private static readonly string Sample = SAMPLE.ReplaceLineEndings("\n");

    // One edit made on a text, null when it didn't go
    private static string? Made(string text, HexEdit edit) =>
        edit(text, HexSourceTree.Read(text, null, (_, _) => string.Empty), out _);

    // What the text comes to at a path, after it is run again
    private static IRuntimeValue? At(string? text, params string[] path) =>
        text is null ? null : HexSourceTree.Read(text, null, (_, _) => string.Empty).Find(path, out _)?.Value;

    private static IEnumerable<string> KeysAt(string? text, params string[] path) =>
        At(text, path) is ObjectValue of ? of.Properties.Keys : [];

    [Fact]
    public void A_value_is_changed_where_it_is_written_the_comment_after_it_kept()
    {
        string? made = Made(Sample, HexValueEdits.EditOf(["moves", "jab", "damage"], new NumberValue(7)));
        Assert.NotNull(made);
        Assert.Contains("damage: 7, // light", made);
        Assert.Equal(7, Assert.IsType<NumberValue>(At(made, "moves", "jab", "damage")).Value);
    }

    [Fact]
    public void A_key_goes_into_an_object_over_lines_at_the_indentation_of_the_others()
    {
        string? made = Made(Sample, HexValueEdits.InsertOf(["moves", "jab"], "level", new StringValue("low"), "damage"));
        Assert.NotNull(made);
        Assert.Contains("        damage: 5, // light\n        level: \"low\",\n", made);
        Assert.Equal("low", Assert.IsType<StringValue>(At(made, "moves", "jab", "level")).Value);
    }

    [Fact]
    public void A_key_goes_into_an_object_on_one_line_after_its_last_key()
    {
        Assert.Contains("kick: { damage: 8, level: \"mid\" }", Made(Sample, HexValueEdits.InsertOf(["moves", "kick"], "level", new StringValue("mid"), null)));
    }

    [Fact]
    public void A_key_taken_out_takes_its_line_with_it()
    {
        string? made = Made(Sample, HexValueEdits.RemoveOf(["moves", "jab", "input"]));
        Assert.NotNull(made);
        Assert.DoesNotContain("input", made);
        Assert.Contains("    jab: {\n        damage: 5,", made);
        Assert.NotNull(At(made, "moves", "jab", "damage"));
    }

    [Fact]
    public void The_last_key_taken_out_takes_the_comma_before_it()
    {
        string? made = Made(Sample, HexValueEdits.RemoveOf(["moves", "jab", "phases", "back"]));
        Assert.NotNull(made);
        Assert.Contains("hit: 2 }\n        }", made);
        Assert.Equal(["swing"], KeysAt(made, "moves", "jab", "phases"));
    }

    [Fact]
    public void An_objects_keys_are_put_in_another_order_with_the_commas_where_they_belong()
    {
        string? made = Made(Sample, HexValueEdits.ReorderOf(["moves", "jab", "phases"], ["back", "swing"]));
        Assert.NotNull(made);
        Assert.Contains("back: { frames: 4 },\n            swing:", made);
        Assert.Equal(["back", "swing"], KeysAt(made, "moves", "jab", "phases"));
    }

    [Fact]
    public void A_key_renamed_keeps_the_comment_over_it()
    {
        string? made = Made(Sample, HexValueEdits.RenameOf(["moves", "jab"], "light_punch"));
        Assert.NotNull(made);
        Assert.Contains("// a quick one\n    light_punch: {", made);
        Assert.NotNull(At(made, "moves", "light_punch", "damage"));
    }

    [Fact]
    public void A_new_move_is_written_over_lines_the_way_the_file_is()
    {
        var phase = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["animation"] = new StringValue("kick"), ["frames"] = new NumberValue(8) });
        var move = new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["input"] = new StringValue("B"),
            ["damage"] = new NumberValue(9),
            ["phases"] = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["swing"] = phase })
        });

        string? made = Made(Sample, HexValueEdits.InsertOf(["moves"], "sweep", move, null));
        Assert.NotNull(made);
        Assert.Contains("    sweep: {\n        input: \"B\",\n        damage: 9,\n        phases: {\n            swing: { animation: \"kick\", frames: 8 }\n        }\n    }\n};", made);
        Assert.Equal(8, Assert.IsType<NumberValue>(At(made, "moves", "sweep", "phases", "swing", "frames")).Value);
    }

    [Fact]
    public void A_move_taken_out_takes_the_comment_over_it()
    {
        string? made = Made(Sample, HexValueEdits.RemoveOf(["moves", "jab"]));
        Assert.NotNull(made);
        Assert.DoesNotContain("a quick one", made);
        Assert.Equal(["kick"], KeysAt(made, "moves"));
    }

    [Fact]
    public void What_isnt_there_says_so_instead_of_changing_anything()
    {
        Assert.Null(Made(Sample, HexValueEdits.EditOf(["moves", "uppercut", "damage"], new NumberValue(1))));
    }

    [Fact]
    public void A_batch_goes_one_edit_after_the_other_and_stops_at_the_first_that_cant()
    {
        string? both = HexValueEdits.Apply(Sample, null, (_, _) => string.Empty,
            [HexValueEdits.EditOf(["moves", "kick", "damage"], new NumberValue(10)), HexValueEdits.RenameOf(["moves", "kick"], "boot")], out _);
        Assert.Equal(10, Assert.IsType<NumberValue>(At(both, "moves", "boot", "damage")).Value);

        string? none = HexValueEdits.Apply(Sample, null, (_, _) => string.Empty,
            [HexValueEdits.EditOf(["moves", "kick", "damage"], new NumberValue(10)), HexValueEdits.RemoveOf(["moves", "nothing"])], out string why);
        Assert.Null(none);
        Assert.Contains("nothing", why);
    }
}
