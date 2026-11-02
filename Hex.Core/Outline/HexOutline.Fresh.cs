namespace Hex.Core;

// The outline made fresh every time, for the agent harness's index which outlines files off the editor's thread. The
// one Outline keeps for the editor is a single static slot, two threads in it at once would hand each other's back.
internal static partial class HexOutline
{
    /// <summary>The outline of a file's text by its path's extension, nothing kept, safe on any thread.</summary>
    public static List<OutlineEntry> Fresh(string text, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".hor" ? OutlineOf(text) : OutlineOfOther(text, extension);
    }
}
