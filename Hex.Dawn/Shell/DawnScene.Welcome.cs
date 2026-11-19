using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;
using System.Numerics;

namespace Hex.Dawn;

// What Hex opens on when nothing is open. Hex always has a document, it starts with an empty layout, so the welcome is a
// page over that layout's canvas, there while the empty layout is all there is and gone the moment a component goes into
// it or a file is opened. New, open, a folder, the commands, the help, and the files worked on last. Started on a folder
// of code (no layouts near its top) or on nothing, the welcome comes up in that layout's code view instead, with no
// designer round it, no Design, Code and Play over it and no Properties beside it. Somebody who writes Go never sees
// a layout designer they didn't ask for. A folder of layouts and every self test start on the designer as ever.
internal sealed partial class DawnScene
{
    private const string WELCOME_LAYOUT = "welcome.hor";

    private Panel? welcome;
    private readonly List<WelcomeSizing> welcomeSizes = [];

    // The two Welcome pages keep their own column widths, including while the other one is shown.
    private sealed class WelcomeSizing(Panel page, UILayout made)
    {
        private readonly StackPanel content = made.Get<StackPanel>("page"), columns = made.Get<StackPanel>("columns"),
            start = made.Get<StackPanel>("start"), recent = made.Get<StackPanel>("recent_column");
        private readonly Label subtitle = made.Get<Label>("subtitle");
        public readonly StackPanel List = made.Get<StackPanel>("recent");
        private readonly StackPanel news = made.Get<StackPanel>("news");
        public readonly Button CommandsButton = made.Get<Button>("commands");
        public readonly StackPanel Agents = made.Get<StackPanel>("agents");
        public readonly StackPanel Getting = made.Get<StackPanel>("getting");
        private readonly StackPanel gettingColumn = made.Get<StackPanel>("getting_column"), footer = made.Get<StackPanel>("footer"),
            side = made.Get<StackPanel>("side_column");
        private readonly Spacer top = made.Get<Spacer>("welcome_top"), agentsGap = made.Get<Spacer>("agents_gap");
        private readonly Label agentsTitle = made.Get<Label>("agents_title");

        // The widest the page goes, and from how wide the agents get a column of their own
        private const float WIDEST = 1240.0f, THREE_COLUMNS = 980.0f;

        /// <summary>Getting started made again at a width, the scene's (it knows the settings), whenever the column changes width.</summary>
        public Action<StackPanel, float>? FillGetting;

        /// <summary>How wide Getting started's column was made, for making it again at the same width when what it says changes.</summary>
        public float GettingWidth { get; private set; }

        public void Invalidate() => width = 0;
        private float width;
        private UISkin? measuredWith;

