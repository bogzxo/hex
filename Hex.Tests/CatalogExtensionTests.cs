using System.Diagnostics;
using System.Text;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// The extensions of the catalog that came after Emmet (Text tools, Snippets, Pretty JSON and the TODO list), each run
/// against a <see cref="FakeHex"/> with a file on screen, the way Dawn runs it, and what they did to the file read back.
/// </summary>
public class CatalogExtensionTests
{
    /* Text tools */

    private static FakeHex Text(string text, (int, int) caret, (int, int)? anchor = null)
    {
        var hex = FakeHex.Load("text_tools");
        hex.Text = text;
        hex.Caret = caret;
        hex.Anchor = anchor ?? caret;
        return hex;
    }

    [Fact]
    public void Sort_lines_sorts_the_selected_lines_and_leaves_a_line_the_selection_only_touches_the_start_of()
    {
        var hex = Text("keep\npear\napple\nfig\nlast", caret: (5, 1), anchor: (2, 1));
        hex.Run("Sort lines");
        Assert.Equal("keep\napple\nfig\npear\nlast", hex.Text);
        Assert.Equal("apple\nfig\npear", hex.Selected());
    }

    [Fact]
    public void With_nothing_selected_the_line_commands_work_on_the_whole_file()
    {
        var hex = Text("b\nA\na\nb", caret: (1, 1));
        hex.Run("Sort lines, ignoring case");
        Assert.Equal("A\na\nb\nb", hex.Text);
        hex.Run("Unique lines");
        Assert.Equal("A\na\nb", hex.Text);
        hex.Run("Reverse lines");
        Assert.Equal("b\na\nA", hex.Text);
    }

    [Fact]
    public void Join_lines_with_nothing_selected_joins_the_line_and_the_one_under_it()
    {
        var hex = Text("let x = [\n    1,\n    2\n];", caret: (2, 3));
        hex.Run("Join lines");
        Assert.Equal("let x = [\n    1, 2\n];", hex.Text);
    }

    [Fact]
    public void Align_lines_on_lines_up_what_was_typed()
    {
        var hex = Text("a = 1\nlong_name = 2\nno equals here", caret: (3, 15), anchor: (1, 1));
        hex.Answer = "=";
        hex.Run("Align lines on...");
        Assert.Equal("a         = 1\nlong_name = 2\nno equals here", hex.Text);
    }

    [Theory]
    [InlineData("snake_case", "fooBarBaz", "foo_bar_baz")]
    [InlineData("snake_case", "HTTPServerError", "http_server_error")]
    [InlineData("kebab-case", "fooBar", "foo-bar")]
    [InlineData("camelCase", "foo_bar_baz", "fooBarBaz")]
    [InlineData("PascalCase", "foo_bar", "FooBar")]
    [InlineData("UPPER CASE", "loud", "LOUD")]
    public void A_case_command_changes_the_word_the_caret_is_in(string command, string word, string expected)
    {
        var hex = Text($"x = {word};", caret: (1, 6));
        hex.Run(command);
        Assert.Equal($"x = {expected};", hex.Text);
        Assert.Equal(expected, hex.Selected());
    }

    [Fact]
    public void Title_case_changes_what_is_selected()
    {
        var hex = Text("the quick brown fox", caret: (1, 20), anchor: (1, 5));
        hex.Run("Title Case");
        Assert.Equal("the Quick Brown Fox", hex.Text);
    }

    [Theory]
    [InlineData("(", "say (hello) there")]
    [InlineData("\"", "say \"hello\" there")]
    [InlineData("<span class=\"x\">", "say <span class=\"x\">hello</span> there")]
    public void Wrap_selection_with_puts_the_pair_round_it(string open, string expected)
    {
        var hex = Text("say hello there", caret: (1, 10), anchor: (1, 5));
        hex.Answer = open;
        hex.Run("Wrap selection with...");
        Assert.Equal(expected, hex.Text);
    }

    [Fact]
    public void An_older_hex_is_told_what_it_lacks_rather_than_the_extension_going_wrong()
    {
        var hex = FakeHex.Load("text_tools", old: true);
        hex.Text = "b\na";
        hex.Run("Sort lines");
        Assert.Equal("b\na", hex.Text);
        Assert.Contains(hex.Said, said => said.Contains("newer Hex", StringComparison.Ordinal));
    }

    /* Snippets */

    private static FakeHex Snippets(string file, string text, (int, int) caret)
    {
        var hex = FakeHex.Load("snippets", before: hex =>
        {
            hex.Settings["indent"] = new NumberValue(4);
            hex.Settings["file"] = new StringValue(string.Empty);
        });
        hex.File = file;
        hex.Text = text;
        hex.Caret = hex.Anchor = caret;
        return hex;
    }

