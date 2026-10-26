using System.Text;

namespace Hex.Core.Projects;

// The splicing itself, a few characters put into or taken out of the project's text at the places ProjectXml found,
// indented like the lines round them and ended the way the file ends its lines
internal static partial class ProjectFileEdits
{
    /// <summary>A piece of metadata of an item set, its attribute or its element changed, or an element of it put in.</summary>
    private static string WithMetadata(string text, ProjectXmlElement item, string name, string value)
    {
        if (item.Attribute(name) is { } attribute)
            return attribute.Value == value ? text : text[..attribute.ValueStart] + ProjectXml.Escape(value) + text[attribute.ValueEnd..];

        if (item.Child(name) is { } child)
            return ProjectXml.InnerText(text, child).Trim() == value ? text : text[..child.TagEnd] + ProjectXml.Escape(value) + text[child.CloseStart..];

        string newLine = NewLine(text), indent = IndentOf(text, item.Start), unit = Unit(text, Root(text));
        string element = $"<{name}>{ProjectXml.Escape(value)}</{name}>";
        if (item.SelfClosing)
        {
            // <None Update="a.txt" /> opened up to hold it
            int slash = text.LastIndexOf('/', item.TagEnd - 1);
            return text[..slash].TrimEnd() + ">" + newLine + indent + unit + element + newLine + indent + $"</{item.Name}>" + text[item.TagEnd..];
        }

        if (item.Children.Count > 0)
        {
            ProjectXmlElement last = item.Children[^1];
            return text[..last.End] + newLine + IndentOf(text, last.Start) + element + text[last.End..];
        }

        return text[..item.TagEnd] + newLine + indent + unit + element + newLine + indent + text[item.CloseStart..];
    }

    /// <summary>A piece of metadata of an item taken off, its attribute or its element.</summary>
    private static string WithoutMetadata(string text, ProjectXmlElement item, string name)
    {
        if (item.Attribute(name) is { } attribute)
        {
            int from = attribute.Start;
            while (from > 0 && char.IsWhiteSpace(text[from - 1]))
                from--;
            return text[..from] + text[attribute.End..];
        }

        return item.Child(name) is { } child ? RemoveElement(text, child) : text;
    }

    /// <summary>
    /// After metadata came off a file's item, an Update with nothing left in it taken away, its ItemGroup too when that's
    /// empty, and an item with only white space between its tags closed on itself again.
    /// </summary>
    private static string Tidy(string text, ProjectXmlElement root, string itemType, string path)
    {
        foreach (string by in (string[])["Update", "Include"])
        {
            if (FileItem(root, itemType, path, by) is not { } item || item.Children.Count > 0)
                continue;

            bool bare = item.Attributes.All(attribute => attribute.Name.Equals(by, StringComparison.OrdinalIgnoreCase));
            if (by == "Update" && bare)
                return DropEmptyGroups(RemoveElement(text, item));

            if (!item.SelfClosing && text[item.TagEnd..item.CloseStart].Trim().Length == 0)
                return text[..(item.TagEnd - 1)].TrimEnd() + " />" + text[item.End..];
        }

        return text;
    }

    /// <summary>An element taken out, its whole line when it's alone on it, the line ending and the indentation with it.</summary>
    private static string RemoveElement(string text, ProjectXmlElement element)
    {
        int lineStart = element.Start;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
            lineStart--;
        int lineEnd = element.End;
        while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t')
            lineEnd++;

        bool aloneOnLine = (lineStart == 0 || text[lineStart - 1] == '\n') && (lineEnd >= text.Length || text[lineEnd] is '\r' or '\n');
        if (!aloneOnLine)
            return text[..element.Start] + text[element.End..];

        if (lineEnd < text.Length && text[lineEnd] == '\r')
            lineEnd++;
        if (lineEnd < text.Length && text[lineEnd] == '\n')
            lineEnd++;
        return text[..lineStart] + text[lineEnd..];
    }

