using System.Text.RegularExpressions;

using Horizon.Core.Platform;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What was found, the search view's (Ctrl+Shift+F) and the references of a name (Shift+F12), listed in the side tree by
// the file it is in. Every match is a row of its own, the file's row says how many it has, and the X on a row that's
// hovered dismisses it from the list (a file's every match of it), nothing on the disk changes. With something in the
// replace box each match shows what it turns into after it, in the accent.
internal sealed partial class DawnScene
{
    // The files of the results that were closed, so dismissing a match doesn't open them all again, and the results
    // they're of, forgotten when new ones come
    private readonly HashSet<string> resultsClosed = new(PlatformPaths.Comparer);
    private List<HexResult>? resultsListed;

    private void RebuildResults()
    {
        treeTitle.Text = results is null ? Said("side.search") : resultsTitle;
        UseSideTree(true);
        ShowSearchMessage();

        if (!ReferenceEquals(results, resultsListed))
        {
            resultsClosed.Clear();
            resultsListed = results;
        }

        // Nothing looked for yet, the boxes over the tree and the hint under them say what to do
        if (results is null)
        {
            sideTree.Items = [];
            return;
        }

        // What Replace All would make of each match, while the replace row of the view's own results has something in it
        string? with = null;
        Regex? pattern = null;
        if (resultsFound is { } found && searchView is not null && searchReplaceRow.Visible && searchReplace.Text.Length > 0)
        {
            with = searchReplace.Text;
            if (found.Options.Regex)
                pattern = HexSearch.Pattern(found.Query, found.Options, out _);
        }

        var items = new List<UITreeItem>();
        var byFile = new Dictionary<string, UITreeItem>(PlatformPaths.Comparer);
        foreach (HexResult result in results)
        {
            // Under its file a row is what's on the line, the line's number quiet after it. It was the file and the line
            // again in front of that, "app.d:3  struct Point  3"
            string marker = $":{result.Line + 1}  ";
            int cut = result.Text.IndexOf(marker, StringComparison.Ordinal);
            string said = (cut >= 0 ? result.Text[(cut + marker.Length)..] : result.Text).Trim();

            if (!byFile.TryGetValue(result.Path, out UITreeItem? file))
            {
                file = ResultsFileRow(result.Path, cut >= 0 ? Path.GetDirectoryName(result.Text[..cut])?.Replace('\\', '/') ?? string.Empty : string.Empty);
                byFile[result.Path] = file;
                items.Add(file);
            }

            file.Add(ResultRow(result, said, with, pattern));
        }

        foreach (UITreeItem file in items)
            file.Detail = file.Children!.Count.ToString();

        if (items.Count == 0 && resultsFound is null)
            items.Add(NoteItem(Said("side_tree.results_empty"), DimColor));

        // Finding stops at its most, said over them so nobody takes the list for all there is
        if (results.Count >= HexSearch.MOST_FOUND)
            items.Insert(0, NoteItem(ResultsMostText, DimColor));

        sideTree.Items = items;
    }

    private UITreeItem ResultsFileRow(string path, string folder) => new(Path.GetFileName(path), HexExplorer.FileIcon(path))
    {
        IconColor = explorer.FileIconColor(path),
        Note = folder,
        Tooltip = path,
        Expanded = !resultsClosed.Contains(path),
        Children = [],
        Actions = [new UITreeAction("close", Said("search.dismiss_file"), () => DismissResults(path, null))],
        Tag = new SideTag(Toggled: toggled =>
        {
            if (toggled.Expanded) resultsClosed.Remove(path);
            else resultsClosed.Add(path);
        })
    };

    private UITreeItem ResultRow(HexResult result, string said, string? with, Regex? pattern)
    {
        bool marked = result.Found > 0 && result.FoundAt >= 0 && result.FoundAt + result.Found <= said.Length;
        var row = new UITreeItem(said, string.Empty)
        {
            // What was found lit in the line, where it is once the line's indent is gone
            Mark = marked ? (result.FoundAt, result.Found) : default,
            Detail = $"{result.Line + 1}",
            Tooltip = $"{result.Path}:{result.Line + 1}",
            Actions = [new UITreeAction("close", Said("search.dismiss"), () => DismissResults(result.Path, result))],
            Tag = new SideTag(Pressed: () => OpenResult(result))
        };

        // What it turns into right after it, the match still lit behind and the replacement in the accent
        if (with is not null && marked)
        {
            int end = result.FoundAt + result.Found;
            string swapped = HexSearch.Replacement(said, result.FoundAt, result.Found, with, pattern);
            row.Text = said.Insert(end, swapped);
            row.Lit = [(end, swapped.Length)];
        }

        return row;
    }

    /// <summary>
    /// A result opened where it is, the search view kept on the side. The designer and the code each remember a side of
    /// their own, and a layout's search opening a file of code put the code's explorer up in its place.
    /// </summary>
    private void OpenResult(HexResult result)
    {
        OpenAt(result.Path, result.Line, result.Column);
        if (Side != HexSide.Search)
            Side = HexSide.Search;
    }

    /// <summary>A match taken off the list, or every match of a file without one. Only the list, the file is as it was.</summary>
    private void DismissResults(string path, HexResult? match)
    {
        if (results is null)
            return;

        if (match is { } one)
            results.Remove(one);
        else
            results.RemoveAll(result => PlatformPaths.Comparer.Equals(result.Path, path));
        treeDirty = true;
    }

    /// <summary>The line over the results when finding stopped at its most.</summary>
    private string ResultsMostText => Said("side_tree.results_most", ("count", HexSearch.MOST_FOUND.ToString()));
}
