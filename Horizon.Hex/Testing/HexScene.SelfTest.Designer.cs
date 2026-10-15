using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// The self test, the designer, adding, selecting, editing, undoing, the canvas and the inspector.
internal sealed partial class HexScene
{
    private void TestEditorItself(HexPointerScript test)
    {
        /* The editor itself */

        test.Check("layout: the editor is drawn at the scale it was asked for, layouts and all", () =>
        {
            Vector2 viewport = Engine.WindowManager.ViewportSize;
            UIRect screen = chrome.Root.Bounds;

            return compositor.UIScale == compositor.Scale && stage.UIScale == compositor.UIScale
                && Near(screen.Width * compositor.UIScale, viewport.X, 1) && Near(screen.Height * compositor.UIScale, viewport.Y, 1);
        });
        test.Check("layout: its columns take the room the window has", Fits);
        test.Check("layout: the editor reads in a font of its own, the layouts in the one games have", () =>
            compositor.Skin is { } own && stage.Skin is { } staged && own.Font.LineHeight != staged.Font.LineHeight);
        test.Run(() => welcomeWanted = true);
        test.Settle(0.2f);
        test.Check("welcome: with nothing open the welcome is over the empty layout, with what to start with", () =>
            welcome is { Visible: true } page && page.Parent == canvas && Descendants(page).OfType<Button>().Any(button => button.Label == "@welcome.new_layout"));
        test.Run(() => welcomeWanted = false);
        test.Wait();
        test.Check("welcome: and gone once it isn't wanted", () => welcome is not { Visible: true });

        test.Check("bootstrap: the palette was filled from its template", () =>
            paletteButtons.Count == Palette.Length && paletteButtons[0].Label == "stack" && paletteButtons[0].Parent == layout.Get<GridPanel>("palette"));
    }

    private void TestAdding(HexPointerScript test)
    {
        /* Adding */

        test.Click(Kind("stack"));
        test.Wait();
        test.Check("add: a stack onto the screen, selected", () =>
            (stack = document.Selected) is StackPanel && stack.Parent == document.Module.Root && document.Walk().Count() == 1);

        test.Click(Kind("button"));
        test.Wait();
        test.Check("add: a button goes into the selected container", () => (button = document.Selected) is Button && button.Parent == stack);

        test.Click(Kind("label"));
        test.Wait();
        test.Check("add: and next to the selection when that isn't a container", () =>
            (label = document.Selected) is Label && label.Parent == stack && document.Walk().Count() == 3);
        test.Check("tree: a row for each of them, the selected one lit", () =>
            treeRows.Count == 3 && treeRows[label!].Selected && !treeRows[button!].Selected);
        test.Check("code: written in the order they are in the layout", () =>
        {
            string written = Code();
            return written.Contains("let stack1 = compositor.stack(")
                && written.Contains("let button1 = compositor.button({ parent: stack1, label: \"Button\" });")
                && written.IndexOf("button1") < written.IndexOf("label1");
        });
    }

    private void TestSelecting(HexPointerScript test)
    {
        /* Selecting */

        test.Click(At(() => button));
        test.Wait();
        test.Check("canvas: clicking a component selects it", () => document.Selected == button);
        test.Check("canvas: and has the UI mark it out", () => stage.Highlighted == button);
        test.Check("inspector: a row for every property of a button", () =>
            editors.ContainsKey("label") && editors.ContainsKey("size") && editors.ContainsKey("anchor") && editors.ContainsKey("visible")
            && editors["label"] is TextBox { Text: "Button" } && !editors.ContainsKey("on_pressed"));

        test.Click(Row(() => label));
        test.Wait();
        test.Check("tree: clicking a row selects its component", () => document.Selected == label && editors["text"] is TextBox { Text: "Label" });

        test.Click(Row(() => button));
        test.Wait();
    }

