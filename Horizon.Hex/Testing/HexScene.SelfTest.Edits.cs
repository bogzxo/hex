using Horizon.HIDL.Runtime;

namespace Horizon.Hex;

// The self test, edits by path (HexSourceEdits under hex.edit and the rest) on a file of moves with comments in the
// awkward places. Nothing on screen, the text in and the text out, and every one has to run afterwards.
internal sealed partial class HexScene
{
    private const string EDITS_SAMPLE = """
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

    private void TestEdits(HexPointerScript test)
    {
        // One edit made on a text, null when it didn't go
        static string? Made(string text, HexEdit edit) =>
            edit(text, HexSourceTree.Read(text, null, (_, _) => string.Empty), out _);

        // What the text comes to at a path, after it is run again
        static IRuntimeValue? At(string? text, params string[] path) =>
            text is null ? null : HexSourceTree.Read(text, null, (_, _) => string.Empty).Find(path, out _)?.Value;

        static IEnumerable<string> KeysAt(string? text, params string[] path) =>
            At(text, path) is ObjectValue of ? of.Properties.Keys : [];

        string sample = EDITS_SAMPLE.ReplaceLineEndings("\n");

        test.Check("edits: a value changed where it is written, the comment after it kept", () =>
            Made(sample, EditOf(["moves", "jab", "damage"], new NumberValue(7))) is { } made
            && made.Contains("damage: 7, // light") && At(made, "moves", "jab", "damage") is NumberValue { Value: 7 });

        test.Check("edits: a key into an object over lines, at the indentation of the others", () =>
            Made(sample, InsertOf(["moves", "jab"], "level", new StringValue("low"), "damage")) is { } made
            && made.Contains("        damage: 5, // light\n        level: \"low\",\n") && At(made, "moves", "jab", "level") is StringValue { Value: "low" });

        test.Check("edits: and into one on one line, after its last key", () =>
            Made(sample, InsertOf(["moves", "kick"], "level", new StringValue("mid"), null)) is { } made
            && made.Contains("kick: { damage: 8, level: \"mid\" }"));

        test.Check("edits: a key taken out, its line with it", () =>
            Made(sample, RemoveOf(["moves", "jab", "input"])) is { } made
            && !made.Contains("input") && made.Contains("    jab: {\n        damage: 5,") && At(made, "moves", "jab", "damage") is not null);

        test.Check("edits: the last key taken out, the comma before it with it", () =>
            Made(sample, RemoveOf(["moves", "jab", "phases", "back"])) is { } made
            && made.Contains("hit: 2 }\n        }") && KeysAt(made, "moves", "jab", "phases").SequenceEqual(["swing"]));

        test.Check("edits: an object's keys in another order, the commas where they belong", () =>
            Made(sample, ReorderOf(["moves", "jab", "phases"], ["back", "swing"])) is { } made
            && made.Contains("back: { frames: 4 },\n            swing:") && KeysAt(made, "moves", "jab", "phases").SequenceEqual(["back", "swing"]));

        test.Check("edits: a key renamed, the comment over it staying over it", () =>
            Made(sample, RenameOf(["moves", "jab"], "light_punch")) is { } made
            && made.Contains("// a quick one\n    light_punch: {") && At(made, "moves", "light_punch", "damage") is not null);

        test.Check("edits: a new move written over lines the way the file is", () =>
        {
            var phase = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["animation"] = new StringValue("kick"), ["frames"] = new NumberValue(8) });
            var move = new ObjectValue(new Dictionary<string, IRuntimeValue>
            {
                ["input"] = new StringValue("B"),
                ["damage"] = new NumberValue(9),
                ["phases"] = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["swing"] = phase })
            });

            return Made(sample, InsertOf(["moves"], "sweep", move, null)) is { } made
                && made.Contains("    sweep: {\n        input: \"B\",\n        damage: 9,\n        phases: {\n            swing: { animation: \"kick\", frames: 8 }\n        }\n    }\n};")
                && At(made, "moves", "sweep", "phases", "swing", "frames") is NumberValue { Value: 8 };
        });

        test.Check("edits: a move taken out with the comment over it", () =>
            Made(sample, RemoveOf(["moves", "jab"])) is { } made
            && !made.Contains("a quick one") && KeysAt(made, "moves").SequenceEqual(["kick"]));

        test.Check("edits: what isn't there says so instead of changing anything", () =>
            Made(sample, EditOf(["moves", "uppercut", "damage"], new NumberValue(1))) is null);
    }
}
