using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hex.Core.Notebooks;

namespace Hex.Tests;

public class NotebookHtmlHighlightTests
{
    [Theory]
    [InlineData("python", "def hello():\r\n\treturn 'λ 😀 </script><script>alert(1)</script>' # comment\r\n")]
    [InlineData("C#", "public string Hello() {\n return \"λ <img src=x onerror=alert(1)>\";\n}\n")]
    [InlineData("javascript", "const hello = 42; // λ\nconsole.log(\"<svg onload=alert(1)>\");\n")]
    public void Export_highlights_and_round_trips_source(string language, string source)
    {
        var notebook = new Notebook { Metadata = new JsonObject { ["language_info"] = new JsonObject { ["name"] = language } } };
        notebook.Cells.Add(new NotebookCell { Source = source });
        string html = NotebookHtmlExport.Write(notebook, new());
        string code = Regex.Match(html, "<code class=\"language-[^\"]*\">(.*?)</code>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("<span class=\"syntax-", code);
        Assert.Equal(source, WebUtility.HtmlDecode(Regex.Replace(code, "</?span[^>]*>", "")));
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains(".syntax-keyword", html);
    }

    [Theory]
    [InlineData("unknown-language")]
    [InlineData("\"><script>alert(1)</script>")]
    public void Unknown_cell_language_overrides_notebook_and_is_plain(string language)
    {
        const string source = "def hello():\n\treturn '<script>λ</script>'\n";
        var notebook = new Notebook();
        notebook.Cells.Add(new NotebookCell { Source = source, Metadata = new JsonObject { ["language"] = language } });
        string html = NotebookHtmlExport.Write(notebook, new());
        Assert.Contains(WebUtility.HtmlEncode(source), html);
        Assert.DoesNotContain("<span class=\"syntax-", html);
        Assert.DoesNotContain("<script", html);
    }
}
