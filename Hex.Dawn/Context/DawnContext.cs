using System.Numerics;

using Hex.Shell;

using Horizon.Core.Threading;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>
/// What a part of the editor that has a class of its own (the languages, and the rest as they come out of DawnScene)
/// is handed to work with, instead of the scene: the open documents, the workspace, saying things, work off the
/// editor's thread, the little windows, the settings and the commands. It reaches nothing else of the editor, and the
/// other parts reach it through what it adds to these (a source of diagnostics, a command, a page of settings).
/// </summary>
internal sealed class DawnContext(IDocuments documents, IWorkspace workspace, INotices notices, IWork work, IWindows windows,
    ISettingsHost settings, ITerminals terminals, IPanels panels, IStrings strings, IEditorWindow window, UICommands commands, HexOptions options,
    Action wake, Action redraw, Func<Keyboard> keyboard, Func<bool> typing)
{
    public IDocuments Documents { get; } = documents;
    public IWorkspace Workspace { get; } = workspace;
    public INotices Notices { get; } = notices;
    public IWork Work { get; } = work;
    public IWindows Windows { get; } = windows;
    public ISettingsHost Settings { get; } = settings;
    public ITerminals Terminals { get; } = terminals;
    public IPanels Panels { get; } = panels;
    public IStrings Strings { get; } = strings;
    public IEditorWindow Window { get; } = window;
    public UICommands Commands { get; } = commands;

    /// <summary>What the editor was started with, whether it is testing itself among them.</summary>
    public HexOptions Options { get; } = options;

    /// <summary>Wakes the editor's thread from another, which sleeps while nothing is going on, for something that came in.</summary>
    public void Wake() => wake();

    /// <summary>Has the window drawn again for a while, for something that changed without anybody touching it.</summary>
    public void Redraw() => redraw();

    /// <summary>The keyboard, for a part that has keys of its own that aren't commands.</summary>
    public Keyboard Keyboard => keyboard();

    /// <summary>Whether something has the keyboard, a text box or the code, which a key without control is typing into.</summary>
    public bool Typing => typing();
}

/// <summary>
/// What a file of another language is offered at the caret, null for nothing. <paramref name="handled"/> says the file is
/// one of the source's, so the editor's own completions (HIDL's) aren't wanted, null or not.
/// </summary>
internal delegate UICompletionRequest? HexCompletionSource(HexDocument of, CodeEditor editor, out bool handled);

/// <summary>The documents that are open, and what happens to them.</summary>
internal interface IDocuments
{
    /// <summary>Every document that is open, in the order of their tabs.</summary>
    IReadOnlyList<HexDocument> All { get; }

    /// <summary>Opens a file and shows it, the tab it has when it's open already. False when it couldn't be read.</summary>
    bool Open(string path);

    /// <summary>The document on screen.</summary>
    HexDocument Current { get; }

    /// <summary>The open document of a file, by its path, null when it isn't open.</summary>
    HexDocument? Find(string path);

    /// <summary>A file opened as text in a tab behind the one on screen, or the one it has open already. Null when it can't be read.</summary>
    HexDocument? OpenBehind(string path);

    /// <summary>A document's text replaced as one step to undo, its tab saying it has changes when it isn't the one on screen.</summary>
    void Rewrite(HexDocument of, string text);

    /// <summary>Opens a file, or shows it open, with the caret at a line and column of it (from 0).</summary>
    void OpenAt(string path, int line, int column);

    /// <summary>Saves the documents with changes that a test says, each where it came from, the one on screen staying on screen.</summary>
    void SaveAll(Func<HexDocument, bool> which);

    /// <summary>A file was opened as text in the language its name says, before it is shown.</summary>
    event Action<HexDocument> Opened;

    /// <summary>The text of a document was typed into, or changed by something else.</summary>
    event Action<HexDocument> Edited;

    /// <summary>A document was closed.</summary>
    event Action<HexDocument> Closed;

    /// <summary>Something that has diagnostics for documents, which go under their code and in the Problems panel with the rest.</summary>
    void AddDiagnostics(Func<HexDocument, IReadOnlyList<UICodeDiagnostic>?> source);

