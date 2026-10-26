using Horizon.UI;
using Horizon.UI.Components;

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
        made.Get<Button>("open_folder").OnPressed = () => explorer.AskFolder(explorer.OpenFolder);
        made.Get<Button>("commands").OnPressed = CommandPalette;
        made.Get<Button>("keys").OnPressed = OpenKeysWindow;
        made.Get<Button>("language").OnPressed = ShowLanguageHelp;
        made.Get<Button>("changes").OnPressed = ShowChangelog;
        made.Get<Label>("subtitle").Text = Said("welcome.subtitle", ("version", HexAbout.Version));

        var list = made.Get<StackPanel>("recent");
        var rows = layout.Populate(list, Math.Min(recent.Count, 10));
        for (int i = 0; i < rows.Count; i++)
        {
            string path = recent[i];
            var link = rows[i].Get<Button>("link");
            link.Label = $"{Path.GetFileName(path)}    {Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty)}";
            link.Icon = HexExplorer.FileIcon(path);
            link.Tooltip = path;
            link.OnPressed = () => Open(path);
        }

        if (recent.Count == 0)
            Hint(list, Said("welcome.no_recent"));

        return page;
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
