using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// The Emmet extension (emmet in the hex-extensions catalog, checked out as a submodule of Hex), run in a bare HIDL runtime
/// the way Dawn runs it. Abbreviations written out, where one starts on a line, and Tab and Ctrl+E against a hex object
/// that only has what Emmet asks for, a line of text with a caret in it, and writes down what it was told to replace.
/// </summary>
public class EmmetTests
{
    private static readonly string Folder = Path.Combine(Catalog.Folder, "emmet");

    /// <summary>A hex object with one line on screen and the caret at a column of it (from 1).</summary>
    private sealed class Screen
    {
        public string Line = string.Empty;
        public int Column = 1;
        public string File = "page.html";
        public readonly List<(int Line, int From, int To, string Text, int Caret)> Replaced = [];
        public readonly List<(string Name, IRuntimeValue Function, string Key)> Commands = [];
        public readonly List<string> Said = [];
        public ObjectValue Object { get; }

        public Screen()
        {
            Object = Natives.Object(
                ("caret", Natives.Function("caret", _ => Natives.Object(
                    ("line", new NumberValue(3)),
                    ("column", new NumberValue(Column)),
                    ("text", new StringValue(Line)),
                    ("selected", new StringValue(string.Empty))))),
                ("replace_text", Natives.Function("replace_text", args =>
                {
                    ObjectValue from = args.Object(0), to = args.Object(1);
                    Replaced.Add(((int)Number(from, "line"), (int)Number(from, "column"), (int)Number(to, "column"), args.Text(2), (int)args.Number(3)));
                    return Values.Null;
                })),
                ("setting", Natives.Function("setting", args => args.Text(0) switch
                {
                    "indent" => new NumberValue(4),
                    "tab" => Values.Bool(true),
                    _ => new StringValue(".html, .htm, .jsx")
                })),
                ("command", Natives.Action("command", args => Commands.Add((args.Text(0), args.Callable(1), args.Text(2, string.Empty))))),
                ("say", Natives.Action("say", args => Said.Add(args.Text(0)))),
                ("file", Natives.Function("file", _ => new StringValue(File))));
        }

        private static float Number(ObjectValue of, string key) => ((NumberValue)of.Properties[key]).Value;
    }

    private static (HIDLRuntime Runtime, Screen Hex) Load()
    {
        var screen = new Screen();
        var runtime = new HIDLRuntime { BaseDirectory = Folder, Output = null };
        runtime.UserScope.DeclareSystem("hex", screen.Object);
        runtime.RunFile(Path.Combine(Folder, "extension.hor"));
        runtime.Invoke(runtime.UserScope.Variables["on_start"], screen.Object);
        return (runtime, screen);
    }

    private static IRuntimeValue Call(HIDLRuntime runtime, string function, params IRuntimeValue[] arguments) =>
        runtime.Invoke(runtime.UserScope.Variables[function], arguments);

    /// <summary>An abbreviation written out with four spaces a level, the caret as a | where it lands.</summary>
    private static string? Expand(string abbreviation, bool jsx = false)
    {
        var (runtime, _) = Load();
        var style = Natives.Object(("jsx", Values.Bool(jsx)), ("self_closing", Values.Bool(jsx)));
        if (Call(runtime, "expansion", new StringValue(abbreviation), new StringValue("    "), style) is not ObjectValue made)
            return null;

        string text = ((StringValue)made.Properties["text"]).Value;
        int caret = (int)((NumberValue)made.Properties["caret"]).Value;
        return text.Insert(caret, "|");
    }

    [Theory]
    [InlineData("div", "<div>|</div>")]
    [InlineData("a", "<a href=\"|\"></a>")]
    [InlineData("img", "<img src=\"|\" alt=\"\">")]
    [InlineData("br", "<br>|")]
    [InlineData("div#main.wide.dark", "<div id=\"main\" class=\"wide dark\">|</div>")]
    [InlineData(".box", "<div class=\"box\">|</div>")]
    [InlineData("a[href=# title=\"Hi there\"]{Go}", "<a href=\"#\" title=\"Hi there\">Go</a>|")]
    [InlineData("p>{Click }+a{here}", "<p>Click <a href=\"|\">here</a></p>")]
    [InlineData("p{Hello}+p", "<p>Hello</p>\n<p>|</p>")]
    [InlineData("div>p^h2", "<div>\n    <p>|</p>\n</div>\n<h2></h2>")]
    [InlineData("div>p>span^^h2", "<div>\n    <p><span>|</span></p>\n</div>\n<h2></h2>")]
    [InlineData("nav>ul>li", "<nav>\n    <ul>\n        <li>|</li>\n    </ul>\n</nav>")]
    [InlineData("ul>li.item$*3", "<ul>\n    <li class=\"item1\">|</li>\n    <li class=\"item2\"></li>\n    <li class=\"item3\"></li>\n</ul>")]
    [InlineData("ul>li*2>a{Link $}", "<ul>\n    <li><a href=\"|\">Link 1</a></li>\n    <li><a href=\"\">Link 2</a></li>\n</ul>")]
    [InlineData("h$*3", "<h1>|</h1>\n<h2></h2>\n<h3></h3>")]
    [InlineData("li.x$$*2", "<li class=\"x01\">|</li>\n<li class=\"x02\"></li>")]
    [InlineData("li{$@-}*3", "<li>3</li>\n<li>2</li>\n<li>1</li>|")]
    [InlineData("li{$@4}*2", "<li>4</li>\n<li>5</li>|")]
    [InlineData("(dt+dd)*2", "<dt>|</dt>\n<dd></dd>\n<dt></dt>\n<dd></dd>")]
    [InlineData("div>(header>h1)+main", "<div>\n    <header>\n        <h1>|</h1>\n    </header>\n    <main></main>\n</div>")]
    [InlineData("ul>.x", "<ul>\n    <li class=\"x\">|</li>\n</ul>")]
    [InlineData("table>.row>.cell", "<table>\n    <tr class=\"row\">\n        <td class=\"cell\">|</td>\n    </tr>\n</table>")]
    [InlineData("em>.x", "<em><span class=\"x\">|</span></em>")]
    [InlineData("ul+", "<ul>\n    <li>|</li>\n</ul>")]
    [InlineData("input:email", "<input type=\"email\" name=\"|\" id=\"\">")]
    [InlineData("input:c", "<input type=\"checkbox\" name=\"|\" id=\"\">")]
    [InlineData("btn:s.primary", "<button class=\"primary\" type=\"submit\">|</button>")]
    [InlineData("link:css", "<link rel=\"stylesheet\" href=\"style.css\">|")]
    [InlineData("input[type=number]", "<input type=\"number\">|")]
    [InlineData("button[disabled]", "<button disabled=\"|\"></button>")]
    [InlineData("!", "<!DOCTYPE html>\n<html lang=\"en\">\n    <head>\n        <meta charset=\"UTF-8\">\n        <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n        <title>Document</title>\n    </head>\n    <body>|</body>\n</html>")]
    [InlineData("!>main", "<!DOCTYPE html>\n<html lang=\"en\">\n    <head>\n        <meta charset=\"UTF-8\">\n        <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n        <title>Document</title>\n    </head>\n    <body>\n        <main>|</main>\n    </body>\n</html>")]
    public void An_abbreviation_is_written_out(string abbreviation, string expected) =>
        Assert.Equal(expected, Expand(abbreviation));

