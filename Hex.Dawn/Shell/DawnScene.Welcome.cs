using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;
using System.Numerics;

namespace Hex.Dawn;

// What Hex opens on when nothing is open. Hex always has a document, it starts with an empty layout, so the welcome is a
// page over that layout's canvas, there while the empty layout is all there is and gone the moment a component goes into
// it or a file is opened. Started on a folder of code (no layouts near its top) or on nothing, the welcome comes up in that
// layout's code view instead, with no designer round it, no Design, Code and Play over it and no Properties beside it.
// Somebody who writes Go never sees a layout designer they didn't ask for. A folder of layouts and every self test start
// on the designer as ever.
//
// One thing leads. The first time it's Open Folder, with Clone Repository beside it and Getting started under them (see
// DawnScene.WelcomeChecklist). Once files have been worked on they lead instead, Open Folder no longer the accent.
internal sealed partial class DawnScene
{
    private const string WELCOME_LAYOUT = "welcome.hor";

    private Panel? welcome;
    private readonly List<WelcomeSizing> welcomeSizes = [];

    // The two Welcome pages keep their own column widths, including while the other one is shown.
    private sealed class WelcomeSizing(Panel page, UILayout made)
    {
        private readonly StackPanel content = made.Get<StackPanel>("page"), columns = made.Get<StackPanel>("columns"),
            start = made.Get<StackPanel>("start"), lead = made.Get<StackPanel>("lead"), recentColumn = made.Get<StackPanel>("recent_column");
        private readonly ScrollPanel scroll = made.Get<ScrollPanel>("welcome_scroll");
        public readonly Label Subtitle = made.Get<Label>("subtitle");
        private readonly UIComponent icon = made.Get<UIComponent>("icon");
        private readonly Label title = made.Get<Label>("title");
        public readonly StackPanel List = made.Get<StackPanel>("recent");
        private readonly StackPanel news = made.Get<StackPanel>("news");
        public readonly Button CommandsButton = made.Get<Button>("commands");
        public readonly Button OpenFolder = made.Get<Button>("open_folder");
        public readonly StackPanel RecentColumn = made.Get<StackPanel>("recent_column");
        public readonly StackPanel Agents = made.Get<StackPanel>("agents");
        public readonly StackPanel Getting = made.Get<StackPanel>("getting");
        private readonly StackPanel gettingColumn = made.Get<StackPanel>("getting_column"), footer = made.Get<StackPanel>("footer"),
            side = made.Get<StackPanel>("side_column");
        private readonly Spacer top = made.Get<Spacer>("welcome_top");

        // The widest the page goes, the widest the files worked on last go, and how narrow it goes before it's one column
        private const float WIDEST = 1080.0f, RECENT_WIDEST = 720.0f, STACKED = 620.0f;

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
            float over = MathF.Round(Math.Clamp(page.Bounds.Height * 0.07f, 20.0f, 88.0f));
            if (top.Size.Y != over)
                top.Size = top.Size with { Y = over };

            // The room either side a share of the width, a narrow pane (a big interface size, a small window) losing little of
            // what it has to it. A fixed 56 cut the page's words off at its left at 200%
            float margin = MathF.Round(Math.Clamp(page.Bounds.Width * 0.05f, 16.0f, 56.0f));
            float wanted = Math.Clamp(page.Bounds.Width - margin * 2.0f - 12.0f, 160.0f, WIDEST);
            if (wanted == width && measuredWith == skin)
                return;
            width = wanted;
            measuredWith = skin;
            scroll.Padding = new UIEdges(margin, 0, margin, 0);
            bool stacked = width < STACKED, narrow = width < 420.0f;
            columns.Direction = stacked ? UIDirection.Vertical : UIDirection.Horizontal;
            columns.Spacing = stacked ? 28.0f : 48.0f;
            content.Size = content.Size with { X = width };
            columns.Size = columns.Size with { X = width };
            Subtitle.Size = Subtitle.Size with { X = Math.Min(width, 640.0f) };
            icon.Size = new Vector2(narrow ? 40 : 56);
            title.TextScale = narrow ? 0.34f : 0.44f;

            // Getting started the wider of the two, the agents and what's new beside it
            float room = width - columns.Spacing;
            float getting = stacked ? width : MathF.Floor(room * 0.56f);
            float column = stacked ? width : room - getting;
            gettingColumn.Size = gettingColumn.Size with { X = getting };
            side.Size = side.Size with { X = column };

