using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL.Library;
using Horizon.HIDL.Preprocessing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Hex.Dawn;

// What extensions put on screen. A view of a file is beside its code and table, a button for it at the end of the tabs,
// F7 going round them, and it comes up by itself when a file that says the #define it is for is opened. A page of the
// panel is a tab next to the problems, the output and the console. A window is a little window over everything. Every
// one of them a layout out of the extension's folder, its handlers called by the extension (a second at the most, the
// extension switched off when one throws).
internal sealed partial class HexScene : IExtensionScreens
{
    // What an extension put in the title bar after Hex, in brackets, and which one it was
    private string titleNote = string.Empty;
    private HexExtension? titleNoteBy;

    void IExtensionScreens.AddView(ExtensionViewKind kind)
    {
        viewKinds.RemoveAll(other => other.Id == kind.Id);
        viewKinds.Add(kind);
        tabsDirty = true;
    }

    UIScriptedLayout? IExtensionScreens.MakePanelPage(HexExtension owner, string title, string icon, string layoutFile) => MakePanelPage(owner, title, icon, layoutFile);
    UIScriptedLayout? IExtensionScreens.MakeWindow(HexExtension owner, string title, string layoutFile, Vector2 size) => MakeWindow(owner, title, layoutFile, size);

    void IExtensionScreens.TitleNote(HexExtension by, string text)
    {
        titleNote = text;
        titleNoteBy = by;
        tabsDirty = true;
    }

    void IExtensionScreens.ForgetTitleNote(HexExtension by)
    {
        if (titleNoteBy != by)
            return;

        titleNote = string.Empty;
        titleNoteBy = null;
        tabsDirty = true;
    }

    /// <summary>The file on screen for an extension, its path, name, text, #defines and what it came to the last time it ran.</summary>
    IRuntimeValue IExtensionScreens.DocumentObject()
    {
        var defines = new Dictionary<string, IRuntimeValue>();
        foreach (var (name, value) in DefinesOf(document))
            defines[name] = new StringValue(value);

        IRuntimeValue declared = Values.Null;
        if (running.LastRun is { } ran && running.LastRunOf == document)
        {
            var all = new Dictionary<string, IRuntimeValue>();
            foreach (var (name, value) in ran.Declared)
                all[name] = value;
            declared = new ObjectValue(all);
        }

        return Natives.Object(
            ("path", document.Path is { } path ? new StringValue(Path.GetFullPath(path)) : Values.Null),
            ("name", new StringValue(document.Name)),
            ("text", new StringValue(code.Text)),
            ("defines", new ObjectValue(defines)),
            ("values", declared));
    }

    /// <summary>A file to open picked, with Hex's own dialog, or its path typed for the self test.</summary>
    private void AskFile(string title, string folder, List<(string Name, string Pattern)> filter, Action<string> picked)
    {
        if (!options.SelfTest)
        {
            Ask(HexDialogs.OpenFile(title, folder, filter, DialogHost), picked);
            return;
        }

        AskText(title, "The path of the file", folder, "Pick", typed =>
        {
            if (File.Exists(typed.Trim()))
                picked(typed.Trim());
            else
                context.Notices.Say($"there is no file {typed}", error: true);
        });
    }


    void IExtensionScreens.Reload(HexExtension extension, Action start)
    {
        var showing = new List<(HexDocument Document, string Id)>();
        foreach (var ((open, id), made) in viewsMade.ToList())
        {
            if (made.Owner != extension)
                continue;

            if (open.View == HexView.Extension && open.ExtensionView == id)
                showing.Add((open, id));
            made.Closed = true;
            extensionViews.Remove(made.Root);
            viewsMade.Remove((open, id));
        }

        for (int i = windowsMade.Count - 1; i >= 0; i--)
        {
            if (windowsMade[i].Owner == extension)
            {
                windowsMade[i].Closed = true;
                windowsMade[i].Root.Parent?.Remove(windowsMade[i].Root);
                windowsMade.RemoveAt(i);
            }
        }

        viewKinds.RemoveAll(kind => kind.Owner == extension);
        start();

        foreach (var (open, id) in showing)
        {
            if (!documents.Contains(open))
                continue;
            open.View = HexView.Extension;
            open.ExtensionView = id;
        }

        ApplyView();
    }

