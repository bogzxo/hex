using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test, more than one thing at once, groups, copies, what a layout is made for, tabs, closing and dragging edges.
internal sealed partial class DawnScene
{
    private void TestMultipleSelection(HexPointerScript test)
    {
        /* Lots of things at once: selecting more than one, groups, copies, what's on top and the right click menu */

        UIComponent? first = null, second = null, grouped = null;
        UIRect firstWas = default, secondWas = default;

        // Where something is on the screen, which grouping and ungrouping mustn't change
        UIRect Seen(UIComponent? component) => component is { Module: { } module }
            ? new UIRect(module.ToWorld(component.Bounds.Min), module.ToWorld(component.Bounds.Max))
            : default;
        bool Same(UIRect a, UIRect b) => Near(a.Center.X, b.Center.X, 1) && Near(a.Center.Y, b.Center.Y, 1) && Near(a.Width, b.Width, 1);
        Func<Vector2> Nothing() => Part("canvas", 0.04f, 0.5f);

        Pick("File", () => "New Layout");
        test.Settle(0.3f);
        test.Click(Kind("label"));
        test.Wait();
        test.Run(() => first = document.Selected);
        test.Click(Kind("button"));
        test.Wait();
        test.Run(() =>
        {
            second = document.Selected;
            second!.Position = new Vector2(0, -120);
            codeDirty = true;
        });
        test.Wait();
        test.Check("multiselect: two things to play with, next to each other", () => first is Label && second is Button && first.Parent == second.Parent);

        test.Click(At(() => first));
        test.Run(() => designer.fakeModifier = true);
        test.Click(At(() => second));
        test.Run(() => designer.fakeModifier = false);
        test.Wait();
        test.Check("multiselect: ctrl and a click adds to what's selected", () =>
            document.Selection.Count == 2 && stage.Highlights.Count == 2 && designer.treeRows[first!].Selected && designer.treeRows[second!].Selected);

        // The style of the button, picked out of the art of the skin. The button was picked last, the rows are its.
        // Its row is a good way down the properties by now, so it is brought into view first
        test.Run(() => inspectorScroll.ScrollIntoView(designer.editors["style"]));
        test.Wait();
        test.Click(() => chrome.ToWorld(((TextBox)designer.editors["style"]).ButtonBounds.Center));
        test.Settle(0.3f);
        test.Check("art: the button in the style box opens the art of the skin the layouts wear", () =>
            designer.artPicker is { IsOpen: true, Tiles.Count: > 10 } && designer.editors["style"] is TextBox { HasButton: true });
        test.Run(() => designer.artPicker!.Filter = "button_flat");
        test.Settle(0.3f);
        test.Check("art: typing narrows it down", () => designer.artPicker!.Tiles.Count is > 0 and < 40);
        test.Click(() => chrome.ToWorld(designer.artPicker!.Tiles[0].Bounds.Center));
        test.Wait();
        test.Check("art: a click on a piece picks it, for the box, the component and the code", () =>
            !designer.artPicker!.IsOpen && second is Button { Style: var style } && style.StartsWith("button_flat")
            && ((TextBox)designer.editors["style"]).Text == style && Code().Contains($"style: \"{style}\""));
        test.Run(() => designer.Set(second!, "style", new StringValue("button")));
        test.Wait();

        Vector2 firstAt = default;
        test.Run(() =>
        {
            firstAt = first!.Position;
            designer.Set(second!, "intro", new StringValue("fade"));
            designer.Set(second!, "intro_delay", new NumberValue(0.5f));
        });
        test.Check("multiselect: a property changed in the inspector is changed for everything selected", () =>
            first!.Intro == UIIntro.Fade && second!.Intro == UIIntro.Fade && Near(first.IntroDelay, 0.5f, 0.001f) && Near(second.IntroDelay, 0.5f, 0.001f));
        test.Run(() => ((NumberBox)((StackPanel)designer.editors["pos"]).Children[0]).OnValueChanged!(33));
        test.Check("multiselect: one number of a position only changes that number for the rest", () =>
            Near(second!.Position.X, 33) && Near(first!.Position.X, 33) && Near(first.Position.Y, firstAt.Y) && !Near(first.Position.Y, second.Position.Y, 20));
        test.Run(() =>
        {
            designer.Set(second!, "intro", new StringValue("none"));
            designer.Set(second!, "intro_delay", new NumberValue(0.0f));
            document.SettleIntros();
        });
        test.Wait();

        Pick("Layout", () => "Align Left");
        test.Wait();
        test.Check("arrange: left edges puts two things on the one line", () =>
            Near(first!.Bounds.Min.X, second!.Bounds.Min.X, 0.6f) && !Near(first.Bounds.Center.Y, second.Bounds.Center.Y, 20) && document.Selection.Count == 2);
        Pick("Layout", () => "Align Centre");
        test.Wait();
        test.Check("arrange: and centres across puts their middles on it", () => Near(first!.Bounds.Center.X, second!.Bounds.Center.X, 0.6f));

        test.Run(() =>
        {
            firstWas = Seen(first);
            secondWas = Seen(second);
        });
        Pick("Layout", () => "Group");
        test.Wait();
        test.Check("group: puts what's selected in a group, which is what's selected now", () =>
            (grouped = document.Selected) is Group && first!.Parent == grouped && second!.Parent == grouped && document.Selection.Count == 1);
        test.Check("group: and nothing moves on screen", () => Same(Seen(first), firstWas) && Same(Seen(second), secondWas));
        test.Check("group: which goes in the code", () => Code().Contains("compositor.group("));
        test.Check("group: and writes back the same", () => CheckRoundTrip(document));

        test.Click(Nothing());
        test.Click(At(() => first));
        test.Wait();
        test.Check("group: a click on what's in it picks the group", () => document.Selected == grouped && document.Selection.Count == 1);
        test.Click(At(() => first));
        test.Wait();
        test.Check("group: and another click goes in", () => document.Selected == first);

        // A group can be had as any other kind of container, a stack across say, what is in it and all, and back again
        string groupName = string.Empty;
        test.Click(Nothing());
        test.Run(() =>
        {
            designer.Select(grouped);
            groupName = document.NameOf(grouped!);
        });
        test.RightClick(At(() => grouped));
        test.Wait();
        PickFromMenu("Stack, across");
        test.Wait();
        test.Check("change into: the menu of a group changes it into a stack across, its name and what is in it kept", () =>
            document.Selected is StackPanel { Direction: UIDirection.Horizontal } stack && stack is not Collapsible
            && document.NameOf(stack) == groupName && first!.Parent == stack && second!.Parent == stack
            && grouped!.Parent is null && Code().Contains($"let {groupName} = compositor.stack(") && CheckRoundTrip(document));
        test.Run(() => designer.ConvertSelected("tabs", false));
        test.Wait();
        test.Check("change into: into tabs everything in it is a page", () =>
            document.Selected is TabPanel { Tabs.Length: 2 } pages && first!.Parent == pages && CheckRoundTrip(document));
        test.Run(() => designer.ConvertSelected("group", false));
        test.Wait();
        test.Check("change into: and back into a group", () =>
            (grouped = document.Selected) is Group && first!.Parent == grouped && second!.Parent == grouped && document.NameOf(grouped) == groupName);

        test.Click(Nothing());
        test.RightClick(At(() => second));
        test.Wait();
        test.Check("menu: a right click on the canvas picks what's there and opens the menu", () =>
            document.Selected == grouped && contextMenu?.Showing is not null && chrome.Popup == contextMenu);
        PickFromMenu("Ungroup");
        test.Wait();
        test.Check("ungroup: the menu takes the group apart and selects what was in it", () =>
            grouped!.Parent is null && first!.Parent == document.Module.Root && second!.Parent == first.Parent && document.Selection.Count == 2);
        test.Check("ungroup: and nothing moves on screen", () => Same(Seen(first), firstWas) && Same(Seen(second), secondWas));

        Pick("Edit", () => "Duplicate");
        test.Wait();
        test.Check("duplicate: copies everything selected, and selects the copies", () =>
            document.Walk().Count() == 4 && document.Selection.Count == 2 && !document.IsSelected(first!) && !document.IsSelected(second!));
        test.Check("duplicate: with names of their own", () =>
            document.Selection.Select(document.NameOf).Distinct().Count() == 2 && !document.Selection.Any(copy => document.NameOf(copy) == document.NameOf(first!)));

        test.RightClick(Row(() => first));
        test.Wait();
        PickFromMenu("Bring to Front");
        test.Wait();
        test.Check("order: the menu of a row of the tree brings it to the front", () =>
            document.Selected == first && document.Module.Root.Children[^1] == first);
        test.RightClick(At(() => first));
        test.Wait();
        PickFromMenu("Send to Back");
        test.Wait();
        test.Check("order: and the one on the canvas sends it to the back, code and all", () =>
            document.Module.Root.Children[0] == first && Code().IndexOf($"let {document.NameOf(first!)} ") < Code().IndexOf($"let {document.NameOf(second!)} "));
    }

