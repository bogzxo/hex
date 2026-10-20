using System.Diagnostics;
using System.Numerics;

using Hex.Shell;

using Horizon.Core.Threading;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The scene as what the parts of the editor with a class of their own are handed (DawnContext). Each of these is the
// scene's own doing passed on, what's new is the events and the sources the parts add, which the rest of the scene
// raises and asks at the moments it used to call into them by name.
internal sealed partial class HexScene : IDocuments, IWorkspace, INotices, IWork, IWindows, ISettingsHost, ITerminals, IPanels, IStrings
{
    private DawnContext? context;

    // The parts with a class of their own, made with the context
    private HexLanguages languages = null!;
    private HexTasks tasks = null!;
    private HexDebugging debugger = null!;
    private HexTerminalFiles terminalFiles = null!;
    private HexExtensions extensions = null!;

    /// <summary>What a part of the editor is made with.</summary>
    private DawnContext Context => context ??= new DawnContext(this, this, this, this, this, this, this, this, this, commands, options,
        () => Engine.WindowManager.Wake(), () => Engine.WindowManager.RequestFrames(), () => Engine.Input.Keyboard, () => compositor.Focus is not null);

    /* The documents */

    private Action<HexDocument>? documentOpened, documentEdited, documentClosed;
    private readonly List<Func<HexDocument, IReadOnlyList<UICodeDiagnostic>?>> diagnosticSources = [];
    private readonly List<HexCompletionSource> completionSources = [];
    private readonly List<Func<HexDocument, CodeEditor, UITextPosition, string?>> hoverSources = [];
    private readonly List<Func<HexDocument, string, int, UISignature?>> signatureSources = [];

    IReadOnlyList<HexDocument> IDocuments.All => documents;
    bool IDocuments.Open(string path) => Open(path);

    HexDocument IDocuments.Current => document;
    void IDocuments.OpenAt(string path, int line, int column) => OpenAt(path, line, column);

    void IDocuments.SaveAll(Func<HexDocument, bool> which)
    {
        HexDocument was = document;
        foreach (HexDocument open in documents.ToList())
        {
            if (which(open))
            {
                Show(open);
                Save();
            }
        }

        if (documents.Contains(was))
            Show(was);
    }

    HexDocument? IDocuments.OpenBehind(string path)
    {
        string full = Path.GetFullPath(path);
        if (OpenDocument(full) is { } open)
            return open;
        if (!File.Exists(full))
            return null;

        // As it is on the disk, line endings and all, the editor keeps them and its save writes them back the same
        OpenText(full, File.ReadAllText(full), show: false);
        return OpenDocument(full);
    }

    void IDocuments.Rewrite(HexDocument of, string text) => Rewrite(of, text);

    /// <summary>A document's text replaced as one step to undo, its tab saying it has changes when it isn't the one on screen.</summary>
    private void Rewrite(HexDocument of, string text)
    {
        of.Editor.Document.Replace(text);
        if (of != document)
        {
            of.Modified = of.Editor.Text != of.SavedCode;
            tabsDirty = true;
        }
    }

    HexDocument? IDocuments.Find(string path) => OpenDocument(path);

    /// <summary>The document open in Hex for a file, null if it isn't open.</summary>
    private HexDocument? OpenDocument(string path) =>
        documents.Find(open => open.Path is { } at && string.Equals(Path.GetFullPath(at), Path.GetFullPath(path), PlatformPaths.Comparison));

    event Action<HexDocument> IDocuments.Opened { add => documentOpened += value; remove => documentOpened -= value; }
    event Action<HexDocument> IDocuments.Edited { add => documentEdited += value; remove => documentEdited -= value; }
    event Action<HexDocument> IDocuments.Closed { add => documentClosed += value; remove => documentClosed -= value; }

