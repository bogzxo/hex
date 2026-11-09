using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Hex.Core.Notebooks;

// HTML and SVG out of a notebook made safe to put in the page. HTML is read a tag at a time by a small tokenizer and written
// out again from what was read, only the elements and attributes allowed, every text and attribute value encoded and
// every element closed, so nothing it says can be markup the page didn't write. SVG is read as XML and kept as the vector
// it is, with what could run or reach out taken off. Allowlists rather than a list of what's bad, anything not known to be
// harmless is left out
internal static partial class NotebookHtmlExport
{
    /// <summary>HTML made safe, whether a script or a frame was left out of it, and whether anything's left to see.</summary>
    internal sealed record Cleaned(string Html, bool DroppedActive, bool Shows);

    // Elements kept, with what's in them
    private static readonly HashSet<string> ELEMENTS = new(StringComparer.Ordinal)
    {
        "a", "abbr", "article", "aside", "b", "bdi", "bdo", "blockquote", "br", "caption", "center", "cite", "code", "col", "colgroup",
        "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hr", "i", "img", "ins", "kbd", "label", "li", "main", "mark", "nav", "ol", "p", "pre", "q", "rp", "rt", "ruby",
        "s", "samp", "section", "small", "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "time", "tr", "tt", "u", "ul", "var", "wbr",
        // MathML, which browsers draw themselves
        "math", "mi", "mn", "mo", "ms", "mtext", "mrow", "msup", "msub", "msubsup", "mfrac", "msqrt", "mroot", "mtable", "mtr",
        "mtd", "mover", "munder", "munderover", "mspace", "mstyle", "mpadded", "mphantom", "menclose", "semantics", "annotation"
    };

