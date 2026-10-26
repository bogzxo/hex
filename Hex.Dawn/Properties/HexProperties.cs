using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>What the Properties panel is showing, the rows of a layout's component, a file of HIDL's values or the properties of files.</summary>
internal enum HexPropertiesShows
{
    Component,
    Values,
    Files
}

// The Properties panel, the right side as one panel for whatever is selected, Visual Studio's F4 window and VS Code's
// side panel in one. A component picked in the designer has the designer's own rows (HexDesigner.Inspector), a file of
// HIDL its values, and everything else (a file or a folder picked in the explorer, several of them, a tab, a picture, or
// the file on screen when nothing else is picked) its properties, laid out the way the settings page lays out a setting,
// the same rows, headings, search and keys, so the two read as one program. What can be changed is changed where it
// is, the rest is text to select and copy. Alt+Enter brings it up for what has the keyboard.
internal sealed partial class HexProperties
{
    private readonly DawnContext context;
    private readonly SettingsPage page;
    private readonly TextBox filter;
    private readonly Button swap;

    // The files picked to be shown, the explorer's selection or a tab's Properties, empty for the file on screen
    private List<string> picked = [];

    // The designer's component that was selected as the files were picked, another one picked takes the panel back
    private object? pickedOver;

    // Whether a file of HIDL shows its properties rather than its values, after Alt+Enter or the button that swaps them
    private bool propertiesOverValues;

    // What the rows are of now, and what their values are, every row's key to what it says
    private List<string> shown = [];
    private HexDocument? shownDocument;
    private readonly Dictionary<string, IRuntimeValue> values = new(StringComparer.Ordinal);

    // Each time the rows are made again, so what comes back from a thread for rows that are gone is dropped
    private int generation;

    /// <summary>Git for a file's status and its last commit, set once git is made.</summary>
    internal HexGit? Git { get; set; }

    /// <summary>A file's language picked as Change Language Mode picks it, the scene's own, null for worked out again.</summary>
    internal Action<HexDocument, string?>? PickLanguage { get; set; }

    /// <summary>Change Language Mode for the file on screen.</summary>
    internal Action? ChangeLanguageMode { get; set; }

    /// <summary>A file or a folder renamed as the explorer renames one, whether it was.</summary>
    internal Func<string, string, bool>? Rename { get; set; }

    /// <summary>The page the properties of files are on, for a test.</summary>
    internal SettingsPage Page => page;

    /// <summary>The box over the designer's rows that narrows them down, for a test.</summary>
    internal TextBox Filter => filter;

    /// <summary>What the panel shows now.</summary>
    internal HexPropertiesShows Shows { get; private set; } = HexPropertiesShows.Component;

    /// <summary>The files the properties are of, for a test.</summary>
    internal IReadOnlyList<string> Shown => shown;

    public HexProperties(DawnContext with, StackPanel right, UIComponent head, UIComponent below)
    {
        context = with;
        page = new SettingsPage
        {
            ShowNav = false,
            CollapsibleSections = true,
            CopyValues = true,
            Opaque = false,
            Title = string.Empty,
            Padding = new UIEdges(4.0f, 6.0f, 4.0f, 4.0f),
            Visible = false,
            Anchor = Origin.Top,
            Pivot = Origin.Top,
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = IsModified,
            OnChanged = Changed,
            OnReset = Reset,
            OnButton = Pressed,
            NoteOf = NoteOf,
            NoteTipOf = NoteTipOf
        };
        page.Texts = new SettingsTexts
        {
            Title = string.Empty,
            SearchPlaceholder = Said("search"),
            General = Said("general"),
            CopyValue = Said("copy_value"),
            NothingFound = Said("nothing_found"),
            ClearSearch = Said("clear_search")
        };

        // The designer's rows are narrowed down by a box of their own, the page has its search built in
        filter = new TextBox { Placeholder = Said("filter"), Visible = false, MaxLength = 80, Anchor = Origin.Top, Pivot = Origin.Top, LeadIcon = "filter" };
        filter.OnChanged = _ => FilterRows?.Invoke(filter.Text);

        right.Add(filter);
        right.Add(page);
        int at = right.Children.ToList().IndexOf(below);
        right.MoveChild(filter, at);
        right.MoveChild(page, at + 1);
        context.Window.Grip(page, UIResize.Left, left: false);

        // A file of HIDL has its values and its properties, the button over them swaps the one for the other
        swap = head.Add(new Button
        {
            Icon = "symbol-property",
            Style = "button_flat",
            Size = new Vector2(26.0f, 22.0f),
            Anchor = Origin.Right,
            Pivot = Origin.Right,
            Position = new Vector2(-30.0f, 0.0f),
            Animated = false,
            Visible = false,
            OnPressed = () =>
            {
                propertiesOverValues = !propertiesOverValues;
                context.Window.RefreshRight();
            }
        });

        Dotnet.Changed += project => projectsChanged = true;
    }

