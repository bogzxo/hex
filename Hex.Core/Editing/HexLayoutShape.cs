namespace Hex.Core;

/// <summary>
/// One component of a layout the way the designer sees it, its name in the code, its kind, everything about it that
/// isn't how a component of that kind starts out (its parent among them, as "parent"), and the declaration Hex writes
/// for it, the one that goes into the code when it's new.
/// </summary>
/// <param name="Properties">Each property by name with its value written out, lines and all at no indentation.</param>
internal sealed record HexLayoutPart(string Name, string Kind, IReadOnlyList<KeyValuePair<string, string>> Properties, string Declaration)
{
    /// <summary>What it's inside of, by name, null for the screen itself.</summary>
    public string? Parent => Value("parent");

    /// <summary>A property as written, null if it is how it starts out.</summary>
    public string? Value(string key)
    {
        foreach (var (name, value) in Properties)
        {
            if (name == key)
                return value;
        }

        return null;
    }
}

/// <summary>
/// A layout as what the designer can change in it, every component in the order the designer walks them (parents
/// before their children) and the two calls that aren't components, the screen it's made for and the layers it starts
/// with hidden. Two of them side by side are what a change to the layout was, which <see cref="HexLayoutEdits"/> turns
/// into edits of the code somebody wrote rather than a new copy of it.
/// </summary>
/// <param name="Design">The compositor.design call as Hex writes it, null when there's none.</param>
/// <param name="Hidden">The compositor.hidden_layers call as Hex writes it, null when there's none.</param>
internal sealed record HexLayoutShape(IReadOnlyList<HexLayoutPart> Parts, string? Design, string? Hidden)
{
    public HexLayoutPart? Find(string name)
    {
        foreach (HexLayoutPart part in Parts)
        {
            if (part.Name == name)
                return part;
        }

        return null;
    }

    /// <summary>The components inside of one, or on the screen itself for null, in the order they're in.</summary>
    public IEnumerable<HexLayoutPart> ChildrenOf(string? parent) => Parts.Where(part => part.Parent == parent);

    /// <summary>The same layout with a component going by another name, and whatever is inside of it saying so.</summary>
    public HexLayoutShape Renamed(string from, string to) => this with
    {
        Parts = [.. Parts.Select(part => part with
        {
            Name = part.Name == from ? to : part.Name,
            Properties = [.. part.Properties.Select(property => property.Key == "parent" && property.Value == from
                ? new KeyValuePair<string, string>(property.Key, to) : property)]
        })]
    };
}
