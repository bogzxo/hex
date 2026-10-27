using Key = Silk.NET.Input.Key;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The find bar, laid over the top right of the code (Assets/hex/find_bar.hor). It was a dialog, enter found the next one
// and the dialog went, nothing was lit, there was no count, no case, no whole words, no regex, and replacing was two
// dialogs ending in replace all. Now every match is lit as the query is typed, the match grows from where the bar was
// opened rather than hopping on with every letter, "3 of 17", enter and shift enter, three toggles, a row to replace
// one at a time or all of them, and escape puts it away with the caret left on the match.
internal sealed partial class HexCodeEditing
{
    private const string FIND_BAR = "find_bar.hor";

    internal Panel findBar = null!;
    internal TextBox findBox = null!, replaceBox = null!;
    internal Button findCase = null!, findWord = null!, findRegex = null!, findExpand = null!;
    internal Label findCount = null!;
    internal StackPanel replaceRow = null!;

    // How it's looked for, and where the caret was when the bar was opened, which the match grows from
    internal UIFindOptions findOptions;
    private UITextPosition findStart;

    // What the count says, written again only when it changes, and the editor the bar was last looking in
    private (int Index, int Count, bool Error) findCountShown = (-2, -2, false);
    private CodeEditor? findEditor;

    internal bool FindBarOpen => findBar.Visible;

    internal void BuildFindBar()
    {
        UILayout made = layout.Instantiate(FIND_BAR, codeScroll);
        findBar = made.Get<Panel>("find_bar");
        findBox = made.Get<TextBox>("find_query");
        replaceBox = made.Get<TextBox>("replace_with");
        findCase = made.Get<Button>("find_case");
        findWord = made.Get<Button>("find_word");
        findRegex = made.Get<Button>("find_regex");
        findExpand = made.Get<Button>("find_expand");
        findCount = made.Get<Label>("find_count");
        replaceRow = made.Get<StackPanel>("replace_row");
        findBar.Visible = false;

        findBox.OnChanged = FindTyped;
        findBox.OnSubmitted = _ => FindStep(forward: !context.Keyboard.Shift);
        replaceBox.OnSubmitted = _ => ReplaceOne();
        findCase.OnPressed = () => ToggleFindOption(findOptions with { MatchCase = !findOptions.MatchCase });
        findWord.OnPressed = () => ToggleFindOption(findOptions with { WholeWord = !findOptions.WholeWord });
        findRegex.OnPressed = () => ToggleFindOption(findOptions with { Regex = !findOptions.Regex });
        findExpand.OnPressed = () => ShowReplace(!replaceRow.Visible);
        made.Get<Button>("find_previous").OnPressed = () => FindStep(forward: false);
        made.Get<Button>("find_next").OnPressed = () => FindStep(forward: true);
        made.Get<Button>("find_close").OnPressed = CloseFindBar;
        made.Get<Button>("replace_one").OnPressed = ReplaceOne;
        made.Get<Button>("replace_all").OnPressed = ReplaceEvery;
        OrderFindTabs(made.Get<Button>("find_close"), made.Get<Button>("replace_one"));

        commands.Add("Edit/find", context.Strings.Said("code_findbar.find"), FindInFile, "Ctrl+F", "search", UIKeyUse.BoundWhileTyping);
        commands.Add("Edit/find", context.Strings.Said("code_findbar.find_next"), () => FindAgain(forward: true), "F3", "chevron-down", UIKeyUse.BoundWhileTyping);
        commands.Add("Edit/find", context.Strings.Said("code_findbar.find_previous"), () => FindAgain(forward: false), "Shift+F3", "chevron-up", UIKeyUse.BoundWhileTyping);
        commands.Add("Edit/find", context.Strings.Said("code_findbar.replace"), ReplaceInFile, "Ctrl+H", "replace", UIKeyUse.BoundWhileTyping).IsVisible = () => !LayoutTools;

        // While the bar is open, from the boxes and from the code
        commands.Bind(Key.C, () => { if (FindBarOpen) ToggleFindOption(findOptions with { MatchCase = !findOptions.MatchCase }); }, whileTyping: true, alt: true);
        commands.Bind(Key.W, () => { if (FindBarOpen) ToggleFindOption(findOptions with { WholeWord = !findOptions.WholeWord }); }, whileTyping: true, alt: true);
        commands.Bind(Key.R, () => { if (FindBarOpen) ToggleFindOption(findOptions with { Regex = !findOptions.Regex }); }, whileTyping: true, alt: true);
        commands.Bind(Key.Enter, () => { if (FindBarOpen) ReplaceEvery(); }, control: true, alt: true, whileTyping: true);
        commands.Bind(Key.Escape, EscapeFindBar, whileTyping: true);
    }