    [Fact]
    public void In_jsx_a_class_is_a_class_name_and_a_void_element_closes_itself()
    {
        Assert.Equal("<label htmlFor=\"|\" className=\"x\"></label>", Expand("label.x", jsx: true));
        Assert.Equal("<br />|", Expand("br", jsx: true));
    }

    [Theory]
    [InlineData("div[")]
    [InlineData("p{unfinished")]
    [InlineData("a)")]
    [InlineData("(a")]
    [InlineData("a.")]
    public void What_isnt_an_abbreviation_is_nothing(string abbreviation) =>
        Assert.Null(Expand(abbreviation));

    [Theory]
    [InlineData("ul>li", 0)]
    [InlineData("    ul>li", 4)]
    [InlineData("text p.x", 5)]
    [InlineData("<p>ul>li", 3)]
    [InlineData("<div class=\"a\">ul", 15)]
    [InlineData("<br/>p", 5)]
    [InlineData("a[title=\"x y\"]", 0)]
    [InlineData("say p{a b}", 4)]
    [InlineData("x ", 2)]
    public void An_abbreviation_starts_where_it_should(string line, int start)
    {
        var (runtime, _) = Load();
        Assert.Equal(start, (int)((NumberValue)Call(runtime, "abbreviation_start", new StringValue(line), new NumberValue(line.Length))).Value);
    }

    [Fact]
    public void Tab_writes_it_out_indented_like_its_line_with_the_caret_in_the_first_empty_place()
    {
        var (runtime, hex) = Load();
        hex.Line = "    ul>li*2";
        hex.Column = hex.Line.Length + 1;

        Assert.Equal(Values.True, Call(runtime, "on_tab", hex.Object, new StringValue("C:/site/page.html")));
        var (line, from, to, text, caret) = Assert.Single(hex.Replaced);
        Assert.Equal((3, 5, 12), (line, from, to));
        Assert.Equal("<ul>\n        <li></li>\n        <li></li>\n    </ul>", text);
        Assert.Equal("<ul>\n        <li>".Length, caret);
    }

    [Theory]
    [InlineData("Some words", "page.html")]
    [InlineData("<div cla", "page.html")]
    [InlineData("ul>li", "notes.txt")]
    [InlineData("ul>li", null)]
    [InlineData("div[", "page.html")]
    public void Tab_is_left_to_indent_when_there_is_nothing_to_write_out(string line, string? file)
    {
        var (runtime, hex) = Load();
        hex.Line = line;
        hex.Column = line.Length + 1;

        Assert.Equal(Values.False, Call(runtime, "on_tab", hex.Object, file is null ? Values.Null : new StringValue(file)));
        Assert.Empty(hex.Replaced);
    }

    [Fact]
    public void Ctrl_e_writes_out_any_word_in_any_file()
    {
        var (runtime, hex) = Load();
        var (name, function, key) = Assert.Single(hex.Commands);
        Assert.Equal(("Expand abbreviation", "Ctrl+E"), (name, key));

        hex.File = "notes.txt";
        hex.Line = "widget";
        hex.Column = 7;
        runtime.Invoke(function, hex.Object);
        Assert.Equal("<widget></widget>", Assert.Single(hex.Replaced).Text);

        hex.Line = "   ";
        hex.Column = 4;
        runtime.Invoke(function, hex.Object);
        Assert.Single(hex.Replaced);
        Assert.Single(hex.Said);
    }

    [Fact]
    public void Too_many_elements_are_said_and_not_written()
    {
        var (runtime, hex) = Load();
        hex.Line = "li*100000";
        hex.Column = hex.Line.Length + 1;

        Assert.Equal(Values.True, Call(runtime, "on_tab", hex.Object, new StringValue("page.html")));
        Assert.Empty(hex.Replaced);
        Assert.Single(hex.Said);
    }
}
