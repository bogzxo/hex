using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL.Library;
using Horizon.HIDL.Preprocessing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.Hex;

// What extensions put on screen. A view of a file is beside its code and table, a button for it at the end of the tabs,
// F7 going round them, and it comes up by itself when a file that says the #define it is for is opened. A page of the
// panel is a tab next to the problems, the output and the console. A window is a little window over everything. Every
// one of them a layout out of the extension's folder, its handlers called by the extension (a second at the most, the
// extension switched off when one throws).
internal sealed partial class HexScene
{
    private const string VIEW_BUTTON = "view_button.hor";

    private readonly List<ExtensionViewKind> viewKinds = [];
    private readonly Dictionary<(HexDocument, string), ExtensionUI> viewsMade = [];
    private readonly List<ExtensionUI> panelsMade = [], windowsMade = [];

    // The panel the views of extensions go into, in the middle where the code is
    private Panel extensionViews = null!;

    // The buttons of the views of the file on screen, at the end of the tabs, by the view they're for
    private readonly List<(string Id, Button Button)> viewButtons = [];

    private void BuildExtensionViews()
    {
        extensionViews = layout.Get<Panel>("ext_view");
        extensionViews.Visible = false;
    }

    /// <summary>What the defines of a file are, its #define lines, read off its text (kept against the text).</summary>
    private IReadOnlyDictionary<string, string> DefinesOf(HexDocument of)
    {
        string text = of.Editor.Text;
        if (!ReferenceEquals(text, definesOf.Text))
            definesOf = (text, HIDLPreprocessor.Read(text));
        return definesOf.Defines;
    }

    private (string? Text, IReadOnlyDictionary<string, string> Defines) definesOf = (null, new Dictionary<string, string>());

    /// <summary>The views of extensions a document is offered, the ones for a #define it has and the ones for any file.</summary>
    private List<ExtensionViewKind> ViewsFor(HexDocument of)
    {
        var offered = new List<ExtensionViewKind>();
        if (!of.IsText)
            return offered;

        IReadOnlyDictionary<string, string> defines = DefinesOf(of);
        foreach (ExtensionViewKind kind in viewKinds)
        {
            if (kind.Owner.Enabled && kind.Owner.Failed is null && (kind.When is null || defines.ContainsKey(kind.When)))
                offered.Add(kind);
        }

        return offered;
    }

    /// <summary>The view of an extension that is on screen, null when it's the code, the table or the designer.</summary>
    private ExtensionUI? ShownView => document.View == HexView.Extension && document.ExtensionView is { } id && viewsMade.TryGetValue((document, id), out ExtensionUI? made) ? made : null;

    /// <summary>
    /// Helper method for ApplyView, the views of extensions put up or away for the document on screen, their buttons with
    /// them. A view is made the first time it is shown for a document and kept until the document is closed.
    /// </summary>
    private void ApplyExtensionViews()
    {
        List<ExtensionViewKind> offered = ViewsFor(document);
        var views = layout.Get<StackPanel>("views");

        foreach (var (_, button) in viewButtons)
            views.Remove(button);
        viewButtons.Clear();

        foreach (ExtensionViewKind kind in offered)
        {
            var button = layout.Instantiate(VIEW_BUTTON, views).Get<Button>("view_button");
            button.Label = kind.Title;
            button.Icon = kind.Icon;
            button.Tooltip = $"{kind.Title}, a view of {kind.Owner.Name} (F7 goes round the views)";
            button.Selected = document.View == HexView.Extension && document.ExtensionView == kind.Id;
            string id = kind.Id;
            button.OnPressed = () => SwitchToExtensionView(id);
            viewButtons.Add((kind.Id, button));
        }

        if (offered.Count > 0)
            views.Visible = true;

        // The one of the document on screen, if it is on one of them
        bool showing = document.View == HexView.Extension && document.ExtensionView is { } wanted && offered.Any(kind => kind.Id == wanted);
        if (document.View == HexView.Extension && !showing)
        {
            document.View = HexView.Code;
            document.ExtensionView = null;
        }

        foreach (ExtensionUI made in viewsMade.Values)
            made.Root.Visible = false;

        extensionViews.Visible = showing;
        ExtensionViewKind? shown = showing ? offered.First(kind => kind.Id == document.ExtensionView) : null;
        ApplyViewSides(shown);
        if (shown is null)
            return;

        if (!viewsMade.TryGetValue((document, shown.Id), out ExtensionUI? view))
            view = MakeView(shown, document);

        if (view is not null)
            view.Root.Visible = true;
    }