    private static bool Tab(FakeHex hex) => hex.Call("on_tab", hex.Object, new StringValue(hex.File)) is BooleanValue { Value: true };

    [Fact]
    public void A_body_is_read_into_its_text_and_the_places_tab_goes_in_order()
    {
        var hex = Snippets("a.js", string.Empty, (1, 1));
        var made = (ObjectValue)hex.Call("parse_body", new StringValue("for (${1:i} = 0; ${1:i} < ${2:n}; $0)\\$"), new StringValue("    "));
        Assert.Equal("for (i = 0; i < n; )$", Values.Text(made.Properties["text"]));
        var places = ((ListValue)made.Properties["places"]).Items.Cast<ObjectValue>().Select(place => ((int)((NumberValue)place.Properties["offset"]).Value, (int)((NumberValue)place.Properties["length"]).Value)).ToArray();
        Assert.Equal([(5, 1), (16, 1), (19, 0)], places);
    }

    [Fact]
    public void Tab_after_a_trigger_writes_it_out_indented_with_its_first_place_selected_then_goes_from_place_to_place()
    {
        var hex = Snippets("C:/site/app.js", "    fn", (1, 7));
        Assert.True(Tab(hex));
        Assert.Equal("    function name() {\n        \n    }", hex.Text);
        Assert.Equal("name", hex.Selected());

        hex.Type("go");
        Assert.True(Tab(hex));
        Assert.Equal((1, 17), hex.Caret);

        hex.Type("a, b");
        Assert.True(Tab(hex));
        Assert.Equal("    function go(a, b) {\n        \n    }", hex.Text);
        Assert.Equal((2, 9), hex.Caret);

        // At $0 the run is over, Tab is a Tab again (nothing before the caret is a trigger)
        Assert.False(Tab(hex));
    }

    [Fact]
    public void Tab_is_left_alone_after_a_word_that_isnt_a_snippet_or_in_a_file_with_none()
    {
        Assert.False(Tab(Snippets("C:/site/app.js", "hello", (1, 6))));
        Assert.False(Tab(Snippets("C:/notes.txt", "fn", (1, 3))));
    }

    [Fact]
    public void Your_own_snippets_win_over_the_ones_it_came_with()
    {
        var hex = Snippets("C:/site/app.js", "fn", (1, 3));
        hex.OwnSnippets = Natives.Object(("*", Natives.Object(("fn", new StringValue("fun ${1:x}")))));
        Assert.True(Tab(hex));
        Assert.Equal("fun x", hex.Text);
    }

    [Fact]
    public void Insert_snippet_lists_the_files_snippets_and_writes_the_one_picked_at_the_caret()
    {
        var hex = Snippets("C:/tool.py", "x = 1\n", (2, 1));
        hex.Run("Insert snippet...");
        hex.Press("pr ");
        Assert.Equal("x = 1\nprint()", hex.Text);
        Assert.Equal((2, 7), hex.Caret);
    }

    /* Pretty JSON */

    private static FakeHex Json(string text, bool trailingCommas = true)
    {
        var hex = FakeHex.Load("pretty_json", before: hex =>
        {
            hex.Settings["indent"] = new NumberValue(4);
            hex.Settings["check_on_save"] = Values.True;
            hex.Settings["trailing_commas"] = trailingCommas ? Values.True : Values.False;
        });
        hex.File = "C:/data/thing.json";
        hex.Text = text;
        return hex;
    }

    [Fact]
    public void Format_json_spreads_it_out_with_every_number_and_text_as_written()
    {
        var hex = Json("{\"b\":[1.50e+3,true,null,{}],\"a\":\"x\\\"y\",\"c\":[]}");
        hex.Run("Format JSON");
        Assert.Equal("{\n    \"b\": [\n        1.50e+3,\n        true,\n        null,\n        {}\n    ],\n    \"a\": \"x\\\"y\",\n    \"c\": []\n}\n", hex.Text);

        hex.Run("Format JSON, keys sorted");
        Assert.StartsWith("{\n    \"a\": ", hex.Text, StringComparison.Ordinal);

        hex.Run("Minify JSON");
        Assert.Equal("{\"a\":\"x\\\"y\",\"b\":[1.50e+3,true,null,{}],\"c\":[]}", hex.Text);
    }

