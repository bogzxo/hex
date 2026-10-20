using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// The editor. Its own UI is a layout like the ones it edits (Assets/hex/editor.hor), loaded from its file and
/// tied to the code here by the names of its parts. The layouts being edited each live in a module of their own
/// that is drawn into the canvas, so what is on screen is the real thing. The same components, laid out by the
/// same code, as when a game loads the file.
/// There are two UIs on screen for that reason: the editor's own, in a plain font that reads well small, and
/// the one the layouts are in (the stage), which looks the way a game would show them.
/// </summary>
internal sealed partial class HexScene : Scene
{
    public const string EDITOR_LAYOUT = "Assets/hex/editor.hor";

    // The skin of the editor itself, flat and without any art. See the file for what it looks like and why
    private const string TOOL_SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string TOOL_SKIN = "skin.hor";
    private const string TOOL_SKIN_NAME = "flat";

    // Which skin the layouts were shown in the last time, see the Skin menu
    private const string SKIN_FILE = "hex_skin.txt";

    // The screen the layouts being edited are made for, scaled to whatever room the canvas has.
    private static readonly Vector2 DesignSize = new(1600, 900);
    private static readonly UIRect DesignScreen = new(DesignSize / -2.0f, DesignSize / 2.0f);

    // How big the editor can draw itself. Text is at its sharpest at the whole ones.
    private static readonly float[] Scales = [0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f];

    // How long (in seconds) after it comes up the editor takes the garbage of starting out, the pictures the skins
    // were cut from, the fonts being built, the workspace being read, and gives the memory back to the machine (an
    // aggressive collection decommits what it can). Left to itself the collector only got round to it after a
    // while, and the editor sat at twice its weight until then
    private const float SETTLE_SECONDS = 3.0f;
    private float settleIn = SETTLE_SECONDS;

    // The least the parts of the editor that take whatever room is left are given, and how much of the height
    // the canvas gets at most.
    private const float MIN_FLEXIBLE = 80;
    private const float MIN_CENTER = 240;

    // How long a layout has to be left alone before how it is now is a step to come back to
    private const float SETTLE_TIME = 0.3f;

    // What is said quietly and what has gone wrong, the editor's skin's own, so they read on whichever theme it is in
    // (it comes in a light one as well). The numbers are for the moment before there is a skin
    private Vector4 DimColor => compositor.Skin?.DimTextColor ?? new Vector4(0.84f, 0.87f, 0.93f, 0.62f);
    private Vector4 ErrorColor => compositor.Skin?.ErrorColor ?? new Vector4(1.0f, 0.45f, 0.4f, 1.0f);
    private Vector4 WarningColor => compositor.Skin?.WarningColor ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
    private static readonly Vector4 FolderColor = new(0.38f, 0.62f, 1.0f, 1.0f);

    public override Camera ActiveCamera { get; protected set; }

    private readonly HexOptions options;
    private readonly Camera2D camera;
    private readonly UICompositor compositor, stage;
    private readonly UIModule chrome;
    private readonly UILayout layout;

    // The parts of the editor's own layout, found by name
    private readonly TextBox nameBox;
    private readonly StackPanel root, top, toolbar, body, activity, left, center, right;
    private readonly StackPanel tree, inspector, layers;
    private readonly MenuBar menu;
    private readonly HexLayoutDebugger layoutDebugger;
    private readonly Panel canvas;

    // What is behind the layouts in the canvas, a module of the stage as big as the canvas
    private readonly UIModule backdrop;
    private readonly ScrollPanel treeScroll, inspectorScroll;
    private readonly Panel codeScroll;
    private readonly UIComponent rightHead, activityGap, palette;
    private readonly ScrollPanel paletteScroll;
    private readonly Label treeTitle;
    
    // The layouts that were opened or saved last, the latest first
    private readonly List<string> recent = [];

    // A change that hasn't been noted as a step to come back to yet, and how long it has to rest still
    private bool recordPending;
    private float settleTimer;
    private readonly UICommands commands = new();

    private readonly List<HexDocument> documents = [];
    private HexDocument document = null!;

    // What has to be put together again before the next frame, so a click never rebuilds what it is in the middle of
    private bool tabsDirty = true, treeDirty = true, inspectorDirty = true, codeDirty = true;

    // Where files are opened from and new ones saved to without a workspace
    private string browseDirectory;

    // How often the code of a layout is written again while it is out of sight, and how long until it is next
    private const float HIDDEN_CODE_EVERY = 0.1f;
    private float codeWait;
    private bool codeShown;

