using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The tabs over the editor (a native TabStrip) and the breadcrumbs under them, where the file is and, in code, where
// the caret is in it, or in the designer what the selected component is inside of.
internal sealed partial class HexScene
{
    private readonly List<Button> tabButtons = [];

    private TabStrip docTabs = null!;

    private Breadcrumbs crumbs = null!;

    // The breadcrumbs' pieces and what a click on each does
    private readonly List<string> crumbParts = [];
    private readonly List<Action> crumbActions = [];
    private (HexDocument? Document, int Version, int Line, UIComponent? Selected, HexView View) crumbsFor;

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

        while (docTabs.Tabs.Count > documents.Count)
            docTabs.Tabs.RemoveAt(docTabs.Tabs.Count - 1);
        while (docTabs.Tabs.Count < documents.Count)
            docTabs.Tabs.Add(new StripTab());

        for (int i = 0; i < documents.Count; i++)
        {
            HexDocument shown = documents[i];
            StripTab tab = docTabs.Tabs[i];
            var (icon, _) = KindOf(shown);

            tab.Title = shown.IsText && shown.Path is { } path ? Path.GetFileName(path) : shown.Name;
            tab.Icon = icon;
            tab.IconColor = shown.IsText ? null : FolderColor;
            tab.Tooltip = shown.Path ?? $"{shown.Name}, never saved";
            tab.Modified = shown.Modified;
            tab.Tag = shown;
        }

        docTabs.Selected = documents.IndexOf(document);

        // The window says what is open, with a dot while it has changes, VS Code's way
        string name = document.IsText && document.Path is { } file ? Path.GetFileName(file) : document.Name;
        ((TitleBar)top).Title = $"{name}{(document.Modified || document.CodeEdited ? " *" : string.Empty)}  -  Hex {HexAbout.Version}{(titleNote.Length > 0 ? $" ({titleNote})" : string.Empty)}";
        Engine.WindowManager.Title = $"{name} - Hex {HexAbout.Version}";
    }

    private void ShowTabMenu(HexDocument tab)
    {
        var shown = new Menu("tab");
        shown.Add("Close", () => CloseDocument(tab), "Ctrl+W", icon: "close");
        shown.Add("Close the others", () => CloseAll(except: tab), icon: "close-all");
        shown.Add("Close the saved ones", () => CloseAll(onlySaved: true));
        shown.Add("Close all", () => CloseAll(), icon: "close-all");
        shown.AddSeparator();
        shown.Add("Open to the side", () => OpenToSide(tab), "Ctrl+\\", "split-horizontal").IsEnabled = () => tab.IsText && tab != document;
        shown.AddSeparator();
        shown.Add("Copy the path", () => UIClipboard.Copy(tab.Path ?? tab.Name), "Shift+Alt+C", "copy").IsEnabled = () => tab.Path is not null;
        shown.Add("Copy the path in the workspace", () => UIClipboard.Copy(tab.Path is { } path ? Relative(path) : tab.Name)).IsEnabled = () => tab.Path is not null;
        shown.Add("Reveal in the explorer", () => RevealInExplorer(tab), icon: "files").IsEnabled = () => tab.Path is not null;
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

        if (workspace.IsEmpty)
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;
        else
            OpenUpTo(path);

        left.Show(true);
        Side = HexSide.Explorer;
    }

    /// <summary>Every tick, the breadcrumbs, put together again when the file, the caret's line or the selection moved.</summary>
    private void UpdateCrumbs()
    {
        var now = (document, code.Document.Version, code.Document.Caret.Line, LayoutTools ? document.Selected : null, document.View);
        if (crumbsFor == now)
            return;

        crumbsFor = now;
        crumbParts.Clear();
        crumbActions.Clear();

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

        var (icon, _) = KindOf(document);
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
            foreach (var entry in OutlineAt(code.Text, code.Document.Caret.Line, document?.Path))
            {
                crumbParts.Add(entry.Depth == 0 ? $"$(symbol-variable) {entry.Name}" : $"$(symbol-field) {entry.Name}");
                crumbActions.Add(() => code.GoTo(entry.Line, entry.Column));
            }
        }

        crumbs.Set(crumbParts);
    }

    /// <summary>The entries of the outline a line is inside of, the outermost first, by how deep each is.</summary>
    private static List<OutlineEntry> OutlineAt(string text, int line, string? path = null)
    {
        var chain = new List<OutlineEntry>();
        foreach (OutlineEntry entry in HexOutline.Outline(text, path))
        {
            if (entry.Line > line)
                break;

            while (chain.Count > 0 && chain[^1].Depth >= entry.Depth)
                chain.RemoveAt(chain.Count - 1);
            chain.Add(entry);
        }

        return chain;
    }
}
