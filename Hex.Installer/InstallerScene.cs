using System.Collections.Concurrent;
using System.Numerics;
using Hex.Installation;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Installer;

/// <summary>Hex's own flat skin, Inter typography and blue accent, drawn entirely by Horizon.UI.</summary>
internal sealed class InstallerScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }
    private readonly SetupOptions options;
    private readonly UICompositor ui;
    private readonly Camera2D camera;
    private readonly Label status, detail, version, step;
    private readonly ProgressBar progress;
    private readonly Button primary, cancel, browse;
    private readonly Label target;
    private readonly UIModule dialogs;
    private readonly StackPanel card;
    private readonly Button customize;
    private readonly WindowPanel optionsWindow;
    private readonly ToggleButton desktopHex, desktopDusk, applicationMenu, menuDusk, windowsAppPaths;
    private InstallChoices choices;
    private string installRoot;
    private Task<string?>? folderPick;
    private (Vector2 Size, float Scale, bool Maximized, float Radius) rounded;
    private readonly ConcurrentQueue<Action> changes = new();
    private CancellationTokenSource cancellation = new();
    private ReleaseAsset? release;
    private volatile bool busy;
    private volatile bool closeAsked;
    private bool started, completed;

    public InstallerScene(SetupOptions options)
    {
        this.options = options;
        installRoot = options.Target;
        choices = InstallTransaction.ReadChoices(installRoot);
        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;
        ui = AddComponent(new UICompositor(camera, "Assets/installer/", "skin.hor") { Strings = InstallerStrings.Read() });
        var module = ui.CreateModule();
        var layout = module.LoadLayout("Assets/installer/setup.hor");
        dialogs = ui.CreateModule();
        var preferences = dialogs.LoadLayout("Assets/installer/options.hor");
        optionsWindow = preferences.Get<WindowPanel>("options_window");
        optionsWindow.Modal = true;
        desktopHex = preferences.Get<ToggleButton>("desktop_hex");
        desktopDusk = preferences.Get<ToggleButton>("desktop_dusk");
        applicationMenu = preferences.Get<ToggleButton>("application_menu");
        menuDusk = preferences.Get<ToggleButton>("menu_dusk");
        windowsAppPaths = preferences.Get<ToggleButton>("windows_app_paths");
        applicationMenu.Label = InstallText.Get(OperatingSystem.IsWindows() ? "integration.menu_windows" : "integration.menu_linux");
        menuDusk.Label = InstallText.Get(OperatingSystem.IsWindows() ? "integration.menu_dusk_windows" : "integration.menu_dusk");
        desktopDusk.Visible = applicationMenu.Visible = menuDusk.Visible = !OperatingSystem.IsMacOS();
        windowsAppPaths.Visible = OperatingSystem.IsWindows();
        preferences.Get<Button>("options_cancel").OnPressed = optionsWindow.Close;
        preferences.Get<Button>("options_apply").OnPressed = () =>
        {
            choices = new(desktopHex.State, desktopDusk.State, applicationMenu.State, menuDusk.State, windowsAppPaths.State);
            optionsWindow.Close();
        };
        card = layout.Get<StackPanel>("card");
        step = layout.Get<Label>("step");
        version = layout.Get<Label>("version");
        status = layout.Get<Label>("status");
        detail = layout.Get<Label>("detail");
        progress = layout.Get<ProgressBar>("progress");
        primary = layout.Get<Button>("primary");
        cancel = layout.Get<Button>("cancel");
        browse = layout.Get<Button>("browse");
        customize = layout.Get<Button>("customize");
        customize.Visible = !options.Uninstall && !options.Update;
        customize.OnPressed = ShowInstallChoices;
        target = layout.Get<Label>("target");
        version.Text = InstallText.Get("version.platform", ("platform", InstallPaths.Rid));
        target.Text = UIRichText.Literal(installRoot);
        browse.Visible = !options.Update && !options.Uninstall;
        browse.OnPressed = Browse;
        if (options.Uninstall)
        {
            primary.Danger = true;
            step.Text = InstallText.Get("steps.uninstall");
            layout.Get<Label>("heading").Text = InstallText.Get("uninstall.heading");
            layout.Get<Label>("description").Text = InstallText.Get("uninstall.description");
            status.Text = InstallText.Get("status.uninstall_ready");
            primary.Label = InstallText.Get("actions.uninstall");
            primary.Enabled = true;
        }
        primary.OnPressed = PressPrimary;
        cancel.OnPressed = RequestClose;
        Engine.WindowManager.CloseRequested = () => { if (!busy) return true; closeAsked = true; return false; };
    }

    public override void UpdateState(float dt)
    {
        Vector2 viewport = Engine.WindowManager.ViewportSize;
        if (viewport.X > 0 && viewport.Y > 0 && camera.ViewSize != viewport) camera.ViewSize = viewport;
        ui.Scale = InstallerLayout.Fit(viewport, Engine.WindowManager.ScalesToDisplay ? Engine.WindowManager.DisplayScale : 1);
        var corners = (viewport, Engine.WindowManager.DisplayScale, Engine.WindowManager.IsMaximized, card.Radius);
        if (rounded != corners)
        {
            rounded = corners;
            Engine.WindowManager.SetCornerRadius(card.Radius * ui.Scale);
        }
        base.UpdateState(dt);
        while (changes.TryDequeue(out var action)) action();
        progress.Visible = busy || completed;
        FinishFolderPick();
        browse.Enabled = !busy && !completed && folderPick is null;
        customize.Enabled = !busy && !completed && folderPick is null;
        menuDusk.Enabled = applicationMenu.State;
        if (folderPick is not null || optionsWindow.Visible) primary.Enabled = false;
        if (closeAsked) { closeAsked = false; RequestClose(); }
        if (!started)
        {
            started = true;
            if (options.Preview) { status.Text = InstallText.Get("status.preview"); detail.Text = InstallText.Get("detail.features"); primary.Enabled = true; }
            else if (!options.Uninstall) Check();
        }
    }

    private void Check() => Work(async token =>
    {
        if (options.Parent is int pid)
        {
            Report(new(InstallText.Get("status.waiting"), .02f));
            await InstallerHandoff.WaitForParent(pid, options.ParentStart, token);
        }
        var latest = await new ReleaseClient().Latest(InstallPaths.Rid, token);
        if (options.Version is not null && latest.Version != options.Version)
            throw new IOException(InstallText.Get("errors.release_changed"));
        InstallerDestination.ValidateVersion(installRoot, latest);
        changes.Enqueue(() =>
        {
            release = latest;
            version.Text = InstallText.Get("version.release", ("version", latest.Version), ("platform", InstallPaths.Rid));
            status.Text = InstallText.Get("status.install_ready");
            detail.Text = InstallText.Get("detail.features");
            primary.Label = InstallText.Get("actions.install");
            primary.Enabled = true;
        });
    }, then: () => { if (options.Update) PressPrimary(); });

    private void PressPrimary()
    {
        if (busy || options.Preview || folderPick is not null || optionsWindow.Visible) return;
        if (completed)
        {
            try { if (!options.Uninstall) InstallerHandoff.LaunchHex(installRoot); Engine.WindowManager.Close(); }
            catch (Exception e) { detail.Text = UIRichText.Literal(e.Message); }
            return;
        }
        if (!options.Uninstall && release is null) { Check(); return; }
        try
        {
            InstallTransaction.ValidateRoot(installRoot);
            InstallerDestination.ValidateVersion(installRoot, release);
        }
        catch (Exception e) { detail.Text = UIRichText.Literal(e.Message); return; }
        step.Text = options.Uninstall ? InstallText.Get("steps.removing") : InstallText.Get("steps.installing");
        Work(async token =>
        {
            if (options.Uninstall) { Report(new(InstallText.Get("status.removing"), .4f)); InstallTransaction.Uninstall(installRoot); }
            else await InstallTransaction.Install(release!, installRoot, Report, token, choices);
        }, then: () =>
        {
            completed = true;
            primary.Danger = false;
            status.Color = new Vector4(.48f, .83f, .67f, 1);
            step.Text = InstallText.Get("steps.complete");
            status.Text = options.Uninstall ? InstallText.Get("status.uninstalled") : InstallText.Get("status.installed");
            detail.Text = options.Uninstall ? InstallText.Get("detail.uninstalled") : InstallText.Get("detail.installed");
            progress.Progress = 1;
            primary.Label = options.Uninstall ? InstallText.Get("actions.done") : InstallText.Get("actions.launch");
            primary.Enabled = true;
            cancel.Label = InstallText.Get("actions.close");
            cancel.Enabled = true;
        });
    }

    private void Report(InstallProgress update) => changes.Enqueue(() => { status.Text = UIRichText.Literal(update.Message); progress.Progress = update.Fraction; });

    private void Browse()
    {
        if (busy || completed || folderPick is not null || optionsWindow.Visible || options.Update || options.Uninstall) return;
        string from = installRoot;
        while (!Directory.Exists(from) && Path.GetDirectoryName(from) is { } parent) from = parent;
        var host = new UIDialogHost(window => dialogs.Root.Add(window), [InstallPaths.Home]);
        folderPick = UIFileDialogs.Folder(InstallText.Get("destination.browse_title"), from, host);
    }

    private void FinishFolderPick()
    {
        if (folderPick is not { IsCompleted: true } picked) return;
        folderPick = null;
        try
        {
            if (picked.GetAwaiter().GetResult() is { } folder)
            {
                string destination = InstallerDestination.FromFolder(folder, OperatingSystem.IsMacOS());
                InstallerDestination.ValidateVersion(destination, release);
                installRoot = destination;
                if (File.Exists(Path.Combine(destination, InstallPaths.Manifest))) choices = InstallTransaction.ReadChoices(destination);
                target.Text = UIRichText.Literal(installRoot);
                detail.Text = InstallText.Get("detail.features");
            }
        }
        catch (Exception e) { detail.Text = UIRichText.Literal(e.Message); }
        primary.Enabled = !busy;
    }

    private void Work(Func<CancellationToken, Task> work, Action? then = null)
    {
        busy = true;
        status.Color = null;
        detail.Tooltip = string.Empty;
        primary.Enabled = false;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await work(cancellation.Token); changes.Enqueue(() => { busy = false; then?.Invoke(); }); }
            catch (Exception e)
            {
                changes.Enqueue(() =>
                {
                    busy = false;
                    status.Color = e is OperationCanceledException ? null : ui.Skin?.ErrorColor;
                    detail.Tooltip = e.Message;
                    status.Text = e is OperationCanceledException ? InstallText.Get("status.cancelled") : InstallText.Get("status.failed");
                    detail.Text = UIRichText.Literal(e.Message);
                    primary.Label = InstallText.Get("actions.retry");
                    primary.Enabled = true;
                    cancel.Enabled = true;
                    cancel.Label = InstallText.Get("actions.close");
                });
            }
        });
    }

    private void ShowInstallChoices()
    {
        if (busy || completed || folderPick is not null || options.Update || options.Uninstall) return;
        desktopHex.State = choices.DesktopHex;
        desktopDusk.State = choices.DesktopDusk;
        applicationMenu.State = choices.ApplicationMenu;
        menuDusk.State = choices.MenuDusk;
        windowsAppPaths.State = choices.WindowsAppPaths;
        optionsWindow.Visible = true;
        optionsWindow.OnClosed = () => primary.Enabled = !busy;
        ui.Focus = desktopHex;
    }

    private void RequestClose()
    {
        if (!busy) { Engine.WindowManager.Close(); return; }
        cancellation.Cancel();
        status.Text = InstallText.Get("status.finishing");
        cancel.Enabled = false;
    }
}