    /// <summary>What a source says about a document changed, its code and the Problems panel are told again.</summary>
    void DiagnosticsChanged(HexDocument of);

    /// <summary>Something that completes what is typed in some files, asked before the editor's own completions.</summary>
    void AddCompletions(HexCompletionSource source);

    /// <summary>Something that says what a word of some files is as the pointer rests on it (markdown), null to leave it to the next.</summary>
    void AddHovers(Func<HexDocument, CodeEditor, UITextPosition, string?> source);

    /// <summary>Something that says what a call in some files takes, the argument being typed picked out, null to leave it to the next.</summary>
    void AddSignatures(Func<HexDocument, string, int, UISignature?> source);
}

/// <summary>The folders of the workspace, and what changes in them on the disk.</summary>
internal interface IWorkspace
{
    /// <summary>The workspace's folders, none for no workspace.</summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>The folders things are looked for in, the workspace's, or the repository Hex runs from without one.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>A file by where it is in the folders things are looked for in, with the name of its folder in front when there is more than one.</summary>
    string Relative(string path);

    /// <summary>More folders to watch than the workspace's, asked whenever what is watched is put together again.</summary>
    void WatchToo(Func<IEnumerable<string>> folders);

    /// <summary>What is watched put together again, the workspace's folders and the ones of <see cref="WatchToo"/>.</summary>
    void Rewatch();

    /// <summary>
    /// Files that were made, changed or taken away in the folders that are watched, each once with what happened to it
    /// last, every tick something did. The folders of builds and of git aren't said.
    /// </summary>
    event Action<IReadOnlyList<(string Path, LanguageServerFileChange Change)>> FilesChanged;

    /// <summary>A folder is about to go in the trash, for whatever runs in it to stop first (a process's folder can't go on Windows).</summary>
    event Action<string> Deleting;
}

/// <summary>Saying things, in the status bar, in a card or in the output.</summary>
internal interface INotices
{
    /// <summary>A line in the status bar, for a moment.</summary>
    void Say(string message, bool error = false);

    /// <summary>A line in the output, Output in the panel.</summary>
    void Note(string text, bool error = false);

    /// <summary>A card in the corner, with buttons that do something.</summary>
    UINotification Notify(string message, bool error = false, params (string Label, Action Pressed)[] actions);
}

/// <summary>Work off the editor's thread, each piece known by a key so it goes once at a time, with a card while it goes.</summary>
internal interface IWork
{
    /// <summary>
    /// Starts a piece of work known by a key, or hands back the one of that key already going. <paramref name="seen"/>
    /// puts a card up for it while it goes. <paramref name="done"/> on the editor's thread once it's over.
    /// </summary>
    WorkItem Start(string key, string title, Func<WorkContext, Task> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true);

    /// <inheritdoc cref="Start(string, string, Func{WorkContext, Task}, Action{WorkItem}?, bool, bool)"/>
    WorkItem Start(string key, string title, Action<WorkContext> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true);

    /// <summary>
    /// Something done off the editor's thread, quickly and without a key, what it came to handed over on the editor's.
    /// What the work reads of the editor it is given before it goes, never reaches for while it runs.
    /// </summary>
    void InBackground<T>(string doing, Func<T> work, Action<T> done, bool quiet = false);

    /// <summary>A piece of work that's going brought to notice again, its card back up.</summary>
    void Renew(WorkItem item);

    /// <summary>The piece of work of a key that's going, null when none is.</summary>
    WorkItem? Find(string key);
}

/// <summary>The little windows over the editor and the dialogs.</summary>
internal interface IWindows
{
    /// <summary>A window with a page of markdown in it that scrolls.</summary>
    WindowPanel ShowText(string title, string markdown, float height = 520.0f);

    /// <summary>The settings, at a section when one is said.</summary>
    void OpenSettings(string? section = null);

    /// <summary>A page in the system's browser.</summary>
    void OpenInBrowser(string url);

    /// <summary>Hex's own dialog for a file to open, from a folder, something done with the one picked.</summary>
    void ChooseFile(string title, string folder, (string Name, string Pattern)[] filter, Action<string> picked);

