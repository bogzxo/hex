namespace Hex.Core.Projects;

/// <summary>
/// The edits Visual Studio makes to an SDK style project when a file's properties change, made the same way, on the
/// text. A file's Copy to Output Directory is an Update item of its kind with the metadata in it, in an ItemGroup of
/// items of that kind, and set back to Do not copy it goes again. A file's Build Action moved from one kind to another
/// is a Remove of the old kind (or its Include taken out) and an Include of the new one (or a Remove of it taken out).
/// Everything else in the file stays as it was, its comments, its indentation and its line endings. Nothing here reads
/// or writes the disk, a path is the file as the project writes it, relative to the project's folder.
/// </summary>
internal static partial class ProjectFileEdits
{
    /// <summary>The metadata Copy to Output Directory is.</summary>
    public const string COPY_TO_OUTPUT = "CopyToOutputDirectory";

    // The attributes of an item that say which files it is or when, not metadata of it
    private static readonly HashSet<string> NotMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "Include", "Update", "Remove", "Exclude", "Condition", "KeepMetadata", "RemoveMetadata", "KeepDuplicates", "MatchOnMetadata", "MatchOnMetadataOptions"
    };

    /// <summary>
    /// A piece of metadata of a file's item set, or taken off again with null. The item of the project that is that file
    /// alone gets it, or else an Update item of the kind is written for it. An Update item left with nothing in it goes,
    /// and its ItemGroup with it when that's empty. The same text back when nothing had to change.
    /// </summary>
    public static string SetMetadata(string text, string itemType, string path, string name, string? value)
    {
        if (ProjectXml.Read(text) is not { } root)
            throw new FormatException("the project file isn't XML that can be edited");

        if ((FileItem(root, itemType, path, "Update") ?? FileItem(root, itemType, path, "Include")) is not { } item)
            return value is null ? text : AddItem(text, root, itemType, "Update", path, [(name, value)]);

        if (value is not null)
            return WithMetadata(text, item, name, value);

        string taken = WithoutMetadata(text, item, name);
        return ProjectXml.Read(taken) is { } after ? Tidy(taken, after, itemType, path) : taken;
    }

    /// <summary>
    /// A file's Build Action changed, the item of one kind (null when it's in none) made an item of another. What it had
    /// of its own metadata (Copy to Output Directory) goes with it.
    /// </summary>
    public static string SetItemType(string text, string path, string? from, string to)
    {
        if (from is not null && from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return text;

        var carried = new List<(string Name, string Value)>();
        if (from is not null)
        {
            // Its own Include taken out, or a Remove of it when it came from a glob or an import
            if (FileItem(Root(text), from, path, "Include") is { } include)
            {
                Carry(text, include, carried);
                text = RemoveElement(text, include);
                text = DropEmptyGroups(text);
            }
            else if (ItemWithSpec(Root(text), from, path, "Include") is { } shared)
            {
                // One of a list of files, a.txt;b.txt, the others keep it
                Carry(text, shared, carried);
                ProjectXmlAttribute list = shared.Attribute("Include")!;
                string rest = string.Join(";", Specs(list.Value).Where(spec => !SamePath(spec, path)));
                text = text[..list.ValueStart] + ProjectXml.Escape(rest) + text[list.ValueEnd..];
            }
            else
            {
                text = AddItem(text, Root(text), from, "Remove", path, []);
            }

            while (FileItem(Root(text), from, path, "Update") is { } update)
            {
                Carry(text, update, carried);
                text = DropEmptyGroups(RemoveElement(text, update));
            }
        }

        // A Remove of the new kind taken out gives it back where a glob had it, an Include otherwise
        if (FileItem(Root(text), to, path, "Remove") is { } remove)
        {
            text = DropEmptyGroups(RemoveElement(text, remove));
            return carried.Count > 0 ? AddItem(text, Root(text), to, "Update", path, carried) : text;
        }

        return AddItem(text, Root(text), to, "Include", path, carried);
    }

    /// <summary>
    /// An Update item of a file with a piece of metadata, in an ItemGroup of its own at the end of the project, after
    /// everything else of the file. What's done when the usual place didn't win, a glob's Update further down.
    /// </summary>
    public static string AppendUpdate(string text, string itemType, string path, string name, string value)
    {
        ProjectXmlElement root = Root(text);
        return AddGroup(text, root, ItemText(itemType, "Update", path, [(name, value)], ChildIndent(text, root) + Unit(text, root), Unit(text, root), NewLine(text)));
    }

    /// <summary>Whether the project's own text says anything of a piece of metadata for a file, an item of it that sets it.</summary>
    public static bool SaysMetadata(string text, string path, string name)
    {
        if (ProjectXml.Read(text) is not { } root)
            return false;

        foreach (ProjectXmlElement item in Items(root))
        {
            bool its = item.Attribute("Update") is { } update && Specs(update.Value).Any(spec => SamePath(spec, path))
                || item.Attribute("Include") is { } include && Specs(include.Value).Any(spec => SamePath(spec, path));
            if (its && (item.Attribute(name) is not null || item.Child(name) is not null))
                return true;
        }

        return false;
    }

    /* Finding the items of a file */

    private static ProjectXmlElement Root(string text) => ProjectXml.Read(text) ?? throw new FormatException("the project file isn't XML that can be edited");

    /// <summary>Every item of the project, the elements in its ItemGroups.</summary>
    internal static IEnumerable<ProjectXmlElement> Items(ProjectXmlElement root) =>
        root.Children.Where(group => group.Name == "ItemGroup").SelectMany(group => group.Children);

    /// <summary>The last item of a kind that is that file alone by an attribute (Include, Update, Remove), with no condition on it.</summary>
    private static ProjectXmlElement? FileItem(ProjectXmlElement root, string itemType, string path, string by) =>
        Items(root).LastOrDefault(item => item.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase) && Unconditional(item)
            && item.Attribute(by) is { } attribute && Specs(attribute.Value) is [var only] && SamePath(only, path));

    /// <summary>An item of a kind that has the file among a list of them.</summary>
    private static ProjectXmlElement? ItemWithSpec(ProjectXmlElement root, string itemType, string path, string by) =>
        Items(root).LastOrDefault(item => item.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase) && Unconditional(item)
            && item.Attribute(by) is { } attribute && Specs(attribute.Value).Any(spec => SamePath(spec, path)));

    private static bool Unconditional(ProjectXmlElement item) => item.Attribute("Condition") is null && item.Parent?.Attribute("Condition") is null;

    private static string[] Specs(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether two paths of a project are the same file, whichever way the slashes lean, MSBuild doesn't mind case.</summary>
    internal static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimStart('.', '\\'), b.Replace('/', '\\').TrimStart('.', '\\'), StringComparison.OrdinalIgnoreCase)
        && !a.Contains('*') && !b.Contains('*');

    // The metadata an item has, its attributes that aren't about which files and its child elements
    private static void Carry(string text, ProjectXmlElement item, List<(string Name, string Value)> into)
    {
        foreach (ProjectXmlAttribute attribute in item.Attributes.Where(attribute => !NotMetadata.Contains(attribute.Name)))
            Keep(into, attribute.Name, attribute.Value);
        foreach (ProjectXmlElement child in item.Children)
            Keep(into, child.Name, ProjectXml.InnerText(text, child).Trim());
    }

    private static void Keep(List<(string Name, string Value)> into, string name, string value)
    {
        into.RemoveAll(kept => kept.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        into.Add((name, value));
    }
}
