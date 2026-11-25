using System.Numerics;

using Hex.Core.Modules;

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
internal sealed partial class DawnScene : Scene
{
    public const string EDITOR_LAYOUT = "Assets/hex/editor.hor";

    // The skin of the editor itself, flat and without any art. See the file for what it looks like and why
    private const string TOOL_SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string TOOL_SKIN = "skin.hor";
    private const string TOOL_SKIN_NAME = "flat";

    // Which skin the layouts were shown in the last time, see the Skin menu
    private const string SKIN_FILE = "hex_skin.txt";


    // How big the editor can draw itself, the same sizes as Dusk (HexFrame)
    private static float[] Scales => HexFrame.Scales;

    // Ask the runtime to consider reclaiming startup garbage after the initial load. This is a nonblocking,
    // optimized request: forcing a compacting collection here paused typing, and again after closing a document.
    // The runtime decides whether collection is worthwhile; normal collections remain responsible for heap growth.
    private const float SETTLE_SECONDS = 3.0f;
    private float settleIn = SETTLE_SECONDS;

    // What the start leaves until the editor has been up a moment, nothing the first frames need (see AfterStart)
    private const float AFTER_START_SECONDS = 1.0f;
    private readonly List<Action> afterStart = [];
    private float afterStartIn = AFTER_START_SECONDS;

    /// <summary>Has something of the start done once the editor has been on screen a moment, off the first frames' path.</summary>
    private void AfterStart(Action run) => afterStart.Add(run);

    // The least the parts of the editor that take whatever room is left are given, and how much of the height
    // the canvas gets at most.
    private const float MIN_FLEXIBLE = 80;
    private const float MIN_CENTER = 240;


    // What is said quietly and what has gone wrong, the editor's skin's own, so they read on whichever theme it is in
    // (it comes in a light one as well). The numbers are for the moment before there is a skin
    private Vector4 DimColor => compositor.Skin?.DimTextColor ?? new Vector4(0.84f, 0.87f, 0.93f, 0.62f);
    private Vector4 ErrorColor => compositor.Skin?.ErrorColor ?? new Vector4(1.0f, 0.45f, 0.4f, 1.0f);
    private Vector4 WarningColor => compositor.Skin?.WarningColor ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
    // A folder's icon, the skin's text turned down. Folders are the shape of the tree, the colours are left to what the
    // files are and what git and the problems say of them. It was a blue of its own on every theme, names and all
    private Vector4 FolderColor => compositor.Skin is { } skin ? skin.TextColor with { W = skin.TextColor.W * FOLDER_ICON } : DimColor;
    private const float FOLDER_ICON = 0.72f;

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
    private bool backdropLoaded;
    private bool sceneReady;
    private bool markdownGrammarWarmed;
    private readonly ScrollPanel treeScroll, inspectorScroll;
    private readonly Panel codeScroll;
    private readonly UIComponent rightHead, activityGap, palette;
    private readonly ScrollPanel paletteScroll;
    private readonly Label treeTitle;
    
    // The layouts that were opened or saved last, the latest first
    private readonly List<string> recent = [];

    private readonly UICommands commands = new();

    // The menu a right click put up, while it's up
    private ContextMenu? contextMenu;

    private readonly List<HexDocument> documents = [];
    private HexDocument document = null!;

    // What has to be put together again before the next frame, so a click never rebuilds what it is in the middle of
    private bool tabsDirty = true, treeDirty = true, inspectorDirty = true, codeDirty = true;

    // The palette's git colours the tabs were last made with, see RebuildMarked
    private (Vector4, Vector4, Vector4) tabsColouredWith;

    // Where files are opened from and new ones saved to without a workspace
    private string browseDirectory;

    // How often the code of a layout is written again while it is out of sight, and how long until it is next
    private const float HIDDEN_CODE_EVERY = 0.1f;
    private float codeWait;
    private bool codeShown;

    // The files that were told what #define layout does to them and saved anyway, they aren't asked twice
    private readonly HashSet<string> layoutWarned = new(StringComparer.OrdinalIgnoreCase);