    // Elements left out with everything in them, what's in them is code or another document, never text to read
    private static readonly HashSet<string> DROPPED = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "noembed", "noframes", "template",
        "textarea", "title", "xmp", "plaintext", "select", "head", "annotation-xml", "audio", "video", "canvas", "map"
    };

    // The dropped elements that would have been active, a script, a frame, a plugin
    private static readonly HashSet<string> ACTIVE = new(StringComparer.Ordinal) { "script", "iframe", "frame", "object", "embed", "applet", "canvas" };

    private static readonly HashSet<string> VOID = new(StringComparer.Ordinal) { "br", "hr", "img", "wbr", "col", "area", "input", "base", "link", "meta", "source", "track", "param", "mspace" };

    private static readonly HashSet<string> ATTRIBUTES = new(StringComparer.Ordinal)
    {
        "class", "id", "title", "lang", "dir", "align", "valign", "colspan", "rowspan", "scope", "headers", "width", "height",
        "border", "cellpadding", "cellspacing", "style", "alt", "start", "reversed", "type", "open", "datetime", "abbr", "span",
        "bgcolor", "color", "nowrap", "summary",
        "mathvariant", "displaystyle", "scriptlevel", "stretchy", "fence", "separator", "lspace", "rspace", "accent", "accentunder",
        "columnalign", "rowalign", "linethickness", "display", "notation", "encoding", "mathcolor", "mathbackground", "mathsize"
    };

    private const string SVG = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> SVG_ELEMENTS = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "symbol", "use", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan",
        "textPath", "title", "desc", "clipPath", "mask", "pattern", "linearGradient", "radialGradient", "stop", "marker", "image",
        "style", "metadata", "switch", "a", "view", "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite",
        "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap", "feDistantLight", "feDropShadow", "feFlood", "feFuncA",
        "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feMerge", "feMergeNode", "feMorphology", "feOffset", "fePointLight",
        "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence"
    };

    /// <summary>HTML from a notebook kept to what's safe, see the top of this part.</summary>
    internal static Cleaned Clean(string html)
    {
        var written = new StringBuilder(html.Length);
        var open = new List<string>();
        bool active = false, shows = false;
        int i = 0;
        while (i < html.Length)
        {
            int tag = html.IndexOf('<', i);
            if (tag < 0)
                tag = html.Length;
            if (tag > i)
            {
                string text = WebUtility.HtmlDecode(html[i..tag]);
                shows |= !string.IsNullOrWhiteSpace(text);
                written.Append(Encode(text));
                i = tag;
                if (i >= html.Length)
                    break;
            }

            // A comment, a doctype or a processing instruction, none of them kept
            if (At(html, i, "<!--"))
            {
                int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (At(html, i, "<!") || At(html, i, "<?"))
            {
                int end = html.IndexOf('>', i);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            bool closing = i + 1 < html.Length && html[i + 1] == '/';
            int nameAt = i + (closing ? 2 : 1);
            if (nameAt >= html.Length || !char.IsAsciiLetter(html[nameAt]))
            {
                // A < that starts no tag is text
                written.Append("&lt;");
                i++;
                continue;
            }

            int nameEnd = nameAt;
            while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':' or '_'))
                nameEnd++;
            string name = html[nameAt..nameEnd].ToLowerInvariant();
            var (attributes, selfClosed, after) = ReadAttributes(html, nameEnd);
            i = after;

            if (closing)
            {
                int at = open.LastIndexOf(name);
                if (at >= 0)
                {
                    for (int shut = open.Count - 1; shut >= at; shut--)
                        written.Append("</").Append(open[shut]).Append('>');
                    open.RemoveRange(at, open.Count - at);
                }

                continue;
            }

            if (name == "svg")
            {
                // An SVG inside HTML is taken out whole and kept as a picture of its own
                int end = SvgEnd(html, tag);
                string? image = SvgImage(html[tag..end], Attribute(attributes, "aria-label") ?? "SVG", null);
                written.Append(image ?? Notice("An SVG in this output couldn't be read, so it was left out."));
                shows |= image is not null;
                i = end;
                continue;
            }

            if (DROPPED.Contains(name))
            {
                active |= ACTIVE.Contains(name);
                if (!selfClosed && !VOID.Contains(name))
                    i = RawEnd(html, i, name);
                continue;
            }

            if (!ELEMENTS.Contains(name))
            {
                active |= name is "form" or "button" or "input" && attributes.Any(pair => pair.Name.StartsWith("on", StringComparison.Ordinal));
                continue;
            }

            if (name == "img")
            {
                string? source = Attribute(attributes, "src")?.Trim();
                if (source is null || !EmbeddedImage(source))
                {
                    string alt = Attribute(attributes, "alt") is { Length: > 0 } said ? said : "image";
                    written.Append("<span class=\"missing\">[").Append(Encode(alt)).Append(", not embedded in the notebook]</span>");
                    shows = true;
                    continue;
                }

                shows = true;
            }

            if (name is "hr" or "table" or "math")
                shows = true;

            written.Append('<').Append(name);
            foreach (var (attribute, value) in attributes)
            {
                if (KeptAttribute(name, attribute, value) is { } kept)
                    written.Append(' ').Append(attribute).Append("=\"").Append(Encode(kept)).Append('"');
            }

            if (name == "a" && Attribute(attributes, "href") is { } href && SafeLink(href))
                written.Append(" rel=\"noopener noreferrer\"");
            written.Append('>');

            if (!VOID.Contains(name) && !selfClosed)
                open.Add(name);
            else if (!VOID.Contains(name))
                written.Append("</").Append(name).Append('>');
        }

        for (int shut = open.Count - 1; shut >= 0; shut--)
            written.Append("</").Append(open[shut]).Append('>');
        return new Cleaned(written.ToString(), active, shows);
    }

    private static bool At(string text, int at, string what) => string.CompareOrdinal(text, at, what, 0, what.Length) == 0;

    // Helper method for a tag's attributes, read up to its >, names lower case and values decoded
    private static (List<(string Name, string Value)> Attributes, bool SelfClosed, int After) ReadAttributes(string html, int i)
    {
        var attributes = new List<(string, string)>();
        bool selfClosed = false;
        while (i < html.Length)
        {
            char c = html[i];
            if (c == '>')
                return (attributes, selfClosed, i + 1);
            if (char.IsWhiteSpace(c) || c == '/')
            {
                selfClosed = c == '/';
                i++;
                continue;
            }

            selfClosed = false;
            int start = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
                i++;
            string name = html[start..i].ToLowerInvariant();
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;
            string value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                    i++;
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    char quote = html[i];
                    int end = html.IndexOf(quote, i + 1);
                    if (end < 0)
                        end = html.Length;
                    value = html[(i + 1)..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    start = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                        i++;
                    value = html[start..i];
                }
            }

            if (name.Length > 0)
                attributes.Add((name, WebUtility.HtmlDecode(value)));
        }

        return (attributes, selfClosed, html.Length);
    }

    private static string? Attribute(List<(string Name, string Value)> attributes, string name)
    {
        foreach (var (attribute, value) in attributes)
        {
            if (attribute == name)
                return value;
        }

        return null;
    }

    // Helper method for where a dropped element's content ends, after its closing tag
    private static int RawEnd(string html, int from, string name)
    {
        string close = "</" + name;
        int end = html.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return html.Length;
        int shut = html.IndexOf('>', end);
        return shut < 0 ? html.Length : shut + 1;
    }

    // Helper method for where an SVG inside HTML ends, svgs inside it counted
    private static int SvgEnd(string html, int from)
    {
        int depth = 0, i = from;
        while (i < html.Length)
        {
            int open = html.IndexOf("<svg", i, StringComparison.OrdinalIgnoreCase);
            int close = html.IndexOf("</svg", i, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                return html.Length;
            if (open >= 0 && open < close)
            {
                int shut = html.IndexOf('>', open);
                if (shut < 0)
                    return html.Length;
                if (html[shut - 1] != '/')
                    depth++;
                i = shut + 1;
                continue;
            }

            depth--;
            int end = html.IndexOf('>', close);
            i = end < 0 ? html.Length : end + 1;
            if (depth <= 0)
                return i;
        }

        return html.Length;
    }

    // Helper method for an attribute's value as it's kept, null for one left out
    private static string? KeptAttribute(string element, string name, string value)
    {
        switch (name)
        {
            case "href" when element == "a":
                return SafeLink(value) ? value.Trim() : null;
            case "name" when element == "a":
                return value;
            case "src" when element == "img":
                return value.Trim();
            case "style":
                return SafeCss(value) ? value : null;
            default:
                return ATTRIBUTES.Contains(name) ? value : null;
        }
    }

    // A link a reader can follow from the page, out to the web, an address or another place in it. Nothing on the disk
    private static bool SafeLink(string href)
    {
        string link = new([.. href.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]);
        return link.StartsWith('#') || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    // A picture the page has in it already, never one it would have to fetch
    private static bool EmbeddedImage(string source)
    {
        foreach (string kind in (string[])["png", "jpeg", "jpg", "gif", "webp", "svg+xml"])
        {
            if (source.StartsWith($"data:image/{kind};", StringComparison.OrdinalIgnoreCase) || source.StartsWith($"data:image/{kind},", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Styles that can't fetch anything or run anything. A url() only to something in the same document
    private static bool SafeCss(string css)
    {
        if (css.Contains('\\') || css.Contains('<') || css.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || css.Contains("expression", StringComparison.OrdinalIgnoreCase) || css.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
            || css.Contains("behavior", StringComparison.OrdinalIgnoreCase) || css.Contains("-moz-binding", StringComparison.OrdinalIgnoreCase)
            || css.Contains("image-set", StringComparison.OrdinalIgnoreCase))
            return false;

        int start = 0;
        while ((start = css.IndexOf("url(", start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = css.IndexOf(')', start + 4);
            if (end < 0 || !css[(start + 4)..end].Trim().Trim('\'', '"').StartsWith('#'))
                return false;
            start = end + 1;
        }

        return true;
    }

    /// <summary>
    /// An SVG kept as the vector it is, in an img of its own (its own document, so its ids, defs and styles reach no other
    /// and nothing in it runs or loads), sized as the SVG says. Null for SVG that isn't. Its comments and metadata, where
    /// authors and licences are said, are kept.
    /// </summary>
    internal static string? SvgImage(string source, string alt, JsonObject? size, string kind = "picture")
    {
        if (CleanSvg(source) is not { } svg)
            return null;

        var image = new StringBuilder("<img class=\"").Append(kind).Append(" svg\" src=\"data:image/svg+xml;base64,")
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(svg.Text))).Append("\" alt=\"").Append(Encode(alt)).Append('"');
        if (size?["width"] is not null || size?["height"] is not null)
            AppendSize(image, size);
        else if (svg.Width is { } width && svg.Height is { } height)
            image.Append(" width=\"").Append(Number(width)).Append("\" height=\"").Append(Number(height)).Append('"');
        return image.Append('>').ToString();
    }

    /// <summary>An SVG with what could run or fetch anything taken off, and its size in CSS pixels when it says one.</summary>
    internal static (string Text, double? Width, double? Height)? CleanSvg(string source)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 16_000_000
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { } root || root.Name.LocalName != "svg")
            return null;

        // An svg written in HTML without its namespace is given it, a data: image is read as XML and needs it
        XNamespace svg = SVG;
        if (root.Name.Namespace == XNamespace.None)
        {
            foreach (XElement element in root.DescendantsAndSelf())
            {
                if (element.Name.Namespace == XNamespace.None)
                    element.Name = svg + element.Name.LocalName;
            }
        }

        foreach (XNode node in document.Nodes().ToList())
        {
            if (node is XDocumentType or XProcessingInstruction)
                node.Remove();
        }

        foreach (XElement element in root.DescendantsAndSelf().ToList())
        {
            if (element.Parent is null && element != root)
                continue;
            if (element.Ancestors().Any(above => above.Name == svg + "metadata"))
                continue;
            if (element.Name.Namespace != svg || !SVG_ELEMENTS.Contains(element.Name.LocalName)
                || element.Name.LocalName == "style" && !SafeCss(element.Value))
            {
                element.Remove();
                continue;
            }

            foreach (XAttribute attribute in element.Attributes().ToList())
            {
                string name = attribute.Name.LocalName;
                string value = attribute.Value.Trim();
                bool reference = name == "href";
                if (attribute.IsNamespaceDeclaration)
                    continue;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || attribute.Name.Namespace == XNamespace.Xml && name == "base"
                    || reference && !value.StartsWith('#') && !(element.Name.LocalName == "image" && EmbeddedImage(value) && !value.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
                    || !reference && !SafeCss(value))
                    attribute.Remove();
            }
        }

        foreach (XProcessingInstruction instruction in document.DescendantNodes().OfType<XProcessingInstruction>().ToList())
            instruction.Remove();

        double? width = Length(root.Attribute("width")?.Value), height = Length(root.Attribute("height")?.Value);
        if ((width is null || height is null) && root.Attribute("viewBox")?.Value is { } box
            && box.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double boxWidth)
            && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double boxHeight)
            && boxWidth > 0 && boxHeight > 0)
        {
            if (width is null && height is null)
                (width, height) = (boxWidth, boxHeight);
            else if (width is null)
                width = height * boxWidth / boxHeight;
            else
                height = width * boxHeight / boxWidth;
        }

        return (document.ToString(SaveOptions.DisableFormatting), width, height);
    }

    // Helper method for an SVG length in CSS pixels, null for none or a percentage
    private static double? Length(string? said)
    {
        if (said is null)
            return null;
        string value = said.Trim();
        (string unit, double scale) = value switch
        {
            _ when value.EndsWith("px", StringComparison.OrdinalIgnoreCase) => ("px", 1.0),
            _ when value.EndsWith("pt", StringComparison.OrdinalIgnoreCase) => ("pt", 4.0 / 3.0),
            _ when value.EndsWith("pc", StringComparison.OrdinalIgnoreCase) => ("pc", 16.0),
            _ when value.EndsWith("in", StringComparison.OrdinalIgnoreCase) => ("in", 96.0),
            _ when value.EndsWith("cm", StringComparison.OrdinalIgnoreCase) => ("cm", 96.0 / 2.54),
            _ when value.EndsWith("mm", StringComparison.OrdinalIgnoreCase) => ("mm", 96.0 / 25.4),
            _ => (string.Empty, 1.0)
        };
        string number = value[..^unit.Length].Trim();
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double length) && length > 0 && double.IsFinite(length)
            ? length * scale : null;
    }
}