    private void TestEditingProperties(HexPointerScript test)
    {
        /* Editing properties */

        test.Click(At(() => editors["label"]));
        test.Wait();
        test.Check("inspector: clicking a text box gives it the focus", () => editors["label"].IsFocused);

        test.Run(() => TypeInto((TextBox)editors["label"], "Fight"));
        test.Wait();
        test.Check("inspector: typing changes the component", () => ((Button)button!).Label == "Fight");
        test.Check("inspector: and the code, on screen as well", () => Code().Contains("label: \"Fight\"") && code.Text.Contains("label: \"Fight\""));

        test.Run(() => grab = At(() => ((StackPanel)editors["size"]).Children[0], 0.3f)());
        test.Drag(() => grab, () => grab + new Vector2(160, 0) * compositor.UIScale);
        test.Wait();
        test.Check("inspector: dragging across a number box changes the number", () => Near(button!.Size.X, 40, 1.5f) && Code().Contains("size: vec("));

        test.Click(At(() => editors["anchor"]));
        test.Wait();
        test.Check("dropdown: clicking it opens its list on top of the inspector", () =>
            editors["anchor"] is Dropdown { IsOpen: true } open && chrome.Popup == open);
        test.Click(Option("anchor", 1));
        test.Wait();
        test.Check("dropdown: clicking an option chooses it and closes the list", () =>
            button!.Anchor == Horizon.Rendering.Origin.TopLeft && Code().Contains("anchor: \"top_left\"") && editors["anchor"] is Dropdown { IsOpen: false });
        test.Click(At(() => editors["anchor"]));
        test.Click(Option("anchor", 0));
        test.Wait();
        test.Check("dropdown: and back, which takes it out of the code again", () => button!.Anchor == Horizon.Rendering.Origin.Center && !Code().Contains("anchor:"));
        test.Click(At(() => editors["anchor"]));
        test.Click(At(() => editors["kind"]));
        test.Wait();
        test.Check("dropdown: clicking anywhere else closes the list and changes nothing", () =>
            chrome.Popup is null && button!.Anchor == Horizon.Rendering.Origin.Center);
    }

    private void TestEntrances(HexPointerScript test)
    {
        /* Entrances */

        Reveal("intro_time");
        test.Click(At(() => editors["intro"]));
        test.Click(Option("intro", 1));
        test.Wait();
        test.Check("intro: a component can be told to pop in", () => button!.Intro == UIIntro.Pop && Code().Contains("intro: \"pop\""));
        test.Check("intro: and does so straight away, to be looked at", () => button!.Tweens.Count > 0);
        test.Run(() => document.SettleIntros());
        test.Wait();
        test.Click(At(() => layout.Get<Button>("play_button")));
        test.Wait();
        test.Check("intro: the play button next to the name of the layout plays them again", () => button!.Tweens.Count > 0);

        test.Click(At(() => ((StackPanel)editors["intro_time"]).Children[0]));
        test.Wait();
        test.Check("slider: clicking halfway along sets the number halfway", () =>
            Near(button!.IntroTime, 1.0f, 0.12f) && Code().Contains("intro_time: ")
            && ((StackPanel)editors["intro_time"]).Children[1] is NumberBox shown && Near(shown.Value, button.IntroTime, 0.001f));

        test.Run(() => TypeInto((NumberBox)((StackPanel)editors["intro_time"]).Children[1], "0.35"));
        test.Click(At(() => editors["intro"]));
        test.Click(Option("intro", 0));
        test.Wait();
        test.Check("intro: put back, there is nothing about it in the code", () =>
            button!.Intro == UIIntro.None && Near(button.IntroTime, 0.35f, 0.001f) && !Code().Contains("intro"));
    }

