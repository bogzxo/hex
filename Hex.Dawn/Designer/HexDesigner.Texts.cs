using System.Numerics;

using Horizon.Content;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Editing what a component says right there on the canvas. A double click on it (or Enter with it selected) puts a text
// box over it, Enter or a click somewhere else keeps what was typed and Escape leaves it as it was. A text that's a key
// of the game's language ("@main_menu.quit") is changed in the language's file (Languages), anything else is the
// property set the way the inspector sets it, so the code is written again and Ctrl+Z takes it back.
internal sealed partial class HexDesigner
{
    /// <summary>A text of a component, the property it's in, and which item of it for a list (the tabs, the options), -1 for the whole property.</summary>
    internal readonly record struct TextSlot(UIComponent Component, string Property, int Item, string Text)
    {
        public bool IsKey => Localisation.IsKey(Text);
        public string Key => Text[1..];
    }

    // How soon a second click has to come to make a double click, and how far from the first it can be, in pixels
    private const long DOUBLE_CLICK_MS = 400;
    private const float DOUBLE_CLICK_RANGE = 6.0f;

    // How small the box over a component gets, a tiny label still wants room to type into
    private const float TEXT_EDITOR_WIDTH = 160.0f, TEXT_EDITOR_HEIGHT = 28.0f;

    // The last click on the canvas that could be the first half of a double click, and where the press going on started
    private long? lastClickAt;
    private Vector2 lastClickWhere;
    private Vector2? clickPressedAt;

    // A double click waiting for the tick after, where it was in the world and in the layout
    private (Vector2 World, Vector2 InLayout)? doubleClicked;

    // The box over the canvas while a text is being edited, what it's the text of, and what it said when it opened
    internal TextBox? textEditor;
    internal TextSlot? textEditing;
    private HexDocument? textEditingIn;
    private string textEditingWas = string.Empty;

    /// <summary>
    /// The text a component shows, which a double click edits. A point of the layout picks the tab under it, and for a
    /// window or a collapsible only the bar along its top is its text, the rest of it is whatever is inside. Null for a
    /// component that says nothing (a panel, a slider) or a point on the part of one that isn't its text.
    /// </summary>
    internal static TextSlot? TextOf(UIComponent component, Vector2? at = null) => component switch
    {
        Label => Slot(component, "text"),
        Button or ToggleButton => Slot(component, "label"),
        Readout => Slot(component, "text"),

        // A box with nothing typed in it shows what it's for, which is the text it has as far as anybody looking can tell
        TextBox { Text.Length: 0, Placeholder.Length: > 0 } => Slot(component, "placeholder"),
        TextBox => Slot(component, "text"),

        Dropdown dropdown => dropdown.Options.Length > 0 ? Slot(component, "options", dropdown.Index) : null,
        Selector selector => selector.Options.Length > 0 ? Slot(component, "options", selector.Index) : null,

        // The tab under the point, or the one that's open when it came from a key and not a click
        TabPanel tabs when at is { } point => tabs.TabAt(point) is >= 0 and var tab ? Slot(component, "tabs", tab) : null,
        TabPanel tabs => tabs.Children.Count > 0 ? Slot(component, "tabs", tabs.Selected) : null,

        Collapsible collapsible when at is null || collapsible.HeaderBounds.Contains(at.Value) => Slot(component, "title"),
        WindowPanel window when at is null || window.TitleBounds.Contains(at.Value) => Slot(component, "title"),
        TitleBar => Slot(component, "title"),
        _ => null
    };

    // A property's text, or one item of a list of them. A tab past the end of the names is unnamed and edited as nothing
    private static TextSlot? Slot(UIComponent component, string property, int item = -1)
    {
        if (!HexDocument.TryRead(component, property, out IRuntimeValue value))
            return null;

        if (item < 0)
            return value is StringValue text ? new TextSlot(component, property, -1, text.Value) : null;

        return value is ListValue list
            ? new TextSlot(component, property, item, item < list.Count && list[item] is StringValue said ? said.Value : string.Empty)
            : null;
    }