    // The files that were told what #define layout does to them and saved anyway, they aren't asked twice
    private readonly HashSet<string> layoutWarned = new(StringComparer.OrdinalIgnoreCase);

    // What the lists on screen are showing right now, by what they stand for
    private readonly List<Button> paletteButtons = [];
    private readonly List<(string Path, bool IsDirectory, UITreeItem Row)> fileRows = [];
    private readonly Dictionary<UIComponent, Button> treeRows = [];
    private readonly Dictionary<string, Button> layerRows = [];
    private readonly Dictionary<string, UIComponent> editors = [];

    // Layouts that are only opened to be tested, and how that went
    private bool checksDone;

    public HexScene(HexOptions options)
    {
        this.options = options;
        browseDirectory = Path.GetFullPath(options.Workspace);

        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // The layouts first and the editor over them. The canvas is a hole in the editor they are seen through, so the
        // menus, lists and dialogs of the editor come out on top of them rather than under. What is dark behind the
        // layouts is a module of the stage itself, the first one, see PlaceCanvas
        stage = AddComponent(new UICompositor(camera));
        compositor = AddComponent(new UICompositor(camera, TOOL_SKIN_DIRECTORY, TOOL_SKIN));
        backdrop = stage.CreateModule();
        backdrop.ShowInLayoutDebugger = false;
        backdrop.Interactive = false;
        backdrop.LoadLayout("Assets/hex/backdrop.hor");

        // What the editor says, before anything of it is shown, its layouts name the texts by key
        ReadStrings();

        // Bootstrapping. The editor is made of the same thing it makes
        chrome = compositor.CreateModule();
        chrome.ShowInLayoutDebugger = false;
        layout = chrome.LoadLayout(EDITOR_LAYOUT);

        // The editor's views take turns on layers of their own, hidden in the file so they can be shown one at a time in the
        // designer and worked on. Running, which of them is up is the code's to say with Visible, so what the file starts
        // hidden starts out of sight that way and every layer is shown
        HideLayersByVisible(chrome.Root);
        chrome.ShowAllLayers();

        root = layout.Get<StackPanel>("root");
        top = layout.Get<StackPanel>("top");
        toolbar = layout.Get<StackPanel>("toolbar");
        body = layout.Get<StackPanel>("body");
        activity = layout.Get<StackPanel>("activity");
        left = layout.Get<StackPanel>("left");
        center = layout.Get<StackPanel>("center");
        right = layout.Get<StackPanel>("right");
        codeScroll = layout.Get<Panel>("code_scroll");
        menu = layout.Get<MenuBar>("menu");
        layers = layout.Get<StackPanel>("layers");

        nameBox = layout.Get<TextBox>("name_box");
        tree = layout.Get<StackPanel>("tree");
        treeTitle = layout.Get<Label>("tree_title");
        treeScroll = layout.Get<ScrollPanel>("tree_scroll");
        inspector = layout.Get<StackPanel>("inspector");
        inspectorScroll = layout.Get<ScrollPanel>("inspector_scroll");
        canvas = layout.Get<Panel>("canvas");
        rightHead = layout.Get<UIComponent>("right_head");
        activityGap = layout.Get<UIComponent>("activity_gap");
        palette = layout.Get<UIComponent>("palette");
        paletteScroll = layout.Get<ScrollPanel>("palette_scroll");

        layoutDebugger = new HexLayoutDebugger(stage);
        LoadSettings();
        DeclareMenus();
        extensions = new HexExtensions(Context, this);
        terminalFiles = new HexTerminalFiles(Context);
        extensions.TerminalApi = terminalFiles.Api();
        languages = new HexLanguages(Context);
        BuildStatusBar();
        BuildPanel();
        BuildFindBar();
        BuildQuiet();
        BuildConsole();
        BuildTerminal();
        StartRemote();
        BuildValuesTree();
        BuildSideTree();
        BuildTable();
        BuildExtensionViews();
        toasts = layout.Get<Notifications>("toasts");
        BuildTabs();

        recent.AddRange(ReadRecent());
        AddWindowCommands();
        AddDocumentCommands();
        AddWorkspaceCommands();
        AddSettingsCommands();
        AddFindCommands();
        AddCodeCommands();
        debugger = new HexDebugging(Context);
        AddHelpCommands();
        tasks = new HexTasks(Context);
        languages.StartChecks();
        BuildEffects();
        BuildModes();
        AddDesignerCommands();
        commands.Build(menu);
        LoadKeys();
        WireViews();
        WireToolbar();
        WireActivityBar();

        // A right click on the canvas that nothing of the editor took is a menu for what's there
        compositor.ContextRequested = OnContextRequested;

        // The window has no frame of the system's, the title bar is the editor's and so are the edges
        compositor.ResizesWindow = true;

        SetScale(options.Scale ?? ReadScale() ?? ScaleFor(Engine.WindowManager.ViewportSize), remember: false);

        nameBox.OnChanged = text =>
        {
            document.Name = text.Length > 0 ? text : "layout";
            tabsDirty = codeDirty = true;
        };

        BuildPalette();
        extensions.Find();
        Show(NewDocument());
        startedWith = document;

        // The folders of last time and the files that were open in them, unless the editor was told what to open
        RestoreWorkspace();

        // A folder it was started on (dawn -window folder in a terminal) is the workspace instead
        if (options.Folder is not null)
            OpenFolder(options.Folder);

        if (options.Open is not null && options.Line > 0)
            OpenAt(options.Open, options.Line - 1, 0);
        else if (options.Open is not null)
            Open(options.Open);
        else if (settings.ReopenLast && recent.Count > 0 && !options.Browse && workspace.OpenFiles.Count == 0)
            Open(recent[0]);

        // What wasn't saved when Hex last stopped without asking, the self test starting with none of its own
        if (options.SelfTest)
            Backups.DropAll();
        else
            RestoreBackups();

        // Last, an extension's on_start finds the editor up and a file open
        extensions.StartAll();
        extensions.WatchFolders();

        // The file open by now was opened before any extension said what views it has, so it's asked again
        if (document.IsText)
            OpenOnExtensionView(document);

        if (options.SelfTest)
            StartSelfTest();
        else if (options.Check.Length == 0)
            Horizon.UI.Skinning.UISkins.Remember(HexFiles.Kept(SKIN_FILE));
        else if (options.Browse)
            OpenPressed();
    }


