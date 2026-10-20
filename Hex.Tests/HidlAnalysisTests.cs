using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// What Hex knows about a file of HIDL from its text (HidlAnalysis), which the editor and the language server both go
/// by: completion by what's being typed into, hovers, declarations, and what's wrong with a file.
/// </summary>
public sealed class HidlAnalysisTests
{
    private static readonly HexSaid Keys = (key, _) => key;

    [Fact]
    public void A_line_and_a_column_are_found_in_the_text()
    {
        const string text = "let a = 1;\nlet bee = 2;\n";
        Assert.Equal("let bee = 2;", HidlAnalysis.LineOf(text, 1));
        Assert.Equal(string.Empty, HidlAnalysis.LineOf(text, 7));
        Assert.Equal(15, HidlAnalysis.OffsetOf(text, 1, 4));
        Assert.Equal((4, 7), HidlAnalysis.WordAt("let bee = 2;", 5));
    }

    [Fact]
    public void Completing_a_name_offers_the_words_of_the_language_the_file_and_the_workspace()
    {
        const string text = "let health = 100;\nlet h";
        UICompletionRequest? offered = HidlAnalysis.Complete(text, 1, 5, ["hitbox"]);

        Assert.NotNull(offered);
        Assert.Equal(4, offered.Value.Start);
        Assert.Contains(offered.Value.Items, item => item is { Text: "let", Detail: "keyword" });
        Assert.Contains(offered.Value.Items, item => item is { Text: "health", Detail: "in this file" });
        Assert.Contains(offered.Value.Items, item => item is { Text: "hitbox", Detail: "in the workspace" });
    }

    [Fact]
    public void A_property_that_is_one_of_a_few_words_offers_the_words()
    {
        const string text = "let a = { anchor: \"to";
        UICompletionRequest? offered = HidlAnalysis.Complete(text, 0, text.Length);

        Assert.NotNull(offered);
        Assert.Contains(offered.Value.Items, item => item.Text == "top_left");
        Assert.DoesNotContain(offered.Value.Items, item => item.Text == "let");
    }

    [Fact]
    public void After_compositor_the_kinds_of_components_are_offered_and_inside_one_its_properties()
    {
        HidlAnalysis.KnowKinds();

        string kinds = "let a = compositor.lab";
        UICompletionRequest? offered = HidlAnalysis.Complete(kinds, 0, kinds.Length);
        Assert.NotNull(offered);
        Assert.Contains(offered.Value.Items, item => item is { Text: "label", Insert: "label({ " });

        string inside = "let a = compositor.label({ te";
        offered = HidlAnalysis.Complete(inside, 0, inside.Length);
        Assert.NotNull(offered);
        Assert.Contains(offered.Value.Items, item => item.Text == "text");
        Assert.Equal("parent", offered.Value.Items[0].Text);
    }

    [Fact]
    public void A_component_being_made_is_the_innermost_one_still_open()
    {
        const string text = "compositor.stack({ children: [compositor.label({ text: \"a\" }), ";
        Assert.Equal("stack", HidlAnalysis.ComponentBeingMade(text, text.Length));
        Assert.Equal("label", HidlAnalysis.ComponentBeingMade(text, text.IndexOf("text", StringComparison.Ordinal)));
        Assert.Null(HidlAnalysis.ComponentBeingMade("let a = { b: 1 };", 12));
    }

    [Fact]
    public void A_declaration_is_found_by_its_let_or_const()
    {
        const string text = "// the speed\nconst speed = 4;\nlet go = speed * 2;";
        Assert.Equal((1, 6), HidlAnalysis.FindDeclaration(text, "speed"));
        Assert.Null(HidlAnalysis.FindDeclaration(text, "slow"));
    }

    [Fact]
    public void A_hover_says_what_a_word_is()
    {
        const string text = "// @doc How fast it goes\nlet speed = 4;\nprint(speed);";

        string? declared = HidlAnalysis.Hover(text, 2, 7);
        Assert.NotNull(declared);
        Assert.Contains("made on line 2", declared);
        Assert.Contains("How fast it goes", declared);

        Assert.Contains("**print**", HidlAnalysis.Hover(text, 2, 2));
        Assert.Contains("**let**", HidlAnalysis.Hover(text, 1, 1));
        Assert.Null(HidlAnalysis.Hover(text, 1, 11));
    }

    [Fact]
    public void A_hover_takes_documentation_it_is_handed_for_a_name_the_file_doesnt_declare()
    {
        string? said = HidlAnalysis.Hover("print(far);", 0, 7, name => name == "far" ? new HidlDocumented("Somewhere else", "other.hor line 3") : null);
        Assert.NotNull(said);
        Assert.Contains("Somewhere else", said);
        Assert.Contains("other.hor line 3", said);
    }

    [Fact]
    public void Every_mistake_the_parser_finds_is_said_and_a_good_file_has_none()
    {
        Assert.Empty(HidlAnalysis.SyntaxErrors("let a = 1;\nlet b = { c: 2 };"));

        List<UICodeDiagnostic> found = HidlAnalysis.SyntaxErrors("let a = ;\nlet b = 2;\nlet = 3;");
        Assert.NotEmpty(found);
        Assert.All(found, diagnostic => Assert.False(diagnostic.Warning));
        Assert.Equal(0, found[0].Line);
    }

    [Fact]
    public void A_check_says_where_running_the_file_went_wrong()
    {
        // A name nobody made is null, and null times a number is where it goes wrong
        List<UICodeDiagnostic> found = HidlAnalysis.Check("let speed = 4;\nlet go = sped * 2;", null, new Dictionary<string, string>(), Keys);

        UICodeDiagnostic wrong = Assert.Single(found);
        Assert.Equal(1, wrong.Line);
        Assert.False(wrong.Warning);
        Assert.Contains("'*'", wrong.Message);
    }

    [Fact]
    public void A_check_of_a_layout_is_only_its_syntax()
    {
        Assert.Empty(HidlAnalysis.Check("#define layout\nlet a = compositor.label({ text: \"hi\" });", null, new Dictionary<string, string>(), Keys));
    }

    [Fact]
    public void A_check_lints_what_a_file_comes_to()
    {
        const string moves = """
            let moves = {
                jab: { damage: 3, startup: 4, recovery: 6 },
                hook: { damage: 6, startup: 7, recovery: 9 },
                kick: { damage: 5, startup: 6, recovery: 8 },
                sweep: { damage: 4, startup: 5, recovry: 10 }
            };
            """;
        List<UICodeDiagnostic> found = HidlAnalysis.Check(moves, null, new Dictionary<string, string>(), Keys);
        Assert.Contains(found, diagnostic => diagnostic.Warning && diagnostic.Line == 4);
    }
}
