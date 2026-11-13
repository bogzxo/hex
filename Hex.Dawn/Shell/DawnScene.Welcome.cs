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
        public void Invalidate() => width = 0;
        private float width;
        private UISkin? measuredWith;

        public void Fit(UISkin? skin)
        {
            if (!page.Visible || page.Bounds.Width <= 0.0f)
                return;
            float wanted = Math.Clamp(page.Bounds.Width - 104.0f, 160.0f, 760.0f);
            if (wanted == width && measuredWith == skin)
                return;
            width = wanted;
            measuredWith = skin;
            bool stacked = width < 560.0f;
            columns.Direction = stacked ? UIDirection.Vertical : UIDirection.Horizontal;
            content.Size = content.Size with { X = width };
            columns.Size = columns.Size with { X = width };
            subtitle.Size = subtitle.Size with { X = width };
            float column = stacked ? width : (width - columns.Spacing) * 0.5f;
            start.Size = start.Size with { X = column };
            recent.Size = recent.Size with { X = column };
            foreach (Button action in start.Children.OfType<Button>())
            {
                action.Size = new Vector2(column, 36);
                action.Align = Origin.Left;
            }
            foreach (UIComponent row in List.Children)
            {
                if (row is Label hint)
                    hint.Size = hint.Size with { X = column };
                else if (row is Button link)
                    FitFile(link, column, skin);
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
        PutNewFile(into == codeScroll, newLayout, openFile, made.Get<Button>("commands"));
        var openFolder = made.Get<Button>("open_folder");
        openFolder.OnPressed = () => explorer.AskFolder(explorer.OpenFolder);
        if (into == codeScroll && openFolder.Parent is { } start)
        {
            start.MoveChild(openFolder, 1);
            start.MoveChild(openFile, 2);
        }
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

        FillWelcomeNews(made);
        var sizing = new WelcomeSizing(page, made);
        welcomeSizes.Add(sizing);
        FillWelcomeRecent(sizing);
        return page;
    }

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
    /// A New File put in the Start list, made like Open File beside it. On the plain welcome it goes first and New Layout
    /// goes to the bottom, somebody who writes Go wants a file before a layout, on the designer's welcome it sits after
    /// New Layout.
    /// </summary>
    private void PutNewFile(bool plain, Button newLayout, Button like, Button last)
    {
        if (like.Parent is not { } start)
            return;

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

        int IndexOf(UIComponent child) => start.Children.ToList().IndexOf(child);

        start.Insert(plain ? IndexOf(newLayout) : IndexOf(newLayout) + 1, newFile);
        if (plain)
            start.MoveChild(newLayout, IndexOf(last));
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
