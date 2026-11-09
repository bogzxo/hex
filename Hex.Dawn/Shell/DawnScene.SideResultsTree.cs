using Horizon.UI.Components;

namespace Hex.Dawn;

// A tree in the side bar's search view in place of what was found, the call hierarchy's (HexLanguages.HierarchyTree).
// It goes with the list of results it was shown over, so anything that lists results of its own (a search typed, Find
// All References) has the tree gone without being told.
internal sealed partial class DawnScene
{
    // The rows and the results they were shown with, null for none
    private (List<HexResult> Of, List<UITreeItem> Rows)? resultsTree;

    /// <summary>Whether what's listed is the tree, the results it went with still being the ones shown.</summary>
    private bool ResultsTreeShown => resultsTree is { } tree && ReferenceEquals(tree.Of, results);

    private void ShowResultsTree(string title, List<UITreeItem> rows)
    {
        List<HexResult> none = [];
        ShowResults(title, none);
        resultsTree = (none, rows);
        treeDirty = true;
    }

    // The rows drawn again as they are, a row that was waiting for its children has them
    private void RefreshResultsTree()
    {
        if (ResultsTreeShown && Side == HexSide.Search)
            sideTree.Refresh();
    }
}
