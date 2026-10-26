using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The workspace and its explorer: the folders Hex has open, kept in a .hex-workspace (or next to Hex while it has
// none), and the files of them on the side, made, renamed, deleted and opened from there. Made with the context and
// the editor's window.
internal sealed partial class HexExplorer
{
    private readonly DawnContext context;
    private readonly IEditorWindow window;

    // What of the editor is used everywhere in here, by the names it had in the scene
    private HexDocument document => context.Documents.Current;
    private IReadOnlyList<HexDocument> documents => context.Documents.All;
    private HexOptions options => context.Options;
    private HexSettings settings => context.Settings.Values;
    private UICommands commands => context.Commands;
    private UIModule chrome => window.Chrome;
    private UICompositor compositor => window.Compositor;
    private string browseDirectory { get => context.Windows.BrowseFolder; set => context.Windows.BrowseFolder = value; }
    private bool browsing { get => window.Browsing; set => window.Browsing = value; }
    private bool tabsDirty { get => window[DawnParts.Tabs]; set => window[DawnParts.Tabs] = value; }
    private bool treeDirty { get => window[DawnParts.Tree]; set => window[DawnParts.Tree] = value; }

    /// <summary>The folders of the workspace changed, folders in or out, files made or taken away, what knows its names forgets.</summary>
    public event Action? FoldersChanged;

    /// <summary>A folder is about to go in the trash, for whatever runs in it to stop first (IWorkspace.Deleting).</summary>
    public event Action<string>? Deleting;

    /// <summary>Open in Terminal on a folder, a terminal tab started in it. The scene fills it in, the terminal is its.</summary>
    internal Action<string>? OpenTerminalIn;

    /// <summary>Find in Folder, the search view brought up looking only in a folder, by the folder and what its include box says.</summary>
    internal Action<string, string>? FindInFolder;

    // The extension of the file made last, what a new file in a folder with nothing to go by is guessed to want
    private string? lastMadeExtension;

    public HexExplorer(DawnContext with)
    {
        context = with;
        window = with.Window;
    }
}
