using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Grouping, changing a container into another kind, copies, and what goes on top of and inside what.
internal sealed partial class HexDocument
{
    /* Rearranging: grouping, copying, and what goes on top of what */

    /// <summary>
    /// Puts components into a new <see cref="Group"/>, keeping every one of them exactly where it is on screen. They
    /// have to be in the same container (or all on the screen). Each one is placed from the middle of the group from
    /// then on, whatever it was anchored to before, which is what makes the group one thing you can move about.
    /// </summary>
    /// <returns>The group, null with the reason why if it can't be done.</returns>
    public Group? Group(IReadOnlyList<UIComponent> components, out string problem)
    {
        problem = string.Empty;
        if (components.Count == 0)
        {
            problem = "select something to group first";
            return null;
        }

        UIComponent parent = components[0].Parent ?? Module.Root;
        if (components.Any(component => (component.Parent ?? Module.Root) != parent))
        {
            problem = "only things in the same container can be grouped, they'd jump about otherwise";
            return null;
        }

        var ordered = components.OrderBy(component => IndexIn(parent, component)).ToList();

        UIRect union = ordered[0].Bounds;
        foreach (var component in ordered.Skip(1))
            union = new UIRect(Vector2.Min(union.Min, component.Bounds.Min), Vector2.Max(union.Max, component.Bounds.Max));

        UIRect area = parent.Bounds.Shrink(parent.Padding);
        var group = new Group { Anchor = Origin.Center, Position = union.Center - area.Center };
        parent.Insert(IndexIn(parent, ordered[0]), group);

        foreach (var component in ordered)
        {
            Vector2 center = component.Bounds.Center;

            // Stretching to fill the group would make it as big as the group is, which is as big as they are: round and round
            if (component.Fill != UIFill.None)
            {
                component.Size = component.Bounds.Size;
                component.Fill = UIFill.None;
            }

            component.Anchor = Origin.Center;
            component.Pivot = null;
            component.Position = center - union.Center;
            group.Add(component);
        }

        NameOf(group);
        return group;
    }

    /// <summary>Whether a component holds other components, which is what can be changed into another kind that does.</summary>
    public static bool IsContainer(UIComponent component) => component is Panel or TabPanel;

    /// <summary>
    /// Changes a container into another kind of container, a group into a stack, a stack into a grid or tabs. It keeps its
    /// name, its place in the tree, where it is and how big, its look where the new kind has one, and everything in it in
    /// the same order. Into tabs, everything in it is a page, its name the tab.
    /// </summary>
    /// <param name="across">For a stack, whether it runs across rather than down.</param>
    /// <returns>The new one, null with the reason why if it can't be done.</returns>
    public UIComponent? Convert(UIComponent container, string kind, bool across, out string problem)
    {
        problem = string.Empty;
        if (!IsContainer(container))
        {
            problem = $"{NameOf(container)} doesn't hold anything to put in something else";
            return null;
        }

        if (UIModule.CreateDetached(kind) is not { } made || !IsContainer(made))
        {
            problem = $"a {kind} can't hold other components";
            return null;
        }

        UIComponent parent = container.Parent ?? Module.Root;
        int index = IndexIn(parent, container);
        string name = NameOf(container);

        // Where it is and what it is to the layout
        made.Anchor = container.Anchor;
        made.Pivot = container.Pivot;
        made.Position = container.Position;
        made.Size = container.Size;
        made.Fill = container.Fill;
        made.Padding = container.Padding;
        made.Visible = container.Visible;
        made.Enabled = container.Enabled;
        made.Layer = container.Layer;
        made.Tooltip = container.Tooltip;

        // Its look, and what it is filled with, where both are panels
        if (container is Panel was && made is Panel panel)
        {
            panel.Color = was.Color;
            panel.Background = was.Background;
            panel.Radius = was.Radius;
            panel.Outline = was.Outline;
            panel.Template = was.Template;
            panel.PreviewCount = was.PreviewCount;
            panel.Stagger = was.Stagger;
        }

        if (made is StackPanel stack)
        {
            stack.Direction = across ? UIDirection.Horizontal : UIDirection.Vertical;
            if (container is StackPanel stacked)
                stack.Spacing = stacked.Spacing;
        }

        if (made is Collapsible section)
            section.Title = name.Replace('_', ' ');

        // What was in it, in its order. Stand-ins for a template's items aren't the layout's, the new one makes its own
        var moving = container.Children.Where(child => !previews.Contains(child)).ToList();
        parent.Insert(index, made);
        foreach (var child in moving)
            made.Add(child);

        if (made is TabPanel tabs)
            tabs.Tabs = [.. moving.Select(page => NameOf(page).Replace('_', ' '))];

        parent.Remove(container);
        names.Remove(container);
        selection.Remove(container);
        names.Set(made, name);

        RefreshPreviews();
        return made;
    }

