using Hex.Installation;
using Horizon.Core.Threading;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool checkingHexUpdate;

    private static void ReadInstallerStrings() => InstallText.Use(Horizon.Content.Localisation.ReadFile(
        Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets/installer/localisation/en-gb.hor")));

    private void CheckForHexUpdates()
    {
        if (checkingHexUpdate || options.SelfTest) return;

        // Hex itself is a download that would have to be run, an APK is updated by installing another one
        if (Hex.Core.HexHost.NoDownloads("A new Hex") is { } why)
        {
            Note(why + " Install the new APK instead.", error: true);
            return;
        }
        ReadInstallerStrings();
        checkingHexUpdate = true;
        ReleaseAsset? release = null;
        // A nightly build updates from the newest nightly, the stable one from the newest release
        InstallChannel channel = HexChannel;
        StartWork("hex-update-check", InstallText.Get("updates.checking"), async context =>
        {
            release = await new ReleaseClient().Latest(InstallPaths.Rid, channel, context.Token);
        }, result =>
        {
            checkingHexUpdate = false;
            if (result.State != WorkState.Succeeded || release is null)
            {
                Note(InstallText.Get("updates.check_failed", ("reason", result.Error ?? InstallText.Get("updates.cancelled"))), error: true);
                return;
            }
            string installed = channel == InstallChannel.Dev ? Hex.Shell.HexVersion.Nightly : HexAbout.Version;
            if (!InstallChannels.IsNewer(channel, release.Version, installed))
            {
                Note(InstallText.Get("updates.current"));
                return;
            }
            UIDialog.Show(chrome, InstallText.Get("updates.title"), InstallText.Get("updates.available", ("version", UIRichText.Literal(release.Shown))),
                new DialogChoice(InstallText.Get("actions.install_update"), () => PrepareHexUpdate(release)),
                new DialogChoice(InstallText.Get("actions.later")));
        }, seen: false, cancellable: true);
    }

    // Which Hex this is, the nightly build of the development branch or a stable release
    private static InstallChannel HexChannel => Hex.Shell.HexVersion.Dev ? InstallChannel.Dev : InstallChannel.Stable;

    /// <summary>
    /// Hex Setup opened, the copy that came with this Hex, run from a folder of its own so it can replace this one. Said
    /// in a note when there's none, a Hex built from its source has no setup beside it.
    /// </summary>
    private void OpenHexSetup()
    {
        ReadInstallerStrings();
        string root = Path.TrimEndingDirectorySeparator(Hex.Core.HexHost.ProgramFolder);
        string bundled = Path.Combine(root, "installer");
        if (!File.Exists(Path.Combine(bundled, InstallPaths.InstallerExecutable)))
        {
            Note(InstallText.Get("errors.no_installer"), error: true);
            return;
        }

        try
        {
            string copy = InstallerHandoff.CopyInstaller(bundled);
            // This installation said when Setup manages it, so it opens on this Hex and its channel
            string[] args = File.Exists(Path.Combine(root, InstallPaths.Manifest))
                ? ["--target", root, "--channel", HexChannel.Word()]
                : ["--channel", HexChannel.Word()];
            InstallerHandoff.StartInstallerCopy(copy, args);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Note(InstallText.Get("updates.setup_failed", ("reason", e.Message)), error: true);
        }
    }

    private void PrepareHexUpdate(ReleaseAsset release)
    {
        if (OperatingSystem.IsMacOS())
        {
            PrepareMacUpdate(release);
            return;
        }
        string root = Path.TrimEndingDirectorySeparator(Hex.Core.HexHost.ProgramFolder);
        PreparedInstallerUpdate? installer = null;
        StartWork("hex-update-prepare", InstallText.Get("updates.preparing"), async context =>
        {
            // Portable copies remain portable; never claim an arbitrary directory as installer-owned.
            _ = InstallTransaction.ReadManifest(root);
            installer = await PreparedInstallerUpdate.Prepare(release, update => context.Report(update.Fraction, update.Message), context.Token);
        }, result =>
        {
            if (result.State != WorkState.Succeeded || installer is null)
            {
                installer?.Dispose();
                Note(InstallText.Get("updates.prepare_failed", ("reason", result.Error ?? "")), error: true);
                return;
            }
            RestartInto(installer.Executable, installer.Arguments(root), installer.Dispose, installer.HandedOff);
        }, seen: false, cancellable: true);
    }

    /// <summary>
    /// A Hex in Hex.app updates itself with the release's app zip, the old app put back if the new one can't be moved in.
    /// Any other Hex on a Mac (built from source, a folder) has no installer to hand over to, it's sent to the release page.
    /// </summary>
    private void PrepareMacUpdate(ReleaseAsset release)
    {
        string? app = Hex.Core.HexBundle.Root;
        if (app is null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", ["https://github.com/bogzxo/hex/releases" + (release.Channel == InstallChannel.Dev ? "" : "/latest")]) { UseShellExecute = false }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            Note("This Hex is not the app, download the new Hex from the releases page that opened.");
            return;
        }

        BundleUpdate? update = null;
        StartWork("hex-update-prepare", InstallText.Get("updates.preparing"), async context =>
        {
            update = await BundleUpdate.Prepare(release, app, u => context.Report(u.Fraction, u.Message), context.Token);
        }, result =>
        {
            if (result.State != WorkState.Succeeded || update is null)
            {
                update?.Dispose();
                Note(InstallText.Get("updates.prepare_failed", ("reason", result.Error ?? "")), error: true);
                return;
            }
            RestartInto("/bin/sh", update.Script(Environment.ProcessId), update.Dispose, update.HandedOff);
        }, seen: false, cancellable: true);
    }
}
