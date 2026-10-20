using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Selecting more than one thing, groups, copies, what goes on top of what, and the right click menu for all of it.
internal sealed partial class HexDesigner
{
    // The menu a right click opened last, for the self-test to find its items

    // Ctrl or shift held, as far as clicks go. The self-test can't hold keys, so it says so here instead
    internal bool fakeModifier;

    /// <summary>Whether a click adds to what is selected (ctrl, command on a Mac, or shift held) rather than replacing it.</summary>
    private bool Modifier()
    {
        var keyboard = context.Keyboard;
        return fakeModifier
            || keyboard.Primary
            || keyboard.IsDown(Key.ShiftLeft) || keyboard.IsDown(Key.ShiftRight);
    }

    /// <summary>
    /// Helper method to have the UI mark out everything that's selected, and open the pages of any tabs the selection
    /// is hidden away on so it can be seen.
    /// </summary>
    internal void MarkSelection()
    {
        stage.Highlighted = document.Selected;
        stage.Highlights = [.. document.Selection];

        foreach (var component in document.Selection)
            RevealTabs(component);

        treeDirty = inspectorDirty = true;
        treeReveal = true;
    }

    // The tree is to scroll to what was selected, once its rows have been laid out
    internal bool treeReveal;

    /// <summary>
    /// Helper method to scroll the tree so the row of what is selected is in sight, whichever way it was selected (the
    /// canvas, the arrange menu, an undo). The rows are made again when the selection changes and have nowhere to be
    /// until the UI has laid them out at the end of the tick, so it waits for that and does it on the tick after.
    /// </summary>
    internal void RevealInTree()
    {
        if (!treeReveal || treeDirty)
            return;

        if (document.Selected is { } selected && treeRows.TryGetValue(selected, out var row) && row.Bounds.IsEmpty)
            return;

        treeReveal = false;

        if (document.Selected is { } shown && treeRows.TryGetValue(shown, out var found))
            treeScroll.ScrollIntoView(found);
    }

    private static void RevealTabs(UIComponent component)
    {
        for (UIComponent? at = component; at?.Parent is { } parent; at = parent)
        {
            if (parent is TabPanel tabs)
                tabs.Reveal(at);
        }
    }

    /// <summary>Selects a component, on top of the rest with ctrl or shift held, on its own without.</summary>
    private void Pick(UIComponent? component)
    {
        if (component is not null && Modifier())
        {
            document.ToggleSelected(component);
            MarkSelection();
            return;
        }

        Select(component);
    }

    /// <summary>
    /// Helper method to work out what a click on a component picks. A component inside a group picks the group (that's
    /// what a group is for); clicking again goes a level in, the way every drawing program does it. Alt goes straight
    /// to what's under the pointer.
    /// </summary>
    private UIComponent? ResolveClick(UIComponent? hit)
    {
        var keyboard = context.Keyboard;
        if (hit is null || keyboard.IsDown(Key.AltLeft) || keyboard.IsDown(Key.AltRight))
            return hit;

        // The groups it's in, the outermost first
        var groups = new List<UIComponent>();
        for (UIComponent? at = hit.Parent; at is not null && at != document.Module.Root; at = at.Parent)
        {
            if (at is Group)
                groups.Insert(0, at);
        }

        if (groups.Count == 0)
            return hit;

        // A group that's selected (or has the selection inside of it) is open: the click goes a level in
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (document.Selected is { } selected && IsInside(selected, groups[i]))
                return i + 1 < groups.Count ? groups[i + 1] : hit;
        }