        public void Fit(UISkin? skin)
        {
            if (!page.Visible || page.Bounds.Width <= 0.0f)
                return;
            // The room over the page a share of the window's height, so a tall window doesn't have it all under the page
            float over = MathF.Round(Math.Clamp(page.Bounds.Height * 0.08f, 32.0f, 112.0f));
            if (top.Size.Y != over)
                top.Size = top.Size with { Y = over };

            float wanted = Math.Clamp(page.Bounds.Width - 104.0f, 160.0f, WIDEST);
            if (wanted == width && measuredWith == skin)
                return;
            width = wanted;
            measuredWith = skin;
            bool stacked = width < 560.0f, three = width >= THREE_COLUMNS;
            columns.Direction = stacked ? UIDirection.Vertical : UIDirection.Horizontal;
            columns.Spacing = three ? 56.0f : 40.0f;
            content.Size = content.Size with { X = width };
            columns.Size = columns.Size with { X = width };
            subtitle.Size = subtitle.Size with { X = Math.Min(width, 640.0f) };

            // Getting started the widest of three, its rows a description and a switch each; two halves on a middling page
            float room = width - columns.Spacing * (three ? 2 : 1);
            float column = stacked ? width : three ? MathF.Floor(room * 0.29f) : MathF.Floor(room * 0.5f);
            float getting = stacked ? width : three ? room - column * 2.0f : column;
            recent.Size = recent.Size with { X = column };
            gettingColumn.Size = gettingColumn.Size with { X = getting };
            side.Size = side.Size with { X = column };
            side.Visible = three;

            // The agents in their own column on a wide page, under the recent files otherwise
            StackPanel agentsIn = three ? side : recent;
            if (Agents.Parent != agentsIn)
            {
                agentsIn.Add(agentsGap);
                agentsIn.Add(agentsTitle);
                agentsIn.Add(Agents);
            }
            // Heading the column of its own level with the other two, the gap's own spacing gone with it
            agentsGap.Visible = !three;

            // The row of what to start with, each as wide as its words, one over the other a line each on a narrow page
            start.Direction = stacked ? UIDirection.Vertical : UIDirection.Horizontal;
            start.Size = start.Size with { X = width };
            foreach (Button action in start.Children.OfType<Button>())
            {
                action.Size = new Vector2(stacked ? width : WordsWidth(action, skin), 36);
                action.Align = stacked ? Origin.Left : Origin.Center;
            }
            footer.Direction = stacked ? UIDirection.Vertical : UIDirection.Horizontal;
            foreach (Button help in footer.Children.OfType<Button>())
            {
                help.Size = new Vector2(stacked ? width : WordsWidth(help, skin), 30);
                help.Align = Origin.Left;
            }

            GettingWidth = getting;
            FillGetting?.Invoke(Getting, getting);
            foreach (UIComponent row in List.Children)
            {
                if (row is Label hint)
                    hint.Size = hint.Size with { X = column };
                else if (row is Button link)
                    FitFile(link, column, skin);
            }
            foreach (Button agent in Agents.Children.OfType<Button>())
            {
                agent.Size = new Vector2(column, 36);
                agent.Align = Origin.Left;
            }

            // What's new, each line wrapped to its column with its button under it
            foreach (StackPanel line in news.Children.OfType<StackPanel>())
            {
                foreach (UIComponent part in line.Children)
                {
                    if (part is Label said)
                        said.Size = said.Size with { X = column };
                    else if (part is Button go)
                    {
                        go.Size = new Vector2(column, 30);
                        go.Align = Origin.Left;
                    }
                }
            }
        }

        // How wide a button is to say its icon and its words, with the room a button has round them
        private static float WordsWidth(Button button, UISkin? skin) => skin is null ? 160.0f
            : MathF.Ceiling(skin.Font.Measure($"$({button.Icon}) {button.Label}", button.LabelScale).X + skin.ButtonPadding.Total.X + 16.0f);

        // A long name loses its middle first, the full path still there on hover.
        private static void FitFile(Button link, float column, UISkin? skin)
        {
            link.Size = new Vector2(column, 36);
            link.Align = Origin.Left;
            string name = Path.GetFileName(link.Tooltip);
            int length = name.Length;
            if (skin is not null)
                while (length > 4 && skin.Font.Measure($"$({link.Icon}) {HexText.MiddleOf(name, length)}", link.LabelScale).X
                    + skin.ButtonPadding.Total.X > column)
                    length--;
            link.Label = HexText.MiddleOf(name, length);
        }
    }

    // The empty layout Hex started with, which the welcome is over
    private HexDocument? startedWith;

    // The editor testing itself starts on the empty layout as it always did, unless a test asks for the welcome
    private bool welcomeWanted;

    /// <summary>Whether the welcome is what is on screen, nothing of anybody's open and the layout Hex started with untouched.</summary>
    private bool Welcoming => (welcomeWanted || !options.SelfTest) && documents.Count == 1 && document == startedWith
        && document is { Path: null, IsText: false, Modified: false } && !document.Module.Root.Children.Any();

