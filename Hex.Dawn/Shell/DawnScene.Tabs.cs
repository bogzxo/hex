using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The tabs over the editor (a native TabStrip) and the breadcrumbs under them, where the file is and, in code, where
// the caret is in it, or in the designer what the selected component is inside of.
internal sealed partial class DawnScene
{
    private readonly List<Button> tabButtons = [];

    private TabStrip docTabs = null!;

    private Breadcrumbs crumbs = null!;

    // The breadcrumbs' pieces and what a click on each does
    private readonly List<string> crumbParts = [];
    private readonly List<Action> crumbActions = [];
    private (HexDocument? Document, int Version, int Line, UIComponent? Selected, HexView View) crumbsFor;
    private bool crumbsPlain;

    private void BuildTabs()
    {
        docTabs = layout.Get<TabStrip>("doc_tabs");
        crumbs = layout.Get<Breadcrumbs>("crumbs");

        docTabs.OnSelected = index => Show(documents[index]);
        docTabs.OnClose = index => CloseDocument(documents[index]);
        docTabs.OnContextMenu = point =>
        {
            int index = docTabs.TabAt(point);
            if (index >= 0)
                ShowTabMenu(documents[index]);
        };

        crumbs.OnPressed = index =>
        {
            if (index < crumbActions.Count)
                crumbActions[index]();
        };
    }

    private void RebuildTabs()
    {
        tabButtons.Clear();

        // The plain welcome over the empty layout is no file anybody opened, so no tab says "Untitled". Only ever the one
        // document then, so the tabs left still line up with the documents
        if (plainShown && documents.Count == 1)
        {
            docTabs.Tabs.Clear();
            docTabs.Selected = -1;
            RetitleWindow();
            return;
        }

        while (docTabs.Tabs.Count > documents.Count)
            docTabs.Tabs.RemoveAt(docTabs.Tabs.Count - 1);
        while (docTabs.Tabs.Count < documents.Count)
            docTabs.Tabs.Add(new StripTab());

        for (int i = 0; i < documents.Count; i++)
        {
            HexDocument shown = documents[i];
            StripTab tab = docTabs.Tabs[i];
            string icon = IconOfDocument(shown);

            tab.Title = shown.IsText && shown.Path is { } path ? Path.GetFileName(path) : shown.Name;
            tab.Icon = icon;
            tab.IconColor = shown.IsText ? null : FolderColor;
            tab.Tooltip = shown.Path is { } where ? HexText.MiddleOf(Relative(where)) : Said("tabs.never_saved", ("name", shown.Name));
            tab.Modified = shown.Modified;
            tab.TitleColor = TabGitColor(shown);
            tab.Tag = shown;
        }

        docTabs.Selected = documents.IndexOf(document);
        RetitleWindow();
    }

    /// <summary>The title bar and the window's title said for what is open.</summary>
    private void RetitleWindow()
    {
        // The window says what is open, with a dot while it has changes, VS Code's way. The plain welcome is over the empty
        // layout nobody opened, so it's the welcome of the workspace there and not "layout"
        string name = plainShown ? WelcomeTitle()
            : document.IsText && document.Path is { } file ? Path.GetFileName(file) : document.Name;
        bool changed = !plainShown && (document.Modified || document.CodeEdited);
        string version = HexAbout.Version.ToString();
        ((TitleBar)top).Title = titleNote.Length > 0
            ? Said(changed ? "tabs.title_changed_note" : "tabs.title_note", ("name", name), ("version", version), ("note", titleNote))
            : Said(changed ? "tabs.title_changed" : "tabs.title", ("name", name), ("version", version));
        Engine.WindowManager.Title = Said("tabs.window_title", ("name", name), ("version", version));
    }

    /// <summary>What the title says while the welcome is up, Welcome and the workspace's name, or the folder's without one.</summary>
    private string WelcomeTitle()
    {
        if (!explorer.workspace.IsEmpty)
            return Said("tabs.welcome_of", ("workspace", explorer.workspace.Name));

        return Path.GetFileName(Path.TrimEndingDirectorySeparator(browseDirectory)) is { Length: > 0 } folder
            ? Said("tabs.welcome_of", ("workspace", folder))
            : Said("tabs.welcome");
    }

