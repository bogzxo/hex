using System.Globalization;
using System.Numerics;

using Hex.Core;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Extensions window (extensions_window.hor), what the activity bar's Extensions and Ctrl+Shift+X open. Three tabs
// over one list of cards. Installed is every extension there is, with a switch that turns it on and off, Update when a
// source has a newer one, and Uninstall for the ones that didn't come with Hex. Browse is everything every source has,
// Install on what isn't here. Sources is where they come from, a card a source with how its last sync went, and a row
// at the top to add a GitHub repository, a git URL or a folder. The search box narrows all three as it's typed. The
// cards are made again whenever the extensions say something changed (HexExtensions.ListChanged), or the window's
// width does. The settings window has the same lists on its Extensions pages, Installed, Available and Sources, made
// by the same code into its panels (see AddExtensionsSettings).
internal sealed partial class DawnScene
{
    private enum ExtensionsTab { Installed, Browse, Sources }

    private const float CARD_BADGE = 44.0f, CARD_ACTION_HEIGHT = 30.0f, CARD_SPACING = 12.0f;

    private WindowPanel? extensionsWindow;
    private StackPanel? extensionsList;
    private Label? extensionsSaid;
    private TextBox? extensionsSearch;
    private Button? extensionsTabInstalled, extensionsTabBrowse, extensionsTabSources;
    private ExtensionsTab extensionsTab = ExtensionsTab.Installed;
    private float extensionsWidth;
    private bool extensionsDirty;

    // How wide a card is in the list being filled right now, the window's or a settings page's
    private float cardsWidth = 640.0f;

    /// <summary>A list of cards on a page of the settings, its tab, what's in it, the line over it and its search box.</summary>
    private sealed record SettingsCards(ExtensionsTab Tab, StackPanel List, Label Said, TextBox? Search);

    // How wide the settings rows were the last time the cards on the settings pages were made
    private float settingsCardsWidth;

    // The lists on the settings window's Extensions pages while it's up
    private readonly List<SettingsCards> settingsCards = [];

    /// <summary>The Extensions window, opened at a tab, or brought to the front at it when it's up.</summary>
    private void OpenExtensionsWindow(ExtensionsTab? at = null)
    {
        if (at is { } tab)
            extensionsTab = tab;

        if (extensionsWindow is { } open)
        {
            open.BringToFront();
            FillExtensions();
            return;
        }

        UILayout made = layout.Instantiate("extensions_window.hor", chrome.Root);
        var window = made.Get<WindowPanel>("window");
        DressWindow(window);
        extensionsWindow = window;
        extensionsList = made.Get<StackPanel>("extensions_list");
        extensionsSaid = made.Get<Label>("extensions_said");
        extensionsSearch = made.Get<TextBox>("extensions_search");
        extensionsTabInstalled = made.Get<Button>("extensions_tab_installed");
        extensionsTabBrowse = made.Get<Button>("extensions_tab_browse");
        extensionsTabSources = made.Get<Button>("extensions_tab_sources");

        extensionsTabInstalled.OnPressed = () => ShowExtensionsTab(ExtensionsTab.Installed);
        extensionsTabBrowse.OnPressed = () => ShowExtensionsTab(ExtensionsTab.Browse);
        extensionsTabSources.OnPressed = () => ShowExtensionsTab(ExtensionsTab.Sources);
        made.Get<Button>("extensions_sync").OnPressed = () => extensions.SyncCatalog(asked: true);
        extensionsSearch.OnChanged = _ => FillExtensions();

        if (settingsCards.Count == 0)
            extensions.ListChanged += MarkExtensionsDirty;
        window.OnClosed = () =>
        {
            if (settingsCards.Count == 0)
                extensions.ListChanged -= MarkExtensionsDirty;
            window.Parent?.Remove(window);
            extensionsWindow = null;
            extensionsList = null;
            extensionsSaid = null;
            extensionsSearch = null;
        };

        extensionsWidth = window.Size.X;
        FillExtensions();
        window.TakeKeyboard();
        extensionsSearch.Focus();
    }

    // The extensions changed under the window, from any of their callbacks, made again on the next tick rather than
    // in the middle of whatever pressed the button
    private void MarkExtensionsDirty() => extensionsDirty = true;