    // What the lists on screen are showing right now, by what they stand for
    private readonly List<(string Path, bool IsDirectory, UITreeItem Row)> fileRows = [];

    // Layouts that are only opened to be tested, and how that went
    private bool checksDone;

    public DawnScene(HexOptions options)
    {
        this.options = options;
        browseDirectory = Path.GetFullPath(options.Workspace);

        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // The layouts first and the editor over them. The canvas is a hole in the editor they are seen through, so the
        // menus, lists and dialogs of the editor come out on top of them rather than under. What is dark behind the
        // layouts is a module of the stage itself, the first one, see PlaceCanvas
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: camera");
        stage = AddComponent(new UICompositor(camera));
        stage.Enabled = false;
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: stage compositor");
        compositor = AddComponent(new UICompositor(camera, TOOL_SKIN_DIRECTORY, TOOL_SKIN));
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: editor compositor");
        backdrop = stage.CreateModule();
        backdrop.ShowInLayoutDebugger = false;
        backdrop.Interactive = false;
        backdrop.Enabled = false;

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: compositors and backdrop");
        // What the editor says, before anything of it is shown, its layouts name the texts by key
        ReadStrings();

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: strings");
        // The field of light the frame's glass sits over, under everything of the editor's, see DawnScene.Materials
        BuildGlow();

        // Bootstrapping. The editor is made of the same thing it makes
        chrome = compositor.CreateModule();
        chrome.ShowInLayoutDebugger = false;
        layout = chrome.LoadLayout(EDITOR_LAYOUT);

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: editor layout loaded");
        // The editor's views take turns on layers of their own, hidden in the file so they can be shown one at a time in the
        // designer and worked on. Running, which of them is up is the code's to say with Visible, so what the file starts
        // hidden starts out of sight that way and every layer is shown
        HideLayersByVisible(chrome.Root);
        chrome.ShowAllLayers();

        root = layout.Get<StackPanel>("root");
        top = layout.Get<StackPanel>("top");
#if DEBUG
        // A Debug build's mark is the grey Hex as it is, the window's and the taskbar's, not tinted the look's accent
        // the way an installed Hex's is, so the one built from the source is never taken for it
        if (top is TitleBar bar)
            bar.IconColor = System.Numerics.Vector4.One;
#endif
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

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: settings");
        // Whether this run has agents at all, said before anything of theirs would be made (HexAi). The command line has
        // the last word, the settings say it otherwise, and it's kept for the run
        if (options.NoAi)
            HexAi.Force();
        else
            HexAi.Decide(settings.NoAi);
        settings.ApplyModules();
        BuildRenderScale();
        ReadBackdropAhead();
        DeclareMenus();
        codeEditing = new HexCodeEditing(Context);
        WireDeclarations();
        explorer = new HexExplorer(Context);
        git = new HexGit(Context);

        BuildGit();
        explorer.FoldersChanged += codeEditing.ForgetVocabulary;
        running = new HexRunning(Context);
        designer = new HexDesigner(Context);
        running.Ran += path => extensions.Raise("on_run", new StringValue(path));
        extensions = new HexExtensions(Context, this);
        AddExtensionsSettings();
        terminalFiles = new HexTerminalFiles(Context);
        languages = new HexLanguages(Context) { SayMissing = !options.SelfTest };
        languages.TypesNear = () =>
        {
            HexCodeIndex index = Harness;
            return file => InterfacesNear(index, file);
        };
        explorer.FoldersChanged += languages.WorkspaceChanged;
        codeEditing.languages = languages;

        // A folder that opens with projects nobody restored has them restored, the way Visual Studio does
        explorer.FoldersChanged += languages.RestoreWhereNeeded;

        // A folder with a solution offers Visual Studio's keys, once (DawnScene.Keys.VisualStudioOffer)
        explorer.FoldersChanged += OfferVisualStudioKeys;
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: parts up to the status bar");
        BuildStatusBar();
        BuildPanel();
        codeEditing.BuildFindBar();
        BuildQuiet();
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: status bar, panel, find bar");
        BuildTerminal();
        // The agents' panel and Agentic's commands, never made with Agentic or No AI off; Agentic's own widgets wait until it's shown or warmed (EnsureNoon)
        if (HexModules.IsEnabled(HexModuleId.Agentic))
            EnsureAgentsPanel();
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: terminal and agents");
        StartRemote();
        BuildSideTree();
        BuildExtensionViews();
        toasts = layout.Get<Notifications>("toasts");
        BuildTabs();

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: side tree, tabs");
        recent.AddRange(ReadRecent());
        AddWindowCommands();
        AddDocumentCommands();
        explorer.AddWorkspaceCommands();
        AddPlaceCommands();
        AddSettingsCommands();
        AddFindCommands();
        AddCodeCommands();
        debugger = new HexDebugging(Context);
        debugger.NativeProjectConfigured = languages.CMakeConfigured;
        explorer.FoldersChanged += debugger.WorkspaceChanged;
        testExplorer = new HexTestExplorer(Context, debugger);
        debugger.NativeBuildChanged = testExplorer.NativeBuildChanged;
        debugger.NativeBuildReconfigured = testExplorer.NativeBuildReconfigured;
        languages.PythonInterpreterChanged = testExplorer.PythonInterpreterChanged;
        explorer.FoldersChanged += testExplorer.WorkspaceChanged;
        BuildDebugBar();
        AddHelpCommands();
        tasks = new HexTasks(Context) { RestartInto = RestartInto, Extensions = extensions };
        debugger.Tasks = tasks;
        debugger.InstallsItself = !options.SelfTest;
        BuildProperties();
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: commands, debugger, tests, properties");
        // The servers' look round the machine spawns shells, which raced the first frame for the processor, so it waits
        // until the editor is up. A self test has them at once, its checks look
        if (options.SelfTest)
        {
            languages.StartChecks();
        }
        else
        {
            AfterStart(languages.StartChecks);
            AfterStart(languages.RestoreWhereNeeded);
        }
        BuildEffects();
        BuildModes();
        if (HexModules.IsEnabled(HexModuleId.Designer))
            designer.AddDesignerCommands();
        WireRefresh();
        WireExplorerHead();
        BuildImageView();
        BuildNotebookView();
        BuildMarkdownPreview();
        if (HexModules.IsEnabled(HexModuleId.Notebooks))
            AddNotebookCommands();
        BuildDiffs();
        BuildHistory();
        BuildConflicts();
        BuildMergeEditor();
        BuildPeek();
        AddLanguageModeCommands();
        if (HexModules.IsEnabled(HexModuleId.LocalAi))
        {
            AddPredictionCommands();
            AddPickerCommands();
        }
        commands.Build(menu);
        LoadKeys();
        WireViews();
        WireToolbar();
        WireActivityBar();

        // A right click on the canvas that nothing of the editor took is a menu for what's there
        compositor.ContextRequested = designer.OnContextRequested;

        // The window has no frame of the system's, the title bar is the editor's and so are the edges
        compositor.ResizesWindow = true;

        SetInterfaceSize(StartingInterfaceSize(), remember: false);

        nameBox.OnChanged = text =>
        {
            document.Name = text.Length > 0 ? text : "layout";
            tabsDirty = codeDirty = true;
        };

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: views, keys, scale");
        extensions.Find();
        Show(NewDocument());
        startedWith = document;

        // The folders of last time and the files that were open in them, unless the editor was told what to open
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: extensions found, first document");
        explorer.RestoreWorkspace();

        // A folder it was started on (dawn -window folder in a terminal) is the workspace instead, unless it's a folder of
        // the workspace of last time, which is kept whole (a restart on its first folder lost the others)
        if (options.Folder is { } startedOn && !explorer.workspace.Folders.Any(folder => PlatformPaths.Same(folder, startedOn)))
            explorer.OpenFolder(options.Folder);

        // The agents' chats of this workspace as they were when Hex last had it open, none read with Agentic off
        if (HexModules.IsEnabled(HexModuleId.Agentic))
            BringBackChats();

        // A fresh start or one on a folder is on the explorer, for code and for the designer, whatever the side bar was left on.
        // The self test is left where it starts, its checks expect the designer the way it always was
        if (!options.SelfTest && (options.Fresh || options.Folder is not null))
        {
            browsing = true;
            Side = HexSide.Explorer;
        }

        // A start on code rather than on layouts has the welcome without the designer round it, see DawnScene.Welcome
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: workspace and chats");
        plainWelcome = WantsPlainWelcome();

        if (options.Files is { Length: > 0 } named)
            OpenLaunchFiles(named);
        else if (options.Open is not null && options.Line > 0)
            OpenAt(options.Open, options.Line - 1, 0);
        else if (options.Open is not null)
            Open(options.Open);
        else if (settings.ReopenLast && recent.Count > 0 && !options.Browse && !options.Fresh && explorer.workspace.OpenFiles.Count == 0
            && (options.Folder is null || PlatformPaths.IsUnder(recent[0], options.Folder)))
            Open(recent[0]);

        // What wasn't saved when Hex last stopped without asking, the self test starting with none of its own. A fresh
        // start leaves them all for the next one, a folder brings back only its own
        if (options.SelfTest)
            Backups.DropAll();
        else if (!options.Fresh && options.Folder is not null)
            RestoreBackupsUnder(options.Folder);
        else if (!options.Fresh)
            RestoreBackups();

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: files and backups");
        // Last, an extension's on_start finds the editor up and a file open
        extensions.StartAll();
        explorer.FoldersChanged += () => extensions.Raise("on_workspace_changed");
        // Watching the extensions' folders and reading the catalog are nothing the first frame needs
        if (options.SelfTest)
        {
            extensions.WatchFolders();
            extensions.StartCatalog();
        }
        else
        {
            AfterStart(extensions.WatchFolders);
            AfterStart(extensions.StartCatalog);
        }

        // The file open by now was opened before any extension said what views it has, so it's asked again
        if (document.IsText)
            OpenOnExtensionView(document);

        // The door for agents, once the workspace's folders are known for the bridge to pick this Hex by
        BuildMcp();

        // And the door for other machines, Hex Link, nothing built or listening when the Link module is off
        if (HexModules.IsEnabled(HexModuleId.Link))
            BuildLink();

        // Now that initial files and workspace state are known, activate the stage compositor & designer palette only if a layout is on screen
        sceneReady = true;
        SyncDesignerStage(LayoutTools && !plainWelcome);

        // Warm the Markdown grammar only if the editor actually opens into an Agentic view
        if (HexModules.IsEnabled(HexModuleId.Agentic) && (Face == HexFace.Noon || Side == HexSide.Agents))
            AfterStart(WarmMarkdownGrammarOnce);

        Horizon.Core.Diagnostics.StartupTrace.Mark("editor: extensions started, MCP");
        // Every part is made by now, what each tick does is put in order
        DeclareTicks();

        // A Mac's menu bar along the top of the screen, mirrored from the menus that are made by now (DawnScene.MacMenu)
        BuildMacMenu();

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

    // What each stretch of the update allocated while the ticks are weighed (HexTicks.Weighing), for the self test that
    // measures what the editor makes standing still. The tick lists' own stretch counts its steps as well
    internal static readonly string[] WEIGHED_STRETCHES = ["window", "designer", "lists", "tick lists", "bars", "components", "fit"];
    internal readonly long[] weighedStretches = new long[WEIGHED_STRETCHES.Length];
    private long weighedFrom;

    private void Weigh(int stretch)
    {
        if (!HexTicks.Weighing)
            return;
        long now = GC.GetAllocatedBytesForCurrentThread();
        weighedStretches[stretch] += now - weighedFrom;
        weighedFrom = now;
    }

    // When Hex last said something, and how long that stays in the status bar
    private long saidAt;
    private const long SAID_FOR = 10_000;

    // Whether what the status bar says is an error, which stays there until it's pressed or replaced
    private bool saidError;

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
    private void Say(string message, bool error = false, string? details = null)
    {
        saidAt = System.Environment.TickCount64;
        saidError = error;
        // With details the status bar has the short line and the tooltip the rest (the output keeps all of it)
        messageItem.Text = (error ? "$(error) " : string.Empty) + UIRichText.Plain(message);
        messageItem.Tooltip = details ?? string.Empty;
        messageItem.Color = error ? ErrorColor : null;
        Note(details is null ? message : message + " " + details, error);
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
        weighedFrom = HexTicks.Weighing ? GC.GetAllocatedBytesForCurrentThread() : 0;

        // A unit of the camera per pixel of the window, whatever the window is resized to. The UI does the scaling
        Vector2 viewport = Engine.WindowManager.ViewportSize;
        if (viewport != camera.ViewSize && viewport.X > 0 && viewport.Y > 0)
            camera.ViewSize = viewport;

        if (options.Check.Length > 0 && !checksDone)
            RunChecks();

        if (settleIn > 0.0f && (settleIn -= dt) <= 0.0f)
        {
            ForgetCodeText();
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }

        if (afterStart.Count > 0 && (afterStartIn -= dt) <= 0.0f)
        {
            foreach (Action run in afterStart)
                run();
            afterStart.Clear();
        }
        Profile.Mark("collection");

        UpdateZoomSettings(dt);

        // A three finger swipe of a trackpad goes through the open files, left to the next one and right to the one before
        if (Engine.Input.Mouse.Swipe is var swiped and not 0 && !options.SelfTest)
            CycleDocuments(swiped > 0 ? 1 : -1);
        UpdateShortcutHints();
        UpdateWindow(dt);
        UpdateMacMenu();
        UpdateAppearance();
        Profile.Mark("window");
        Weigh(0);
        UpdateDesigner(dt);
        Profile.Mark("designer");
        Weigh(1);
        RebuildMarked();
        Profile.Mark("lists");
        Weigh(2);

        // The three stretches of the tick that are lists of steps, see DeclareTicks. Each step is timed by its own name
        // with the profile on, the mark after each list is for what little is left over
        workTicks.Run(dt);
        Profile.Mark("work");
        lateTicks.Run(dt);
        Profile.Mark("late");
        panelTicks.Run(dt);
        Profile.Mark("panel");
        Weigh(3);

        UpdateStatusBar();
        UpdateCrumbs();
        Profile.Mark("bars");
        Weigh(4);

        base.UpdateState(dt);
        Profile.Mark("components");
        Weigh(5);

        // The editor fitted round the canvas last, after the components moved along, a side sliding in or out has taken
        // its step by now and the middle is sized to what it will be laid out as. Fitted first, it was a step behind and
        // the right side shook a few pixels to and fro for as long as the slide took
        FitChrome();
        Profile.Mark("fit");
        Weigh(6);
        Profile.End();

        UpdateSelfTest(dt);
    }

    /// <summary>A dialog that was answered, the x of the window, its colours and the canvas put where it is.</summary>
    private void UpdateWindow(float dt)
    {
        UpdateDialog();
        UpdateClosing();
        UpdateOpening();
        UpdateHosted();
        UpdateFileDrops();
        UpdateRenderScale();
        Hex.Shell.HexModes.Update(dt);
        ColourChrome();
        PlaceCanvas();
    }

    /// <summary>Loads the stage backdrop layout once on first Designer view.</summary>
    private void EnsureBackdropLoaded()
    {
        if (backdropLoaded)
            return;
        backdropLoaded = true;
        backdrop.LoadLayout("Assets/hex/backdrop.hor");
    }

    /// <summary>
    /// Enables the stage compositor, its backdrop and the designer palette only when a layout is actually on screen and
    /// the Designer module is enabled. Disabled during code editing so the extra compositor does zero work per frame.
    /// </summary>
    private void SyncDesignerStage(bool active)
    {
        if (!sceneReady)
            return;

        bool enable = active && HexModules.IsEnabled(HexModuleId.Designer);
        if (enable)
        {
            EnsureBackdropLoaded();
            designer.EnsurePalette();
        }

        stage.Enabled = enable;
        backdrop.Enabled = enable;
    }

    /// <summary>Warms the Markdown TextMate grammar lazily the first time an Agentic or Markdown view actually needs it.</summary>
    private void WarmMarkdownGrammarOnce()
    {
        if (markdownGrammarWarmed)
            return;
        markdownGrammarWarmed = true;
        _ = Task.Run(() => Horizon.UI.Scripting.CodeLanguages.ForFile("message.md")?.Read("x\n", 1));
    }

    /// <summary>The pointer on the canvas, the steps to come back to, and the code of a layout written again after a change.</summary>
    private void UpdateDesigner(float dt)
    {
        bool designerOn = HexModules.IsEnabled(HexModuleId.Designer);
        if (designerOn)
        {
            designer.UpdateCanvasPointer();
            designer.UpdateRecord(dt);
        }
        UpdateKeys();

        // After the keys, so Escape in the box over a text on the canvas puts the box away and doesn't deselect too
        if (designerOn)
            designer.UpdateTextEdit();

        // Whatever changed the code changed the layout. A file of code keeps its own history in its editor
        if (codeDirty && !document.IsText)
        {
            designer.recordPending = true;
            designer.settleTimer = HexDesigner.SETTLE_TIME;
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
        if (LayoutTools)
            designer.RevealInTree();

        // The side's rows aren't made again under a name being typed over in them, they are once it's done
        bool treeHeld = treeDirty && sideTree.Edited is not null;

        // The tabs take git's colours from the palette as they're made, so another look makes them again. A tab kept the
        // dark look's yellow in the day look, all but invisible on its pill
        if (compositor.Skin?.Palette is { } palette && (palette.Warning, palette.Success, palette.Error) is var gitColours && gitColours != tabsColouredWith)
        {
            tabsColouredWith = gitColours;
            tabsDirty = true;
        }

        if (tabsDirty) RebuildTabs();
        if (treeDirty && !treeHeld) RebuildTree();
        if (inspectorDirty && right.Shown)
        {
            RebuildInspector();
            inspectorDirty = false;
        }

        tabsDirty = false;
        treeDirty = treeHeld;
        if (codeShown)
            codeDirty = codeShown = false;
    }

    // The stretches of the tick that are lists of steps, made once the parts are (DeclareTicks)
    private HexTicks workTicks = null!, lateTicks = null!, panelTicks = null!;

    /// <summary>The steps of the tick by name in the order they run, for the tests and for whoever wants to read the order.</summary>
    internal IEnumerable<string> TickNames => workTicks.Names.Concat(lateTicks.Names).Concat(panelTicks.Names);

    /// <summary>
    /// What the editor does every tick, in order, said once. The order is the order of these lines and nothing else's:
    /// the editor's own steps and the parts' (<see cref="IHexPart"/>) in the one list. Three lists, for the three
    /// stretches of the tick that the components' own update and the fitting of the window come between.
    /// </summary>
    private void DeclareTicks()
    {
        // What was done on other threads taken in, the folders of the workspace and the values of the file on screen
        workTicks = new HexTicks("work", Profile)
            .Add("finished", TakeFinishedWork)
            .Add("horizon", UpdateHorizon)
            .Add("welcome", UpdateWelcome)
            .Add("disk", UpdateDiskChanges)
            .Add("settings_file", UpdateSettingsFile)
            .Add("backups", UpdateBackups)
            .Add("find_bar", codeEditing.UpdateFindBar)
            .Add("places", UpdatePlaces)
            .Add("backdrop", UpdateBackdrop)
            .Add("split", UpdateSplit)
            .Add("running_item", UpdateRunningItem)
            .Add("folders", TakeFolderChanges)
            .Add("values", running.UpdateValues)
            .Add(properties)
            .Add("peek", UpdatePeek)
            .Add("file_changes", UpdateFileChanges)
            .Add(languages)
            .Add(git)
            .Add(tasks)
            .Add("value_box", running.UpdateValueBox)
            .Add("visual_studio_offer", UpdateVisualStudioOffer);

        lateTicks = new HexTicks("late", Profile)
            .Add("extension_ui", UpdateExtensionUI)
            .Add("effects", UpdateEffects)
            .Add("auto_save", UpdateAutoSave)
            .Add(debugger)
            .Add("debug_bar", UpdateDebugBar)
            .Add("asked_view", ApplyAskedView);

        panelTicks = new HexTicks("panel", Profile)
            .Add("table", running.UpdateTable);
        if (HexModules.IsEnabled(HexModuleId.Notebooks))
            panelTicks.Add("notebooks", UpdateNotebooks);
        panelTicks
            .Add("markdown", UpdateMarkdownPreview)
            .Add("terminal", UpdateTerminal)
            .Add("remote", UpdateRemote);

        // The agent of a selection, the harness, the Agents panel and the server, a step each so each is weighed. With
        // Agentic or No AI off the agents' steps aren't in the list at all, the harness and the server are the MCP tools' and stay
        bool agenticOn = HexModules.IsEnabled(HexModuleId.Agentic);
        if (agenticOn)
            panelTicks.Add("agent", UpdateAgent);
        panelTicks.Add("harness", UpdateHarness);
        if (agenticOn)
            panelTicks.Add("agents_panel", UpdateAgentsPanel);
        panelTicks
            .Add("spelling", UpdateSpelling)
            .Add("mcp", UpdateMcp);
        if (HexModules.IsEnabled(HexModuleId.Link))
            panelTicks.Add("link", UpdateLink);
        panelTicks.Add("discord", UpdateDiscord);
        if (agenticOn)
            panelTicks.Add("noon", UpdateNoon).Add("chats_kept", UpdateChatsKept).Add("agent_data", UpdateAgentData);
        panelTicks
            .Add("dev_restart", UpdateDevRestart)
            .Add("dev_continue", UpdateDevContinue)
            .Add("console", running.UpdateConsole)
            .Add("asked_console", running.RunAskedConsole)
            .Add("panel", UpdatePanel)
            .Add("tree_title", FitTreeTitle);
    }

    // Something changed in a folder of the workspace, where the languages are is looked for again, and the explorer reads
    // that folder again when it had read it, the only folders whose changes it can show
    private void TakeFolderChanges()
    {
        var (any, read) = folders.TakeChanges();
        if (any)
            languagesNear.Clear();
        if (read)
            treeDirty |= Side == HexSide.Explorer;
    }

    /// <summary>
    /// Helper method to put the code of the layout on screen, and to work out whether it is still what is in its file.
    /// </summary>
    private void ShowCode()
    {
        // A file of code is held up against what was saved line by line. Its whole text put together to compare was done
        // on every key, near a megabyte of garbage a key in a big file
        bool same;
        if (document.IsText)
        {
            same = document.Editor.Document.TextEquals(document.SavedCode);
        }
        else
        {
            string written = document.GenerateCode();
            SayTurnedDown(document);

            // A layout's code is written again out of the layout, unless it was edited by hand and not put back yet
            if (!document.CodeEdited)
                SetCode(document, written, fresh: false);
            same = written == document.SavedCode;
        }

        // The tab of a layout says so when there is something to save
        bool modified = !same || document.CodeEdited || document.CrLf != document.SavedCrLf;
        if (modified != document.Modified)
        {
            document.Modified = modified;
            tabsDirty = true;
        }
    }

    /// <summary>
    /// The backups of files inside the folder Hex was started on brought back (see RestoreBackups), the rest left where
    /// they are for a start on their own folder. An untitled one is nobody's folder's, so it waits for that too.
    /// </summary>
    private void RestoreBackupsUnder(string folder)
    {
        List<string> brought = [];
        foreach (var backup in Backups.All())
        {
            if (backup.Path is not { } path || !PlatformPaths.IsUnder(path, folder))
                continue;

            if (BringBack(backup))
                brought.Add(backup.Name);
            Backups.Remove(backup);
        }

        if (brought.Count > 0)
            Notify(Said("backups.restored", ("files", string.Join(", ", brought))));
    }

    private void Quit(int failures)
    {
        System.Environment.ExitCode = failures == 0 ? 0 : 1;
        Backups.DropAll();
        StopDiscord();
        closeConfirmed = true;
        Engine.WindowManager.Close();
    }
}