    private void TestDesignSize(HexPointerScript test)
    {
        /* What a layout is made for, and the canvas */

        Pick("Layout", () => HexDesigner.DescribeDesign(new Vector2(1600, 900)));
        test.Wait();
        test.Check("design: the layout menu says what it's made for, in the code before anything else", () =>
            document.Design == new Vector2(1600, 900) && Code().Contains("compositor.design({ size: vec(1600, 900), fit: \"contain\" });")
            && Code().IndexOf("compositor.design") < Code().IndexOf("let "));
        Pick("Layout", () => Said("designer_canvas.preview_on_21_9"));
        test.Wait();
        test.Check("design: on an ultrawide it keeps its shape, in the middle", () =>
            document.Module.Viewport is { } seen && Near(seen.Width / seen.Height, 21.0f / 9.0f, 0.01f)
            && Near(document.Module.Frame.Width, 1600) && Near(document.Module.Frame.Height, 900) && Near(document.Module.Frame.Center.X, seen.Center.X));
        Pick("Layout", () => "Stretch");
        test.Wait();
        test.Check("design: stretched it takes the lot", () =>
            document.Fit == UIFit.Stretch && Near(document.Module.Frame.Width, document.Module.Viewport!.Value.Width) && Code().Contains("fit: \"stretch\""));
        Pick("Layout", () => Said("designer_canvas.preview_on_its_own_shape"));
        Pick("Layout", () => "Any Screen");
        test.Wait();
        test.Check("design: made for any screen there's nothing about it in the code", () => document.Design is null && !Code().Contains("compositor.design"));

        float fitted = 0;
        test.Run(() => fitted = document.Module.Scale.X);
        Pick("View", () => "Zoom In");
        test.Wait();
        test.Check("zoom: the view menu zooms the canvas in", () => designer.canvasZoom > 1.0f && document.Module.Scale.X > fitted * 1.2f);
        Pick("View", () => "Zoom to Fit");

        // Waited on rather than given a few updates, a busy machine took longer than that now and then
        test.WaitFor(() => designer.canvasZoom == 1.0f && Near(document.Module.Scale.X, fitted, 0.001f), 2.0f);
        test.Check("zoom: and back to fit", () => designer.canvasZoom == 1.0f && Near(document.Module.Scale.X, fitted, 0.001f));
    }

