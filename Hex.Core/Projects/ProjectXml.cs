namespace Hex.Core.Projects;

/// <summary>An attribute of an element of a project file, its name and where its value sits in the text.</summary>
internal sealed record ProjectXmlAttribute(string Name, string Value, int Start, int End, int ValueStart, int ValueEnd);

/// <summary>
/// An element of a project file and where it is in the text, from its &lt; to past its end tag (or its /&gt;), with the
/// elements inside it. Only what an edit has to know is kept, the text itself is never rebuilt from these.
/// </summary>
internal sealed class ProjectXmlElement
{
    public string Name { get; init; } = string.Empty;
    public int Start { get; init; }

    /// <summary>Just past the &gt; of the start tag.</summary>
    public int TagEnd { get; set; }

    /// <summary>Just past the &gt; of the end tag, or of the start tag when it closes itself.</summary>
    public int End { get; set; }

    /// <summary>Where the end tag starts, the same as End for one that closes itself.</summary>
    public int CloseStart { get; set; }

    public bool SelfClosing { get; set; }
    public List<ProjectXmlAttribute> Attributes { get; } = [];
    public List<ProjectXmlElement> Children { get; } = [];
    public ProjectXmlElement? Parent { get; init; }

    public ProjectXmlAttribute? Attribute(string name) => Attributes.Find(attribute => attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ProjectXmlElement? Child(string name) => Children.Find(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"<{Name}> {Start}..{End}";
}

/// <summary>
/// A tiny reader of the XML a project file is, made for editing it by hand. It only says where the elements and their
/// attributes are, so an edit can splice a few characters in or out and leave every other byte of the file (the
/// comments, the indentation, the quotes, CRLF) the way whoever wrote it left it. XDocument normalises the line endings
/// and the quotes on a round trip, and Visual Studio doesn't touch a line it didn't mean to.
/// </summary>
internal static class ProjectXml
{
    /// <summary>The root element of a project file's text, null when it isn't XML this can read.</summary>
    public static ProjectXmlElement? Read(string text)
    {
        var root = new ProjectXmlElement { Name = "#document", Start = 0 };
        ProjectXmlElement open = root;
        int at = 0;
        try
        {
            while (at < text.Length)
            {
                int lt = text.IndexOf('<', at);
                if (lt < 0)
                    break;

                if (Starts(text, lt, "<!--"))
                {
                    at = After(text, lt, "-->");
                    continue;
                }

                if (Starts(text, lt, "<![CDATA["))
                {
                    at = After(text, lt, "]]>");
                    continue;
                }

                if (Starts(text, lt, "<?") || Starts(text, lt, "<!"))
                {
                    at = After(text, lt, ">");
                    continue;
                }

                if (Starts(text, lt, "</"))
                {
                    int gt = After(text, lt, ">");
                    string name = text[(lt + 2)..(gt - 1)].Trim();
                    if (!name.Equals(open.Name, StringComparison.Ordinal) || open.Parent is null)
                        return null;

                    open.CloseStart = lt;
                    open.End = gt;
                    open = open.Parent;
                    at = gt;
                    continue;
                }

                ProjectXmlElement element = StartTag(text, lt, open);
                open.Children.Add(element);
                at = element.TagEnd;
                if (!element.SelfClosing)
                    open = element;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        return open == root ? root.Children.Find(child => child.Name.Length > 0) : null;
    }

    // A start tag from its <, its name and attributes and whether it closes itself
    private static ProjectXmlElement StartTag(string text, int lt, ProjectXmlElement parent)
    {
        int at = lt + 1;
        int nameStart = at;
        while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('>' or '/'))
            at++;

        var element = new ProjectXmlElement { Name = text[nameStart..at], Start = lt, Parent = parent };
        while (true)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            if (at >= text.Length)
                throw new FormatException("a tag that never ends");

            if (text[at] == '>')
            {
                element.TagEnd = at + 1;
                return element;
            }

            if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '>')
            {
                element.TagEnd = element.End = element.CloseStart = at + 2;
                element.SelfClosing = true;
                return element;
            }

            int attributeStart = at;
            while (at < text.Length && text[at] != '=' && !char.IsWhiteSpace(text[at]) && text[at] is not ('>' or '/'))
                at++;
            string name = text[attributeStart..at];
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            if (at >= text.Length || text[at] != '=')
                throw new FormatException("an attribute without a value");
            at++;
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            if (at >= text.Length || text[at] is not ('"' or '\''))
                throw new FormatException("an attribute value without quotes");

            char quote = text[at];
            int valueStart = at + 1;
            int valueEnd = text.IndexOf(quote, valueStart);
            if (valueEnd < 0)
                throw new FormatException("an attribute value that never ends");

            element.Attributes.Add(new ProjectXmlAttribute(name, Unescape(text[valueStart..valueEnd]), attributeStart, valueEnd + 1, valueStart, valueEnd));
            at = valueEnd + 1;
        }
    }

    private static bool Starts(string text, int at, string with) => string.CompareOrdinal(text, at, with, 0, with.Length) == 0;

    private static int After(string text, int at, string end)
    {
        int found = text.IndexOf(end, at, StringComparison.Ordinal);
        if (found < 0)
            throw new FormatException($"no {end} after {at}");
        return found + end.Length;
    }

    /// <summary>The text of an attribute's value or an element's text as XML means it, the five entities undone.</summary>
    public static string Unescape(string text) => text.Contains('&')
        ? text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&")
        : text;

    /// <summary>A value written into an attribute or an element, what XML needs escaped escaped.</summary>
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>The text inside an element, between its tags, empty for one that closes itself.</summary>
    public static string InnerText(string text, ProjectXmlElement element) =>
        element.SelfClosing ? string.Empty : Unescape(text[element.TagEnd..element.CloseStart]);
}
