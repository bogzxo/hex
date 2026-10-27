using System.Text.RegularExpressions;

using Horizon.Core.Platform;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The search view of the side bar (Assets/hex/search_view.hor), VS Code's. Ctrl+Shift+F was a dialog asking for a line
// of text, the find bar's toggles said in its message and changed nowhere, and the first match of every line listed.
// Now the box is over the results and they come in as it's typed, a quarter of a second after the last key, off the
// editor's thread with the newest search the one shown. The toggles are the find bar's own, the files to include and
// to exclude narrow it, a regex that doesn't build says why in red, and the chevron opens Replace in Files.
internal sealed partial class DawnScene
{
    private const string SEARCH_VIEW = "search_view.hor";

    // How long typing is waited out before it's looked for
    private const float SEARCH_WAIT = 0.25f;

    private StackPanel? searchView;
    private TextBox searchQuery = null!, searchReplace = null!, searchInclude = null!, searchExclude = null!;
    private Button searchExpand = null!, searchCase = null!, searchWord = null!, searchRegex = null!, searchMore = null!;
    private Panel searchReplaceRow = null!;
    private StackPanel searchScope = null!;
    private Label searchMessage = null!;

    // How long until what was typed is looked for, below 0 for nothing waiting, and the number of the newest search, an
    // answer to an older one is dropped
    private float searchWait = -1.0f;
    private int searchAsked;

    // What the listed results were found with, for Replace All and the rows' preview of it, null for results that came
    // from somewhere else (Shift+F12). And what was wrong with the regex, which the line under the boxes says in red
    private (string Query, UIFindOptions Options)? resultsFound;
    private string? searchError;

    // The toggles as the view last showed them, put right when the find bar changes them
    private UIFindOptions searchShown;

    /// <summary>The view over the side tree, made the first time search is shown, and shown only on search.</summary>
    private void PlaceSearchView(bool shown)
    {
        if (searchView is null)
        {
            if (!shown)
                return;
            BuildSearchView();
        }

        searchView!.Visible = shown;
    }

    private void BuildSearchView()
    {
        UILayout made = layout.Instantiate(SEARCH_VIEW, left);
        searchView = made.Get<StackPanel>("search_view");
        searchQuery = made.Get<TextBox>("search_query");
        searchReplace = made.Get<TextBox>("search_replace");
        searchInclude = made.Get<TextBox>("search_include");
        searchExclude = made.Get<TextBox>("search_exclude");
        searchExpand = made.Get<Button>("search_expand");
        searchCase = made.Get<Button>("search_case");
        searchWord = made.Get<Button>("search_word");
        searchRegex = made.Get<Button>("search_regex");
        searchMore = made.Get<Button>("search_more");
        searchReplaceRow = made.Get<Panel>("search_replace_row");
        searchScope = made.Get<StackPanel>("search_scope");
        searchMessage = made.Get<Label>("search_message");

        // Over the tree, the layout put it at the end of the side bar
        int tree = -1;
        for (int i = 0; i < left.Children.Count && tree < 0; i++)
        {
            if (left.Children[i] == sideTree)
                tree = i;
        }
        left.MoveChild(searchView, tree);

        searchQuery.OnChanged = _ => searchWait = SEARCH_WAIT;
        searchQuery.OnSubmitted = _ => SearchNow();
        searchInclude.OnChanged = searchExclude.OnChanged = _ => searchWait = SEARCH_WAIT;
        searchInclude.OnSubmitted = searchExclude.OnSubmitted = _ => SearchNow();
        searchReplace.OnChanged = _ => treeDirty |= Side == HexSide.Search;
        searchCase.OnPressed = () => ToggleSearch(codeEditing.findOptions with { MatchCase = !codeEditing.findOptions.MatchCase });
        searchWord.OnPressed = () => ToggleSearch(codeEditing.findOptions with { WholeWord = !codeEditing.findOptions.WholeWord });
        searchRegex.OnPressed = () => ToggleSearch(codeEditing.findOptions with { Regex = !codeEditing.findOptions.Regex });
        searchExpand.OnPressed = () => ShowSearchReplace(!searchReplaceRow.Visible);
        searchMore.OnPressed = () => ShowSearchScope(!searchScope.Visible);
        made.Get<Button>("search_replace_all").OnPressed = ReplaceAllInFiles;

        // A tick of its own, for the wait after typing and for toggles the find bar changed
        searchView.Add(new HexTicker { Visible = false, Ticked = TickSearchView });
        ShowSearchToggles();
        ShowSearchMessage();
    }

    /// <summary>Ctrl+Shift+F, the search view with its box given the keys and what's in it picked out, what's selected on a line looked for.</summary>
    private void FindInFiles() => ShowSearchView(replacing: false);

    /// <summary>Ctrl+Shift+H, the same with the row to replace with open.</summary>
    private void ReplaceInFiles() => ShowSearchView(replacing: true);