    /// <summary>ItemGroups left with nothing in them but white space taken out, a blank line in front of one with it.</summary>
    private static string DropEmptyGroups(string text)
    {
        while (ProjectXml.Read(text) is { } root && root.Children.FirstOrDefault(group => group.Name == "ItemGroup" && group.Attributes.Count == 0
            && group.Children.Count == 0 && (group.SelfClosing || text[group.TagEnd..group.CloseStart].Trim().Length == 0)) is { } empty)
        {
            string without = RemoveElement(text, empty);

            // The blank line that kept it apart from what's before it goes too, two in a row are left as one
            int at = empty.Start;
            while (at > 0 && text[at - 1] is ' ' or '\t')
                at--;
            string before = without[..at];
            string newLine = NewLine(text);
            if (before.EndsWith(newLine + newLine, StringComparison.Ordinal) && (without[at..].StartsWith(newLine, StringComparison.Ordinal) || without[at..].TrimStart(' ', '\t').StartsWith("</", StringComparison.Ordinal)))
                without = before[..^newLine.Length] + without[at..];
            text = without;
        }

        return text;
    }

    /// <summary>
    /// An item put in, after the last item of an ItemGroup that has only items of its kind in it, or in a new ItemGroup at
    /// the end of the project when there's no such group.
    /// </summary>
    private static string AddItem(string text, ProjectXmlElement root, string itemType, string by, string path, List<(string Name, string Value)> metadata)
    {
        string newLine = NewLine(text), unit = Unit(text, root);
        ProjectXmlElement? group = root.Children.FirstOrDefault(group => group.Name == "ItemGroup" && group.Attributes.Count == 0 && group.Children.Count > 0
            && group.Children.All(item => item.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase)));

        if (group is not null)
        {
            ProjectXmlElement last = group.Children[^1];
            string indent = IndentOf(text, last.Start);
            return text[..last.End] + newLine + indent + ItemText(itemType, by, path, metadata, indent, unit, newLine) + text[last.End..];
        }

        return AddGroup(text, root, ItemText(itemType, by, path, metadata, ChildIndent(text, root) + unit, unit, newLine));
    }

    /// <summary>A new ItemGroup with an item in it, after everything else of the project, a blank line before it.</summary>
    private static string AddGroup(string text, ProjectXmlElement root, string item)
    {
        string newLine = NewLine(text), indent = ChildIndent(text, root), unit = Unit(text, root);
        string group = $"<ItemGroup>{newLine}{indent}{unit}{item}{newLine}{indent}</ItemGroup>";
        if (root.SelfClosing)
        {
            int slash = text.LastIndexOf('/', root.TagEnd - 1);
            return text[..slash].TrimEnd() + ">" + newLine + indent + group + newLine + "</Project>" + text[root.TagEnd..];
        }

        if (root.Children.Count == 0)
            return text[..root.TagEnd] + newLine + indent + group + newLine + text[root.CloseStart..].TrimStart(' ', '\t', '\r', '\n');

        ProjectXmlElement last = root.Children[^1];
        return text[..last.End] + newLine + newLine + indent + group + text[last.End..];
    }

    /// <summary>The text of an item, on one line when it has no metadata, its metadata as elements a line each otherwise.</summary>
    private static string ItemText(string itemType, string by, string path, List<(string Name, string Value)> metadata, string indent, string unit, string newLine)
    {
        var item = new StringBuilder($"<{itemType} {by}=\"{ProjectXml.Escape(path)}\"");
        if (metadata.Count == 0)
            return item.Append(" />").ToString();

        item.Append('>');
        foreach (var (name, value) in metadata)
            item.Append(newLine).Append(indent).Append(unit).Append($"<{name}>{ProjectXml.Escape(value)}</{name}>");
        return item.Append(newLine).Append(indent).Append($"</{itemType}>").ToString();
    }

    /// <summary>How the file ends a line, CRLF when it has one anywhere.</summary>
    internal static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>The white space in front of something on its line, empty when it isn't the first thing there.</summary>
    private static string IndentOf(string text, int at)
    {
        int start = at;
        while (start > 0 && text[start - 1] is ' ' or '\t')
            start--;
        return start == 0 || text[start - 1] == '\n' ? text[start..at] : string.Empty;
    }

    /// <summary>How far in the elements of the project are, two spaces when it has none to go by.</summary>
    private static string ChildIndent(string text, ProjectXmlElement root) =>
        root.Children.Count > 0 && IndentOf(text, root.Children[0].Start) is { Length: > 0 } indent ? indent : "  ";

    /// <summary>One step of indentation, as far as the project's elements are in from it.</summary>
    private static string Unit(string text, ProjectXmlElement root) => ChildIndent(text, root);
}