    /// <summary>Every tick, the cards made again when something changed or the window was made wider or narrower.</summary>
    private void FollowExtensionsWindow()
    {
        if (extensionsWindow is { } window && MathF.Abs(window.Size.X - extensionsWidth) > 1.0f)
        {
            extensionsWidth = window.Size.X;
            extensionsDirty = true;
        }

        // The settings pages' cards go the width of the rows above them, which follows the settings window's
        if (settingsCards.Count > 0 && MathF.Abs(SettingsCardsWidth() - settingsCardsWidth) > 1.0f)
            extensionsDirty = true;

        if (!extensionsDirty)
            return;

        extensionsDirty = false;
        FillExtensions();
        foreach (SettingsCards cards in settingsCards.ToArray())
            FillSettingsCards(cards);
    }

    private void ShowExtensionsTab(ExtensionsTab tab)
    {
        extensionsTab = tab;
        FillExtensions();
    }

    /// <summary>The list made again for the tab that's showing and what's in the search box.</summary>
    private void FillExtensions()
    {
        if (extensionsList is not { } list || extensionsSaid is not { } said)
            return;

        if (extensionsTabInstalled is not null) extensionsTabInstalled.Selected = extensionsTab == ExtensionsTab.Installed;
        if (extensionsTabBrowse is not null) extensionsTabBrowse.Selected = extensionsTab == ExtensionsTab.Browse;
        if (extensionsTabSources is not null) extensionsTabSources.Selected = extensionsTab == ExtensionsTab.Sources;

        cardsWidth = extensionsWidth - 42;
        FillCards(extensionsTab, list, said, extensionsSearch?.Text.Trim() ?? string.Empty);
    }

    // One list of cards made again, for a tab and what was searched for
    private void FillCards(ExtensionsTab tab, StackPanel list, Label said, string query)
    {
        foreach (UIComponent card in list.Children.ToArray())
            list.Remove(card);

        switch (tab)
        {
            case ExtensionsTab.Installed:
                FillInstalled(list, said, query);
                break;
            case ExtensionsTab.Browse:
                FillBrowse(list, said, query);
                break;
            case ExtensionsTab.Sources:
                FillSources(list, said, query);
                break;
        }
    }

    /* Installed */

    private void FillInstalled(StackPanel list, Label said, string query)
    {
        var shown = extensions.All
            .Where(extension => Matches(query, extension.Name, extension.Description, extension.Author, extension.Id))
            .OrderBy(extension => extension.BuiltIn)
            .ThenBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int on = extensions.All.Count(extension => extension.Enabled);
        int behind = extensions.Behind().Count();
        said.Text = Said("extensions_window.installed_line", ("count", Count(extensions.All.Count)), ("on", Count(on)))
            + (behind > 0 ? "   " + Said("extensions_window.updates_line", ("count", Count(behind))) : string.Empty);

        if (shown.Count == 0)
        {
            list.Add(EmptyCard(query.Length > 0 ? Said("extensions_window.nothing_matches", ("query", query)) : Said("extensions_window.none_installed")));
            return;
        }

        foreach (HexExtension extension in shown)
        {
            HexExtension of = extension;
            HexExtensions.CatalogEntry? entry = extensions.Catalog.FirstOrDefault(catalog => catalog.Id.Equals(of.Id, StringComparison.OrdinalIgnoreCase));
            bool update = entry is not null && extensions.Behind().Any(pair => pair.Installed == of);

            var actions = new List<UIComponent>();
            if (update && entry is not null)
                actions.Add(ActionButton(extensions.Installing(entry) is not null ? Said("extension_catalog.working") : Said("extension_catalog.update"), "cloud-download",
                    () => extensions.Install(entry), primary: true, enabled: extensions.Installing(entry) is null));
            if (extensions.CanUninstall(of))
                actions.Add(ActionButton(Said("extensions_window.uninstall"), "trash", () => AskUninstall(of), danger: true));

            var toggle = new ToggleButton(of.Enabled ? Said("extensions_window.on") : Said("extensions_window.off"), of.Enabled)
            {
                Size = new Vector2(84, CARD_ACTION_HEIGHT),
                Style = "switch",
                Tooltip = of.Enabled ? Said("extensions_window.switch_off") : Said("extensions_window.switch_on")
            };
            toggle.OnPressed = () => extensions.Toggle(of, toggle.State);
            actions.Add(toggle);

            string state = of.Failed is { } failed ? $"[color:{HexColour(compositor.Skin?.Palette.Error ?? new Vector4(1, 0.35f, 0.35f, 1))}]{UIRichText.MarkdownLiteral(Said("extensions_window.failed", ("why", failed)))}[/color]"
                : of.BuiltIn ? Said("extensions_window.comes_with_hex_line")
                : update && entry is not null ? Said("extensions_window.update_line", ("version", UIRichText.MarkdownLiteral(entry.Version)))
                : !of.Enabled ? Said("extensions_window.switched_off_line")
                : Said("extensions_window.on_line");
            string from = entry is not null && extensions.InstalledOf(entry) == of && !of.BuiltIn ? "  ·  " + SourceName(entry.Source) : string.Empty;

            list.Add(Card(of.Name, of.Version, of.Author, of.Description, state + from, of.Enabled, actions));
        }
    }

