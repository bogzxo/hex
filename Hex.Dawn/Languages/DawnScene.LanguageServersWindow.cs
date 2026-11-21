using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

using Hex.Core;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>The pages of the Language Servers window.</summary>
internal enum ServersPage { Overview, Language, All, Advanced }

// The Language Servers window (language_servers_window.hor). Down the left Overview, All languages and Advanced, then the
// workspace's own languages; on the right the page picked. Overview leads with where the workspace stands and a row for
// each of its languages with the one button that moves it on. A language's page has its Setup, the steps from what the
// machine needs to what the project needs, each with its command shown and a button that runs it, and its Settings,
// everything that's that language's. What the steps are and what their buttons do is HexLanguages.Setup's.
internal sealed partial class DawnScene
{
    private WindowPanel? serversWindow;
    private StackPanel? serversNav, serversPage;
    private ServersPage serversShown = ServersPage.Overview;
    private string? serversLanguage;
    private bool serversSettingsTab;
    private string serversFilter = string.Empty;
    private readonly HashSet<string> serversLogsOpen = new(StringComparer.Ordinal);
    private int serversVersion = -1;
    private float serversWidth;
    private long serversRebuiltAt;
    private bool serversDirty;

    // The display line leads, two and a half times the body
    private const float SERVERS_BODY = 0.135f, SERVERS_DISPLAY = 0.34f, SERVERS_TITLE = 0.24f, SERVERS_SMALL = 0.12f;
    private const float SERVERS_BUTTON_HEIGHT = 30.0f, SERVERS_NAV_WIDTH = 220.0f;

    /// <summary>The Language Servers window at a language (its Setup), or at Overview, brought to the front when it's up.</summary>
    internal void OpenLanguageServers(string? language = null, bool settings = false)
    {
        serversShown = language is null ? ServersPage.Overview : ServersPage.Language;
        serversLanguage = language;
        serversSettingsTab = settings;

        if (serversWindow is { } open)
        {
            open.BringToFront();
            serversDirty = true;
            return;
        }

        UILayout made = layout.Instantiate("language_servers_window.hor", chrome.Root);
        var window = made.Get<WindowPanel>("window");
        DressWindow(window);

        // No bigger than the settings window would open on this screen. At 200% on a small one the layout's size ran off
        // the edge, and the switches down the right of its rows with it
        Vector2 fits = Horizon.UI.Components.SettingsWindow.SizeFor(chrome.Root.Bounds.Size, Vector2.Zero, Horizon.UI.Components.SettingsWindow.ScaleIn(chrome));
        window.MinSize = Vector2.Min(window.MinSize, fits);
        window.Size = Vector2.Min(window.Size, fits);
        serversWindow = window;
        serversNav = made.Get<StackPanel>("servers_nav");
        serversPage = made.Get<StackPanel>("servers_page");
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            serversWindow = null;
            serversNav = null;
            serversPage = null;
        };