    private void TestUndoing(HexPointerScript test)
    {
        /* Undoing */

        string settled = string.Empty;
        test.Settle(0.6f);
        test.Run(() =>
        {
            settled = Code();
            TypeInto((TextBox)editors["label"], "Brawl");
        });
        test.Settle(0.6f);
        test.Check("undo: the button is lit once there is something to take back", () => (document.CanUndo || recordPending) && ((Button)button!).Label == "Brawl");

        Pick("Edit", () => "Undo");
        test.Settle(0.3f);
        test.Check("undo: takes the last change back, and only that one", () =>
            Code() == settled && document.Find("button1") is Button { Label: "Fight" });
        test.Check("undo: what was selected still is", () =>
            document.Selected == document.Find("button1") && stage.Highlighted == document.Selected && editors["label"] is TextBox { Text: "Fight" });

        Pick("Edit", () => "Redo");
        test.Settle(0.3f);
        test.Check("redo: makes the change again", () => document.Find("button1") is Button { Label: "Brawl" } && !document.CanRedo);

        Pick("Edit", () => "Undo");
        test.Settle(0.3f);
        test.Run(() =>
        {
            // Going back builds the layout again, what the test was holding on to is from before
            stack = document.Find("stack1");
            button = document.Find("button1");
            label = document.Find("label1");
        });
        test.Check("undo: and takes it back again", () => Code() == settled && button is Button { Label: "Fight" } && document.CanRedo);

        test.Click(At(() => editors["visible"], 0.9f));
        test.Wait();
        test.Check("inspector: hiding a component greys its row out", () => !button!.Visible && Code().Contains("visible: false"));
        test.Click(At(() => editors["visible"], 0.9f));
        test.Wait();

        test.Run(() => TypeInto((TextBox)editors["name"], "start_button"));
        test.Wait();
        test.Check("rename: the code and the tree go by the new name", () =>
            document.NameOf(button!) == "start_button" && Code().Contains("let start_button = compositor.button(")
            && treeRows[button!].Children[0] is Label { Text: var row } && row.Contains("start_button") && row.Contains("button[/color]"));

        test.Run(() => TypeInto((TextBox)editors["name"], "label1"));
        test.Wait();
        test.Check("rename: a name another component has is refused", () => document.NameOf(button!) != "label1" && document.NameOf(label!) == "label1");
        test.Run(() => TypeInto((TextBox)editors["name"], "start_button"));
        test.Wait();

        // Resting on what a property is called says what it does, and so does resting on its editor
        UIComponent CaptionOf(string property) => editors[property].Parent!.Parent!.Children[0];
        test.Hover(At(() => CaptionOf("pos")));
        test.Settle(0.8f);
        test.Check("help: resting on a property's name brings up what it does", () =>
            compositor.TooltipOf == CaptionOf("pos") && compositor.TooltipOf!.Tooltip.Contains("anchor"));
        test.Hover(At(() => editors["anchor"]));
        test.Settle(0.8f);
        test.Check("help: and so does resting on its editor", () =>
            compositor.TooltipOf is { } helping && helping.Tooltip.Contains("parent") && helping != CaptionOf("pos"));
        test.Hover(Part("canvas", 0.5f, 0.5f));
        test.Wait();
    }

    private void TestCanvas(HexPointerScript test)
    {
        /* The canvas */

        test.Click(Part("canvas", 0.04f, 0.06f));
        test.Wait();
        test.Check("canvas: clicking where there is nothing selects nothing", () => document.Selected is null && editors.Count == 0);

        test.Click(Kind("panel"));
        test.Wait();
        test.Check("add: onto the screen when nothing is selected", () => (panel = document.Selected) is Panel && panel.Parent == document.Module.Root);

        Vector2 before = Vector2.Zero;
        Vector2 pull = new(120, -60);
        test.Run(() =>
        {
            before = panel!.Position;
            grab = At(() => panel, 0.5f, 0.9f)();
        });
        test.Drag(() => grab, () => grab + pull * compositor.UIScale);
        test.Wait();
        test.Check("canvas: dragging moves a component by as much of the layout as the pointer covered", () =>
            Vector2.Distance(panel!.Position - before, pull / document.Module.Scale.X) < 2.5f && Code().Contains("pos: vec("));
        test.Check("canvas: and the inspector shows where it ended up", () =>
            editors["pos"] is StackPanel row && row.Children[0] is NumberBox x && Near(x.Value, panel!.Position.X, 0.01f));
    }

    private void TestReordering(HexPointerScript test)
    {
        /* Reordering and deleting */

        test.Click(Row(() => label));
        test.Wait();
    }

