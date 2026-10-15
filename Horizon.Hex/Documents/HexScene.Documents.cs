using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Making, opening, saving and closing layouts. Windows asks where, the editor testing itself (which can't click in a dialog of Windows) uses the list on the left to open and its workspace to save.
internal sealed partial class HexScene
{
    /* Documents */

    private HexDocument NewDocument()
    {
        string name = "layout";
        for (int number = 2; documents.Exists(other => other.Name == name); number++)
            name = $"layout{number}";

        var created = new HexDocument(name, stage.CreateModule()) { FallbackDirectory = browseDirectory };
        PrepareEditor(created);

        // Laid out as if the canvas were the screen it is made for, and kept inside of it
        created.Module.Viewport = DesignScreen;
        created.Module.Clip = DesignScreen;

        // An empty layout that nobody has touched has nothing to save
        created.SavedCode = created.GenerateCode();
        SetCode(created, created.SavedCode, fresh: true);

        documents.Add(created);
        return created;
    }

    private void Show(HexDocument shown)
    {
        // Whatever was being done to the layout that is put away is a step back in it, not in the next one
        if (recordPending && document is not null)
            document.Record();
        recordPending = false;

        document = shown;

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

        // Only the layout that is being worked on is on screen
        foreach (var other in documents)
            other.Module.Enabled = other == shown;

        nameBox.Text = shown.Name;
        MarkSelection();
        // The explorer of a workspace stays up the way it does in VS Code, the folder of files opened from goes
        browsing = browsing && !workspace.IsEmpty;
        runOutput.Clear();
        consoleRuntime = null;

        // Its editor is the one in the code panel
        foreach (var child in codeScroll.Children.ToArray())
            codeScroll.Remove(child);
        codeScroll.Add(shown.Editor);

        ApplyView();
        KeepWorkspace();
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
            OpenWorkspaceFile(path);
            return !workspace.IsEmpty;
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

        if (!Path.GetExtension(path).Equals(".hor", StringComparison.OrdinalIgnoreCase) || !Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(source))
            return OpenText(path, source);

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
        RaiseExtensions("on_open", new StringValue(Path.GetFullPath(path)));
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
            return scratch.Load(written).Success && scratch.GenerateCode() == written;
        }
        finally
        {
            stage.RemoveModule(scratch.Module);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /* Opening and saving. Windows asks where, the editor testing itself (which can't click in a dialog of
       Windows) uses the list on the left to open and its workspace to save. */

    private nint WindowHandle => Engine.WindowManager.Window.Native?.Win32?.Hwnd ?? 0;

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

        Ask(HexDialogs.Open(browseDirectory, WindowHandle), path =>
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

        Ask(HexDialogs.Save(directory, saved.Name + ".hor", WindowHandle), path =>
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

        try
        {
            // The code typed into a layout by hand goes into it first, what is saved is the layout
            if (document.CodeEdited)
                ApplyCode();

            string written = document.GenerateCode();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, document.CrLf ? written.Replace("\n", "\r\n") : written);

            document.Path = path;
            document.SavedCode = written;
            document.SavedCrLf = document.CrLf;
            ForgetVocabulary();
            document.Modified = false;

            // Its templates are looked for next to it, which may be somewhere else now
            document.RefreshPreviews();
            tabsDirty = treeDirty = true;
            Remember(path);
            RaiseExtensions("on_save", new StringValue(Path.GetFullPath(path)));

            Console.WriteLine($"[Hex] Saved '{path}'.");
            Say($"saved {path}");
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
        stage.Highlighted = null;
        stage.Highlights = [];

        stage.RemoveModule(document.Module);
        documents.Remove(document);
        ForgetExtensionViews(document);
        recordPending = false;

        Show(documents.Count > 0 ? documents[^1] : NewDocument());
    }

    /// <summary>
    /// Helper method to load the languages of whatever game a layout belongs to, the default one if there is one and
    /// the first there is otherwise, so the texts it names by key read the way they do in the game. Nothing changes
    /// for a layout that isn't near any.
    /// </summary>
    private void UseLanguagesNear(string path)
    {
        if (Horizon.Content.Localisation.FindNear(path) is not { } folder || folder == Horizon.Content.Localisation.Folder)
            return;

        var languages = Horizon.Content.Localisation.Available(folder);
        if (languages.Count == 0)
            return;

        string language = languages.Contains(Horizon.Content.Localisation.DEFAULT_LANGUAGE) ? Horizon.Content.Localisation.DEFAULT_LANGUAGE : languages[0];
        if (Horizon.Content.Localisation.Load(folder, language))
            Say($"the texts of this layout are in {language}, out of {folder}");
    }
}