        return groups[0];
    }

    internal static bool IsInside(UIComponent component, UIComponent container)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at == container)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to find the tab of a tabs panel under a point of the layout, so a click on one in the canvas opens
    /// its page (the canvas isn't clickable the way a running UI is, it's being edited).
    /// </summary>
    private static (TabPanel Tabs, int Index)? TabUnder(UIComponent? hit, Vector2 inLayout)
    {
        for (UIComponent? at = hit; at is not null; at = at.Parent)
        {
            if (at is TabPanel tabs && tabs.TabAt(inLayout) is >= 0 and var index)
                return (tabs, index);
        }

        return null;
    }

    /* Doing things to all of the selection */

    private void DeleteAll()
    {
        var gone = document.TopSelection();
        if (gone.Count == 0)
            return;

        foreach (var component in gone)
            document.Remove(component);

        Select(null);
        treeDirty = codeDirty = true;
        context.Notices.Say(gone.Count == 1 ? "deleted it" : $"deleted {gone.Count} things");
    }

    private void GroupSelected()
    {
        if (document.Group(document.TopSelection(), out string problem) is not { } group)
        {
            context.Notices.Say(problem, error: true);
            return;
        }

        Select(group);
        treeDirty = codeDirty = true;
        context.Notices.Say($"grouped {group.Children.Count} things as {document.NameOf(group)}, click again to get at what's in it");
    }

    private void UngroupSelected()
    {
        var groups = document.TopSelection().OfType<Group>().ToList();
        if (groups.Count == 0)
        {
            context.Notices.Say("there's no group selected to take apart", error: true);
            return;
        }

        var freed = new List<UIComponent>();
        foreach (var group in groups)
            freed.AddRange(document.Ungroup(group));

        Select(null);
        foreach (var component in freed)
            document.ToggleSelected(component);

        MarkSelection();
        treeDirty = codeDirty = true;
        context.Notices.Say($"took {groups.Count} group{(groups.Count == 1 ? string.Empty : "s")} apart");
    }

    // What a container can be changed into, the kind, whether a stack runs across, and what the menu calls it
    private static readonly (string Kind, bool Across, string Label)[] Containers =
    [
        ("stack", false, "Stack, down"),
        ("stack", true, "Stack, across"),
        ("grid", false, "Grid"),
        ("panel", false, "Panel"),
        ("scroll", false, "Scroll"),
        ("collapsible", false, "Collapsible"),
        ("tabs", false, "Tabs, a page each"),
        ("group", false, "Group")
    ];

    /// <summary>Whether the one selected is a container that is already this kind, which isn't offered as a change.</summary>
    private bool IsAlready(string kind, bool across) =>
        document.Selected is { } selected && UIModule.KindOf(selected) == kind
        && (selected is not StackPanel stack || selected is Collapsible || (stack.Direction == UIDirection.Horizontal) == across);

    /// <summary>A menu of what the container selected can be changed into, for its right click and the Layout menu.</summary>
    private void AddConvertMenu(Menu into)
    {
        foreach (var (kind, across, label) in Containers)
        {
            into.Add(label, () => ConvertSelected(kind, across), icon: KindIcon(kind)).IsEnabled =
                () => document.Selected is { } selected && HexDocument.IsContainer(selected) && !IsAlready(kind, across);
        }
    }

    /// <summary>Changes the container selected into another kind of container, everything in it with it.</summary>
    internal void ConvertSelected(string kind, bool across)
    {
        if (document.Selected is not { } selected)
            return;

        string was = UIModule.KindOf(selected) ?? "container";
        if (document.Convert(selected, kind, across, out string problem) is not { } made)
        {
            context.Notices.Say(problem, error: true);
            return;
        }

        Select(made);
        treeDirty = codeDirty = inspectorDirty = true;
        context.Notices.Say($"{document.NameOf(made)} is a {kind}{(kind == "stack" ? (across ? " across" : " down") : string.Empty)} now, it was a {was}, Ctrl+Z puts it back");
    }

    private void DuplicateSelected()
    {
        var originals = document.TopSelection();
        if (originals.Count == 0)
            return;

        var copies = new List<UIComponent>();
        foreach (var original in originals)
        {
            if (document.Duplicate(original, out string problem) is { } copy)
                copies.Add(copy);
            else
                context.Notices.Say($"couldn't copy {document.NameOf(original)}: {problem}", error: true);
        }

        if (copies.Count == 0)
            return;

        Select(null);
        foreach (var copy in copies)
            document.ToggleSelected(copy);

        MarkSelection();
        treeDirty = codeDirty = true;
        context.Notices.Say(copies.Count == 1 ? $"copied it as {document.NameOf(copies[0])}" : $"copied {copies.Count} things");
    }

    private void ReorderSelected(HexDocument.Order order)
    {
        var moving = document.TopSelection();

        // Going to the front the last one goes last, to the back the first one goes first: they keep their order
        if (order is HexDocument.Order.Back or HexDocument.Order.Forward)
            moving.Reverse();

        foreach (var component in moving)
            document.Reorder(component, order);

        treeDirty = codeDirty = true;
    }

    private void SelectAll()
    {
        // Everything next to what's selected, or everything at the top of the layout
        UIComponent container = document.Selected?.Parent ?? document.Module.Root;

        Select(null);
        foreach (var component in container.Children)
        {
            if (document.Walk().Any(pair => pair.Component == component))
                document.ToggleSelected(component);
        }

        MarkSelection();
        context.Notices.Say($"selected {document.Selection.Count} things");
    }

    private void SelectParent()
    {
        if (document.Selected?.Parent is { } parent && parent != document.Module.Root)
            Select(parent);
    }

    private void AddPage()
    {
        if (document.Selected is not TabPanel tabs)
            return;

        if (document.Add("stack", tabs) is { } page)
        {
            tabs.Tabs = [.. tabs.Tabs, $"Page {tabs.Children.Count}"];
            tabs.Selected = tabs.Children.Count - 1;
            Select(page);
            treeDirty = codeDirty = true;
        }
    }

    /* The right click menu */

    /// <summary>The file an include names, opened to edit, what's inside the include isn't edited here.</summary>
    private void OpenIncluded()
    {
        if (document.Selected is not Include { File.Length: > 0 } include)
            return;

        string directory = document.Path is { } path ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? string.Empty : document.FallbackDirectory;
        string file = System.IO.Path.Combine(directory, include.File);
        if (File.Exists(file))
            context.Documents.Open(file);
        else
            context.Notices.Say($"there's no {include.File} next to the layout", error: true);
    }

    private Menu ComponentMenu()
    {
        var shown = new Menu("component");
        Func<bool> any = () => document.Selection.Count > 0;

        shown.Add("Cut", CutSelected, "Ctrl+X").IsEnabled = any;
        shown.Add("Copy", CopySelected, "Ctrl+C", "copy").IsEnabled = any;
        shown.Add("Paste", Paste, "Ctrl+V").IsEnabled = () => clipboard.Length > 0;
        shown.Add("Duplicate", DuplicateSelected, "Ctrl+D", "copy").IsEnabled = any;
        shown.Add("Delete", DeleteAll, "Del", "trash").IsEnabled = any;
        shown.AddSeparator();

        // What goes inside of the one selected, a menu of the kinds
        Menu inside = shown.AddMenu("Add Inside", "add");
        foreach (string kind in Palette)
            inside.Add(kind.Replace('_', ' '), () => AddComponentOf(kind), icon: KindIcon(kind));

        AddMoveMenu(shown.AddMenu("Move", "arrow-swap"), any);
        AddArrangeMenu(shown.AddMenu("Arrange", "layers"), any);
        AddAlignMenu(shown.AddMenu("Align", "symbol-ruler"), any);
        shown.AddSeparator();
        shown.Add("Open Included File", OpenIncluded, icon: "go-to-file").IsVisible = () => document.Selected is Include;
        shown.Add("Group", GroupSelected, "Ctrl+G", "group-by-ref-type").IsEnabled = any;
        shown.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);

        // A container, a group above all, can be had as any other kind of container, what is in it and all
        if (document.Selected is { } container && HexDocument.IsContainer(container))
            AddConvertMenu(shown.AddMenu("Change into", "arrow-swap"));
        shown.AddSeparator();
        shown.Add("Select parent", SelectParent, icon: "arrow-up").IsEnabled = () => document.Selected?.Parent is { } parent && parent != document.Module.Root;
        shown.Add("Select all", SelectAll, "Ctrl+A");
        shown.Add("Its code", ShowSelectedCode, icon: "code").IsEnabled = any;

        if (document.Selected is TabPanel)
            shown.Add("Add a page", AddPage, icon: "add");

        return shown;
    }

    /// <summary>Helper method to show the menu of what's selected where the pointer is, in the editor's own UI.</summary>
    private void ShowComponentMenu(Vector2 world) => context.Windows.MenuAt(ComponentMenu(), world);

    /// <summary>
    /// Helper method for a right click in the editor that nothing in its UI wanted: on the canvas it picks what's under the
    /// pointer (unless that's selected already, so a menu can be had for all of the selection) and shows the menu.
    /// </summary>
    internal void OnContextRequested(UIComponent? over, Vector2 world)
    {
        if (!PointerOnCanvas(world))
            return;

        var hit = ResolveClick(document.OwnerOf(document.Module.FindAt(world)));
        if (hit is null || !document.IsSelected(hit))
            Pick(hit);

        ShowComponentMenu(world);
    }

    /// <summary>
    /// The designer's commands, the clipboard of components, its zoom, the Layout menu and the keys that move what is
    /// selected about, which keep out of the way while something is being typed.
    /// </summary>
    internal void AddDesignerCommands()
    {
        Func<bool> design = () => LayoutTools;
        Func<bool> some = () => LayoutTools && document.Selected is not null;

        // The clipboard, which a text box keeps for itself while it is being typed into
        commands.Add("Edit/clipboard", "Cut", CutSelected, "Ctrl+X", use: UIKeyUse.Bound).IsVisible = design;
        commands.Add("Edit/clipboard", "Copy", CopySelected, "Ctrl+C", "copy", UIKeyUse.Bound).IsVisible = design;
        commands.Add("Edit/clipboard", "Paste", Paste, "Ctrl+V", use: UIKeyUse.Bound).IsVisible = design;
        commands.Add("Edit/clipboard", "Duplicate", DuplicateSelected, "Ctrl+D", use: UIKeyUse.Bound).IsVisible = design;
        commands.Add("Edit/clipboard", "Delete", DeleteAll, "Del", "trash", UIKeyUse.Bound).IsVisible = design;

        commands.Add("View/zoom", "Zoom In", () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), "Ctrl+=", "zoom-in").IsVisible = design;
        commands.Add("View/zoom", "Zoom Out", () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), "Ctrl+-", "zoom-out").IsVisible = design;
        commands.Add("View/zoom", "Zoom to Fit", ZoomToFit, "Ctrl+0", "screen-full").IsVisible = design;
        commands.Add("View/zoom", "Actual Size", ZoomToActualSize, "Ctrl+1", use: UIKeyUse.Bound).IsVisible = design;

        commands.Add("Run/run", "Play Entrances", PlayIntros, "F5", "play").IsVisible = design;

        commands.ShowMenuWhen("Layout", design);
        Menu add = commands.AddMenu("Layout/add", "Add", "add");
        foreach (string kind in Palette)
            add.Add(kind.Replace('_', ' '), () => AddComponentOf(kind), icon: KindIcon(kind));
        commands.Add("Layout/group", "Group", GroupSelected, "Ctrl+G", "group-by-ref-type", UIKeyUse.Bound).IsEnabled = some;
        commands.Add("Layout/group", "Ungroup", UngroupSelected, "Ctrl+Shift+G", use: UIKeyUse.Bound).IsEnabled = () => document.Selection.Any(component => component is Group);
        AddConvertMenu(commands.AddMenu("Layout/group", "Convert To", "arrow-swap"));
        AddMoveMenu(commands.AddMenu("Layout/move", "Move", "arrow-swap"), some);
        AddArrangeMenu(commands.AddMenu("Layout/move", "Arrange", "layers"), some);
        AddAlignMenu(commands.AddMenu("Layout/move", "Align", "symbol-ruler"), some);
        commands.Add("Layout/select", "Select All", SelectAll, "Ctrl+A", use: UIKeyUse.Bound);
        commands.Add("Layout/select", "Deselect All", () => Select(null), "Esc").IsEnabled = some;
        commands.Add("Layout/select", "Show All Layers", ShowAllLayers, icon: "eye").IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        commands.Add("Layout/select", "Show Code", ShowSelectedCode, icon: "code").IsEnabled = some;
        FillScreenMenu(commands.AddMenu("Layout/screen", "Screen", "device-desktop"));

        // Escape puts away the little window on top first (the settings, a page of help), then what is selected
        commands.Bind(Key.Escape, () => { if (!window.CloseTopWindow()) Select(null); });
        commands.Bind(Key.RightBracket, () => ReorderSelected(HexDocument.Order.Front), control: true);
        commands.Bind(Key.LeftBracket, () => ReorderSelected(HexDocument.Order.Back), control: true);
        commands.Bind(Key.RightBracket, () => ReorderSelected(HexDocument.Order.Forward));
        commands.Bind(Key.LeftBracket, () => ReorderSelected(HexDocument.Order.Backward));

        // With control the arrows move it about the tree instead of about the canvas
        commands.Bind(Key.Up, () => MoveSelected(-1), control: true);
        commands.Bind(Key.Down, () => MoveSelected(1), control: true);
        commands.Bind(Key.Left, () => MoveSelectedAcross(inwards: false), control: true);
        commands.Bind(Key.Right, () => MoveSelectedAcross(inwards: true), control: true);

        // The arrows move whatever is selected a pixel at a time, for lining things up by eye
        commands.Bind(Key.Left, () => Nudge(-1, 0));
        commands.Bind(Key.Right, () => Nudge(1, 0));
        commands.Bind(Key.Up, () => Nudge(0, 1));
        commands.Bind(Key.Down, () => Nudge(0, -1));
    }

    /// <summary>The ways to move what is selected about the tree, for the Layout menu and the menu of a component.</summary>
    private void AddMoveMenu(Menu into, Func<bool> some)
    {
        into.Add("Move Up", () => MoveSelected(-1), "Ctrl+Up", "arrow-up").IsEnabled = some;
        into.Add("Move Down", () => MoveSelected(1), "Ctrl+Down", "arrow-down").IsEnabled = some;
        into.Add("Move Out", () => MoveSelectedAcross(inwards: false), "Ctrl+Left", "arrow-left").IsEnabled = some;
        into.Add("Move In", () => MoveSelectedAcross(inwards: true), "Ctrl+Right", "arrow-right").IsEnabled = some;
    }

    private void AddArrangeMenu(Menu into, Func<bool> some)
    {
        into.Add("Bring to Front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = some;
        into.Add("Bring Forward", () => ReorderSelected(HexDocument.Order.Forward), "]").IsEnabled = some;
        into.Add("Send Backward", () => ReorderSelected(HexDocument.Order.Backward), "[").IsEnabled = some;
        into.Add("Send to Back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = some;
    }

    /// <summary>Lining up is against each other with several things selected, against the container with one.</summary>
    private void AddAlignMenu(Menu into, Func<bool> some)
    {
        into.Add("Align Left", () => Align(Edge.Left)).IsEnabled = some;
        into.Add("Align Centre", () => Align(Edge.Center)).IsEnabled = some;
        into.Add("Align Right", () => Align(Edge.Right)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Align Top", () => Align(Edge.Top)).IsEnabled = some;
        into.Add("Align Middle", () => Align(Edge.Middle)).IsEnabled = some;
        into.Add("Align Bottom", () => Align(Edge.Bottom)).IsEnabled = some;
        into.AddSeparator();
        into.Add("Distribute Horizontally", () => Distribute(horizontally: true)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
        into.Add("Distribute Vertically", () => Distribute(horizontally: false)).IsEnabled = () => LayoutTools && document.Selection.Count >= 3;
    }

    /// <summary>The code of the layout, with the caret on the let of what is selected.</summary>
    private void ShowSelectedCode()
    {
        if (document.Selected is { } selected)
            window.ShowDeclaration(document.NameOf(selected));
    }
}
