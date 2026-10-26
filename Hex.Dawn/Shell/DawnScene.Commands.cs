using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What the menus do that isn't anywhere else. Saving every file, putting the properties away, going to something in the
// file or the next problem, a component's code, and the dialogs of the Help menu.
internal sealed partial class DawnScene
{
    // Which problem the last step through them went to
    private int problemStep = -1;

    /// <summary>Ctrl+Shift+S, every file with changes saved where it came from. A layout never saved anywhere is left, saving it asks where.</summary>
    private void SaveAll()
    {
        HexDocument was = document;
        int saved = 0, skipped = 0;

        foreach (HexDocument open in documents.ToList())
        {
            if (!open.Modified && !open.CodeEdited)
                continue;

            if (open.Path is null && !options.SelfTest)
            {
                skipped++;
                continue;
            }

            Show(open);
            Save();
            saved++;
        }

        if (documents.Contains(was))
            Show(was);

        Say(skipped > 0 ? Said("commands.saved_left", ("saved", HexText.Count("file", saved)), ("skipped", HexText.Count("file", skipped))) : saved > 0 ? Said("commands.saved", ("saved", HexText.Count("file", saved))) : Said("commands.nothing_to_save"));
    }

    /// <summary>Throws away what isn't saved of the file on screen, for what is in its file.</summary>
    private void RevertFile()
    {
        if (document.Path is not { } path)
            return;

        UIDialog.Show(chrome, Said("commands.revert_title"), Said("commands.revert_text", ("file", Path.GetFileName(path))),
            new DialogChoice(Said("commands.revert"), () =>
            {
                CloseNow();
                Open(path);
                Say(Said("commands.reverted", ("file", Path.GetFileName(path))));
            }),
            new DialogChoice(Said("keys.leave_it")));
    }

    private void ToggleProperties()
    {
        right.ToggleShown();
        settings.PropertiesHidden = !right.Shown;
        if (!right.Shown && properties is not null)
            properties.Pinned = false;
        SaveSettings();
    }

    /// <summary>Something the file declares, or a key of it, picked out of its outline by a few letters.</summary>
    private void GoToSymbol()
    {
        var choices = new List<(string Text, Action Picked)>();
        foreach (OutlineEntry entry in OutlineHere())
            choices.Add((new string(' ', entry.Depth * 4) + Said("commands.symbol_entry", ("name", entry.Name), ("line", (entry.Line + 1).ToString())), () => code.GoTo(entry.Line, entry.Column)));

        ChooseFrom(Said("commands.symbol_title"), Said("commands.symbol_text", ("file", document.Name)), choices);
    }

    // How much of a problem the status bar says as F8 goes to it
    private const int STEP_SAID = 90;

    /// <summary>The next problem there is (or the one before), going round, opened with the caret on it.</summary>
    private void StepProblem(int direction)
    {
        GatherProblems();
        if (problemList.Count == 0)
        {
            Say(Said("commands.nothing_wrong"));
            return;
        }

        problemStep = ((problemStep + direction) % problemList.Count + problemList.Count) % problemList.Count;
        HexProblem problem = problemList[problemStep];
        OpenAt(problem.Path, problem.Line, problem.Column);
        // A long message cut short, the whole of it ran over the counts and the caret's place along the status bar
        string text = problem.Text.Length > STEP_SAID ? problem.Text[..(STEP_SAID - 3)] + "..." : problem.Text;
        Say(Said("commands.problem_step", ("at", (problemStep + 1).ToString()), ("count", problemList.Count.ToString()), ("problem", text)), error: !problem.Warning);
    }

    /// <summary>The code of the layout, with the caret on the let of a name of it.</summary>
    private void ShowDeclaration(string name)
    {
        SwitchView(HexView.Code);

        if (HexCodeEditing.FindDeclaration(code.Text, name) is { } at)
            code.GoTo(at.Line, at.Column);
    }

    /* Small conveniences */

    /// <summary>Takes down the little window on top (the settings, a page of help), false if there is none.</summary>
    private bool CloseTopWindow()
    {
        if (chrome.Root.Children.OfType<WindowPanel>().LastOrDefault(window => window.Visible) is not { } top)
            return false;

        if (top == SettingsWindow)
            CloseSettings();
        else
            top.Parent?.Remove(top);

        return true;
    }

    /// <summary>F2 in the designer, the name of what is selected to type over, in its row of the properties.</summary>
    private void RenameSelected()
    {
        if (document.Selected is null || !designer.editors.TryGetValue("name", out UIComponent? editor) || editor is not TextBox box)
            return;

        inspectorScroll.ScrollIntoView(box);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Ctrl+Tab, the next file that is open (the one before with shift), round and round.</summary>
    private void CycleDocuments(int by)
    {
        if (documents.Count < 2)
            return;

        int at = documents.IndexOf(document);
        Show(documents[((at + by) % documents.Count + documents.Count) % documents.Count]);
    }

    /* The dialogs of the Help menu */

    /// <summary>
    /// A little window with a page of markdown in it that scrolls, out of text_window.hor, for the Help menu. Dragged about
    /// by its title bar, bigger by its corner, gone with its x.
    /// </summary>
    private WindowPanel ShowText(string title, string markdown, float height = 520.0f) => ShowText(title, markdown, out _, height);

    /// <summary>The same with the label the page is in, for a page that is filled in later (the changelog, once GitHub answers).</summary>
    private WindowPanel ShowText(string title, string markdown, out Label text, float height = 520.0f)
    {
        UILayout made = layout.Instantiate("text_window.hor", chrome.Root);
        var window = made.Get<WindowPanel>("window");
        window.Title = title;
        window.Size = window.Size with { Y = height + 60.0f };
        DressWindow(window);
        window.OnClosed = () => window.Parent?.Remove(window);
        text = made.Get<Label>("text");
        text.Text = markdown;

        // The keyboard in it, so Escape closes it the first time and the arrows don't go on to the code behind
        window.TakeKeyboard();
        return window;
    }

    private void ShowLanguageHelp()
    {
        ReadStrings();
        ShowText(Said("language.title"), Said("language.page"), out Label page);

        // Every block of code on it is HIDL, coloured the way the editor colours it, a fence that says nothing too
        page.CodeLanguage = "hidl";
    }

    /// <summary>The Help menu.</summary>
    private void AddHelpCommands()
    {
        // Keyboard Shortcuts went to File, beside Settings, the way VS Code has them
        AddKeysCommands();
        commands.Add("Help/help", Said("commands.reference"), ShowLanguageHelp, icon: "book");
        commands.Add("Help/help", Said("commands.release_notes"), ShowChangelog, icon: "history");
        commands.Add("Help/help", Said("commands.own_layout"), () => Open(EDITOR_LAYOUT), icon: "layout");
        commands.Add("Help/about", Said("commands.about"), ShowAbout, icon: "info");
    }
}
