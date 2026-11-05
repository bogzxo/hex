using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>A formula drawn as SVG for an export, its size in CSS pixels and how far its baseline is from its bottom.</summary>
internal sealed record NotebookHtmlMath(string Svg, float Width, float Height, float Descent);

/// <summary>What an HTML export is made with. The title, and how to draw a formula, null to leave formulas as their TeX.</summary>
internal sealed class NotebookHtmlOptions
{
    public string Title { get; init; } = "Notebook";

    /// <summary>A formula (its TeX, and whether it's display rather than inline) drawn as SVG, null when it can't be.</summary>
    public Func<string, bool, NotebookHtmlMath?>? Math { get; init; }
}

/// <summary>
/// A notebook written as one HTML page that needs nothing else, its CSS inline and its pictures in it, readable offline in
/// any browser and printable. The cells in order, markdown written out, code with its run counts, and of each output the
/// one form that shows it best (SVG before PNG, a table's HTML before its text). Everything an output or a markdown cell
/// brought is untrusted, plain text is encoded and rich HTML and SVG go through an allowlist (see the Sanitize part), so
/// the page runs no script and loads nothing from the network or the disk. An SVG is kept as the vector it came as, in an
/// image of its own so its ids and styles can't reach another. What can't be shown statically (a widget, JavaScript) is
/// said so where it was.
/// </summary>
internal static partial class NotebookHtmlExport
{
    // Raster pictures an output can carry, best first after SVG
    private static readonly string[] RASTERS = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>The notebook as a page.</summary>
    public static string Write(Notebook notebook, NotebookHtmlOptions options)
    {
        var page = new StringBuilder();
        string title = Encode(options.Title);
        page.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"generator\" content=\"Hex\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(title).Append("</title>\n<style>\n").Append(STYLE).Append("</style>\n</head>\n<body>\n<main class=\"notebook\">\n");

        string language = notebook.Language;
        foreach (NotebookCell cell in notebook.Cells)
        {
            switch (cell.Kind)
            {
                case CellKind.Markdown:
                    page.Append("<section class=\"cell markdown\">\n").Append(Markdown(cell.Source, cell.Rest["attachments"] as JsonObject, options)).Append("</section>\n");
                    break;
                case CellKind.Raw:
                    page.Append("<section class=\"cell raw\"><pre>").Append(Encode(cell.Source)).Append("</pre></section>\n");
                    break;
                default:
                    WriteCode(page, cell, language, options);
                    break;
            }
        }

