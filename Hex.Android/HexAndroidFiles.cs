using Android.Content;
using Android.Content.PM;

using Hex.Core;

using Horizon.Content;
using Horizon.Logging;
using Horizon.Platform.Android;

namespace Hex.Android;

/// <summary>
/// Where Hex's files are on a tablet. Dawn reads its own files (layouts, images, localisation, the built in extensions,
/// the language servers' catalog) off disk by path the way it does next to Hex.exe, a lot of it straight through File and
/// Directory rather than the engine's content, and a few things want a real file (Skia's fonts). So the APK's Assets and
/// shaders are unpacked once per install into the app's own folder, and that folder is Hex's program folder, the engine's
/// content and the working directory. An APK installed over it unpacks again, nothing a person made is ever in there.
/// <para>
/// The rest is the app's own storage, no permission asked. Settings, keys and sessions in files/config/Hex (HEX_HOME),
/// backups and history under the engine's state folder, the workspace new files go in at files/hex/workspace, which is
/// where the explorer starts. Picked documents come in through HexDocuments.
/// </para>
/// </summary>
public sealed class HexAndroidFiles
{
    // The file in the program folder that says which install it was unpacked from
    private const string STAMP = "hex-android.stamp";

    /// <summary>What was set up, null before <see cref="Prepare"/>.</summary>
    public static HexAndroidFiles? Current { get; private set; }

    /// <summary>The unpacked Assets and shaders, Hex's program folder.</summary>
    public string Program { get; }

    /// <summary>Hex's home, settings.hor, keys.hor and the sessions (HEX_HOME).</summary>
    public string Home { get; }

    /// <summary>Where layouts and new files go when nobody said, the explorer's first folder.</summary>
    public string Workspace { get; }

    /// <summary>Where the self test makes and saves its files, emptied before every run.</summary>
    public string SelfTest { get; }

    /// <summary>Copies of documents picked with the system's picker, see HexDocuments.</summary>
    public string Picked { get; }

    private readonly AndroidPaths paths;

    private HexAndroidFiles(AndroidPaths paths, AndroidStorageRoots storage)
    {
        this.paths = paths;
        string root = Path.Combine(paths.Files, "hex");
        Program = Path.Combine(root, "program");

        // Under the engine's own config folder, the recent lists and the rest of what's kept go through its storage,
        // which only writes inside its roots
        Home = Path.Combine(storage.Config, "Hex");
        Workspace = Path.Combine(root, "workspace");
        SelfTest = Path.Combine(paths.Cache, "hex-selftest");
        Picked = Path.Combine(root, "picked");
    }

    /// <summary>
    /// Sets everything up before the engine is made, once a process. Unpacks the APK's content when this install hasn't
    /// yet, then points Hex, the engine's content and the working directory at it.
    /// </summary>
    public static HexAndroidFiles Prepare(Context context)
    {
        if (Current is { } done) return done;

        AndroidRuntime runtime = AndroidBootstrap.Current ?? throw new InvalidOperationException("The platform wasn't set up before Hex.");
        var files = new HexAndroidFiles(runtime.Paths, runtime.Storage.Roots);
        foreach (string folder in new[] { files.Program, files.Home, files.Workspace, files.Picked })
            Directory.CreateDirectory(folder);

        files.Unpack(runtime.Content, Installed(context));

        // Hex's own files are the unpacked ones from here on, for Dawn, the engine (fonts, skins, the shader pack) and
        // every relative path Dawn reads
        HexHost.ProgramFolder = files.Program + Path.DirectorySeparatorChar;
        ContentFiles.Source = new DiskContent(files.Program);
        Directory.SetCurrentDirectory(files.Program);

        // What a tablet has and hasn't. Its own shell and toybox can be started, nothing downloaded can, no pipes to
        // another Hex, no desktop dialogs (the drawn file browser stands in, and the system's picker for documents
        // outside the app)
        HexHost.Name = "Android";
        HexHost.Processes = true;
        HexHost.RunsDownloads = false;
        HexHost.Pipes = false;
        HexHost.NativeDialogs = false;

        // A terminal's shell is the system's, there's no /bin/sh on Android
        if (System.Environment.GetEnvironmentVariable("SHELL") is null)
            System.Environment.SetEnvironmentVariable("SHELL", "/system/bin/sh");

        // Settings and the terminal's files in a home of the app's, never a guess at the user's
        System.Environment.SetEnvironmentVariable("HEX_HOME", files.Home);

        // Hex never makes a sound, and AAudio's callback isn't to be trusted yet, so no stream at all
        if (System.Environment.GetEnvironmentVariable("HORIZON_AUDIO") is null)
            System.Environment.SetEnvironmentVariable("HORIZON_AUDIO", "off");

        Log.Info($"[Hex] Program {files.Program}, home {files.Home}, workspace {files.Workspace}.");
        Current = files;
        return files;
    }

    /// <summary>A path a run was handed (an intent's HORIZON_HEX_OPEN), made full inside the app's folders, null otherwise.</summary>
    public string? Inside(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, Workspace);
        return paths.Owns(full) ? full : null;
    }

    // Which install this is, the time the APK was put on the device, so an install over it unpacks again
    private static string Installed(Context context)
    {
        try
        {
            PackageInfo? info = context.PackageManager?.GetPackageInfo(context.PackageName ?? string.Empty, 0);
            long version = OperatingSystem.IsAndroidVersionAtLeast(28) ? info?.LongVersionCode ?? 0 : (long)(info?.VersionCode ?? 0);
            return $"{version}-{info?.LastUpdateTime}";
        }
        catch (Exception e)
        {
            Log.Warning($"[Hex] Couldn't tell which install this is, unpacking again. {e.Message}");
            return Guid.NewGuid().ToString("N");
        }
    }

    // Copies every file under Assets and shaders out of the APK, unless the stamp says this install did already. Files
    // that are in the folder and not in the APK (from an older install) stay, nothing reads them by listing but the
    // extensions, which a newer APK has the same folders of
    private void Unpack(IContentFiles content, string install)
    {
        string stamp = Path.Combine(Program, STAMP);
        if (File.Exists(stamp) && File.ReadAllText(stamp) == install)
        {
            Log.Info($"[Hex] Program files are this install's ({install}), nothing unpacked.");
            return;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int count = 0;
        long bytes = 0;
        foreach (string root in AndroidContent.DefaultRoots)
        {
            if (!content.DirectoryExists(root)) continue;
            foreach (string file in content.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = file.Replace('\\', '/');
                string target = Path.Combine(Program, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (Stream from = content.OpenRead(relative))
                using (FileStream to = File.Create(target))
                    from.CopyTo(to);
                bytes += new FileInfo(target).Length;
                count++;
            }
        }

        File.WriteAllText(stamp, install);
        Log.Info($"[Hex] Unpacked {count} program files ({bytes / 1024} KB) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms for install {install}.");
    }
}
