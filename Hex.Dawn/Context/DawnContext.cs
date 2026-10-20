using Hex.Shell;

using Horizon.Core.Threading;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>
/// What a part of the editor that has a class of its own (the languages, and the rest as they come out of HexScene)
/// is handed to work with, instead of the scene: the open documents, the workspace, saying things, work off the
/// editor's thread, the little windows, the settings and the commands. It reaches nothing else of the editor, and the
/// other parts reach it through what it adds to these (a source of diagnostics, a command, a page of settings).
/// </summary>
internal sealed class DawnContext(IDocuments documents, IWorkspace workspace, INotices notices, IWork work, IWindows windows,
    ISettingsHost settings, HexCommands commands, HexOptions options, Action wake)
{
    public IDocuments Documents { get; } = documents;
    public IWorkspace Workspace { get; } = workspace;
    public INotices Notices { get; } = notices;
    public IWork Work { get; } = work;
    public IWindows Windows { get; } = windows;
    public ISettingsHost Settings { get; } = settings;
    public HexCommands Commands { get; } = commands;

    /// <summary>What the editor was started with, whether it is testing itself among them.</summary>
    public HexOptions Options { get; } = options;

    /// <summary>Wakes the editor's thread from another, which sleeps while nothing is going on, for something that came in.</summary>
    public void Wake() => wake();
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
    IReadOnlyList<HexDocument> Open { get; }

    /// <summary>The open document of a file, by its path, null when it isn't open.</summary>
    HexDocument? OpenAt(string path);

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
}

/// <summary>The folders of the workspace, and what changes in them on the disk.</summary>
internal interface IWorkspace
{
    /// <summary>The workspace's folders, none for no workspace.</summary>
    IReadOnlyList<string> Folders { get; }

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

    /// <summary>The folder the dialogs start in when nothing says another.</summary>
    string BrowseFolder { get; }
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
