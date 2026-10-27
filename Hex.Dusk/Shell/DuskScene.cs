using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

using Hex.Shell;

namespace Hex.Dusk;

/// <summary>
/// The terminal. Its window is a layout like any game's (Assets/dusk/dusk.hor), the title bar with the menus in it,
/// the panel of tabs and splits and the status bar, tied to the code by the names of its parts. What makes it a
/// terminal application is the engine's <see cref="TerminalHost"/> round the panel, the commands, the plugins, the
/// settings and the history Hex's terminal page has as well, and Dusk is the window round that, its menus, its
/// palette, its themes and what it adds to the terminal object (DuskScene.PluginApi).
/// </summary>
internal sealed partial class DuskScene : Scene
{
    public const string LAYOUT = "Assets/dusk/dusk.hor";
    public const string BUILT_IN_PLUGINS = "Assets/dusk/plugins", DEFAULT_KEYS = "Assets/dusk/keys.hor";

    private const string SKIN_DIRECTORY = "Assets/uix/flat/", SKIN_FILE = "skin.hor", SKIN_NAME = "flat";
    // Hex's version, the two are one program now and Dusk said 0.3 in its title bar while the exe said 0.4.0
    public static readonly string VERSION = typeof(DuskScene).Assembly.GetName().Version?.ToString(3) ?? "?";

    // How long after it comes up Dusk takes the garbage of starting out and gives the memory back
    private const float SETTLE_SECONDS = 3.0f;
    private float settleIn = SETTLE_SECONDS;

    public override Camera ActiveCamera { get; protected set; }

    private readonly DuskOptions options;
    private readonly Camera2D camera;
    private readonly UICompositor compositor;
    private readonly UIModule chrome;
    private readonly UILayout layout;
    private readonly TerminalHost host;

    // The parts of the window, found by name
    private readonly StackPanel root;
    private readonly TitleBar top;
    private readonly MenuBar menu;
    private readonly TerminalPanel panel;
    private readonly StatusBar statusBar;
    private readonly Notifications toasts;

    // What the status bar says and when
    private StatusItem messageItem = null!, profileItem = null!, sizeItem = null!, themeItem = null!;
    private long saidAt;
    private const long SAID_FOR = 8_000;

    private volatile bool closeConfirmed;

    private TerminalSettings settings => host.Settings;

    public DuskScene(DuskOptions options)
    {
        this.options = options;

        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // The theme is wanted before there is a host to read it, so the file is read for it on its own first
        string beside = AppContext.BaseDirectory;
        var early = new TerminalSettings(TerminalFiles.Settings(options.SelfTest ? null : beside));
        compositor = AddComponent(new UICompositor(camera, SKIN_DIRECTORY, SKIN_FILE, ThemeOrUsual(early.Theme)));

        // The field of light the bars' glass sits over, under everything of Dusk's, see DuskScene.Materials
        BuildGlow();
        chrome = compositor.CreateModule();
        layout = chrome.LoadLayout(LAYOUT);

        root = layout.Get<StackPanel>("root");
        top = layout.Get<TitleBar>("top");
        menu = layout.Get<MenuBar>("menu");
        panel = layout.Get<TerminalPanel>("terminal");
        statusBar = layout.Get<StatusBar>("status");
        toasts = layout.Get<Notifications>("toasts");

        // The window has no frame of the system's, the title bar is Dusk's and so are the edges
        compositor.ResizesWindow = true;
        BuildStatusBar();

        // A file next to the exe wins over the user's, except in the self test, which keeps to a home of its own (Program)
        string? programFolder = options.SelfTest ? null : beside;
        host = new TerminalHost(panel, new TerminalHostOptions
        {
            Name = "dusk",
            Version = VERSION,
            ObjectNames = ["terminal", "dusk"],
            // The terminal's own plugins and keys out of the engine first, Dusk's over them, the user's over those
            PluginFolders = [(TerminalHost.EnginePlugins, true), .. TerminalFiles.PluginFolders(Path.Combine(beside, BUILT_IN_PLUGINS), programFolder)],
            KeysFiles = [TerminalHost.EngineKeys, Path.Combine(beside, DEFAULT_KEYS), TerminalFiles.Keys(programFolder)],
            SettingsFile = TerminalFiles.Settings(programFolder),
            HistoryFile = TerminalFiles.History(programFolder),
            Say = Say,
            Notify = Notify,

            // Dusk has no Problems panel, the first a program says is said in the status bar with how to get to it
            Problem = (problem, _) => Say(HexText.Get("dusk_scene.problem", ("file", Path.GetFileName(problem.File) ?? string.Empty), ("line", problem.Line.ToString()), ("message", problem.Message)), !problem.Warning),
            Api = DuskApi,
            ApiFunctions = DuskApiFunctions,
            ApiFile = Path.Combine(TerminalFiles.UserFolder, TerminalApiDescription.FILE),
            Ui = this,
            SettingsChanged = SettingsChanged
        });

        SetScale(options.Scale ?? settings.Scale);
        BuildPanel();
        host.Commands.OnChanged = RefreshMenuKeys;
        host.Blocked = () => chrome.Dialog is not null;
        host.Start();
        host.Watch();
        BuildMenus();
        BuildModes();
        BuildEffects();

        Hex.Shell.HexModes.OnClose(Hex.Shell.HexMode.Dusk, () => closeConfirmed || panel.Tabs.Count <= 1 || AskToClose());

        if (options.SelfTest)
            StartSelfTest();
    }