    private void ShowSearchView(bool replacing)
    {
        left.Show(true);
        Side = HexSide.Search;
        RebuildTree();
        treeDirty = false;
        if (searchView is null)
            return;

        if (replacing)
            ShowSearchReplace(true);

        // What's selected on one line of the code is what's looked for, as itself when the toggles are on regexes
        UITextDocument text = code.Document;
        if (codeScroll.Visible && text.HasSelection && text.SelectionStart.Line == text.SelectionEnd.Line)
        {
            string seed = codeEditing.findOptions.Regex ? Regex.Escape(text.SelectedText) : text.SelectedText;
            if (seed != searchQuery.Text)
            {
                searchQuery.Text = seed;
                SearchNow();
            }
        }

        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        searchQuery.Focus();
        searchQuery.SelectAll();
    }

    private void ShowSearchReplace(bool shown)
    {
        searchReplaceRow.Visible = shown;
        searchExpand.Icon = shown ? "chevron-down" : "chevron-right";
        treeDirty |= Side == HexSide.Search;
    }

    private void ShowSearchScope(bool shown)
    {
        searchScope.Visible = shown;
        searchMore.Selected = shown;
    }

    // A toggle of the view's is the find bar's as well, the bar's buttons and what it lights in the code go along
    private void ToggleSearch(UIFindOptions options)
    {
        codeEditing.findOptions = options;
        codeEditing.findCase.Selected = options.MatchCase;
        codeEditing.findWord.Selected = options.WholeWord;
        codeEditing.findRegex.Selected = options.Regex;
        if (codeEditing.FindBarOpen)
            code.FindOptions = options;

        ShowSearchToggles();
        SearchNow();
    }

    private void ShowSearchToggles()
    {
        searchShown = codeEditing.findOptions;
        searchCase.Selected = searchShown.MatchCase;
        searchWord.Selected = searchShown.WholeWord;
        searchRegex.Selected = searchShown.Regex;
    }

    private void TickSearchView(float dt)
    {
        // The find bar's toggles changed, the view's show it and what it found is looked for again the new way
        if (codeEditing.findOptions != searchShown)
        {
            ShowSearchToggles();
            if (resultsFound is not null && searchQuery.Text.Length > 0)
                SearchNow();
        }

        if (searchWait >= 0.0f && (searchWait -= dt) < 0.0f)
            SearchNow();
    }

    /// <summary>What's in the box looked for now, in the background. An empty box is no results and the hint.</summary>
    private void SearchNow()
    {
        if (searchView is null)
            return;

        searchWait = -1.0f;
        int asked = ++searchAsked;
        string query = searchQuery.Text;
        if (query.Length == 0)
        {
            results = null;
            resultsFound = null;
            searchError = null;
            treeDirty = true;
            ShowSearchMessage();
            return;
        }

        List<string> roots = WorkspaceRoots();
        UIFindOptions options = codeEditing.findOptions;
        string include = searchInclude.Text, exclude = searchExclude.Text;
        Dictionary<string, string> open = ChangedTexts();

        InBackground(Said("search.looking", ("query", query), ("where", DescribeRoots())),
            () => HexSearch.Search(roots, query, options, include, exclude, open, () => Volatile.Read(ref searchAsked) != asked),
            outcome =>
            {
                // A newer search was asked for while this one went, its answer is the one wanted
                if (asked != searchAsked)
                    return;

                results = outcome.Found;
                resultsTitle = Said("side.search");
                resultsFound = (query, options);
                searchError = outcome.Error;
                treeDirty = true;
                treeScroll.Offset = 0;
                ShowSearchMessage();
            }, quiet: true);
    }

    /// <summary>The text of every open file with changes not saved yet by its full path, looked through rather than the disk.</summary>
    private Dictionary<string, string> ChangedTexts()
    {
        var open = new Dictionary<string, string>(PlatformPaths.Comparer);
        foreach (HexDocument each in documents)
        {
            if (each.Modified && each.Path is { } path)
                open[Path.GetFullPath(path)] = each.Editor.Text;
        }

        return open;
    }

    /// <summary>
    /// The line under the boxes, a regex's error in red, how many results in how many files or that there are none. The
    /// hint before anything was looked for is the tree's note, the way every view of the side says what it's for.
    /// </summary>
    private void ShowSearchMessage()
    {
        if (searchView is null)
            return;

        bool error = searchError is not null;
        searchMessage.Color = error ? ErrorColor : DimColor;
        searchMessage.Text = searchError is { } bad ? Said("search.bad_regex", ("error", bad))
            : results is null ? string.Empty
            : results.Count == 0 ? Said("search.none")
            : Said("search.summary", ("results", HexText.Count("result", results.Count)), ("files", HexText.Count("file", ResultFiles(results))));
        searchMessage.Visible = searchMessage.Text.Length > 0;
    }

    private static int ResultFiles(List<HexResult> found) => found.Select(result => result.Path).Distinct(PlatformPaths.Comparer).Count();

    /// <summary>So many matches, said the way the counts are.</summary>
    private static string Matches(int count) => count == 1 ? Said("search.match_one") : Said("search.match_many", ("count", count.ToString()));
}
