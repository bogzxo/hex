using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What is on screen for the document that is open. A layout is its designer (the canvas, the palette, its components,
// its layers and its properties) or its code, flipped with F7 or the two buttons over the middle, and a file of code
// is only ever its code, with the explorer or its outline on the left and what is wrong with it on the right. Also the
// little x of every tab and asking before anything unsaved goes, a tab or the whole window.
internal sealed partial class DawnScene
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

        // A view an extension made for the file goes where the code is, see DawnScene.UIScriptedLayout
        ApplyExtensionViews();
        bool extension = document.View == HexView.Extension;
        bool picture = document.Picture is not null;
        bool notebook = document.View == HexView.Notebook && IsNotebook(document);
        ApplyNotebookView(notebook);

        canvas.Visible = tools;
        codeScroll.Visible = !tools && !table && !extension && !picture && !notebook;
        imageView.Visible = picture;

        // The layout isn't laid out and painted while its code is what's on screen, nobody would see it
        document.Module.Enabled = tools;
        running.Table.Visible = table;
        running.RefreshTable();

        // A layout is its designer or its code, a file of code its code or its table when it has one
        ShowViewButtons();
        layout.Get<Button>("design_view").Visible = !document.IsText;
        layout.Get<Button>("table_view").Visible = document.IsText && !IsNotebook(document);
        layout.Get<Button>("design_view").Selected = tools;
        layout.Get<Button>("code_view").Selected = !tools && !table && !extension && !notebook;
        layout.Get<Button>("table_view").Selected = table;
        nameBox.Visible = !document.IsText;
        layout.Get<Label>("inspector_title").Text = Said(tools ? "properties.title" : "properties.file");

        // Play or Run, or what was picked to run beside it (DawnScene.RunTarget)
        ShowRunTarget();

        if (tools || table || extension || picture || notebook)
            code.Unfocus();
        else
            code.Focus();

        // The side bar is the view each kind of document was left on, put back the next time round
        sideShown = null;
        treeDirty = inspectorDirty = tabsDirty = problemsDirty = true;
    }

    // The files F5 has something for without a task, the kinds Set Up F5 has a preset for (HexTasks.Presets)
    private static readonly string[] runnableExtensions = [".d", ".di", ".cs", ".rs", ".go", ".c", ".cpp", ".cc", ".cxx", ".py", ".js", ".mjs"];

    /// <summary>The buttons over the editor for the ways the file can be shown, its designer, its code, its table and an extension's.</summary>
    public void ShowViewButtons() => running.ShowTableButtons(viewButtons.Count + (IsNotebook(document) ? 1 : 0));

    /// <summary>
    /// Whether Run means anything for a document, what RunOrPlay would do with it. A layout plays, HIDL runs, a file with
    /// a run task or of a language Hex can set up a run for runs. A log, a .rej, markdown or plain text has nothing to run
    /// and a Run button over them only ever opened Set Up F5 for nothing.
    /// </summary>
    internal bool Runnable(HexDocument shown)
    {
        if (!shown.IsText || debugger.DebugStopped)
            return true;
        if (shown.Picture is not null || shown.Path is not { } path)
            return false;

        return IsHidl(path) || runnableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || tasks.RunTaskFor(path) is not null;
    }

    /// <summary>Shows a layout as its designer or as its code. The code typed into goes into the layout on its way to the designer.</summary>
    private void SwitchView(HexView view)
    {
        // A file of code has no designer, a layout no table
        if (document.View == view || (document.IsText && view == HexView.Design) || (!document.IsText && view == HexView.Table) || (view == HexView.Notebook && !IsNotebook(document)))
            return;

        if (document.IsText)
        {
            document.View = view;
            ApplyView();
            if (view != HexView.Notebook)
                Say(view == HexView.Table ? Said("table.shown", ("file", document.Name)) : Said("views.code_of", ("name", document.Name)));
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
        Say(view == HexView.Design ? Said("views.designer_of", ("name", document.Name)) : Said("views.code_of_layout", ("name", document.Name)));
    }

    // F7, the designer and the code of a layout, the code and the table of a file of code that has one
    private void ToggleView()
    {
        if (document.IsText)
        {
            // Round the views a file of code has, its code, its table if it has one, the views of extensions for it
            var round = new List<(HexView View, string? Id)> { (HexView.Code, null) };
            if (IsNotebook(document)) round.Insert(0, (HexView.Notebook, null));
            if (running.HasTable) round.Add((HexView.Table, null));
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
            Say(Said("views.make_layout_needs_file"), error: true);
            return;
        }

        UIDialog.Show(chrome, Said("views.make_layout_title"), LayoutWarning(Path.GetFileName(path)),
            new DialogChoice(Said("views.make_layout"), () => MakeLayoutNow(path)),
            new DialogChoice(Said("keys.leave_it")));
    }

    /// <summary>
    /// What a file loses by being a layout, said before it is made one. The designer writes the whole of a layout's file
    /// anew out of the layout it builds whenever it is saved, nothing else in it survives that.
    /// </summary>
    private static string LayoutWarning(string name) => Said("views.layout_warning", ("name", name));

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
        Say(Said("views.layout_now", ("name", Path.GetFileName(path))));
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

        string names = string.Join(", ", unsaved.Take(6).Select(open => open.Name));
        if (unsaved.Count > 6)
            names = Said("views.names_more", ("names", names), ("count", (unsaved.Count - 6).ToString()));

        closeQuestion = UIDialog.Show(chrome, Said("documents.unsaved_title"),
            unsaved.Count == 1 ? Said("views.unsaved_one", ("names", names)) : Said("views.unsaved_many", ("count", unsaved.Count.ToString()), ("names", names)),
            new DialogChoice(Said("views.save_all"), () => SaveAllAndQuit(unsaved)),
            new DialogChoice(Said("documents.dont_save"), QuitNow, Risky: true),
            new DialogChoice(Said("keys.leave_it"), () => restartInto = null));
    }

    private void SaveAllAndQuit(List<HexDocument> unsaved)
    {
        foreach (var open in unsaved)
        {
            // Never saved anywhere, there is no file to put it in without asking where
            if (open.Path is null)
            {
                Show(open);
                Say(Said("views.never_saved", ("name", open.Name)), error: true);
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
        debugger.Quit();
        closeConfirmed = true;
        StartRestart();
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

        if (wanted is not { } view || (view == HexView.Table && !running.HasTable))
        {
            if (wanted is null)
                viewAsked = null;
            return;
        }

        viewAsked = null;
        SwitchView(view);
    }
}
