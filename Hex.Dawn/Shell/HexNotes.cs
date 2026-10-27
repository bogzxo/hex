using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// What a list of the side bar says while there's nothing in it, the one way every view says it. An icon, the text
/// where the rows' text starts, in the dim colour, going onto as many rows as it takes at the width of the side bar.
/// The explorer, search, the outline, Source Control and the Debug side each had their own, at their own place and in
/// their own grey.
/// </summary>
internal static class HexNotes
{
    /// <summary>The usual icon of a note, the round i.</summary>
    internal const string ICON = "info";

    /// <summary>A note saying something, with an icon, in the dim colour the skin has.</summary>
    internal static UITreeItem Note(string text, Vector4 dim, string icon = ICON) => new(text, icon)
    {
        Color = dim,
        IconColor = dim,
        Wraps = true
    };

    /// <summary>
    /// A row under a note that does something when pressed, New File and New Folder under an empty explorer, written
    /// in the accent so it reads as a link to press.
    /// </summary>
    internal static UITreeItem Link(string text, string icon, Vector4? accent, Action pressed) => new(text, icon)
    {
        Color = accent,
        IconColor = accent,
        Tag = new SideTag(Pressed: pressed)
    };
}