    /// <summary>Where a text is drawn in the layout, the tab's own box for a tab and the title bar for a window.</summary>
    private static UIRect TextArea(TextSlot slot) => slot.Component switch
    {
        TabPanel tabs when slot.Item >= 0 => tabs.TabBounds(slot.Item),
        Collapsible collapsible => collapsible.HeaderBounds,
        WindowPanel window => window.TitleBounds,
        _ => slot.Component.Bounds
    };

    /// <summary>
    /// The text under a point of the canvas, of whatever is there or the nearest thing it's inside of that has one. A
    /// button in a stack in a window is the button's, the empty part of the window's body nobody's.
    /// </summary>
    private TextSlot? TextAt(Vector2 world, Vector2 inLayout)
    {
        for (UIComponent? at = document.OwnerOf(document.Module.FindAt(world)); at is not null && at != document.Module.Root; at = at.Parent)
        {
            if (TextOf(at, inLayout) is { } slot)
                return slot;
        }

        return null;
    }

    /// <summary>
    /// Helper method to tell a double click on the canvas from the clicks it's made of, a press and a release without a
    /// drag and another soon after in the same place. The first click does what a click does and so does the second,
    /// which goes a level into a group, and only once that's done (the tick after) is what's under it edited. That way
    /// a double click on something with no text is still the two clicks it always was, and a press that turns into a
    /// drag is never one, whatever came before it.
    /// </summary>
    private void TrackClicks(UIPointer pointer, bool pressed, bool released, bool overCanvas, Vector2 inLayout)
    {
        if (doubleClicked is var (world, at))
        {
            doubleClicked = null;
            if (overCanvas && TextAt(world, at) is { } slot)
                BeginTextEdit(slot);
        }

        if (pressed)
        {
            // A press anywhere else on the canvas keeps what was typed, the way clicking away from a text box does
            if (overCanvas && textEditor is not null)
                CommitTextEdit();

            clickPressedAt = overCanvas ? pointer.Position : null;
            return;
        }

        if (!released || clickPressedAt is not { } from)
            return;

        clickPressedAt = null;
        long now = System.Environment.TickCount64;
        bool still = Vector2.Distance(from, pointer.Position) <= DOUBLE_CLICK_RANGE;
        bool twice = still && lastClickAt is { } before && now - before <= DOUBLE_CLICK_MS && Vector2.Distance(from, lastClickWhere) <= DOUBLE_CLICK_RANGE;

        // A third click is the first of the next double click
        lastClickAt = still && !twice ? now : null;
        lastClickWhere = from;

        if (twice)
            doubleClicked = (pointer.Position, inLayout);
    }

    /// <summary>Enter in the designer, the text of what's selected edited over it, the same as a double click on it.</summary>
    internal void EditSelectedText()
    {
        // Enter answers a question that's up, it isn't for the layout behind it
        if (!LayoutTools || chrome.Dialog is not null || chrome.Popup is not null || document.Selected is not { } selected)
            return;

        if (TextOf(selected) is { } slot)
            BeginTextEdit(slot);
        else
            context.Notices.Say(context.Strings.Said("designer_texts.nothing_to_edit", ("name", document.NameOf(selected))));
    }

    /// <summary>
    /// Puts a text box over a text on the canvas with the keyboard in it, everything selected so typing replaces it. A key
    /// of the language is shown as what it says, the key faint behind it once that's rubbed out.
    /// </summary>
    internal void BeginTextEdit(TextSlot slot)
    {
        if (textEditor is not null)
            CommitTextEdit();

        Select(slot.Component);
        if (slot.Component is TabPanel tabs && slot.Item >= 0)
            tabs.Selected = slot.Item;

        var box = layout.Instantiate(TemplateOf(EditorTemplate.Text), chrome.Root).Get<TextBox>("box");
        box.ZOffset = 20;
        box.MaxLength = 4000;
        box.Text = slot.IsKey ? (Localisation.Has(slot.Key) ? Localisation.Get(slot.Key) : string.Empty) : slot.Text;
        box.Placeholder = slot.IsKey ? slot.Text : string.Empty;
        box.OnSubmitted = _ => CommitTextEdit();

        textEditor = box;
        textEditing = slot;
        textEditingIn = document;
        textEditingWas = box.Text;

        PlaceTextEditor();
        box.Focus();
        box.SelectAll();

        context.Notices.Say(slot.IsKey
            ? context.Strings.Said("designer_texts.editing_key", ("key", slot.Key), ("file", Localisation.Language + ".hor"))
            : context.Strings.Said("designer_texts.editing", ("property", slot.Property)));
    }