    /// <summary>
    /// Helper method for where Tab goes in the bar, VS Code's order. The find box, the replace box under it when that's
    /// open, then the toggles and the buttons along the find row, then the replace row's. It went by the layout, through
    /// the three toggles and the arrows before it ever got to the replace box.
    /// </summary>
    private void OrderFindTabs(Button close, Button replaceOne)
    {
        findBox.TabTo = by => by > 0 && replaceRow.Visible ? replaceBox : null;
        replaceBox.TabTo = by => by > 0 ? findCase : findBox;
        findCase.TabTo = by => by < 0 ? (replaceRow.Visible ? replaceBox : findBox) : null;
        close.TabTo = by => by > 0 && replaceRow.Visible ? replaceOne : null;
        replaceOne.TabTo = by => by < 0 ? close : null;
    }

    /// <summary>Ctrl+F, the bar opened over the code with what's selected (a word, a line of it) to look for, its text picked out.</summary>
    internal void FindInFile() => OpenFindBar(replacing: replaceRow.Visible && FindBarOpen);

    /// <summary>Ctrl+H, the bar with its replace row.</summary>
    internal void ReplaceInFile() => OpenFindBar(replacing: true);

    private void OpenFindBar(bool replacing)
    {
        if (document.View != HexView.Code && !document.IsText)
            window.SwitchView(HexView.Code);

        // What's selected is looked for, unless it's a match of what's looked for already (Ctrl+H with the bar up), and
        // as itself when the bar is on regexes
        UITextDocument text = code.Document;
        bool onAMatch = FindBarOpen && lastFind.Length > 0 && code.FoundIndex >= 0;
        if (text.HasSelection && text.SelectionStart.Line == text.SelectionEnd.Line && !onAMatch)
            lastFind = findOptions.Regex ? System.Text.RegularExpressions.Regex.Escape(text.SelectedText) : text.SelectedText;

        findStart = text.SelectionStart;
        findBar.Visible = true;
        PlaceFindBar();
        findEditor = code;
        ShowReplace(replacing);
        findBox.Text = lastFind;
        findBox.Focus();
        findBox.SelectAll();
        ApplyFind(grow: true);
    }

    private void ShowReplace(bool shown)
    {
        replaceRow.Visible = shown;
        findExpand.Icon = shown ? "chevron-down" : "chevron-right";
        if (shown && FindBarOpen && compositor.Focus != findBox)
            replaceBox.Focus();
    }

    internal void CloseFindBar()
    {
        findBar.Visible = false;
        code.FindQuery = string.Empty;
        if (codeScroll.Visible)
            code.Focus();
    }

    // Escape puts the bar away from its boxes, or from the code when nothing else there wants it (the list of what can be
    // completed has escape first)
    internal void EscapeFindBar()
    {
        if (!FindBarOpen)
            return;

        if (compositor.Focus == findBox || compositor.Focus == replaceBox || (compositor.Focus == code && !code.CompletionOpen))
            CloseFindBar();
    }

    private void FindTyped(string typed)
    {
        lastFind = typed;
        ApplyFind(grow: true);
    }

    private void ToggleFindOption(UIFindOptions options)
    {
        findOptions = options;
        ApplyFind(grow: true);
    }

    // What's looked for handed to the code, every match lit, and the first from where the bar was opened selected
    private void ApplyFind(bool grow)
    {
        code.FindQuery = lastFind;
        code.FindOptions = findOptions;
        findCase.Selected = findOptions.MatchCase;
        findWord.Selected = findOptions.WholeWord;
        findRegex.Selected = findOptions.Regex;

        if (grow && lastFind.Length > 0)
            code.FindFrom(findStart);
        UpdateFindCount();
    }

