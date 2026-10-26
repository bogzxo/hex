using Horizon.UI;

namespace Hex.Dawn;

// Replace in Files, the search view's Replace All. Asked once with how many matches in how many files, then every match
// still listed swapped. A file open in Hex is changed through its document, one step of its undo and not saved, the way
// VS Code leaves it, every other file is written on the disk as it was but for the matches (see HexSearch.ReplaceIn)
internal sealed partial class DawnScene
{
    /// <summary>Every match listed swapped for what's in the replace box, once it's said yes to.</summary>
    private void ReplaceAllInFiles()
    {
        if (resultsFound is not { } found || results is not { Count: > 0 } listed)
        {
            Say(Said("search.nothing_to_replace"), error: true);
            return;
        }

        string with = searchReplace.Text;
        List<HexResult> taken = [.. listed];
        UIDialog.Show(chrome, Said("search.replace_title"),
            Said("search.replace_ask", ("matches", Matches(taken.Count)), ("files", HexText.Count("file", ResultFiles(taken))), ("with", with)),
            new DialogChoice(Said("search.replace_go"), () => ReplaceListed(taken, found.Query, with, found.Options)),
            new DialogChoice(Said("keys.leave_it")));
    }

    private void ReplaceListed(List<HexResult> taken, string query, string with, UIFindOptions options)
    {
        int open = 0;
        List<HexReplaced> done = HexSearch.ReplaceIn(taken, query, with, options,
            path => OpenDocument(path) is { } opened ? opened.Editor.Text : null,
            (path, text) =>
            {
                if (OpenDocument(path) is not { } opened)
                    return;
                Rewrite(opened, text);

                // The one on screen too, the search after this looks through what has changes by it
                opened.Modified = opened.Editor.Text != opened.SavedCode;
                open++;
            });

        foreach (HexReplaced failed in done.Where(file => file.Error is not null))
            Notify(Said("search.replace_failed", ("file", Relative(failed.Path)), ("error", failed.Error!)), error: true);

        int count = done.Sum(file => file.Count);
        Say(Said("search.replaced", ("matches", Matches(count)), ("files", HexText.Count("file", done.Count(file => file.Count > 0))))
            + (open > 0 ? Said("search.replaced_open", ("count", open.ToString())) : string.Empty), error: count == 0);

        // What's left of it looked for again, the open files as they are now
        SearchNow();
    }
}