    /// <summary>
    /// Takes a group apart: what was in it goes into the group's container where the group was, everything staying where
    /// it is on screen.
    /// </summary>
    /// <returns>What was in it.</returns>
    public List<UIComponent> Ungroup(Group group)
    {
        UIComponent parent = group.Parent ?? Module.Root;
        UIRect area = parent.Bounds.Shrink(parent.Padding);
        int index = IndexIn(parent, group);

        var freed = group.Children.ToList();
        foreach (var component in freed)
        {
            Vector2 center = component.Bounds.Center;

            component.Anchor = Origin.Center;
            component.Pivot = null;
            component.Position = center - area.Center;
            parent.Insert(index++, component);
        }

        parent.Remove(group);
        names.Remove(group);
        selection.Remove(group);
        return freed;
    }

    /// <summary>
    /// Makes a copy of a component and everything in it, next to it and nudged down and to the right so it can be seen.
    /// Made the way a layout file is loaded, so the copy is everything the original would be written out as.
    /// </summary>
    public UIComponent? Duplicate(UIComponent original, out string problem)
    {
        problem = string.Empty;

        var subtree = new List<(UIComponent, int)>();
        Collect(original, 0, subtree);

        // Fresh names for the lot, none of them taken and none of them the same as each other
        var fresh = new Dictionary<UIComponent, string>();
        foreach (var (component, _) in subtree)
            fresh[component] = FreeName(UIModule.KindOf(component) ?? "component", fresh.Values);

        var code = new StringBuilder();
        foreach (var (component, _) in subtree)
            WriteComponent(code, component, component => fresh.TryGetValue(component, out var name) ? name : NameOf(component), top: original);

        UIComponent? parent = original.Parent is { } owner && owner != Module.Root ? owner : null;

        UILayout made;
        try
        {
            made = UILayout.LoadCode(Module, code.ToString(), parent, Path ?? string.Empty);
        }
        catch (Exception e)
        {
            problem = e.Message;
            return null;
        }

        foreach (var (name, part) in made.Parts)
            names.Set(part, name);

        if (!made.Parts.TryGetValue(fresh[original], out var copy))
        {
            problem = "the copy came out without its top, which shouldn't be possible";
            return null;
        }

        UIComponent container = copy.Parent ?? Module.Root;
        container.MoveChild(copy, IndexIn(container, original) + 1);
        copy.Position += new Vector2(16, -16);

        SettleIntros();
        RefreshPreviews();
        return copy;
    }

    /// <summary>Where a component goes among its siblings, which is the order they're drawn in (the last one on top).</summary>
    public enum Order { Front, Forward, Backward, Back }

    /// <summary>Moves a component up or down the order it is drawn in among its siblings.</summary>
    public bool Reorder(UIComponent component, Order order)
    {
        if (component.Parent is not { } parent)
            return false;

        int index = IndexIn(parent, component);
        int last = parent.Children.Count - 1;

        return parent.MoveChild(component, order switch
        {
            Order.Front => last,
            Order.Forward => Math.Min(last, index + 1),
            Order.Backward => Math.Max(0, index - 1),
            _ => 0
        });
    }