    // The view whose sides were last put on screen, so a side somebody brings back by hand while it's up stays back
    private ExtensionViewKind? sidesFor;

    /// <summary>
    /// Helper method to put Hex's sides away for a view that doesn't want them, as it comes on screen, and back the way
    /// the settings have them as it goes. The settings themselves aren't touched, it's the view's say only while it's up.
    /// </summary>
    private void ApplyViewSides(ExtensionViewKind? shown)
    {
        if (shown == sidesFor)
            return;

        sidesFor = shown;
        left.Visible = !settings.SideBarHidden && (shown?.Left ?? true);
        right.Visible = !settings.PropertiesHidden && (shown?.Right ?? true);
    }

    /// <summary>Makes a view of a file out of its layout, and hands it to the extension's open(ui, file).</summary>
    private ExtensionUI? MakeView(ExtensionViewKind kind, HexDocument of)
    {
        var holder = extensionViews.Add(new Panel { Fill = UIFill.Both });
        // What it has room for is the panel the views go in, the holder is only as big as what's in it
        ExtensionUI? made = MakeUI(kind.Owner, "view", kind.Layout, holder, of.Path is { } path ? Path.GetFullPath(path) : null, null, () => extensionViews.Bounds.Size);
        if (made is null)
        {
            extensionViews.Remove(holder);
            return null;
        }

        viewsMade[(of, kind.Id)] = made;
        if (kind.Open is { } open && !kind.Owner.Invoke(open, made, made.File is { } file ? new StringValue(file) : Values.Null))
            ExtensionFailed(kind.Owner);

        return made;
    }

    /// <summary>
    /// Helper method to make a layout of an extension's folder inside of something, its handlers the extension's to call.
    /// Null, said, if the layout doesn't build.
    /// </summary>
    private ExtensionUI? MakeUI(HexExtension owner, string kind, string layoutFile, UIComponent into, string? file, Action? close, Func<Vector2>? room = null)
    {
        string path = Path.GetFullPath(Path.Combine(owner.Folder, layoutFile));
        UILayout made;
        try
        {
            made = UILayout.Load(chrome, path, into);
        }
        catch (Exception e)
        {
            Notify($"{owner.Name}: {Path.GetFileName(path)} didn't build, {e.Message}", error: true);
            return null;
        }

        into.ScriptCaller = (handler, arguments) =>
        {
            if (owner.Failed is null && !owner.Invoke(handler, arguments))
                ExtensionFailed(owner);
        };

        return new ExtensionUI(owner, kind, made, into, file, close, room);
    }

    /// <summary>Shows a view of an extension for the document on screen.</summary>
    private void SwitchToExtensionView(string id)
    {
        if (!document.IsText)
            return;

        document.View = HexView.Extension;
        document.ExtensionView = id;
        ApplyView();
    }

    /// <summary>A page of the panel under the editor, made out of an extension's layout.</summary>
    private ExtensionUI? MakePanelPage(HexExtension owner, string title, string icon, string layoutFile)
    {
        var page = bottom.Add(new Panel { Fill = UIFill.Both });
        ExtensionUI? made = null;
        made = MakeUI(owner, "panel page", layoutFile, page, null, () =>
        {
            bottom.Remove(page);
            if (made is not null) { made.Closed = true; panelsMade.Remove(made); }
            problemsDirty = true;
        });

        if (made is null)
        {
            bottom.Remove(page);
            return null;
        }

        panelTitles[made] = icon.Length > 0 ? $"$({icon}) {title.ToUpperInvariant()}" : title.ToUpperInvariant();
        panelsMade.Add(made);
        problemsDirty = true;
        return made;
    }

    // What the pages of the panel extensions made say on their tabs
    private readonly Dictionary<ExtensionUI, string> panelTitles = [];

    /// <summary>The titles of the pages of the panel, Hex's three and then the extensions'.</summary>
    private string[] PanelTabs(string problemsTitle) =>
        [problemsTitle, Said("panel.output"), Said("panel.console"), .. panelsMade.Select(page => panelTitles[page])];

    /// <summary>A little window over everything, made out of an extension's layout.</summary>
    private ExtensionUI? MakeWindow(HexExtension owner, string title, string layoutFile, Vector2 size)
    {
        var window = chrome.Root.Add(new WindowPanel { Title = title, Size = size, Closable = true, ResizeGrip = true, Padding = new UIEdges(8) });
        window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;

        ExtensionUI? made = null;
        void Close()
        {
            window.Parent?.Remove(window);
            if (made is not null) { made.Closed = true; windowsMade.Remove(made); }
        }

        made = MakeUI(owner, "window", layoutFile, window, null, Close);
        if (made is null)
        {
            window.Parent?.Remove(window);
            return null;
        }

        window.OnClosed = Close;
        windowsMade.Add(made);
        return made;
    }

