using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Changes from the designer written into a layout somebody wrote by hand (HexLayoutEdits), the handlers, the statements
/// after a declaration and the comments inside the braces all kept, which writing the layout out again threw away.
/// </summary>
public sealed class HexLayoutEditsTests
{
    private const string SAMPLE = """
        // A panel written by hand
        #define layout

        let panel = compositor.stack({
            color: vec(0.1, 0.12, 0.17, 0.92),
            // round corners
            radius: 10,
            padding: 24,
        });

        let heading = compositor.label({ parent: panel, text: "Hello" });

        let less = compositor.button({
            parent: panel,
            label: "Less",
            on_pressed: func() {
                heading.text = "less";
            }
        });

        let more = compositor.button({ parent: panel, label: "More" });
        more.on_pressed = func() {
            heading.text = "more";
        };
        """;

    private static readonly string Sample = SAMPLE.ReplaceLineEndings("\n") + "\n";

    private static HexLayoutPart Part(string name, string kind, string declaration, params (string Key, string Value)[] properties) =>
        new(name, kind, [.. properties.Select(property => new KeyValuePair<string, string>(property.Key, property.Value))], declaration);

    // What the designer sees of the sample, the handlers aren't anything it can write
    private static readonly HexLayoutShape Before = new([
        Part("panel", "stack", "", ("color", "vec(0.1, 0.12, 0.17, 0.92)"), ("radius", "10"), ("padding", "24")),
        Part("heading", "label", "", ("parent", "panel"), ("text", "\"Hello\"")),
        Part("less", "button", "", ("parent", "panel"), ("label", "\"Less\"")),
        Part("more", "button", "", ("parent", "panel"), ("label", "\"More\""))
    ], null, null);

    private static HexLayoutShape With(HexLayoutShape shape, string name, Func<HexLayoutPart, HexLayoutPart> change) =>
        shape with { Parts = [.. shape.Parts.Select(part => part.Name == name ? change(part) : part)] };

    private static HexLayoutPart Setting(HexLayoutPart part, string key, string? value)
    {
        var properties = part.Properties.Where(property => property.Key != key).ToList();
        int at = part.Properties.ToList().FindIndex(property => property.Key == key);
        if (value is not null)
            properties.Insert(at < 0 ? properties.Count : at, new(key, value));
        return part with { Properties = properties };
    }

    private static string? Rewrite(HexLayoutShape after, params (string, string)[] renames) =>
        HexLayoutEdits.Rewrite(Sample, Before, after, renames, (Sample, Before), out _);

    [Fact]
    public void NothingChangedIsTheSameText()
    {
        Assert.Equal(Sample, Rewrite(Before));
    }

    [Fact]
    public void AValueIsWrittenWhereItIsAndNothingElseMoves()
    {
        string? edited = Rewrite(With(Before, "panel", part => Setting(part, "radius", "16")));
        Assert.Equal(Sample.Replace("radius: 10,", "radius: 16,"), edited);
    }

    [Fact]
    public void AValuePutBackIsWrittenTheWayItWas()
    {
        HexLayoutShape moved = With(Before, "panel", part => Setting(part, "color", "vec(1, 1, 1, 1)"));
        string? there = HexLayoutEdits.Rewrite(Sample, Before, moved, [], (Sample, Before), out _);
        string? back = HexLayoutEdits.Rewrite(there!, moved, Before, [], (Sample, Before), out _);
        Assert.Equal(Sample, back);
    }

    [Fact]
    public void AValueOnAComponentWithAHandlerKeepsTheHandler()
    {
        string? edited = Rewrite(With(Before, "less", part => Setting(part, "label", "\"Fewer\"")));
        Assert.NotNull(edited);
        Assert.Contains("label: \"Fewer\",\n    on_pressed: func() {\n        heading.text = \"less\";", edited);
        Assert.Contains("more.on_pressed = func() {", edited);
    }

    [Fact]
    public void AKeyPutInGoesAfterTheOneBeforeItWithTheCommasTheBracesHave()
    {
        string? edited = Rewrite(With(Before, "panel", part => part with { Properties = [.. part.Properties, new("spacing", "16")] }));
        Assert.Equal(Sample.Replace("    padding: 24,\n});", "    padding: 24,\n    spacing: 16,\n});"), edited);

        string? oneLine = Rewrite(With(Before, "heading", part => part with { Properties = [.. part.Properties, new("text_scale", "0.5")] }));
        Assert.Contains("compositor.label({ parent: panel, text: \"Hello\", text_scale: 0.5 });", oneLine);
    }

