using System.Numerics;
using System.Reflection;
using System.Text;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What the menus do that isn't anywhere else. Saving every file, putting the properties away, going to something in the
// file or the next problem, a component's code, and the dialogs of the Help menu.
internal sealed partial class HexScene
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

        Say(skipped > 0 ? $"saved {saved} files, {skipped} never saved anywhere are left for Ctrl+S" : saved > 0 ? $"saved {saved} files" : "nothing to save");
    }

    /// <summary>Throws away what isn't saved of the file on screen, for what is in its file.</summary>
    private void RevertFile()
    {
        if (document.Path is not { } path)
            return;

        UIDialog.Show(chrome, "Revert the file?", $"What isn't saved of {Path.GetFileName(path)} goes, and it is as it is in its file.",
            new DialogChoice("Revert", () =>
            {
                CloseNow();
                Open(path);
                Say($"{Path.GetFileName(path)} is as it is in its file");
            }),
            new DialogChoice("Cancel"));
    }

    private void ToggleProperties()
    {
        right.ToggleShown();
        settings.PropertiesHidden = !right.Shown;
        SaveSettings();
    }

    private void SelectCaretLine()
    {
        int line = code.Document.Caret.Line;
        code.Document.Select(new UITextPosition(line, 0), line + 1 < code.Document.LineCount ? new UITextPosition(line + 1, 0) : new UITextPosition(line, code.Document.LineAt(line).Length));
    }

    /// <summary>Something the file declares, or a key of it, picked out of its outline by a few letters.</summary>
    private void GoToSymbol()
    {
        var choices = new List<(string Text, Action Picked)>();
        foreach (OutlineEntry entry in OutlineHere())
            choices.Add(($"{new string(' ', entry.Depth * 4)}{entry.Name}   line {entry.Line + 1}", () => code.GoTo(entry.Line, entry.Column)));

        ChooseFrom("Go to something in the file", $"What {document.Name} declares and the keys of its objects", choices);
    }

    /// <summary>The next problem there is (or the one before), going round, opened with the caret on it.</summary>
    private void StepProblem(int direction)
    {
        GatherProblems();
        if (problemList.Count == 0)
        {
            Say("nothing wrong");
            return;
        }

        problemStep = ((problemStep + direction) % problemList.Count + problemList.Count) % problemList.Count;
        HexProblem problem = problemList[problemStep];
        OpenAt(problem.Path, problem.Line, problem.Column);
        Say($"{problemStep + 1} of {problemList.Count}, {problem.Text}", error: !problem.Warning);
    }

    /// <summary>The code of the layout, with the caret on the let of a name of it.</summary>
    private void ShowDeclaration(string name)
    {
        SwitchView(HexView.Code);

        if (FindDeclaration(code.Text, name) is { } at)
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
    private WindowPanel ShowText(string title, string markdown, float height = 520.0f)
    {
        UILayout made = layout.Instantiate("text_window.hor", chrome.Root);
        var window = made.Get<WindowPanel>("window");
        window.Title = title;
        window.Size = window.Size with { Y = height + 60.0f };
        window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
        window.OnClosed = () => window.Parent?.Remove(window);
        made.Get<Label>("text").Text = markdown;
        return window;
    }

    /// <summary>Help, What changed. The changelog, out of Hex's texts like the rest of the help.</summary>
    private void ShowChangelog()
    {
        ReadStrings();
        ShowText(Said("changelog.title"), Said("changelog.page", ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?")));
    }

    private void ShowLanguageHelp()
    {
        ReadStrings();
        ShowText(Said("language.title"), Said("language.page"));
    }

    private void ShowAbout()
    {
        ReadStrings();
        ShowText(Said("about.title"), Said("about.page",
            ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"),
            ("engine", typeof(UICompositor).Assembly.GetName().Version?.ToString(3) ?? "?"),
            ("dotnet", Environment.Version.ToString())), 300.0f);
    }

    /// <summary>The Help menu.</summary>
    private void AddHelpCommands()
    {
        commands.Add("Help/help", "Keyboard Shortcuts...", OpenKeysWindow, icon: "keyboard");
        commands.Add("Help/help", "HIDL Reference...", ShowLanguageHelp, icon: "book");
        commands.Add("Help/help", "Release Notes...", ShowChangelog, icon: "history");
        commands.Add("Help/help", "Open Hex's Own Layout", () => Open(EDITOR_LAYOUT), icon: "layout");
        commands.Add("Help/about", "About Hex...", ShowAbout, icon: "info");
    }
}