    /// <summary>
    /// Every tick, the views, pages and windows of extensions that are on screen told how long it was (on_tick), and the
    /// keys handed to the view on screen (on_key) while nothing is being typed into.
    /// </summary>
    private void UpdateExtensionUI(float dt)
    {
        if (viewsMade.Count == 0 && panelsMade.Count == 0 && windowsMade.Count == 0)
            return;

        // Made only when something ticks, a view with nothing going on costs nothing a frame
        NumberValue? number = null;
        if (ShownView is { } view)
            TickUI(view, dt, ref number);

        for (int i = panelsMade.Count - 1; i >= 0; i--)
        {
            if (panelsMade[i] is { } page && page.Root.Visible && bottom.Visible && bottom.SelectedPage == page.Root)
                TickUI(page, dt, ref number);
        }

        for (int i = windowsMade.Count - 1; i >= 0; i--)
        {
            if (windowsMade[i] is { } window && window.Root.Visible)
                TickUI(window, dt, ref number);
        }

        // The keys go to the view on screen when nothing has the keyboard, or one of its own drawings does
        if (ShownView is { Key: not null } keyed && (compositor.Focus is null || (compositor.Focus is DrawingPanel && IsInside(compositor.Focus, keyed.Root))))
        {
            var keyboard = Engine.Input.Keyboard;
            foreach (var (key, name) in ExtensionKeys)
            {
                if (keyboard.WasPressed(key) && !keyboard.Control)
                    ExtensionKey(name);
            }
        }
    }

    /// <summary>A UI on screen told the room it has when that changed (on_resize), then how long the tick was (on_tick).</summary>
    private void TickUI(ExtensionUI ui, float dt, ref NumberValue? number)
    {
        // One that went wrong is said about the once and left alone after, not every tick
        if (ui.Owner.Failed is not null)
            return;

        if (ui.Resize is { } resize && ui.Room is var room && room != ui.Sized && room.X > 0 && room.Y > 0)
        {
            ui.Sized = room;
            if (!ui.Owner.Invoke(resize, ui, new Vector2Value(room)))
            {
                ExtensionFailed(ui.Owner);
                return;
            }
        }

        if (ui.Tick is { } tick && !ui.Owner.Invoke(tick, ui, number ??= new NumberValue(dt)))
            ExtensionFailed(ui.Owner);
    }

    private static readonly (Key Key, string Name)[] ExtensionKeys =
    [
        (Key.Left, "left"), (Key.Right, "right"), (Key.Up, "up"), (Key.Down, "down"), (Key.Home, "home"), (Key.End, "end"),
        (Key.Space, "space"), (Key.Delete, "delete"), (Key.Enter, "enter"), (Key.PageUp, "page_up"), (Key.PageDown, "page_down")
    ];

    /// <summary>
    /// Hands a key to the view of an extension on screen, true if it took it (its on_key handed back true), which is how
    /// Ctrl+Z undoes the view's own changes rather than the code's.
    /// </summary>
    private bool ExtensionKey(string name)
    {
        if (ShownView is not { Key: { } handler } view || view.Owner.Failed is not null)
            return false;

        if (!view.Owner.Invoke(handler, [view, new StringValue(name)], out IRuntimeValue taken))
        {
            ExtensionFailed(view.Owner);
            return false;
        }

        return taken is BooleanValue { Value: true };
    }

    /// <summary>Takes away what a document had made for it by extensions, as it closes.</summary>
    private void ForgetExtensionViews(HexDocument closing)
    {
        foreach (var key in viewsMade.Keys.Where(key => key.Item1 == closing).ToList())
        {
            ExtensionUI made = viewsMade[key];
            made.Closed = true;
            extensionViews.Remove(made.Root);
            viewsMade.Remove(key);
        }
    }

    /// <summary>A file that was just opened, put on the view an extension has for its #define, if one has.</summary>
    private void OpenOnExtensionView(HexDocument opened)
    {
        IReadOnlyDictionary<string, string> defines = DefinesOf(opened);
        ExtensionViewKind? wanted = viewKinds.FirstOrDefault(kind => kind.When is { } when && defines.ContainsKey(when) && kind.Owner.Enabled && kind.Owner.Failed is null);
        if (wanted is not null && opened == document)
            SwitchToExtensionView(wanted.Id);
    }
}
