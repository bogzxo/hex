using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The tabs over the editor (a native TabStrip) and the breadcrumbs under them, where the file is and, in code, where
// the caret is in it, or in the designer what the selected component is inside of.
internal sealed partial class DawnScene
{
    private readonly List<Button> tabButtons = [];

    private TabStrip docTabs = null!;
    private Button allTabs = null!;

    private Breadcrumbs crumbs = null!;

    // The breadcrumbs' pieces and what a click on each does
    private readonly List<string> crumbParts = [];
    private readonly List<Action> crumbActions = [];
    private (HexDocument? Document, int Version, int Line, UIComponent? Selected, HexView View, string? Path) crumbsFor;

    // The folder crumbs of the file on screen, which only its path changes, kept rather than worked out at each move
    private (HexDocument? Of, string? Path) crumbFoldersFor;
    private readonly List<string> crumbFolders = [];
    private bool crumbsPlain;

    private void BuildTabs()
    {
        docTabs = layout.Get<TabStrip>("doc_tabs");
        docTabs.HideClippedTabs = true;
        allTabs = layout.Get<Button>("all_tabs");
        allTabs.OnPressed = () =>
        {
            var choices = new List<UIChoice>(documents.Count);
            foreach (HexDocument open in documents)
            {
                HexDocument picked = open;
                choices.Add(new UIChoice(open.Name, () => Show(picked))
                {
                    Id = open.Path ?? open.Name,
                    Detail = open.Path is { } path ? Relative(path) : Said("tabs.never_saved", ("name", open.Name)),
                    Icon = IconOfDocument(open)
                });
            }
            ChooseFrom(Said("tabs.all"), string.Empty, choices);
        };
        crumbs = layout.Get<Breadcrumbs>("crumbs");

        docTabs.OnSelected = index => Show(documents[index]);
        docTabs.OnClose = index => CloseDocument(documents[index]);
        docTabs.OnUnpin = index => PinTab(documents[index], false);
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
        if (linkEditorNotices is not null)
            MarkLinkEditorNotices();
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

            // Another machine's file says whose it is (DawnScene.LinkFiles)
            tab.Title = shown.IsText && shown.Path is { } path ? Path.GetFileName(path) : shown.Name;
            tab.Icon = icon;
            tab.IconColor = shown.IsText ? null : FolderColor;
            tab.Tooltip = shown.Path ?? Said("tabs.never_saved", ("name", shown.Name));
            tab.Modified = shown.Modified;
            tab.Pinned = shown.Pinned;
            tab.TitleColor = TabGitColor(shown);
            tab.Tag = shown;
            if (linkItem is not null || linkServices is not null)
                DecorateLinkedTab(shown, tab);
        }