    /// <summary>Every tick, the welcome put up or taken down for what is open.</summary>
    private void UpdateWelcome()
    {
        DropPlainLayout();
        bool welcoming = Welcoming;

        // Started on code, the empty layout goes to its code view once and the welcome goes over its code. Only once for
        // each, F7 still takes it to the designer
        if (plainWelcome && welcoming && plainFor != document)
        {
            plainFor = document;
            if (document.View == HexView.Design)
            {
                document.View = HexView.Code;
                ApplyView();
            }
        }

        bool plain = plainWelcome && welcoming && document == plainFor && document.View == HexView.Code;
        ShowPlainChrome(plain);

        bool wanted = welcoming && LayoutTools;
        if (wanted != welcome is { Visible: true })
        {
            if (wanted)
                (welcome ??= MakeWelcome(canvas)).Visible = true;
            else if (welcome is not null)
                welcome.Visible = false;
        }

        if (plain != plainPage is { Visible: true })
        {
            if (plain)
                (plainPage ??= MakeWelcome(codeScroll)).Visible = true;
            else if (plainPage is not null)
                plainPage.Visible = false;
        }

        // Over the code, which goes back into the panel after it whenever the editors are placed again
        if (plain && plainPage is not null && codeScroll.Children[^1] != plainPage)
        {
            codeScroll.Remove(plainPage);
            codeScroll.Add(plainPage);
        }
        foreach (WelcomeSizing sizing in welcomeSizes)
            sizing.Fit(compositor.Skin);

        // Getting started says how the language servers are, made again once they've been looked for or set up
        if (welcoming && gettingSetupShown != languages.SetupVersion)
            RefillGettingStarted();
    }

    /// <summary>The welcome made in a panel, the canvas of the empty layout or the code panel for the plain one.</summary>
    private Panel MakeWelcome(UIComponent into)
    {
        UILayout made = layout.Instantiate(WELCOME_LAYOUT, into);
        Panel page = made.Get<Panel>("welcome");

        // The card's own colour, the middle of the window it sits in. Its own near black was a slab in the navy
        if (compositor.Skin is { } skin)
            page.Color = skin.PanelColor with { W = 1.0f };

        var newLayout = made.Get<Button>("new_layout");
        var openFile = made.Get<Button>("open_file");
        newLayout.OnPressed = () => Show(NewDocument());
        openFile.OnPressed = OpenPressed;
        var (newFile, newProject) = PutNewFile(openFile);
        var openFolder = made.Get<Button>("open_folder");
        openFolder.OnPressed = () => explorer.AskFolder(explorer.OpenFolder);

        // The row in order: Open Folder first, the one primary, then what's quieter. On the plain welcome a file before a
        // layout, somebody who writes Go wants one; on the designer's a layout first
        Button[] order = into == codeScroll
            ? [openFolder, newFile, openFile, newProject, newLayout]
            : [openFolder, newLayout, newFile, openFile, newProject];
        if (openFolder.Parent is StackPanel row)
            for (int i = 0; i < order.Length; i++)
                row.MoveChild(order[i], i);
        made.Get<Button>("commands").OnPressed = CommandPalette;
        made.Get<Button>("commands").Label = WithCommandShortcut(Said("welcome.commands"), "menus.command_palette");
        made.Get<Button>("keys").OnPressed = OpenKeysWindow;
        made.Get<Button>("language").OnPressed = ShowLanguageHelp;
        made.Get<Button>("changes").OnPressed = ShowChangelog;
        made.Get<Label>("subtitle").Text = Said("welcome.subtitle", ("version", HexAbout.Label));

        // What the release is called and its number under the name, the name away for a release that has none
        made.Get<Label>("release_name").Text = Hex.Shell.HexVersion.Name;
        made.Get<Label>("release_name").Visible = Hex.Shell.HexVersion.Name.Length > 0;
        made.Get<Label>("release_number").Text = Hex.Shell.HexVersion.Number;
        // The build's own mark, the grey one in a Debug build and the nightly's in a nightly, as the window's is
        made.Get<Image>("icon").File = Hex.Shell.HexIdentity.ICON;

        FillWelcomeNews(made);
        var sizing = new WelcomeSizing(page, made) { FillGetting = FillGettingStarted };
        if (welcomeSizes.Count == 0 && !HexAi.Off)
            agentsChanged += FillWelcomeAgents;
        welcomeSizes.Add(sizing);
        FillWelcomeRecent(sizing);

        // No agents' row with No AI on, its title and the gap over it go with it
        if (HexAi.Off)
        {
            foreach (string part in (ReadOnlySpan<string>)["agents_gap", "agents_title", "agents"])
                made.Get<UIComponent>(part).Visible = false;
        }
        else
            FillWelcomeAgents(sizing);
        return page;
    }

