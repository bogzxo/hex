namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    // Native definitions can mix project files and server-owned source. Keep the latter as URIs until selected;
    // neither the results list nor the ordinary file opener can read them from disk.
    private void ShowDefinitionPlaces(string title, LanguageServer server, IReadOnlyList<LanguageServerPlace> places,
        string word, int workspace, bool alignName = true)
    {
        var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
        var rows = new List<UITreeItem>();
        foreach (var group in places.GroupBy(place => place.Path).OrderBy(group => group.Key, PlatformPaths.Comparer))
        {
            string name = Path.GetFileName(group.Key);
            if (!group.First().OnDisk) name = Uri.UnescapeDataString(name);
            var file = new UITreeItem(name, "file-code")
            {
                Tooltip = group.Key,
                Note = group.First().OnDisk ? Path.GetDirectoryName(Path.GetRelativePath(server.RootDirectory, group.Key)) ?? string.Empty : string.Empty,
                Detail = group.Count().ToString(),
                Expanded = true,
                Children = []
            };
            foreach (var place in group.OrderBy(place => place.Line).ThenBy(place => place.Character))
            {
                string source = place.OnDisk ? LineOf(place, lines).TrimEnd('\r') : string.Empty;
                string text = source.Trim();
                int at = place.Character - (source.Length - source.TrimStart().Length);
                int length = place.EndLine == place.Line ? Math.Max(0, place.EndCharacter - place.Character) : 0;
                file.Add(new UITreeItem(text.Length > 0 ? text : word, "references")
                {
                    Detail = (place.Line + 1).ToString(),
                    Tooltip = $"{group.Key}:{place.Line + 1}",
                    Mark = length > 0 && at >= 0 && at + length <= text.Length ? (at, length) : default,
                    Tag = new SideTag(Pressed: () => SelectDefinition(server, place, word, workspace, alignName))
                });
            }
            rows.Add(file);
        }
        context.Window.ShowResultsTree(title, rows);
    }

    private void SelectDefinition(LanguageServer server, LanguageServerPlace place, string word, int workspace, bool alignName)
    {
        if (workspace != editWorkspaceGeneration || server.Failure is not null) return;
        // A click is a new navigation intent, even after moving to another document. Only its asynchronous answer
        // belongs to that document/caret snapshot. Result rows must not retain the document that produced them.
        var request = CaptureNavigation(context.Documents.Current, server);
        if (!place.OnDisk) OpenSource(server, place, word, request, alignName);
        else context.Documents.OpenAt(place.Path, place.Line, alignName ? NameColumn(place, word) : place.Character);
    }
}