    /// <summary>The designer's rows narrowed down to the ones whose names have what's typed in the filter in them.</summary>
    internal Action<string>? FilterRows { get; set; }

    private string Said(string key, params (string Name, string Value)[] filled) => context.Strings.Said("file_properties." + key, filled);

    /// <summary>
    /// What the panel follows, files picked in the explorer or a tab, or none for the file on screen. What's picked wins
    /// over the designer's selection until the next file comes on screen.
    /// </summary>
    internal void Follow(IReadOnlyList<string>? paths)
    {
        picked = paths is null ? [] : [.. paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath).Distinct(PlatformPaths.Comparer)];
        context.Window.RefreshRight();
    }

    /// <summary>Whether the panel was asked for and stays up for every file, rather than folding away for a file with no values.</summary>
    internal bool Pinned { get; set; }

    /// <summary>The properties of what's picked rather than a file's values, as Alt+Enter asks for them.</summary>
    internal void PreferProperties() => propertiesOverValues = true;

    /// <summary>
    /// Which the panel shows for a document. Files picked come first, then the designer's component, then a file of HIDL's
    /// values, and the properties of the document itself last.
    /// </summary>
    internal HexPropertiesShows Decide(HexDocument document, bool layoutTools, object? component, bool showsValues)
    {
        // A component picked in the designer after the files were takes the panel back
        if (picked.Count > 0 && layoutTools && component is not null && !ReferenceEquals(component, pickedOver))
            picked = [];
        pickedOver = component;
        bool componentSelected = component is not null;
        if (picked.Count > 0)
            return HexPropertiesShows.Files;
        if (layoutTools)
            return componentSelected || document.Path is null ? HexPropertiesShows.Component : HexPropertiesShows.Files;
        return showsValues && !propertiesOverValues ? HexPropertiesShows.Values : HexPropertiesShows.Files;
    }

    /// <summary>The panel made again for a document, as what it was decided to show.</summary>
    internal void Rebuild(HexDocument document, HexPropertiesShows shows, bool canSwap)
    {
        Shows = shows;
        page.Visible = shows == HexPropertiesShows.Files;
        filter.Visible = shows == HexPropertiesShows.Component;
        swap.Visible = canSwap;
        swap.Icon = propertiesOverValues ? "list-tree" : "symbol-property";
        swap.Tooltip = Said(propertiesOverValues ? "show_values" : "show_properties");
        if (shows == HexPropertiesShows.Component)
            FilterRows?.Invoke(filter.Text);
        if (shows != HexPropertiesShows.Files)
            return;

        List<string> paths = picked.Count > 0 ? picked : document.Path is { } path ? [Path.GetFullPath(path)] : [];
        bool same = paths.SequenceEqual(shown, PlatformPaths.Comparer) && ReferenceEquals(document, shownDocument) && !rowsStale;
        shown = paths;
        shownDocument = document;
        if (same)
        {
            RefreshDocumentRows();
            return;
        }

        rowsStale = false;
        Build(document);
    }

    // Whether the rows have to be made again even for the same files, a rename or a project's evaluation changed them
    private bool rowsStale;

    /// <summary>The file on screen changed to another, what was picked gives way to it the way Visual Studio's selection does.</summary>
    internal void DocumentShown()
    {
        propertiesOverValues = false;
        if (picked.Count > 0)
        {
            picked = [];
            context.Window.RefreshRight();
        }
    }

    /// <summary>The page laid out in the room the right side has, as wide as the side and as tall as what's left of it.</summary>
    internal void Fit(float width, float height)
    {
        page.Size = new Vector2(width, height);
        filter.Size = new Vector2(width, 28.0f);
    }

    /// <summary>Every tick, a project that changed under the rows shown evaluated again.</summary>
    internal void Update()
    {
        if (!projectsChanged || !page.Visible)
            return;

        projectsChanged = false;
        if (shownProject is not null)
            EvaluateProject(shownProject, generation);
    }

    private volatile bool projectsChanged;
}
