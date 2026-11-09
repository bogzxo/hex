namespace Hex.Dawn;

// What a fix would change, under the list of Ctrl+.'s fixes while it's the one highlighted, the lines it takes out and
// the ones it puts in. Visual Studio shows that by its lightbulb before anything's picked, and here a fix was a title to
// take on trust.
internal sealed partial class HexLanguages
{
    // The most lines of each, taken out and put in, a preview shows
    private const int PREVIEW_LINES = 4;

    /// <summary>
    /// What a fix would do to the file it's for, markdown, its edits made to the text the way picking it makes them and
    /// the lines that changed. Empty for a fix that's a command of the server's with no edits to show.
    /// </summary>
    private string FixPreview(string? path, string text, LanguageServerAction action)
    {
        var shown = new List<string>();
        int elsewhere = 0;
        foreach (var (file, edits) in action.Edits)
        {
            if (edits.Count == 0)
                continue;

            if (path is null || !PlatformPaths.Same(file, path))
            {
                elsewhere++;
                continue;
            }

            ChangedLines(text, LanguageServer.Apply(text, SortUsings(text, edits)), shown);
        }

        if (shown.Count == 0 && elsewhere == 0)
            return string.Empty;

        string preview = shown.Count > 0 ? "```\n" + string.Join('\n', shown) + "\n```" : string.Empty;
        if (elsewhere > 0)
            preview += (preview.Length > 0 ? "\n" : string.Empty) + context.Strings.Said("code_server.fix_changes_elsewhere", ("files", HexText.Count("file", elsewhere)));
        return preview;
    }

    /// <summary>
    /// The lines that differ between two texts, the ones before them and after them alike left out, those taken out as
    /// "- " and those put in as "+ " with the indent they share taken off. A few of each at most, "..." for the rest.
    /// </summary>
    internal static void ChangedLines(string before, string after, List<string> into)
    {
        string[] was = before.Split('\n'), now = after.Split('\n');
        int same = 0;
        while (same < was.Length && same < now.Length && was[same].TrimEnd('\r') == now[same].TrimEnd('\r'))
            same++;

        int end = 0;
        while (end < was.Length - same && end < now.Length - same && was[^(end + 1)].TrimEnd('\r') == now[^(end + 1)].TrimEnd('\r'))
            end++;

        var taken = was[same..(was.Length - end)].Select(line => line.TrimEnd('\r')).ToList();
        var put = now[same..(now.Length - end)].Select(line => line.TrimEnd('\r')).ToList();
        if (taken.Count == 0 && put.Count == 0)
            return;

        int indent = taken.Concat(put).Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        void Add(List<string> lines, string mark)
        {
            foreach (string line in lines.Take(PREVIEW_LINES))
                into.Add(mark + (line.Length >= indent ? line[indent..] : line.TrimStart()));
            if (lines.Count > PREVIEW_LINES)
                into.Add(mark + "...");
        }

        Add(taken, "- ");
        Add(put, "+ ");
    }
}