    private void AskUninstall(HexExtension extension) =>
        ((IWindows)this).Ask(Said("extensions_window.uninstall_title", ("name", extension.Name)), Said("extensions_window.uninstall_text", ("name", extension.Name)),
            [Said("extensions_window.cancel"), Said("extensions_window.uninstall")], answered =>
            {
                if (answered == Said("extensions_window.uninstall"))
                    extensions.Uninstall(extension, out _);
            }, risky: Said("extensions_window.uninstall"));

    /* Browse */

    private void FillBrowse(StackPanel list, Label said, string query)
    {
        var shown = extensions.Catalog.Where(entry => Matches(query, entry.Name, entry.Description, entry.Author, entry.Id, entry.Source)).ToList();
        said.Text = extensions.Syncing ? Said("extensions_window.syncing")
            : Said("extensions_window.browse_line", ("count", Count(extensions.Catalog.Count)), ("sources", Count(extensions.Sources.Count)));

        if (shown.Count == 0)
        {
            list.Add(EmptyCard(query.Length > 0 ? Said("extensions_window.nothing_matches", ("query", query))
                : extensions.Syncing ? Said("extensions_window.syncing") : Said("extensions_window.nothing_yet")));
            return;
        }

        foreach (HexExtensions.CatalogEntry entry in shown)
        {
            HexExtensions.CatalogEntry of = entry;
            HexExtension? installed = extensions.InstalledOf(of);
            bool going = extensions.Installing(of) is not null;
            string? button = extensions.CatalogButton(of);

            var actions = new List<UIComponent>();
            if (button is not null)
                actions.Add(ActionButton(going ? Said("extension_catalog.working") : button, "cloud-download", () => extensions.Install(of), primary: true, enabled: !going));
            else if (installed is not null)
                actions.Add(ActionButton(Said("extensions_window.manage"), "settings-gear", () => ShowExtensionsTab(ExtensionsTab.Installed)));

            string state = installed is null ? Said("extension_catalog.not_installed")
                : installed.BuiltIn ? Said("extension_catalog.comes_with_hex")
                : HexExtensionCatalog.IsNewer(of.Version, installed.Version) ? Said("extension_catalog.behind", ("installed", UIRichText.MarkdownLiteral(installed.Version)))
                : Said("extension_catalog.installed_line");

            list.Add(Card(of.Name, of.Version, of.Author, of.Description, $"{state}  ·  {SourceName(of.Source)}", installed?.Enabled ?? false, actions));
        }
    }

    /* Sources */