    // What the agents' row said last, it's filled again only when that moves
    private string welcomeAgentsSaid = string.Empty;

    // The longest a chat's title is in the row before it's cut short
    private const int WELCOME_AGENT_TITLE = 40;

    /// <summary>The agents' row of every welcome page filled again once the chats have moved in a way it shows.</summary>
    private void FillWelcomeAgents()
    {
        (HexAgentChat? last, int wanting) = WelcomeAgents();
        string said = $"{last?.Id}|{last?.Shown}|{wanting}";
        if (said == welcomeAgentsSaid)
            return;
        welcomeAgentsSaid = said;
        foreach (WelcomeSizing sizing in welcomeSizes)
            FillWelcomeAgents(sizing);
    }

    // The chat to carry on with, the last one somebody started that has anything in it, and how many want looking at
    private (HexAgentChat? Last, int Wanting) WelcomeAgents() =>
        (agentChats.LastOrDefault(chat => chat.Parent is null && chat.Entries.Count > 0), AgentsWanting());

    /// <summary>
    /// The agents' row of a welcome page, a new agent, the last chat to carry on with and how many want looking at, each
    /// going to Agentic.
    /// </summary>
    private void FillWelcomeAgents(WelcomeSizing sizing)
    {
        StackPanel list = sizing.Agents;
        foreach (UIComponent child in list.Children.ToArray())
            list.Remove(child);

        (HexAgentChat? last, int wanting) = WelcomeAgents();
        WelcomeAgentButton(list, "add", Said("welcome.new_agent"), Said("welcome.new_agent_tip"), () =>
        {
            ShowFace(Hex.Shell.HexFace.Noon);
            NewAgentChat();
        });

        if (last is not null)
        {
            string title = last.Shown.ReplaceLineEndings(" ");
            title = title.Length > WELCOME_AGENT_TITLE ? title[..(WELCOME_AGENT_TITLE - 1)].TrimEnd() + "…" : title;
            WelcomeAgentButton(list, "comment-discussion", Said("welcome.continue_agent", ("title", title)), Said("welcome.continue_agent_tip"), () =>
            {
                ShowFace(Hex.Shell.HexFace.Noon);
                OpenAgentChat(last);
            });
        }

        if (wanting > 0)
        {
            WelcomeAgentButton(list, "bell-dot", Said("welcome.agents_wanting", ("count", wanting.ToString(System.Globalization.CultureInfo.InvariantCulture))), Said("welcome.agents_wanting_tip"), () =>
            {
                ShowFace(Hex.Shell.HexFace.Noon);
                NextNeedingMe();
            });
        }

        sizing.Invalidate();
    }

    // What Getting started said last, by the language servers' and the settings' versions, made again when either moves
    private int gettingSetupShown = -1;

