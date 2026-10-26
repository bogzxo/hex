using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The designer, a layout drawn on the stage in the canvas, picked at, dragged about, its properties on the right and
// its components and layers on the left, made with the context and the editor's window. The layout is the document on
// screen's (its module on the stage), its history the document's.
internal sealed partial class HexDesigner
{
    private readonly DawnContext context;
    private readonly IEditorWindow window;

    // What of the editor is used everywhere in here, by the names it had in the scene
    private HexDocument document => context.Documents.Current;
    private bool LayoutTools => document.InDesigner;
    private UILayout layout => window.Layout;
    private UIModule chrome => window.Chrome;
    private UICompositor compositor => window.Compositor;
    private UICompositor stage => window.Stage;
    private HexSettings settings => context.Settings.Values;
    private UICommands commands => context.Commands;
    private bool browsing { get => window.Browsing; set => window.Browsing = value; }
    private bool tabsDirty { get => window[DawnParts.Tabs]; set => window[DawnParts.Tabs] = value; }
    private bool treeDirty { get => window[DawnParts.Tree]; set => window[DawnParts.Tree] = value; }
    private bool inspectorDirty { get => window[DawnParts.Right]; set => window[DawnParts.Right] = value; }
    private bool codeDirty { get => window[DawnParts.Code]; set => window[DawnParts.Code] = value; }
    private StatusItem coordsItem => window.Coordinates;

    // The designer's parts of the editor's layout
    private readonly Panel canvas;
    private readonly StackPanel tree, inspector, layers;
    private readonly ScrollPanel treeScroll, inspectorScroll, paletteScroll;
    private readonly UIComponent palette;
    private readonly Label treeTitle;

    // The screen a layout that doesn't say which it's made for is laid out and previewed on, the one Hex is on, so a 4K
    // screen previews at 3840 x 2160 instead of a 1600 x 900 that was the same on every screen. A window that doesn't follow
    // its screen (a self test, the same size anywhere) and one that doesn't know its screen yet get 1600 x 900
    internal static Vector2 DesignSize =>
        Horizon.Engine.GameEngine.Instance?.WindowManager is { ScreenSize: { X: >= 320.0f, Y: >= 240.0f } screen } ? screen : UsualDesign;

    internal static UIRect DesignScreen => new(DesignSize / -2.0f, DesignSize / 2.0f);

    private static readonly Vector2 UsualDesign = new(1600, 900);

    // How long a layout has to be left alone before how it is now is a step to come back to
    internal const float SETTLE_TIME = 0.3f;

    // A change that hasn't been noted as a step to come back to yet, and how long it has to rest still
    internal bool recordPending;
    internal float settleTimer;

    internal readonly List<Button> paletteButtons = [];

    internal readonly Dictionary<UIComponent, Button> treeRows = [];

    internal readonly Dictionary<string, Button> layerRows = [];

    internal readonly Dictionary<string, UIComponent> editors = [];

    public HexDesigner(DawnContext with)
    {
        context = with;
        window = with.Window;
        canvas = layout.Get<Panel>("canvas");
        tree = layout.Get<StackPanel>("tree");
        inspector = layout.Get<StackPanel>("inspector");
        layers = layout.Get<StackPanel>("layers");
        treeScroll = layout.Get<ScrollPanel>("tree_scroll");
        inspectorScroll = layout.Get<ScrollPanel>("inspector_scroll");
        palette = layout.Get<UIComponent>("palette");
        paletteScroll = layout.Get<ScrollPanel>("palette_scroll");
        treeTitle = layout.Get<Label>("tree_title");
    }

    // How many components the layout on screen has, for the status bar
    internal int componentCount;

    /// <summary>Whether the layout has a change that isn't a step to come back to yet.</summary>
    internal bool RecordPending => recordPending;
}
