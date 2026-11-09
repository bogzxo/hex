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
        StartWork("hex-update-check", InstallText.Get("updates.checking"), async context =>
        {
            release = await new ReleaseClient().Latest(InstallPaths.Rid, context.Token);
        }, result =>
        {
            checkingHexUpdate = false;
            if (result.State != WorkState.Succeeded || release is null)
            {
                Note(InstallText.Get("updates.check_failed", ("reason", result.Error ?? InstallText.Get("updates.cancelled"))), error: true);
                return;
            }
            if (ReleaseClient.VersionOf(release.Version) <= ReleaseClient.VersionOf(HexAbout.Version))
            {
                Note(InstallText.Get("updates.current"));
                return;
            }
            UIDialog.Show(chrome, InstallText.Get("updates.title"), InstallText.Get("updates.available", ("version", UIRichText.Literal(release.Shown))),
                new DialogChoice(InstallText.Get("actions.install_update"), () => PrepareHexUpdate(release)),
                new DialogChoice(InstallText.Get("actions.later")));
        }, seen: false, cancellable: true);
    }

    private void PrepareHexUpdate(ReleaseAsset release)
    {
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
}
