using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What Hex opens on when nothing is open. Hex always has a document, it starts with an empty layout, so the welcome is a
// page over that layout's canvas, there while the empty layout is all there is and gone the moment a component goes into
// it or a file is opened. New, open, a folder, the commands, the help, and the files worked on last.
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
        bool wanted = Welcoming && LayoutTools;
        if (wanted == welcome is { Visible: true })
            return;

        if (wanted)
            ShowWelcome();
        else if (welcome is not null)
            welcome.Visible = false;
    }

    private void ShowWelcome()
    {
        if (welcome is null)
        {
            UILayout made = layout.Instantiate(WELCOME_LAYOUT, canvas);
            welcome = made.Get<Panel>("welcome");

            // The card's own colour, the middle of the window it sits in. Its own near black was a slab in the navy
            if (compositor.Skin is { } skin)
                welcome.Color = skin.PanelColor with { W = 1.0f };

            made.Get<Button>("new_layout").OnPressed = () => Show(NewDocument());
            made.Get<Button>("open_file").OnPressed = OpenPressed;
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
        }

        welcome.Visible = true;
    }
}
