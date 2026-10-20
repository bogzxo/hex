using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What is on screen for the document that is open. A layout is its designer (the canvas, the palette, its components,
// its layers and its properties) or its code, flipped with F7 or the two buttons over the middle, and a file of code
// is only ever its code, with the explorer or its outline on the left and what is wrong with it on the right. Also the
// little x of every tab and asking before anything unsaved goes, a tab or the whole window.
internal sealed partial class HexScene
{
    /// <summary>Whether the tools of a layout are out, which is the designer of a layout.</summary>
    private bool LayoutTools => document.InDesigner;

    // The window was asked to close with something unsaved, and the question is to be asked on the next tick
    private volatile bool closeAsked;

    // Whatever is unsaved was dealt with (saved or thrown away), the window goes the next time it is asked to
    private volatile bool closeConfirmed;

    // Whether anything open has changes that aren't in its file, kept up every tick for the window's thread to read
    private volatile bool anyUnsaved;

    /// <summary>Puts on screen what the view of the document on screen wants, see the top of this file.</summary>
    private void ApplyView()
    {
        // The code may be a little behind the layout while it was out of sight, it is about to be looked at
        if (codeDirty)
        {
            ShowCode();
            codeDirty = false;
        }

        bool tools = LayoutTools;
        bool table = document.IsText && document.View == HexView.Table;

        // A view an extension made for the file goes where the code is, see HexScene.UIScriptedLayout
        ApplyExtensionViews();
        bool extension = document.View == HexView.Extension;

        canvas.Visible = tools;
        codeScroll.Visible = !tools && !table && !extension;

        // The layout isn't laid out and painted while its code is what's on screen, nobody would see it
        document.Module.Enabled = tools;
        dataTable.Visible = table;
        tabledFrom = null;

        // A layout is its designer or its code, a file of code its code or its table when it has one
        layout.Get<UIComponent>("views").Visible = !document.IsText || HasTable || viewButtons.Count > 0;
        layout.Get<Button>("design_view").Visible = !document.IsText;
        layout.Get<Button>("table_view").Visible = document.IsText;
        layout.Get<Button>("design_view").Selected = tools;
        layout.Get<Button>("code_view").Selected = !tools && !table && !extension;
        layout.Get<Button>("table_view").Selected = table;
        nameBox.Visible = !document.IsText;
        layout.Get<Label>("inspector_title").Text = Said(tools ? "properties.title" : "properties.file");

        var play = layout.Get<Button>("play_button");
        play.Label = Said(tools ? "toolbar.play" : "toolbar.run");
        play.Tooltip = Said(tools ? "toolbar.play_tip" : "toolbar.run_tip");

        if (tools || table || extension)
            code.Unfocus();
        else
            code.Focus();

        // The side bar is the view each kind of document was left on, put back the next time round
        sideShown = null;
        treeDirty = inspectorDirty = tabsDirty = problemsDirty = true;
    }

    /// <summary>Shows a layout as its designer or as its code. The code typed into goes into the layout on its way to the designer.</summary>
    private void SwitchView(HexView view)
    {
        // A file of code has no designer, a layout no table
        if (document.View == view || (document.IsText && view == HexView.Design) || (!document.IsText && view == HexView.Table))
            return;

        if (document.IsText)
        {
            document.View = view;
            ApplyView();
            Say(view == HexView.Table ? Said("table.shown", ("file", document.Name)) : $"the code of {document.Name}");
            return;
        }

        // What was typed has to build, or the designer would show the layout as it was before it
        if (view == HexView.Design && document.CodeEdited)
        {
            ApplyCode();
            if (document.CodeEdited)
                return;
        }

        document.View = view;
        ApplyView();
        Say(view == HexView.Design ? $"the designer of {document.Name}" : $"the code of {document.Name}, F7 goes back to the designer");
    }

    // F7, the designer and the code of a layout, the code and the table of a file of code that has one
    private void ToggleView()
    {
        if (document.IsText)
        {
            // Round the views a file of code has, its code, its table if it has one, the views of extensions for it
            var round = new List<(HexView View, string? Id)> { (HexView.Code, null) };
            if (HasTable) round.Add((HexView.Table, null));
            round.AddRange(ViewsFor(document).Select(kind => (HexView.Extension, (string?)kind.Id)));
            if (round.Count < 2)
                return;

            int at = round.FindIndex(view => view.View == document.View && (view.View != HexView.Extension || view.Id == document.ExtensionView));
            var (next, id) = round[(at + 1) % round.Count];
            if (next == HexView.Extension)
                SwitchToExtensionView(id!);
            else
                SwitchView(next);
            return;
        }

        SwitchView(document.View == HexView.Design ? HexView.Code : HexView.Design);
    }

    private void WireViews()
    {
        layout.Get<Button>("design_view").OnPressed = () => SwitchView(HexView.Design);
        layout.Get<Button>("code_view").OnPressed = () => SwitchView(HexView.Code);

        // The x of the window asks first when something isn't saved, see AskBeforeClosing
        Hex.Shell.HexModes.OnClose(Hex.Shell.HexMode.Dawn, () =>
        {
            if (closeConfirmed || !anyUnsaved)
                return true;

            closeAsked = true;
            return false;
        });
    }

    /// <summary>Called every tick, keeps up what the window's thread reads and asks the question it asked for.</summary>
    private void UpdateClosing()
    {
        bool unsaved = false;
        foreach (var open in documents)
            unsaved |= open.Modified || open.CodeEdited;
        anyUnsaved = unsaved;

        if (closeAsked)
        {
            closeAsked = false;
            AskBeforeClosing();
        }
    }

    /// <summary>
    /// Makes a file that says it builds a layout (calling the compositor) say so with #define layout, at the top under its
    /// comments, saves it and opens it again with its designer.
    /// </summary>
    private void MakeLayout()
    {
        if (!document.IsText || document.Path is not { } path)
        {
            Say("only a file of code that has a file can be made a layout", error: true);
            return;
        }

        UIDialog.Show(chrome, "Make it a layout?", LayoutWarning(Path.GetFileName(path)),
            new DialogChoice("Make it a layout", () => MakeLayoutNow(path)),
            new DialogChoice("Cancel"));
    }

    /// <summary>
    /// What a file loses by being a layout, said before it is made one. The designer writes the whole of a layout's file
    /// anew out of the layout it builds whenever it is saved, nothing else in it survives that.
    /// </summary>
    private static string LayoutWarning(string name) =>
        $"{name} will open in the designer from now on, and the designer writes the whole file anew out of the layout it builds " +
        "every time it is saved from there. Anything in it that isn't part of the layout (comments between the components, " +
        "lets the layout doesn't use, functions, the way it is spaced out) will be gone the first time that happens.";

    private void MakeLayoutNow(string path)
    {
        // Told already, saving it isn't asked about again
        layoutWarned.Add(path);

        int line = 0;
        while (line < code.Document.LineCount && code.Document.LineAt(line).TrimStart().StartsWith("//")) line++;

        code.Document.MoveTo(new UITextPosition(line, 0));
        code.Document.Insert("#define layout\n\n");
        Save();

        CloseNow();
        Open(path);
        Say($"{Path.GetFileName(path)} is a layout now, its designer is open");
    }

    /* Closing, a tab or the window, asking first */

    /// <summary>Closes a document, whichever it is, asking first if it has changes that aren't saved.</summary>
    private void CloseDocument(HexDocument closing)
    {
        if (!documents.Contains(closing))
            return;

        Show(closing);
        Close();
    }

    /// <summary>
    /// The window was asked to close with something unsaved. Every file with changes is saved, or all of them are thrown
    /// away, or nothing happens. A layout that has never been saved anywhere is the one thing saving can't do by itself.
    /// </summary>
    // The question about what is unsaved while it is up, so asking again doesn't put up another on top of it
    private UIDialog? closeQuestion;

    private void AskBeforeClosing()
    {
        if (closeQuestion is { IsOpen: true })
            return;

        var unsaved = documents.Where(open => open.Modified || open.CodeEdited).ToList();
        if (unsaved.Count == 0)
        {
            QuitNow();
            return;
        }

        string names = string.Join(", ", unsaved.Take(6).Select(open => open.Name)) + (unsaved.Count > 6 ? $" and {unsaved.Count - 6} more" : string.Empty);

        closeQuestion = UIDialog.Show(chrome, "Unsaved changes",
            $"{unsaved.Count} {(unsaved.Count == 1 ? "file has" : "files have")} changes that aren't in {(unsaved.Count == 1 ? "its file" : "their files")}: {names}.",
            new DialogChoice("Save all", () => SaveAllAndQuit(unsaved)),
            new DialogChoice("Don't save", QuitNow),
            new DialogChoice("Cancel"));
    }

    private void SaveAllAndQuit(List<HexDocument> unsaved)
    {
        foreach (var open in unsaved)
        {
            // Never saved anywhere, there is no file to put it in without asking where
            if (open.Path is null)
            {
                Show(open);
                Say($"{open.Name} has never been saved, save it somewhere (Ctrl+S) and close again", error: true);
                return;
            }

            Show(open);
            Save();
        }

        QuitNow();
    }

    private void QuitNow()
    {
        // What wasn't saved was saved or thrown away when asked, there's nothing to bring back next time
        Backups.DropAll();
        debugger.EndDebugging();
        closeConfirmed = true;
        Engine.WindowManager.Close();
    }

    // The view --view asked for, until the file can show it
    private string? viewAsked;
    private bool viewAskedRead;

    private void ApplyAskedView()
    {
        if (!viewAskedRead)
        {
            viewAskedRead = true;
            viewAsked = options.View;
        }

        if (viewAsked is null)
            return;

        HexView? wanted = viewAsked.ToLowerInvariant() switch
        {
            "design" => HexView.Design,
            "code" => HexView.Code,
            "table" => HexView.Table,
            _ => null
        };

        if (wanted is not { } view || (view == HexView.Table && !HasTable))
        {
            if (wanted is null)
                viewAsked = null;
            return;
        }

        viewAsked = null;
        SwitchView(view);
    }
}