    private void ShowTabMenu(HexDocument tab)
    {
        var shown = new Menu("tab");
        shown.Add(Said("tabs.close"), () => CloseDocument(tab), "Ctrl+W", icon: "close");
        shown.Add(Said("tabs.close_others"), () => CloseAll(except: tab), icon: "clear-all");
        shown.Add(Said("tabs.close_saved"), () => CloseAll(onlySaved: true), icon: "chrome-close");
        shown.Add(Said("tabs.close_all"), () => CloseAll(), icon: "close-all");
        shown.AddSeparator();
        shown.Add(Said("tabs.open_to_side"), () => OpenToSide(tab), "Ctrl+\\", "split-horizontal").IsEnabled = () => tab.IsText && tab != document;
        shown.AddSeparator();
        shown.Add(Said("tabs.copy_path"), () => UIClipboard.Copy(tab.Path ?? tab.Name), "Shift+Alt+C", "link").IsEnabled = () => tab.Path is not null;
        shown.Add(Said("tabs.copy_relative_path"), () => UIClipboard.Copy(tab.Path is { } path ? Relative(path) : tab.Name), icon: "file-symlink-file").IsEnabled = () => tab.Path is not null;
        shown.Add(Said("tabs.reveal_in_explorer"), () => RevealInExplorer(tab), icon: "files").IsEnabled = () => tab.Path is not null;
        shown.AddSeparator();
        shown.Add(Said("file_properties.menu"), () => ShowPropertiesOf(tab.Path is { } path ? [path] : null), "Alt+Enter", "info");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>
    /// Closes documents, all of them, all but one, or only the ones with nothing to save. The ones with changes are
    /// asked about one at a time, the way closing each would.
    /// </summary>
    private void CloseAll(HexDocument? except = null, bool onlySaved = false)
    {
        foreach (HexDocument closing in documents.ToList())
        {
            if (closing == except || (onlySaved && (closing.Modified || closing.CodeEdited)))
                continue;

            // Nothing to lose goes at once, the first that has something asks and the rest wait for the next go
            if (closing.Modified || closing.CodeEdited)
            {
                CloseDocument(closing);
                return;
            }

            Show(closing);
            CloseNow();
        }

        if (except is not null && documents.Contains(except))
            Show(except);
    }

    /// <summary>Shows a file in the explorer, its folders opened up to it.</summary>
    private void RevealInExplorer(HexDocument tab)
    {
        if (tab.Path is not { } path)
            return;

        if (explorer.workspace.IsEmpty)
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;
        else
            explorer.OpenUpTo(path);

        left.Show(true);
        Side = HexSide.Explorer;
    }

    /// <summary>Every tick, the breadcrumbs, put together again when the file, the caret's line or the selection moved.</summary>
    private void UpdateCrumbs()
    {
        // Asked every tick, so a change to the outline is worked out once the typing stops
        if (!LayoutTools)
            KeptOutline();

        var now = (document, code.Document.Version, code.Document.Caret.Line, LayoutTools ? document.Selected : null, document.View);
        if (crumbsFor == now && crumbsPlain == plainShown)
            return;

        crumbsFor = now;
        crumbsPlain = plainShown;
        crumbParts.Clear();
        crumbActions.Clear();

        // Nothing to say where the plain welcome is, it's no file
        if (plainShown)
        {
            crumbs.Set(crumbParts);
            return;
        }

        // The folders, from the workspace's (or the repository's) down, the file with its icon
        if (document.Path is { } path)
        {
            string full = Path.GetFullPath(path);
            string root = WorkspaceRoots().FirstOrDefault(folder => full.StartsWith(Path.GetFullPath(folder), PlatformPaths.Comparison)) ?? Path.GetDirectoryName(full)!;
            string relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(root)) ?? root, Path.GetDirectoryName(full)!);

            HexDocument here = document;
            foreach (string folder in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (folder == ".") continue;
                crumbParts.Add(folder);
                crumbActions.Add(() => RevealInExplorer(here));
            }
        }

        string icon = IconOfDocument(document);
        crumbParts.Add($"$({icon}) {(document.IsText && document.Path is { } file ? Path.GetFileName(file) : document.Name)}");
        crumbActions.Add(() => { });

        if (LayoutTools)
        {
            // What the selected component is inside of, the outermost first
            var chain = new List<UIComponent>();
            for (UIComponent? at = document.Selected; at is not null && at != document.Module.Root; at = at.Parent)
                chain.Insert(0, at);

            foreach (UIComponent component in chain)
            {
                crumbParts.Add($"$(symbol-class) {document.NameOf(component)}");
                crumbActions.Add(() => designer.Select(component));
            }
        }
        else
        {
            // The lets and keys the caret is inside of, by the outline
            foreach (var entry in OutlineAt(KeptOutline(), code.Document.Caret.Line))
            {
                crumbParts.Add(entry.Depth == 0 ? $"$(symbol-variable) {entry.Name}" : $"$(symbol-field) {entry.Name}");
                crumbActions.Add(() => code.GoTo(entry.Line, entry.Column));
            }
        }

        crumbs.Set(crumbParts);
    }

    /// <summary>The entries of the outline a line is inside of, the outermost first, by how deep each is.</summary>
    private static List<OutlineEntry> OutlineAt(List<OutlineEntry> outline, int line)
    {
        var chain = new List<OutlineEntry>();
        foreach (OutlineEntry entry in outline)
        {
            if (entry.Line > line)
                break;

            while (chain.Count > 0 && chain[^1].Depth >= entry.Depth)
                chain.RemoveAt(chain.Count - 1);
            chain.Add(entry);
        }

        return chain;
    }

    /// <summary>
    /// A document's icon in its tab and its breadcrumb, the one the explorer gives its file, so a file has the one icon
    /// wherever it shows. A page for everything in the tabs and a C# file with a class's icon in the explorer didn't match.
    /// </summary>
    private string IconOfDocument(HexDocument shown) =>
        shown.IsText && shown.Path is { } path ? HexExplorer.FileIcon(path) : KindOf(shown).Icon;
}