    /// <summary>F3, enter in the bar, the next match or the one before. The bar opened if it wasn't.</summary>
    internal void FindAgain(bool forward)
    {
        if (lastFind.Length == 0)
        {
            FindInFile();
            return;
        }

        code.FindQuery = lastFind;
        code.FindOptions = findOptions;
        FindStep(forward);
    }

    internal void FindStep(bool forward)
    {
        if (lastFind.Length == 0)
            return;

        if (!code.FindNext(forward))
            context.Notices.Say(code.FindError is { } bad
                ? context.Strings.Said("code_findbar.bad_regex", ("query", lastFind), ("error", bad.ToString()))
                : context.Strings.Said("code_findbar.not_found", ("query", lastFind), ("file", document.Name)), error: true);
        findStart = code.Document.SelectionStart;
        UpdateFindCount();
    }

    internal void ReplaceOne()
    {
        if (lastFind.Length == 0 || code.ReadOnly)
            return;

        code.ReplaceNext(replaceBox.Text);
        findStart = code.Document.SelectionStart;
        UpdateFindCount();
    }

    internal void ReplaceEvery()
    {
        if (lastFind.Length == 0)
            return;

        int count = code.ReplaceAll(replaceBox.Text);
        context.Notices.Say(count > 0
            ? context.Strings.Said("code_findbar.replaced", ("count", count.ToString()), ("query", lastFind))
            : context.Strings.Said("code_findbar.not_found", ("query", lastFind), ("file", document.Name)), error: count == 0);
        UpdateFindCount();
    }

    /// <summary>Every tick the bar is open, "3 of 17" kept up with the code, written again only when it changed.</summary>
    internal void UpdateFindBar()
    {
        if (!FindBarOpen)
            return;

        // A file with no code on screen (the designer, a table) has nothing to find in
        if (!codeScroll.Visible)
        {
            CloseFindBar();
            return;
        }

        // Another file came to the front, the bar looks in that one now and the last one stops being lit
        if (findEditor != code)
        {
            if (findEditor is not null)
                findEditor.FindQuery = string.Empty;
            findEditor = code;
            ApplyFind(grow: false);
        }

        PlaceFindBar();
        UpdateFindCount();
    }

    // How far the bar keeps from the minimap, or the scroll bar without one
    private const float FIND_BAR_GAP = 12.0f;

    /// <summary>
    /// Helper method to keep the bar left of the editor's minimap and scroll bar, VS Code keeps its find widget clear of
    /// the scroll bar. It was laid over the top of both, the matches ticked down the bar under it.
    /// </summary>
    private void PlaceFindBar()
    {
        float right = code.CodeBounds.Max.X - FIND_BAR_GAP;
        float most = codeScroll.Bounds.Width - findBar.Bounds.Width - FIND_BAR_GAP;
        float x = -Math.Clamp(codeScroll.Bounds.Max.X - right, FIND_BAR_GAP, MathF.Max(FIND_BAR_GAP, most));
        if (MathF.Abs(findBar.Position.X - x) > 0.5f)
            findBar.Position = findBar.Position with { X = x };
    }

    private void UpdateFindCount()
    {
        bool error = code.FindError is not null && findOptions.Regex;
        int count = lastFind.Length == 0 ? -1 : code.FoundCount;
        int index = count > 0 ? code.FoundIndex : -1;
        if (findCountShown == (index, count, error))
            return;

        findCountShown = (index, count, error);
        findCount.Text = error ? context.Strings.Said("find.bad_regex")
            : count < 0 ? string.Empty
            : count == 0 ? context.Strings.Said("find.none")
            : index < 0 ? context.Strings.Said("find.count", ("count", HexText.Count("result", count)))
            : context.Strings.Said("find.of", ("index", (index + 1).ToString()), ("count", count.ToString()));
        findCount.Color = error || count == 0 ? context.Panels.Error : context.Panels.Dim;
    }
}
