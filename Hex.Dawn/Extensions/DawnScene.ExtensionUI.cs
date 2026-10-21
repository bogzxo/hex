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
internal sealed partial class DawnScene : IExtensionScreens
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

    UIScriptedLayout? IExtensionScreens.MakePanelPage(HexExtension owner, string title, string icon, string layoutFile) =>
        screens.MakePage(owner, icon.Length > 0 ? $"$({icon}) {title.ToUpperInvariant()}" : title.ToUpperInvariant(), Path.GetFullPath(Path.Combine(owner.Folder, layoutFile)),
            bottom, () => problemsDirty = true);

    UIScriptedLayout? IExtensionScreens.MakeWindow(HexExtension owner, string title, string layoutFile, Vector2 size) =>
        screens.MakeWindow(owner, title, Path.GetFullPath(Path.Combine(owner.Folder, layoutFile)), size);

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

        AskText(title, Said("context.file_path"), folder, Said("context.pick"), typed =>
        {
            if (File.Exists(typed.Trim()))
                picked(typed.Trim());
            else
                context.Notices.Say(Said("context.no_file", ("path", typed)), error: true);
        });
    }


    void IExtensionScreens.Reload(HexExtension extension, Action start)
    {
        // The views that were on screen go back on their files once it's started again
        var showing = screens.Forget(extension).Where(view => view.File.View == HexView.Extension && view.File.ExtensionView == view.Id).ToList();
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

    // The views, pages and windows the extensions made, the engine's to keep and tick
    private UIScriptedScreens<HexDocument> screens = null!;

    // The panel the views of extensions go into, in the middle where the code is
    private Panel extensionViews = null!;

    // The buttons of the views of the file on screen, at the end of the tabs, by the view they're for
    private readonly List<(string Id, Button Button)> viewButtons = [];

    private void BuildExtensionViews()
    {
        extensionViews = layout.Get<Panel>("ext_view");
        extensionViews.Visible = false;
        screens = new UIScriptedScreens<HexDocument>(chrome, said => Notify(said, error: true), ScriptFailed);
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
    private UIScriptedLayout? ShownView => document.View == HexView.Extension && document.ExtensionView is { } id ? screens.View(document, id) : null;

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
            button.Tooltip = Said("extension_ui.view_tooltip", ("title", kind.Title), ("extension", kind.Owner.Name));
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

        foreach (UIScriptedLayout made in screens.Views)
            made.Root.Visible = false;

        extensionViews.Visible = showing;
        ExtensionViewKind? shown = showing ? offered.First(kind => kind.Id == document.ExtensionView) : null;
        ApplyViewSides(shown);
        if (shown is null)
            return;

        UIScriptedLayout? view = screens.View(document, shown.Id) ?? MakeView(shown, document);

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
        UIScriptedLayout? made = screens.MakeView(kind.Owner, kind.Id, of, Path.GetFullPath(Path.Combine(kind.Owner.Folder, kind.Layout)), extensionViews,
            of.Path is { } path ? Path.GetFullPath(path) : null);
        if (made is null)
            return null;

        if (kind.Open is { } open && !kind.Owner.Invoke(open, made, made.File is { } file ? new StringValue(file) : Values.Null))
            extensions.Failed(kind.Owner);

        return made;
    }

    /// <summary>
    /// Helper method to make a layout of an extension's folder inside of something, its handlers the extension's to call.
    /// Null, said, if the layout doesn't build.
    /// </summary>
    private UIScriptedLayout? MakeUI(HexExtension owner, string kind, string layoutFile, UIComponent into, string? file, Action? close) =>
        screens.Make(owner, kind, Path.GetFullPath(Path.Combine(owner.Folder, layoutFile)), into, file, close);

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

    /// <summary>The titles of the pages of the panel, Hex's three and then the extensions'.</summary>
    private string[] PanelTabs(string problemsTitle) =>
        [problemsTitle, Said("panel.output"), Said("panel.console"), .. screens.Pages.Select(screens.TitleOf)];

    /// <summary>
    /// Every tick, the views, pages and windows of extensions that are on screen told how long it was (on_tick), and the
    /// keys handed to the view on screen (on_key) while nothing is being typed into.
    /// </summary>
    private void UpdateExtensionUI(float dt)
    {
        extensions.Update(dt);
        screens.Update(dt, ShownView, bottom);

        // The keys go to the view on screen when nothing has the keyboard, or one of its own drawings does
        if (ShownView is { Key: not null } keyed && (compositor.Focus is null || (compositor.Focus is DrawingPanel && HexDesigner.IsInside(compositor.Focus, keyed.Root))))
        {
            var keyboard = Engine.Input.Keyboard;
            if (!keyboard.Control)
            {
                foreach (var (key, name) in UIScriptedLayout.Keys)
                {
                    if (keyboard.WasPressed(key))
                        screens.Press(keyed, name, keyboard.Shift, keyboard.Alt);
                }
            }
        }
    }

    /// <summary>
    /// Hands a key to the view of an extension on screen, true if it took it (its on_key handed back true), which is how
    /// Ctrl+Z undoes the view's own changes rather than the code's.
    /// </summary>
    private bool ExtensionKey(string name) => screens.Press(ShownView, name);

    /// <summary>Takes away what a document had made for it by extensions, as it closes.</summary>
    private void ForgetExtensionViews(HexDocument closing)
    {
        screens.Forget(closing);
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