    /// <summary>
    /// Moves components a place up or down among their siblings, which is the order the tree lists them in and a
    /// stack lays them out in. Ones that are next to each other move together and keep their order, and one that
    /// is at the end already stays there.
    /// </summary>
    /// <param name="by">Less than nothing for up (towards the first), more for down.</param>
    /// <returns>Whether anything moved.</returns>
    public bool Move(IReadOnlyList<UIComponent> components, int by)
    {
        bool moved = false;

        // The ones nearest the way they are going go first, or they would be in the way of the ones behind them
        var ordered = by < 0 ? components : components.Reverse();
        foreach (var component in ordered)
        {
            if (component.Parent is not { } parent)
                continue;

            int index = IndexIn(parent, component);
            int target = index + Math.Sign(by);
            if (target < 0 || target >= parent.Children.Count)
                continue;

            // Not past one that is moving as well and got stuck at the end, they keep their order
            UIComponent neighbour = parent.Children[target];
            if (components.Contains(neighbour) || previews.Contains(neighbour))
                continue;

            moved |= parent.MoveChild(component, target);
        }

        return moved;
    }

    /// <summary>
    /// Takes a component out of the container it is in and puts it after that container, in whatever that is in.
    /// </summary>
    /// <returns>Whether it could be done, with the reason why not if it couldn't.</returns>
    public bool MoveOut(UIComponent component, out string problem)
    {
        problem = string.Empty;
        if (component.Parent is not { } parent || parent == Module.Root)
        {
            problem = $"{NameOf(component)} is on the screen already, there is nothing to take it out of";
            return false;
        }

        if (parent is TabPanel || parent.Parent is TabPanel)
        {
            problem = "a page belongs to its tabs, and what goes next to a page would be a page without a tab";
            return false;
        }

        UIComponent outer = parent.Parent ?? Module.Root;
        outer.Insert(IndexIn(outer, parent) + 1, component);
        return true;
    }

    /// <summary>
    /// Puts a component into the container that comes right before it among its siblings, at the end of it, which
    /// is where it would be had it been listed a step further in. For tabs that is the page that is open.
    /// </summary>
    public bool MoveInto(UIComponent component, out string problem)
    {
        problem = string.Empty;
        UIComponent parent = component.Parent ?? Module.Root;
        int index = IndexIn(parent, component);

        UIComponent? into = index > 0 ? parent.Children[index - 1] : null;
        if (into is TabPanel tabs)
            into = tabs.Selected >= 0 && tabs.Selected < tabs.Children.Count ? tabs.Children[tabs.Selected] : null;

        if (into is not Panel || into is Include || previews.Contains(into))
        {
            problem = $"what comes before {NameOf(component)} isn't something things go into, move it under a stack or a panel first";
            return false;
        }

        into.Add(component);
        return true;
    }

    /// <summary>
    /// Writes components and everything in them as the code that makes them, the way a layout file has them, for
    /// putting them down somewhere else (<see cref="Paste"/>), in this layout or another.
    /// </summary>
    public string Copy(IReadOnlyList<UIComponent> components)
    {
        var code = new StringBuilder();
        foreach (var top in components)
        {
            var subtree = new List<(UIComponent, int)>();
            Collect(top, 0, subtree);

            foreach (var (component, _) in subtree)
                WriteComponent(code, component, top: top);
        }

        return code.ToString();
    }

    /// <summary>
    /// Makes what <see cref="Copy"/> wrote, in a container or on the screen. Everything keeps the name it had
    /// where that is free and gets a new one where it isn't.
    /// </summary>
    /// <returns>The tops of what was made, empty with the reason why if the code wouldn't run.</returns>
    public List<UIComponent> Paste(string code, UIComponent? parent, out string problem)
    {
        problem = string.Empty;

        UILayout made;
        try
        {
            made = UILayout.LoadCode(Module, code, parent, Path ?? string.Empty);
        }
        catch (Exception e)
        {
            problem = e.Message;
            return [];
        }

        foreach (var (name, part) in made.Parts)
            names.Set(part, names.Has(name) ? FreeName(UIModule.KindOf(part) ?? "component") : name);

        var tops = made.Roots.ToList();
        foreach (var top in tops)
        {
            NameOf(top);

            // Nudged so a copy put down on top of what it was copied from can be seen to be there
            top.Position += new Vector2(16, -16);
        }

        SettleIntros();
        RefreshPreviews();
        return tops;
    }

    private static int IndexIn(UIComponent parent, UIComponent child)
    {
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] == child)
                return i;
        }

        return -1;
    }
}