        page.Append("</main>\n</body>\n</html>\n");
        return page.ToString();
    }

    // Helper method for a code cell, its source with In [n] by it and then its outputs
    private static void WriteCode(StringBuilder page, NotebookCell cell, string language, NotebookHtmlOptions options)
    {
        string count = cell.ExecutionCount is { } run ? run.ToString(System.Globalization.CultureInfo.InvariantCulture) : " ";
        page.Append("<section class=\"cell code\">\n<div class=\"input\"><div class=\"prompt\">In&nbsp;[").Append(count).Append("]:</div>")
            .Append("<pre class=\"source\"><code class=\"language-").Append(Encode(Plain(language))).Append("\">")
            .Append(Encode(cell.Source)).Append("</code></pre></div>\n");

        if (cell.Outputs.Count > 0)
        {
            page.Append("<div class=\"outputs\">\n");
            foreach (JsonObject output in cell.Outputs)
                WriteOutput(page, output, options);
            page.Append("</div>\n");
        }

        page.Append("</section>\n");
    }

    private static void WriteOutput(StringBuilder page, JsonObject output, NotebookHtmlOptions options)
    {
        string type = output["output_type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "stream":
            {
                bool error = output["name"]?.GetValue<string>() == "stderr";
                page.Append("<div class=\"output\"><div class=\"prompt\"></div><pre class=\"stream").Append(error ? " stderr" : string.Empty).Append("\">")
                    .Append(Ansi(NotebookFile.Text(output["text"]))).Append("</pre></div>\n");
                return;
            }
            case "error":
            {
                string traceback = output["traceback"] is JsonArray lines ? string.Join('\n', lines.Select(line => line is JsonValue value && value.TryGetValue(out string? said) ? said : string.Empty)) : string.Empty;
                if (traceback.Length == 0)
                    traceback = $"{Said(output["ename"])}: {Said(output["evalue"])}";
                page.Append("<div class=\"output\"><div class=\"prompt\"></div><pre class=\"error\">").Append(Ansi(traceback)).Append("</pre></div>\n");
                return;
            }
            case "execute_result":
            case "display_data":
            {
                string prompt = type == "execute_result" && output["execution_count"] is JsonValue run && run.TryGetValue(out int count)
                    ? $"Out[{count}]:" : string.Empty;
                page.Append("<div class=\"output\"><div class=\"prompt\">").Append(prompt.Replace(" ", "&nbsp;", StringComparison.Ordinal)).Append("</div><div class=\"result\">");
                page.Append(output["data"] is JsonObject data ? Data(data, output["metadata"] as JsonObject, options) : Notice("This output has no data to show."));
                page.Append("</div></div>\n");
                return;
            }
            default:
                page.Append("<div class=\"output\"><div class=\"prompt\"></div><div class=\"result\">")
                    .Append(Notice($"An output of kind “{type}” can't be shown in a static page.")).Append("</div></div>\n");
                return;
        }
    }

    /// <summary>The best of the forms a result came in, as HTML. One form each, the others are its fallbacks.</summary>
    internal static string Data(JsonObject data, JsonObject? metadata, NotebookHtmlOptions options)
    {
        var shown = new StringBuilder();
        string? plain = data["text/plain"] is { } said ? NotebookFile.Text(said) : null;

        // A widget lives in a running kernel, its static fallback is shown under a note saying so
        if (data.ContainsKey("application/vnd.jupyter.widget-view+json"))
            shown.Append(Notice("This is an interactive widget. It needs a running Jupyter kernel, so its static text is shown instead."));

        if (data["image/svg+xml"] is { } svg && SvgImage(NotebookFile.Text(svg), Alt(plain), metadata?["image/svg+xml"] as JsonObject) is { } vector)
            return shown.Append(vector).ToString();

        foreach (string mime in RASTERS)
        {
            if (data[mime] is { } raster && RasterImage(mime, NotebookFile.Text(raster), Alt(plain), metadata?[mime] as JsonObject) is { } picture)
                return shown.Append(picture).ToString();
        }

        if (data["text/html"] is { } html)
        {
            Cleaned cleaned = Clean(NotebookFile.Text(html));
            if (cleaned.Shows)
            {
                if (cleaned.DroppedActive)
                    shown.Append(Notice("Scripts and embedded frames in this output were left out of the export."));
                return shown.Append("<div class=\"html\">").Append(cleaned.Html).Append("</div>").ToString();
            }

            if (cleaned.DroppedActive)
                shown.Append(Notice("This output needs JavaScript to draw itself, which an exported page doesn't run. Its static form is shown instead."));
        }

        if (data["text/markdown"] is { } markdown)
            return shown.Append("<div class=\"markdown\">").Append(Markdown(NotebookFile.Text(markdown), null, options)).Append("</div>").ToString();

        if (data["text/latex"] is { } latex)
            return shown.Append("<div class=\"latex\">").Append(Formula(NotebookFile.Text(latex), display: true, options)).Append("</div>").ToString();

        if (data["application/json"] is { } json)
            return shown.Append("<pre class=\"text\">").Append(Encode(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).Append("</pre>").ToString();

        if (plain is not null)
        {
            // Only the object's name says nothing when there's another form, what it is is said below instead
            if (!NotebookHtml.IsBareRepr(plain) || data.Count == 1)
                return shown.Append("<pre class=\"text\">").Append(Ansi(plain)).Append("</pre>").ToString();
        }

        if (data.ContainsKey("application/javascript"))
            return shown.Append(Notice("This output is JavaScript, which an exported page doesn't run.")).ToString();

        if (data["image/svg+xml"] is not null)
            return shown.Append(Notice("This SVG couldn't be read, so it was left out.")).ToString();

        if (shown.Length > 0)
            return shown.ToString();

        return Notice(data.Count == 0 ? "This output has no data to show."
            : "No static form of this output can be shown (" + string.Join(", ", data.Select(entry => entry.Key)) + ").");
    }

    // Helper method for a picture's description out of a result's text, nothing when that's only the object's name
    private static string Alt(string? plain) => plain is null || NotebookHtml.IsBareRepr(plain) ? "output" : plain.Trim().Split('\n')[0];

    // Helper method for a raster picture kept as the bytes it came as, at the size its metadata asks for when it asks
    private static string? RasterImage(string mime, string encoded, string alt, JsonObject? size)
    {
        string base64 = string.Concat(encoded.Where(c => !char.IsWhiteSpace(c)));
        if (base64.Length == 0)
            return null;
        try
        {
            _ = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        var image = new StringBuilder("<img class=\"picture\" src=\"data:").Append(mime).Append(";base64,").Append(base64).Append("\" alt=\"").Append(Encode(alt)).Append('"');
        AppendSize(image, size);
        return image.Append('>').ToString();
    }

    private static void AppendSize(StringBuilder image, JsonObject? size)
    {
        if (size?["width"] is JsonValue w && w.TryGetValue(out double width) && width > 0 && double.IsFinite(width))
            image.Append(" width=\"").Append(Number(width)).Append('"');
        if (size?["height"] is JsonValue h && h.TryGetValue(out double height) && height > 0 && double.IsFinite(height))
            image.Append(" height=\"").Append(Number(height)).Append('"');
    }

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // Helper method for a formula, drawn as SVG when there's something to draw it with, its TeX otherwise
    private static string Formula(string tex, bool display, NotebookHtmlOptions options)
    {
        string source = Undelimit(tex);
        NotebookHtmlMath? drawn = null;
        if (options.Math is { } draw && source.Length > 0)
        {
            try
            {
                drawn = draw(source, display);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
            {
                drawn = null;
            }
        }

        if (drawn is null || SvgImage(drawn.Svg, source, null, "math") is not { } image)
            return display ? $"<div class=\"math source\">{Encode(source)}</div>" : $"<code class=\"math source\">{Encode(source)}</code>";

        // Inline formulas sit on the baseline of the text round them
        string place = display ? string.Empty : $" style=\"vertical-align:-{Number(drawn.Descent)}px\"";
        image = image.Replace("<img ", $"<img{place} ", StringComparison.Ordinal);
        return display ? $"<div class=\"math display\">{image}</div>" : image;
    }

    // Helper method for a formula's TeX without the $, $$, \( or \[ round it
    private static string Undelimit(string source)
    {
        string text = source.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            if (text.Length >= open.Length + close.Length && text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal))
                return text[open.Length..^close.Length].Trim();
        }

        return text;
    }

    // Helper method for a note where something couldn't be shown, so nothing is silently blank
    private static string Notice(string text) => $"<p class=\"notice\">{Encode(text)}</p>";

    private static string Said(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;

    /// <summary>Text made safe to put in HTML, in an element or a quoted attribute.</summary>
    internal static string Encode(string text) => WebUtility.HtmlEncode(text);

    // Helper method for a word safe in a class name
    private static string Plain(string word) => new([.. word.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '+')]);

    // Helper method for text with ANSI escapes, encoded, its colours as spans of the sixteen
    private static string Ansi(string written)
    {
        var (text, colours) = AnsiText.Read(written);
        if (colours.Count == 0)
            return Encode(text);

        string[] lines = text.Split('\n');
        var byLine = colours.GroupBy(span => span.Line).ToDictionary(group => group.Key, group => group.OrderBy(span => span.Start).ToList());
        var html = new StringBuilder(text.Length + colours.Count * 32);
        for (int line = 0; line < lines.Length; line++)
        {
            if (line > 0)
                html.Append('\n');
            string row = lines[line];
            if (!byLine.TryGetValue(line, out var spans))
            {
                html.Append(Encode(row));
                continue;
            }

            int at = 0;
            foreach (var span in spans)
            {
                int start = Math.Clamp(span.Start, at, row.Length), end = Math.Clamp(span.Start + span.Length, start, row.Length);
                html.Append(Encode(row[at..start]));
                int colour = span.Kind - Horizon.UI.Scripting.SyntaxKind.Ansi0;
                html.Append("<span class=\"ansi").Append(colour).Append("\">").Append(Encode(row[start..end])).Append("</span>");
                at = end;
            }

            html.Append(Encode(row[at..]));
        }

        return html.ToString();
    }
}