    /// <summary>
    /// One of a list of things to pick, narrowed down by what is typed (the palette's list), something done with the one
    /// picked.
    /// </summary>
    void Choose(string title, string message, List<(string Text, Action Picked)> choices);

    /// <summary>A question with buttons, the label of the one pressed handed on.</summary>
    void Ask(string title, string text, string[] buttons, Action<string> answered);

    /// <summary>A line of text asked for, what was typed handed on, enter or the button.</summary>
    void Prompt(string title, string text, string value, string button, string placeholder, Action<string> typed);

    /// <summary>A menu where the pointer is.</summary>
    void Menu(Menu menu);

    /// <summary>A menu at a place of the chrome, where something was right clicked.</summary>
    void MenuAt(Menu menu, Vector2 at);

    /// <summary>A folder picked with Hex's own dialog, something done with it.</summary>
    void ChooseFolder(Action<string> picked);

    /// <summary>The folder the dialogs start in when nothing says another, the one files were last opened from.</summary>
    string BrowseFolder { get; set; }

    /// <summary>Where Hex's own dialogs go, over the editor, with the workspace's folders among their places.</summary>
    UIDialogHost DialogHost { get; }

    /// <summary>What a dialog hands back done with once it does, one dialog at a time (the self test types instead).</summary>
    void Pick(Task<string?> asked, Action<string> picked);
}

/// <summary>The settings, and the window they're changed in.</summary>
internal interface ISettingsHost
{
    /// <summary>The settings as they are.</summary>
    HexSettings Values { get; }

    /// <summary>Writes the settings to their file, after a change.</summary>
    void Save();

    /// <summary>
    /// A source of rows for the settings window, made each time it opens. <paramref name="panel"/> goes under the rows of
    /// <paramref name="section"/>, <paramref name="opened"/> and <paramref name="closed"/> as the window comes and goes.
    /// </summary>
    void AddSource(Func<ISettingsSource> source, string? section = null, Func<UIComponent>? panel = null, Action? opened = null, Action? closed = null);

    /// <summary>A row of the window read again, while it is up.</summary>
    void Refresh(string key);
}

/// <summary>The terminal page under the editor, its tabs.</summary>
internal interface ITerminals
{
    /// <summary>The terminal host Dawn's terminal page runs on, its plugins, its profiles and what its object has.</summary>
    TerminalHost Host { get; }

    /// <summary>
    /// Runs a command line in a terminal tab of its own, known by a key: the tab of that key again while its shell is
    /// still there, a new one in a folder otherwise, named so. The page is brought up with it.
    /// </summary>
    void RunInTab(string key, string title, string folder, string commandLine);

    /// <summary>
    /// Runs a command line in a Dusk window of its own, in a folder, with the shell the terminal page uses, or as a new
    /// tab of the Dusk that's running when there is one.
    /// </summary>
    void RunInDusk(string folder, string commandLine);

    /// <summary>The id of the shell a new terminal starts, pwsh, powershell, cmd, bash, which a command line is written for.</summary>
    string UsualProfileId { get; }
}

/// <summary>The side bar and the panel under the editor.</summary>
internal interface IPanels
{
    /// <summary>A view of the side bar a part fills, its title and rows made again whenever it's shown or refreshed.</summary>
    void AddSide(HexSide side, string title, Func<List<UITreeItem>> rows);

    /// <summary>A view of the side bar brought up, the bar shown with it.</summary>
    void ShowSide(HexSide side);

    /// <summary>Whether the side bar is on a view.</summary>
    bool Showing(HexSide side);

    /// <summary>A view's rows made again, if it's the one the side bar is on.</summary>
    void RefreshSide(HexSide side);

    /// <summary>The side bar's rows drawn again as they are, after one was filled in as it opened.</summary>
    void RefreshRows();

    /// <summary>The panel's output brought up.</summary>
    void ShowOutput();

    /// <summary>The panel's terminal page brought up, the console's and the terminals' tabs.</summary>
    void ShowTerminals();

    /// <summary>Whether the panel is up on its terminal page.</summary>
    bool TerminalsShown { get; }

    /// <summary>The colour of what is only a warning, the skin's.</summary>
    Vector4 Warning { get; }

    /// <summary>The Problems panel put together again, for problems of files that aren't on screen.</summary>
    void RefreshProblems();