    private void TestTabs(HexPointerScript test)
    {
        /* Tabs */

        TabPanel? tabbed = null;
        Pick("Layout", () => "Deselect All");
        test.Click(Kind("tabs"));
        test.Wait();
        test.Check("tabs: come with a page for each tab", () =>
            (tabbed = document.Selected as TabPanel) is { Children.Count: 2 } && string.Join(",", tabbed.Tabs) == "One,Two" && tabbed.Selected == 0);
        test.Click(() => document.Module.ToWorld(tabbed!.TabBounds(1).Center));
        test.Wait();
        test.Check("tabs: a click on a tab in the canvas opens its page", () => tabbed!.Selected == 1 && document.Selected == tabbed);
        test.Click(Kind("label"));
        test.Wait();
        test.Check("tabs: what's added goes onto the page that's open", () => document.Selected is Label added && added.Parent == tabbed!.Children[1]);
        test.Check("tabs: and the lot writes back the same", () => CheckRoundTrip(document));
    }

    private void TestClosingWithChanges(HexPointerScript test)
    {
        /* Closing with changes */

        int open = 0;
        test.Run(() => open = documents.Count);
        Pick("File", () => "Close");
        test.Wait();
        test.Check("close: a layout with changes asks first", () => chrome.Dialog is { IsOpen: true } && documents.Count == open);

        // The question pops up, and while it is still growing its buttons are not yet where they will be
        test.Settle(0.35f);

        // Where the answer is, remembered while the question is still up (the click takes it down)
        Vector2 dontSave = Vector2.Zero;
        test.Run(() => dontSave = chrome.Dialog is { } asked ? chrome.ToWorld(asked.Buttons[1].Bounds.Center) : Vector2.Zero);
        test.Click(() => dontSave);
        test.Wait();
        test.Check("close: and goes without saving when told to", () => chrome.Dialog is null && documents.Count == Math.Max(1, open - 1));
    }

