namespace Hex.Core.Projects;

internal static partial class ProjectFileEdits
{
    public static string AddProjectReference(string text, string include) =>
        AddItem(text, Root(text), "ProjectReference", "Include", include, []);

    public static string RemoveProjectReference(string text, Func<string, bool> matches)
    {
        // Work backwards so every XML source offset remains valid. Preserve the other
        // entries and metadata in a semicolon-delimited Include.
        var items = ReferenceItems(Root(text)).OrderByDescending(item => item.Start);
        foreach (var item in items)
        {
            if (item.Attribute("Include") is not { } include) continue;
            string[] specs = include.Value.Split(';');
            string[] kept = specs.Where(spec => !matches(spec.Trim())).ToArray();
            if (kept.Length == specs.Length) continue;
            text = kept.Length == 0 ? RemoveElement(text, item)
                : text[..include.ValueStart] + ProjectXml.Escape(string.Join(';', kept)) + text[include.ValueEnd..];
        }
        return text;
    }

    internal static IEnumerable<ProjectXmlElement> ReferenceItems(ProjectXmlElement root) =>
        root.Children.SelectMany(item => item.Name == "ProjectReference" ? [item] : ReferenceItems(item));
}