    /// <summary>Helper method to keep the box over the text it edits, however the canvas is zoomed and panned about meanwhile.</summary>
    private void PlaceTextEditor()
    {
        if (textEditor is not { } box || textEditing is not { } slot)
            return;

        UIRect area = TextArea(slot);
        Vector2 a = chrome.ToLocal(document.Module.ToWorld(area.Min));
        Vector2 b = chrome.ToLocal(document.Module.ToWorld(area.Max));

        UIComponent root = chrome.Root;
        box.Anchor = Origin.Center;
        box.Pivot = null;
        box.Size = new Vector2(MathF.Max(MathF.Abs(b.X - a.X), TEXT_EDITOR_WIDTH), TEXT_EDITOR_HEIGHT);
        box.Position = (a + b) * 0.5f - root.Bounds.Shrink(root.Padding).Center;
    }

    /// <summary>
    /// Called every tick after the keys, to put the box where its text is and see whether it's done with. Escape leaves
    /// the text as it was, the keyboard gone somewhere else (a click on the inspector, Tab) keeps what was typed. The
    /// layout going, or the component, takes the box with it.
    /// </summary>
    internal void UpdateTextEdit()
    {
        if (textEditor is not { } box || textEditing is not { } slot)
            return;

        if (document != textEditingIn || !LayoutTools || slot.Component.Module != document.Module)
        {
            CloseTextEdit();
            return;
        }

        if (!box.IsFocused)
        {
            CommitTextEdit();
            return;
        }

        // Seen before the UI has its go at the key, which only takes the keyboard away from the box
        if (context.Keyboard.WasPressed(Key.Escape))
        {
            CancelTextEdit();
            return;
        }

        PlaceTextEditor();
    }

    /// <summary>What was typed over a component kept, in the language's file for a key and in the component otherwise.</summary>
    internal void CommitTextEdit()
    {
        if (textEditor is not { } box || textEditing is not { } slot)
            return;

        string typed = box.Text;
        bool changed = typed != textEditingWas;
        CloseTextEdit();

        if (!changed)
            return;

        if (slot.IsKey)
            WriteKeyText(slot.Key, typed);
        else
            SetText(slot, typed);
    }

    /// <summary>Escape, the box put away and the text left as it was.</summary>
    internal void CancelTextEdit()
    {
        if (textEditor is null)
            return;

        CloseTextEdit();
        context.Notices.Say(context.Strings.Said("designer_texts.left"));
    }

    private void CloseTextEdit()
    {
        if (textEditor is { } box)
        {
            if (box.IsFocused)
                box.Unfocus();
            box.Parent?.Remove(box);
        }

        textEditor = null;
        textEditing = null;
        textEditingIn = null;
    }

    /// <summary>
    /// Helper method to set a text that isn't a key on the component it's from, the way the inspector sets a property,
    /// so the code is written again and the change is a step to undo. Only that component, whatever else is selected.
    /// </summary>
    private void SetText(TextSlot slot, string typed)
    {
        IRuntimeValue value = new StringValue(typed);
        if (slot.Item >= 0)
        {
            // The list with the one item changed, a tab with no name yet given the names before it as nothing
            var texts = HexDocument.TryRead(slot.Component, slot.Property, out IRuntimeValue had) && had is ListValue list
                ? list.Items.Select(item => item is StringValue text ? text.Value : string.Empty).ToList()
                : [];
            while (texts.Count <= slot.Item)
                texts.Add(string.Empty);

            texts[slot.Item] = typed;
            value = new ListValue([.. texts.Select(text => (IRuntimeValue)new StringValue(text))]);
        }

        if (HexDocument.Write(slot.Component, slot.Property, value) is { } problem)
        {
            context.Notices.Say(problem, error: true);
            return;
        }

        codeDirty = inspectorDirty = true;
        context.Notices.Say(context.Strings.Said("designer_texts.changed", ("name", document.NameOf(slot.Component)), ("property", slot.Property), ("text", typed)));
    }
}