    /// <summary>Whether the side bar is out, and putting it out or away as Ctrl+B does, kept as that is.</summary>
    bool SideBarShown { get; set; }

    /// <summary>Whether the properties are out, and putting them out or away as Ctrl+Alt+B does, kept as that is.</summary>
    bool PropertiesShown { get; set; }

    /// <summary>The colour of what is said quietly in a row, the skin's.</summary>
    Vector4 Dim { get; }

    /// <summary>The colour of what went wrong, the skin's.</summary>
    Vector4 Error { get; }
}

/// <summary>Hex's texts, Assets/hex/localisation, with what goes in their gaps.</summary>
internal interface IStrings
{
    /// <summary>The text of a key, {name} in it filled in.</summary>
    string Said(string key, params (string Name, string Value)[] filled);
}

/// <summary>
/// The editor's own window, what a part that has things of its own on screen needs of it: the layout it is made of (the
/// part's components found in it by name), the chrome popups and windows go over, the editor of code on screen, the
/// views of a document, the right side, and a few helpers the window has for its sides.
/// </summary>
internal interface IEditorWindow
{
    /// <summary>The layout the editor is made of, its parts by name.</summary>
    UILayout Layout { get; }

    /// <summary>What popups, menus and windows go over.</summary>
    UIModule Chrome { get; }

    UICompositor Compositor { get; }

    /// <summary>The editor of the document on screen, of its code.</summary>
    CodeEditor Code { get; }

    /// <summary>The document on screen shown as its code, its designer, its table or a view of an extension.</summary>
    void SwitchView(HexView view);

    /// <summary>The right side made again (the properties of a layout, the values of a file of code).</summary>
    void RefreshRight();

    /// <summary>A side panel made to be dragged wider or narrower by an edge, as the sides are.</summary>
    void Grip(UIComponent panel, UIResize edge, bool left);

    /// <summary>A little window dressed the way every window of the editor's is, its stuff and its glass.</summary>
    void Dress(WindowPanel window);

    /// <summary>A quiet line saying something where a list has nothing in it.</summary>
    Label Hint(UIComponent into, string text);

    /// <summary>The outline of the code on screen, what it declares and the keys of its objects.</summary>
    List<OutlineEntry> Outline();

    /// <summary>What the layouts being edited are drawn by, under the editor and seen through its canvas.</summary>
    UICompositor Stage { get; }

    /// <summary>Whether a part of the window has to be put together again before the next frame, and saying it has.</summary>
    bool this[DawnParts part] { get; set; }

    /// <summary>The side tree (the explorer, the outline) rather than the designer's list of components, on the side.</summary>
    void UseSideTree(bool on);

    /// <summary>Whether the side bar is on the files, and putting it on them or off.</summary>
    bool Browsing { get; set; }

    /// <summary>The code of the layout on screen, the caret on what a name of it declares.</summary>
    void ShowDeclaration(string name);

    /// <summary>The little window on top (the settings, a page of help) taken down, false when there is none.</summary>
    bool CloseTopWindow();

    /// <summary>The mouse, for the canvas's wheel and its middle button.</summary>
    Horizon.Input.Mouse Mouse { get; }

    /// <summary>What the file on screen came to the last time it ran, null when it hasn't (HexRunning's).</summary>
    HexValues? LastRun { get; }

    /// <summary>What the window has to do with the code on screen, added to its right click menu (run it, the palette).</summary>
    void AddCodeMenu(Menu menu);

    /// <summary>The places something was found, listed in the side bar's search view.</summary>
    void ShowResults(string title, List<HexResult> found);

    /// <summary>The view of the side bar that's what's on screen's own, the components or the outline.</summary>
    HexSide HomeSide { get; }

    /// <summary>The name of the document on screen shown again, it was renamed.</summary>
    void ShowName();

    /// <summary>What was last found in the files, put away.</summary>
    void ClearResults();

    /// <summary>The status bar's piece that says where the pointer is.</summary>
    StatusItem Coordinates { get; }
}

/// <summary>The parts of the editor's window that are put together again when what they show changed.</summary>
internal enum DawnParts
{
    Tabs,
    Tree,
    Right,
    Code
}