    private void TestColours(HexPointerScript test)
    {
        /* Colours */

        ColorPicker Picker() => (ColorPicker)((StackPanel)editors["color"]).Children[0];

        // The colour is the last but one of what a label has, in a small window that is past the end of what is in sight
        Reveal("color");
        test.Check("colour: a colour has a picker and its four numbers", () =>
            editors["color"] is StackPanel { Children: [ColorPicker, StackPanel { Children.Count: 4 }] });
        test.Click(In(() => Picker().AlphaBounds, 0.5f, 0.97f));
        test.Drag(In(() => Picker().ShadeBounds, 0.3f, 0.4f), In(() => Picker().ShadeBounds, 0.95f, 0.95f));
        test.Wait();
        test.Check("colour: dragging through the square picks a shade, the strip how solid it is", () =>
            ((Label)label!).Color is { X: > 0.8f, Y: < 0.2f, Z: < 0.2f, W: > 0.9f } && Code().Contains("color: vec("));
        test.Click(In(() => Picker().HueBounds, 0.5f, 0.34f));
        test.Wait();
        test.Check("colour: the other strip picks the hue", () => ((Label)label!).Color is { X: < 0.2f, Y: > 0.8f, W: > 0.9f });
        test.Check("colour: and the numbers follow", () =>
            ((StackPanel)editors["color"]).Children[1].Children[1] is NumberBox green && Near(green.Value, ((Label)label!).Color!.Value.Y, 0.002f));
    }