            // Open Folder and Clone Repository as wide as their words, side by side while they fit, one over the other when not
            float leadWords = 0.0f;
            foreach (Button action in lead.Children.OfType<Button>())
                leadWords += WordsWidth(action, skin) + 24.0f + lead.Spacing;
            bool leadStacked = leadWords > width;
            lead.Direction = leadStacked ? UIDirection.Vertical : UIDirection.Horizontal;
            foreach (Button action in lead.Children.OfType<Button>())
                action.Size = new Vector2(leadStacked ? width : WordsWidth(action, skin) + 24.0f, 40);

            // The quieter ones in a row of their own, a line each where they don't fit
            float startWords = 0.0f;
            foreach (Button action in start.Children.OfType<Button>())
                startWords += WordsWidth(action, skin) + start.Spacing;
            bool startStacked = startWords > width;
            start.Direction = startStacked ? UIDirection.Vertical : UIDirection.Horizontal;
            start.Size = start.Size with { X = width };
            foreach (Button action in start.Children.OfType<Button>())
            {
                action.Size = new Vector2(startStacked ? width : WordsWidth(action, skin), 32);
                action.Align = Origin.Left;
            }

            float footerWords = 0.0f;
            foreach (Button help in footer.Children.OfType<Button>())
                footerWords += WordsWidth(help, skin) + footer.Spacing;
            bool footerStacked = footerWords > width;
            footer.Direction = footerStacked ? UIDirection.Vertical : UIDirection.Horizontal;
            foreach (Button help in footer.Children.OfType<Button>())
            {
                help.Size = new Vector2(footerStacked ? width : WordsWidth(help, skin), 30);
                help.Align = Origin.Left;
            }

            GettingWidth = getting;
            FillGetting?.Invoke(Getting, getting);

            // The files worked on last, the lead once there are any, no wider than reads well
            float files = MathF.Min(width, RECENT_WIDEST);
            recentColumn.Size = recentColumn.Size with { X = files };
            foreach (UIComponent row in List.Children)
            {
                if (row is Label hint)
                    hint.Size = hint.Size with { X = files };
                else if (row is Button link)
                    FitFile(link, files, skin);
            }
            foreach (Button agent in Agents.Children.OfType<Button>())
            {
                agent.Size = new Vector2(column, 34);
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

        // A file's row: its name, then the folder it's in, dim, at the right. The name keeps most of the row and loses its
        // middle first when it hasn't room, the folder has what's left, the full path still there on hover
        private static void FitFile(Button link, float column, UISkin? skin)
        {
            link.Size = new Vector2(column, 40);
            link.Align = Origin.Left;
            string name = Path.GetFileName(link.Tooltip);
            Label? where = link.Children.Count > 0 ? link.Children[0] as Label : null;
            if (skin is null)
            {
                link.Label = name;
                return;
            }

            // The name whole when it and some of its folder fit, otherwise the folder goes and the name has the row, losing
            // its middle only when the row's too narrow for it alone
            float padding = skin.ButtonPadding.Total.X;
            float nameWidth = skin.Font.Measure($"$({link.Icon}) {name}", link.LabelScale).X;
            const float GAP = 14.0f, LEAST_FOLDER = 90.0f;
            bool folderFits = where is not null && nameWidth + padding + GAP + LEAST_FOLDER <= column;
            int length = name.Length;
            while (length > 4 && nameWidth + padding > column)
                nameWidth = skin.Font.Measure($"$({link.Icon}) {HexText.MiddleOf(name, --length)}", link.LabelScale).X;
            link.Label = HexText.MiddleOf(name, length);
            if (where is null)
                return;

            // The folder right after the name, dim, cut from its middle to what's left of the row
            where.Visible = folderFits;
            if (!folderFits)
                return;
            string folder = FolderShown(link.Tooltip);
            float room = column - nameWidth - padding - GAP;
            int folderLength = folder.Length;
            while (folderLength > 6 && skin.Font.Measure(HexText.MiddleOf(folder, folderLength), where.TextScale).X > room)
                folderLength--;
            where.Text = HexText.MiddleOf(folder, folderLength);
            where.Position = new Vector2(skin.ButtonPadding.Left + nameWidth + GAP, 0);
        }

        // The folder a file is in, the home folder as ~
        private static string FolderShown(string path)
        {
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            return home.Length > 0 && folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + folder[home.Length..] : folder;
        }
    }

    // The empty layout Hex started with, which the welcome is over
    private HexDocument? startedWith;

    // The editor testing itself starts on the empty layout as it always did, unless a test asks for the welcome
    private bool welcomeWanted;