        // What's on the machine now, the PATH read again, the project looked at again
        languages.ProbeSetup();
        serversWidth = window.Size.X;
        FillLanguageServers();
        window.TakeKeyboard();
    }

    /// <summary>Every tick, the window made again when what it shows changed, or the window changed width.</summary>
    private void FollowLanguageServersWindow()
    {
        if (serversWindow is not { } window)
            return;

        if (MathF.Abs(window.Size.X - serversWidth) > 1.0f)
        {
            serversWidth = window.Size.X;
            serversDirty = true;
        }

        // A language's Settings is typed into, it's made again only when asked, every change would take the caret away
        bool typing = serversShown == ServersPage.Language && serversSettingsTab;
        if (languages.SetupVersion != serversVersion && !typing)
            serversDirty = true;

        // While a command goes its log and its bar move, a few times a second is enough
        if (languages.SetupBusy && !typing && Environment.TickCount64 - serversRebuiltAt > 400)
            serversDirty = true;

        // A setting typed into goes in once the typing's stopped a moment, the way the settings take a text
        if (serversTyped is { } typed && Environment.TickCount64 - serversTypedAt > 600)
        {
            serversTyped = null;
            typed();
        }

        if (serversDirty)
            FillLanguageServers();
    }

    private Action? serversTyped;
    private long serversTypedAt;

    // A setting being typed into, put in force a moment after the last key, the one before it dropped
    private void TypedSetting(Action commit)
    {
        serversTyped = commit;
        serversTypedAt = Environment.TickCount64;
    }

    private void FillLanguageServers()
    {
        if (serversNav is not { } nav || serversPage is not { } page)
            return;

        serversDirty = false;
        serversVersion = languages.SetupVersion;
        serversRebuiltAt = Environment.TickCount64;

        FillServersNav(nav);
        foreach (UIComponent child in page.Children.ToList())
            page.Remove(child);

        HexServerEntry? language = serversLanguage is { } name ? languages.ServerEntry(name) : null;
        switch (serversShown)
        {
            case ServersPage.Language when language is not null:
                FillLanguagePage(page, language);
                break;
            case ServersPage.All:
                FillAllLanguages(page);
                break;
            case ServersPage.Advanced:
                FillServersAdvanced(page);
                break;
            default:
                FillServersOverview(page);
                break;
        }
    }

    // How wide the page's words go, the window less the list and the margins
    private float ServersPageWidth => MathF.Max(380.0f, (serversWindow?.Size.X ?? 1040.0f) - 252.0f - 52.0f);

    private void ShowServersPage(ServersPage page, string? language = null)
    {
        serversShown = page;
        serversLanguage = language;
        if (page != ServersPage.Language)
            serversSettingsTab = false;
        FillLanguageServers();
    }

    /* The list down the left */

    private void FillServersNav(StackPanel nav)
    {
        foreach (UIComponent child in nav.Children.ToList())
            nav.Remove(child);

        nav.Add(NavButton(Said("server_window.overview"), "home", serversShown == ServersPage.Overview, () => ShowServersPage(ServersPage.Overview)));
        nav.Add(NavButton(Said("server_window.all"), "list-flat", serversShown == ServersPage.All, () => ShowServersPage(ServersPage.All)));
        nav.Add(NavButton(Said("server_window.advanced"), "settings", serversShown == ServersPage.Advanced, () => ShowServersPage(ServersPage.Advanced)));

        List<HexServerEntry> here = languages.WorkspaceEntries();
        if (here.Count > 0)
        {
            nav.Add(new Spacer { Size = new Vector2(SERVERS_NAV_WIDTH, 10.0f) });
            nav.Add(new Label { Text = Said("server_window.this_workspace"), Dim = true, TextScale = SERVERS_SMALL, Size = new Vector2(SERVERS_NAV_WIDTH, 22.0f), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            foreach (HexServerEntry entry in here)
                nav.Add(LanguageNavButton(entry));
        }

        // The language shown, from All languages, said too when it isn't the workspace's
        if (serversShown == ServersPage.Language && serversLanguage is { } shown && here.All(entry => entry.Name != shown) && languages.ServerEntry(shown) is { } other)
        {
            nav.Add(new Spacer { Size = new Vector2(SERVERS_NAV_WIDTH, 10.0f) });
            nav.Add(LanguageNavButton(other));
        }
    }

    private Button LanguageNavButton(HexServerEntry entry)
    {
        List<SetupStep> steps = languages.SetupStepsFor(entry);
        int left = HexServerSetup.LeftOf(steps);
        var (icon, colour) = LanguageMark(steps);
        string label = left > 0 ? $"{languages.LanguageNameOf(entry)}  ({left})" : languages.LanguageNameOf(entry);
        var button = NavButton(label, icon, serversShown == ServersPage.Language && serversLanguage == entry.Name, () => ShowServersPage(ServersPage.Language, entry.Name));
        button.IconColor = colour;
        button.Tooltip = left > 0 ? Said("server_window.left", ("count", Count(left))) : Said("server_window.ready_short");
        return button;
    }

    private static Button NavButton(string label, string icon, bool selected, Action pressed) => new()
    {
        Label = label,
        Icon = icon,
        Size = new Vector2(SERVERS_NAV_WIDTH, 32.0f),
        Style = "button_flat",
        Selected = selected,
        Align = Origin.Left,
        OnPressed = pressed
    };

    // A language's mark in a list: ready, something going, something failed, or something left
    private (string Icon, Vector4? Colour) LanguageMark(List<SetupStep> steps)
    {
        var palette = compositor.Skin?.Palette;
        if (steps.Any(step => step.State is SetupState.Running or SetupState.Queued))
            return ("loading~spin", palette?.Accent);
        if (steps.Any(step => step.State == SetupState.Failed))
            return ("error", palette?.Error);
        if (steps.Any(step => step.State == SetupState.Checking))
            return ("circle-large-outline", null);
        return HexServerSetup.LeftOf(steps) == 0 ? ("pass-filled", palette?.Success) : ("circle-large-outline", palette?.Warning);
    }

    /* Overview */

    private void FillServersOverview(StackPanel page)
    {
        float width = ServersPageWidth;
        List<HexServerEntry> here = languages.WorkspaceEntries();
        string? folder = languages.SetupFolder;

        page.Add(Words(folder is not null ? Path.GetFileName(folder.TrimEnd('/', '\\')) : Said("server_window.no_folder_eyebrow"), width, SERVERS_SMALL, dim: true));

        if (here.Count == 0)
        {
            page.Add(Words(Said("server_window.nothing_here"), width, SERVERS_DISPLAY, bold: true));
            page.Add(Words(Said("server_window.nothing_here_text"), width, SERVERS_BODY, dim: true));
            page.Add(ActionsRow([ServersButton(Said("server_window.all"), "list-flat", () => ShowServersPage(ServersPage.All), primary: true)]));
            return;
        }

        var perLanguage = here.Select(entry => (Entry: entry, Steps: languages.SetupStepsFor(entry))).ToList();
        bool checking = perLanguage.Any(pair => pair.Steps.Any(step => step.State == SetupState.Checking));
        int left = perLanguage.Sum(pair => HexServerSetup.LeftOf(pair.Steps));
        var waiting = perLanguage.Where(pair => HexServerSetup.LeftOf(pair.Steps) > 0).Select(pair => languages.LanguageNameOf(pair.Entry)).ToList();

        string heading = checking ? Said("server_window.checking")
            : left == 0 ? Said("server_window.ready")
            : Said(left == 1 ? "server_window.one_left" : "server_window.some_left", ("count", Count(left)), ("languages", Joined(waiting)));
        page.Add(Words(heading, width, SERVERS_DISPLAY, bold: true));
        page.Add(Words(left == 0 && !checking ? Said("server_window.ready_text") : Said("server_window.overview_text"), width, SERVERS_BODY, dim: true));

        // Set up everything, the one primary button, when there's more than one thing it can do
        var actions = new List<UIComponent>();
        int runnable = perLanguage.Sum(pair => HexServerSetup.RunnableInOrder(pair.Steps).Count);
        if (languages.SettingUpAll)
            actions.Add(ServersButton(Said("server_window.setting_up"), "loading~spin", () => { }, primary: true, enabled: false));
        else if (runnable > 0)
            actions.Add(ServersButton(Said("server_window.set_up_everything"), "play", () => languages.SetupEverything(here), primary: true));
        actions.Add(ServersButton(Said("server_window.check_again"), "refresh", () => languages.ProbeSetup()));
        page.Add(ActionsRow(actions));

        page.Add(new Spacer { Size = new Vector2(width, 6.0f) });
        foreach (var (entry, steps) in perLanguage.OrderByDescending(pair => HexServerSetup.LeftOf(pair.Steps) > 0))
            page.Add(LanguageRow(entry, steps, width));
    }

    // A language in a list: its mark, its name and server, where it stands, and the one button that moves it on
    private UIComponent LanguageRow(HexServerEntry entry, List<SetupStep> steps, float width)
    {
        var card = new Panel { Size = new Vector2(width, 0.0f), Radius = 10, Padding = new UIEdges(14, 12, 14, 12), Color = compositor.Skin?.Palette.Raised, Outline = compositor.Skin?.Palette.Border };
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 12, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });

        var (icon, colour) = LanguageMark(steps);
        row.Add(Words($"{Coloured(colour)}$({icon}){(colour is null ? string.Empty : "[/color]")}", 22, 0.17f));

        SetupStep? next = steps.FirstOrDefault(step => step.Left);
        var buttons = new List<UIComponent>();
        if (next is not null && StepButton(entry, next) is { } primary)
            buttons.Add(primary);
        buttons.Add(ServersButton(Said("server_window.open"), "chevron-right", () => ShowServersPage(ServersPage.Language, entry.Name)));
        float buttonsWidth = buttons.Sum(button => button.Size.X) + (buttons.Count - 1) * 6;

        float textWidth = width - 28 - 22 - 24 - buttonsWidth;
        var words = row.Add(new StackPanel { Spacing = 3, Size = new Vector2(textWidth, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        words.Add(Words($"**{UIRichText.MarkdownLiteral(languages.LanguageNameOf(entry))}**  [color:dim]{UIRichText.MarkdownLiteral(entry.Command)}[/color]", textWidth, 0.145f));
        string state = steps.Any(step => step.State == SetupState.Checking) ? Said("server_window.checking_short")
            : next is null ? Said("server_window.ready_short")
            : StepSentence(next);
        words.Add(Words(state, textWidth, SERVERS_SMALL, dim: true));

        var actions = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        foreach (UIComponent button in buttons)
            actions.Add(button);
        return card;
    }

    // A step as a sentence, for a row that only says the next one
    private string StepSentence(SetupStep step) => step.State switch
    {
        SetupState.Blocked => Said("server_setup.sentence_blocked", ("title", step.Title), ("waits", step.BlockedBy ?? string.Empty)),
        SetupState.Running => Said("server_setup.sentence_running", ("title", step.Title)),
        SetupState.Queued => Said("server_setup.sentence_queued", ("title", step.Title)),
        SetupState.Failed => Said("server_setup.sentence_failed", ("title", step.Title)),
        SetupState.Outdated => Said("server_setup.sentence_outdated", ("title", step.Title)),
        SetupState.Manual => Said("server_setup.sentence_manual", ("title", step.Title)),
        _ => step.Kind == SetupStepKind.Project ? step.Detail : Said("server_setup.sentence_needed", ("title", step.Title))
    };

    // The button that does a step: Install, Update, Run, Try again, Download, or Cancel while it goes. Null for none
    private Button? StepButton(HexServerEntry entry, SetupStep step) => step.State switch
    {
        SetupState.Running or SetupState.Queued => ServersButton(Said("server_setup.cancel"), "debug-stop", () => languages.CancelSetup(step.Key)),
        SetupState.Failed when step.Command is not null => ServersButton(Said("server_setup.try_again"), "refresh", () => languages.RunSetupStep(entry, step), primary: true),
        SetupState.Outdated when step.Command is not null => ServersButton(Said("server_setup.update"), "arrow-circle-up", () => languages.RunSetupStep(entry, step), primary: true),
        SetupState.Needed or SetupState.Optional when step.Command is not null => ServersButton(
            step.Kind == SetupStepKind.Project ? Said("server_setup.run") : Said("server_setup.install"), step.Kind == SetupStepKind.Project ? "play" : "cloud-download",
            () => languages.RunSetupStep(entry, step), primary: step.State == SetupState.Needed),
        SetupState.Manual when step.Download is not null => ServersButton(Said("server_setup.download"), "link-external", () => OpenInBrowser(step.Download), primary: true),
        _ => null
    };

    /* A language */

    private void FillLanguagePage(StackPanel page, HexServerEntry entry)
    {
        float width = ServersPageWidth;
        page.Add(Words(languages.LanguageNameOf(entry), width, SERVERS_DISPLAY, bold: true));
        page.Add(Words($"{Said("server_window.uses", ("command", UIRichText.MarkdownLiteral(entry.Command)))}  [color:dim]{UIRichText.MarkdownLiteral(entry.About)}[/color]", width, SERVERS_BODY));

        // Setup and Settings, the two of a language
        page.Add(ActionsRow(
        [
            TabButton(Said("server_window.setup_tab"), "checklist", !serversSettingsTab, () => { serversSettingsTab = false; FillLanguageServers(); }),
            TabButton(Said("server_window.settings_tab"), "settings-gear", serversSettingsTab, () => { serversSettingsTab = true; FillLanguageServers(); })
        ]));

        if (serversSettingsTab)
            FillLanguageSettings(page, entry, width);
        else
            FillLanguageSetup(page, entry, width);
    }

    private static Button TabButton(string label, string icon, bool selected, Action pressed) => new()
    {
        Label = label,
        Icon = icon,
        Size = new Vector2(132.0f, SERVERS_BUTTON_HEIGHT),
        Style = "button_flat",
        Selected = selected,
        OnPressed = pressed
    };

    private void FillLanguageSetup(StackPanel page, HexServerEntry entry, float width)
    {
        List<SetupStep> steps = languages.SetupStepsFor(entry);
        int left = HexServerSetup.LeftOf(steps);
        bool checking = steps.Any(step => step.State == SetupState.Checking);

        var actions = new List<UIComponent>();
        if (!checking && left == 0)
        {
            page.Add(Words($"{Coloured(compositor.Skin?.Palette.Success)}$(pass-filled)[/color]  {Said("server_window.language_ready")}", width, 0.15f));
            actions.Add(ServersButton(Said("server_window.restart"), "debug-restart", () => languages.RestartLanguage(entry)));
        }
        else if (!checking)
        {
            page.Add(Words(Said(left == 1 ? "server_window.steps_left_one" : "server_window.steps_left", ("count", Count(left))), width, 0.15f));
            if (HexServerSetup.RunnableInOrder(steps).Count > 0 && !languages.SettingUpAll)
                actions.Add(ServersButton(Said("code_server.set_up", ("language", languages.LanguageNameOf(entry))), "play", () => languages.SetupEverything([entry]), primary: true));
        }
        actions.Add(ServersButton(Said("server_window.check_again"), "refresh", () => languages.ProbeSetup()));
        page.Add(ActionsRow(actions));

        SetupStepKind? kind = null;
        foreach (SetupStep step in steps)
        {
            if (step.Kind != kind)
            {
                kind = step.Kind;
                page.Add(new Spacer { Size = new Vector2(width, 4.0f) });
                page.Add(Words($"**{Said("server_setup.kind_" + step.Kind.ToString().ToLowerInvariant())}**", width, 0.13f, dim: true));
            }
            page.Add(StepCard(entry, step, width));
        }
    }

    // A step: its mark and state, what it is, the command it runs (to copy, or to Run), its log, and its button
    private UIComponent StepCard(HexServerEntry entry, SetupStep step, float width)
    {
        var palette = compositor.Skin?.Palette;
        var card = new Panel { Size = new Vector2(width, 0.0f), Radius = 10, Padding = new UIEdges(14, 12, 14, 12), Color = palette?.Raised, Outline = palette?.Border };
        var column = card.Add(new StackPanel { Spacing = 6, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });

        var (icon, colour, word) = StepMark(step);
        Button? primary = StepButton(entry, step);
        float buttonWidth = primary?.Size.X ?? 0.0f;
        float textWidth = width - 28 - buttonWidth - 12;

        var head = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 12, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        var words = head.Add(new StackPanel { Spacing = 3, Size = new Vector2(textWidth, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        string optional = step.Optional ? $"  [color:dim]{Said("server_setup.optional_tag")}[/color]" : string.Empty;
        words.Add(Words($"{Coloured(colour)}$({icon}) {UIRichText.MarkdownLiteral(word)}{(colour is null ? string.Empty : "[/color]")}   **{UIRichText.MarkdownLiteral(step.Title)}**{optional}", textWidth, 0.145f));
        if (step.About.Length > 0)
            words.Add(Words(UIRichText.MarkdownLiteral(step.About), textWidth, SERVERS_SMALL, dim: true));
        string detail = step.State == SetupState.Blocked ? Said("server_setup.blocked_by", ("waits", step.BlockedBy ?? string.Empty)) : step.Detail;
        if (detail.Length > 0)
            words.Add(Words(UIRichText.MarkdownLiteral(detail), textWidth, SERVERS_SMALL, dim: true));
        if (primary is not null)
            head.Add(primary);

        // The command, as it'll run, for this system, to read, copy, or press Run for
        if (step.Command is { } command && step.State != SetupState.Ready)
        {
            var line = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            var code = new Panel { Size = new Vector2(width - 28 - 96, 0), Radius = 6, Padding = new UIEdges(10, 6, 10, 6), Color = palette?.Field };
            code.Add(new Label { Text = $"`{command.Replace("`", "'", StringComparison.Ordinal)}`", Markdown = true, CodeFont = this.code.Font, TextScale = 0.125f, Wrap = true, Size = new Vector2(width - 28 - 96 - 20, 0), Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            line.Add(code);
            line.Add(ServersButton(Said("server_setup.copy"), "copy", () =>
            {
                UICompositor.Copy(command);
                Say(Said("server_setup.copied"));
            }, width: 88));
            if (step.Folder is { } folder)
                column.Add(Words(Said("server_setup.runs_in", ("folder", UIRichText.MarkdownLiteral(folder))), width - 28, SERVERS_SMALL, dim: true));
        }

        // A download, with the page to get it from
        if (step.State == SetupState.Manual && step.Download is { } page)
            column.Add(Words(Said("server_setup.manual_text", ("page", UIRichText.MarkdownLiteral(page))), width - 28, SERVERS_SMALL, dim: true));

        // How far it's got, and what it's said
        if (step.State == SetupState.Running && languages.SetupProgress(step.Key) is { } progress and > 0.0f)
            column.Add(new ProgressBar { Size = new Vector2(width - 28, 6), Progress = progress, ShowText = false, FillColor = palette?.Accent });

        IReadOnlyList<string> log = languages.SetupLog(step.Key);
        if (log.Count > 0)
        {
            bool open = serversLogsOpen.Contains(step.Key) || step.State is SetupState.Running or SetupState.Failed;
            column.Add(ServersButton(open ? Said("server_setup.hide_log") : Said("server_setup.show_log"), open ? "chevron-up" : "chevron-down", () =>
            {
                if (!serversLogsOpen.Remove(step.Key))
                    serversLogsOpen.Add(step.Key);
                FillLanguageServers();
            }, flat: true));
            if (open)
            {
                var shown = log.Skip(Math.Max(0, log.Count - 30)).Select(said => said.Replace("`", "'", StringComparison.Ordinal));
                var box = column.Add(new Panel { Size = new Vector2(width - 28, 0), Radius = 6, Padding = new UIEdges(10, 8, 10, 8), Color = palette?.Field });
                box.Add(new Label { Text = UIRichText.Literal(string.Join("\n", shown)), CodeFont = this.code.Font, TextScale = 0.115f, Wrap = true, Size = new Vector2(width - 48, 0), Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            }
        }

        return card;
    }

    // A step's mark: its icon, its colour (by what it means) and its word, never the colour alone
    private (string Icon, Vector4? Colour, string Word) StepMark(SetupStep step)
    {
        var palette = compositor.Skin?.Palette;
        return step.State switch
        {
            SetupState.Checking => ("loading~spin", null, Said("server_setup.state_checking")),
            SetupState.Ready => ("pass-filled", palette?.Success, step.Kind == SetupStepKind.Project ? Said("server_setup.state_done") : Said("server_setup.state_ready")),
            SetupState.Needed => ("circle-large-outline", palette?.Warning, step.Kind == SetupStepKind.Project ? Said("server_setup.state_to_do") : Said("server_setup.state_not_installed")),
            SetupState.Optional => ("circle-large-outline", null, step.Kind == SetupStepKind.Project ? Said("server_setup.state_to_do") : Said("server_setup.state_not_installed")),
            SetupState.Outdated => ("arrow-circle-up", palette?.Warning, Said("server_setup.state_outdated")),
            SetupState.Running => ("loading~spin", palette?.Accent, Said("server_setup.state_running")),
            SetupState.Queued => ("watch", palette?.Accent, Said("server_setup.state_queued")),
            SetupState.Failed => ("error", palette?.Error, Said("server_setup.state_failed")),
            SetupState.Blocked => ("lock", null, Said("server_setup.state_blocked")),
            _ => ("link-external", palette?.Warning, Said("server_setup.state_manual"))
        };
    }

    /* A language's Settings */

    private void FillLanguageSettings(StackPanel page, HexServerEntry entry, float width)
    {
        // The server: where it is, started again, what changed in it
        page.Add(SettingsHeading(Said("server_settings.server"), width));
        string path = languages.ServerPathOf(entry);
        var pathBox = new TextBox
        {
            Text = path,
            // What's left of the line after its caption and its two buttons, so neither goes off the edge
            Size = new Vector2(MathF.Max(160, MathF.Min(460, width - 220 - 96 - 120 - 3 * 8)), SERVERS_BUTTON_HEIGHT),
            Padding = new UIEdges(10, 4, 10, 4),
            TextScale = 0.13f,
            Placeholder = Said("server_settings.on_the_path", ("command", entry.Command)),
            MaxLength = 1024
        };
        pathBox.OnSubmitted = typed => languages.SetServerPath(entry, typed.Trim().Trim('"'));
        pathBox.OnChanged = typed => TypedSetting(() =>
        {
            if (typed.Trim().Trim('"') != languages.ServerPathOf(entry))
                languages.SetServerPath(entry, typed.Trim().Trim('"'));
        });
        page.Add(SettingRow(Said("server_settings.where"), null, Said("server_settings.where_help", ("command", entry.Command)), width,
        [
            pathBox,
            ServersButton(Said("server_settings.choose"), "folder-opened", () => { languages.ChooseServerFile(entry); serversDirty = true; }, width: 96),
            ServersButton(Said("server_settings.use_path"), "discard", () => { languages.SetServerPath(entry, string.Empty); FillLanguageServers(); }, width: 120, enabled: path.Length > 0)
        ], below: true));

        var serverActions = new List<UIComponent> { ServersButton(Said("server_window.restart"), "debug-restart", () => languages.RestartLanguage(entry)) };
        if (entry.ReleasesPage.Length > 0)
            serverActions.Add(ServersButton(Said("server_settings.releases"), "link-external", () => OpenInBrowser(entry.ReleasesPage)));
        page.Add(SettingRow(string.Empty, null, null, width, serverActions, below: true));

        // What it's told, the catalog's settings with the user's own over them, a field each of its kind
        page.Add(SettingsHeading(Said("server_settings.told"), width));
        var told = new List<(string Path, JsonNode? Value)>();
        Flatten(languages.SettingsOf(entry), string.Empty, told);
        if (told.Count == 0)
            page.Add(Words(Said("server_settings.told_none"), width, SERVERS_BODY, dim: true));
        foreach (var (key, value) in told)
            page.Add(ServerSettingLine(entry, key, value, width));

        // The language's own
        if (entry.Name.Equals("python", StringComparison.OrdinalIgnoreCase) && languages.SetupFolder is { } folder)
        {
            page.Add(SettingsHeading(Said("server_settings.python"), width));
            string picked = HexLanguages.PythonPickedFor(folder) ?? Said("server_settings.python_found");
            page.Add(SettingRow(Said("server_settings.interpreter"), null, UIRichText.MarkdownLiteral(picked), width,
            [
                ServersButton(Said("server_settings.choose_interpreter"), "list-selection", () => languages.PickPythonInterpreter(folder), width: 170)
            ]));
        }

        if (entry.Name is "csharp" or "typescript")
        {
            page.Add(SettingsHeading(Said("server_settings.colours"), width));
            string now = languages.CodeColors;
            page.Add(SettingRow(Said("server_settings.colours_of"), null, Said("server_settings.colours_help"), width,
            [
                ChoiceButton(Said("server_settings.colours_resharper"), now == "resharper", () => { languages.CodeColors = "resharper"; FillLanguageServers(); }),
                ChoiceButton(Said("server_settings.colours_vs"), now == "visual_studio", () => { languages.CodeColors = "visual_studio"; FillLanguageServers(); }),
                ChoiceButton(Said("server_settings.colours_skin"), now == "skin", () => { languages.CodeColors = "skin"; FillLanguageServers(); })
            ], below: true));
        }

        // The files it opens
        page.Add(SettingsHeading(Said("server_settings.files"), width));
        page.Add(Words(Said("server_settings.files_text", ("extensions", UIRichText.MarkdownLiteral(string.Join("  ", entry.Extensions)))), width, SERVERS_BODY, dim: true));
    }

    // The leaves of a server's settings, by their dotted paths, html.format.wrapLineLength
    private static void Flatten(JsonNode? node, string path, List<(string, JsonNode?)> into)
    {
        if (node is JsonObject inside)
        {
            foreach (var (key, value) in inside)
                Flatten(value, path.Length == 0 ? key : $"{path}.{key}", into);
            return;
        }
        if (path.Length > 0)
            into.Add((path, node));
    }

    // One of a server's settings: a switch, a number or a text, and Reset when it was changed
    private UIComponent ServerSettingLine(HexServerEntry entry, string path, JsonNode? value, float width)
    {
        var controls = new List<UIComponent>();
        if (value is JsonValue flag && flag.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            bool on = flag.GetValueKind() == System.Text.Json.JsonValueKind.True;
            var toggle = new ToggleButton(on ? Said("extensions_window.on") : Said("extensions_window.off"), on) { Size = new Vector2(84, SERVERS_BUTTON_HEIGHT), Style = "switch" };
            toggle.OnPressed = () => { languages.SetServerSetting(entry, path, JsonValue.Create(toggle.State)); FillLanguageServers(); };
            controls.Add(toggle);
        }
        else
        {
            bool number = value is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number;
            bool list = value is JsonArray;
            string text = value switch
            {
                JsonArray items => string.Join(", ", items.Select(item => item is JsonValue v && v.TryGetValue(out string? s) ? s : item?.ToJsonString() ?? string.Empty)),
                JsonValue v when v.TryGetValue(out string? s) => s,
                null => string.Empty,
                _ => value.ToJsonString()
            };
            var box = new TextBox { Text = text, Size = new Vector2(MathF.Min(320, width - 330), SERVERS_BUTTON_HEIGHT), Padding = new UIEdges(10, 4, 10, 4), TextScale = 0.13f, MaxLength = 1024 };
            void Commit(string typed)
            {
                typed = typed.Trim();
                if (typed == text)
                    return;
                JsonNode? made = number && double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? JsonValue.Create(parsed)
                    : list ? new JsonArray([.. typed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => (JsonNode)JsonValue.Create(item)!)])
                    : JsonValue.Create(typed);
                languages.SetServerSetting(entry, path, made);
                serversDirty = true;
            }
            box.OnSubmitted = Commit;
            box.OnChanged = typed => TypedSetting(() => Commit(typed));
            controls.Add(box);
        }

        // Named the way the settings window names its rows, by the last word of its key, with the key small after it.
        // A setting that costs something says so under what it does
        bool changed = languages.ServerSettingChanged(entry, path);
        var (help, cost) = ServerSettingAbout(path);
        return SettingRow(ServerSettingTitle(path), path, help, width, controls, cost: cost,
            reset: changed ? () => { languages.SetServerSetting(entry, path, null); FillLanguageServers(); } : null);
    }

    // What a server's setting does and what it costs, for the ones Hex knows of, nulls for the rest
    private (string? Help, string? Cost) ServerSettingAbout(string path) => path switch
    {
        HexLanguages.CSHARP_ANALYZERS => (Said("server_settings.csharp_analyzers"), Said("server_settings.csharp_analyzers_cost")),
        _ => (null, null)
    };

    // A server's key as a name, its last word split where its capitals are: wrapLineLength is Wrap line length
    internal static string ServerSettingTitle(string path)
    {
        string last = path[(path.LastIndexOf('.') + 1)..];
        var said = new System.Text.StringBuilder(last.Length + 4);
        for (int i = 0; i < last.Length; i++)
        {
            char c = last[i];
            if (i == 0)
                said.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c) && !char.IsUpper(last[i - 1]))
                said.Append(' ').Append(char.ToLowerInvariant(c));
            else
                said.Append(c);
        }

        return said.ToString();
    }

    private Label SettingsHeading(string text, float width) => new()
    {
        Text = $"**{UIRichText.MarkdownLiteral(text)}**",
        Markdown = true,
        TextScale = 0.16f,
        Size = new Vector2(width, 34),
        Align = Origin.BottomLeft,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft
    };

    // How wide the column down the right is that a row's switch or box sits in, the gap before it, and the most a
    // row's sentence runs to, the settings window's own
    private const float SERVERS_CONTROL_COLUMN = 300.0f, SERVERS_COLUMN_GAP = 24.0f, SERVERS_HELP_MOST = 640.0f;

    // A setting the way the settings window has its rows: its name in bold with a reset while it's changed and its key
    // small and dim after them, the sentence of what it does under them, what it costs under that when it costs
    // something, and what sets it in a column down the right. Buttons and anything wide go on a line under the sentence
    private StackPanel SettingRow(string title, string? key, string? help, float width, IEnumerable<UIComponent> controls, bool below = false, string? cost = null, Action? reset = null)
    {
        var row = new StackPanel { Spacing = 4, Padding = new UIEdges(0, 6, 0, 6), Size = new Vector2(width, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };
        bool side = !below && width - SERVERS_CONTROL_COLUMN - SERVERS_COLUMN_GAP >= 280.0f;
        StackPanel text = row, line;
        float textWidth = width;
        if (side)
        {
            textWidth = width - SERVERS_CONTROL_COLUMN - SERVERS_COLUMN_GAP;
            var body = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = SERVERS_COLUMN_GAP, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            text = body.Add(new StackPanel { Spacing = 4, Size = new Vector2(textWidth, 0), Anchor = Origin.Top, Pivot = Origin.Top });
            var column = body.Add(new StackPanel { Size = new Vector2(SERVERS_CONTROL_COLUMN, 0), Anchor = Origin.Top, Pivot = Origin.Top });
            line = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.Right, Pivot = Origin.Right });
        }
        else
            line = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };

        if (title.Length > 0)
        {
            var top = text.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.Left, Pivot = Origin.Left });
            top.Add(new Label { Text = $"**{UIRichText.MarkdownLiteral(title)}**", Markdown = true, TextScale = SERVERS_BODY, Size = new Vector2(0, 26), Align = Origin.Left });
            if (reset is not null)
                top.Add(new Button { Icon = "discard", Style = "button_flat", Animated = false, Size = new Vector2(26, 26), Tooltip = Said("server_settings.reset"), OnPressed = reset });
            if (key is not null)
            {
                var keyLabel = top.Add(new Label { Text = $"`{key}`", Markdown = true, Dim = true, TextScale = SERVERS_SMALL, Size = new Vector2(0, 26), Align = Origin.Left });
                keyLabel.CodeFont = code.Font;
            }
        }

        float helpWidth = MathF.Min(SERVERS_HELP_MOST, textWidth);
        if (help is { Length: > 0 })
            text.Add(Words(help, helpWidth, SERVERS_BODY * 0.95f, dim: true));
        if (cost is { Length: > 0 })
            text.Add(Words($"**{UIRichText.MarkdownLiteral(Said("server_settings.cost"))}** {cost}", helpWidth, SERVERS_BODY * 0.95f, dim: true));

        foreach (UIComponent control in controls)
            line.Add(control);
        if (!side && line.Children.Count > 0)
            row.Add(line);
        return row;
    }

    private static Button ChoiceButton(string label, bool chosen, Action pressed) => new()
    {
        Label = label,
        Size = new Vector2(MathF.Max(110, 36 + label.Length * 7.5f), SERVERS_BUTTON_HEIGHT),
        Style = chosen ? "button" : "button_flat",
        Selected = chosen,
        Primary = chosen,
        OnPressed = pressed
    };

    /* All languages */

    private void FillAllLanguages(StackPanel page)
    {
        float width = ServersPageWidth;
        page.Add(Words(Said("server_window.all_title"), width, SERVERS_TITLE, bold: true));

        var search = new TextBox
        {
            Text = serversFilter,
            Size = new Vector2(MathF.Min(360, width), SERVERS_BUTTON_HEIGHT),
            Padding = new UIEdges(10, 4, 10, 4),
            TextScale = 0.135f,
            Placeholder = Said("server_window.filter"),
            MaxLength = 100,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft
        };
        search.OnChanged = typed =>
        {
            serversFilter = typed.Trim();
            serversDirty = true;
            refocusServersFilter = true;
        };
        page.Add(search);
        if (refocusServersFilter)
        {
            refocusServersFilter = false;
            search.Focus();
            search.Select(search.Text.Length, search.Text.Length);
        }

        var here = languages.WorkspaceEntries().Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = languages.ServerEntries.Where(entry => Matches(serversFilter, entry.Name, languages.LanguageNameOf(entry), entry.Command, entry.About, string.Join(' ', entry.Extensions)))
            .Select(entry => (Entry: entry, Steps: languages.SetupStepsFor(entry))).ToList();

        void Group(string title, IEnumerable<(HexServerEntry Entry, List<SetupStep> Steps)> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0)
                return;
            page.Add(Words($"**{UIRichText.MarkdownLiteral(title)}**  [color:dim]{Count(list.Count)}[/color]", width, 0.14f));
            foreach (var (entry, steps) in list)
                page.Add(LanguageRow(entry, steps, width));
        }

        bool Installed(List<SetupStep> steps) => steps.Any(step => step.Kind == SetupStepKind.Server && step.State is SetupState.Ready or SetupState.Outdated);
        Group(Said("server_window.this_workspace"), shown.Where(pair => here.Contains(pair.Entry.Name)));
        Group(Said("server_window.installed"), shown.Where(pair => !here.Contains(pair.Entry.Name) && Installed(pair.Steps)));
        Group(Said("server_window.not_installed"), shown.Where(pair => !here.Contains(pair.Entry.Name) && !Installed(pair.Steps)));

        if (shown.Count == 0)
            page.Add(Words(Said("extensions_window.nothing_matches", ("query", UIRichText.MarkdownLiteral(serversFilter))), width, SERVERS_BODY, dim: true));
    }

    private bool refocusServersFilter;

    /* Advanced */

    private void FillServersAdvanced(StackPanel page)
    {
        float width = ServersPageWidth;
        page.Add(Words(Said("server_window.advanced_title"), width, SERVERS_TITLE, bold: true));

        var updates = new ToggleButton(languages.ServerUpdates ? Said("extensions_window.on") : Said("extensions_window.off"), languages.ServerUpdates) { Size = new Vector2(84, SERVERS_BUTTON_HEIGHT), Style = "switch" };
        updates.OnPressed = () => { languages.ServerUpdates = updates.State; FillLanguageServers(); };
        page.Add(SettingRow(Said("language_settings.updates"), null, Said("language_settings.updates_help"), width, [ServersButton(Said("language_settings.check_now"), "refresh", languages.CheckForServerUpdates, width: 120), updates]));

        var cards = new ToggleButton(languages.MissingCards ? Said("extensions_window.on") : Said("extensions_window.off"), languages.MissingCards) { Size = new Vector2(84, SERVERS_BUTTON_HEIGHT), Style = "switch" };
        cards.OnPressed = () => { languages.MissingCards = cards.State; Context.Settings.Save(); FillLanguageServers(); };
        page.Add(SettingRow(Said("code_server.missing_cards"), null, Said("code_server.missing_cards_help"), width, [cards]));

        page.Add(new Spacer { Size = new Vector2(width, 8.0f) });
        page.Add(languages.AddServerForm());
    }

    /* Pieces */

    private static Label Words(string text, float width, float scale, bool dim = false, bool bold = false) => new()
    {
        Text = bold ? $"**{text}**" : text,
        Markdown = true,
        Dim = dim,
        TextScale = scale,
        Wrap = true,
        Size = new Vector2(width, 0),
        Align = Origin.TopLeft,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft
    };

    private static StackPanel ActionsRow(IEnumerable<UIComponent> actions)
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };
        foreach (UIComponent action in actions)
            row.Add(action);
        return row;
    }

    private static Button ServersButton(string label, string icon, Action pressed, bool primary = false, bool enabled = true, float width = 0, bool flat = false) => new()
    {
        Label = label,
        Icon = icon,
        Size = new Vector2(width > 0 ? width : MathF.Max(96, 44 + label.Length * 7.5f), SERVERS_BUTTON_HEIGHT),
        Primary = primary,
        Enabled = enabled,
        Style = primary ? "button" : flat ? "button_flat" : "button_flat",
        OnPressed = pressed
    };

    private string Coloured(Vector4? colour) => colour is { } c ? $"[color:{HexColour(c)}]" : string.Empty;

    // "C# and Python", "C#, Python and Go"
    private string Joined(List<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " " + Said("server_window.and") + " " + names[^1]
    };
}