    private void TestDraggingEdges(HexPointerScript test)
    {
        /* Dragging edges, of what is in the canvas and of the editor's own panels */

        // A point of the editor or of the layout in the canvas, in their own units, as the pointer wants it
        Func<Vector2> InChrome(Func<Vector2> point) => () => chrome.ToWorld(point());
        Func<Vector2> InLayout(Func<Vector2> point) => () => document.Module.ToWorld(point());

        Vector2 boxSize = default, boxAt = default;
        Pick("File", () => "New Layout");
        test.Settle(0.3f);
        test.Click(Kind("panel"));
        test.Wait();
        test.Run(() =>
        {
            box = document.Selected;
            designer.Set(box!, "size", new Vector2Value(new Vector2(200, 120)));
        });
        test.Wait();
        test.Run(() => boxSize = box!.Bounds.Size);
        test.Check("handles: the one thing that is selected has a tab on every edge", () =>
            box is Panel && stage.Highlighted == box && stage.HighlightGrips == UIResize.All && Near(boxSize.X, 200) && Near(boxSize.Y, 120));

        test.Hover(InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)));
        test.Check("handles: and the one under the pointer is lit", () => stage.HighlightLitGrips == UIResize.Right);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)),
            InLayout(() => new Vector2(box!.Bounds.Center.X + boxSize.X * 0.5f - 2 + 60, box.Bounds.Center.Y)));
        test.Wait();
        test.Check("handles: dragging the right edge makes it that much wider and no taller, code and all", () =>
            Near(box!.Size.X, 260, 1.5f) && Near(box.Size.Y, 120) && Code().Contains("size: vec(") && !designer.dragging);

        test.Run(() => boxSize = box!.Bounds.Size);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Max.Y - 2)),
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2 + 30, box.Bounds.Max.Y - 2 + 40)));
        test.Wait();
        test.Check("handles: a corner drags both", () => box!.Size.X > boxSize.X + 10 && box.Size.Y > boxSize.Y + 10);

        test.Run(() => settings.Snap = 8.0f);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)),
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2 + 21, box.Bounds.Center.Y)));
        test.Wait();
        test.Check("snap: with a grid a size lands on it", () => Near(box!.Size.X % 8.0f, 0.0f, 0.01f));
        test.Run(() => boxAt = box!.Position);
        test.Drag(At(() => box), InLayout(() => box!.Bounds.Center + new Vector2(21, 0)));
        test.Wait();
        test.Check("snap: and so does a move, by whole steps of it", () =>
            box!.Position.X > boxAt.X && Near((box.Position.X - boxAt.X) % 8.0f, 0.0f, 0.01f) && Near(box.Position.Y, boxAt.Y));
        test.Run(() => settings.Snap = 0.0f);

        test.Run(() => settings.CanvasHandles = false);
        test.Hover(InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)));
        test.Check("handles: switched off there are none", () => stage.HighlightGrips == UIResize.None && stage.HighlightLitGrips == UIResize.None);
        test.Run(() => settings.CanvasHandles = true);

        test.Run(() => designer.Set(box!, "resize", new StringValue("right, bottom")));
        test.Wait();
        test.Check("resize: a component that can be dragged in a game says so in its code", () =>
            box!.Resizable == (UIResize.Right | UIResize.Bottom) && Code().Contains("resize: \"right, bottom\""));
        test.Check("resize: and writes back the same", () => CheckRoundTrip(document));

        float listWide = 0;
        test.Run(() => listWide = treeScroll.Size.X);
        test.Hover(InChrome(() => new Vector2(treeScroll.Bounds.Max.X + 4, treeScroll.Bounds.Center.Y)));
        test.Check("panels: the edge between the lists and the canvas lights up under the pointer", () => treeScroll.ActiveGrip == UIResize.Right);
        test.Drag(
            InChrome(() => new Vector2(treeScroll.Bounds.Max.X + 4, treeScroll.Bounds.Center.Y)),
            InChrome(() => new Vector2(treeScroll.Bounds.Min.X + listWide + 4 + 70, treeScroll.Bounds.Center.Y)));
        test.Wait();
        test.Check("panels: dragging it makes the lists wider and the canvas make do", () =>
            Near(treeScroll.Size.X, listWide + 70, 2) && Near(settings.LeftWidth, treeScroll.Size.X) && Fits() && !treeScroll.IsResizing);
        test.Check("panels: and nothing in the canvas was touched by it", () => document.Selected == box && !designer.dragging);
        test.Check("panels: the rows of the list and the buttons over it are as wide as the list is now", () =>
            tree.Children.Count > 0 && tree.Children.All(row => Near(row.Size.X, treeScroll.Size.X - 24, 1))
            && Near(designer.paletteButtons[0].Size.X * 2 + 4 + PALETTE_BAR, treeScroll.Size.X, 2) && tree.Children[0].Bounds.Max.X <= treeScroll.Bounds.Max.X);

        // The properties on the other side, dragged by their left edge. What a property is set with gets the room
        // Where to take hold of it is read once, before it moves. Asked for again on the way the edge has gone
        // along with the pointer and the layout is a tick behind, and the drag comes up short
        float propertiesWide = 0, editorWide = 0;
        Vector2 propertiesEdge = default;
        test.Run(() =>
        {
            propertiesWide = inspectorScroll.Size.X;
            editorWide = designer.editors["name"].Bounds.Width;
            propertiesEdge = new Vector2(inspectorScroll.Bounds.Min.X - 4, inspectorScroll.Bounds.Center.Y);
        });
        test.Drag(InChrome(() => propertiesEdge), InChrome(() => propertiesEdge - new Vector2(90, 0)));
        test.Wait();
        test.Check("panels: dragging the properties wider makes what is in them wider with them", () =>
            Near(inspectorScroll.Size.X, propertiesWide + 90, 2) && Near(designer.editors["name"].Bounds.Width, editorWide + 90, 2)
            && designer.editors["name"].Bounds.Max.X <= inspectorScroll.Bounds.Max.X - 19.5f && Fits());
        test.Check("panels: every kind of editor in them, the ones that are two and four numbers across as well", () =>
            designer.editors["pos"] is StackPanel pair && Near(pair.Bounds.Width, designer.editors["name"].Bounds.Width, 2)
            && designer.editors["padding"] is StackPanel four && Near(four.Bounds.Width, designer.editors["name"].Bounds.Width, 2));

        test.Run(ResetPanels);
        test.Wait();
        test.Check("panels: and they go back to what they come as", () =>
            Near(treeScroll.Size.X, listWide, 1) && settings.LeftWidth == 0.0f && Fits());

        // The layers dragged far taller than the side bar has room for stop where it runs out, the editor stays in the
        // window, status bar and all. They pushed it off the bottom once
        Vector2 layersEdge = Vector2.Zero;
        test.Run(() => layersEdge = new Vector2(layersScroll.Bounds.Center.X, layersScroll.Bounds.Max.Y + 4));
        test.Drag(InChrome(() => layersEdge), InChrome(() => layersEdge + new Vector2(0, 700)));
        test.Settle(0.2f);
        test.Check("panels: the layers dragged taller than there is room for stop at it, the editor stays in the window", () =>
            layersScroll.Size.Y > usualLayers && treeScroll.Size.Y >= MIN_FLEXIBLE - 1 && Fits());
        test.Run(ResetPanels);
        test.Wait();
    }
}
