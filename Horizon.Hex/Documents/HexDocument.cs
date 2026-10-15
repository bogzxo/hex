using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

/// <summary>
/// One layout being edited. A module of the UI holding the real components, the names they go by in the code,
/// and the code itself, which is written out of the components and never kept.
/// What is on screen is the layout, so there is nothing for the two to disagree about. It's in parts, this one is the
/// layout's components, their names and what is selected, HexDocument.Code reads and writes it, HexDocument.History
/// is undo and the entrances, HexDocument.Arrange groups, copies and moves things about.
/// </summary>
internal sealed partial class HexDocument
{
    // Properties that aren't part of how a component is laid out: where it is in the tree (written as its parent),
    // other names for the same thing, and what only changes while the UI is in use.
    private static readonly HashSet<string> Unwritten = ["parent", "spr_scale", "on_press", "offset"];

    // How many items a container is ever shown with, whatever its layout asks for
    private const int MAX_PREVIEW = 64;

    // How many steps back there are to take
    private const int MAX_HISTORY = 200;

    private readonly HexNames names = new();

    // What stands in for the items a program will make. Built from the templates the containers of the layout
    // name, shown in the canvas and nowhere else. They aren't part of the layout and never get written.
    private readonly HashSet<UIComponent> previews = [];

    // What the layout was before each change and what it was before each of those was undone, as the code that
    // builds it. The code is the whole of a layout, so going back is building it again from what it was.
    private readonly List<string> undone = [], redone = [];
    private string recorded = string.Empty;

    public string Name { get; set; }

    /// <summary>The file the layout was loaded from or last saved to, null for one that has never been either.</summary>
    public string? Path { get; set; }

    /// <summary>The code of the layout as it was last opened or saved, which is what there is to compare it to.</summary>
    public string SavedCode { get; set; } = string.Empty;

    /// <summary>Whether the layout has been changed since it was opened or saved. The editor keeps this up, see HexScene.UpdateState.</summary>
    public bool Modified { get; set; }

    /// <summary>Where templates are looked for while the layout has no file of its own to be next to.</summary>
    public string FallbackDirectory { get; set; } = string.Empty;

    /// <summary>The module the layout lives in, shown in the canvas of the editor.</summary>
    public UIModule Module { get; }

    /// <summary>
    /// Whether this is a file of code and not a layout (a data file, a language, a skin), which is edited as the text it
    /// is in <see cref="Editor"/> and has nothing in its module.
    /// </summary>
    public bool IsText { get; init; }

    /// <summary>
    /// The code, to edit by hand. For a text file it is the whole of it. For a layout it is what the layout is written
    /// as, which a change to the layout writes again, and which goes back into the layout when it is applied (see
    /// <see cref="CodeEdited"/>). Every document has its own, so its undo history goes with it.
    /// </summary>
    public CodeEditor Editor { get; } = new() { Fill = UIFill.Both };

    /// <summary>Whether the file had its lines ended with \r\n, which saving keeps. The code itself is always \n.</summary>
    public bool CrLf { get; set; }

    /// <summary>Whether the file had its lines ended with \r\n when it was last read or saved, swapping them is a change to save.</summary>
    public bool SavedCrLf { get; set; }

    /// <summary>Whether a layout is shown as its designer or as its code. A file of code is only ever its code.</summary>
    public HexView View { get; set; } = HexView.Design;

    /// <summary>Which view of an extension the file is shown in, while <see cref="View"/> is Extension.</summary>
    public string? ExtensionView { get; set; }

    /// <summary>Whether the code of a layout was changed by hand since it was last written out of the layout, and not applied yet.</summary>
    public bool CodeEdited { get; set; }

    // What is selected, in the order it was picked: the last one is the one the inspector shows
    private readonly List<UIComponent> selection = [];

    /// <summary>Everything that is selected, the one picked last at the end.</summary>
    public IReadOnlyList<UIComponent> Selection => selection;