    /// <summary>Everything on a hidden layer of the editor's own module made invisible instead, see the constructor.</summary>
    private void HideLayersByVisible(UIComponent under)
    {
        foreach (UIComponent child in under.Children)
        {
            if (child.Layer.Length > 0 && chrome.IsLayerHidden(child.Layer))
                child.Visible = false;
            else
                HideLayersByVisible(child);
        }
    }

    public override void PostInit()
    {
        base.PostInit();

        // The dark the editor sits on. Its root has no colour of its own, so the layouts drawn under it show through the canvas
        Engine.Graphics.ClearColor = new Vector4(0.065f, 0.07f, 0.092f, 1.0f);
    }

    // When Hex last said something, and how long that stays in the status bar
    private long saidAt;
    private const long SAID_FOR = 10_000;

    // The cards in the corner, for what the status bar can't hold
    private Notifications toasts = null!;

    /// <summary>
    /// Puts a card up in the corner, for what somebody has to read or do something about, and says it in the output too.
    /// An error stays until it is closed.
    /// </summary>
    private UINotification Notify(string message, bool error = false, params (string Label, Action Pressed)[] actions)
    {
        Note(message, error);
        return toasts.Show(message, error ? "error" : "info", error ? ErrorColor : null, error ? 0.0f : 8.0f, actions);
    }

    /// <summary>Says something in the status bar, and keeps it in the output under the editor.</summary>
    private void Say(string message, bool error = false)
    {
        saidAt = System.Environment.TickCount64;
        messageItem.Text = (error ? "$(error) " : string.Empty) + Plain(message);
        messageItem.Color = error ? ErrorColor : null;
        Note(message, error);
    }

    /* Every frame */

    // Where the time of a tick goes, with HEX_PROFILE set, see HexProfiler
    internal static readonly HexProfiler Profile = new();