    private void FillSources(StackPanel list, Label said, string query)
    {
        said.Text = extensions.Syncing ? Said("extensions_window.syncing") : Said("extensions_window.sources_line", ("count", Count(extensions.Sources.Count)));
        list.Add(AddSourceCard());

        foreach (string source in extensions.Sources.Where(source => Matches(query, source)))
        {
            string of = source;
            int count = extensions.SyncedCount(of);
            int listed = extensions.Catalog.Count(entry => entry.Source.Equals(of, StringComparison.OrdinalIgnoreCase));
            HexExtensionSourceKind kind = HexExtensionCatalog.KindOf(of);

            var actions = new List<UIComponent>();
            if (HexExtensionCatalog.PageOf(of) is { } page)
                actions.Add(ActionButton(Said("extensions_window.open_page"), "link-external", () => OpenInBrowser(page)));
            if (kind == HexExtensionSourceKind.Folder)
                actions.Add(ActionButton(Said("extensions_window.open_folder"), "folder-opened", () => OpenInBrowser(new Uri(Path.GetFullPath(of)).AbsoluteUri)));
            bool removable = !extensions.IsMainSource(of) || extensions.Sources.Count > 1;
            actions.Add(ActionButton(Said("extensions_window.remove"), "trash", () => extensions.RemoveSource(of), danger: true, enabled: removable,
                tooltip: removable ? null : Said("extensions_window.last_source")));

            string state = extensions.ProblemOf(of) is { } why
                ? $"[color:{HexColour(compositor.Skin?.Palette.Error ?? new Vector4(1, 0.35f, 0.35f, 1))}]{UIRichText.MarkdownLiteral(Said("extensions_window.source_failed", ("why", why)))}[/color]"
                : listed < count ? Said("extensions_window.source_count_shadowed", ("count", Count(count)), ("above", Count(count - listed)))
                : Said("extensions_window.source_count", ("count", Count(count)));
            if (extensions.IsMainSource(of))
                state += "  ·  " + Said("extensions_window.main_source");

            string title = kind switch
            {
                HexExtensionSourceKind.GitHub => Said("extensions_window.kind_github"),
                HexExtensionSourceKind.Git => Said("extensions_window.kind_git"),
                _ => Said("extensions_window.kind_folder")
            };
            list.Add(Card(of, string.Empty, string.Empty, title, state, true, actions, kind switch
            {
                HexExtensionSourceKind.GitHub => "github",
                HexExtensionSourceKind.Git => "source-control",
                _ => "folder-opened"
            }));
        }
    }

