using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Making, opening, saving and closing layouts. Windows asks where, the editor testing itself (which can't click in a dialog of Windows) uses the list on the left to open and its workspace to save.
internal sealed partial class DawnScene
{
    // Whether the stage is showing its layouts in the font of the editor, which it does for the editor's own
    private bool stagePlain;

    // A dialog of Windows that is open, and what to do with the file that is picked in it
    private Task<string?>? dialog;
    private Action<string>? dialogPicked;

    /* Documents */

    private HexDocument NewDocument()
    {
        string name = "layout";
        for (int number = 2; documents.Exists(other => other.Name == name); number++)
            name = $"layout{number}";

        var created = new HexDocument(name, stage.CreateModule()) { FallbackDirectory = browseDirectory };
        PrepareEditor(created);

        // Laid out as if the canvas were the screen it is made for, and kept inside of it
        created.Module.Viewport = HexDesigner.DesignScreen;
        created.Module.Clip = HexDesigner.DesignScreen;

        // An empty layout that nobody has touched has nothing to save
        created.SavedCode = created.GenerateCode();
        SetCode(created, created.SavedCode, fresh: true);

        documents.Add(created);
        return created;
    }

    private void Show(HexDocument shown)
    {
        // Whatever was being done to the layout that is put away is a step back in it, not in the next one
        if (designer.recordPending && document is not null)
            document.Record();
        designer.recordPending = false;

        HexDocument? was = document;
        document = shown;
        if (was is not null)
            SplitFollows(was, shown);

        // The editor's own layouts are made for the skin of the editor, everything else for the one games have
        bool plain = shown.Path is { } path && SameDirectory(path, EDITOR_LAYOUT);
        if (plain != stagePlain)
        {
            stagePlain = plain;

            if (plain)
                stage.SetSkin(TOOL_SKIN_DIRECTORY, TOOL_SKIN);
            else
                stage.FollowSkins();
        }

        // Localisation is one language for the whole program, so the one of the file on screen is loaded every time it
        // comes up. Only on opening, the last file opened won, and with a game's layout and one of Hex's own open
        // (which the restore at start does) the game's layout was looked up in Hex's texts and every key came out bare
        if (shown.Path is { } shownPath)
            UseLanguagesNear(shownPath, announce: false);

        // Only the layout that is being worked on is on screen
        foreach (var other in documents)
            other.Module.Enabled = other == shown;

        nameBox.Text = shown.Name;
        designer.MarkSelection();
        // The explorer of a workspace stays up the way it does in VS Code, the folder of files opened from goes
        browsing = browsing && !explorer.workspace.IsEmpty;
        running.Forget();

        // Its editor is the one in the code panel (or one of the two side by side), the find bar stays over whichever it is
        PlaceEditors();

        ApplyView();
        FoldValuesFor(shown);
        explorer.KeepWorkspace();
        WatchOpenFiles();
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
    }

    /// <summary>
    /// Opens a layout file in a tab of its own, and checks that writing it back out and reading that gives the
    /// same layout again: if it doesn't, saving would change it.
    /// </summary>
    /// <returns>Whether it opened and writes back the same.</returns>
    public bool Open(string path)
    {
        if (!File.Exists(path))
        {
            Say($"there is no '{path}'", error: true);
            return false;
        }

        // A workspace file is the workspace it describes, the way VS Code opens one
        if (path.EndsWith(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase))
        {
            explorer.OpenWorkspaceFile(path);
            return !explorer.workspace.IsEmpty;
        }

        // Somebody who opens the same file twice wants to see it, not have it twice
        string full = Path.GetFullPath(path);
        if (documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path) == full) is { } already)
        {
            Show(already);
            Say($"{already.Name} is open already");
            return true;
        }

        // A file of code that isn't a layout (data, a language, a skin), or any other text, is edited as the text it is
        string source = File.ReadAllText(path);
        if (source.Contains('\0'))
        {
            Say($"{Path.GetFileName(path)} isn't text", error: true);
            return false;
        }

        // A built-in extension that has the file for its own opens it (a layout in the designer), anything else is its text
        if (FileKindFor(path, source) is { } kind)
            return kind.Open(path, source);

        return OpenText(path, source);
    }

    /// <summary>Opens a layout in the designer, the designer's way of opening its files (HexDesignerKind).</summary>
    internal bool OpenLayout(string path, string source)
    {
        // Reusing the empty layout the editor starts with saves a tab
        HexDocument target = document.Path is null && !document.Module.Root.Children.Any() ? document : NewDocument();
        target.Name = Path.GetFileNameWithoutExtension(path);
        target.Path = path;

        // A game's layouts name their texts by key, "@main_menu.quit", which reads as the text only with the game's
        // languages loaded. They are next to the layouts, a localisation folder somewhere up the path
        UseLanguagesNear(path);

        var (success, message) = target.Load(source, path);
        Show(target);

        if (!success)
        {
            target.Path = null;
            Console.WriteLine($"[Hex] '{path}' didn't open: {message}");
            Say(message, error: true);
            return false;
        }

        Remember(path);
        extensions.Raise("on_open", new StringValue(Path.GetFullPath(path)));
        target.DiskText = source;
        target.SavedCode = target.GenerateCode();
        target.CodeEdited = false;
        SetCode(target, target.SavedCode, fresh: true);

        bool faithful = CheckRoundTrip(target);
        int count = target.Walk().Count();

        Console.WriteLine($"[Hex] Opened '{path}': {count} components, {target.PreviewCount} stand-in items, round trip {(faithful ? "ok" : "DIFFERS")}.");

        if (!faithful)
            Say($"opened {target.Name}, but it doesn't write back the same", error: true);
        else if (message.Length > 0)
            Say(message, error: true);
        else
            Say($"opened {target.Name}: {count} components, writes back the same");

        return faithful;
    }

    /// <summary>
    /// Helper to test that the code written for a layout builds that same layout. It is loaded into a module
    /// nobody sees and written out again, which has to give the same code.
    /// </summary>
    private bool CheckRoundTrip(HexDocument checkedDocument)
    {
        string written = checkedDocument.GenerateCode();

        var scratch = new HexDocument(checkedDocument.Name, stage.CreateModule());
        scratch.Module.Enabled = false;

        try
        {
            // From where the file is, its templates and the files it includes are looked for next to it
            return scratch.Load(written, checkedDocument.Path).Success && scratch.GenerateCode() == written;
        }
        finally
        {
            stage.RemoveModule(scratch.Module);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), PlatformPaths.Comparison);

    /* Opening and saving. Hex's own dialog asks where (UIFileBrowser), the editor testing itself uses the list on
       the left to open and its workspace to save. */

    /// <summary>
    /// Where a dialog of Hex's own goes, over the rest of the editor and coloured as its other windows are, with the
    /// workspace's folders among its places.
    /// </summary>
    private UIDialogHost DialogHost => new(window =>
    {
        window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
        chrome.Root.Add(window);
    }, WorkspaceRoots());

    private void OpenPressed()
    {
        if (options.SelfTest)
        {
            // Testing itself there is no dialog of Windows to ask in, the files of the folder are shown instead, and again goes back
            if (browsing)
            {
                Side = HomeSide;
                return;
            }

            ShowSide(HexSide.Explorer);
            Say($"files of {browseDirectory}");
            return;
        }

        Ask(HexDialogs.Open(browseDirectory, DialogHost), path =>
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;
            Open(path);
        });
    }

    private void Save()
    {
        // A file of code that has had #define layout typed into it is a layout the next time it is opened, which it is
        // told before it is saved that way
        if (document.IsText && document.Path is { } typed && !layoutWarned.Contains(typed)
            && Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(document.Editor.Text)
            && !Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(document.SavedCode))
        {
            HexDocument warned = document;
            UIDialog.Show(chrome, "Save it as a layout?", LayoutWarning(Path.GetFileName(typed)),
                new DialogChoice("Save it", () =>
                {
                    layoutWarned.Add(typed);
                    if (documents.Contains(warned))
                    {
                        Show(warned);
                        Save();
                    }
                }),
                new DialogChoice("Cancel"));
            return;
        }

        // Back to where it came from, unless it has been given another name since. That is saving a copy
        if (document.Path is { } known && Path.GetFileNameWithoutExtension(known) == document.Name)
            SaveTo(known);
        else if (document.Path is null && !options.SelfTest)
            SaveAs();
        else
            SaveTo(Path.Combine(document.Path is { } copyOf ? Path.GetDirectoryName(Path.GetFullPath(copyOf))! : browseDirectory, document.Name + ".hor"));
    }

    private void SaveAs()
    {
        if (options.SelfTest)
        {
            SaveTo(Path.Combine(browseDirectory, document.Name + ".hor"));
            return;
        }

        HexDocument saved = document;
        string directory = saved.Path is { } known ? Path.GetDirectoryName(Path.GetFullPath(known))! : browseDirectory;

        Ask(HexDialogs.Save(directory, saved.Name + ".hor", DialogHost), path =>
        {
            // The dialog was open for a while, the layout it was about may not be the one on screen any more
            if (!documents.Contains(saved))
                return;

            Show(saved);

            // A layout goes by the name of its file
            saved.Name = Path.GetFileNameWithoutExtension(path);
            nameBox.Text = saved.Name;
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;

            SaveTo(path);
        });
    }

    /// <summary>Helper to show a dialog of Windows and do something with the file that is picked in it, one at a time.</summary>
    private void Ask(Task<string?> asked, Action<string> picked)
    {
        if (dialog is not null)
        {
            Say("there is a dialog open already", error: true);
            return;
        }

        dialog = asked;
        dialogPicked = picked;
    }

    private void UpdateDialog()
    {
        if (dialog is not { IsCompleted: true } answered)
            return;

        Action<string>? picked = dialogPicked;
        dialog = null;
        dialogPicked = null;

        if (answered.IsFaulted)
            Say($"the dialog didn't open: {answered.Exception?.InnerException?.Message}", error: true);
        else if (answered.Result is { } path)
            picked?.Invoke(path);
    }

    private void SaveTo(string path)
    {
        // Saving the editor over itself with something broken would be the end of it, that is only ever saved as a copy
        if (SameDirectory(path, EDITOR_LAYOUT))
            path = Path.Combine(Path.GetFullPath(options.Workspace), document.Name + "_edited.hor");

        // Something else wrote the file since Hex read it, that isn't written over without asking
        if (!SafeToSave(path))
            return;

        try
        {
            // The code typed into a layout by hand goes into it first, what is saved is the layout
            if (document.CodeEdited)
                ApplyCode();

            // A file of code tidied first, one step to undo, the auto save leaves it be (see DawnScene.Conveniences)
            if (document.IsText && !savingQuietly && Tidied(document.Editor.Text) is { } tidy)
                document.Editor.Document.Replace(tidy);

            string written = document.GenerateCode();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string onDisk = document.CrLf ? written.Replace("\n", "\r\n") : written;
            File.WriteAllText(path, onDisk);
            UILayout.Forget(path);

            document.Path = path;
            document.DiskText = onDisk;
            document.SavedCode = written;
            document.SavedCrLf = document.CrLf;
            codeEditing.ForgetVocabulary();
            document.Modified = false;

            // Its templates are looked for next to it, which may be somewhere else now
            document.RefreshPreviews();
            tabsDirty = treeDirty = true;
            Remember(path);
            WatchOpenFiles();
            extensions.Raise("on_save", new StringValue(Path.GetFullPath(path)));

            Console.WriteLine($"[Hex] Saved '{path}'.");
            if (!savingQuietly)
                Say($"saved {path}");
            SavedEffects();
        }
        catch (Exception e)
        {
            Notify($"couldn't save {path}: {e.Message}", error: true, ("Save as...", SaveAs));
        }
    }

    private void Close()
    {
        // The code on screen is the newest word on whether there is anything to lose
        if (document.Modified || document.CodeEdited || document.GenerateCode() != document.SavedCode)
        {
            HexDocument closing = document;
            UIDialog.Show(chrome, "Unsaved changes", $"{closing.Name} has changes that aren't in its file.",
                new DialogChoice("Save", () =>
                {
                    Save();

                    // Saved on the spot it can go. Waiting on a dialog of Windows it stays until that is answered
                    if (document == closing && !document.Modified && document.GenerateCode() == document.SavedCode) CloseNow();
                }),
                new DialogChoice("Don't save", () => { if (document == closing) CloseNow(); }),
                new DialogChoice("Cancel"));
            return;
        }

        CloseNow();
    }

    private void CloseNow()
    {
        RememberClosed(document);
        stage.Highlighted = null;
        stage.Highlights = [];

        stage.RemoveModule(document.Module);
        documents.Remove(document);
        documentClosed?.Invoke(document);
        ForgetExtensionViews(document);
        DropBackup(document);
        designer.recordPending = false;

        Show(documents.Count > 0 ? documents[^1] : NewDocument());
    }

    // Where the languages of the files of a folder are, by the folder, null for nowhere
    private readonly Dictionary<string, string?> languagesNear = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Helper method to load the languages of whatever game a layout belongs to, the default one if there is one and
    /// the first there is otherwise, so the texts it names by key read the way they do in the game. Nothing changes
    /// for a layout that isn't near any.
    /// </summary>
    /// <param name="announce">Whether to say so in the status bar, on opening a file and not on every switch of tabs.</param>
    private void UseLanguagesNear(string path, bool announce = true)
    {
        if (!Path.GetExtension(path).Equals(".hor", StringComparison.OrdinalIgnoreCase))
            return;

        // Looking lists every folder next to every folder up the path, which in somewhere like Temp is thousands of them
        // and was 60 ms an open, so what was found for a folder is kept until something changes in the workspace
        string from = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        if (!languagesNear.TryGetValue(from, out string? folder))
            languagesNear[from] = folder = Horizon.Content.Localisation.FindNear(path);

        if (folder is null || folder == Horizon.Content.Localisation.Folder)
            return;

        var languages = Horizon.Content.Localisation.Available(folder);
        if (languages.Count == 0)
            return;

        string language = languages.Contains(Horizon.Content.Localisation.DEFAULT_LANGUAGE) ? Horizon.Content.Localisation.DEFAULT_LANGUAGE : languages[0];
        if (Horizon.Content.Localisation.Load(folder, language) && announce)
            Say($"the texts of this layout are in {language}, out of {folder}");
    }

    /// <summary>The File menu's files, Edit's undo and redo, and Ctrl+Tab through what is open, all going while typing.</summary>
    private void AddDocumentCommands()
    {
        // Ctrl+N is a scratchpad, a file of text to put something down in, a new layout is on the File menu
        commands.Add("File/new", "New Scratchpad", NewScratchpad, "Ctrl+N", "new-file", UIKeyUse.BoundWhileTyping);
        commands.Add("File/new", "New Layout", () => Show(NewDocument()), icon: "layout");
        commands.Add("File/new", "New File...", () => explorer.NewFileIn(explorer.workspace.IsEmpty ? browseDirectory : explorer.workspace.Folders[0]), icon: "file-add");
        commands.Add("File/new", "Open...", OpenPressed, "Ctrl+O", "folder-opened", UIKeyUse.BoundWhileTyping);
        recentMenu = commands.AddMenu("File/new", "Open Recent", "history");
        FillRecentMenu();

        commands.Add("File/save", "Save", Save, "Ctrl+S", "save", UIKeyUse.BoundWhileTyping);
        commands.Add("File/save", "Save As...", SaveAs);
        commands.Add("File/save", "Save All", SaveAll, "Ctrl+Shift+S", "save-all", UIKeyUse.BoundWhileTyping);
        commands.Add("File/save", "Revert File", RevertFile, icon: "discard").IsEnabled = () => document.Path is not null && (document.Modified || document.CodeEdited);

        commands.Add("File/close", "Close", Close, "Ctrl+W", "close", UIKeyUse.BoundWhileTyping);
        commands.Add("File/close", "Close Others", () => CloseAll(except: document), icon: "close-all").IsEnabled = () => documents.Count > 1;
        commands.Add("File/close", "Close All", () => CloseAll(), icon: "close-all");

        // VS Code's, the file closed last back and the path of the one on screen copied
        commands.Add("File/close", "Reopen Closed Editor", ReopenClosed, "Ctrl+Shift+T", "history", UIKeyUse.BoundWhileTyping).IsEnabled = () => closed.Count > 0;
        commands.Add("File/path", "Copy Path", () => CopyPath(relative: false), "Shift+Alt+C", "copy", UIKeyUse.BoundWhileTyping).IsEnabled = () => document.Path is not null;
        commands.Add("File/path", "Copy Relative Path", () => CopyPath(relative: true)).IsEnabled = () => document.Path is not null;

        commands.Add("Edit/history", "Undo", Undo, "Ctrl+Z", "discard", UIKeyUse.BoundWhileTyping).IsEnabled = () => !LayoutTools || document.CanUndo || designer.recordPending;
        commands.Add("Edit/history", "Redo", Redo, "Ctrl+Y", "redo", UIKeyUse.BoundWhileTyping).IsEnabled = () => !LayoutTools || document.CanRedo;

        // Ctrl+Tab goes through the files that are open, the way every editor does, with shift the other way
        commands.Bind(Key.Tab, () => CycleDocuments(1), control: true, whileTyping: true, shift: false);
        commands.Bind(Key.Tab, () => CycleDocuments(-1), control: true, whileTyping: true, shift: true);
    }

    /* Undoing, the view of an extension's own, the code's, or the layout's */

    private void Undo()
    {
        // The view of an extension undoes its own changes, if it takes the key
        if (ExtensionKey("undo"))
            return;

        // The editor of code undoes its own typing (it is handed the key as well), a file of code has no layout to undo
        if (compositor.Focus is CodeEditor)
            return;
        if (!LayoutTools)
        {
            code.Document.Undo();
            return;
        }

        if (document.Undo())
            designer.Restored("undone");
        else
            Say("nothing to undo");
    }

    private void Redo()
    {
        if (ExtensionKey("redo"))
            return;

        if (compositor.Focus is CodeEditor)
            return;
        if (!LayoutTools)
        {
            code.Document.Redo();
            return;
        }

        if (document.Redo())
            designer.Restored("redone");
        else
            Say("nothing to redo");
    }
}