    /// <summary>
    /// A tick of the editor, in the order it goes. The window and the canvas, the designer, the lists that were
    /// marked to be made again, what finished on other threads, the extensions, the panel and the bars, then the components
    /// of both UIs and the editor fitted round the canvas. With HEX_PROFILE set what each of those cost is kept (see HexProfiler), which is why they're in groups.
    /// </summary>
    public override void UpdateState(float dt)
    {
        Profile.Begin();

        // A unit of the camera per pixel of the window, whatever the window is resized to. The UI does the scaling
        Vector2 viewport = Engine.WindowManager.ViewportSize;
        if (viewport != camera.ViewSize && viewport.X > 0 && viewport.Y > 0)
            camera.ViewSize = viewport;

        if (options.Check.Length > 0 && !checksDone)
            RunChecks();

        if (settleIn > 0.0f && (settleIn -= dt) <= 0.0f)
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        UpdateWindow();
        Profile.Mark("window");
        UpdateDesigner(dt);
        Profile.Mark("designer");
        RebuildMarked();
        Profile.Mark("lists");
        UpdateWork(dt);
        Profile.Mark("work");

        UpdateExtensionUI(dt);
                UpdateEffects();
        UpdateAutoSave(dt);
        debugger.Update();
        ApplyAskedView();
        Profile.Mark("extensions");

        UpdateTable();
        UpdateTerminal(dt);
        UpdateRemote();
        UpdateConsole();
        RunAskedConsole();
        UpdatePanel(dt);
        Profile.Mark("panel");

        UpdateStatusBar();
        UpdateCrumbs();
        Profile.Mark("bars");

        base.UpdateState(dt);
        Profile.Mark("components");

        // The editor fitted round the canvas last, after the components moved along, a side sliding in or out has taken
        // its step by now and the middle is sized to what it will be laid out as. Fitted first, it was a step behind and
        // the right side shook a few pixels to and fro for as long as the slide took
        FitChrome();
        Profile.Mark("fit");
        Profile.End();

        UpdateSelfTest(dt);
    }

    /// <summary>A dialog that was answered, the x of the window, its colours and the canvas put where it is.</summary>
    private void UpdateWindow()
    {
        UpdateDialog();
        UpdateClosing();
        Hex.Shell.HexModes.Update();
        ColourChrome();
        PlaceCanvas();
    }

    /// <summary>The pointer on the canvas, the steps to come back to, and the code of a layout written again after a change.</summary>
    private void UpdateDesigner(float dt)
    {
        UpdateCanvasPointer();
        UpdateHistory(dt);

        // Whatever changed the code changed the layout. A file of code keeps its own history in its editor
        if (codeDirty && !document.IsText)
        {
            recordPending = true;
            settleTimer = SETTLE_TIME;
        }

        // The code of a layout is written again for every change, every tick of a drag. Out of sight in the designer it is
        // written at most every so often, the change waits, in sight it is written as it happens
        codeWait -= dt;
        if (codeDirty && (!LayoutTools || codeWait <= 0.0f))
        {
            ShowCode();
            codeWait = HIDDEN_CODE_EVERY;
            codeShown = true;
        }
    }

    /// <summary>The tabs, the tree and the inspector made again if something marked them, once a tick however many times it did.</summary>
    private void RebuildMarked()
    {
        RevealInTree();

        if (tabsDirty) RebuildTabs();
        if (treeDirty) RebuildTree();
        if (inspectorDirty) RebuildInspector();

        tabsDirty = treeDirty = inspectorDirty = false;
        if (codeShown)
            codeDirty = codeShown = false;
    }

    /// <summary>What was done on other threads taken in, the folders of the workspace and the values of the file on screen.</summary>
    private void UpdateWork(float dt)
    {
        TakeFinishedWork();
        UpdateWelcome();
        UpdateDiskChanges();
        UpdateBackups(dt);
        UpdateFindBar();
        UpdatePlaces();
        UpdateBackdrop();
        UpdateSplit();
        UpdateRunningItem();

        // Something changed in a folder of the workspace, the explorer reads that folder again and where the languages are is looked for again
        if (folders.TakeChanges())
        {
            languagesNear.Clear();
            treeDirty |= Side == HexSide.Explorer;
        }

        UpdateValues(dt);
        UpdateFileChanges();
        languages.Update(dt);
        UpdateValueBox();
    }

    /// <summary>
    /// Helper method to put the code of the layout on screen, and to work out whether it is still what is in its file.
    /// </summary>
    private void ShowCode()
    {
        string written = document.GenerateCode();

        // A layout's code is written again out of the layout, unless it was edited by hand and not put back yet
        if (!document.IsText && !document.CodeEdited)
            SetCode(document, written, fresh: false);

        // The tab of a layout says so when there is something to save
        bool modified = written != document.SavedCode || document.CodeEdited || document.CrLf != document.SavedCrLf;
        if (modified != document.Modified)
        {
            document.Modified = modified;
            tabsDirty = true;
        }
    }

    private void Quit(int failures)
    {
        System.Environment.ExitCode = failures == 0 ? 0 : 1;
        Backups.DropAll();
        closeConfirmed = true;
        Engine.WindowManager.Close();
    }
}
