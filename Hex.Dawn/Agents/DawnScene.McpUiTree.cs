using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Dawn;

/// <summary>Something on screen as an agent sees it, what it is, what it says, where it is in window pixels, and how to point at it.</summary>
internal sealed record HexScreenNode(string Id, string Kind, string Text, string? Name, Vector4 Box, int Depth, bool Enabled, bool Focused, bool Interactive, Func<Vector2?> Target)
{
    /// <summary>The middle of it in window pixels.</summary>
    public Vector2 Center => new(Box.X + Box.Z * 0.5f, Box.Y + Box.W * 0.5f);
}

// What's on screen as a list an agent can read and point at. The components of the editor's own UI (and of the layouts
// on the canvas, when asked), and what some of them draw that isn't a component of its own, the rows of a tree, the
// items of the status bar, the menu bar's titles and the items of an open menu. A component keeps its id for as long as
// it lives (a weak table, nothing kept alive by it), a row or an item is known by where it is until the next look.
internal sealed partial class DawnScene
{
    private readonly ConditionalWeakTable<object, StrongBox<int>> mcpIds = new();
    private readonly Dictionary<string, Func<Vector2?>> mcpTargets = new(StringComparer.Ordinal);
    private int mcpNextId;

    // $(icon) in a label, which says nothing to an agent looking for the words
    [GeneratedRegex(@"\$\([a-z0-9-]+\)\s*")]
    private static partial Regex IconPattern();

    private string McpIdOf(object of, char prefix)
    {
        StrongBox<int> id = mcpIds.GetValue(of, _ => new StrongBox<int>(++mcpNextId));
        return $"{prefix}{id.Value}";
    }

    /// <summary>A rectangle of a module in window pixels, x and y its top left, the way a screenshot has it.</summary>
    private Vector4 McpBox(UIModule module, UIRect bounds)
    {
        Vector2 a = camera.WorldToScreen(module.ToWorld(bounds.Min)), b = camera.WorldToScreen(module.ToWorld(bounds.Max));
        Vector2 min = Vector2.Min(a, b), max = Vector2.Max(a, b);
        return new Vector4(min, max.X - min.X, max.Y - min.Y);
    }

    /// <summary>A point in window pixels as the compositors' world has it, where the agent's pointer goes.</summary>
    private Vector2 McpWorld(Vector2 window) => camera.ScreenToWorld(window);

    /// <summary>
    /// What a component says, its text, label or what's typed in it, its icons left out and its @ keys put into the words
    /// they show as. An agent looking for "Properties" found "@properties.title" and nothing to click.
    /// </summary>
    private string McpTextOf(UIComponent component) => Plain(compositor.Resolve(component switch
    {
        Label shown => shown.Text,
        Button pressed => pressed.Label,
        ToggleButton toggled => toggled.Label,
        TextBox box => box.Text,
        CodeEditor code => $"({code.Document.LineCount} lines of code)",
        _ => string.Empty
    }));

    private static string Plain(string text) => IconPattern().Replace(text, string.Empty).Replace('\n', ' ').Trim();

