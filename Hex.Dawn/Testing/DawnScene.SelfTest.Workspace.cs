using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test, the workspace and the explorer, closing tabs and the window, the help windows and the conveniences.
internal sealed partial class DawnScene
{
    private void TestWorkspace(HexPointerScript test)
    {
        /* The workspace, more than one folder, the explorer, a file of it, and going places across it */

        string folderA = Path.Combine(testFolder, "ws_a"), folderB = Path.Combine(testFolder, "ws_b");
        string workspaceFile = Path.Combine(testFolder, "selftest.hex-workspace");

        test.Run(() =>
        {
            foreach (string folder in new[] { folderA, folderB })
                if (Directory.Exists(folder)) Directory.Delete(folder, true);

            Directory.CreateDirectory(Path.Combine(folderA, "inner"));
            Directory.CreateDirectory(folderB);
            System.IO.File.WriteAllText(Path.Combine(folderA, "a.hor"), "let alpha = 1;\n");
            System.IO.File.WriteAllText(Path.Combine(folderA, "inner", "deep.hor"), "let deep = 0;\n");
            System.IO.File.WriteAllText(Path.Combine(folderB, "b.hor"), "let beta = alpha + 1;\nlet names = { gamma: 2 };\n");

            explorer.AddFolder(folderA);
            explorer.AddFolder(folderB);
        });
        test.Wait();
        test.Check("workspace: two folders added, the explorer has both", () =>
            explorer.workspace.Folders.Count == 2 && browsing && treeTitle.Text.StartsWith("EXPLORER")
            && explorer.explorerRows.Any(row => row.IsFolder && row.Path == folderA) && explorer.explorerRows.Any(row => row.IsFolder && row.Path == folderB)
            && explorer.explorerRows.Any(row => row.Path == Path.Combine(folderA, "a.hor")) && explorer.explorerRows.Any(row => row.Path == Path.Combine(folderB, "b.hor")));
        test.Check("workspace: a folder in one of them is shut until it is opened up", () =>
            explorer.explorerRows.Any(row => row.Path == Path.Combine(folderA, "inner")) && !explorer.explorerRows.Any(row => row.Path.EndsWith("deep.hor")));

        test.Click(Explorer(() => Path.Combine(folderA, "inner")));
        test.Wait();
        test.Check("workspace: a click opens a folder up", () => explorer.explorerRows.Any(row => row.Path == Path.Combine(folderA, "inner", "deep.hor")));
        test.Click(Explorer(() => Path.Combine(folderB, "b.hor")));
        test.Wait();
        test.Check("workspace: a click on a file opens it", () => document.Path == Path.Combine(folderB, "b.hor") && document.IsText);

        List<string> everyFile = [];
        test.Run(() => everyFile = AllWorkspaceFiles());
        test.Check("workspace: what is looked through is every folder of it", () =>
            everyFile.Contains(Path.Combine(folderA, "inner", "deep.hor")) && everyFile.Contains(Path.Combine(folderB, "b.hor")));

        test.Run(() =>
        {
            code.GoTo(0, 12);
            codeEditing.GoToDefinition();
        });
        test.Wait();
        test.Check("workspace: F12 on a name goes to its let in the other folder", () =>
            document.Path == Path.Combine(folderA, "a.hor") && code.Document.Caret == new UITextPosition(0, 4));

        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\nlet x = be");
            code.Complete(typed: true);
        });
        test.WaitFor(() => codeEditing.vocabulary is not null && code.Completions.Any(item => item.Text == "beta"), 3.0f);
        test.Check("workspace: names of the other files are offered as they are typed, gathered off the editor's thread", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "beta"));
        test.Run(() =>
        {
            code.CloseCompletion();
            code.Document.Undo();
        });

        test.Run(() => explorer.MakeFile(Path.Combine(folderB, "made.hor")));
        test.Wait();
        test.Check("workspace: a file made in a folder is there and open", () =>
            System.IO.File.Exists(Path.Combine(folderB, "made.hor")) && document.Path == Path.Combine(folderB, "made.hor") && explorer.explorerRows.Any(row => row.Path.EndsWith("made.hor")));

        test.Run(() => explorer.SaveWorkspaceAs(workspaceFile));
        test.Check("workspace: saved to a file, it reads back the same, its folders relative to it", () =>
            System.IO.File.Exists(workspaceFile) && System.IO.File.ReadAllText(workspaceFile).Contains("\"ws_a\"")
            && HexWorkspace.Load(workspaceFile) is { } read && read.Folders.SequenceEqual(explorer.workspace.Folders) && read.OpenFiles.Contains(Path.Combine(folderB, "made.hor")));

        test.Run(() =>
        {
            explorer.CloseWorkspace();
            explorer.OpenWorkspaceFile(workspaceFile);
        });
        test.Wait();
        test.Check("workspace: closed and opened again from its file, its folders are back", () =>
            explorer.workspace.Folders.Count == 2 && explorer.workspace.FilePath == workspaceFile && explorer.explorerRows.Count > 2);

        test.Run(() => explorer.RemoveFolder(folderB));
        test.Wait();
        test.Check("workspace: a folder taken out is out of the explorer, its files where they were", () =>
            explorer.workspace.Folders.Count == 1 && !explorer.explorerRows.Any(row => row.Path.StartsWith(folderB)) && System.IO.File.Exists(Path.Combine(folderB, "b.hor")));
        test.Run(explorer.CloseWorkspace);
    }

    private void TestClosingTabs(HexPointerScript test)
    {
        /* The x of a tab, and of the window, asking first */

        Func<Vector2> CloseX = In(() => docTabs.CloseBounds(docTabs.Selected), 0.5f, 0.5f);
        Vector2 answer = Vector2.Zero;
        void Answer(int button)
        {
            test.Settle(0.35f);
            test.Run(() => answer = chrome.Dialog is { } asked ? chrome.ToWorld(asked.Buttons[button].Bounds.Center) : Vector2.Zero);
            test.Click(() => answer);
            test.Wait();
        }

        int openBefore = 0;
        string dataBefore = string.Empty;
        test.Run(() =>
        {
            Open(dataFile);
            openBefore = documents.Count;
            dataBefore = System.IO.File.ReadAllText(dataFile);
        });
        test.Wait();
        test.Click(CloseX);
        test.Wait();
        test.Check("close x: a tab with nothing to save goes at once", () => chrome.Dialog is null && documents.Count == openBefore - 1 && !documents.Any(open => open.Path == dataFile));

        test.Run(() =>
        {
            Open(dataFile);
            openBefore = documents.Count;
            code.Document.MoveToEnd(false);
            code.Document.Insert("let unsaved = 1;");
        });
        test.Wait();
        test.Click(CloseX);
        test.Wait();
        test.Check("close x: one with changes asks first", () => chrome.Dialog is { IsOpen: true } && documents.Count == openBefore);
        Answer(2);
        test.Check("close x: cancelled, it stays with what was typed", () => chrome.Dialog is null && documents.Count == openBefore && code.Text.Contains("let unsaved"));

        bool mayClose = true;
        test.Run(() => mayClose = Engine.WindowManager.CloseRequested!());
        test.Wait();
        test.Check("close window: with something unsaved the window stays and asks", () => !mayClose && chrome.Dialog is { IsOpen: true });
        Answer(2);
        test.Check("close window: cancelled, nothing goes", () => chrome.Dialog is null && !closeConfirmed && documents.Count == openBefore);

        // Through the window manager, the way the title bar's x and File, Exit go. Asked once, answered, and it stays answered
        test.Run(() => Engine.WindowManager.Close());
        test.Settle(0.4f);
        test.Check("close window: the x asks once, one question up", () => chrome.Dialog is { IsOpen: true } && chrome.Dialog == closeQuestion);
        Answer(2);
        test.Settle(0.4f);
        test.Check("close window: and cancelled it isn't asked again", () => chrome.Dialog is null && !closeConfirmed && documents.Count == openBefore);

        test.Click(CloseX);
        test.Wait();
        Answer(1);
        test.Check("close x: told not to save, it goes and the file is as it was", () =>
            chrome.Dialog is null && documents.Count == openBefore - 1 && System.IO.File.ReadAllText(dataFile) == dataBefore);

        // Ends on a layout's designer, the canvas is where the pointer goes last
        test.Run(() => Open(savedFile));
        test.Wait();
    }

    private void TestHelpWindows(HexPointerScript test)
    {
        /* The windows of the Help menu, their texts out of Hex's own file of them, wrapped to the window as it is resized */

        WindowPanel? help = null;
        Label? page = null;
        int linesWide = 0;
        test.Run(() =>
        {
            ShowLanguageHelp();
            help = chrome.Root.Children.OfType<WindowPanel>().LastOrDefault();
            page = help is null ? null : Descendants(help).OfType<Label>().FirstOrDefault(label => label.Markdown);
        });
        test.Settle(0.3f);
        test.Check("help: the language's page comes out of Hex's texts, not its key", () =>
            help is { Title: "HIDL Reference" } && page is { Text: var said } && said.StartsWith("# HIDL") && !said.Contains("@language"));

        test.Run(() =>
        {
            linesWide = page!.LineCount;
            help!.Size = help.Size with { X = 420 };
        });
        test.Settle(0.3f);
        test.Check("help: made narrower the page wraps to it, more lines and none past its edge", () =>
            page!.LineCount > linesWide && page.Bounds.Max.X <= help!.Bounds.Max.X + 1.0f);

        test.Run(() =>
        {
            ShowAbout();
            help = chrome.Root.Children.OfType<WindowPanel>().LastOrDefault();
        });
        test.Settle(0.2f);
        test.Run(() =>
        {
            ShowChangelog();
            help = chrome.Root.Children.OfType<WindowPanel>().LastOrDefault();
        });
        test.Settle(0.2f);
        test.Check("help: the changelog goes down to 0.0.1, and says which Hex this is", () =>
            help is { Title: "Release Notes" } && Descendants(help).OfType<Label>().Any(label =>
                label.Text.Contains("## 0.0.1") && label.Text.Contains($"This is Hex {HexAbout.Version}")));

        test.Run(() =>
        {
            ShowAbout();
            help = chrome.Root.Children.OfType<WindowPanel>().LastOrDefault();
        });
        test.Settle(0.2f);
        test.Check("help: about fills in what Hex knows, the version", () =>
            help is { Title: "About Hex" } && Descendants(help).OfType<Label>().Any(label => label.Text.StartsWith("# Hex ") && !label.Text.Contains("{version}")));
        test.Run(() =>
        {
            foreach (WindowPanel open in chrome.Root.Children.OfType<WindowPanel>().ToList())
                open.Parent?.Remove(open);
        });
    }

    private void TestConveniences(HexPointerScript test)
    {
        /* Conveniences. Escape puts a little window away, F2 renames, Ctrl+Tab goes through the files, the status bar
           lets go of an old message */

        test.Run(() =>
        {
            OpenSettings();
            ShowAbout();
        });
        test.Settle(0.2f);
        test.Run(() => CloseTopWindow());
        test.Wait();
        test.Check("conveniences: escape puts away the little window on top, and only it", () =>
            chrome.Root.Children.OfType<WindowPanel>().Count() == 1 && SettingsWindow is { Visible: true });
        test.Run(() => CloseTopWindow());
        test.Wait();
        test.Check("conveniences: and the next one after it", () => !chrome.Root.Children.OfType<WindowPanel>().Any() && SettingsWindow is null);

        test.Run(() =>
        {
            Open(savedFile);
            SwitchView(HexView.Design);
            designer.Select(document.Walk().Select(pair => pair.Component).FirstOrDefault());
        });
        test.Settle(0.3f);
        test.Run(RenameSelected);
        test.Wait();
        test.Check("conveniences: F2 in the designer puts the name of what is selected in front of you to type over", () =>
        {
            bool found = designer.editors.TryGetValue("name", out UIComponent? name);
            Console.WriteLine($"[Hex self-test]   F2: selected {document.Selected?.GetType().Name}, editor {found} {name?.GetType().Name}, focus {compositor.Focus?.GetType().Name}, selection '{(name as TextBox)?.Selection}' of '{(name as TextBox)?.Text}', tools {LayoutTools}");
            return found && name is TextBox box && box.IsFocused && box.Selection == box.Text && box.Text.Length > 0;
        });
        test.Run(() => compositor.Focus = null);

        HexDocument? wasShowing = null;
        test.Run(() =>
        {
            Open(dataFile);
            wasShowing = document;
            CycleDocuments(1);
        });
        test.Wait();
        test.Check("conveniences: Ctrl+Tab goes to the next file that is open, and round again with shift", () => document != wasShowing);
        test.Run(() => CycleDocuments(-1));
        test.Wait();
        test.Check("conveniences: back with shift", () => document == wasShowing);

        test.Run(() => saidAt = System.Environment.TickCount64 - SAID_FOR - 1);
        test.Wait();
        test.Check("conveniences: an old message goes from the status bar", () => messageItem.Text.Length == 0);
    }
}
