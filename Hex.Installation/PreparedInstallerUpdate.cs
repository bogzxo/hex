namespace Hex.Installation;

/// <summary>A verified release's own installer, relocated outside the installation, with its cached package.</summary>
public sealed class PreparedInstallerUpdate : IDisposable
{
    public string Executable { get; }
    public string Package { get; }
    public string Version { get; }
    private bool handedOff;

    private PreparedInstallerUpdate(string executable, string package, string version) =>
        (Executable, Package, Version) = (executable, package, version);

    public IReadOnlyList<string> Arguments(string root) =>
        [.. InstallerHandoff.UpdateArguments(root, Version), "--package", Package];

    /// <summary>Download once, validate the entire package, then copy its new installer and all runtime dependencies.</summary>
    public static async Task<PreparedInstallerUpdate> Prepare(ReleaseAsset release, Action<InstallProgress>? progress,
        CancellationToken cancel, ReleaseClient? client = null)
    {
        string work = Path.Combine(Path.GetTempPath(), "hex-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "release.zip"), stage = Path.Combine(work, "stage");
        string? executable = null;
        try
        {
            await (client ?? new ReleaseClient()).Download(release, zip, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(new(InstallText.Get("status.verifying"), .75f));
            // The normal package validator rejects unsafe archives before any executable can be started.
            InstallTransaction.Prepare(zip, stage, Path.Combine(work, "empty"), release.Version, cancel);
            cancel.ThrowIfCancellationRequested();
            executable = InstallerHandoff.CopyInstaller(Path.Combine(stage, "installer"));
            string package = Path.Combine(Path.GetDirectoryName(executable)!, "hex-update-package.zip");
            File.Move(zip, package);
            cancel.ThrowIfCancellationRequested();
            return new(executable, package, release.Version);
        }
        catch
        {
            if (executable is not null) Discard(executable);
            throw;
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public void HandedOff() => handedOff = true;

    public void Dispose()
    {
        if (!handedOff) Discard(Executable);
    }

    private static void Discard(string executable)
    {
        string folder = Path.GetDirectoryName(executable)!;
        try
        {
            if (Directory.Exists(folder) && InstallerHandoff.IsInstallerCopy(folder)) Directory.Delete(folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* A retained temporary copy must not block cancellation. */ }
    }
}