    void IDocuments.AddDiagnostics(Func<HexDocument, IReadOnlyList<UICodeDiagnostic>?> source) => diagnosticSources.Add(source);
    void IDocuments.DiagnosticsChanged(HexDocument of) => ShowDiagnostics(of);
    void IDocuments.AddCompletions(HexCompletionSource source) => completionSources.Add(source);
    void IDocuments.AddHovers(Func<HexDocument, CodeEditor, UITextPosition, string?> source) => hoverSources.Add(source);
    void IDocuments.AddSignatures(Func<HexDocument, string, int, UISignature?> source) => signatureSources.Add(source);

    /* The workspace */

    private readonly List<Func<IEnumerable<string>>> watchedToo = [];
    private Action<IReadOnlyList<(string Path, LanguageServerFileChange Change)>>? filesChanged;
    private Action<string>? folderDeleting;

    IReadOnlyList<string> IWorkspace.Folders => workspace.Folders;
    IReadOnlyList<string> IWorkspace.Roots => WorkspaceRoots();
    string IWorkspace.Relative(string path) => Relative(path);
    void IWorkspace.WatchToo(Func<IEnumerable<string>> folders) => watchedToo.Add(folders);
    void IWorkspace.Rewatch() => WatchFolders();
    event Action<IReadOnlyList<(string Path, LanguageServerFileChange Change)>> IWorkspace.FilesChanged { add => filesChanged += value; remove => filesChanged -= value; }
    event Action<string> IWorkspace.Deleting { add => folderDeleting += value; remove => folderDeleting -= value; }

    /// <summary>Watches the folders of the workspace (for the explorer) and the ones the parts of the editor want watched too, and nothing else.</summary>
    private void WatchFolders() =>
        folders.Watch(workspace.Folders.Concat(watchedToo.SelectMany(more => more())).Where(Directory.Exists).Distinct(PlatformPaths.Comparer));

    /// <summary>Every tick, what changed on the disk in the folders that are watched said to whoever wants to know.</summary>
    private void UpdateFileChanges()
    {
        if (filesChanged is not null && folders.TakeFileChanges() is { Count: > 0 } changes)
            filesChanged(changes);
    }

    /* Saying things, and work */

    void INotices.Say(string message, bool error) => Say(message, error);
    void INotices.Note(string text, bool error) => Note(text, error);
    UINotification INotices.Notify(string message, bool error, params (string Label, Action Pressed)[] actions) => Notify(message, error, actions);

    WorkItem IWork.Start(string key, string title, Func<WorkContext, Task> what, Action<WorkItem>? done, bool seen, bool cancellable) =>
        StartWork(key, title, what, done, seen, cancellable);
    WorkItem IWork.Start(string key, string title, Action<WorkContext> what, Action<WorkItem>? done, bool seen, bool cancellable) =>
        StartWork(key, title, what, done, seen, cancellable);
    void IWork.Renew(WorkItem item) => RenewWork(item);
    WorkItem? IWork.Find(string key) => work.Find(key);

    /* The little windows */

    WindowPanel IWindows.ShowText(string title, string markdown, float height) => ShowText(title, markdown, height);
    void IWindows.OpenSettings(string? section) => OpenSettings(section);
    void IWindows.OpenInBrowser(string url) => OpenInBrowser(url);
    void IWindows.ChooseFile(string title, string folder, (string Name, string Pattern)[] filter, Action<string> picked)
    {
        if (!options.SelfTest)
        {
            Ask(HexDialogs.OpenFile(title, folder, filter, DialogHost), picked);
            return;
        }

        // Testing itself the path is typed, there's nobody to click through a dialog
        AskText(title, "The path of the file", folder, "Pick", typed =>
        {
            if (File.Exists(typed.Trim()))
                picked(typed.Trim());
            else
                Say($"there is no file {typed}", error: true);
        });
    }

    void IWindows.ChooseFolder(Action<string> picked) => AskFolder(picked);