    /// <summary>
    /// Getting started: the settings that help and that nobody finds by looking, in groups. Each a row of its name (which
    /// opens it in the settings), how it is now, and what changes it there and then: a switch for one that's on or off,
    /// a button for the rest. The ones about agents go with No AI on.
    /// </summary>
    private void FillGettingStarted(StackPanel list, float width)
    {
        foreach (UIComponent child in list.Children.ToArray())
            list.Remove(child);
        gettingSetupShown = languages.SetupVersion;

        void Group(string key)
        {
            list.Add(new Spacer { Size = new Vector2(width, list.Children.Count == 0 ? 2 : 12) });
            list.Add(new Label { Text = Said("welcome.getting_" + key), Dim = true, TextScale = 0.13f, Size = new Vector2(width, 22), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        }

        // A row: its icon, its name over how it is now, and its switch or button at the right
        void Row(string icon, string name, string said, string? key, UIComponent control)
        {
            float controlWidth = control.Size.X;
            float words = MathF.Max(120, width - 28 - controlWidth - 16);
            var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(width, 0) };
            row.Add(new Label { Text = $"$({icon})", TextScale = 0.16f, Size = new Vector2(20, 40), Align = Origin.Center, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            var text = row.Add(new StackPanel { Spacing = 0, Size = new Vector2(words, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            text.Add(new Button
            {
                Label = Said("welcome.getting_" + name),
                Style = "button_flat",
                LabelScale = 0.15f,
                Align = Origin.Left,
                Animated = false,
                Size = new Vector2(words, 24),
                Tooltip = key is not null ? Said("welcome.getting_in_settings") : string.Empty,
                Anchor = Origin.TopLeft,
                Pivot = Origin.TopLeft,
                OnPressed = key is not null ? () => OpenSettingAt(key) : null
            });
            // Under the name's words, which start after the flat button's padding
            var about = text.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 0, Size = new Vector2(words, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            about.Add(new Spacer { Size = new Vector2(compositor.Skin?.ButtonPadding.Left ?? 12, 1) });
            about.Add(new Label { Text = said, Dim = true, TextScale = 0.125f, Wrap = true, Size = new Vector2(words - 24, 0), Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            row.Add(control);
            list.Add(row);
        }

        // A setting that's on or off as a switch, changed the way the settings change it
        UIComponent Switch(string key)
        {
            bool on = SettingFlag(key);
            var toggle = new ToggleButton(on ? Said("extensions_window.on") : Said("extensions_window.off"), on) { Size = new Vector2(84, 30), Style = "switch", Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };
            toggle.OnPressed = () =>
            {
                SetSettingFlag(key, toggle.State);
                RefillGettingStarted();
            };
            return toggle;
        }

        Button Action(string label, Action pressed, bool primary = false) => new()
        {
            Label = Said("welcome.getting_" + label),
            Style = primary ? "button" : "button_flat",
            Primary = primary,
            LabelScale = 0.14f,
            Size = new Vector2(96, 30),
            Animated = false,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            OnPressed = pressed
        };

        Group("writing");
        Row("whole-word", "spelling", Said("welcome.getting_spelling_about"), "spell_check", Switch("spell_check"));
        Row("wand", "format", Said("welcome.getting_format_about"), "format_on_save", Switch("format_on_save"));

        // The workspace's languages, and how many things are left before they work
        Group("languages");
        List<HexServerEntry> here = languages.WorkspaceEntries();
        var steps = here.Select(entry => (Entry: entry, Steps: languages.SetupStepsFor(entry))).ToList();
        int left = steps.Sum(pair => HexServerSetup.LeftOf(pair.Steps));
        var waiting = steps.Where(pair => HexServerSetup.LeftOf(pair.Steps) > 0).Select(pair => languages.LanguageNameOf(pair.Entry)).ToList();
        string servers = here.Count == 0 ? Said("welcome.getting_servers_none")
            : steps.Any(pair => pair.Steps.Any(step => step.State == SetupState.Checking)) ? Said("welcome.getting_servers_checking")
            : left > 0 ? Said("welcome.getting_servers_left", ("count", left.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("languages", Joined(waiting)))
            : Said("welcome.getting_servers_ready", ("languages", Joined([.. here.Select(languages.LanguageNameOf)])));
        Row("server-process", "servers", servers, null, left > 0 ? Action("set_up", () => OpenLanguageServers(), primary: true) : Action("open", () => OpenLanguageServers()));

        Group("look_keys");
        string look = UISkins.Theme is { } theme ? $"{UISkins.Current.Title}, {theme}" : UISkins.Current.Title;
        Row("paintcan", "look", Said("welcome.getting_look_about", ("look", look)), "look", Action("change", () => OpenSettingAt("look")));
        Row("keyboard", "vs_keys", Said("welcome.getting_vs_keys_about"), "visual_studio_keys", Switch("visual_studio_keys"));
        Row("arrow-swap", "import", Said("welcome.getting_import_about"), null, Action("import_now", ImportVsCodeSettings));

        Group("speed");
        Row("zap", "persistent", Said("welcome.getting_persistent_about"), "persistent_process", Switch("persistent_process"));
        if (OperatingSystem.IsWindows())
            Row("debug-start", "at_login", Said("welcome.getting_at_login_about"), "persistent_at_login", Switch("persistent_at_login"));

        Group("ai");
        Row("circle-slash", "no_ai", Said("welcome.getting_no_ai_about"), "no_ai", Switch("no_ai"));
        if (!HexAi.Off)
            Row("plug", "mcp", Said("welcome.getting_mcp_about"), "mcp_server", Switch("mcp_server"));
    }

    /// <summary>Every welcome page's Getting started made again at the width it was, after something it says changed.</summary>
    private void RefillGettingStarted()
    {
        foreach (WelcomeSizing sizing in welcomeSizes)
            if (sizing.GettingWidth > 0)
                FillGettingStarted(sizing.Getting, sizing.GettingWidth);
    }

    // The settings' source a key is one of, the one the settings window would change it through
    private ISettingsSource? SettingSourceOf(string key)
    {
        var sources = new List<ISettingsSource> { new EditorSource(this) };
        sources.AddRange(settingSources.Select(added => added.Source()));
        return sources.FirstOrDefault(source => source.Settings.Any(setting => setting.Key == key));
    }

    // Whether a setting that's on or off is on, as the settings window reads it
    private bool SettingFlag(string key) => SettingSourceOf(key)?.Get(key) is Horizon.HIDL.Runtime.BooleanValue { Value: true };

    // A setting that's on or off changed through its source, applied and saved the way the settings window does it
    private void SetSettingFlag(string key, bool on)
    {
        if (SettingSourceOf(key) is not { } source)
            return;
        source.Set(key, new Horizon.HIDL.Runtime.BooleanValue(on));
        SaveSettings();
        SettingsPage?.Refresh(key);
    }

    /// <summary>The settings, brought up scrolled to one setting with it picked out.</summary>
    private void OpenSettingAt(string key)
    {
        OpenSettings(null);
        settingsView?.Reveal(key);
    }

    private static void WelcomeAgentButton(StackPanel list, string icon, string label, string tip, Action pressed) =>
        list.Add(new Button
        {
            Icon = icon, Label = label, Tooltip = tip, Style = "button_flat", LabelScale = 0.16f, Animated = false,
            Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, OnPressed = pressed
        });

    /// <summary>
    /// What the release that's running brought, a line each and a button under it that goes to the thing, the setting,
    /// the view of the side or the panel. The section is away for a version with nothing listed (HexWhatsNew).
    /// </summary>
    private void FillWelcomeNews(UILayout made)
    {
        IReadOnlyList<HexNewsItem> items = HexWhatsNew.For(Hex.Shell.HexVersion.Number);
        made.Get<StackPanel>("news_column").Visible = items.Count > 0;
        made.Get<Label>("news_title").Text = Said("welcome.news", ("version", HexAbout.Label));
        StackPanel list = made.Get<StackPanel>("news");
        foreach (HexNewsItem item in items)
        {
            var line = list.Add(new StackPanel { Spacing = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left });
            line.Add(new Label
            {
                Text = Said("whats_new." + item.Text), TextScale = 0.15f, Wrap = true, Dim = true,
                Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(300, 0)
            });
            line.Add(new Button
            {
                Label = Said("whats_new." + item.Go), Icon = item.Icon, Style = "button_flat", LabelScale = 0.15f, Animated = false,
                Anchor = Origin.Left, Pivot = Origin.Left,
                Tooltip = Said(item.Place switch
                {
                    HexNewsPlace.Side => "welcome.news_tip_side",
                    HexNewsPlace.Problems => "welcome.news_tip_problems",
                    HexNewsPlace.NewProject => "new_project.title",
                    _ => "welcome.news_tip_setting"
                }),
                OnPressed = () => GoToNews(item)
            });
        }

        // The whole of what changed, every release's notes, a link under the lines
        list.Add(new Label
        {
            Text = Said("welcome.news_all"), Markdown = true, TextScale = 0.15f,
            Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left,
            OnLink = _ => ShowChangelog()
        });
    }

    /// <summary>Goes to where something new is, its setting's row, its section of the settings, its view of the side or the panel.</summary>
    internal void GoToNews(HexNewsItem item)
    {
        switch (item.Place)
        {
            case HexNewsPlace.Setting:
                OpenSettings(null);
                settingsView?.Reveal(item.Target);
                break;
            case HexNewsPlace.Settings:
                OpenSettings(item.Target);
                break;
            case HexNewsPlace.Side:
                // Showing the view that's already up would put the side bar away
                if (Side != item.Side || !left.Shown)
                    ShowSide(item.Side);
                break;
            case HexNewsPlace.Problems:
                ShowPanel(PROBLEMS_PAGE);
                break;
            case HexNewsPlace.NewProject:
                ShowNewProject();
                break;
        }
    }

    // Both pages live for the session, including while hidden behind a document or the other view.
    private void FillWelcomeRecent(WelcomeSizing sizing)
    {
        StackPanel list = sizing.List;
        foreach (UIComponent child in list.Children.ToArray())
            list.Remove(child);
        var rows = layout.Populate(list, recent.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            string path = recent[i];
            var link = rows[i].Get<Button>("link");
            link.Label = Path.GetFileName(path);
            link.LabelScale = 0.16f;
            link.Icon = HexExplorer.FileIcon(path);
            link.Tooltip = path;
            link.OnPressed = () => Open(path);
        }
        if (recent.Count == 0)
        {
            Label hint = Hint(list, Said("welcome.no_recent"));
            hint.Wrap = true;
            hint.Size = new Vector2(260, 0);
        }
        sizing.Invalidate();
    }

    /// <summary>
    /// A New File and a New Project put in the row of what to start with, made like Open File beside them. Where they go in
    /// the row is MakeWelcome's to say.
    /// </summary>
    private (Button NewFile, Button NewProject) PutNewFile(Button like)
    {
        StackPanel start = (StackPanel)like.Parent!;

        var newFile = new Button
        {
            Label = Said("welcome.new_file"),
            Icon = "file-add",
            Style = like.Style,
            LabelScale = like.LabelScale,
            Anchor = like.Anchor,
            Pivot = like.Pivot,
            Hover = like.Hover,
            HoverAmount = like.HoverAmount,
            HoverTime = like.HoverTime,

            // Gives under the pointer and when pressed like the ones it sits among. It was the one still button of the list
            Animated = like.Animated,
            OnPressed = NewFileFromWelcome,
        };

        start.Add(newFile);

        // New Project beside New File, a whole folder of a language to start from rather than one file
        var newProject = new Button
        {
            Label = Said("new_project.welcome"),
            Icon = "new-folder",
            Style = like.Style,
            LabelScale = like.LabelScale,
            Anchor = like.Anchor,
            Pivot = like.Pivot,
            Hover = like.Hover,
            HoverAmount = like.HoverAmount,
            HoverTime = like.HoverTime,
            Animated = like.Animated,
            OnPressed = ShowNewProject,
        };
        start.Add(newProject);
        return (newFile, newProject);
    }

    /// <summary>The welcome's New File, asked for by name in the first folder of the workspace, a scratchpad with none open.</summary>
    private void NewFileFromWelcome()
    {
        if (explorer.workspace.IsEmpty)
            NewScratchpad();
        else
            explorer.NewFileIn(explorer.workspace.Folders[0]);
    }
}