    /// <summary>The one component the inspector shows, the last one picked. Setting it selects that and nothing else.</summary>
    public UIComponent? Selected
    {
        get => selection.Count > 0 ? selection[^1] : null;
        set
        {
            selection.Clear();
            if (value is not null)
                selection.Add(value);
        }
    }

    public bool IsSelected(UIComponent component) => selection.Contains(component);

    /// <summary>Selects a component on top of what is selected, or lets go of it if it already was (ctrl or shift click).</summary>
    public void ToggleSelected(UIComponent component)
    {
        if (!selection.Remove(component))
            selection.Add(component);
    }

    /// <summary>
    /// The selected components that aren't inside of another selected one, in the order they are in the layout: what
    /// gets moved, deleted or grouped. Dragging a group and something inside of it would move that thing twice.
    /// </summary>
    public List<UIComponent> TopSelection()
    {
        var top = new List<UIComponent>(selection.Count);
        foreach (UIComponent component in selection)
        {
            if (!HasSelectedAncestor(component))
                top.Add(component);
        }

        // One of them (a drag, every tick of it) is in the order already, more go by where they are in the layout, which
        // was a walk of the lot and an IndexOf through it for every one
        if (top.Count > 1)
        {
            var order = new Dictionary<UIComponent, int>();
            foreach (var (component, _) in Walk())
                order[component] = order.Count;
            top.Sort((a, b) => order.GetValueOrDefault(a, int.MaxValue).CompareTo(order.GetValueOrDefault(b, int.MaxValue)));
        }

        return top;
    }

    private bool HasSelectedAncestor(UIComponent component)
    {
        for (UIComponent? at = component.Parent; at is not null; at = at.Parent)
        {
            if (selection.Contains(at))
                return true;
        }

        return false;
    }

    /* What the layout was made for */

    /// <summary>The screen the layout says it was made for, null if it says nothing (and gets whatever screen it's shown on).</summary>
    public Vector2? Design => Module.DesignSize;

    /// <summary>How the layout goes onto a screen of another shape, see <see cref="UIFit"/>.</summary>
    public UIFit Fit => Module.Fit;

    /// <summary>Says what the layout is made for, which goes into the file as compositor.design (null takes it out).</summary>
    public void SetDesign(Vector2? size, UIFit fit)
    {
        Module.DesignSize = size;
        Module.Fit = fit;
    }

    /// <summary>How many stand-in items are being shown, see <see cref="RefreshPreviews"/>.</summary>
    public int PreviewCount => previews.Count;

    public bool CanUndo => undone.Count > 0;
    public bool CanRedo => redone.Count > 0;

    /// <summary>How many changes there are to undo.</summary>
    public int UndoCount => undone.Count;

    public HexDocument(string name, UIModule module)
    {
        Name = name;
        Module = module;

        // A layout that is being edited is looked at, not used. Clicking a button in it selects the button.
        Module.Interactive = false;
    }

    /// <summary>Every component of the layout with how deep in the tree it is, parents before their children.</summary>
    public IEnumerable<(UIComponent Component, int Depth)> Walk()
    {
        var found = new List<(UIComponent, int)>();
        foreach (var root in Module.Root.Children)
            Collect(root, 0, found);

        return found;
    }

    private void Collect(UIComponent component, int depth, List<(UIComponent, int)> found)
    {
        // Stand-ins aren't components of the layout, and neither is anything inside of them
        if (previews.Contains(component))
            return;

        found.Add((component, depth));
        foreach (var child in component.Children)
            Collect(child, depth + 1, found);
    }

    /// <summary>
    /// The component of the layout something in the canvas belongs to. Itself, or for a stand-in item the
    /// container it stands in.
    /// </summary>
    public UIComponent? OwnerOf(UIComponent? component)
    {
        UIComponent? owner = component;

        for (UIComponent? at = component; at is not null && at != Module.Root; at = at.Parent)
        {
            if (previews.Contains(at))
                owner = at.Parent;
        }

        return owner == Module.Root ? null : owner;
    }