    [Fact]
    public void AKeyTakenOutTakesItsCommentWithIt()
    {
        string? edited = Rewrite(With(Before, "panel", part => Setting(part, "radius", null)));
        Assert.Equal(Sample.Replace("    // round corners\n    radius: 10,\n", ""), edited);
    }

    [Fact]
    public void ANewComponentGoesAfterTheOneBeforeItAndComesOutAgain()
    {
        var added = Part("note", "label", "let note = compositor.label({ parent: panel, text: \"New\" });\n", ("parent", "panel"), ("text", "\"New\""));
        HexLayoutShape after = Before with { Parts = [.. Before.Parts.Take(2), added, .. Before.Parts.Skip(2)] };

        string? edited = Rewrite(after);
        Assert.NotNull(edited);
        Assert.Contains("let heading = compositor.label({ parent: panel, text: \"Hello\" });\n\nlet note = compositor.label({ parent: panel, text: \"New\" });\n\nlet less", edited);

        string? back = HexLayoutEdits.Rewrite(edited!, after, Before, [], null, out _);
        Assert.Equal(Sample, back);
    }

    [Fact]
    public void AComponentTakenOutTakesWhatWasSaidToItWithIt()
    {
        HexLayoutShape after = Before with { Parts = [.. Before.Parts.Where(part => part.Name != "more")] };
        string? edited = Rewrite(after);
        Assert.NotNull(edited);
        Assert.DoesNotContain("more", edited);
        Assert.Contains("let less", edited);
    }

    [Fact]
    public void AComponentUsedElsewhereIsntTakenOut()
    {
        HexLayoutShape after = Before with { Parts = [.. Before.Parts.Where(part => part.Name != "heading")] };
        Assert.Null(HexLayoutEdits.Rewrite(Sample, Before, after, [], null, out string why));
        Assert.Contains("heading", why);
    }

    [Fact]
    public void ARenameRenamesTheUsesAndNotTheKeys()
    {
        HexLayoutShape after = Before.Renamed("heading", "title");
        string? edited = Rewrite(after, ("heading", "title"));
        Assert.NotNull(edited);
        Assert.Contains("let title = compositor.label(", edited);
        Assert.Contains("title.text = \"less\";", edited);
        Assert.DoesNotContain("heading", edited);
    }

    [Fact]
    public void AnUndeclaredComponentIsTurnedDown()
    {
        var unnamed = Before with { Parts = [.. Before.Parts, Part("button1", "button", "", ("parent", "panel"))] };
        HexLayoutShape after = With(unnamed, "button1", part => Setting(part, "label", "\"x\""));
        Assert.Null(HexLayoutEdits.Rewrite(Sample, unnamed, after, [], null, out string why));
        Assert.Contains("button1", why);
    }

    [Fact]
    public void MovedBeforeTheOneItFollowedIsMovedWithWhatWasSaidToIt()
    {
        HexLayoutPart[] parts = [.. Before.Parts];
        HexLayoutShape after = Before with { Parts = [parts[0], parts[1], parts[3], parts[2]] };
        string? edited = Rewrite(after);
        Assert.NotNull(edited);
        int more = edited!.IndexOf("let more", StringComparison.Ordinal), less = edited.IndexOf("let less", StringComparison.Ordinal);
        Assert.True(more < less);
        Assert.True(edited.IndexOf("more.on_pressed", StringComparison.Ordinal) < less);
    }

    [Fact]
    public void TheDesignCallIsPutInUnderTheDefine()
    {
        string? edited = Rewrite(Before with { Design = "compositor.design({ size: vec(1600, 900), fit: \"contain\" });" });
        Assert.Contains("#define layout\n\ncompositor.design({ size: vec(1600, 900), fit: \"contain\" });\n\nlet panel", edited);
    }

    [Fact]
    public void StatementsAreFoundWithTheirKeys()
    {
        List<HexStatement> statements = HexLayoutText.Statements(Sample);
        Assert.Equal(5, statements.Count);
        Assert.Equal(["color", "radius", "padding"], statements[0].Entries.Select(entry => entry.Key));
        Assert.Equal("more", statements[4].Subject);
        Assert.Equal(["parent", "label", "on_pressed"], statements[2].Entries.Select(entry => entry.Key));
    }
}
