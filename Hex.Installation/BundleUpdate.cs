using System.Diagnostics;

namespace Hex.Installation;

/// <summary>
/// An update of a Hex that runs from a macOS application bundle (Hex.app): the release's app zip, Hex-version-macos-arch.zip,
/// is downloaded and checked against the release's digest, its Hex.app extracted next to the one that is running, and
/// when Hex closes a small shell script moves the old app aside, the new one in its place and opens it, putting the old
/// one back if that fails. Nothing is signed again here, the app arrives with the signature it was made with.
/// </summary>
public sealed class BundleUpdate : IDisposable
{
    /// <summary>The folder next to the app the new one was extracted into, its Hex.app inside.</summary>
    public string Stage { get; }
    public string App { get; }
    private bool handedOff;

    private BundleUpdate(string stage, string app) => (Stage, App) = (stage, app);

    /// <summary>Whether a release asset is the app zip of an architecture (osx-arm64, osx-x64).</summary>
    public static bool IsAppZip(string name, string rid) =>
        name.StartsWith("Hex-", StringComparison.Ordinal) && name.EndsWith("-macos-" + rid.Replace("osx-", "") + ".zip", StringComparison.Ordinal);

    /// <summary>Download and extract the release for the app at <paramref name="app"/>, ready for <see cref="Script"/>.</summary>
    public static async Task<BundleUpdate> Prepare(ReleaseAsset release, string app, Action<InstallProgress>? progress,
        CancellationToken cancel, ReleaseClient? client = null)
    {
        app = Path.TrimEndingDirectorySeparator(app);
        string parent = Path.GetDirectoryName(app) ?? throw new IOException("Hex.app has no folder.");
        // Next to the app so the move in is a rename, and so a folder the user can't write to is found out now
        string stage = Path.Combine(parent, ".Hex-update-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Path.GetTempPath(), "hex-update-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            try { Directory.CreateDirectory(stage); }
            catch (UnauthorizedAccessException) { throw new IOException("Hex can't write to " + parent + ", download the new Hex and drag it to Applications instead."); }
            await (client ?? new ReleaseClient()).Download(release, zip, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(new(InstallText.Get("status.verifying"), .75f));
            Run("/usr/bin/ditto", ["-x", "-k", zip, stage]);
            string extracted = Path.Combine(stage, "Hex.app");
            if (!File.Exists(Path.Combine(extracted, "Contents", "MacOS", "Hex")) || !File.Exists(Path.Combine(extracted, "Contents", "Info.plist")))
                throw new InvalidDataException("The download has no Hex.app in it.");
            // A download made here has none, but one that was opened in a browser first has
            Run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", extracted], mustSucceed: false);
            Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", extracted]);
            return new(stage, app);
        }
        catch
        {
            Discard(stage);
            throw;
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    /// <summary>The arguments for /bin/sh that wait for this process to end, swap the app and open it.</summary>
    public IReadOnlyList<string> Script(int pid) => ["-c", SwapScript, "sh", pid.ToString(), App, Path.Combine(Stage, "Hex.app"), Stage];

    // The old app is only removed once the new one is in. When either move fails the old one goes back, and is opened.
    internal const string SwapScript = """
        pid="$1"; app="$2"; new="$3"; stage="$4"; old="$app.hex-old"
        n=0
        while kill -0 "$pid" 2>/dev/null && [ "$n" -lt 600 ]; do sleep 0.2; n=$((n + 1)); done
        rm -rf "$old"
        if mv "$app" "$old"; then
          if mv "$new" "$app"; then rm -rf "$old"; else mv "$old" "$app"; fi
        fi
        rm -rf "$stage"
        open "$app"
        """;

    public void HandedOff() => handedOff = true;

    public void Dispose()
    {
        if (!handedOff) Discard(Stage);
    }

    private static void Discard(string stage)
    {
        try { if (Directory.Exists(stage) && Path.GetFileName(stage).StartsWith(".Hex-update-", StringComparison.Ordinal)) Directory.Delete(stage, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* left behind, the next update clears it */ }
    }

    private static void Run(string program, string[] arguments, bool mustSucceed = true)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(program + " could not be started.");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && mustSucceed)
            throw new InvalidDataException(Path.GetFileName(program) + " failed: " + error.Trim());
    }
}