    // The row at the top of Sources, a box for what to add and Add, Browse for a folder
    private UIComponent AddSourceCard()
    {
        var card = new Panel
        {
            Fill = UIFill.Horizontal,
            Color = (compositor.Skin?.Palette.Accent ?? new Vector4(0.4f, 0.6f, 1, 1)) with { W = 0.08f },
            Radius = 10,
            Padding = new UIEdges(14, 12, 14, 12)
        };
        var column = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 8, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        column.Add(new Label { Text = $"**{UIRichText.MarkdownLiteral(Said("extensions_window.add_source"))}**", Markdown = true, TextScale = 0.145f, Anchor = Origin.Left, Align = Origin.Left });
        column.Add(new Label
        {
            Text = Said("extensions_window.add_source_help"),
            Markdown = true,
            Wrap = true,
            Dim = true,
            TextScale = 0.12f,
            Size = new Vector2(TextWidth(0), 0),
            Anchor = Origin.Left,
            Align = Origin.TopLeft
        });

        var row = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Anchor = Origin.Left, Pivot = Origin.Left });
        var box = row.Add(new TextBox
        {
            Size = new Vector2(MathF.Max(240, TextWidth(0) - 230), 32),
            Padding = new UIEdges(10, 4, 10, 4),
            TextScale = 0.135f,
            Placeholder = Said("extensions_window.add_source_placeholder"),
            MaxLength = 400
        });

        void Add()
        {
            if (box.Text.Trim().Length == 0)
                return;
            if (extensions.AddSource(box.Text, out string why))
                box.Text = string.Empty;
            else
                Notify(why, true);
        }

        box.OnSubmitted = _ => Add();
        row.Add(ActionButton(Said("extensions_window.add"), "add", Add, primary: true));
        row.Add(ActionButton(Said("extensions_window.browse_folder"), "folder-opened", () => explorer.AskFolder(picked =>
        {
            if (!extensions.AddSource(picked, out string why))
                Notify(why, true);
        })));
        return card;
    }

    /* Cards */

    // How wide the words of a card can be, the window less its margins, the badge and the buttons at the right
    private float TextWidth(float actions) => MathF.Max(200, cardsWidth - 28 - CARD_BADGE - CARD_SPACING * 2 - actions);

    /// <summary>
    /// One card, a badge with the first letter (or an icon) at the left, the name, version and author in a line over what
    /// it is and a quiet line of how it stands, and its buttons at the right.
    /// </summary>
    private UIComponent Card(string name, string version, string author, string description, string state, bool lit, List<UIComponent> actions, string? icon = null)
    {
        Vector4 accent = compositor.Skin?.Palette.Accent ?? new Vector4(0.4f, 0.6f, 1, 1);
        var card = new Panel
        {
            Fill = UIFill.Horizontal,
            Color = compositor.Skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.04f),
            Radius = 10,
            Padding = new UIEdges(14, 12, 14, 12)
        };
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = CARD_SPACING, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });

        var badge = row.Add(new Panel
        {
            Size = new Vector2(CARD_BADGE, CARD_BADGE),
            Radius = 10,
            Color = accent with { W = lit ? 0.22f : 0.08f },
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft
        });
        if (icon is not null)
            badge.Add(new Label { Text = $"$({icon})", TextScale = 0.22f, Anchor = Origin.Center, Pivot = Origin.Center, Align = Origin.Center });
        else
            badge.Add(new Label { Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?", TextScale = 0.2f, Anchor = Origin.Center, Pivot = Origin.Center, Align = Origin.Center });

        float actionsWidth = actions.Sum(action => action.Size.X) + MathF.Max(0, actions.Count - 1) * 6;
        float width = TextWidth(actionsWidth);
        var words = row.Add(new StackPanel { Spacing = 3, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(width, 0) });

        string by = author.Length > 0 ? "  " + Said("extensions.by", ("author", UIRichText.MarkdownLiteral(author))) : string.Empty;
        string head = $"**{UIRichText.MarkdownLiteral(name)}**" + (version.Length > 0 || by.Length > 0 ? $"  [color:dim]{UIRichText.MarkdownLiteral(version)}{by}[/color]" : string.Empty);
        words.Add(new Label { Text = head, Markdown = true, TextScale = 0.145f, Wrap = true, Size = new Vector2(width, 0), Anchor = Origin.Left, Align = Origin.TopLeft });
        if (description.Length > 0)
            words.Add(new Label { Text = UIRichText.MarkdownLiteral(description), Markdown = true, TextScale = 0.125f, Wrap = true, Size = new Vector2(width, 0), Anchor = Origin.Left, Align = Origin.TopLeft });
        words.Add(new Label { Text = state, Markdown = true, Dim = true, TextScale = 0.115f, Wrap = true, Size = new Vector2(width, 0), Anchor = Origin.Left, Align = Origin.TopLeft });

        var buttons = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        foreach (UIComponent action in actions)
            buttons.Add(action);

        return card;
    }

    private UIComponent EmptyCard(string text) => new Panel
    {
        Fill = UIFill.Horizontal,
        Padding = new UIEdges(14, 24, 14, 24)
    }.With(new Label { Text = text, Markdown = true, Dim = true, TextScale = 0.13f, Anchor = Origin.Center, Pivot = Origin.Center, Align = Origin.Center });

    private static Button ActionButton(string label, string icon, Action pressed, bool primary = false, bool danger = false, bool enabled = true, string? tooltip = null)
    {
        float width = MathF.Max(84, 40 + label.Length * 7.5f);
        return new Button
        {
            Label = label,
            Icon = icon,
            Size = new Vector2(width, CARD_ACTION_HEIGHT),
            Primary = primary,
            Danger = danger,
            Enabled = enabled,
            Style = primary || danger ? "button" : "button_flat",
            Tooltip = tooltip ?? string.Empty,
            OnPressed = pressed
        };
    }

    // Whether every word typed is in one of the texts, any case, nothing typed matching everything
    private static bool Matches(string query, params string[] texts)
    {
        if (query.Length == 0)
            return true;
        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!texts.Any(text => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    // What a source is called on a card, owner/name, a URL without its .git, or a folder's name
    private static string SourceName(string source)
    {
        if (source.Length == 0)
            return string.Empty;
        string name = HexExtensionCatalog.KindOf(source) == HexExtensionSourceKind.Folder ? Path.GetFileName(source.TrimEnd('/', '\\')) : source;
        return UIRichText.MarkdownLiteral(name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    /* The settings window's Extensions pages */

    /// <summary>
    /// The settings window's Extensions pages given the lists of the Extensions window, Installed under the switched off
    /// row, Available in place of the catalogue's old rows, and Sources on a page of its own. Called once, as the scene
    /// starts.
    /// </summary>
    private void AddExtensionsSettings()
    {
        ISettingsHost host = Context.Settings;
        host.AddSource(() => new NoSettings(HexExtensions.SETTINGS), HexExtensions.SETTINGS, () => MakeSettingsCards(ExtensionsTab.Installed), null, ForgetSettingsCards);
        host.AddSource(() => new NoSettings(HexExtensions.AVAILABLE), HexExtensions.AVAILABLE, () => MakeSettingsCards(ExtensionsTab.Browse), null, ForgetSettingsCards);
        host.AddSource(() => new NoSettings(HexExtensions.SOURCES), HexExtensions.SOURCES, () => MakeSettingsCards(ExtensionsTab.Sources), null, ForgetSettingsCards);
    }

    // A page's list, a search box over it (not on Sources, which has few), the line that says how many, and the cards
    private UIComponent MakeSettingsCards(ExtensionsTab tab)
    {
        float width = SettingsCardsWidth();
        var panel = new StackPanel { Spacing = 8.0f, Padding = new UIEdges(0.0f, 6.0f, 0.0f, 14.0f), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };

        TextBox? search = null;
        if (tab != ExtensionsTab.Sources)
        {
            search = panel.Add(new TextBox
            {
                Size = new Vector2(width, 30),
                Padding = new UIEdges(10, 4, 10, 4),
                TextScale = 0.135f,
                Placeholder = Said("extensions_window.search"),
                MaxLength = 200,
                Anchor = Origin.TopLeft,
                Pivot = Origin.TopLeft
            });
        }

        var said = panel.Add(new Label { Markdown = true, Dim = true, TextScale = 0.12f, Wrap = true, Size = new Vector2(width, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Align = Origin.TopLeft });
        var list = panel.Add(new StackPanel { Spacing = 10.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(width, 0) });

        var cards = new SettingsCards(tab, list, said, search);
        if (search is not null)
            search.OnChanged = _ => FillSettingsCards(cards);
        if (settingsCards.Count == 0)
            extensions.ListChanged += MarkExtensionsDirty;
        settingsCards.Add(cards);
        FillSettingsCards(cards);
        return panel;
    }

    // As wide as the settings rows are, before there are any the prose width the other panels have
    private float SettingsCardsWidth() =>
        settingsView?.Page.RowWidthNow is > 0.0f and var rows ? rows : PANEL_PROSE * (settingsView?.Page.Zoom ?? 1.0f);

    private void FillSettingsCards(SettingsCards cards)
    {
        float width = settingsCardsWidth = SettingsCardsWidth();
        if (cards.Search is { } search) search.Size = search.Size with { X = width };
        cards.Said.Size = cards.Said.Size with { X = width };
        cards.List.Size = cards.List.Size with { X = width };

        float before = cardsWidth;
        cardsWidth = width;
        FillCards(cards.Tab, cards.List, cards.Said, cards.Search?.Text.Trim() ?? string.Empty);
        cardsWidth = before;
    }

    // The settings window closed, its lists let go of
    private void ForgetSettingsCards()
    {
        if (settingsCards.Count == 0)
            return;
        settingsCards.Clear();
        if (extensionsWindow is null)
            extensions.ListChanged -= MarkExtensionsDirty;
    }

    /// <summary>A page of the settings that is only a panel, it has no rows of its own.</summary>
    private sealed class NoSettings(string section) : ISettingsSource
    {
        public string Section => SettingsSections.EXTENSIONS;
        public IReadOnlyList<UISetting> Settings { get; } = [];
        public IRuntimeValue? Get(string key) => null;
        public bool IsModified(string key) => false;
        public void Set(string key, IRuntimeValue value) { }
        public void Reset(string key) { }
        public override string ToString() => section;
    }
}
