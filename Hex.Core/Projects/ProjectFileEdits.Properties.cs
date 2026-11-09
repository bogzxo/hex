namespace Hex.Core.Projects;

// A property of the project itself set the way Visual Studio's project properties set one, its element in a
// PropertyGroup with no condition changed where it is, or put after the last property of the first such group when the
// project doesn't say it yet. Taken off, its element goes and the rest of the group stays.
internal static partial class ProjectFileEdits
{
    /// <summary>What the project's own text says a property is, the last unconditional one, null when it says nothing.</summary>
    public static string? Property(string text, string name) =>
        ProjectXml.Read(text) is { } root && PropertyElement(root, name) is { } element ? ProjectXml.InnerText(text, element).Trim() : null;

    /// <summary>
    /// A property of the project set, or taken off with null (or nothing). The same text back when nothing had to change.
    /// </summary>
    public static string SetProperty(string text, string name, string? value)
    {
        ProjectXmlElement root = Root(text);
        bool taken = value is not { Length: > 0 };
        if (PropertyElement(root, name) is { } element)
        {
            if (taken)
                return RemoveElement(text, element);
            if (element.SelfClosing)
                return text[..element.Start] + $"<{element.Name}>{ProjectXml.Escape(value!)}</{element.Name}>" + text[element.End..];
            return ProjectXml.InnerText(text, element).Trim() == value ? text : text[..element.TagEnd] + ProjectXml.Escape(value!) + text[element.CloseStart..];
        }

        if (taken)
            return text;

        string newLine = NewLine(text), unit = Unit(text, root);
        string property = $"<{name}>{ProjectXml.Escape(value!)}</{name}>";
        ProjectXmlElement? group = root.Children.FirstOrDefault(group => group.Name == "PropertyGroup" && group.Attributes.Count == 0 && group.Children.Count > 0);
        if (group is not null)
        {
            ProjectXmlElement last = group.Children[^1];
            return text[..last.End] + newLine + IndentOf(text, last.Start) + property + text[last.End..];
        }

        // A project with no group of properties gets one before everything else, where the SDK's templates have it
        string indent = ChildIndent(text, root);
        string made = $"<PropertyGroup>{newLine}{indent}{unit}{property}{newLine}{indent}</PropertyGroup>";
        if (root.SelfClosing)
        {
            int slash = text.LastIndexOf('/', root.TagEnd - 1);
            return text[..slash].TrimEnd() + ">" + newLine + indent + made + newLine + "</Project>" + text[root.TagEnd..];
        }

        if (root.Children.Count == 0)
            return text[..root.TagEnd] + newLine + indent + made + newLine + text[root.CloseStart..].TrimStart(' ', '\t', '\r', '\n');
        return text[..root.TagEnd] + newLine + newLine + indent + made + text[root.TagEnd..];
    }

    // The last element of a property in a PropertyGroup, both with no condition, the one MSBuild takes
    private static ProjectXmlElement? PropertyElement(ProjectXmlElement root, string name) =>
        root.Children.Where(group => group.Name == "PropertyGroup" && group.Attribute("Condition") is null)
            .SelectMany(group => group.Children)
            .LastOrDefault(element => element.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && element.Attribute("Condition") is null);
}
