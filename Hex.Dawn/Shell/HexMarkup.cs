using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>What somebody wrote, put in a label that reads markup without its brackets being taken for it.</summary>
internal static class HexMarkup
{
    /// <summary>What somebody wrote, without the brackets a label would take for its markup.</summary>
    public static string Plain(string text) => text.Replace('[', '(').Replace(']', ')');

    /// <summary>What somebody wrote as it is, unless a label would take some of it for its markup.</summary>
    public static string Literal(string text) => UIRichText.HasTags(text) ? Plain(text) : text;
}