    private void TestSectionsAndKeys(HexPointerScript test)
    {
        /* The sections of the properties, the tab key, and the tree keeping up with what is selected */

        Collapsible Section(string editor)
        {
            UIComponent? at = editors[editor];
            while (at is not null and not Collapsible)
                at = at.Parent;
            return (Collapsible)at!;
        }

        test.Check("sections: the properties are in sections by what they are about", () =>
            Section("intro").Title == "entrance" && Section("pos").Title == "placement" && Section("color").Title == "label");
        Reveal("intro");
        test.Click(In(() => Section("intro").HeaderBounds, 0.5f, 0.5f));
        test.WaitFor(() => Section("intro").Openness == 0.0f, 1.0f);
        test.Check("sections: a click on the bar folds it away, and only it", () =>
            Section("intro") is { Expanded: false, Openness: 0.0f } && Section("pos").Expanded);
        test.Run(() => Select(stack));
        test.Wait();
        test.Run(() => Select(label));
        test.Wait();
        test.Check("sections: and it stays folded for the next thing selected", () => !Section("intro").Expanded);
        Reveal("intro");
        test.Click(In(() => Section("intro").HeaderBounds, 0.5f, 0.5f));
        test.WaitFor(() => Section("intro").Openness == 1.0f, 1.0f);
        test.Check("sections: and out again", () => Section("intro") is { Expanded: true, Openness: 1.0f });

        Reveal("text");
        test.Click(At(() => editors["text"]));
        test.Wait();
        test.Run(() => compositor.FocusNext());
        test.Wait();
        test.Check("tab: the next box has all of what is in it picked out, to type over", () =>
            compositor.Focus is TextBox { Text.Length: > 0 } next && next != editors["text"] && next.Selection == next.Text);
        test.Run(() => compositor.FocusNext(-1));
        test.Wait();
        test.Check("tab: and shift goes back, all of it picked out again", () =>
            compositor.Focus == editors["text"] && ((TextBox)editors["text"]).Selection == ((TextBox)editors["text"]).Text);
        test.Run(() => compositor.Focus = null);

        test.Run(() => treeScroll.Offset = treeScroll.MaxOffset);
        test.Run(() => Select(stack));
        test.WaitFor(() => !treeReveal, 1.0f);
        test.Wait();
        test.Check("tree: selecting something scrolls its row into sight", () =>
            treeRows[stack!].Bounds is var row && treeScroll.Bounds.Contains(row.Center) && treeScroll.Bounds.Shrink(treeScroll.Padding).Max.Y >= row.Max.Y - 0.5f);
        test.Run(() => Select(label));
        test.Wait();

        Pick("Layout", () => "Move Up");
        test.Wait();
        test.Check("reorder: up puts it before its sibling, in the layout and in the code", () =>
            stack!.Children[0] == label && Code().IndexOf("label1") < Code().IndexOf("start_button ="));
        Pick("Layout", () => "Move Down");
        test.Wait();
        test.Check("reorder: and down puts it back", () => stack!.Children[1] == label);

        test.Click(Part("move_up"));
        test.Wait();
        test.Check("reorder: the buttons over the tree do the same", () => stack!.Children[0] == label && treeRows[label!].Selected);
        test.Click(Part("move_down"));
        test.Wait();
        test.Check("reorder: there and back", () => stack!.Children[1] == label);
        Pick("Layout", () => "Move Down");
        test.Wait();
        test.Check("reorder: the last one stays the last one", () => stack!.Children[^1] == label);

        test.RightClick(Row(() => label));
        PickFromMenu("Move Up");
        test.Wait();
        test.Check("reorder: the right click menu of a row moves it up", () => stack!.Children[0] == label);
        test.RightClick(Row(() => label));
        PickFromMenu("Move Down");
        test.Wait();
        test.Check("reorder: and down again", () => stack!.Children[1] == label);

        Pick("Layout", () => "Move Out");
        test.Wait();
        test.Check("reparent: out puts it after the container it was in", () =>
            label!.Parent == document.Module.Root
            && document.Module.Root.Children.ToList().IndexOf(label) == document.Module.Root.Children.ToList().IndexOf(stack!) + 1
            && document.Selected == label);
        Pick("Layout", () => "Move In");
        test.Wait();
        test.Check("reparent: and into puts it back in, at the end", () => label!.Parent == stack && stack!.Children[^1] == label && CheckRoundTrip(document));

        UIComponent? pasted = null;
        int rowsBefore = 0;
        test.Run(() => rowsBefore = treeRows.Count);
        Pick("Edit", () => "Copy");
        Pick("Edit", () => "Paste");
        test.Wait();
        test.Check("clipboard: a copy is pasted next to what's selected, with a name of its own, and selected", () =>
            (pasted = document.Selected) is Label && pasted != label && pasted.Parent == stack
            && document.NameOf(pasted) != document.NameOf(label!) && treeRows.Count == rowsBefore + 1);
        Pick("Edit", () => "Cut");
        test.Wait();
        test.Check("clipboard: cut takes it out again", () => pasted!.Parent is null && treeRows.Count == rowsBefore && document.Selected is null);
        test.Click(Row(() => label));
        test.Wait();

        Pick("Edit", () => "Delete");
        test.Wait();
        test.Check("delete: gone from the layout, the tree and the code", () =>
            label!.Parent is null && !treeRows.ContainsKey(label) && !Code().Contains("label1") && document.Selected is null && treeRows.Count == 3);
    }

    private void TestTemplates(HexPointerScript test)
    {
        /* Containers that are filled in by the program */

        test.Run(() => System.IO.File.WriteAllText(
            Path.Combine(testFolder, "item.hor"),
            "let row = compositor.button({ label: \"an item\", style: \"button_flat\", size: vec(200, 40) });\n"));

        test.Click(Row(() => stack));
        test.Wait();
        test.Run(() =>
        {
            ((TextBox)editors["template"]).Insert("item.hor");
            TypeInto((NumberBox)editors["preview_count"], "3");
        });
        test.Settle(0.3f);
        test.Check("templates: a container shows items made from its template", () => document.PreviewCount == 3 && stack!.Children.Count == 4);
        test.Check("templates: which are in neither the tree nor the code", () =>
            document.Walk().Count() == 3 && treeRows.Count == 3 && Code().Contains("template: \"item.hor\"") && !Code().Contains("an item"));

        test.Click(At(() => stack!.Children[^1]));
        test.Wait();
        test.Check("templates: clicking one of them selects the container", () => document.Selected == stack);
    }
}