    [Theory]
    [InlineData("{\n  \"a\": 1,\n}", "line 3, column 1")]
    [InlineData("[1, 2", "line 1, column 6")]
    [InlineData("{\"a\" 1}", "line 1, column 6")]
    public void What_isnt_json_is_said_where_and_left_as_it_is(string text, string where)
    {
        // Strict, so the comma before the } counts as wrong
        var hex = Json(text, trailingCommas: false);
        hex.Run("Format JSON");
        Assert.Equal(text, hex.Text);
        Assert.Contains(hex.Said, said => said.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void A_comma_after_the_last_item_is_fine_and_formatted_away()
    {
        var hex = Json("{\n  \"a\": [1, 2,],\n  \"b\": {\"c\": true,},\n}");
        hex.Run("Check JSON");
        Assert.Contains(hex.Said, said => said.Contains("strict JSON wouldn't take", StringComparison.Ordinal));

        hex.Call("on_save", hex.Object, new StringValue(hex.File));
        Assert.Empty(hex.Problems);

        hex.Run("Format JSON");
        Assert.Equal("{\n    \"a\": [\n        1,\n        2\n    ],\n    \"b\": {\n        \"c\": true\n    }\n}\n", hex.Text);

        // Still no empty items, a comma on its own is wrong either way
        hex = Json("[1,,2]");
        hex.Run("Format JSON");
        Assert.Equal("[1,,2]", hex.Text);
        hex = Json("{,}");
        hex.Run("Format JSON");
        Assert.Equal("{,}", hex.Text);
    }

    [Fact]
    public void Strict_calls_a_comma_after_the_last_item_wrong_on_save()
    {
        var hex = Json("[1, 2,]", trailingCommas: false);
        hex.Call("on_save", hex.Object, new StringValue(hex.File));
        Assert.Equal(["1: a comma before the ] with nothing after it"], hex.Problems);
    }

    [Fact]
    public void Json_with_comments_is_read_but_not_formatted()
    {
        var hex = Json("{\n  // the name\n  \"a\": 1\n}");
        hex.Run("Check JSON");
        Assert.Contains(hex.Said, said => said.Contains("nothing's wrong", StringComparison.Ordinal));
        hex.Run("Format JSON");
        Assert.Equal("{\n  // the name\n  \"a\": 1\n}", hex.Text);
    }

    [Fact]
    public void A_json_file_is_checked_as_it_is_saved()
    {
        var hex = Json("{\"a\": }");
        hex.Call("on_save", hex.Object, new StringValue(hex.File));
        Assert.Equal(["1: a value can't start with }"], hex.Problems);

        hex.Text = "{\"a\": 1}";
        hex.Call("on_save", hex.Object, new StringValue(hex.File));
        Assert.Empty(hex.Problems);
    }

    [Fact]
    public void A_big_file_formats_well_inside_the_second_an_extension_gets()
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < 800; i++)
            text.Append(i > 0 ? "," : string.Empty).Append($"{{\"id\":{i},\"name\":\"item {i}\",\"tags\":[\"a\",\"b\"],\"on\":true}}");
        text.Append(']');
        var hex = Json(text.ToString());

        var timer = Stopwatch.StartNew();
        hex.Run("Format JSON");
        timer.Stop();
        Assert.StartsWith("[\n    {\n        \"id\": 0,", hex.Text, StringComparison.Ordinal);
        Assert.True(timer.ElapsedMilliseconds < 800, $"{text.Length / 1024} KB took {timer.ElapsedMilliseconds} ms");
    }

    /* TODO list */

    [Fact]
    public void Go_to_todo_lists_the_files_todos_and_goes_to_the_one_picked()
    {
        var hex = FakeHex.Load("todo", before: hex =>
        {
            hex.Settings["tags"] = new StringValue("TODO, FIXME, HACK, XXX, BUG");
            hex.Settings["in_problems"] = Values.False;
        });
        hex.Text = "int a; // TODO: name it\nint TODOS;\n/* FIXME the sum */\nmastodon";
        hex.Run("Go to TODO...");
        Assert.Equal(["1  TODO: name it", "3  FIXME the sum"], hex.Menu!.Items.Select(item => Values.Text(((ObjectValue)item).Properties["label"])));

        hex.Press("3 ");
        Assert.Equal((3, 4), hex.Caret);
    }

    [Fact]
    public void In_problems_puts_them_there_as_the_file_changes()
    {
        var hex = FakeHex.Load("todo", before: hex =>
        {
            hex.Settings["tags"] = new StringValue("TODO");
            hex.Settings["in_problems"] = Values.True;
        });
        hex.File = "C:/a.cs";
        hex.Text = "x\n// TODO later";
        hex.Call("on_change", hex.Object, new StringValue(hex.File));
        Assert.Equal(["2: TODO later"], hex.Problems);
    }
}