    void IWindows.Ask(string title, string text, string[] buttons, Action<string> answered)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();
        textDialog = UIDialog.Show(chrome, title, text, [.. buttons.Select(label => new DialogChoice(label, () => answered(label)))]);
    }

    void IWindows.Prompt(string title, string text, string value, string button, string placeholder, Action<string> typed) =>
        AskText(title, text, value, button, typed, box => box.Placeholder = placeholder);

    void IWindows.Menu(Menu menu) => contextMenu = ContextMenu.ShowAt(chrome, compositor.Pointer.Position, menu);
    string IWindows.BrowseFolder => browseDirectory;
    void IWindows.Choose(string title, string message, List<(string Text, Action Picked)> choices) => ChooseFrom(title, message, choices);

    /// <summary>A page in the browser the system has.</summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say($"{url} couldn't be opened, {e.Message}", error: true);
        }
    }

    /* The settings */

    // What the parts add to the settings window, made each time it opens
    private readonly List<(Func<ISettingsSource> Source, string? Section, Func<UIComponent>? Panel, Action? Opened, Action? Closed)> settingSources = [];

    HexSettings ISettingsHost.Values => settings;
    void ISettingsHost.Save() => SaveSettings();
    void ISettingsHost.AddSource(Func<ISettingsSource> source, string? section, Func<UIComponent>? panel, Action? opened, Action? closed) =>
        settingSources.Add((source, section, panel, opened, closed));
    void ISettingsHost.Refresh(string key) => SettingsPage?.Refresh(key);

    /* The terminal page */

    TerminalHost ITerminals.Host => terminals;

    void ITerminals.RunInTab(string key, string title, string folder, string commandLine)
    {
        ShowPanel(CONSOLE_PAGE);
        string line = commandLine + "\r";

        TerminalTab? tab = terminalPanel.Tabs.FirstOrDefault(open => open.Tag is string was && was == key);
        if (tab?.Views.FirstOrDefault() is { IsRunning: true } view)
        {
            terminalPanel.Select(tab);
            terminalPanel.FocusPane(view);
            view.Send(line);
            return;
        }

        if (tab is not null)
            terminalPanel.CloseTab(tab);
        tab = terminalPanel.NewTab(terminals.UsualProfile, folder);
        tab.Tag = key;
        tab.Renamed = title;
        tab.Views.FirstOrDefault()?.SendWhenReady(line);
    }

    /* The side bar and the panel */

    // The views of the side bar the parts fill, by the view
    private readonly Dictionary<HexSide, (string Title, Func<List<UITreeItem>> Rows)> sideViews = [];

    void IPanels.AddSide(HexSide side, string title, Func<List<UITreeItem>> rows) => sideViews[side] = (title, rows);

    void IPanels.ShowSide(HexSide side)
    {
        left.Show(true);
        Side = side;
    }

    bool IPanels.Showing(HexSide side) => Side == side;

    void IPanels.RefreshSide(HexSide side)
    {
        if (Side == side)
            treeDirty = true;
    }

    void IPanels.RefreshRows() => sideTree.Refresh();
    void IPanels.ShowOutput() => ShowPanel(OUTPUT_PAGE);
    void IPanels.RefreshProblems() => problemsDirty = true;

    bool IPanels.SideBarShown
    {
        get => left.Shown;
        set
        {
            if (left.Shown != value)
                ToggleSideBar();
        }
    }

    bool IPanels.PropertiesShown
    {
        get => right.Shown;
        set
        {
            if (right.Shown != value)
                ToggleProperties();
        }
    }
    Vector4 IPanels.Dim => DimColor;
    Vector4 IPanels.Error => ErrorColor;

    /// <summary>A view of the side bar a part fills, its rows made again. False when no part fills the one it's on.</summary>
    private bool RebuildSideView()
    {
        if (!sideViews.TryGetValue(Side, out var view))
            return false;

        UseSideTree(true);
        treeTitle.Text = view.Title;
        sideTree.Items = view.Rows();
        return true;
    }

    /* Hex's texts */

    string IStrings.Said(string key, params (string Name, string Value)[] filled) => Said(key, filled);
}