    public override void PostInit()
    {
        base.PostInit();
        ColourWindow();
    }

    private bool started;

    /// <summary>
    /// The first tick, which is the first moment a texture can be made, so the font is built here and the first shell
    /// started with it, not in the constructor (no window yet) and not in PostInit (no device yet either).
    /// </summary>
    private void StartUp()
    {
        if (started || !Horizon.Graphics.GraphicsDevice.IsAvailable)
            return;

        started = true;
        panel.Font = FontOf(settings.Font);
        ReadBackdrop();
        StartInstance();

        // Started by Windows for a console it's passing on, which is the first tab, a shell of its own would only be in the way
        if (options.Handoff)
        {
            handoffWaitingSince = System.Environment.TickCount64;
            host.Raise("on_start");
            return;
        }

        // The tabs of last time when there were any and the settings say so, otherwise one of the profile asked for (with
        // the command line it was started to run typed into it, a task of Dawn's)
        if (options.Profile is null && options.Run is null && !options.SelfTest && settings.RestoreSessions && RestoreSession())
        {
            host.Raise("on_start");
            return;
        }

        TerminalProfile? first = host.ProfileCalled(options.Profile ?? string.Empty) ?? host.UsualProfile;
        TerminalTab tab = panel.NewTab(first, options.StartIn);
        if (options.Run is { Length: > 0 } run)
            tab.Views.FirstOrDefault()?.SendWhenReady(run + "\r");
        host.Raise("on_start");
    }

    private void StartInstance()
    {
        // the self test leaves the pipe to the Dusk somebody has open
        if (!options.SelfTest)
            instance = new Hex.Shell.HexInstance(Hex.Shell.HexMode.Dusk, Engine.WindowManager.Wake);
    }

    // The skin the terminals' colours were last taken from, the compositor swaps its skin a tick after it is asked to
    private Horizon.UI.Skinning.UISkin? themedFor;

    private void UpdateTheme()
    {
        if (compositor.Skin is { } skin && !ReferenceEquals(skin, themedFor))
        {
            themedFor = skin;
            ApplyTheme();
        }
    }

    /// <summary>What a change of the settings means for Dusk's own window, its scale and its theme.</summary>
    private void SettingsChanged()
    {
        if (settings.Scale != compositor.Scale)
            SetScale(settings.Scale);
        if (ThemeOrUsual(settings.Theme) != compositor.Skin?.Theme)
            compositor.SetSkin(SKIN_DIRECTORY, SKIN_FILE, ThemeOrUsual(settings.Theme));
        UpdateQuakeKey();
        ReadEffects();
        ReadBackdrop();
    }

    private bool closeAsked;

    /// <summary>The x with more than one tab open asks first, see UpdateClosing.</summary>
    private bool AskToClose()
    {
        closeAsked = true;
        return false;
    }

    private void UpdateClosing()
    {
        if (!closeAsked)
            return;

        closeAsked = false;
        UIDialog.Show(chrome, HexText.Get("dusk_scene.close_title"), HexText.Get("dusk_scene.close_message", ("count", panel.Tabs.Count.ToString())),
            new DialogChoice(HexText.Get("documents.close"), () => Quit(0)), new DialogChoice(HexText.Get("dusk_scene.stay")));
    }

    /// <summary>Says something in the status bar for a while.</summary>
    private void Say(string message, bool error = false)
    {
        saidAt = System.Environment.TickCount64;
        messageItem.Text = (error ? "$(error) " : string.Empty) + message.ReplaceLineEndings(" ");
        messageItem.Color = error ? compositor.Skin?.ErrorColor : null;
        if (error)
            Console.WriteLine($"[Dusk] {message}");
    }

    /// <summary>Puts a card up in the corner, for what somebody has to read. An error stays until it is closed.</summary>
    private void Notify(string message, bool error = false)
    {
        Say(message, error);
        toasts.Show(message, error ? "error" : "info", error ? compositor.Skin?.ErrorColor : null, error ? 0.0f : 6.0f);
    }

    public override void UpdateState(float dt)
    {
        Vector2 viewport = Engine.WindowManager.ViewportSize;
        if (viewport != camera.ViewSize && viewport.X > 0 && viewport.Y > 0)
            camera.ViewSize = viewport;

        if (settleIn > 0.0f && (settleIn -= dt) <= 0.0f)
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        StartUp();
        UpdateClosing();
        Hex.Shell.HexModes.Update();
        UpdateLater();
        UpdateBackdropArrange();
        UpdateEffects();
        UpdateTheme();
        UpdateKeys();
        UpdateQuake();
        UpdateInstance();
        UpdateHandoffs();
        UpdateStatusBar();
        host.Update(dt);
        UpdateSession(dt);

        base.UpdateState(dt);

        FitChrome();
        UpdateSelfTest(dt);
    }

    private void Quit(int code)
    {
        System.Environment.ExitCode = code;
        SaveSession();
        closeConfirmed = true;
        Engine.WindowManager.Close();
    }

    protected override void DisposeOther()
    {
        instance?.Dispose();
        host.Dispose();
        panel.Dispose();
        base.DisposeOther();
    }
}