    private const string VIEW_BUTTON = "view_button.hor";

    private readonly List<ExtensionViewKind> viewKinds = [];
    private readonly Dictionary<(HexDocument, string), UIScriptedLayout> viewsMade = [];
    private readonly List<UIScriptedLayout> panelsMade = [], windowsMade = [];

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
            if (kind.Owner.Enabled && kind.Owner.Failed is null && (kind.When is null || ViewIsFor(kind.When, of, defines)))
                offered.Add(kind);
        }

        return offered;
    }

    /// <summary>
    /// Whether a view is for a document, by a #define the file has, or by the kind of file when what it says starts
    /// with a dot (".md, .markdown", a Markdown preview for every Markdown file).
    /// </summary>
    private static bool ViewIsFor(string when, HexDocument of, IReadOnlyDictionary<string, string> defines)
    {
        if (!when.StartsWith('.'))
            return defines.ContainsKey(when);
        string extension = of.Path is { } path ? Path.GetExtension(path) : string.Empty;
        return when.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(kind => kind.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The view of an extension that is on screen, null when it's the code, the table or the designer.</summary>
    private UIScriptedLayout? ShownView => document.View == HexView.Extension && document.ExtensionView is { } id && viewsMade.TryGetValue((document, id), out UIScriptedLayout? made) ? made : null;

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

        foreach (UIScriptedLayout made in viewsMade.Values)
            made.Root.Visible = false;

        extensionViews.Visible = showing;
        ExtensionViewKind? shown = showing ? offered.First(kind => kind.Id == document.ExtensionView) : null;
        ApplyViewSides(shown);
        if (shown is null)
            return;

        if (!viewsMade.TryGetValue((document, shown.Id), out UIScriptedLayout? view))
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
        left.Show(!settings.SideBarHidden && (shown?.Left ?? true));
        right.Show(!settings.PropertiesHidden && (shown?.Right ?? true));
    }

    /// <summary>Makes a view of a file out of its layout, and hands it to the extension's open(ui, file).</summary>
    private UIScriptedLayout? MakeView(ExtensionViewKind kind, HexDocument of)
    {
        var holder = extensionViews.Add(new Panel { Fill = UIFill.Both });
        // What it has room for is the panel the views go in, the holder is only as big as what's in it
        UIScriptedLayout? made = MakeUI(kind.Owner, "view", kind.Layout, holder, of.Path is { } path ? Path.GetFullPath(path) : null, null, () => extensionViews.Bounds.Size);
        if (made is null)
        {
            extensionViews.Remove(holder);
            return null;
        }

        viewsMade[(of, kind.Id)] = made;
        if (kind.Open is { } open && !kind.Owner.Invoke(open, made, made.File is { } file ? new StringValue(file) : Values.Null))
            extensions.Failed(kind.Owner);

        return made;
    }

    /// <summary>
    /// Helper method to make a layout of an extension's folder inside of something, its handlers the extension's to call.
    /// Null, said, if the layout doesn't build.
    /// </summary>
    private UIScriptedLayout? MakeUI(HexExtension owner, string kind, string layoutFile, UIComponent into, string? file, Action? close, Func<Vector2>? room = null) =>
        UIScriptedLayout.Make(chrome, owner, kind, Path.GetFullPath(Path.Combine(owner.Folder, layoutFile)), into, file, close,
            said => Notify(said, error: true), ScriptFailed, room);

    // What a layout of an extension says when a call of its went wrong, the extension switched off and said so
    private void ScriptFailed(IUIScriptOwner owner) => extensions.Failed((HexExtension)owner);

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
    private UIScriptedLayout? MakePanelPage(HexExtension owner, string title, string icon, string layoutFile)
    {
        var page = bottom.Add(new Panel { Fill = UIFill.Both });
        UIScriptedLayout? made = null;
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
    private readonly Dictionary<UIScriptedLayout, string> panelTitles = [];

    /// <summary>The titles of the pages of the panel, Hex's three and then the extensions'.</summary>
    private string[] PanelTabs(string problemsTitle) =>
        [problemsTitle, Said("panel.output"), Said("panel.console"), .. panelsMade.Select(page => panelTitles[page])];

    /// <summary>A little window over everything, made out of an extension's layout.</summary>
    private UIScriptedLayout? MakeWindow(HexExtension owner, string title, string layoutFile, Vector2 size)
    {
        UIScriptedLayout? made = UIScriptedLayout.MakeWindow(chrome, owner, title, Path.GetFullPath(Path.Combine(owner.Folder, layoutFile)), size,
            said => Notify(said, error: true), ScriptFailed, closed => windowsMade.Remove(closed));
        if (made is not null)
            windowsMade.Add(made);
        return made;
    }

    /// <summary>
    /// Every tick, the views, pages and windows of extensions that are on screen told how long it was (on_tick), and the
    /// keys handed to the view on screen (on_key) while nothing is being typed into.
    /// </summary>
    private void UpdateExtensionUI(float dt)
    {
        extensions.Update(dt);
        if (viewsMade.Count == 0 && panelsMade.Count == 0 && windowsMade.Count == 0)
            return;

        // Made only when something ticks, a view with nothing going on costs nothing a frame
        NumberValue? number = null;
        if (ShownView is { } view)
            view.Update(dt, ref number, ScriptFailed);

        for (int i = panelsMade.Count - 1; i >= 0; i--)
        {
            if (panelsMade[i] is { } page && page.Root.Visible && bottom.Visible && bottom.SelectedPage == page.Root)
                page.Update(dt, ref number, ScriptFailed);
        }

        for (int i = windowsMade.Count - 1; i >= 0; i--)
        {
            if (windowsMade[i] is { } window && window.Root.Visible)
                window.Update(dt, ref number, ScriptFailed);
        }

        // The keys go to the view on screen when nothing has the keyboard, or one of its own drawings does
        if (ShownView is { Key: not null } keyed && (compositor.Focus is null || (compositor.Focus is DrawingPanel && IsInside(compositor.Focus, keyed.Root))))
        {
            var keyboard = Engine.Input.Keyboard;
            if (!keyboard.Control)
            {
                foreach (var (key, name) in UIScriptedLayout.Keys)
                {
                    if (keyboard.WasPressed(key))
                        keyed.Press(name, keyboard.Shift, keyboard.Alt, ScriptFailed);
                }
            }
        }
    }

    /// <summary>
    /// Hands a key to the view of an extension on screen, true if it took it (its on_key handed back true), which is how
    /// Ctrl+Z undoes the view's own changes rather than the code's.
    /// </summary>
    private bool ExtensionKey(string name) => ShownView is { } view && view.Press(name, ScriptFailed);

    /// <summary>Takes away what a document had made for it by extensions, as it closes.</summary>
    private void ForgetExtensionViews(HexDocument closing)
    {
        foreach (var key in viewsMade.Keys.Where(key => key.Item1 == closing).ToList())
        {
            UIScriptedLayout made = viewsMade[key];
            made.Closed = true;
            extensionViews.Remove(made.Root);
            viewsMade.Remove(key);
        }
    }

    /// <summary>A file that was just opened, put on the view an extension has for its #define, if one has.</summary>
    private void OpenOnExtensionView(HexDocument opened)
    {
        IReadOnlyDictionary<string, string> defines = DefinesOf(opened);
        // only a view for a #define is the file's own way of being shown, one for a kind of file (a preview) is asked for
        ExtensionViewKind? wanted = viewKinds.FirstOrDefault(kind => kind.When is { } when && !when.StartsWith('.') && defines.ContainsKey(when) && kind.Owner.Enabled && kind.Owner.Failed is null);
        if (wanted is not null && opened == document)
            SwitchToExtensionView(wanted.Id);
    }
}