    /// <summary>The name a component goes by in the code.</summary>
    /// <summary>The component that goes by a name in the code, null if none does.</summary>
    public UIComponent? Find(string name) => names.Find(name);

    public string NameOf(UIComponent component)
    {
        if (!names.TryGetName(component, out string name))
        {
            name = FreeName(UIModule.KindOf(component) ?? "component");
            names.Set(component, name);
        }

        return name;
    }

    /// <summary>
    /// Gives a component another name. It has to be one a variable can have and nothing else in the layout
    /// can have it already.
    /// </summary>
    public bool Rename(UIComponent component, string name)
    {
        if (!HIDLWriter.IsIdentifier(name) || name == "compositor")
            return false;

        if (names.Find(name) is { } other && other != component)
            return false;

        names.Set(component, name);
        return true;
    }

    /// <summary>
    /// Adds a new component of a kind (see <see cref="UIModule.Kinds"/>) to the layout.
    /// </summary>
    /// <param name="parent">What to put it in, null for the screen itself.</param>
    public UIComponent? Add(string kind, UIComponent? parent)
    {
        if (UIModule.CreateDetached(kind) is not { } component)
            return null;

        var backdrop = new System.Numerics.Vector4(0.1f, 0.12f, 0.17f, 0.9f);

        // Something to look at: an empty label or a panel without a size can't be seen, let alone clicked.
        switch (component)
        {
            case Label label: label.Text = "Label"; break;
            case Button button: button.Label = "Button"; break;
            case ToggleButton toggle: toggle.Label = "Toggle"; break;
            case Selector selector: selector.Options = ["one", "two", "three"]; break;
            case Dropdown dropdown: dropdown.Options = ["one", "two", "three"]; break;
            case ListBox list: list.Items = ["one", "two", "three", "four"]; break;
            case Divider divider: divider.Size = new System.Numerics.Vector2(240, 0); break;
            case Spacer spacer: spacer.Space = 24; break;
            case ScrollPanel scroll: scroll.Size = new System.Numerics.Vector2(240, 160); scroll.Color = backdrop; break;
            case Collapsible section:
                section.Title = "Section";
                section.Size = new System.Numerics.Vector2(260, 0);
                section.Padding = new UIEdges(12);
                section.Color = backdrop;
                break;
            case StackPanel stack: stack.Padding = new UIEdges(16); stack.Color = backdrop; break;
            case GridPanel grid: grid.Padding = new UIEdges(16); grid.Color = backdrop; break;
            case TabPanel tabs:
                tabs.Size = new System.Numerics.Vector2(480, 300);
                tabs.Tabs = ["One", "Two"];
                break;
            case Panel panel: panel.Size = new System.Numerics.Vector2(240, 160); panel.Color = backdrop; break;
        }

        (parent ?? Module.Root).Add(component);
        NameOf(component);

        // Tabs with nothing to switch between are a strip of nothing, they come with a page a tab
        if (component is TabPanel pages)
        {
            for (int i = 0; i < pages.Tabs.Length; i++)
                Add("stack", pages);
        }

        return component;
    }

    /// <summary>Takes a component out of the layout, along with everything inside of it.</summary>
    public void Remove(UIComponent component)
    {
        var gone = new List<(UIComponent, int)>();
        Collect(component, 0, gone);

        foreach (var (removed, _) in gone)
        {
            names.Remove(removed);
            selection.Remove(removed);
        }

        component.Parent?.Remove(component);
    }

    /// <summary>Empties the layout.</summary>
    public void Clear()
    {
        foreach (var root in Module.Root.Children.ToArray())
            Module.Root.Remove(root);

        names.Clear();
        previews.Clear();
        selection.Clear();

        // What the layout is made for is part of the code, which is about to be built again
        Module.DesignSize = null;
        Module.Fit = UIFit.Contain;
    }

    private string FreeName(string kind, ICollection<string>? taken = null)
    {
        for (int number = 1; ; number++)
        {
            string name = $"{kind}{number}";
            if (!names.Has(name) && taken?.Contains(name) != true)
                return name;
        }
    }
}
