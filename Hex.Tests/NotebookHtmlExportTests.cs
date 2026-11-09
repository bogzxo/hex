using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hex.Core.Notebooks;
using Hex.Dawn;

namespace Hex.Tests;

public class NotebookHtmlExportTests
{
    private static readonly NotebookHtmlOptions Plain = new() { Title = "test" };

    private static Notebook With(params NotebookCell[] cells)
    {
        var notebook = new Notebook();
        notebook.Cells.AddRange(cells);
        return notebook;
    }

    private static NotebookCell Code(string source, int? count, params JsonObject[] outputs)
    {
        var cell = new NotebookCell { Kind = CellKind.Code, Source = source, ExecutionCount = count };
        cell.Outputs.AddRange(outputs);
        return cell;
    }

    private static JsonObject Display(JsonObject data, string type = "display_data", int? count = null)
    {
        var output = new JsonObject { ["output_type"] = type, ["data"] = data, ["metadata"] = new JsonObject() };
        if (count is { } run)
            output["execution_count"] = run;
        return output;
    }

    // What the page says once every entity is read back, for checking text whatever way it was encoded
    private static string Read(string html) => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "</?span\\b[^>]*>", ""));

    // The SVG documents a page has as images, decoded
    private static List<string> Svgs(string html) => [.. Regex.Matches(html, "data:image/svg\\+xml;base64,([A-Za-z0-9+/=]+)")
        .Select(match => Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)))];

    private static void SelfContained(string html)
    {
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("<[^>]*\\s(src|srcset|poster|data|action|formaction)\\s*=\\s*\"(?!data:image/)", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("<[^>]*\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Regex.Matches(html, "</main>").Count);
        Assert.Equal(1, Regex.Matches(html, "</body>").Count);
    }

    [Fact]
    public void Cells_come_out_in_order_with_counts_streams_and_errors()
    {
        var notebook = With(
            new NotebookCell { Kind = CellKind.Markdown, Source = "# Beam check\n\nSome *words*." },
            Code("print('hi')", 3,
                new JsonObject { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = "hi\n" },
                new JsonObject { ["output_type"] = "stream", ["name"] = "stderr", ["text"] = "careful\n" },
                Display(new JsonObject { ["text/plain"] = "42" }, "execute_result", 3)),
            Code("1/0", 4, new JsonObject
            {
                ["output_type"] = "error", ["ename"] = "ZeroDivisionError", ["evalue"] = "division by zero",
                ["traceback"] = new JsonArray("\u001b[0;31mZeroDivisionError\u001b[0m: division by zero <script>x</script>")
            }),
            new NotebookCell { Kind = CellKind.Raw, Source = "raw <b>text</b>" });

        string html = NotebookHtmlExport.Write(notebook, Plain);
        SelfContained(html);
        int heading = html.IndexOf("<h1 id=\"Beam-check\">Beam check</h1>", StringComparison.Ordinal);
        int first = html.IndexOf("In&nbsp;[3]:", StringComparison.Ordinal);
        int second = html.IndexOf("In&nbsp;[4]:", StringComparison.Ordinal);
        int raw = html.IndexOf("raw &lt;b&gt;text&lt;/b&gt;", StringComparison.Ordinal);
        Assert.True(heading > 0 && first > heading && second > first && raw > second, html);
        Assert.Contains("<em>words</em>", html);
        Assert.Contains("<pre class=\"stream\">hi\n</pre>", html);
        Assert.Contains("<pre class=\"stream stderr\">careful\n</pre>", html);
        Assert.Contains("Out[3]:", html);
        Assert.Contains("<span class=\"ansi1\">ZeroDivisionError</span>: division by zero &lt;script&gt;x&lt;/script&gt;", html);
        Assert.DoesNotContain('\u001b', html);
    }

    [Fact]
    public void Code_and_plain_text_are_encoded_never_markup()
    {
        var notebook = With(Code("s = '</code></pre><script>alert(1)</script>'", 1,
            Display(new JsonObject { ["text/plain"] = "<img src=x onerror=alert(1)>" }, "execute_result", 1)));
        string html = NotebookHtmlExport.Write(notebook, new NotebookHtmlOptions { Title = "<b>t</b>" });
        SelfContained(html);
        Assert.Contains("&lt;/code&gt;&lt;/pre&gt;&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("<title>&lt;b&gt;t&lt;/b&gt;</title>", html);
    }

    [Fact]
    public void Rich_html_keeps_tables_and_drops_anything_active()
    {
        const string DATAFRAME = """
            <div><style scoped>.dataframe tbody tr th { vertical-align: sentinel-top; }</style>
            <table border="1" class="dataframe"><thead><tr style="text-align: right;"><th></th><th>load (kN)</th></tr></thead>
            <tbody><tr><th>0</th><td>12.5</td></tr><tr><th>1</th><td>&lt;7&gt; &amp; ok</td></tr></tbody></table></div>
            <script>alert(1)</script><img src=x onerror="alert(2)"><iframe src="https://example.com"></iframe>
            <a href="javascript:alert(3)">bad</a> <a href="file:///etc/passwd">local</a> <a href="https://jupyter.org" onclick="x()">good</a>
            <div style="background:url(https://tracker.example/x.png)">styled</div><object data="x.swf"></object>
            </div></div></main></body><p>after
            """;
        var notebook = With(Code("df", 1, Display(new JsonObject { ["text/html"] = DATAFRAME, ["text/plain"] = "   load\n0  12.5" }, "execute_result", 1)));
        string html = NotebookHtmlExport.Write(notebook, Plain);
        SelfContained(html);
        Assert.Contains("<table border=\"1\" class=\"dataframe\">", html);
        Assert.Contains("<th>load (kN)</th>", html);
        Assert.Contains("<td>&lt;7&gt; &amp; ok</td>", html);
        Assert.Contains("<a href=\"https://jupyter.org\" rel=\"noopener noreferrer\">good</a>", html);
        Assert.Contains(">bad</a>", html);
        Assert.Contains(">local</a>", html);
        Assert.DoesNotContain("file:///", html);
        Assert.DoesNotContain("tracker.example", html);
        Assert.DoesNotContain("sentinel-top", html);
        Assert.Contains("[image, not embedded in the notebook]", html);
        Assert.Contains("Scripts and embedded frames in this output were left out", html);
        Assert.Contains("<p>after</p>", html);
    }

    [Fact]
    public void Svg_is_preferred_kept_as_vector_and_cleaned()
    {
        const string SVG = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <!-- Created with matplotlib (https://matplotlib.org/), licence notice kept -->
            <svg xmlns:xlink="http://www.w3.org/1999/xlink" width="460.8pt" height="345.6pt" viewBox="0 0 460.8 345.6" xmlns="http://www.w3.org/2000/svg" version="1.1" onload="alert(1)">
             <metadata><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description about="author">Ada</rdf:Description></rdf:RDF></metadata>
             <defs><style type="text/css">*{stroke-linejoin: round}</style><clipPath id="p1"><rect width="10" height="10"/></clipPath></defs>
             <script>alert(2)</script><foreignObject><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
             <g clip-path="url(#p1)"><path d="M 0 0 L 10 10" style="fill: none; stroke: #1f77b4"/><use xlink:href="#p1"/></g>
             <image href="https://example.com/x.png" width="5" height="5"/><image href="file:///c:/secret.png"/>
             <a href="javascript:alert(3)"><text>link</text></a>
            </svg>
            """;
        var notebook = With(Code("plot()", 2, Display(new JsonObject
        {
            ["image/svg+xml"] = new JsonArray(SVG.Split('\n').Select(line => (JsonNode)(line + "\n")).ToArray()),
            ["image/png"] = "iVBORw0KGgo=", ["text/plain"] = "<Figure size 640x480 with 1 Axes>"
        })));
        string html = NotebookHtmlExport.Write(notebook, Plain);
        SelfContained(html);
        Assert.DoesNotContain("data:image/png", html);
        Assert.Contains("width=\"614.4\" height=\"460.8\"", html);
        string svg = Assert.Single(Svgs(html));
        Assert.Contains("viewBox=\"0 0 460.8 345.6\"", svg);
        Assert.Contains("<path d=\"M 0 0 L 10 10\"", svg);
        Assert.Contains("clip-path=\"url(#p1)\"", svg);
        Assert.Contains("<clipPath id=\"p1\">", svg);
        Assert.Contains("xlink:href=\"#p1\"", svg);
        Assert.Contains("stroke-linejoin: round", svg);
        Assert.Contains("Ada", svg);
        Assert.Contains("licence notice kept", svg);
        foreach (string gone in new[] { "onload", "<script", "foreignObject", "example.com", "file:///", "javascript:", "DOCTYPE" })
            Assert.DoesNotContain(gone, svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html.Replace("image/svg", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Svgs_with_the_same_ids_each_keep_their_own()
    {
        string Plot(string colour) => $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 10'><defs><linearGradient id='g'><stop stop-color='{colour}'/></linearGradient></defs><rect id='r' width='20' height='10' fill='url(#g)'/></svg>";
        var notebook = With(
            Code("a", 1, Display(new JsonObject { ["image/svg+xml"] = Plot("red") })),
            Code("b", 2, Display(new JsonObject { ["image/svg+xml"] = Plot("blue") })),
            Code("c", 3, Display(new JsonObject { ["text/html"] = $"<p>inline</p>{Plot("green")}" })));
        string html = NotebookHtmlExport.Write(notebook, Plain);
        SelfContained(html);
        List<string> svgs = Svgs(html);
        Assert.Equal(3, svgs.Count);
        Assert.Contains("red", svgs[0]);
        Assert.Contains("blue", svgs[1]);
        Assert.Contains("green", svgs[2]);
        Assert.All(svgs, svg => Assert.Contains("id=\"g\"", svg));

        // Each is a document of its own, none of the ids is in the page itself
        Assert.DoesNotContain("id=\"g\"", html);
        Assert.Contains("width=\"20\" height=\"10\"", html);
        Assert.Contains("<p>inline</p>", html);
    }

    [Fact]
    public void Markdown_has_its_blocks_attachments_and_nothing_fetched()
    {
        var cell = new NotebookCell
        {
            Kind = CellKind.Markdown,
            Source = "## Results\n\n1. first\n2. second\n   - nested `a<b`\n\n> quoted **bold**\n\n---\n\n| Load | Δ (mm) |\n|:-----|------:|\n| 10 | 1.5 |\n\n"
                + "```python\nif x < 3:\n    print('<b>')\n```\n\n![beam](attachment:beam.png) ![svg](attachment:beam.svg) ![gone](attachment:none.png) ![web](https://example.com/a.png)\n"
                + "[docs](https://jupyter.org) [bad](javascript:alert(1)) x<sub>2</sub> <span onclick=\"x()\">ok</span> <img src=\"local.png\">"
        };
        cell.Rest["attachments"] = new JsonObject
        {
            ["beam.png"] = new JsonObject { ["image/png"] = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" },
            ["beam.svg"] = new JsonObject { ["image/svg+xml"] = "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='30'><rect width='80' height='30' fill='blue'/></svg>" }
        };
        string html = NotebookHtmlExport.Write(With(cell), Plain);
        SelfContained(html);
        Assert.Contains("<h2 id=\"Results\">Results</h2>", html);
        Assert.Matches(new Regex("<ol>\\s*<li>first\\s*</li>\\s*<li>second\\s*<ul>\\s*<li>nested <code>a&lt;b</code>", RegexOptions.Singleline), html);
        Assert.Matches(new Regex("<blockquote>\\s*<p>quoted <strong>bold</strong></p>\\s*</blockquote>"), html);
        Assert.Contains("<hr>", html);
        Assert.Contains("<th>Load</th><th style=\"text-align:right\">", html);
        Assert.Contains("Δ (mm)", Read(html));
        Assert.Contains("<pre class=\"source\"><code class=\"language-python\">if x &lt; 3:\n    print(&#39;&lt;b&gt;&#39;)</code></pre>", html);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo", html);
        Assert.Contains("[gone, attachment none.png is missing]", html);
        Assert.Contains("[web, not embedded in the notebook]", html);
        Assert.Contains("[image, not embedded in the notebook]", html);
        Assert.Contains("<a href=\"https://jupyter.org\" rel=\"noopener noreferrer\">docs</a>", html);
        Assert.Contains("x<sub>2</sub>", html);
        Assert.Contains("<span>ok</span>", html);
        Assert.Single(Svgs(html));
    }

    [Fact]
    public void Math_is_drawn_as_paths_or_left_as_its_tex()
    {
        var cell = new NotebookCell { Kind = CellKind.Markdown, Source = "Stress $\\sigma=\\frac{My}{I}$ where $a<b$.\n\n$$\n\\delta = \\frac{FL^3}{3EI}\n$$" };
        string tex = NotebookHtmlExport.Write(With(cell), Plain);
        SelfContained(tex);
        Assert.Contains("<code class=\"math source\">a&lt;b</code>", tex);
        Assert.Contains("<div class=\"math source\">\\delta = \\frac{FL^3}{3EI}</div>", tex);

        string drawn = NotebookHtmlExport.Write(With(cell), new NotebookHtmlOptions { Title = "m", Math = NotebookRender.MathSvg });
        SelfContained(drawn);
        List<string> formulas = Svgs(drawn);
        Assert.Equal(3, formulas.Count);
        Assert.All(formulas, svg =>
        {
            Assert.Contains("<path", svg);
            Assert.DoesNotContain("<text", svg);
            Assert.DoesNotContain("<image", svg);
        });
        Assert.Contains("class=\"math svg\"", drawn);
        Assert.Contains("<div class=\"math display\">", drawn);
        Assert.Contains("vertical-align:-", drawn);
        Assert.Contains("alt=\"a&lt;b\"", drawn);
    }

    [Fact]
    public void Latex_output_and_formulas_CSharpMath_cant_read_fall_back_to_tex()
    {
        var notebook = With(Code("x", 1, Display(new JsonObject { ["text/latex"] = "$$\\notacommand{x}$$", ["text/plain"] = "x" })));
        string html = NotebookHtmlExport.Write(notebook, new NotebookHtmlOptions { Math = NotebookRender.MathSvg });
        Assert.Contains("<div class=\"math source\">\\notacommand{x}</div>", html);
    }

    [Fact]
    public void What_cant_be_shown_statically_says_so()
    {
        var notebook = With(
            Code("w", 1, Display(new JsonObject
            {
                ["application/vnd.jupyter.widget-view+json"] = new JsonObject { ["model_id"] = "abc", ["version_major"] = 2 },
                ["text/plain"] = "IntSlider(value=3)"
            })),
            Code("p", 2, Display(new JsonObject
            {
                ["text/html"] = "<div id=\"plot\"></div><script>Plotly.newPlot('plot', [])</script>",
                ["application/vnd.plotly.v1+json"] = new JsonObject(),
                ["text/plain"] = "Figure({'data': []})"
            })),
            Code("j", 3, Display(new JsonObject { ["application/javascript"] = "alert(1)", ["text/plain"] = "<IPython.core.display.Javascript object>" })),
            Code("u", 4, Display(new JsonObject { ["application/x-custom"] = "?" })),
            Code("e", 5, Display(new JsonObject())),
            Code("k", 6, new JsonObject { ["output_type"] = "mystery" }),
            Code("b", 7, Display(new JsonObject { ["image/svg+xml"] = "<broken", ["text/plain"] = "Readable fallback" })));
        string html = NotebookHtmlExport.Write(notebook, Plain);
        SelfContained(html);
        Assert.Contains("interactive widget", html);
        Assert.Contains("IntSlider(value=3)", html);
        Assert.Contains("needs JavaScript to draw itself", html);
        Assert.Contains("Figure({&#39;data&#39;: []})", html);
        Assert.Contains("This output is JavaScript", html);
        Assert.Contains("(application/x-custom)", html);
        Assert.Contains("This output has no data to show.", html);
        Assert.Contains("“mystery”", Read(html));
        Assert.Contains("Readable fallback", html);
        Assert.DoesNotContain("alert(1)", html);
    }

    [Fact]
    public void Unicode_and_multiline_arrays_read_from_a_file_survive()
    {
        const string FILE = """
            {"cells": [
              {"cell_type": "markdown", "id": "a1", "metadata": {}, "source": ["# Δσ – ünïcødé 🙂\n", "\n", "line two"]},
              {"cell_type": "code", "id": "a2", "metadata": {}, "execution_count": 7, "source": ["x = 'é'\n", "print(x)"],
               "outputs": [{"output_type": "stream", "name": "stdout", "text": ["é\n", "日本語\n"]},
                           {"output_type": "execute_result", "execution_count": 7, "metadata": {}, "data": {"text/plain": ["'α'\n", "'β'"], "text/html": ["<b>α</b>\n", "<i>β</i>"]}}]}
            ], "metadata": {"language_info": {"name": "python"}}, "nbformat": 4, "nbformat_minor": 5}
            """;
        string html = NotebookHtmlExport.Write(NotebookFile.Read(FILE), Plain);
        string read = Read(html);
        Assert.Contains("Δσ – ünïcødé 🙂", read);
        Assert.Contains("x = 'é'\nprint(x)", read);
        Assert.Contains("é\n日本語\n", read);
        Assert.Contains("<b>α</b>\n<i>β</i>", read);
        Assert.Contains("<meta charset=\"utf-8\">", html);
    }

    [Fact]
    public void A_snapshot_is_exported_and_the_notebook_is_left_as_it_was()
    {
        var model = With(Code("a = 1", 1, new JsonObject { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = "before\n" }));
        string before = NotebookFile.Write(model);
        Notebook snapshot = DawnScene.NotebookSnapshot(model);

        // The live one goes on changing, a cell typed into and output coming in, the snapshot doesn't see it
        model.Cells[0].Source = "a = 2";
        model.Cells[0].Outputs[0]["text"] = "after\n";
        model.Cells.Add(new NotebookCell { Kind = CellKind.Markdown, Source = "later" });
        string html = NotebookHtmlExport.Write(snapshot, Plain);
        Assert.Contains("a = 1", Read(html));
        Assert.Contains("before", html);
        Assert.DoesNotContain("later", html);

        // And exporting changed nothing of the notebook it was made from
        var again = With(Code("a = 1", 1, new JsonObject { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = "before\n" }));
        string unchanged = NotebookFile.Write(again);
        NotebookHtmlExport.Write(again, Plain);
        Assert.Equal(unchanged, NotebookFile.Write(again));
        Assert.NotEqual(before, NotebookFile.Write(model));
    }

    [Fact]
    public void A_large_notebook_exports_quickly()
    {
        var notebook = new Notebook();
        for (int i = 0; i < 2000; i++)
        {
            notebook.Cells.Add(new NotebookCell { Kind = CellKind.Markdown, Source = $"## Step {i}\n\nSome **text** with `code` and a [link](https://example.com/{i})." });
            notebook.Cells.Add(Code($"x = {i}\nprint(x)", i, new JsonObject { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = $"{i}\n" }));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string html = NotebookHtmlExport.Write(notebook, Plain);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 10_000);
        Assert.Equal(2000, Regex.Matches(html, "<section class=\"cell code\">").Count);
    }

    [Fact]
    public void Page_is_responsive_printable_and_locked_down()
    {
        string html = NotebookHtmlExport.Write(Notebook.Empty(), Plain);
        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("@media print", html);
        Assert.Contains("@media (max-width: 700px)", html);
        Assert.Contains("img.picture { display: block; max-width: 100%; height: auto; }", html);
    }
}