        docTabs.Selected = documents.IndexOf(document);
        RetitleWindow();
    }

    private void DecorateLinkedTab(HexDocument shown, StripTab tab)
    {
        tab.Title = LinkRemoteTitle(shown) ?? tab.Title;
        tab.Tooltip = LinkLiveTooltip(shown) ?? LinkRemoteTooltip(shown) ?? tab.Tooltip;
        tab.PresenceColors = LinkLiveTabColors(shown);
        LinkClaimTab(shown, tab);
        LinkHoldTabMark(shown, tab);
    }

    /// <summary>The title bar and the window's title said for what is open.</summary>
    private void RetitleWindow()
    {
        // The window says what is open, with a dot while it has changes, VS Code's way. The plain welcome is over the empty
        // layout nobody opened, so it's the welcome of the workspace there and not "layout"
        string name = plainShown ? WelcomeTitle()
            : document.IsText && document.Path is { } file ? Path.GetFileName(file) : document.Name;
        bool changed = !plainShown && (document.Modified || document.CodeEdited);
        string version = HexAbout.Label;
        // What's open, and after it, smaller and quieter, whose window it is. Hex3, what the release is called, which
        // build of it this is, and the line an extension put there (Hello Dolly's) last so it's the first cut short
        ((TitleBar)top).Title = Said(changed ? "tabs.title_changed" : "tabs.title", ("name", name));
        string build = Hex.Shell.HexVersion.Build is { Length: > 0 } number ? Said("tabs.build", ("number", number)) : string.Empty;
        ((TitleBar)top).Caption = titleNote.Length > 0
            ? Said("tabs.caption_note", ("codename", Hex.Shell.HexVersion.Name), ("build", build), ("note", titleNote)).Trim()
            : Said("tabs.caption", ("codename", Hex.Shell.HexVersion.Name), ("build", build)).Trim();
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
        shown.Add(Said("tabs.close_others"), () => CloseAll(except: tab, keepPinned: true), icon: "clear-all");
        shown.Add(Said("tabs.close_saved"), () => CloseAll(onlySaved: true, keepPinned: true), icon: "chrome-close");
        shown.Add(Said("tabs.close_all"), () => CloseAll(keepPinned: true), icon: "close-all");
        shown.AddSeparator();
        shown.Add(Said(tab.Pinned ? "tabs.unpin" : "tabs.pin"), () => PinTab(tab, !tab.Pinned), "Ctrl+K Shift+Enter", tab.Pinned ? "pinned" : "pin");
        shown.Add(Said("tabs.open_to_side"), () => OpenToSide(tab), "Ctrl+\\", "split-horizontal").IsEnabled = () => tab.IsText && tab != document;
        shown.AddSeparator();
        shown.Add(Said("tabs.copy_path"), () => UIClipboard.Copy(tab.Path ?? tab.Name), "Shift+Alt+C", "link").IsEnabled = () => tab.Path is not null;
        shown.Add(Said("tabs.copy_relative_path"), () => UIClipboard.Copy(tab.Path is { } path ? Relative(path) : tab.Name), icon: "file-symlink-file").IsEnabled = () => tab.Path is not null;
        shown.Add(Said("tabs.reveal_in_explorer"), () => RevealInExplorer(tab), icon: "files").IsEnabled = () => tab.Path is not null;
        AddLinkClaimMenu(shown, tab.Path);
        AddLinkHoldMenu(shown, tab.Path);
        shown.AddSeparator();
        shown.Add(Said("file_properties.menu"), () => ShowPropertiesOf(tab.Path is { } path ? [path] : null), "Alt+Enter", "info");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>
    /// Closes documents, all of them, all but one, or only the ones with nothing to save. The ones with changes are
    /// asked about one at a time, the way closing each would. Asked for by somebody (the tab's menu, the commands) the
    /// pinned ones stay, the way VS Code keeps them.
    /// </summary>
    private void CloseAll(HexDocument? except = null, bool onlySaved = false, bool keepPinned = false)
    {
        foreach (HexDocument closing in documents.ToList())
        {
            if (closing == except || (onlySaved && (closing.Modified || closing.CodeEdited)) || (keepPinned && closing.Pinned))
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

    /// <summary>
    /// Pins a tab or unpins it, Ctrl+K Shift+Enter or the tab's menu. A pinned one goes after the others already pinned,
    /// at the front, and one unpinned goes after the last of them, so the pinned are always together at the start.
    /// </summary>
    private void PinTab(HexDocument tab, bool pin)
    {
        if (tab.Pinned == pin || !documents.Remove(tab))
            return;

        tab.Pinned = pin;
        int at = 0;
        while (at < documents.Count && documents[at].Pinned)
            at++;
        documents.Insert(at, tab);
        RebuildTabs();
    }

    /// <summary>Shows a file in the explorer, its folders opened up to it.</summary>
    private void RevealInExplorer(HexDocument tab)
    {
        if (tab.Path is { } path)
            RevealInExplorer(path);
    }

    /// <summary>Shows a file in the explorer, its folders opened up to it.</summary>
    private void RevealInExplorer(string path)
    {
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

        // Code's crumbs change with the caret's line and the outline, which says so itself when it's worked out again
        // (OutlineCache), so a key typed on the same line makes nothing. A layout's go by its text, the names in it
        var now = (document, LayoutTools ? code.Document.Version : 0, code.Document.Caret.Line, LayoutTools ? document.Selected : null, document.View, document.Path);
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
            if (crumbFoldersFor != (document, path))
            {
                crumbFoldersFor = (document, path);
                crumbFolders.Clear();
                string full = Path.GetFullPath(path);
                string root = WorkspaceRoots().FirstOrDefault(folder => full.StartsWith(Path.GetFullPath(folder), PlatformPaths.Comparison)) ?? Path.GetDirectoryName(full)!;
                string relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(root)) ?? root, Path.GetDirectoryName(full)!);
                foreach (string folder in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    if (folder != ".")
                        crumbFolders.Add(folder);
            }

            HexDocument here = document;
            Action reveal = () => RevealInExplorer(here);
            foreach (string folder in crumbFolders)
            {
                crumbParts.Add(folder);
                crumbActions.Add(reveal);
            }
        }

        string icon = IconOfDocument(document);
        crumbParts.Add($"$({icon}) {(document.IsText && document.Path is { } file ? Path.GetFileName(file) : document.Name)}");
        crumbActions.Add(() => { });

        if (LayoutTools)
        {
            // What the selected component is inside of, the outermost first
            List<UIComponent> chain = [];
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
        List<OutlineEntry> chain = [];
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