    /// <summary>Whether the welcome is what is on screen, nothing of anybody's open and the layout Hex started with untouched.</summary>
    private bool Welcoming => (welcomeWanted || !options.SelfTest) && documents.Count == 1 && document == startedWith
        && document is { Path: null, IsText: false, Modified: false } && document.Module.Root.Children.Count == 0;

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
        made.Get<Button>("open_folder").OnPressed = () => explorer.AskFolder(explorer.OpenFolder);
        // Cloned from here, the folder it went into is opened, it's what somebody cloning from the welcome is after
        made.Get<Button>("clone").OnPressed = () => git.AskClone(open: true);

        // The quieter row in order. On the plain welcome a file before a layout, somebody who writes Go wants one; on the
        // designer's a layout first
        Button[] order = into == codeScroll
            ? [newFile, openFile, newProject, newLayout]
            : [newLayout, newFile, openFile, newProject];
        if (openFile.Parent is StackPanel row)
            for (int i = 0; i < order.Length; i++)
                row.MoveChild(order[i], i);
        made.Get<Button>("commands").OnPressed = CommandPalette;
        made.Get<Button>("commands").Label = WithCommandShortcut(Said("welcome.commands"), "menus.command_palette");
        made.Get<Button>("keys").OnPressed = OpenKeysWindow;
        made.Get<Button>("language").OnPressed = ShowLanguageHelp;
        made.Get<Button>("changes").OnPressed = ShowChangelog;

        // The layout's buttons say their words by key, "@welcome.open_file", worked out as they're drawn. Said here, so
        // they're measured as what they say: measured by their keys, every one of them was half as wide again as its words
        foreach (string part in (ReadOnlySpan<string>)["lead", "start", "footer"])
        {
            foreach (Button button in made.Get<StackPanel>(part).Children.OfType<Button>())
            {
                if (button.Label.StartsWith('@'))
                    button.Label = Said(button.Label[1..]);
            }
        }

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

        // No agents' row with No AI on
        if (HexAi.Off)
            made.Get<UIComponent>("agents_block").Visible = false;
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
    /// going to Agentic. The chat to carry on with first when there is one, it's the likelier of the two.
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

    private static void WelcomeAgentButton(StackPanel list, string icon, string label, string tip, Action pressed) =>
        list.Add(new Button
        {
            Icon = icon, Label = label, Tooltip = tip, Style = "button_flat", LabelScale = 0.155f, Animated = false,
            Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, OnPressed = pressed
        });

    /// <summary>
    /// What the release that's running brought, a line each and a button under it that goes to the thing, the setting,
    /// the view of the side or the panel. The section is away for a version with nothing listed (HexWhatsNew). The whole
    /// of every release's notes is Release Notes at the foot of the page.
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
                Text = Said("whats_new." + item.Text), TextScale = 0.145f, Wrap = true, Dim = true,
                Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(300, 0)
            });
            line.Add(new Button
            {
                Label = Said("whats_new." + item.Go), Icon = item.Icon, Style = "button_flat", LabelScale = 0.145f, Animated = false,
                Anchor = Origin.Left, Pivot = Origin.Left,
                Tooltip = Said(item.Place switch
                {
                    HexNewsPlace.Side => "welcome.news_tip_side",
                    HexNewsPlace.Problems => "welcome.news_tip_problems",
                    HexNewsPlace.NewProject => "new_project.title",
                    HexNewsPlace.LocalAi => "welcome.news_tip_local_ai",
                    _ => "welcome.news_tip_setting"
                }),
                OnPressed = () => GoToNews(item)
            });
        }
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
            case HexNewsPlace.LocalAi:
                // With every AI off the window doesn't open, and Settings has the switch that turns it back on
                if (HexAi.Off)
                    OpenSettings(EditorSettingsCatalog.AGENTS);
                else
                    OpenLocalAiWindow(item.Target);
                break;
        }
    }

    // Both pages live for the session, including while hidden behind a document or the other view. The files lead once
    // there are any: the section shown, the words over them saying to pick up where things were left, and Open Folder no
    // longer the accent, the one thing that leads is the file worked on last
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
            link.Animated = false;
            link.OnPressed = () => Open(path);
            // The folder it's in, dim after its name, cut to the room by WelcomeSizing
            link.Add(new Label
            {
                TextScale = 0.13f, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Visible = false
            });
        }
        if (recent.Count == 0)
        {
            Label hint = Hint(list, Said("welcome.no_recent"));
            hint.Wrap = true;
            hint.Size = new Vector2(260, 0);
        }

        bool history = recent.Count > 0;
        sizing.RecentColumn.Visible = history;
        sizing.OpenFolder.Primary = !history;
        sizing.Subtitle.Text = Said(history ? "welcome.subtitle_back" : "welcome.subtitle");
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