    /// <summary>
    /// Everything on screen, top down in the order it's laid out, the editor's own modules then (with layouts) the
    /// canvas's. Only what's showing unless hidden is asked for, every id it hands out pointable at afterwards.
    /// </summary>
    private List<HexScreenNode> McpScreen(bool layouts = false, bool hidden = false)
    {
        mcpTargets.Clear();
        var names = new Dictionary<UIComponent, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, part) in layout.Parts)
            names.TryAdd(part, name);

        var nodes = new List<HexScreenNode>();
        foreach (UIModule module in compositor.Modules)
        {
            if (module.Enabled || hidden)
                McpWalk(module, module.Root, 0, names, nodes, hidden);
        }

        if (layouts)
        {
            foreach (UIModule module in stage.Modules)
            {
                if (module != backdrop && (module.Enabled || hidden))
                    McpWalk(module, module.Root, 0, names, nodes, hidden);
            }
        }

        McpAddMenus(nodes);
        return nodes;
    }

    private void McpWalk(UIModule module, UIComponent component, int depth, Dictionary<UIComponent, string> names, List<HexScreenNode> into, bool hidden)
    {
        if (!hidden && !component.IsShownInHierarchy)
            return;

        string id = McpIdOf(component, 'c');
        var weak = new WeakReference<UIComponent>(component);
        Func<Vector2?> target = () => weak.TryGetTarget(out UIComponent? alive) && alive.Module is { } at ? McpCenter(McpBox(at, alive.Bounds)) : null;
        mcpTargets[id] = target;

        string text = McpTextOf(component);
        if (text.Length == 0 && component.Tooltip.Length > 0)
            text = $"tip: {Plain(compositor.Resolve(component.Tooltip))}";

        into.Add(new HexScreenNode(id, component.GetType().Name, text, names.GetValueOrDefault(component), McpBox(module, component.Bounds), depth,
            component.EnabledInHierarchy, component.IsFocused, component.IsInteractive, target));

        // What some components draw that isn't a component of its own
        switch (component)
        {
            case TreeView tree:
                McpTreeRows(module, tree, depth + 1, into);
                break;
            case StatusBar bar:
                McpStatusItems(module, bar, depth + 1, into);
                break;
            case TabStrip strip:
                for (int i = 0; i < strip.Tabs.Count; i++)
                    McpTab(module, component, i, strip.Tabs[i].Title + (strip.Tabs[i].Modified ? " (modified)" : string.Empty), i == strip.Selected, at => strip.TabBounds(at), depth + 1, into);
                break;
            case TabPanel pages:
                for (int i = 0; i < pages.Tabs.Length; i++)
                    McpTab(module, component, i, pages.TitleOf(i), i == pages.Selected, at => pages.TabBounds(at), depth + 1, into);
                break;
        }

        foreach (UIComponent child in component.Children)
            McpWalk(module, child, depth + 1, names, into, hidden);
    }

    private void McpTreeRows(UIModule module, TreeView tree, int depth, List<HexScreenNode> into)
    {
        for (int i = 0; i < tree.VisibleCount; i++)
        {
            UITreeItem item = tree.VisibleAt(i);
            if (tree.RowBounds(item) is not { } bounds)
                continue;

            string id = McpIdOf(item, 'r');
            var weakTree = new WeakReference<TreeView>(tree);
            var weakItem = new WeakReference<UITreeItem>(item);
            Func<Vector2?> target = () =>
            {
                if (!weakTree.TryGetTarget(out TreeView? alive) || !weakItem.TryGetTarget(out UITreeItem? row) || alive.Module is not { } at)
                    return null;
                alive.Reveal(row);
                return alive.RowBounds(row) is { } now ? McpCenter(McpBox(at, now)) : null;
            };

            mcpTargets[id] = target;
            string text = item.Detail.Length > 0 ? $"{Plain(item.Text)}  {Plain(item.Detail)}" : Plain(item.Text);
            into.Add(new HexScreenNode(id, "TreeRow", text, null, McpBox(module, bounds), depth, true, false, true, target));
        }
    }

    // A tab of a strip of tabs (the files over the editor) or of a panel's pages (Problems, Output, Terminal)
    private void McpTab(UIModule module, UIComponent owner, int index, string title, bool selected, Func<int, UIRect> boundsOf, int depth, List<HexScreenNode> into)
    {
        UIRect bounds = boundsOf(index);
        if (bounds.Width <= 0.0f)
            return;

        string id = $"{McpIdOf(owner, 't')}.{index}";
        Func<Vector2?> target = () => owner.Module is not null ? McpCenter(McpBox(module, boundsOf(index))) : null;
        mcpTargets[id] = target;
        into.Add(new HexScreenNode(id, selected ? "Tab(current)" : "Tab", Plain(title), null, McpBox(module, bounds), depth, true, false, true, target));
    }

    private void McpStatusItems(UIModule module, StatusBar bar, int depth, List<HexScreenNode> into)
    {
        for (int i = 0; i < bar.Items.Count; i++)
        {
            StatusItem item = bar.Items[i];
            if (item.IsVisible?.Invoke() == false || item.Text.Length == 0)
                continue;

            string id = $"s{i}";
            Func<Vector2?> target = () => McpCenter(McpBox(module, bar.ItemBounds(item)));
            mcpTargets[id] = target;
            string text = Plain(item.Text);
            into.Add(new HexScreenNode(id, "StatusItem", item.Tooltip.Length > 0 ? $"{text}  (tip: {Plain(compositor.Resolve(item.Tooltip))})" : text, null,
                McpBox(module, bar.ItemBounds(item)), depth, true, false, item.OnPressed is not null, target));
        }
    }

    /// <summary>The menu bar's titles, the items of the menu that's open and of a right click's menu, at the end.</summary>
    private void McpAddMenus(List<HexScreenNode> into)
    {
        if (menu.Module is { } module && menu.IsShownInHierarchy)
        {
            for (int i = 0; i < menu.Menus.Count; i++)
            {
                Menu bar = menu.Menus[i];
                if (bar.IsVisible?.Invoke() == false)
                    continue;

                string title = bar.Title, id = $"m{i}";
                Func<Vector2?> target = () => McpCenter(McpBox(module, menu.TitleBounds(title)));
                mcpTargets[id] = target;
                into.Add(new HexScreenNode(id, "MenuTitle", title, null, McpBox(module, menu.TitleBounds(title)), 1, true, false, true, target));
            }

            if (menu.OpenMenu is { } open)
                McpMenuItems(open, module, label => menu.ItemBounds(label, reveal: false), 'i', into);

            // And the menu open off one of its items, the one the pointer went into last
            if (menu.OpenSubmenu is { } inside)
                McpMenuItems(inside, module, label => menu.ItemBounds(label, reveal: false), 'j', into);
        }

        if (contextMenu?.Showing is { } shown && contextMenu.Module is { } menuModule)
            McpMenuItems(shown, menuModule, label => contextMenu.ItemBounds(label, reveal: false), 'x', into);
    }

    private void McpMenuItems(Menu open, UIModule module, Func<string, UIRect> boundsOf, char prefix, List<HexScreenNode> into)
    {
        for (int i = 0; i < open.Items.Count; i++)
        {
            MenuItem item = open.Items[i];
            if (item.IsSeparator || !item.Listed)
                continue;

            UIRect bounds = boundsOf(item.Label);
            if (bounds.Width <= 0.0f)
                continue;

            string label = item.Label, id = $"{prefix}{i}";
            Func<Vector2?> target = () => McpCenter(McpBox(module, boundsOf(label)));
            mcpTargets[id] = target;
            string text = item.Shortcut.Length > 0 ? $"{label}  ({item.Shortcut})" : label;
            into.Add(new HexScreenNode(id, item.Submenu is null ? "MenuItem" : "Submenu", text, null, McpBox(module, bounds), 2,
                item.IsEnabled?.Invoke() ?? true, false, true, target));
        }
    }

    private static Vector2 McpCenter(Vector4 box) => new(box.X + box.Z * 0.5f, box.Y + box.W * 0.5f);

    /// <summary>
    /// The list written for a model, a line a thing, indented by how deep it is. A container that says nothing and does
    /// nothing is left out (its children move up a level), there are hundreds of them and they're noise.
    /// </summary>
    private static string McpDescribe(List<HexScreenNode> nodes, bool interactiveOnly, int maxDepth, int most)
    {
        var said = new StringBuilder();
        int lines = 0, left = 0;
        var depthShown = new Dictionary<int, int>();
        foreach (HexScreenNode node in nodes)
        {
            if (node.Depth > maxDepth)
                continue;

            bool quiet = node.Text.Length == 0 && node.Name is null && !node.Focused;
            if (interactiveOnly ? !node.Interactive || (quiet && node.Kind.EndsWith("Panel", StringComparison.Ordinal)) : quiet && !node.Interactive)
                continue;

            if (node.Box.Z < 1.0f || node.Box.W < 1.0f)
                continue;

            if (lines >= most)
            {
                left++;
                continue;
            }

            lines++;
            int indent = interactiveOnly ? 0 : Math.Min(node.Depth, 12);
            said.Append(' ', indent).Append(node.Id).Append(' ').Append(node.Kind);
            if (node.Name is not null)
                said.Append(" #").Append(node.Name);
            if (node.Text.Length > 0)
                said.Append(" \"").Append(node.Text.Length > 120 ? node.Text[..117] + "..." : node.Text).Append('"');
            said.Append($" [{node.Box.X:0},{node.Box.Y:0} {node.Box.Z:0}x{node.Box.W:0}]");
            if (!node.Enabled)
                said.Append(" disabled");
            if (node.Focused)
                said.Append(" focused");
            said.Append('\n');
        }

        if (left > 0)
            said.Append($"... and {left} more, narrow it with find, root or interactive\n");
        return said.ToString();
    }

    /// <summary>
    /// What a piece of text points at, the best of what says it. Said exactly before said in part, something that can be
    /// pressed before something that's only drawn, the one on top (later in the list) before the one under it.
    /// </summary>
    private HexScreenNode? McpFindText(string text, out int others)
    {
        string wanted = Plain(text);
        List<HexScreenNode> all = McpScreen();
        var found = all.Where(node => node.Box.Z >= 1.0f && node.Box.W >= 1.0f && node.Enabled && node.Text.Length > 0)
            .Select((node, order) => (Node: node, Order: order,
                Score: (node.Text.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? 4 : node.Text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ? 2
                    : node.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 1 : 0) * 2 + (node.Interactive ? 1 : 0)))
            .Where(each => each.Score >= 2)
            .OrderByDescending(each => each.Score).ThenByDescending(each => each.Order)
            .ToList();

        others = Math.Max(0, found.Count - 1);
        return found.Count > 0 ? found[0].Node : null;
    }
}
